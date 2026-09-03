using System.Text.Json;
using Clicky.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class AgentPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HeyBuddyCoreTests", Guid.NewGuid().ToString("N"));
    private readonly AppStore store;
    private readonly AgentRunner runner;
    public AgentPolicyTests()
    {
        store = new(root);
        runner = new(store);
    }

    [Fact]
    public async Task DenialPausesWithoutExecutionOrAlternateRoute()
    {
        var executor = new RecordingExecutor([Definition("message.send", RiskLevel.Sensitive), Definition("files.read")]);
        var provider = new ScriptedProvider((_, _, _) => Task.FromResult(new ModelReply("", [new("one", "message.send", "{}"), new("two", "files.read", "{}")])));
        runner.RequestApproval = (_, _) => Task.FromResult(false);
        var run = await runner.RunAsync("Send a message", provider, [executor]);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(0, run.Actions);
        Assert.Equal(1, provider.Calls);
        Assert.Contains("denied", Events(run.Id));
        Assert.DoesNotContain(store.GetHistory(session: run.Id), h => h.Role == "tool");
    }

    [Fact]
    public async Task SensitiveActionsFailClosedWithoutApprovalCallback()
    {
        var executor = new RecordingExecutor([Definition("message.send", RiskLevel.ReadOnly)]);
        var run = await runner.RunAsync("Send", ProviderCalling("message.send"), [executor]);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(0, executor.Executions);
    }

    [Fact]
    public async Task CancelledProviderCannotDispatchALateTool()
    {
        using var cancelled = new CancellationTokenSource();
        var provider = new ScriptedProvider((_, _, _) => { cancelled.Cancel(); return Task.FromResult(new ModelReply("late", [new("one", "files.read", "{}")])); });
        var executor = new RecordingExecutor([Definition("files.read")]);
        var run = await runner.RunAsync("Read", provider, [executor], cancellationToken: cancelled.Token);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(0, run.Actions);
    }

    [Fact]
    public async Task CompletedEffectIsRecordedWhenCancellationArrivesAtItsReturn()
    {
        using var cancel = new CancellationTokenSource();
        var executor = new RecordingExecutor([Definition("files.create", RiskLevel.LocalWrite)], () => { cancel.Cancel(); return new(true, "Created the local file."); });
        var provider = new ScriptedProvider((_, _, _) => Task.FromResult(new ModelReply("", [new("one", "files.create", "{}"), new("two", "files.create", "{}")])));
        var run = await runner.RunAsync("Create two files", provider, [executor], cancellationToken: cancel.Token);
        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(1, executor.Executions);
        Assert.Equal(1, run.Actions);
        Assert.Contains("tool_success", Events(run.Id));
        Assert.Single(store.GetHistory(session: run.Id), h => h.Role == "tool");
    }

    [Fact]
    public async Task ActionLimitStopsInsideABatchOfToolCalls()
    {
        var executor = new RecordingExecutor([Definition("files.read")]);
        var provider = new ScriptedProvider((_, _, _) => Task.FromResult(new ModelReply("", Enumerable.Range(0, 35).Select(i => new ToolCall(i.ToString(), "files.read", "{}")).ToArray())));
        var run = await runner.RunAsync("Read documents", provider, [executor]);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(30, run.Actions);
        Assert.Equal(30, executor.Executions);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task FailedActionGetsOnlyTwoRetriesEvenWithReorderedJson()
    {
        var executor = new RecordingExecutor([Definition("files.read")], () => new(false, "File is locked."));
        var provider = new ScriptedProvider((n, _, _) => Task.FromResult(new ModelReply("", [new(n.ToString(), "files.read", n % 2 == 0 ? "{\"path\":\"x\",\"offset\":0}" : "{ \"offset\": 0, \"path\": \"x\" }")])));
        var run = await runner.RunAsync("Read locked file", provider, [executor]);
        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(3, executor.Executions);
        Assert.Equal(3, run.Actions);
        Assert.Equal(3, provider.Calls);
    }

    [Fact]
    public async Task UnknownToolIsNeverExecutedAndCannotBeCompletedByProse()
    {
        var executor = new RecordingExecutor([Definition("files.read")]);
        var provider = new ScriptedProvider((n, request, _) =>
        {
            if (n == 1)
                return Task.FromResult(new ModelReply("", [new("one", "invented.tool", "{}")]));
            Assert.Contains(request.Messages, m => m.Role == "tool" && m.Content.Contains("Unknown tool", StringComparison.Ordinal));
            return Task.FromResult(new ModelReply("Done", []));
        });
        var run = await runner.RunAsync("Try unknown", provider, [executor]);
        Assert.Equal(0, run.Actions);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(RunStatus.Paused, run.Status);
    }

    [Fact]
    public async Task ApprovalReceivesConcreteArgumentsAndRunsOnlyOnConsent()
    {
        ApprovalRequest? shown = null;
        runner.RequestApproval = (request, _) => { shown = request; return Task.FromResult(true); };
        var executor = new RecordingExecutor([Definition("message.send", RiskLevel.Sensitive)]);
        var provider = new ScriptedProvider((n, _, _) => Task.FromResult(n == 1 ? new ModelReply("", [new("one", "message.send", "{\"recipient\":\"test\",\"text\":\"Draft\"}")]) : new ModelReply("Sent", [])));
        var run = await runner.RunAsync("Send the approved draft", provider, [executor]);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(1, executor.Executions);
        Assert.NotNull(shown);
        Assert.Contains("Draft", shown.Arguments);
        Assert.Equal(RiskLevel.Sensitive, shown.Risk);
        Assert.Contains("approved", Events(run.Id));
    }

    [Theory]
    [InlineData("desktop_type")]
    [InlineData("desktop_click")]
    [InlineData("run_sql")]
    [InlineData("inventory_adjust")]
    [InlineData("publish_post")]
    public void DangerousNamesCannotDowngradeRisk(string name) => Assert.Equal(RiskLevel.Sensitive, ToolPolicy.EffectiveRisk(Definition(name), "{}"));

    [Fact]
    public async Task ImagesAreForwardedWithoutBecomingActions()
    {
        var provider = new ScriptedProvider((_, request, _) => { Assert.Single(request.Messages.Last().Images!); return Task.FromResult(new ModelReply("Image understood", [])); });
        var run = await runner.RunAsync("Look", provider, [], images: [new("AA==")]);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(0, run.Actions);
    }

    private static ToolDefinition Definition(string name, RiskLevel risk = RiskLevel.ReadOnly) => new(name, "Test tool", JsonSchema.Parse("{\"type\":\"object\"}"), risk);
    [Fact]
    public async Task DiscoveryAddsARegisteredToolWithoutExecutingItAndKeepsItsApproval()
    {
        const string hidden = "cx_test_send_message";
        var executor = new RecordingExecutor([Definition(hidden, RiskLevel.Sensitive)]);
        var approvals = 0;
        runner.RequestApproval = (_, _) => { approvals++; return Task.FromResult(true); };
        var provider = new ScriptedProvider((number, request, _) =>
        {
            if (number == 1)
            {
                Assert.DoesNotContain(request.Tools!, t => t.Name == hidden);
                return Task.FromResult(new ModelReply("", [new("discover", ToolDiscovery.SearchName, "{\"query\":\"cx_test_send_message\"}")]));
            }
            if (number == 2)
            {
                Assert.Contains(request.Tools!, t => t.Name == hidden);
                Assert.Equal(0, executor.Executions);
                Assert.Equal(0, approvals);
                return Task.FromResult(new ModelReply("", [new("action", hidden, "{}")]));
            }
            return Task.FromResult(new ModelReply("Approved action finished", []));
        });
        var run = await runner.RunAsync("Investigate", provider, [executor]);
        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(1, executor.Executions);
        Assert.Equal(1, approvals);
        Assert.Equal(2, run.Actions);
    }
    [Fact]
    public async Task OversizedTaskFailsBeforeProviderOrToolDispatch()
    {
        var provider = ProviderCalling("files.read");
        var executor = new RecordingExecutor([Definition("files.read")]);
        var run = await runner.RunAsync(new string('x', 60000), provider, [executor]);
        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(0, provider.Calls);
        Assert.Equal(0, executor.Executions);
        Assert.Contains("Shorten the prompt", run.Result);
    }
    private static ScriptedProvider ProviderCalling(string name) => new((_, _, _) => Task.FromResult(new ModelReply("", [new("one", name, "{}")])));
    private string[] Events(string runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "clicky.db") }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT kind FROM events WHERE run_id=$id ORDER BY id";
        query.Parameters.AddWithValue("$id", runId);
        using var reader = query.ExecuteReader();
        var results = new List<string>();
        while (reader.Read())
            results.Add(reader.GetString(0));
        return results.ToArray();
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }

    private sealed class ScriptedProvider(Func<int, ModelRequest, CancellationToken, Task<ModelReply>> respond) : IModelProvider
    {
        public string Name => "Deterministic test provider";
        public bool IsCloud => false;
        public int Calls
        {
            get; private set;
        }
        public Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken) => respond(++Calls, request, cancellationToken);
    }
    private sealed class RecordingExecutor(IReadOnlyList<ToolDefinition> tools, Func<ToolResult>? execute = null) : IToolExecutor
    {
        public IReadOnlyList<ToolDefinition> Tools => tools;
        public int Executions
        {
            get; private set;
        }
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.FromResult(execute?.Invoke() ?? new(true, "Read finished"));
        }
    }
}
