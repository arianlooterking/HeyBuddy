using System.Collections.Concurrent;
using System.IO;
using Expression = System.Linq.Expressions.Expression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using Clicky.Core;
using Clicky.Windows;
using Clicky.Windows.Native;

internal static class Program
{
    private static int exitCode;
    private static readonly List<string> checks = [];
    private static readonly List<string> errors = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "artifacts/routing-ui");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", Path.Combine(output, "bootstrap-" + Guid.NewGuid().ToString("N")));
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = LoadAppStyles() };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            await RunCaseAsync("auto-direct", AutoDirectAsync);
            await RunCaseAsync("agent-background", AgentBackgroundAsync);
            await RunCaseAsync("chat-only", ChatOnlyAsync);
            await RunCaseAsync("auto-question", AutoQuestionAsync);
            await RunCaseAsync("auto-action-needs-execution", AutoActionNeedsExecutionAsync);
            await RunCaseAsync("auto-mutation-needs-state-change", AutoMutationNeedsStateChangeAsync);
            await RunCaseAsync("enter-routing", EnterRoutingAsync);
            await RunCaseAsync("new-conversation-cancellation", NewConversationCancellationAsync);
            await RunCaseAsync("direct-cancellation-privacy", DirectCancellationPrivacyAsync);
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                Passed = errors.Count == 0,
                Timestamp = DateTimeOffset.UtcNow,
                Checks = checks,
                Errors = errors,
                WindowsShown = false,
                RealAppLaunches = 0,
                MicrophoneCapture = false,
                ActualInference = false,
                KeyboardScope = "Routed PreviewKeyDown Enter event; no global input or physical Shift+Enter simulation",
                Provider = "Isolated scripted loopback SSE server",
                AppDiscovery = "Synthetic nonexistent application registration; direct run cancelled before dispatch"
            }, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var check in checks)
                Console.WriteLine("PASS: " + check);
            foreach (var error in errors)
                Console.WriteLine("FAIL: " + error);
            exitCode = errors.Count == 0 ? 0 : 1;
            application.Shutdown(exitCode);
            Dispatcher.ExitAllFrames();
        }));
        Dispatcher.Run();
        return exitCode;

        async Task RunCaseAsync(string name, Func<Fixture, Task> test)
        {
            var directory = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using var fixture = new Fixture(directory);
                await test(fixture);
                Require(!Application.Current.Windows.OfType<Window>().Any(window => window.IsVisible) && !fixture.Services.Speech.IsRecording && !fixture.Services.Factory.ModelManager.GetStatus().Running,
                    name + ": no visible window, microphone capture, or model worker");
            }
            catch (Exception error) { errors.Add(name + ": " + error); }
        }
    }

    private static async Task AutoDirectAsync(Fixture fixture)
    {
        Require(fixture.Mode.SelectedIndex == 0 && ((ComboBoxItem)fixture.Mode.SelectedItem).Content?.ToString() == "Auto", "Default mode is Auto");
        Action<AgentRun> stopBeforeDispatch = run => { if (run.Status == RunStatus.Running) fixture.Services.Agents.Cancel(run.Id); };
        fixture.Services.Agents.RunChanged += stopBeforeDispatch;
        try
        {
            await fixture.SendAsync("open HeyBuddy Routing Fixture");
        }
        finally { fixture.Services.Agents.RunChanged -= stopBeforeDispatch; }
        var run = fixture.Services.Store.GetRuns().Single();
        Require(run.Status == RunStatus.Cancelled && run.Actions == 0, "Exact Auto app command reaches the persisted common runner and honours cancellation before dispatch");
        Require(fixture.Server.Requests.Count == 0 && fixture.Services.Factory.CachedClientCount == 0, "Exact Auto app command does not create or call a model provider");
        Require(Field<string>(fixture.Window, "currentPage") == "chat", "Exact Auto app command stays in foreground conversation");
    }

    private static async Task AgentBackgroundAsync(Fixture fixture)
    {
        fixture.Mode.SelectedIndex = 1;
        var response = fixture.Server.Queue("I have a plan", hold: true);
        await fixture.SendAsync("open HeyBuddy Routing Fixture");
        var payload = await response.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(Field<string>(fixture.Window, "currentPage") == "tasks", "Agent exact app command stays in background Tasks mode");
        Require(fixture.Services.Store.GetRuns().Single().Status == RunStatus.Running && !Field<bool>(fixture.Window, "busy"), "Agent returns foreground control while its provider is pending");
        Require(payload.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0, "Agent requests registered tool schemas");
        response.Release();
        await UntilAsync(() => fixture.Services.Store.GetRuns().Single().Status == RunStatus.Paused);
        Require(fixture.Services.Store.GetRuns().Single().Actions == 0, "Agent prose without actual action pauses instead of reporting completion");
        Require(!fixture.Services.Store.GetHistory().Any(h => h.Kind == "chat"), "Background Agent task does not insert task prose into foreground chat history");
    }

    private static async Task ChatOnlyAsync(Fixture fixture)
    {
        fixture.Mode.SelectedIndex = 3;
        var response = fixture.Server.Queue("I can explain this task.", toolName: "desktop_launch");
        await fixture.SendAsync("open HeyBuddy Routing Fixture");
        var payload = await response.Received.Task;
        Require(!payload.TryGetProperty("tools", out _), "Chat only sends no tool schemas, including for exact open requests");
        Require(fixture.Services.Store.GetRuns().Count == 0, "Chat only does not create or dispatch a task even if a provider advertises a tool call");
        Require(fixture.Services.Store.GetHistory().Any(h => h.Kind == "chat" && h.Role == "assistant" && h.Text == "I can explain this task."), "Chat only persists its ordinary textual answer");
    }

    private static async Task AutoQuestionAsync(Fixture fixture)
    {
        var response = fixture.Server.Queue("Four.");
        await fixture.SendAsync("What is two plus two?");
        var payload = await response.Received.Task;
        Require(payload.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0, "Auto ordinary question has tool capability available");
        var run = fixture.Services.Store.GetRuns().Single();
        Require(run.Status == RunStatus.Completed && run.Actions == 0 && run.Result == "Four.", "Auto ordinary no-tool answer completes normally");
        Require(fixture.Services.Store.GetHistory().Any(h => h.Kind == "chat" && h.Role == "assistant" && h.Text == "Four."), "Auto final answer appears in conversation history");
    }

    private static async Task AutoActionNeedsExecutionAsync(Fixture fixture)
    {
        const string prompt = "Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not open an app, click, press Enter, save, close, or touch any other document.";
        var response = fixture.Server.Queue("Done.");
        await fixture.SendAsync(prompt);
        var payload = await response.Received.Task;
        Require(payload.TryGetProperty("tools", out var tools) && tools.GetArrayLength() > 0, "Auto action request receives registered tool schemas");
        var run = fixture.Services.Store.GetRuns().Single();
        Require(run.Status == RunStatus.Paused && run.Actions == 0,
            "Auto action request cannot report completion from zero-action prose");
        Require(run.Result.Contains("desktop_type", StringComparison.Ordinal),
            "Auto action request names the missing typing evidence");
    }

    private static async Task AutoMutationNeedsStateChangeAsync(Fixture fixture)
    {
        const string prompt = "Find the Notepad window for heybuddy-typing-check.txt, inspect its editable document, and append this exact text: Hello from HeyBuddy. Use the desktop tools and verify the result. Do not open an app, click, press Enter, save, close, or touch any other document.";
        var inspection = fixture.Server.Queue("I found the application.", toolName: "desktop_apps");
        var prose = fixture.Server.Queue("Done.");
        await fixture.SendAsync(prompt);
        var payload = await inspection.Received.Task;
        await prose.Received.Task;
        var system = payload.GetProperty("messages").EnumerateArray()
            .Single(message => message.GetProperty("role").GetString() == "system")
            .GetProperty("content").GetString() ?? "";
        Require(system.Contains("desktop_type", StringComparison.Ordinal),
            "Auto routes the original typing request with a concrete desktop_type completion requirement");
        var run = fixture.Services.Store.GetRuns().Single();
        Require(run.Status == RunStatus.Paused && run.Actions == 1,
            "Auto mutation request cannot complete after read-only inspection");
        Require(run.Result.Contains("desktop_type", StringComparison.Ordinal),
            "Auto mutation request reports the still-missing typing evidence");
    }

    private static async Task NewConversationCancellationAsync(Fixture fixture)
    {
        var originalSession = Field<string>(fixture.Window, "sessionId");
        var document = Path.Combine(fixture.Directory, "private-fixture.txt");
        await File.WriteAllTextAsync(document, "Synthetic private document context for cancellation regression.");
        Field<List<string>>(fixture.Window, "attachments").Add(document);
        var response = fixture.Server.Queue("Late answer must not enter the new conversation.", hold: true);
        var sending = fixture.SendAsync("Read my attached note.");
        await response.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(fixture.Services.Settings.FileContextSessions.Contains(originalSession), "Original attachment conversation gets a persisted privacy marker before inference");
        fixture.NewConversation();
        var newSession = Field<string>(fixture.Window, "sessionId");
        fixture.Composer.Text = "Fresh unsent message";
        fixture.Status.Text = "Fresh conversation status";
        response.Release();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await FlushAsync();
        Require(newSession != originalSession && fixture.Services.Store.GetHistory(session: newSession).Count == 0 && Field<List<ChatMessage>>(fixture.Window, "conversation").Count == 0,
            "Cancelled old response cannot add content or history to a new conversation");
        Require(fixture.Composer.Text == "Fresh unsent message", "Cancelled response preserves the new unsent message");
        var saved = AppSettings.Load();
        Require(saved.FileContextSessions.Contains(originalSession) && !saved.FileContextSessions.Contains(newSession) && !Field<bool>(fixture.Window, "conversationContainsFiles"),
            "Cancellation preserves the old privacy marker without marking the fresh session");
        Require(fixture.Status.Text == "Fresh conversation status", "Stale cancellation callbacks do not overwrite the new conversation's status");
    }

    private static async Task EnterRoutingAsync(Fixture fixture)
    {
        fixture.Mode.SelectedIndex = 3;
        fixture.Composer.Text = "Question submitted through Enter";
        var response = fixture.Server.Queue("Keyboard reply.");
        var source = new FixturePresentationSource { RootVisual = fixture.Composer };
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            throw new InvalidOperationException("The routing fixture requires Shift to be released; it does not alter global keyboard state.");
        fixture.Composer.RaiseEvent(key);
        Require(key.Handled, "Composer PreviewKeyDown handles Enter before the multiline TextBox consumes it");
        await response.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await UntilAsync(() => !Field<bool>(fixture.Window, "busy"));
        Require(fixture.Server.Requests.Count == 1 && fixture.Composer.Text.Length == 0,
            "Routed Enter submits the composer exactly once and clears the sent text");
        Require(fixture.Services.Store.GetHistory().Any(h => h.Kind == "chat" && h.Role == "assistant" && h.Text == "Keyboard reply."),
            "Routed Enter completes the actual conversation response path");
    }

    private static async Task DirectCancellationPrivacyAsync(Fixture fixture)
    {
        var originalSession = Field<string>(fixture.Window, "sessionId");
        string? newSession = null;
        Action<AgentRun> resetBeforeDispatch = run =>
        {
            if (run.Status != RunStatus.Running || newSession is not null)
                return;
            fixture.NewConversation();
            newSession = Field<string>(fixture.Window, "sessionId");
        };
        fixture.Services.Agents.RunChanged += resetBeforeDispatch;
        try
        {
            await fixture.SendAsync("open HeyBuddy Routing Fixture");
        }
        finally { fixture.Services.Agents.RunChanged -= resetBeforeDispatch; }
        await FlushAsync();
        Require(newSession is not null && newSession != originalSession, "Direct command cancellation changes the conversation during the runner callback");
        Require(fixture.Services.Store.GetRuns().Single().Actions == 0 && fixture.Server.Requests.Count == 0, "Direct cancellation performs no native action and no inference");
        Require(fixture.Services.Store.GetHistory(session: newSession).Count == 0 && Field<List<ChatMessage>>(fixture.Window, "conversation").Count == 0,
            "Cancelled direct command stores no reply in the fresh conversation");
        Require(!fixture.Services.Settings.FileContextSessions.Contains(newSession!) && !Field<bool>(fixture.Window, "conversationContainsFiles"),
            "Cancelled direct command cannot set a privacy marker on the fresh conversation");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Directory
        {
            get;
        }
        public AppServices Services
        {
            get;
        }
        public MainWindow Window
        {
            get;
        }
        public ScriptedServer Server { get; } = new();
        public ComboBox Mode => (ComboBox)Window.FindName("ModeSelector");
        public TextBox Composer => (TextBox)Window.FindName("Composer");
        public TextBlock Status => (TextBlock)Window.FindName("StatusText");
        public Fixture(string directory)
        {
            Directory = directory;
            System.IO.Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", directory);
            Services = new();
            Services.Settings.Provider = "compatible";
            Services.Settings.Endpoint = Server.Endpoint;
            Services.Settings.Model = "scripted-test-provider";
            Services.Settings.ModelDirectory = Path.Combine(directory, "Models");
            Services.Settings.RuntimeDirectory = Path.Combine(directory, "Runtime");
            Services.Settings.WorkDirectory = Path.Combine(directory, "Workspace");
            Services.Settings.CompanionEnabled = false;
            Services.Settings.PreloadLocalModel = false;
            Services.Settings.SpeakReplies = false;
            Services.Settings.CaptureScreen = false;
            Services.Settings.OnboardingCompleted = true;
            Services.Settings.Save();
            SetSyntheticCatalog(Services.Desktop, directory);
            Window = new(Services);
            Field<DispatcherTimer>(Window, "foregroundTimer").Stop();
            // Never call Show: Loaded installs hooks and starts the companion/preload workflow.
        }
        public Task SendAsync(string text)
        {
            Composer.Text = text;
            return (Task)Invoke(Window, "SendAsync")!;
        }
        public void NewConversation() => Invoke(Window, "NewConversation", Window, new RoutedEventArgs());
        public async ValueTask DisposeAsync()
        {
            Window.PrepareExit();
            await Server.DisposeAsync();
            await Services.DisposeAsync();
            await FlushAsync();
        }
    }

    private static ResourceDictionary LoadAppStyles()
    {
        // Use the actual shipped style resources with a plain WPF Application. Constructing the
        // production App would schedule its real tray/window startup when the dispatcher begins.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Routing.AppStyles.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = document.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml), resources.Nodes());
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private sealed class FixturePresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }

    private static void SetSyntheticCatalog(WindowsDesktopTools desktop, string directory)
    {
        // The existing internal discovery seam is invoked through reflection from this external UI test assembly.
        // The target does not exist, so even a failed cancellation assertion cannot launch a real application.
        var catalog = Field<DesktopAppCatalog>(desktop, "applications");
        var field = typeof(DesktopAppCatalog).GetField("discover", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var returnType = field.FieldType.GenericTypeArguments[2];
        var registrationType = returnType.GenericTypeArguments[0];
        var app = new DesktopApp("app-routing-fixture", "HeyBuddy Routing Fixture", "Synthetic test registration", "desktop", Path.Combine(directory, "nonexistent-routing-fixture.exe"), null);
        var registration = Activator.CreateInstance(registrationType, app, "fixture-fingerprint")!;
        var values = Array.CreateInstance(registrationType, 1);
        values.SetValue(registration, 0);
        var callback = Expression.Lambda(field.FieldType, Expression.Convert(Expression.Constant(values), returnType),
            Expression.Parameter(typeof(string), "query"), Expression.Parameter(typeof(CancellationToken), "cancellationToken")).Compile();
        field.SetValue(catalog, callback);
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Task FlushAsync() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
            await Task.Delay(20, timeout.Token);
        await FlushAsync();
    }
    private static void Require(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);
        checks.Add(message);
    }

    private sealed class ResponsePlan(string text, string? toolName, bool hold)
    {
        public string Text { get; } = text;
        public string? ToolName { get; } = toolName;
        public TaskCompletionSource<JsonElement> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Ready => hold ? release.Task : Task.CompletedTask;
        public void Release() => release.TrySetResult();
    }

    private sealed class ScriptedServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentQueue<ResponsePlan> plans = new();
        private readonly ConcurrentBag<Task> clients = [];
        private readonly Task acceptLoop;
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public string Endpoint
        {
            get;
        }
        public ScriptedServer()
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/";
            acceptLoop = AcceptAsync();
        }
        public ResponsePlan Queue(string text, bool hold = false, string? toolName = null)
        {
            var plan = new ResponsePlan(text, toolName, hold);
            plans.Enqueue(plan);
            return plan;
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                    clients.Add(RespondAsync(await listener.AcceptTcpClientAsync(lifetime.Token)));
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
        }
        private async Task RespondAsync(TcpClient client)
        {
            using (client)
                try
                {
                    var stream = client.GetStream();
                    var header = new List<byte>();
                    var one = new byte[1];
                    while (header.Count < 65536)
                    {
                        if (await stream.ReadAsync(one, lifetime.Token) != 1)
                            return;
                        header.Add(one[0]);
                        if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10)
                            break;
                    }
                    var headers = Encoding.ASCII.GetString(header.ToArray());
                    var length = int.Parse(headers.Split("\r\n").Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim());
                    if (length > 1_000_000)
                        throw new InvalidDataException("Fixture request is unexpectedly large.");
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, lifetime.Token);
                    var payload = JsonDocument.Parse(body).RootElement.Clone();
                    Requests.Enqueue(payload);
                    if (!plans.TryDequeue(out var plan))
                        throw new InvalidOperationException("Unexpected provider request.");
                    plan.Received.TrySetResult(payload);
                    await plan.Ready.WaitAsync(lifetime.Token);
                    var delta = new Dictionary<string, object> { ["content"] = plan.Text };
                    if (plan.ToolName is not null)
                        delta["tool_calls"] = new[] { new { index = 0, id = "fixture-call", type = "function", function = new { name = plan.ToolName, arguments = "{\"appId\":\"app-routing-fixture\"}" } } };
                    var data = "data: " + JsonSerializer.Serialize(new
                    {
                        choices = new[] { new { delta } }
                    }) + "\n\ndata: [DONE]\n\n";
                    var encoded = Encoding.UTF8.GetBytes(data);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {encoded.Length}\r\nConnection: close\r\n\r\n"), lifetime.Token);
                    await stream.WriteAsync(encoded, lifetime.Token);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            listener.Stop();
            await acceptLoop;
            await Task.WhenAll(clients);
            lifetime.Dispose();
        }
    }
}
