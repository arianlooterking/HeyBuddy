using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Clicky.Core;

public sealed class AgentRunner
{
    private readonly AppStore store;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new();
    public event Action<AgentRun>? RunChanged;
    public Func<ApprovalRequest, CancellationToken, Task<bool>>? RequestApproval
    {
        get; set;
    }
    public AgentRunner(AppStore store) => this.store = store;
    public void Cancel(string id)
    {
        if (active.TryGetValue(id, out var source))
            TryCancel(source);
    }
    public void CancelAll()
    {
        foreach (var source in active.Values)
            TryCancel(source);
    }
    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException) { /* The run finished after the active snapshot was read. */ }
    }

    /// <summary>Observers report progress only and may be called off the UI thread.</summary>
    public async Task<AgentRun> RunAsync(string prompt, IModelProvider provider, IReadOnlyList<IToolExecutor> executors,
        string context = "", string? parentId = null, CancellationToken cancellationToken = default, IReadOnlyList<ImageAttachment>? images = null, int contextTokens = 8192,
        bool requireAction = false, bool requireStateChange = false, ActionCompletionRequirement? requiredCompletion = null,
        Action<AgentRun>? onProgress = null, Action<string>? onText = null, IReadOnlyList<ChatMessage>? previousMessages = null, string? persistedPrompt = null)
    {
        using var state = new RunExecution(this, persistedPrompt ?? prompt, parentId, cancellationToken, onProgress);
        var token = state.Source.Token;
        try
        {
            requireStateChange |= requiredCompletion is not null;
            requireAction |= requireStateChange;
            var tools = executors.SelectMany(e => e.Tools).Where(t => t.Name != ToolDiscovery.SearchName).GroupBy(t => t.Name, StringComparer.Ordinal).Select(g => g.First()).ToArray();
            var discovery = new ToolDiscovery(tools, prompt);
            var messages = new List<ChatMessage> { new("system", PromptCatalog.Agent + "\nOnly a bounded subset of registered tools is shown. If you need another capability, use tools.search with the app and task. Discovery never executes actions.\nUser-managed context:\n" + ContextBudget.ExcerptContext(context)) };
            if (requireAction)
                messages[0] = messages[0] with
                {
                    Content = messages[0].Content + "\nThis task requires actual tool execution. Prose, plans, and tool discovery alone do not complete it. Use a registered tool and verify its result, or explain why the task cannot be completed."
                };
            if (requireStateChange)
                messages[0] = messages[0] with
                {
                    Content = messages[0].Content + "\nThis task requires a successful state-changing tool action. Read-only inspection cannot complete it. Call the concrete action tool instead of asking for approval in prose; the app will enforce approval, show the user the exact preview when required, and return the action result."
                };
            if (requiredCompletion is not null)
                messages[0] = messages[0] with
                {
                    Content = messages[0].Content + $"\nCompletion evidence is still required for {requiredCompletion.Description}. A successful registered tool must match: {requiredCompletion.ToolHint}. Preparatory inspection and unrelated state changes do not complete the request. Call the required action tool and let the app handle any approval instead of asking for approval in prose."
                };
            if (parentId is not null)
                messages.Add(new("user", "Previous task context, for reference only:\n" + string.Join("\n", store.GetHistory(session: parentId, limit: 30).Reverse().Select(h => h.Role + ": " + h.Text))));
            if (previousMessages is not null)
                messages.AddRange(previousMessages);
            messages.Add(new("user", prompt, images));
            for (var round = 0; round < 30; round++)
            {
                if (state.StopAtLimit())
                    return state.Run;
                var request = ContextBudget.Fit(new(messages, discovery.Visible), contextTokens);
                var reply = await provider.CompleteAsync(request, onText is null ? null : delta =>
                {
                    if (!token.IsCancellationRequested)
                        Notify(onText, delta);
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                messages.Add(new("assistant", reply.Text, ToolCalls: reply.ToolCalls));
                if (!string.IsNullOrWhiteSpace(reply.Text))
                    store.AddMessage(state.Run.Id, "agent", "assistant", reply.Text);
                if (reply.ToolCalls.Count == 0)
                {
                    if (requiredCompletion is not null && !state.Satisfies(requiredCompletion))
                        state.Update(RunStatus.Paused, $"No successful tool result verified {requiredCompletion.Description}. Still required: {requiredCompletion.ToolHint}. Review the task before continuing." + ProseDetail(reply.Text));
                    else if (requireAction && state.SuccessfulActions == 0)
                        state.Update(RunStatus.Paused, "No successful tool action was verified. Review the task before continuing." + ProseDetail(reply.Text));
                    else if (requireStateChange && state.SuccessfulStateChanges == 0)
                        state.Update(RunStatus.Paused, "No successful state-changing action was verified. Read-only inspection did not complete the requested change; review the task before continuing." + ProseDetail(reply.Text));
                    else if (state.LastActionSucceeded == false)
                        state.Update(RunStatus.Paused, "The latest tool action failed. Its intended result is unverified; review the failure before continuing." + ProseDetail(reply.Text));
                    else
                        state.Update(RunStatus.Completed, reply.Text);
                    return state.Run;
                }
                foreach (var call in reply.ToolCalls)
                {
                    if (state.StopAtLimit())
                        return state.Run;
                    ToolResult result;
                    if (call.Name == ToolDiscovery.SearchName)
                    {
                        using var arguments = JsonDocument.Parse(call.Arguments);
                        result = discovery.Search(arguments.RootElement);
                        state.RecordDiscovery(result);
                        state.RecordToolMessage(call.Name, result);
                    }
                    else
                    {
                        var execution = await ExecuteToolAsync(state, call, executors).ConfigureAwait(false);
                        if (execution is null)
                            return state.Run;
                        result = execution;
                    }
                    messages.Add(new("tool", ContextBudget.ToolResultExcerpt(result), ToolCallId: call.Id));
                    token.ThrowIfCancellationRequested();
                    if (state.RetryLimitReached)
                    {
                        state.Update(RunStatus.Paused, "The same action failed three times (initial attempt plus two retries). Review the failure before continuing.");
                        return state.Run;
                    }
                }
            }
            state.Update(RunStatus.Paused, "Reached the reasoning-step limit. Review and continue explicitly.");
        }
        catch (ContextBudgetExceededException error) { state.Update(state.Run.Actions > 0 ? RunStatus.Paused : RunStatus.Failed, error.Message); }
        catch (OperationCanceledException) { state.RecordCancellation(); }
        catch (Exception error) { state.Update(RunStatus.Failed, error.Message); }
        return state.Run;
    }

    /// <summary>Executes one exact registered tool through the same approval, cancellation and persistence path as model-selected tools.</summary>
    public async Task<AgentRun> RunToolAsync(string prompt, string toolName, JsonElement arguments, IReadOnlyList<IToolExecutor> executors,
        string? parentId = null, CancellationToken cancellationToken = default, Action<AgentRun>? onProgress = null)
    {
        using var state = new RunExecution(this, prompt, parentId, cancellationToken, onProgress);
        try
        {
            if (state.StopAtLimit())
                return state.Run;
            var result = await ExecuteToolAsync(state, new(Guid.NewGuid().ToString("N"), toolName, arguments.GetRawText()), executors).ConfigureAwait(false);
            state.Source.Token.ThrowIfCancellationRequested();
            if (result is not null)
                state.Update(result.Success ? RunStatus.Completed : RunStatus.Failed, result.Message);
        }
        catch (OperationCanceledException) { state.RecordCancellation(); }
        catch (Exception error) { state.Update(RunStatus.Failed, error.Message); }
        return state.Run;
    }

    private async Task<ToolResult?> ExecuteToolAsync(RunExecution state, ToolCall call, IReadOnlyList<IToolExecutor> executors)
    {
        var token = state.Source.Token;
        token.ThrowIfCancellationRequested();
        // Resolve the current definition from the same executor that will receive the call.
        var registered = executors.Select(e => (Executor: e, Definition: e.Tools.FirstOrDefault(t => t.Name == call.Name)))
            .FirstOrDefault(x => x.Definition is not null);
        if (registered.Definition is null || call.Name == ToolDiscovery.SearchName)
        {
            var unknown = new ToolResult(false, "Unknown tool; no action was performed.");
            state.LastActionSucceeded = false;
            state.RecordToolMessage(call.Name, unknown);
            return unknown;
        }
        using var document = JsonDocument.Parse(call.Arguments);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Tool arguments must be a JSON object.");
        var risk = ToolPolicy.EffectiveRisk(registered.Definition, call.Arguments);
        if (risk == RiskLevel.Sensitive)
        {
            state.Update(RunStatus.AwaitingApproval, $"Approval required: {registered.Definition.Name}");
            token.ThrowIfCancellationRequested();
            var approved = RequestApproval is not null && await RequestApproval(new(state.Run.Id, registered.Definition.Name,
                registered.Definition.Description, call.Arguments, risk), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            store.AddEvent(state.Run.Id, approved ? "approved" : "denied", registered.Definition.Name);
            if (!approved)
            {
                state.Update(RunStatus.Paused, "Action declined. No alternative route will be attempted. Edit the task to continue.");
                return null;
            }
            state.Update(RunStatus.Running);
        }
        token.ThrowIfCancellationRequested();
        ToolResult result;
        try
        {
            result = await registered.Executor.ExecuteAsync(call.Name, document.RootElement.Clone(), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var interrupted = new ToolResult(false, "Tool execution was interrupted. Its final state is unknown; inspect the target before retrying a write.");
            state.RecordResult(call.Name, document.RootElement, interrupted, risk);
            state.RecordToolMessage(call.Name, interrupted);
            throw;
        }
        catch (Exception error) { result = new(false, error.Message); }
        // A returned result describes an attempted action even if cancellation arrived as it completed.
        // Persist it before observing cancellation; never dispatch another action afterward.
        state.RecordResult(call.Name, document.RootElement, result, risk);
        state.RecordToolMessage(call.Name, result);
        return result;
    }

    private static string ProseDetail(string text) => string.IsNullOrWhiteSpace(text) ? "" : "\n\nModel response (not execution evidence):\n" + text;
    private static void Notify<T>(Action<T>? observers, T value)
    {
        if (observers is null)
            return;
        foreach (Action<T> observer in observers.GetInvocationList())
        {
            try
            {
                observer(value);
            }
            catch { /* An observational UI callback cannot change approval or execution state. */ }
        }
    }

    private sealed class RunExecution : IDisposable
    {
        private readonly AgentRunner owner;
        private readonly Action<AgentRun>? onProgress;
        private readonly Dictionary<string, int> failedAttempts = new(StringComparer.Ordinal);
        private readonly List<(string Name, RiskLevel Risk)> successfulToolEvidence = [];
        public AgentRun Run
        {
            get; private set;
        }
        public CancellationTokenSource Source
        {
            get;
        }
        public int SuccessfulActions
        {
            get; private set;
        }
        public int SuccessfulStateChanges
        {
            get; private set;
        }
        public bool? LastActionSucceeded
        {
            get; set;
        }
        public bool RetryLimitReached
        {
            get; private set;
        }
        public RunExecution(AgentRunner owner, string prompt, string? parentId, CancellationToken cancellationToken, Action<AgentRun>? onProgress)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Describe the task first.");
            this.owner = owner;
            this.onProgress = onProgress;
            var now = DateTimeOffset.UtcNow;
            Run = new(Guid.NewGuid().ToString("N"), prompt, RunStatus.Queued, now, now, ParentId: parentId);
            Source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Source.CancelAfter(TimeSpan.FromMinutes(10));
            owner.active[Run.Id] = Source;
            try
            {
                Update(RunStatus.Running);
                owner.store.AddMessage(Run.Id, "agent", "user", prompt);
            }
            catch { Dispose(); throw; }
        }
        public void Update(RunStatus status, string result = "")
        {
            Run = Run with
            {
                Status = status,
                Result = result,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            owner.store.SaveRun(Run);
            Notify(owner.RunChanged, Run);
            Notify(onProgress, Run);
        }
        public bool StopAtLimit()
        {
            Source.Token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow - Run.CreatedAt < TimeSpan.FromMinutes(10) && Run.Actions < 30)
                return false;
            Update(RunStatus.Paused, "Reached this run's action/time limit. Review progress and continue explicitly.");
            return true;
        }
        public void RecordDiscovery(ToolResult result)
        {
            Run = Run with
            {
                Actions = Run.Actions + 1
            };
            owner.store.AddEvent(Run.Id, "tool_discovery", result.Message);
            Update(RunStatus.Running, result.Message);
        }
        public void RecordResult(string name, JsonElement arguments, ToolResult result, RiskLevel risk)
        {
            Run = Run with
            {
                Actions = Run.Actions + 1
            };
            LastActionSucceeded = result.Success;
            owner.store.AddEvent(Run.Id, result.Success ? "tool_success" : "tool_failure", name + ": " + result.Message);
            var fingerprint = Fingerprint(name, arguments);
            if (result.Success)
            {
                SuccessfulActions++;
                if (risk is RiskLevel.LocalWrite or RiskLevel.Sensitive)
                    SuccessfulStateChanges++;
                successfulToolEvidence.Add((name, risk));
                failedAttempts.Remove(fingerprint);
            }
            else
            {
                failedAttempts.TryGetValue(fingerprint, out var failures);
                failedAttempts[fingerprint] = failures + 1;
                RetryLimitReached = failures + 1 >= 3;
            }
            Update(RunStatus.Running, name + ": " + result.Message);
        }
        public bool Satisfies(ActionCompletionRequirement requirement) => successfulToolEvidence.Any(evidence => requirement.IsSatisfiedBy(evidence.Name, evidence.Risk));
        public void RecordToolMessage(string name, ToolResult result) => owner.store.AddMessage(Run.Id, "agent", "tool", name + "\n" + ContextBudget.ToolResultExcerpt(result));
        public void RecordCancellation() => Update(DateTimeOffset.UtcNow - Run.CreatedAt >= TimeSpan.FromMinutes(10) ? RunStatus.Paused : RunStatus.Cancelled,
            "Stopped. Completed actions remain recorded; review progress before continuing.");
        public void Dispose()
        {
            owner.active.TryRemove(Run.Id, out _);
            Source.Dispose();
        }
    }

    private static string Fingerprint(string name, JsonElement arguments)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            void Write(JsonElement value)
            {
                if (value.ValueKind == JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(property.Value);
                    }
                    writer.WriteEndObject();
                }
                else if (value.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                        Write(item);
                    writer.WriteEndArray();
                }
                else
                    value.WriteTo(writer);
            }
            Write(arguments);
        }
        return name + ":" + Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }
}
public static class ToolPolicy
{
    private static readonly string[] SensitiveNames = ["send", "publish", "delete", "remove", "payment", "purchase", "buy", "deploy", "execute", "shell", "command", "sql", "update_price", "inventory"];
    public static RiskLevel EffectiveRisk(ToolDefinition tool, string arguments)
    {
        if (tool.Risk == RiskLevel.Sensitive)
            return RiskLevel.Sensitive;
        var name = tool.Name.ToLowerInvariant();
        if (SensitiveNames.Any(name.Contains))
            return RiskLevel.Sensitive;
        // Only these exact native capabilities avoid general input approval. Their executors validate stable IDs.
        if (name.StartsWith("desktop_", StringComparison.Ordinal))
            return tool.Name switch
            {
                "desktop_windows" or "desktop_snapshot" or "desktop_apps" => tool.Risk,
                "desktop_launch" or "desktop_activate" => RiskLevel.LocalWrite,
                _ => RiskLevel.Sensitive
            };
        return tool.Risk;
    }
}
