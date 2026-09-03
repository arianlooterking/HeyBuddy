using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Clicky.Core;
using ModelContextProtocol.Authentication;
using Xunit;

namespace Clicky.Connectors.Tests;

public sealed class ConnectorTests
{
    [Fact]
    public void ReviewedRiskRequiresExactTrustedProviderAndNeverTrustsUnknownNames()
    {
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "github"));
        Assert.Equal(RiskLevel.ReadOnly, ConnectorService.ReviewedRisk(config, "get_me"));
        Assert.Equal(RiskLevel.Sensitive, ConnectorService.ReviewedRisk(config, "send_message"));
        Assert.Equal(RiskLevel.Sensitive, ConnectorService.ReviewedRisk(config, "get_me_and_delete_everything"));
        config.Endpoint = "https://api.githubcopilot.com.evil.example/mcp/";
        Assert.Equal(RiskLevel.Sensitive, ConnectorService.ReviewedRisk(config, "get_me"));
        config.Endpoint = "https://api.githubcopilot.com/another-route";
        Assert.Equal(RiskLevel.Sensitive, ConnectorService.ReviewedRisk(config, "get_me"));
        config.Transport = ConnectorTransport.Stdio;
        Assert.Equal(RiskLevel.Sensitive, ConnectorService.ReviewedRisk(config, "get_me"));
    }

    [Fact]
    public async Task SettingsPersistWithoutCredentialsAndEndpointChangeInvalidatesSecrets()
    {
        using var dir = new TestDirectory();
        var secrets = new MemoryCredentials();
        await using var service = new ConnectorService(secrets, dir.Path);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "custom-mcp"));
        config.Endpoint = "https://example.org/mcp";
        config.AuthMode = ConnectorAuthMode.Bearer;
        await service.SaveAsync(config);
        service.SetSecret(config.Id, "token", "test-token-that-must-not-leak");
        var json = await File.ReadAllTextAsync(System.IO.Path.Combine(dir.Path, "connectors.json"));
        Assert.DoesNotContain("test-token-that-must-not-leak", json);
        await using (var reload = new ConnectorService(secrets, dir.Path))
        {
            Assert.Single(reload.Configurations);
            Assert.Equal(ConnectorStatus.Configured, reload.GetStatus(config.Id));
            Assert.Empty(reload.Tools);
        }
        config.Endpoint = "https://other.example.org/mcp";
        await service.SaveAsync(config);
        Assert.False(service.HasSecret(config.Id, "token"));
        Assert.Null(service.Configurations.Single().LastVerifiedAt);
    }

    [Theory]
    [InlineData("http://10.0.0.1/mcp")]
    [InlineData("https://user:password@example.org/mcp")]
    [InlineData("https://example.org/mcp?api_key=secret")]
    public async Task RejectsUnsafeEndpointConfigurations(string endpoint)
    {
        using var dir = new TestDirectory();
        await using var service = new ConnectorService(new MemoryCredentials(), dir.Path);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "custom-mcp"));
        config.Endpoint = endpoint;
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(config));
    }

    [Fact]
    public async Task RejectsSeparatePlaintextSecretArgument()
    {
        using var dir = new TestDirectory();
        await using var service = new ConnectorService(new MemoryCredentials(), dir.Path);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "custom-mcp"));
        config.Transport = ConnectorTransport.Stdio;
        config.Command = "example.exe";
        config.Arguments = ["--api-key", "must-not-persist"];
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(config));
        Assert.Empty(service.Configurations);
    }

    [Fact]
    public async Task GoogleAccountTestThenOperationsUseProtectedTokenAndFixedApiTarget()
    {
        using var dir = new TestDirectory();
        var secrets = new MemoryCredentials();
        var calls = new List<(HttpMethod Method, Uri? Url, string? Token)>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            calls.Add((request.Method, request.RequestUri, request.Headers.Authorization?.Parameter));
            return Json(request.RequestUri!.Host == "openidconnect.googleapis.com" ? "{\"email\":\"sample@example.test\"}" : "{\"messages\":[]}");
        }));
        await using var service = new ConnectorService(secrets, dir.Path, httpClient: http);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "gmail"));
        config.Enabled = true;
        config.ClientId = "test-public-client";
        await service.SaveAsync(config);
        secrets.Set($"connector.{config.Id}.oauth", JsonSerializer.Serialize(new
        {
            AccessToken = "private-test-token",
            RefreshToken = "private-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Scope = string.Join(' ', config.Scopes)
        }));
        var result = await service.TestAsync(config.Id);
        Assert.True(result.Success);
        Assert.Equal("sample@example.test", result.Account);
        Assert.All(service.Tools, t => Assert.Equal(RiskLevel.ReadOnly, t.Risk));
        var list = service.GetConnectorTools(config.Id).Single(x => x.OriginalName == "list_messages");
        var read = await service.ExecuteAsync(list.Definition.Name, JsonSchema.Parse("{\"q\":\"hello & other\",\"maxResults\":\"3\"}"), default);
        Assert.True(read.Success);
        Assert.Equal(2, calls.Count);
        Assert.Equal("gmail.googleapis.com", calls.Last().Url!.Host);
        Assert.Contains("q=hello%20%26%20other", calls.Last().Url!.AbsoluteUri);
        Assert.All(calls, c => Assert.Equal("private-test-token", c.Token));
        Assert.DoesNotContain("private-test-token", await File.ReadAllTextAsync(System.IO.Path.Combine(dir.Path, "connectors.json")));
    }

    [Fact]
    public async Task ExpiredGoogleTokensRefreshAndPersistReplacementOnlyInCredentialStore()
    {
        using var dir = new TestDirectory();
        var secrets = new MemoryCredentials();
        var refreshes = 0;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "oauth2.googleapis.com")
            {
                refreshes++;
                return Json("{\"access_token\":\"replacement\",\"expires_in\":3600}");
            }
            Assert.Equal("replacement", request.Headers.Authorization?.Parameter);
            return Json("{\"email\":\"refresh@example.test\"}");
        }));
        await using var service = new ConnectorService(secrets, dir.Path, httpClient: http);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "docs"));
        config.Enabled = true;
        config.ClientId = "public-client";
        await service.SaveAsync(config);
        secrets.Set($"connector.{config.Id}.oauth", JsonSerializer.Serialize(new
        {
            AccessToken = "expired",
            RefreshToken = "refresh-secret",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1),
            Scope = ""
        }));
        Assert.True((await service.TestAsync(config.Id)).Success);
        Assert.Equal(1, refreshes);
        Assert.Contains("refresh-secret", secrets.Get($"connector.{config.Id}.oauth"));
        Assert.DoesNotContain("replacement", await File.ReadAllTextAsync(System.IO.Path.Combine(dir.Path, "connectors.json")));
    }

    [Fact]
    public async Task VaultTraversalFailsAndDisconnectInvalidatesQueuedTools()
    {
        using var dir = new TestDirectory();
        var vault = System.IO.Path.Combine(dir.Path, "vault");
        Directory.CreateDirectory(vault);
        await File.WriteAllTextAsync(System.IO.Path.Combine(vault, "hello.md"), "Persian سلام and Turkish merhaba");
        await File.WriteAllTextAsync(System.IO.Path.Combine(dir.Path, "outside.md"), "private");
        await using var service = new ConnectorService(new MemoryCredentials(), dir.Path);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "obsidian"));
        config.Enabled = true;
        config.LocalPath = vault;
        await service.SaveAsync(config);
        Assert.True((await service.TestAsync(config.Id)).Success);
        var read = service.GetConnectorTools(config.Id).Single(x => x.OriginalName == "read_note").Definition.Name;
        Assert.True((await service.ExecuteAsync(read, JsonSchema.Parse("{\"path\":\"hello.md\"}"), default)).Success);
        Assert.False((await service.ExecuteAsync(read, JsonSchema.Parse("{\"path\":\"../outside.md\"}"), default)).Success);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(read, JsonSchema.Parse("{\"path\":\"hello.md\"}"), cancelled.Token));
        await service.DisconnectAsync(config.Id);
        Assert.False((await service.ExecuteAsync(read, JsonSchema.Parse("{\"path\":\"hello.md\"}"), default)).Success);
    }

    [Fact]
    public async Task OAuthCallbackRejectsWrongStateThenAcceptsBoundResponse()
    {
        var port = FreePort();
        var opened = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiver = new LoopbackOAuthReceiver(uri => opened.SetResult(uri));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var receiving = receiver.ReceiveAsync(new AuthorizationCallbackContext
        {
            AuthorizationUri = new("https://auth.example.test/authorize?state=expected-state&code_challenge=challenge"),
            RedirectUri = new($"http://127.0.0.1:{port}/callback/")
        }, cts.Token);
        await opened.Task;
        using var http = new HttpClient();
        var rejected = await http.GetAsync($"http://127.0.0.1:{port}/callback/?code=wrong&state=incorrect", cts.Token);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.False(receiving.IsCompleted);
        var accepted = await http.GetAsync($"http://127.0.0.1:{port}/callback/?code=correct&state=expected-state&iss=https%3A%2F%2Fauth.example.test", cts.Token);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var result = await receiving;
        Assert.Equal("correct", result!.Code);
        Assert.Equal("expected-state", result.State);
        Assert.Equal("https://auth.example.test", result.Iss);
    }

    [Fact]
    public async Task RealMcpHttpHandshakeDiscoveryAndCallPersistSessionAndUnknownToolStaysSensitive()
    {
        using var dir = new TestDirectory();
        await using var server = new McpHttpFixture();
        await using var service = new ConnectorService(new MemoryCredentials(), dir.Path);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "custom-mcp"));
        config.Enabled = true;
        config.Endpoint = server.Endpoint;
        await service.SaveAsync(config);
        var result = await service.TestAsync(config.Id);
        Assert.True(result.Success, result.Message);
        Assert.Equal(ConnectorStatus.Connected, result.Status);
        var tool = Assert.Single(service.Tools);
        Assert.Equal(RiskLevel.Sensitive, tool.Risk); // fixture advertises readOnlyHint=true; it cannot grant trust
        var call = await service.ExecuteAsync(tool.Name, JsonSchema.Parse("{\"message\":\"hello\"}"), default);
        Assert.True(call.Success, call.Message);
        Assert.Contains("hello", call.Message);
        Assert.Equal(1, server.InitializeCount);
        Assert.Equal(1, server.CallCount);
        await service.RefreshToolsAsync(config.Id);
        Assert.Equal(1, server.InitializeCount);
        await service.SetToolAccessAsync(config.Id, ["echo"]);
        Assert.Empty(service.Tools);
        Assert.Single(service.GetConnectorTools(config.Id));
        Assert.False((await service.ExecuteAsync(tool.Name, JsonSchema.Parse("{\"message\":\"blocked\"}"), default)).Success);
        Assert.Equal(1, server.CallCount);
        Assert.Contains("echo", service.Configurations.Single().DisabledTools);
        await service.SetToolAccessAsync(config.Id, []);
        Assert.Single(service.Tools);
        Assert.True((await service.ExecuteAsync(tool.Name, JsonSchema.Parse("{\"message\":\"re-enabled\"}"), default)).Success);
        Assert.Equal(2, server.CallCount);
        await service.DisconnectAsync(config.Id);
        Assert.Empty(service.Tools);
    }

    [Fact]
    public async Task RealStdioMcpServerReceivesOnlyExplicitSecretsAndCallsWork()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var dir = new TestDirectory();
        var script = System.IO.Path.Combine(dir.Path, "mcp-fixture.ps1");
        await File.WriteAllTextAsync(script, """
            while ($null -ne ($line = [Console]::ReadLine())) {
              $request = $line | ConvertFrom-Json
              if ($null -eq $request.id) { continue }
              if ($request.method -notin @('initialize','tools/list','tools/call','ping')) {
                [Console]::WriteLine((@{jsonrpc='2.0';id=$request.id;error=@{code=-32601;message='Method not found'}} | ConvertTo-Json -Depth 20 -Compress))
                continue
              }
              $result = switch ($request.method) {
                'initialize' { @{protocolVersion=$request.params.protocolVersion; capabilities=@{tools=@{}}; serverInfo=@{name='synthetic-stdio';version='1'}} }
                'tools/list' { @{tools=@(@{name='echo';description='Synthetic echo';inputSchema=@{type='object';properties=@{message=@{type='string'}}}})} }
                'tools/call' { @{content=@(@{type='text';text=($request.params.arguments.message + ' secret-present=' + [bool]$env:CLICKY_TEST_SECRET)})} }
                default { @{} }
              }
              [Console]::WriteLine((@{jsonrpc='2.0';id=$request.id;result=$result} | ConvertTo-Json -Depth 20 -Compress))
            }
            """);
        await using var service = new ConnectorService(new MemoryCredentials(), dir.Path);
        var config = ConnectorConfiguration.FromCatalog(ConnectorCatalog.Entries.Single(c => c.Id == "custom-mcp"));
        config.Enabled = true;
        config.Transport = ConnectorTransport.Stdio;
        config.Command = "pwsh.exe";
        config.Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", script];
        config.SecretEnvironmentNames = ["CLICKY_TEST_SECRET"];
        await service.SaveAsync(config);
        service.SetSecret(config.Id, "env.CLICKY_TEST_SECRET", "test-only-secret");
        var result = await service.TestAsync(config.Id);
        Assert.True(result.Success, result.Message);
        var tool = Assert.Single(service.Tools);
        var response = await service.ExecuteAsync(tool.Name, JsonSchema.Parse("{\"message\":\"local\"}"), default);
        Assert.True(response.Success, response.Message);
        Assert.Contains("local secret-present=True", response.Message);
    }

    internal static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    internal static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handle(request));
    }
    private sealed class MemoryCredentials : ICredentialStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        public string? Get(string name) => _values.GetValueOrDefault(name);
        public void Set(string name, string value) => _values[name] = value;
        public void Delete(string name) => _values.TryRemove(name, out _);
    }
    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clicky-connector-test-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }

    private sealed class McpHttpFixture : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _run;
        public string Endpoint { get; } = $"http://127.0.0.1:{FreePort()}/mcp/";
        public int InitializeCount;
        public int CallCount;
        public McpHttpFixture()
        {
            _listener.Prefixes.Add(Endpoint);
            _listener.Start();
            _run = RunAsync();
        }
        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync().WaitAsync(_stop.Token);
                    if (context.Request.HttpMethod != "POST")
                    {
                        context.Response.StatusCode = context.Request.HttpMethod == "DELETE" ? 204 : 405;
                        context.Response.Close();
                        continue;
                    }
                    using var reader = new StreamReader(context.Request.InputStream);
                    using var doc = JsonDocument.Parse(await reader.ReadToEndAsync(_stop.Token));
                    var request = doc.RootElement;
                    if (!request.TryGetProperty("id", out var id))
                    {
                        context.Response.StatusCode = 202;
                        context.Response.Close();
                        continue;
                    }
                    var method = request.GetProperty("method").GetString();
                    if (method is not "initialize" and not "tools/list" and not "tools/call" and not "ping")
                    {
                        var error = JsonSerializer.SerializeToUtf8Bytes(new
                        {
                            jsonrpc = "2.0",
                            id = id.Clone(),
                            error = new
                            {
                                code = -32601,
                                message = "Method not found"
                            }
                        });
                        context.Response.ContentType = "application/json";
                        context.Response.ContentLength64 = error.Length;
                        await context.Response.OutputStream.WriteAsync(error, _stop.Token);
                        context.Response.Close();
                        continue;
                    }
                    object result;
                    if (method == "initialize")
                    {
                        InitializeCount++;
                        result = new
                        {
                            protocolVersion = request.GetProperty("params").GetProperty("protocolVersion").GetString(),
                            capabilities = new
                            {
                                tools = new
                                {
                                }
                            },
                            serverInfo = new
                            {
                                name = "Synthetic HTTP",
                                version = "1"
                            }
                        };
                    }
                    else if (method == "tools/list")
                        result = new
                        {
                            tools = new[] { new { name = "echo", description = "Untrusted synthetic tool", annotations = new { readOnlyHint = true }, inputSchema = new { type = "object", properties = new { message = new { type = "string" } } } } }
                        };
                    else if (method == "tools/call")
                    {
                        CallCount++;
                        result = new
                        {
                            content = new[] { new { type = "text", text = request.GetProperty("params").GetProperty("arguments").GetProperty("message").GetString() } }
                        };
                    }
                    else
                        result = new
                        {
                        };
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        jsonrpc = "2.0",
                        id = id.Clone(),
                        result
                    });
                    context.Response.ContentType = "application/json";
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                    context.Response.Close();
                }
            }
            catch (OperationCanceledException) { }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _run;
            _listener.Close();
            _stop.Dispose();
        }
    }
}
