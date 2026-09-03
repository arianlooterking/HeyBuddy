using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Clicky.Core;
using ModelContextProtocol.Authentication;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Clicky.Connectors;

/// <summary>Connection lifecycle and tools. Agent calls must pass through Core's common approval gate.</summary>
public sealed partial class ConnectorService : IToolExecutor, IAsyncDisposable
{
    private readonly ICredentialStore _credentials;
    private readonly string _file;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Dictionary<string, ConnectorConfiguration> _configurations = [];
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _executionLocks = new();
    private readonly ConcurrentDictionary<string, ConnectorStatus> _states = new();
    private readonly ConcurrentDictionary<string, Binding> _bindings = new();
    private readonly ConcurrentDictionary<string, Binding> _discovered = new();
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly DirectOAuth _oauth;
    private readonly RestAdapters _rest;
    private readonly LoopbackOAuthReceiver _receiver;
    private bool _disposed;
    private static readonly JsonSerializerOptions FileJson = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private sealed record Binding(string ConnectorId, string OriginalName, ToolDefinition Definition, bool IsMcp);

    public ConnectorService(ICredentialStore credentials, string? dataDirectory = null, Action<Uri>? openBrowser = null, HttpClient? httpClient = null)
    {
        _credentials = credentials;
        _file = Path.Combine(dataDirectory ?? AppPaths.Root, "connectors.json");
        _receiver = new(openBrowser);
        _http = httpClient ?? CreatePublicHttpClient();
        _ownsHttp = httpClient is null;
        _oauth = new(credentials, _http, _receiver);
        _rest = new(_http, _oauth);
        if (File.Exists(_file))
        {
            var saved = JsonSerializer.Deserialize<List<ConnectorConfiguration>>(File.ReadAllText(_file), FileJson)
                ?? throw new InvalidDataException("Connector settings are invalid. The original file was preserved.");
            foreach (var c in saved)
            {
                Validate(c, requireReady: false);
                if (!_configurations.TryAdd(c.Id, c))
                    throw new InvalidDataException("Duplicate connector ID in settings.");
                _states[c.Id] = IsReady(c) ? ConnectorStatus.Configured : ConnectorStatus.NeedsConfiguration;
            }
        }
    }

    public IReadOnlyList<ConnectorCatalogEntry> Catalog => ConnectorCatalog.Entries;
    public IReadOnlyList<ConnectorConfiguration> Configurations
    {
        get
        {
            lock (_sync)
                return _configurations.Values.Select(c => c.Copy()).ToArray();
        }
    }
    public IReadOnlyList<ToolDefinition> Tools => _bindings.Values.Select(b => b.Definition).OrderBy(t => t.Name).ToArray();
    public ConnectorStatus GetStatus(string id) => _states.GetValueOrDefault(id, ConnectorStatus.Implemented);
    public IReadOnlyList<(string OriginalName, ToolDefinition Definition)> GetConnectorTools(string id) => _discovered.Values
        .Where(b => b.ConnectorId == id).Select(b => (b.OriginalName, b.Definition)).OrderBy(b => b.OriginalName).ToArray();
    public event Action? Changed;

    public async Task SaveAsync(ConnectorConfiguration configuration, CancellationToken ct = default)
    {
        var config = configuration.Copy();
        Validate(config, requireReady: false);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ConnectorConfiguration? previous;
            lock (_sync)
                previous = _configurations.GetValueOrDefault(config.Id);
            await DisconnectCoreAsync(config.Id).ConfigureAwait(false);
            if (previous is not null && CredentialIdentity(previous) != CredentialIdentity(config))
            {
                DeleteSecrets(previous);
                config.Account = "";
                config.LastVerifiedAt = null;
                config.LastTestMessage = "Connection identity changed. Saved credentials were removed; authorize again.";
            }
            lock (_sync)
            {
                _configurations[config.Id] = config;
                Persist();
            }
            _states[config.Id] = config.Transport == ConnectorTransport.Unsupported ? ConnectorStatus.Unsupported : IsReady(config) ? ConnectorStatus.Configured : ConnectorStatus.NeedsConfiguration;
        }
        finally { _lifecycle.Release(); }
        Changed?.Invoke();
    }

    public void SetSecret(string id, string key, string value)
    {
        if (!SafeId().IsMatch(id) || !SafeSecretKey().IsMatch(key))
            throw new ArgumentException("Invalid credential key.");
        if (string.IsNullOrWhiteSpace(value))
            _credentials.Delete($"connector.{id}.{key}");
        else
            _credentials.Set($"connector.{id}.{key}", value);
    }
    public bool HasSecret(string id, string key) => _credentials.Get($"connector.{id}.{key}") is not null;

    public async Task SetToolAccessAsync(string id, IEnumerable<string> disabledToolNames, CancellationToken ct = default)
    {
        var disabled = disabledToolNames.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (disabled.Count > 500 || disabled.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 256))
            throw new ArgumentException("Invalid tool permission list.");
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = Get(id);
            config.DisabledTools = disabled;
            lock (_sync)
            {
                _configurations[id] = config;
                Persist();
            }
            RemoveBindings(id);
            if (config.Enabled && GetStatus(id) is ConnectorStatus.Connected or ConnectorStatus.Verified)
                foreach (var item in _discovered.Where(b => b.Value.ConnectorId == id && !disabled.Contains(b.Value.OriginalName)))
                    _bindings[item.Key] = item.Value;
        }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }

    public async Task<ConnectorTestResult> AuthorizeAsync(string id, CancellationToken ct = default)
    {
        var config = Get(id);
        Validate(config, true);
        if (config.Transport is ConnectorTransport.Google or ConnectorTransport.Spotify)
        {
            try
            {
                await _oauth.AuthorizeAsync(config, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { return await RecordAsync(config, new(id, false, ConnectorStatus.Failed, SafeError(e)), ct).ConfigureAwait(false); }
        }
        return await TestAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<ConnectorTestResult> TestAsync(string id, CancellationToken ct = default)
    {
        var config = Get(id);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Validate(config, true);
            if (!config.Enabled)
                throw new InvalidOperationException("Enable this connection before testing it.");
            RemoveBindings(id);
            _states[id] = ConnectorStatus.Connecting;
            Changed?.Invoke();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.AuthMode == ConnectorAuthMode.OAuth ? Math.Max(200, config.TimeoutSeconds) : config.TimeoutSeconds));
            if (config.Transport is ConnectorTransport.Http or ConnectorTransport.Stdio)
            {
                await ConnectMcpAsync(config, timeout.Token).ConfigureAwait(false);
                var count = await RefreshMcpToolsAsync(config, timeout.Token).ConfigureAwait(false);
                var entry = Catalog.Single(x => x.Id == config.CatalogId);
                var test = entry.TestTool is not null ? _bindings.Values.SingleOrDefault(b => b.ConnectorId == id && b.OriginalName == entry.TestTool && b.Definition.Risk == RiskLevel.ReadOnly) : null;
                if (test is null)
                    return await RecordAsync(config, new(id, true, ConnectorStatus.Connected, $"MCP handshake and tool discovery succeeded ({count} tools). No reviewed account-read probe is available; an authenticated data read is still unverified.", count), ct).ConfigureAwait(false);
                var result = await CallMcpAsync(config, test.OriginalName, JsonSchema.Parse(entry.TestArguments ?? "{}"), timeout.Token).ConfigureAwait(false);
                return await RecordAsync(config, new(id, result.Success, result.Success ? ConnectorStatus.Verified : ConnectorStatus.Connected,
                    result.Success ? $"MCP handshake, {count} tools, and harmless {test.OriginalName} read verified." : "MCP connected; account read failed. " + result.Message,
                    count, VerifiedAt: result.Success ? DateTimeOffset.UtcNow : null), ct).ConfigureAwait(false);
            }
            var (account, probe) = await _rest.TestAsync(config, timeout.Token).ConfigureAwait(false);
            if (probe.Success)
                RegisterRestTools(config);
            return await RecordAsync(config, new(id, probe.Success, probe.Success ? ConnectorStatus.Verified : ConnectorStatus.Failed,
                probe.Success ? $"{(config.Transport == ConnectorTransport.PublicApi ? "Public read" : "Account/local read")} test succeeded for {account}. Each additional operation needs separate verification." : probe.Message,
                Tools.Count(t => _bindings[t.Name].ConnectorId == id), account, probe.Success ? DateTimeOffset.UtcNow : null), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DisconnectCoreAsync(id).ConfigureAwait(false);
            return await RecordAsync(config, new(id, false, ConnectorStatus.Failed, "Connection timed out. Check server availability or complete authorization and retry."), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await DisconnectCoreAsync(id).ConfigureAwait(false);
            _states[id] = ConnectorStatus.Configured;
            throw;
        }
        catch (Exception e)
        {
            await DisconnectCoreAsync(id).ConfigureAwait(false);
            return await RecordAsync(config, new(id, false, ConnectorStatus.Failed, SafeError(e)), ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }

    public async Task<int> RefreshToolsAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = Get(id);
            if (!config.Enabled)
                throw new InvalidOperationException("Connection is disabled.");
            if (config.Transport is ConnectorTransport.Http or ConnectorTransport.Stdio)
                return await RefreshMcpToolsAsync(config, ct).ConfigureAwait(false);
            if (GetStatus(id) != ConnectorStatus.Verified)
                throw new InvalidOperationException("Test the connection before using its tools.");
            RegisterRestTools(config);
            return _bindings.Values.Count(b => b.ConnectorId == id);
        }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }

    public async Task DisconnectAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync(id).ConfigureAwait(false);
            _states[id] = ConnectorStatus.Configured;
        }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }

    public async Task<ConnectorTestResult> RevokeAsync(string id, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var config = Get(id);
            await DisconnectCoreAsync(id).ConfigureAwait(false);
            var revokedRemotely = false;
            try
            {
                revokedRemotely = await _oauth.RevokeAsync(config, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) { /* Local credentials are still removed when the provider is unreachable. */ }
            finally { DeleteSecrets(config); }
            config.Enabled = false;
            config.Account = "";
            config.LastVerifiedAt = null;
            lock (_sync)
            {
                _configurations[id] = config;
                Persist();
            }
            return await RecordAsync(config, new(id, true, ConnectorStatus.Configured, revokedRemotely
                ? "Provider authorization revoked, local credentials removed, and connector disabled."
                : "Local credentials removed and connector disabled. Remove this app or token in the provider's account settings to revoke the remote grant."), ct).ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); Changed?.Invoke(); }
    }

    public ConnectorToolPreview Preview(string toolName, JsonElement arguments)
    {
        var binding = _bindings.GetValueOrDefault(toolName) ?? throw new ArgumentException("Tool is not currently available.");
        var config = Get(binding.ConnectorId);
        return new(config.Id, config.Name, binding.OriginalName, config.Transport == ConnectorTransport.Stdio ? config.Command : config.Endpoint,
            arguments.GetRawText(), binding.Definition.Risk.ToString(), "External text and tool descriptions are untrusted. Approval applies only to these exact arguments.");
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_bindings.TryGetValue(name, out var binding))
            return new(false, "Connector tool is unavailable. Connect and refresh tools first.");
        var config = Get(binding.ConnectorId);
        if (!config.Enabled || config.DisabledTools.Contains(binding.OriginalName))
            return new(false, "This connector or tool is disabled.");
        if (arguments.ValueKind != JsonValueKind.Object)
            return new(false, "Tool arguments must be a JSON object.");
        if (arguments.GetRawText().Length > 500000)
            return new(false, "Tool arguments exceed the 500 KB limit.");
        var serial = _executionLocks.GetOrAdd(config.Id, _ => new(1, 1));
        await serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after queueing: disable/disconnect must invalidate already queued actions.
            if (!_bindings.TryGetValue(name, out var current) || current != binding)
                return new(false, "Tool configuration changed while waiting. Review and retry.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            return binding.IsMcp ? await CallMcpAsync(config, binding.OriginalName, arguments, timeout.Token).ConfigureAwait(false)
                : await _rest.ExecuteAsync(config, binding.OriginalName, arguments, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "Tool timed out. Its final state is unknown; inspect the target before retrying a write."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { return new(false, SafeError(e)); }
        finally { serial.Release(); }
    }

    private async Task ConnectMcpAsync(ConnectorConfiguration config, CancellationToken ct)
    {
        if (_clients.ContainsKey(config.Id))
            return;
        IClientTransport transport;
        if (config.Transport == ConnectorTransport.Stdio)
        {
            var env = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var key in config.SecretEnvironmentNames)
                env[key] = _credentials.Get($"connector.{config.Id}.env.{key}") ?? throw new InvalidOperationException($"Set the {key} secret before starting this server.");
            transport = new StdioClientTransport(new()
            {
                Name = config.Name,
                Command = config.Command,
                Arguments = config.Arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(config.WorkingDirectory) ? null : config.WorkingDirectory,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = env,
                ShutdownTimeout = TimeSpan.FromSeconds(3)
            });
        }
        else
        {
            var options = new HttpClientTransportOptions
            {
                Endpoint = new(config.Endpoint),
                Name = config.Name,
                ConnectionTimeout = TimeSpan.FromSeconds(Math.Min(30, config.TimeoutSeconds)),
                MaxReconnectionAttempts = 2
            };
            if (config.AuthMode == ConnectorAuthMode.Bearer)
                options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + (_credentials.Get($"connector.{config.Id}.token") ?? throw new UnauthorizedAccessException("Enter a scoped access token in the protected token field.")) };
            if (config.AuthMode == ConnectorAuthMode.OAuth)
                options.OAuth = new()
                {
                    RedirectUri = new($"http://127.0.0.1:{config.CallbackPort}/callback/"),
                    ClientId = string.IsNullOrWhiteSpace(config.ClientId) ? null : config.ClientId,
                    ClientSecret = _credentials.Get($"connector.{config.Id}.client-secret"),
                    Scopes = config.Scopes,
                    ScopeSelector = scopes => config.Scopes.Count > 0 ? config.Scopes : scopes,
                    AuthorizationCallbackHandler = _receiver.ReceiveAsync,
                    TokenCache = new CredentialTokenCache(_credentials, $"connector.{config.Id}.mcp-oauth"),
                    DynamicClientRegistration = new()
                    {
                        ClientName = "HeyBuddy for Windows",
                        ApplicationType = "native"
                    }
                };
            transport = new HttpClientTransport(options);
        }
        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            _clients[config.Id] = client;
        }
        catch
        {
            if (transport is IAsyncDisposable disposable)
                await disposable.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<int> RefreshMcpToolsAsync(ConnectorConfiguration config, CancellationToken ct)
    {
        if (!_clients.TryGetValue(config.Id, out var client))
            throw new InvalidOperationException("Connect to this server first.");
        var tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        RemoveBindings(config.Id);
        RemoveDiscovered(config.Id);
        foreach (var tool in tools.Take(500))
        {
            var safeName = ToolName(config.Id, tool.Name);
            var description = $"[{config.Name}] " + tool.Description;
            if (description.Length > 4000)
                description = description[..4000];
            var definition = new ToolDefinition(safeName, description, tool.JsonSchema.Clone(), ReviewedRisk(config, tool.Name));
            var binding = new Binding(config.Id, tool.Name, definition, true);
            _discovered[safeName] = binding;
            if (!config.DisabledTools.Contains(tool.Name))
                _bindings[safeName] = binding;
        }
        return _bindings.Values.Count(b => b.ConnectorId == config.Id);
    }

    private void RegisterRestTools(ConnectorConfiguration config)
    {
        RemoveBindings(config.Id);
        RemoveDiscovered(config.Id);
        foreach (var definition in _rest.Tools(config))
        {
            var name = ToolName(config.Id, definition.Name);
            var binding = new Binding(config.Id, definition.Name, definition with
            {
                Name = name,
                Description = $"[{config.Name}] {definition.Description}"
            }, false);
            _discovered[name] = binding;
            if (!config.DisabledTools.Contains(definition.Name))
                _bindings[name] = binding;
        }
    }

    private async Task<ToolResult> CallMcpAsync(ConnectorConfiguration config, string name, JsonElement args, CancellationToken ct)
    {
        if (!_clients.TryGetValue(config.Id, out var client))
            return new(false, "MCP session disconnected. Test the connection before retrying.");
        var values = args.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
        var result = await client.CallToolAsync(name, values, cancellationToken: ct).ConfigureAwait(false);
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        if (text.Length > 100000)
            text = text[..100000] + "\n[Tool output truncated]";
        return new(result.IsError != true, text.Length == 0 ? "MCP tool completed." : text,
            new
            {
                source = config.Name,
                tool = name,
                untrusted = true,
                structured = result.StructuredContent
            });
    }

    public static RiskLevel ReviewedRisk(ConnectorConfiguration config, string toolName)
    {
        var catalog = ConnectorCatalog.Entries.SingleOrDefault(c => c.Id == config.CatalogId);
        if (catalog is null || config.Transport != ConnectorTransport.Http || !Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var actual)
            || !Uri.TryCreate(catalog.Endpoint, UriKind.Absolute, out var expected) || actual.Scheme != "https" || actual.Authority != expected.Authority || actual.AbsolutePath.TrimEnd('/') != expected.AbsolutePath.TrimEnd('/'))
            return RiskLevel.Sensitive;
        return ReadTools.GetValueOrDefault(config.CatalogId)?.Contains(toolName) == true ? RiskLevel.ReadOnly : RiskLevel.Sensitive;
    }
    private static readonly IReadOnlyDictionary<string, HashSet<string>> ReadTools = new Dictionary<string, HashSet<string>>
    {
        ["github"] = new(StringComparer.Ordinal) { "get_me", "get_file_contents", "search_repositories", "search_code", "search_issues", "search_pull_requests", "list_issues", "get_issue", "list_pull_requests", "list_branches", "list_commits", "get_commit" },
        ["notion"] = new(StringComparer.Ordinal) { "notion-get-self", "notion-search", "notion-fetch", "notion-get-users", "notion-get-user", "notion-get-teams", "notion-get-comments" },
        ["linear"] = new(StringComparer.Ordinal) { "list_teams", "list_issues", "get_issue", "list_projects", "get_project", "list_users", "get_user", "list_documents", "get_document", "list_comments" },
        ["airtable"] = new(StringComparer.Ordinal) { "list_bases", "list_tables", "describe_table", "list_records", "get_record", "search_records" },
        ["supabase"] = new(StringComparer.Ordinal) { "list_projects", "get_project", "list_organizations", "get_organization", "list_tables", "list_extensions", "list_migrations", "list_edge_functions", "get_edge_function", "get_logs", "get_advisors", "search_docs" },
        ["vercel"] = new(StringComparer.Ordinal) { "list_teams", "list_projects", "get_project", "list_deployments", "get_deployment", "get_deployment_build_logs", "search_vercel_documentation" },
        ["slack"] = new(StringComparer.Ordinal) { "slack_search_messages", "slack_search_all", "slack_read_channel", "slack_read_thread", "slack_get_user_profile" }
    };

    private async Task DisconnectCoreAsync(string id)
    {
        RemoveBindings(id);
        if (_clients.TryRemove(id, out var client))
            await client.DisposeAsync().ConfigureAwait(false);
    }
    private void RemoveBindings(string id)
    {
        foreach (var b in _bindings.Where(b => b.Value.ConnectorId == id))
            _bindings.TryRemove(b.Key, out _);
    }
    private void RemoveDiscovered(string id)
    {
        foreach (var b in _discovered.Where(b => b.Value.ConnectorId == id))
            _discovered.TryRemove(b.Key, out _);
    }
    private ConnectorConfiguration Get(string id)
    {
        lock (_sync)
            return _configurations.TryGetValue(id, out var config) ? config.Copy() : throw new KeyNotFoundException("Connector configuration not found.");
    }
    private Task<ConnectorTestResult> RecordAsync(ConnectorConfiguration config, ConnectorTestResult result, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        config.LastTestMessage = result.Message;
        config.LastVerifiedAt = result.VerifiedAt;
        if (result.Account is not null)
            config.Account = result.Account;
        lock (_sync)
        {
            _configurations[config.Id] = config;
            Persist();
        }
        _states[config.Id] = result.Status;
        return Task.FromResult(result);
    }
    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_configurations.Values, FileJson));
        if (File.Exists(_file))
            File.Copy(_file, _file + ".bak", true);
        File.Move(temporary, _file, true);
    }
    private void DeleteSecrets(ConnectorConfiguration config)
    {
        foreach (var key in new[] { "token", "api-key", "client-secret", "oauth", "mcp-oauth" }.Concat(config.SecretEnvironmentNames.Select(x => "env." + x)))
            _credentials.Delete($"connector.{config.Id}.{key}");
    }
    private static string CredentialIdentity(ConnectorConfiguration c) => JsonSerializer.Serialize(new { c.Transport, c.Endpoint, c.Command, c.Arguments, c.WorkingDirectory, c.ClientId, c.AuthMode, c.Scopes });
    private static bool IsReady(ConnectorConfiguration c) => c.Transport switch { ConnectorTransport.Http => !string.IsNullOrWhiteSpace(c.Endpoint), ConnectorTransport.Stdio => !string.IsNullOrWhiteSpace(c.Command), ConnectorTransport.Google or ConnectorTransport.Spotify => !string.IsNullOrWhiteSpace(c.ClientId), ConnectorTransport.LocalFiles => Directory.Exists(c.LocalPath), ConnectorTransport.Unsupported => false, _ => true };
    private static void Validate(ConnectorConfiguration c, bool requireReady)
    {
        if (!SafeId().IsMatch(c.Id))
            throw new ArgumentException("Connector ID must contain 1–32 ASCII letters, digits, underscores or hyphens.");
        if (!ConnectorCatalog.Entries.Any(e => e.Id == c.CatalogId))
            throw new ArgumentException("Unknown catalog entry.");
        if (c.TimeoutSeconds is < 5 or > 600 || c.CallbackPort is < 1024 or > 65535)
            throw new ArgumentException("Use a 5–600 second timeout and an unprivileged callback port.");
        if (c.Transport == ConnectorTransport.Unsupported && requireReady)
            throw new PlatformNotSupportedException("This Apple-native integration is unavailable on Windows.");
        if (c.Transport == ConnectorTransport.Http && !string.IsNullOrWhiteSpace(c.Endpoint))
        {
            if (!Uri.TryCreate(c.Endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)
                || uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))
                throw new ArgumentException("MCP endpoints must use HTTPS, or HTTP on loopback only. Do not embed credentials in URLs.");
            if (Regex.IsMatch(uri.Query, "(?i)(token|secret|password|api.?key|authorization)="))
                throw new ArgumentException("Credentials cannot be stored in endpoint URLs. Use the protected token field.");
        }
        if (c.Transport is ConnectorTransport.Google or ConnectorTransport.Spotify or ConnectorTransport.PublicApi)
        {
            var entry = ConnectorCatalog.Entries.Single(x => x.Id == c.CatalogId);
            if (entry.Transport != c.Transport || c.Endpoint != entry.Endpoint)
                throw new ArgumentException("Built-in API endpoints are fixed. Use Custom MCP for another server.");
        }
        if (c.AuthMode == ConnectorAuthMode.ApiKey)
            throw new ArgumentException("Use Bearer for MCP access tokens. Arbitrary API-key headers are not supported.");
        if (c.SecretEnvironmentNames.Any(x => !Regex.IsMatch(x, "^[A-Za-z_][A-Za-z0-9_]{0,127}$")))
            throw new ArgumentException("Invalid secret environment variable name.");
        if (c.Arguments.Any(x => x.Contains('\0') || Regex.IsMatch(x, "(?i)(--?((access[-_]|auth[-_]|bearer[-_])?token|client[-_]?secret|secret|password|api[-_]?key))(=|\\s|$)")))
            throw new ArgumentException("Do not put credentials in command arguments. Use protected environment fields.");
        if (requireReady && !IsReady(c))
            throw new InvalidOperationException("Complete this connection's setup fields before testing.");
    }

    private static string ToolName(string id, string name)
    {
        var cleaned = Regex.Replace(name, "[^a-zA-Z0-9_-]", "_");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..6].ToLowerInvariant();
        var prefix = "cx_" + id + "_";
        var remaining = Math.Max(1, 63 - prefix.Length - hash.Length - 1);
        return prefix + cleaned[..Math.Min(cleaned.Length, remaining)] + "_" + hash;
    }
    private static string SafeError(Exception e) => e switch
    {
        UnauthorizedAccessException or ArgumentException or InvalidOperationException or PlatformNotSupportedException or DirectoryNotFoundException => e.Message,
        HttpRequestException http => $"Connection failed{(http.StatusCode is not null ? $" (HTTP {(int)http.StatusCode})" : "")}. Verify endpoint, network access, credentials, scopes and provider restrictions.",
        SocketException => "Could not bind or reach the connection. Check whether another application uses the OAuth callback port.",
        _ => "Connection failed (" + e.GetType().Name + "). Check setup, provider availability and supported protocol. No successful integration is claimed."
    };
    private static HttpClient CreatePublicHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, ct) =>
            {
                // Resolve at connection time and connect to the validated IP to prevent DNS rebinding.
                var ips = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                if (ips.Length == 0 || ips.Any(RestAdapters.IsPrivateAddress))
                    throw new HttpRequestException("Private addresses are not permitted for public API calls.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(ips, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HeyBuddy/0.1 (Windows personal assistant)");
        return client;
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var id in _clients.Keys)
                await DisconnectCoreAsync(id).ConfigureAwait(false);
            _bindings.Clear();
            if (_ownsHttp)
                _http.Dispose();
        }
        finally { _lifecycle.Release(); }
    }
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,32}$")] private static partial Regex SafeId();
    [GeneratedRegex("^[a-zA-Z0-9_.-]{1,160}$")] private static partial Regex SafeSecretKey();
}
