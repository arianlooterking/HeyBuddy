using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Clicky.Windows.Native;

var includeCalculator = false;
var outputDirectory = Path.GetFullPath("artifacts/hosted-window-smoke");
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--calculator")
        includeCalculator = true;
    else if (args[index] == "--output" && index + 1 < args.Length)
        outputDirectory = Path.GetFullPath(args[++index]);
    else
    {
        Console.Error.WriteLine("Usage: HostedWindowSmoke [--calculator] [--output <directory>]");
        return 2;
    }
}
Directory.CreateDirectory(outputDirectory);
var results = new List<Check>();
var evidence = new List<object>();
var watch = Stopwatch.StartNew();
const string calculatorAppId = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
var calculator = new DesktopApp("observed-calculator", "Calculator", "Native check", "packaged", null, calculatorAppId);
var success = false;
string? failure = null;

void Verify(string name, bool pass)
{
    results.Add(new(name, pass ? "passed" : "failed"));
    Console.WriteLine((pass ? "PASS " : "FAIL ") + name);
    if (!pass)
        throw new InvalidOperationException(name);
}
void Skip(string name, string reason)
{
    results.Add(new(name, "skipped", reason));
    Console.WriteLine("SKIP " + name + ": " + reason);
}
void Reject(string name, Action action)
{
    var rejected = false;
    try
    {
        action();
    }
    catch (InvalidOperationException) { rejected = true; }
    Verify(name, rejected);
}
void VerifyStaleCache(WindowsDesktopTools tools, DesktopWindow window, NativeMethods.WindowIdentity identity)
{
    // Change only the test executor's in-memory cache. Calling its resolver performs no input.
    var field = typeof(WindowsDesktopTools).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Update the harness for the window-cache implementation.");
    var cached = (Dictionary<string, NativeMethods.WindowIdentity>)field.GetValue(tools)!;
    var original = cached[window.Id];
    var resolve = typeof(WindowsDesktopTools).GetMethod("ResolveWindow", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Update the harness for the window resolver.");
    try
    {
        cached[window.Id] = identity with
        {
            ContentStarted = identity.ContentStarted + 1
        };
        var staleRejected = false;
        try
        {
            resolve.Invoke(tools, [window.Id, true]);
        }
        catch (TargetInvocationException error) when (error.InnerException is InvalidOperationException) { staleRejected = true; }
        Verify("A cached content-lifetime swap is rejected before activation or input", staleRejected);
    }
    finally { cached[window.Id] = original; }
}
void RecordIdentity(string kind, NativeMethods.WindowIdentity identity)
{
    // Window titles, accessibility text, paths and owner document contents are not written.
    evidence.Add(new
    {
        kind,
        handle = (long)identity.Handle,
        identity.HostProcessId,
        identity.HostStarted,
        contentHandle = (long)identity.ContentHandle,
        identity.ContentProcessId,
        identity.ContentStarted,
        identity.Application,
        identity.AppUserModelId
    });
}

try
{
    Reject("Missing window is rejected", () => NativeMethods.ReadWindowIdentity(0, true));
    var absent = new NativeMethods.WindowIdentity(0, 0, 0, 0, 0, 0, "Absent", null);
    Verify("Missing host cannot verify a packaged app", !DesktopAppLauncher.Matches(calculator, absent));
    var tools = new WindowsDesktopTools();
    var windows = tools.ListWindows();
    DesktopWindow? ordinary = null;
    NativeMethods.WindowIdentity? ordinaryIdentity = null;
    DesktopApp? ordinaryApp = null;
    foreach (var candidate in windows.Where(window => window.ContentHandle == window.Handle && window.HostProcessId == window.ProcessId && window.AppUserModelId is null))
    {
        try
        {
            var candidateIdentity = NativeMethods.ReadWindowIdentity((nint)candidate.Handle, true);
            using var candidateProcess = Process.GetProcessById(candidateIdentity.ContentProcessId);
            var executable = candidateProcess.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executable))
                continue;
            var candidateApp = new DesktopApp("observed-ordinary", candidateIdentity.Application, "Native check", "desktop", executable, null);
            if (!DesktopAppLauncher.Matches(candidateApp, candidateIdentity))
                continue;
            ordinary = candidate;
            ordinaryIdentity = candidateIdentity;
            ordinaryApp = candidateApp;
            break;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { }
    }
    if (ordinary is null)
        Skip("Ordinary application and cached-target checks", "No accessible ordinary app window is open; no application was started.");
    else
    {
        var identity = ordinaryIdentity!;
        RecordIdentity("ordinary", identity);
        Verify("Ordinary app keeps the same host/content identity", !identity.IsHosted && identity.HostProcessId == identity.ContentProcessId && identity.HostStarted == identity.ContentStarted);
        NativeMethods.RequireWindowIdentity(identity, true);
        Verify("Unchanged ordinary window revalidates", true);
        Verify("Ordinary app matches its exact observed executable", DesktopAppLauncher.Matches(ordinaryApp!, identity));
        Verify("A different package cannot match an ordinary host", !DesktopAppLauncher.Matches(calculator, identity));
        Reject("Reused ordinary host lifetime is rejected", () => NativeMethods.RequireWindowIdentity(identity with { HostStarted = identity.HostStarted + 1 }, true));
        VerifyStaleCache(tools, ordinary, identity);
    }

    if (!includeCalculator)
        Skip("Actual Calculator hosted-window checks", "Optional; use --calculator after opening Calculator yourself.");
    else
    {
        // Find the app through raw Windows ownership/AUMID queries, independently of ListWindows
        // and ReadWindowIdentity. Otherwise a broken mapper could hide an open app as a skip.
        var handle = FindCalculatorWindow(calculatorAppId);
        if (handle == 0)
            Skip("Actual Calculator hosted-window checks", "Calculator has no visible, identifiable window. Open it manually and rerun --calculator; this harness never launches it.");
        else
        {
            var identity = NativeMethods.ReadWindowIdentity(handle, true);
            RecordIdentity("calculator", identity);
            Verify("Calculator exposes its exact package application identity", identity.AppUserModelId == calculatorAppId);
            if (!identity.IsHosted)
                Skip("Calculator frame-host-specific checks", "This Calculator version owns its top-level window directly; it does not use ApplicationFrameHost.");
            else
            {
                Verify("Hosted Calculator has a separate content process", identity.HostProcessId != identity.ContentProcessId);
                Verify("The frame host process alone cannot match Calculator", !DesktopAppLauncher.Matches(calculator, identity.HostProcessId));
                Verify("Content is a real descendant of the frame", NativeMethods.IsChild(handle, identity.ContentHandle) && NativeMethods.GetAncestor(identity.ContentHandle, 2) == handle);
                NativeMethods.RequireWindowIdentity(identity, true);
                Verify("Unchanged host/content identity revalidates", true);
                Reject("Changed host start time is rejected", () => NativeMethods.RequireWindowIdentity(identity with { HostStarted = identity.HostStarted + 1 }, true));
                Reject("Changed content start time is rejected", () => NativeMethods.RequireWindowIdentity(identity with { ContentStarted = identity.ContentStarted + 1 }, true));
                Reject("Wrong content process is rejected", () => NativeMethods.RequireWindowIdentity(identity with { ContentProcessId = identity.HostProcessId }, true));
                Reject("Reparented or swapped child handle is rejected", () => NativeMethods.RequireWindowIdentity(identity with { ContentHandle = handle }, true));
                Reject("Changed package application identity is rejected", () => NativeMethods.RequireWindowIdentity(identity with { AppUserModelId = "Other.Package!App" }, true));
            }
            Verify("Registered Calculator matches its observed window", DesktopAppLauncher.Matches(calculator, identity));
            Verify("Another package cannot match Calculator's window", !DesktopAppLauncher.Matches(calculator with
            {
                AppUserModelId = "Other.Package!App"
            }, identity));
            Verify("Stale content cannot satisfy launch verification", !DesktopAppLauncher.Matches(calculator, identity with
            {
                ContentStarted = identity.ContentStarted + 1
            }));
            var window = tools.ListWindows().Single(entry => entry.Handle == (long)handle);
            Verify("Public window exposes content process plus separate host evidence", window.ProcessId == identity.ContentProcessId && window.HostProcessId == identity.HostProcessId && window.ContentHandle == (long)identity.ContentHandle && window.AppUserModelId == calculatorAppId);
            Verify("Window ID pins host lifetime", window.Id.Contains($"-{identity.HostProcessId}-{identity.HostStarted:X}"));
            if (identity.IsHosted)
                Verify("Window ID also pins content handle and lifetime", window.Id.Contains($"-c{identity.ContentHandle:X}-{identity.ContentProcessId}-{identity.ContentStarted:X}"));
            var repeated = tools.ListWindows().Single(entry => entry.Handle == (long)handle);
            Verify("Unchanged Calculator window retains its issued ID", repeated.Id == window.Id);
            VerifyStaleCache(tools, window, identity);
        }
    }
    success = true;
}
catch (Exception error)
{
    failure = error.ToString();
    Console.Error.WriteLine(error);
}
File.WriteAllText(Path.Combine(outputDirectory, "results.json"), JsonSerializer.Serialize(new
{
    passed = success,
    checksPassed = results.Count(check => check.Status == "passed"),
    checksSkipped = results.Count(check => check.Status == "skipped"),
    includeCalculator,
    results,
    evidence,
    error = failure,
    milliseconds = watch.Elapsed.TotalMilliseconds,
    applicationsLaunched = false,
    foregroundChanged = false,
    inputSent = false,
    microphoneOpened = false
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{results.Count(check => check.Status == "passed")} passed; {results.Count(check => check.Status == "skipped")} skipped. No app actions performed.");
return success ? 0 : 1;

static nint FindCalculatorWindow(string expectedAppId)
{
    static bool HasAppId(nint handle, string expected)
    {
        NativeMethods.GetWindowThreadProcessId(handle, out var pid);
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return NativeMethods.ProcessAppId(process) == expected;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }
    nint found = 0;
    NativeMethods.EnumWindows((root, parameter) =>
    {
        if (!NativeMethods.IsWindowVisible(root))
            return true;
        if (HasAppId(root, expectedAppId))
        {
            found = root;
            return false;
        }
        var visited = 0;
        NativeMethods.EnumChildWindows(root, (child, childParameter) =>
        {
            if (++visited > 256)
                return false;
            if (NativeMethods.IsWindowVisible(child) && NativeMethods.IsChild(root, child) &&
                NativeMethods.GetAncestor(child, 2) == root && HasAppId(child, expectedAppId))
            {
                found = root;
                return false;
            }
            return true;
        }, 0);
        return found == 0;
    }, 0);
    return found;
}

internal sealed record Check(string Name, string Status, string? Reason = null);
