using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Clicky.Connectors;
using Clicky.Core;

var output = Path.GetFullPath(Value("--output") ?? Path.Combine("artifacts", "connector-live"));
Directory.CreateDirectory(output);
var isolationRoot = Path.Combine(Path.GetTempPath(), "HeyBuddy-connector-smoke", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(isolationRoot);
var started = DateTimeOffset.UtcNow;
var results = new List<ProbeResult>();
var options = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

await using (var service = new ConnectorService(new MemoryCredentials(), Path.Combine(isolationRoot, "connectors"),
    _ => throw new InvalidOperationException("Account authorization is prohibited in the anonymous smoke test.")))
{
    foreach (var catalogId in new[] { "web", "maps", "polymarket" })
    {
        var configuration = ConnectorConfiguration.FromCatalog(service.Catalog.Single(x => x.Id == catalogId));
        configuration.Enabled = true;
        configuration.TimeoutSeconds = 30;
        await service.SaveAsync(configuration, cancellation.Token);
        await ProbeAsync(service, configuration, "Anonymous public read", null);
    }

    foreach (var cli in new[] { (Id: "codex", Flag: "--codex"), (Id: "claude-code", Flag: "--claude") })
    {
        var executable = Value(cli.Flag);
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            results.Add(new(cli.Id, "MCP stdio initialization and tool discovery", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, 0, false, "Not tested", "Supply the existing executable with " + cli.Flag + ". No installation was attempted.",
                0, null, null, [], "Unverified; no account or tool invocation was used."));
            await RecordAsync();
            continue;
        }

        executable = Path.GetFullPath(executable);
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use a directly installed .exe, not a package-manager shim.");
        var profile = Path.Combine(isolationRoot, cli.Id);
        var workspace = Path.Combine(profile, "workspace");
        Directory.CreateDirectory(workspace);
        var environment = new Dictionary<string, string>
        {
            ["HOME"] = profile,
            ["USERPROFILE"] = profile,
            ["APPDATA"] = Path.Combine(profile, "AppData", "Roaming"),
            ["LOCALAPPDATA"] = Path.Combine(profile, "AppData", "Local"),
            ["CODEX_HOME"] = Path.Combine(profile, "codex"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(profile, "claude"),
            ["CLAUDE_CODE_SIMPLE"] = "1",
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["DISABLE_AUTOUPDATER"] = "1"
        };
        foreach (var path in environment.Where(x => x.Key is "HOME" or "USERPROFILE" or "APPDATA" or "LOCALAPPDATA" or "CODEX_HOME" or "CLAUDE_CONFIG_DIR").Select(x => x.Value))
            Directory.CreateDirectory(path);

        var configuration = ConnectorConfiguration.FromCatalog(service.Catalog.Single(x => x.Id == cli.Id));
        configuration.Command = executable;
        configuration.Arguments = cli.Id == "codex" ? ["mcp-server"] : ["--bare", "--setting-sources", "", "mcp", "serve"];
        configuration.WorkingDirectory = workspace;
        configuration.SecretEnvironmentNames = [.. environment.Keys];
        configuration.Enabled = true;
        configuration.TimeoutSeconds = 35;
        await service.SaveAsync(configuration, cancellation.Token);
        foreach (var variable in environment) service.SetSecret(configuration.Id, "env." + variable.Key, variable.Value);
        await ProbeAsync(service, configuration, "MCP stdio initialization and tool discovery", executable);
    }
}
await RecordAsync();
Console.WriteLine($"Recorded {results.Count(x => x.Passed)}/{results.Count} successful probes to {Path.Combine(output, "results.json")}");
return results.All(x => x.Passed) ? 0 : 1;

string? Value(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

async Task ProbeAsync(ConnectorService service, ConnectorConfiguration configuration, string kind, string? executable)
{
    var probeStarted = DateTimeOffset.UtcNow;
    var watch = Stopwatch.StartNew();
    Console.WriteLine($"{configuration.Name}: starting {kind.ToLowerInvariant()}");
    try
    {
        // TestAsync for these CLI catalog entries has no TestTool: it only initializes and lists tools.
        var result = await service.TestAsync(configuration.Id, cancellation.Token);
        results.Add(new(configuration.CatalogId, kind, probeStarted, DateTimeOffset.UtcNow, watch.Elapsed.TotalMilliseconds,
            result.Success, result.Status.ToString(), result.Message, result.ToolCount, executable, result.VerifiedAt,
            service.GetConnectorTools(configuration.Id).Select(x => x.OriginalName).ToArray(),
            executable is null ? "Not applicable: anonymous public data only." : "Unverified; no account or tool invocation was used."));
        Console.WriteLine($"{configuration.Name}: {result.Status}; {result.Message}");
    }
    catch (Exception error)
    {
        results.Add(new(configuration.CatalogId, kind, probeStarted, DateTimeOffset.UtcNow, watch.Elapsed.TotalMilliseconds,
            false, "Failed", error.GetType().Name + ": " + error.Message, 0, executable, null, [],
            executable is null ? "Not applicable: anonymous public data only." : "Unverified; no account or tool invocation was used."));
        Console.WriteLine($"{configuration.Name}: failed ({error.GetType().Name})");
    }
    finally
    {
        await service.DisconnectAsync(configuration.Id);
        await RecordAsync();
    }
}

async Task RecordAsync()
{
    var report = new
    {
        Product = "HeyBuddy",
        StartedAt = started,
        UpdatedAt = DateTimeOffset.UtcNow,
        Scope = "Live anonymous read probes and installed CLI MCP handshake/tool discovery only. No account authorization, no tool invocation for CLIs, no writes to services, no downloads, no saved user configuration changes.",
        IsolatedStateDirectory = isolationRoot,
        Passed = results.Count(x => x.Passed),
        Failed = results.Count(x => !x.Passed),
        Probes = results
    };
    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report, options));
}

sealed record ProbeResult(string Connector, string Probe, DateTimeOffset StartedAt, DateTimeOffset FinishedAt,
    double ElapsedMilliseconds, bool Passed, string Status, string Message, int ToolCount, string? Executable,
    DateTimeOffset? VerifiedAt, string[] DiscoveredToolNames, string AccountRead);

sealed class MemoryCredentials : ICredentialStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public void Set(string name, string value) => _values[name] = value;
    public void Delete(string name) => _values.TryRemove(name, out _);
}
