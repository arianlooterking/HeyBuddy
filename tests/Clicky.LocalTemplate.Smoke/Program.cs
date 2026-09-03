using System.Diagnostics;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;
using Microsoft.Data.Sqlite;

if (!args.Contains("--live")) throw new ArgumentException("Pass --live only after releasing other managed GPU workers.");
var label = args.Contains("--before") ? "before" : "after";
var output = Path.GetFullPath("artifacts/context-template");
Directory.CreateDirectory(output);
var settingsFile = Path.GetFullPath("artifacts/refinement-live/data/settings.json");
var settings = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(settingsFile))!;
settings.Provider = "local";
settings.SpeakReplies = false;
settings.PreloadLocalModel = false;
Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", Path.Combine(output, "isolated-" + Guid.NewGuid().ToString("N")));
var messages = new List<ChatMessage>
{
    new("system", PromptCatalog.Agent),
    new("user", string.Concat(Enumerable.Repeat("Older context that must be omitted. ", 700))),
    new("assistant", "Earlier conversation completed."),
    new("user", "For this diagnostic only, inspect the supplied tool observations and reply exactly OK. Do not request any actions.")
};
var resultSizes = new List<int>();
using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath("artifacts/refinement-live/data/clicky.db"), Mode = SqliteOpenMode.ReadOnly }.ToString()))
{
    connection.Open();
    using var query = connection.CreateCommand();
    query.CommandText = "SELECT text FROM history WHERE session_id=(SELECT id FROM runs WHERE status='Failed' ORDER BY created_at DESC LIMIT 1) AND role='tool' ORDER BY created_at";
    using var reader = query.ExecuteReader();
    while (reader.Read())
    {
        var observation = reader.GetString(0);
        var separator = observation.IndexOf('\n');
        var name = observation[..separator];
        var content = observation[(separator + 1)..];
        var id = "recorded-" + resultSizes.Count;
        messages.Add(new("assistant", "", ToolCalls: [new(id, name, name == "desktop_snapshot" ? "{\"windowId\":\"recorded-window\"}" : "{}")]));
        messages.Add(new("tool", content, ToolCallId: id));
        resultSizes.Add(content.Length);
    }
}
if (resultSizes.Count < 2) throw new InvalidDataException("Two saved local tool observations are required for this reproduction.");
var tools = new[]
{
    new ToolDefinition("desktop_windows", "List Windows windows", JsonSchema.Parse("{\"type\":\"object\",\"properties\":{}}"), RiskLevel.ReadOnly),
    new ToolDefinition("desktop_snapshot", "Inspect a listed window", JsonSchema.Parse("{\"type\":\"object\",\"properties\":{\"windowId\":{\"type\":\"string\"}},\"required\":[\"windowId\"]}"), RiskLevel.ReadOnly)
};
var request = ContextBudget.Fit(new(messages, tools, 64), settings.ContextSize);
var roleSequence = request.Messages.Select(m => m.Role).ToArray();
Console.WriteLine("Request roles: " + string.Join(", ", roleSequence));
await using var factory = new ModelProviderFactory(settings, new NoCredentials());
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var clock = Stopwatch.StartNew();
var success = false;
string? diagnostic = null;
var replyCharacters = 0;
string[] returnedTools = [];
try
{
    var reply = await factory.Create().CompleteAsync(request, null, timeout.Token);
    success = true;
    replyCharacters = reply.Text.Length;
    returnedTools = reply.ToolCalls.Select(c => c.Name).ToArray();
}
catch (Exception error) { diagnostic = error.Message; }
await factory.ModelManager.StopAsync();
await File.WriteAllTextAsync(Path.Combine(output, label + ".json"), JsonSerializer.Serialize(new
{
    Timestamp = DateTimeOffset.UtcNow,
    Label = label,
    Success = success,
    Diagnostic = diagnostic,
    ElapsedMilliseconds = clock.Elapsed.TotalMilliseconds,
    Roles = roleSequence,
    EstimatedTokens = ContextBudget.EstimateRequest(request),
    RecordedToolResultCharacters = resultSizes,
    ReplyCharacters = replyCharacters,
    ReturnedToolNames = returnedTools,
    ActualToolExecutions = 0,
    RawRequestsWritten = false,
    WorkerStopped = !factory.ModelManager.GetStatus().Running
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(success ? "Local request succeeded; returned tool calls were not executed." : diagnostic);
if (label == "before") return !success && diagnostic?.Contains("System message must be at the beginning.", StringComparison.Ordinal) == true ? 0 : 1;
return success ? 0 : 1;

sealed class NoCredentials : ICredentialStore
{
    public string? Get(string name) => null;
    public void Set(string name, string value) => throw new InvalidOperationException();
    public void Delete(string name) => throw new InvalidOperationException();
}
