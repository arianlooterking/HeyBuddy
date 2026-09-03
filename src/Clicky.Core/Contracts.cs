using System.Text.Json;

namespace Clicky.Core;

public enum RiskLevel
{
    ReadOnly, LocalWrite, Sensitive
}
public enum RunStatus
{
    Queued, Running, AwaitingApproval, Completed, Failed, Cancelled, Paused
}
public sealed record ImageAttachment(string Base64, string MimeType = "image/png", string Name = "Screen");
public sealed record ToolCall(string Id, string Name, string Arguments);
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<ImageAttachment>? Images = null,
    IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null);
public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema, RiskLevel Risk = RiskLevel.Sensitive);
public sealed record ModelRequest(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition>? Tools = null, int MaxTokens = 2048);
public sealed record ModelReply(string Text, IReadOnlyList<ToolCall> ToolCalls, string? Model = null, string? AudioBase64 = null, int AudioSampleRate = 24000);
public sealed record ToolResult(bool Success, string Message, object? Data = null)
{
    public string ToJson() => JsonSerializer.Serialize(this);
}
public sealed record ApprovalRequest(string RunId, string ToolName, string Description, string Arguments, RiskLevel Risk);
public sealed record GuidanceCommand(string Kind, double X, double Y, double X2 = 0, double Y2 = 0,
    string Label = "", string MonitorId = "", int Step = 0);
public sealed record ScreenCapture(string Base64, int Width, int Height, int Left, int Top, string MonitorId)
{
    public ImageAttachment ToAttachment() => new(Base64, "image/png", MonitorId);
}
public sealed record AgentRun(string Id, string Prompt, RunStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, int Actions = 0, string Result = "", string? ParentId = null);
public sealed record HistoryEntry(string Id, string SessionId, string Kind, string Role, string Text, DateTimeOffset CreatedAt);
public sealed record SkillDocument(string Name, string Content, bool Enabled, string Path);

public interface IModelProvider
{
    string Name
    {
        get;
    }
    bool IsCloud
    {
        get;
    }
    Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken);
}
public interface IToolExecutor
{
    IReadOnlyList<ToolDefinition> Tools
    {
        get;
    }
    Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
}
public interface ICredentialStore
{
    string? Get(string name);
    void Set(string name, string value);
    void Delete(string name);
}
public static class JsonSchema
{
    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
