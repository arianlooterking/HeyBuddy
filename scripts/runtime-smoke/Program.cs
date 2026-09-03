using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;

var latencyArg = Array.IndexOf(args, "--latency-smoke");
if (latencyArg >= 0)
{
    await LatencySmoke.RunAsync(args.Length > latencyArg + 1 ? args[latencyArg + 1] : "run");
    return;
}
if (args.Contains("--recovery-smoke"))
{
    await RecoverySmoke.RunAsync();
    return;
}
var benchmarkArg = Array.IndexOf(args, "--benchmark-vision");
if (benchmarkArg >= 0)
{
    if (args.Length <= benchmarkArg + 1) throw new ArgumentException("Pass a saved PNG screenshot after --benchmark-vision.");
    await VisionBenchmark.RunAsync(args[benchmarkArg + 1]);
    return;
}
var settings = new AppSettings { Provider = "local", GpuLayers = 24, ContextSize = 8192, CpuThreads = 6 };
if (args.Contains("--documents"))
{
    var folder = Path.Combine(Environment.CurrentDirectory, "scripts", "runtime-smoke", "output"); Directory.CreateDirectory(folder);
    var prefix = "report-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
    foreach (var extension in new[] { ".docx", ".pdf" })
    {
        var path = Path.Combine(folder, prefix + extension);
        await DocumentWriter.GenerateAsync(path, "گزارش آزمایش محلی", "سلام، این یک گزارش محلی است.\nاین متن فارسی باید از راست به چپ و با حروف پیوسته نمایش داده شود.\nEnglish and Turkish: Merhaba dünya.\nعدد نمونه: ۱۲۳۴۵");
        Console.WriteLine(path);
        if (extension == ".pdf") { using var document = UglyToad.PdfPig.PdfDocument.Open(path); Console.WriteLine("PDF raw: " + document.GetPage(1).Text); }
    }
    return;
}
await using var factory = new ModelProviderFactory(settings, new EmptyCredentials());
using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15));
if (args.Contains("--install")) await factory.ModelManager.InstallAsync(new Progress<DownloadProgress>(p => { if (p.Stage != "Downloading") Console.WriteLine($"{p.Stage}: {p.FileName}"); }), lifetime.Token);
Console.WriteLine(JsonSerializer.Serialize(factory.ModelManager.GetStatus()));
if (!args.Contains("--live")) return;
var watch = Stopwatch.StartNew();
var endpoint = await factory.ModelManager.StartAsync(lifetime.Token);
Console.WriteLine($"Worker startup ms: {watch.ElapsedMilliseconds}");
using (var unauthenticated = new HttpClient())
{
    using var authCheck = await unauthenticated.PostAsync(new Uri(endpoint, "chat/completions"), new StringContent("{\"model\":\"qwen3.5-4b\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}", System.Text.Encoding.UTF8, "application/json"), lifetime.Token);
    Console.WriteLine($"Unauthenticated inference HTTP: {(int)authCheck.StatusCode}");
    if (authCheck.StatusCode != System.Net.HttpStatusCode.Unauthorized) throw new Exception("Managed worker did not enforce authentication.");
}
var provider = factory.Create();
watch.Restart();
var firstToken = -1L;
var reply = await provider.CompleteAsync(new([new("system", "You are a concise desktop assistant. Follow the user's instruction. Reply in the user's language."), new("user", "Say exactly: Local inference is working.")], MaxTokens: 96), t => { if (firstToken < 0) firstToken = watch.ElapsedMilliseconds; }, lifetime.Token);
Console.WriteLine(JsonSerializer.Serialize(new { test = "text", elapsedMs = watch.ElapsedMilliseconds, firstTokenMs = firstToken, text = reply.Text, calls = reply.ToolCalls.Count }));
watch.Restart(); firstToken = -1;
reply = await provider.CompleteAsync(new([new("user", "به فارسی فقط یک جمله کوتاه بگو که آماده کمک هستی.")], MaxTokens: 96), t => { if (firstToken < 0) firstToken = watch.ElapsedMilliseconds; }, lifetime.Token);
Console.WriteLine(JsonSerializer.Serialize(new { test = "persian", elapsedMs = watch.ElapsedMilliseconds, firstTokenMs = firstToken, text = reply.Text }));
watch.Restart();
reply = await provider.CompleteAsync(new([new("system", "Use the available tool when the user requests workspace files. Do not invent a result."), new("user", "List the files in my workspace using files.list.")], [new("files.list", "List files in the workspace", JsonSchema.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""), RiskLevel.ReadOnly)], 192), null, lifetime.Token);
Console.WriteLine(JsonSerializer.Serialize(new { test = "tool_call", elapsedMs = watch.ElapsedMilliseconds, text = reply.Text, calls = reply.ToolCalls }));
if (reply.ToolCalls.Count == 0) throw new Exception("Local tool-call smoke test did not produce a structured tool call.");
var toolCall = reply.ToolCalls[0];
var workspace = Path.Combine(Environment.CurrentDirectory, "scripts", "runtime-smoke", "output", "workspace"); Directory.CreateDirectory(workspace);
await File.WriteAllTextAsync(Path.Combine(workspace, "validation.txt"), "Local tool workflow validation.");
using (var documentTools = new DocumentTools(new() { WorkDirectory = workspace }))
{
    var toolResult = await documentTools.ExecuteAsync(toolCall.Name, JsonSchema.Parse(toolCall.Arguments), lifetime.Token);
    if (!toolResult.Success) throw new Exception("Real local tool failed: " + toolResult.Message);
    watch.Restart();
    reply = await provider.CompleteAsync(new([new("user", "List my workspace files."), new("assistant", "", ToolCalls: [toolCall]), new("tool", toolResult.ToJson(), ToolCallId: toolCall.Id)], MaxTokens: 192), null, lifetime.Token);
    Console.WriteLine(JsonSerializer.Serialize(new { test = "actual_tool_result_followup", elapsedMs = watch.ElapsedMilliseconds, text = reply.Text }));
    if (!reply.Text.Contains("validation.txt", StringComparison.OrdinalIgnoreCase)) throw new Exception("Model did not correctly read the actual tool result.");
}
watch.Restart();
reply = await provider.CompleteAsync(new([new("user", "Türkçe tek kısa cümleyle yardıma hazır olduğunu söyle.")], MaxTokens: 96), null, lifetime.Token);
Console.WriteLine(JsonSerializer.Serialize(new { test = "turkish", elapsedMs = watch.ElapsedMilliseconds, text = reply.Text }));
var visionArg = Array.IndexOf(args, "--vision");
if (visionArg >= 0 && args.Length > visionArg + 1)
{
    var image = new ImageAttachment(Convert.ToBase64String(await File.ReadAllBytesAsync(args[visionArg + 1])), "image/png", "Synthetic test image");
    watch.Restart(); firstToken = -1;
    var screenGuidance = args.Contains("--screen-guidance");
    var visionMessages = screenGuidance
        ? new ChatMessage[]
        {
            new("system", PromptCatalog.Conversation),
            new("user", "Where is the Increment counter button? Point to it without clicking.\n\n<focused_window_context untrusted=\"true\">{\"elements\":[{\"name\":\"Increment counter\",\"type\":\"Button\",\"x\":0.19,\"y\":0.45}]}</focused_window_context>", [image])
        }
        : [new("user", "Describe the visible application and its main controls. Be brief.", [image])];
    reply = await provider.CompleteAsync(new(visionMessages, MaxTokens: 256), t => { if (firstToken < 0) firstToken = watch.ElapsedMilliseconds; }, lifetime.Token);
    var parsedGuidance = GuidanceParser.Parse(reply.Text);
    Console.WriteLine(JsonSerializer.Serialize(new { test = screenGuidance ? "screen_guidance" : "vision", elapsedMs = watch.ElapsedMilliseconds, firstTokenMs = firstToken, text = parsedGuidance.Text, guidance = parsedGuidance.Commands }));
    if (screenGuidance && (parsedGuidance.Commands.Count == 0 || parsedGuidance.Commands.Any(command => command.X is < 0 or > 1 || command.Y is < 0 or > 1)))
        throw new Exception("Local screen-guidance smoke test did not produce a valid visual pointer.");
}
await factory.ModelManager.StopAsync();
Console.WriteLine("Worker stopped successfully.");

sealed class EmptyCredentials : ICredentialStore
{
    public string? Get(string name) => null;
    public void Set(string name, string value) => throw new NotSupportedException();
    public void Delete(string name) => throw new NotSupportedException();
}
