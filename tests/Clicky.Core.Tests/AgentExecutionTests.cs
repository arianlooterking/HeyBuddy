using System.Text.Json;
using Clicky.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clicky.Core.Tests;

public sealed class AgentExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HeyBuddyAgentExecutionTests", Guid.NewGuid().ToString("N"));
    private readonly AppStore store;
    private readonly AgentRunner runner;
    public AgentExecutionTests()
    {
        store = new(root);
        runner = new(store);
    }

    [Theory]
    [InlineData("prose")]
    [InlineData("discovery")]
    [InlineData("unknown")]
    [InlineData("failed")]
    public async Task RequiredActionRejectsCompletionWithoutSuccessfulExecution(string scenario)
    {
        var executor = new Executor([Definition("files.read")], (_, _, _) => Task.FromResult(new ToolResult(false, "File unavailable.")));
        var provider = new Provider((number, _, _, _) => Task.FromResult(number == 1 && scenario != "prose"
            ? new ModelReply("I will do that", [scenario switch
            {
                "discovery" => new("one", ToolDiscovery.SearchName, "{\"query\":\"files.read\"}"),
                "unknown" => new("one", "invented.tool", "{}"),
                _ => new ToolCall("one", "files.read", "{}")
            }])
            : new ModelReply("Done", [])));

        var run = await runner.RunAsync("Carry out this task", provider, [executor], requireAction: true);

        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Contains("No successful tool action", run.Result);
        Assert.Equal(scenario == "failed" ? 1 : 0, executor.Executions);
        Assert.Equal(RunStatus.Paused, Assert.Single(store.GetRuns()).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequiredActionMustResolveLatestFailureBeforeCompleting(bool correctiveActionSucceeds)
    {
        var attempts = 0;
        var executor = new Executor([Definition("desktop_apps"), Definition("desktop_launch", RiskLevel.LocalWrite)],
            (_, _, _) => Task.FromResult(++attempts == 2 ? new ToolResult(false, "Window did not appear.") : new ToolResult(true, "Verified result.")));
        var provider = new Provider((number, _, _, _) => Task.FromResult(number switch
        {
            1 => new("Inspect then launch", [new("one", "desktop_apps", "{}"), new("two", "desktop_launch", "{\"id\":\"app-1\"}")]),
            2 when correctiveActionSucceeds => new("Retry the verified app", [new("three", "desktop_launch", "{\"id\":\"app-1\"}")]),
            _ => new ModelReply("Done", [])
        }));

        var run = await runner.RunAsync("Open the app", provider, [executor], requireAction: true);

        Assert.Equal(correctiveActionSucceeds ? RunStatus.Completed : RunStatus.Paused, run.Status);
        Assert.Equal(correctiveActionSucceeds ? 3 : 2, run.Actions);
        if (!correctiveActionSucceeds)
            Assert.Contains("latest tool action failed", run.Result);
    }

    [Fact]
    public async Task AutoModeCannotCompleteAfterAnAttemptedToolFails()
    {
        var executor = new Executor([Definition("desktop_launch", RiskLevel.LocalWrite)],
            (_, _, _) => Task.FromResult(new ToolResult(false, "The requested app window did not appear.")));
        var provider = new Provider((number, _, _, _) => Task.FromResult(number == 1
            ? new ModelReply("Opening the app", [new("one", "desktop_launch", "{\"id\":\"app-1\"}")])
            : new ModelReply("Done", [])));

        var run = await runner.RunAsync("Open the app", provider, [executor]);

        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(1, run.Actions);
        Assert.Contains("latest tool action failed", run.Result);
        Assert.Contains("tool_failure", Events(run.Id));
        Assert.Equal(RunStatus.Paused, Assert.Single(store.GetRuns()).Status);
    }

    [Fact]
    public async Task RequiredStateChangeCannotCompleteAfterReadOnlyInspectionAndProse()
    {
        var executor = new Executor([Definition("desktop_snapshot", RiskLevel.ReadOnly)]);
        var provider = new Provider((number, request, _, _) =>
        {
            if (number == 1)
            {
                var system = Assert.Single(request.Messages, message => message.Role == "system").Content;
                Assert.Contains("Call the concrete action tool instead of asking for approval in prose", system);
                Assert.Contains("the app will enforce approval", system);
                return Task.FromResult(new ModelReply("I inspected the editor.", [new("inspect", "desktop_snapshot", "{\"windowId\":\"window-1\"}")]));
            }
            return Task.FromResult(new ModelReply("Done.", []));
        });

        var run = await runner.RunAsync("Append text in Notepad", provider, [executor], requireAction: true, requireStateChange: true);

        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(1, run.Actions);
        Assert.Contains("No successful state-changing action", run.Result);
    }

    [Theory]
    [InlineData("files.write", RiskLevel.LocalWrite)]
    [InlineData("messages.send", RiskLevel.Sensitive)]
    [InlineData("desktop_launch", RiskLevel.ReadOnly)]
    public async Task SuccessfulEffectiveWriteRiskCompletesRequiredStateChange(string toolName, RiskLevel declaredRisk)
    {
        runner.RequestApproval = (_, _) => Task.FromResult(true);
        var executor = new Executor([Definition(toolName, declaredRisk)]);
        var provider = new Provider((number, _, _, _) => Task.FromResult(number == 1
            ? new ModelReply("Acting.", [new("change", toolName, "{}")])
            : new ModelReply("Verified.", [])));

        var run = await runner.RunAsync("Make the requested change", provider, [executor], requireAction: true, requireStateChange: true);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(1, run.Actions);
        Assert.Equal(1, executor.Executions);
    }

    [Fact]
    public async Task TypingRequestPausesAfterActivationAndInspectionWithoutTyping()
    {
        const string prompt = "Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not open an app, click, press Enter, save, close, or touch any other document.";
        var requirement = ActionIntent.RequiredCompletion(prompt)!;
        var executor = new Executor([Definition("desktop_activate", RiskLevel.LocalWrite), Definition("desktop_snapshot")]);
        var provider = new Provider((number, request, _, _) =>
        {
            if (number == 1)
            {
                var system = Assert.Single(request.Messages, message => message.Role == "system").Content;
                Assert.Contains("desktop_type", system);
                Assert.Contains("let the app handle any approval", system);
                return Task.FromResult(new ModelReply("Preparing the editor.",
                    [new("activate", "desktop_activate", "{\"id\":\"window-1\"}"), new("inspect", "desktop_snapshot", "{\"id\":\"window-1\"}")]));
            }
            return Task.FromResult(new ModelReply("Please approve typing and I will continue.", []));
        });

        var run = await runner.RunAsync(prompt, provider, [executor], requiredCompletion: requirement);

        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(["desktop_activate", "desktop_snapshot"], executor.ExecutedTools);
        Assert.Contains("desktop_type", run.Result);
        Assert.Contains("typing the requested text", run.Result);
    }

    [Fact]
    public async Task ApprovedSuccessfulTypingSatisfiesTypingRequirement()
    {
        const string prompt = "Type Hello from HeyBuddy into Notepad.";
        var requirement = ActionIntent.RequiredCompletion(prompt)!;
        ApprovalRequest? approval = null;
        runner.RequestApproval = (request, _) =>
        {
            approval = request;
            return Task.FromResult(true);
        };
        var executor = new Executor([Definition("desktop_activate", RiskLevel.LocalWrite), Definition("desktop_type", RiskLevel.Sensitive)]);
        var provider = new Provider((number, _, _, _) => Task.FromResult(number switch
        {
            1 => new ModelReply("Activating the editor.", [new("activate", "desktop_activate", "{\"id\":\"window-1\"}")]),
            2 => new ModelReply("Typing the requested text.", [new("type", "desktop_type", "{\"windowId\":\"window-1\",\"text\":\"Hello from HeyBuddy\"}")]),
            _ => new ModelReply("The text was typed successfully.", [])
        }));

        var run = await runner.RunAsync(prompt, provider, [executor], requiredCompletion: requirement);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(["desktop_activate", "desktop_type"], executor.ExecutedTools);
        Assert.NotNull(approval);
        Assert.Equal("desktop_type", approval.ToolName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSensitiveActionCannotBypassMissingOrDeniedApproval(bool hasApprovalCallback)
    {
        var executor = new Executor([Definition("desktop_click", RiskLevel.LocalWrite)]);
        ApprovalRequest? preview = null;
        if (hasApprovalCallback)
            runner.RequestApproval = (request, _) => { preview = request; return Task.FromResult(false); };

        var run = await runner.RunToolAsync("Click the selected button", "desktop_click", JsonSchema.Parse("{\"windowId\":\"window-1\",\"elementId\":\"button-2\"}"), [executor]);

        Assert.Equal(RunStatus.Paused, run.Status);
        Assert.Equal(0, run.Actions);
        Assert.Equal(0, executor.Executions);
        Assert.Contains("denied", Events(run.Id));
        Assert.Equal(run, Assert.Single(store.GetRuns()));
        if (hasApprovalCallback)
        {
            Assert.NotNull(preview);
            Assert.Equal(RiskLevel.Sensitive, preview.Risk);
            Assert.Contains("button-2", preview.Arguments);
        }
    }

    [Fact]
    public async Task DirectActionCannotExecuteWhenCancellationArrivesWithApproval()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new Executor([Definition("messages.send")]);
        runner.RequestApproval = (_, _) => { cancellation.Cancel(); return Task.FromResult(true); };

        var run = await runner.RunToolAsync("Send the draft", "messages.send", JsonSchema.Parse("{\"text\":\"draft\"}"), [executor], cancellationToken: cancellation.Token);

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(0, run.Actions);
        Assert.DoesNotContain("tool_success", Events(run.Id));
    }

    [Fact]
    public async Task DirectActionRecordsReturnedEffectBeforeCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var executor = new Executor([Definition("desktop_launch", RiskLevel.LocalWrite)], (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new ToolResult(true, "The expected app window was verified."));
        });

        var run = await runner.RunToolAsync("Open the app", "desktop_launch", JsonSchema.Parse("{\"id\":\"app-1\"}"), [executor], cancellationToken: cancellation.Token);

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(1, executor.Executions);
        Assert.Equal(1, run.Actions);
        Assert.Contains("tool_success", Events(run.Id));
        Assert.Contains("expected app window", Assert.Single(store.GetHistory(session: run.Id), h => h.Role == "tool").Text);
    }

    [Fact]
    public async Task DirectRunCanBeCancelledByItsPublishedIdBeforeDispatch()
    {
        var executor = new Executor([Definition("desktop_activate", RiskLevel.LocalWrite)]);
        var progress = new List<AgentRun>();
        var run = await runner.RunToolAsync("Activate a window", "desktop_activate", JsonSchema.Parse("{\"id\":\"window-1\"}"), [executor], onProgress: update =>
        {
            progress.Add(update);
            if (update.Status == RunStatus.Running)
                runner.Cancel(update.Id);
        });

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(0, executor.Executions);
        Assert.Equal(run.Id, progress[0].Id);
        Assert.Equal(RunStatus.Cancelled, progress[^1].Status);
    }

    [Fact]
    public async Task DirectFailureIsPersistedInsteadOfCompleted()
    {
        var executor = new Executor([Definition("desktop_launch", RiskLevel.LocalWrite)], (_, _, _) => Task.FromResult(new ToolResult(false, "No matching window was verified.")));
        var run = await runner.RunToolAsync("Open an app", "desktop_launch", JsonSchema.Parse("{\"id\":\"app-1\"}"), [executor]);

        Assert.Equal(RunStatus.Failed, run.Status);
        Assert.Equal(1, run.Actions);
        Assert.Contains("No matching window", run.Result);
        Assert.Contains("tool_failure", Events(run.Id));
        Assert.Equal(run, Assert.Single(store.GetRuns()));
    }

    [Fact]
    public async Task StreamingCancellationSuppressesLateTextAndToolDispatch()
    {
        var executor = new Executor([Definition("files.read")]);
        var observed = new List<string>();
        string? runId = null;
        var provider = new Provider((_, _, onText, _) =>
        {
            onText?.Invoke("First delta");
            onText?.Invoke("Late delta");
            return Task.FromResult(new ModelReply("Late reply", [new("one", "files.read", "{}")]));
        });

        var run = await runner.RunAsync("Read the file", provider, [executor], onProgress: update => runId = update.Id,
            onText: delta => { observed.Add(delta); runner.Cancel(runId!); });

        Assert.Equal(RunStatus.Cancelled, run.Status);
        Assert.Equal(["First delta"], observed);
        Assert.Equal(0, executor.Executions);
    }

    [Fact]
    public async Task PreviousStructuredTurnsArePassedBeforeCurrentPromptAndOrdinaryAnswersStillComplete()
    {
        ChatMessage[] previous = [new("user", "Remember this question"), new("assistant", "I inspected it", ToolCalls: [new("prior", "files.read", "{}")]), new("tool", "Observed contents", ToolCallId: "prior")];
        var progress = new List<AgentRun>();
        var streamed = new List<string>();
        var provider = new Provider((_, request, onText, _) =>
        {
            Assert.Equal("Current question", request.Messages[^1].Content);
            Assert.Contains(request.Messages, m => m.ToolCallId == "prior" && m.Content == "Observed contents");
            Assert.Contains(request.Messages, m => m.ToolCalls is { Count: 1 } calls && calls[0].Id == "prior");
            onText?.Invoke("An answer");
            return Task.FromResult(new ModelReply("An answer", []));
        });

        var run = await runner.RunAsync("Current question", provider, [], previousMessages: previous, onProgress: progress.Add, onText: streamed.Add);

        Assert.Equal(RunStatus.Completed, run.Status);
        Assert.Equal(0, run.Actions);
        Assert.Equal(["An answer"], streamed);
        Assert.Equal(RunStatus.Running, progress[0].Status);
        Assert.Equal(run, progress[^1]);
    }

    [Theory]
    [InlineData("desktop_apps", RiskLevel.ReadOnly, RiskLevel.ReadOnly)]
    [InlineData("desktop_launch", RiskLevel.ReadOnly, RiskLevel.LocalWrite)]
    [InlineData("desktop_activate", RiskLevel.LocalWrite, RiskLevel.LocalWrite)]
    [InlineData("desktop_launch", RiskLevel.Sensitive, RiskLevel.Sensitive)]
    [InlineData("desktop_launch_custom", RiskLevel.ReadOnly, RiskLevel.Sensitive)]
    [InlineData("Desktop_Launch", RiskLevel.ReadOnly, RiskLevel.Sensitive)]
    [InlineData("desktop_key", RiskLevel.LocalWrite, RiskLevel.Sensitive)]
    [InlineData("desktop_scroll", RiskLevel.ReadOnly, RiskLevel.Sensitive)]
    public void OnlyExactReviewedDesktopCapabilitiesReceiveTheExemption(string name, RiskLevel declared, RiskLevel expected)
        => Assert.Equal(expected, ToolPolicy.EffectiveRisk(Definition(name, declared), "{}"));

    private static ToolDefinition Definition(string name, RiskLevel risk = RiskLevel.ReadOnly) => new(name, "Test capability", JsonSchema.Parse("{\"type\":\"object\"}"), risk);
    private string[] Events(string runId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "clicky.db") }.ToString());
        connection.Open();
        using var query = connection.CreateCommand();
        query.CommandText = "SELECT kind FROM events WHERE run_id=$id ORDER BY id";
        query.Parameters.AddWithValue("$id", runId);
        using var reader = query.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result.ToArray();
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
    }
    private sealed class Provider(Func<int, ModelRequest, Action<string>?, CancellationToken, Task<ModelReply>> respond) : IModelProvider
    {
        private int calls;
        public string Name => "Test provider";
        public bool IsCloud => false;
        public Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken) => respond(++calls, request, onText, cancellationToken);
    }
    private sealed class Executor(IReadOnlyList<ToolDefinition> tools, Func<string, JsonElement, CancellationToken, Task<ToolResult>>? execute = null) : IToolExecutor
    {
        public IReadOnlyList<ToolDefinition> Tools => tools;
        public int Executions
        {
            get; private set;
        }
        public List<string> ExecutedTools { get; } = [];
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            Executions++;
            ExecutedTools.Add(name);
            return execute?.Invoke(name, arguments, cancellationToken) ?? Task.FromResult(new ToolResult(true, "Verified test action."));
        }
    }
}
