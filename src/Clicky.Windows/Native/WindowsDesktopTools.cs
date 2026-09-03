using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Windows.Automation;
using Clicky.Core;
using DrawingPoint = System.Drawing.Point;

namespace Clicky.Windows.Native;

public sealed record DesktopWindow(string Id, long Handle, string Title, string Application, int ProcessId, bool Foreground, int Left, int Top, int Width, int Height, uint Dpi, bool IsMinimized = false, int? HostProcessId = null, long? ContentHandle = null, string? AppUserModelId = null);
public sealed record DesktopElement(string Id, string Name, string Type, string AutomationId, bool Enabled, bool Password, int Left, int Top, int Width, int Height);
public sealed record DesktopSnapshot(string WindowId, string SnapshotId, IReadOnlyList<DesktopElement> Elements, bool Truncated);
public sealed record DesktopObservationElement(string ElementId, string Name, string Type, string AutomationId,
    double X, double Y, double Left, double Top, double Width, double Height);
public sealed record DesktopObservation(string WindowId, string SnapshotId, string Title, string Application,
    int CaptureLeft, int CaptureTop, int CaptureWidth, int CaptureHeight,
    IReadOnlyList<DesktopObservationElement> Elements, bool Truncated);
public sealed record DesktopActionVisual(string Kind, int X, int Y, string Label);

/// <summary>Only registered tools perform input. Guidance rendering has no dependency on this executor.</summary>
public sealed class WindowsDesktopTools : IToolExecutor
{
    private readonly object gate = new();
    private readonly Dictionary<string, NativeMethods.WindowIdentity> windows = new();
    private readonly Dictionary<string, SnapshotState> snapshots = new();
    private sealed record SnapshotState(string Id, nint Handle, DateTimeOffset Created, Dictionary<string, AutomationElement> Elements);
    private readonly SemaphoreSlim inputLock = new(1, 1);
    private readonly DesktopAppCatalog applications = new();
    public event Action<DesktopActionVisual>? ActionVisual;
    public IReadOnlyList<ToolDefinition> Tools
    {
        get;
    } =
    [
        new("desktop_apps", "Find installed GUI applications by name, including Notepad, Calculator, browsers, Telegram, VS Code and Office when installed. Returns up to 100 entries; use query to narrow results. Duplicate names require choosing the intended installation. Only returned app IDs can be launched.", Schema(new { query = new { type = "string", maxLength = 160 } }), RiskLevel.ReadOnly),
        new("desktop_launch", "Open one installed application using an appId returned by desktop_apps. No command strings, arguments, files or URLs are accepted. Reuses a single matching window; multiple existing windows require explicit selection. Success includes observed process/window evidence.", Schema(new { appId = new { type = "string" } }, "appId"), RiskLevel.LocalWrite),
        new("desktop_windows", "List unelevated Windows windows, including minimized windows. Use returned windowId values only.", JsonSchema.Parse("{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"), RiskLevel.ReadOnly),
        new("desktop_activate", "Restore and foreground one window returned by desktop_windows. Verifies its process identity, permissions and final foreground state; Windows may refuse focus. Does not click or type.", Schema(new { windowId = new { type = "string" } }, "windowId"), RiskLevel.LocalWrite),
        new("desktop_snapshot", "Inspect accessible controls in a listed window. IDs expire; inspect again after layout changes. Does not capture pixels.", Schema(new { windowId = new { type = "string" } }, "windowId"), RiskLevel.ReadOnly),
        new("desktop_click", "Click one control from a recent snapshot in the foreground target. Sensitive: this may submit or change business data. Do not retry if input was performed.", ElementSchema(), RiskLevel.Sensitive),
        new("desktop_type", "Insert text into an editable control from a recent snapshot. Password fields are refused. Explicit approval is required.", Schema(new { windowId = new { type = "string" }, snapshotId = new { type = "string" }, elementId = new { type = "string" }, text = new { type = "string", maxLength = 50000 } }, "windowId", "snapshotId", "elementId", "text"), RiskLevel.Sensitive),
        new("desktop_key", "Send a navigation key to the foreground listed window. Allowed: Enter, Escape, Tab, Shift+Tab, arrows, Home, End, PageUp, PageDown, Backspace, Delete, Ctrl+A, Ctrl+C. Enter may submit data.", Schema(new { windowId = new { type = "string" }, key = new { type = "string" } }, "windowId", "key"), RiskLevel.Sensitive),
        new("desktop_scroll", "Scroll a control using its accessibility ScrollPattern. Direction is up or down; unsupported controls are refused.", Schema(new { windowId = new { type = "string" }, snapshotId = new { type = "string" }, elementId = new { type = "string" }, direction = new { type = "string", @enum = new[] { "up", "down" } } }, "windowId", "snapshotId", "elementId", "direction"), RiskLevel.Sensitive)
    ];
    private static JsonElement Schema(object properties, params string[] required) => JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false });
    private static JsonElement ElementSchema() => Schema(new { windowId = new { type = "string" }, snapshotId = new { type = "string" }, elementId = new { type = "string" } }, "windowId", "snapshotId", "elementId");
    public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name == "desktop_windows")
                return new(true, "Visible Windows windows.", ListWindows());
            if (name == "desktop_apps")
                return new(true, "Installed application choices. Select an exact returned Id as appId; use a narrower query if needed.", await ListAppsAsync(arguments.TryGetProperty("query", out var query) && query.ValueKind == JsonValueKind.String ? query.GetString() : null, bounded.Token));
            if (name == "desktop_snapshot")
                return new(true, "Current accessibility snapshot.", await Task.Run(() => Snapshot(Text(arguments, "windowId"), bounded.Token), bounded.Token).WaitAsync(TimeSpan.FromSeconds(16), cancellationToken));
            if (name is not ("desktop_click" or "desktop_type" or "desktop_key" or "desktop_scroll" or "desktop_launch" or "desktop_activate"))
                return new(false, "Unknown desktop tool.");
            await inputLock.WaitAsync(bounded.Token);
            try
            {
                if (name == "desktop_launch")
                    return await LaunchAsync(Text(arguments, "appId"), bounded.Token);
                if (name == "desktop_activate")
                    return await ActivateAsync(Text(arguments, "windowId"), bounded.Token);
                return await Task.Run(() => Perform(name, arguments, bounded.Token), bounded.Token).WaitAsync(TimeSpan.FromSeconds(16), cancellationToken);
            }
            finally { inputLock.Release(); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(false, "The target application did not respond within 15 seconds. Inspect its state before continuing; do not automatically retry input.", new { performed = name is not ("desktop_apps" or "desktop_windows" or "desktop_snapshot"), completionUnknown = true }); }
        catch (OperationCanceledException) { throw; }
        catch (TimeoutException) { return new(false, "The target application's accessibility provider is unresponsive. No additional input will be attempted; inspect its state before retrying.", new { performed = name is not ("desktop_apps" or "desktop_windows" or "desktop_snapshot"), completionUnknown = true }); }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or ElementNotAvailableException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException)
        {
            return new(false, exception.Message);
        }
    }
    public IReadOnlyList<DesktopWindow> ListWindows()
    {
        var output = new List<DesktopWindow>();
        var current = NativeMethods.GetForegroundWindow();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;
            var title = NativeMethods.Title(hwnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == Environment.ProcessId)
                return true;
            try
            {
                var identity = NativeMethods.ReadWindowIdentity(hwnd, allowMinimized: true);
                if (identity.ContentProcessId == Environment.ProcessId)
                    return true;
                var id = $"w{hwnd:X}-{identity.HostProcessId}-{identity.HostStarted:X}";
                if (identity.IsHosted)
                    id += $"-c{identity.ContentHandle:X}-{identity.ContentProcessId}-{identity.ContentStarted:X}";
                NativeMethods.GetWindowRect(hwnd, out var rectangle);
                lock (gate)
                {
                    if (windows.TryGetValue(id, out var previous) && previous != identity)
                        snapshots.Remove(id);
                    windows[id] = identity;
                }
                output.Add(new(id, hwnd, title, identity.Application, identity.ContentProcessId, current == hwnd, rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top, NativeMethods.GetDpiForWindow(hwnd), NativeMethods.IsIconic(hwnd), identity.HostProcessId, identity.ContentHandle, identity.AppUserModelId));
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException) { }
            return true;
        }, 0);
        lock (gate)
        {
            var live = output.Select(x => x.Id).ToHashSet();
            foreach (var old in windows.Keys.Where(k => !live.Contains(k)).ToArray())
            {
                windows.Remove(old);
                snapshots.Remove(old);
            }
        }
        return output;
    }

    /// <summary>Creates a short-lived accessibility map whose normalized coordinates match the supplied screen image.</summary>
    public DesktopObservation? ObserveWindow(nint handle, ScreenCapture capture, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var window = ListWindows().FirstOrDefault(entry => entry.Handle == handle.ToInt64());
        if (window is null)
            return null;
        var snapshot = Snapshot(window.Id, cancellationToken);
        var right = (long)capture.Left + capture.Width;
        var bottom = (long)capture.Top + capture.Height;
        var elements = snapshot.Elements
            .Where(element => element.Enabled && !element.Password && element.Width > 0 && element.Height > 0 &&
                (long)element.Left < right && (long)element.Top < bottom &&
                (long)element.Left + element.Width > capture.Left && (long)element.Top + element.Height > capture.Top &&
                (!string.IsNullOrWhiteSpace(element.Name) || !string.IsNullOrWhiteSpace(element.AutomationId)))
            .OrderBy(element => element.Top)
            .ThenBy(element => element.Left)
            .Take(120)
            .Select(element => new DesktopObservationElement(
                element.Id,
                element.Name.Length > 160 ? element.Name[..160] : element.Name,
                element.Type,
                element.AutomationId.Length > 120 ? element.AutomationId[..120] : element.AutomationId,
                Normalize(element.Left + element.Width / 2d, capture.Left, capture.Width),
                Normalize(element.Top + element.Height / 2d, capture.Top, capture.Height),
                Normalize(element.Left, capture.Left, capture.Width),
                Normalize(element.Top, capture.Top, capture.Height),
                Normalize(element.Width, 0, capture.Width),
                Normalize(element.Height, 0, capture.Height)))
            .ToArray();
        return new(window.Id, snapshot.SnapshotId, window.Title, window.Application,
            capture.Left, capture.Top, capture.Width, capture.Height, elements,
            snapshot.Truncated || snapshot.Elements.Count > elements.Length);
    }

    private static double Normalize(double value, double origin, double size)
        => Math.Round(Math.Clamp((value - origin) / size, 0, 1), 4, MidpointRounding.AwayFromZero);

    public Task<IReadOnlyList<DesktopApp>> ListAppsAsync(string? query = null, CancellationToken cancellationToken = default) => applications.ListAsync(query, cancellationToken);
    public Task<ToolResult> ActivateWindowAsync(string windowId, CancellationToken cancellationToken = default) => ExecuteAsync("desktop_activate", JsonSerializer.SerializeToElement(new { windowId }), cancellationToken);
    private async Task<ToolResult> ActivateAsync(string windowId, CancellationToken ct, string? applicationName = null)
    {
        var hwnd = ResolveWindow(windowId, allowMinimized: true);
        NativeMethods.RequireInteractiveDesktop();
        ct.ThrowIfCancellationRequested();
        var alreadyForeground = NativeMethods.GetForegroundWindow() == hwnd && !NativeMethods.IsIconic(hwnd);
        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindowAsync(hwnd, 9); // SW_RESTORE
            await Task.Delay(150, ct);
        }
        ResolveWindow(windowId, allowMinimized: true);
        ct.ThrowIfCancellationRequested();
        if (!alreadyForeground)
            NativeMethods.SetForegroundWindow(hwnd);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (NativeMethods.GetForegroundWindow() == hwnd && !NativeMethods.IsIconic(hwnd))
            {
                ResolveWindow(windowId);
                var window = ListWindows().SingleOrDefault(entry => entry.Id == windowId);
                if (window is null || !window.Foreground)
                    return new(false, "Focus or the selected window changed during verification. Inspect windows again.", new
                    {
                        performed = !alreadyForeground,
                        verified = false,
                        windowId
                    });
                var name = applicationName ?? window.Title;
                return new(true, alreadyForeground ? $"{name} is already open in front." : $"Brought {name} to the front.", new
                {
                    performed = !alreadyForeground,
                    verified = true,
                    window
                });
            }
            await Task.Delay(100, ct);
        }
        return new(false, "Windows refused foreground activation. Select this window manually, then continue deliberately.", new
        {
            performed = !alreadyForeground,
            verified = false,
            windowId
        });
    }
    private async Task<ToolResult> LaunchAsync(string appId, CancellationToken ct)
    {
        var registration = await applications.ResolveAsync(appId, ct);
        var app = registration.App;
        var existing = ListWindows().Where(window => MatchesApplication(app, window.Id)).ToArray();
        if (existing.Length > 1)
            return new(false, "This application has multiple windows. Choose the intended windowId and use desktop_activate.", new
            {
                performed = false,
                app,
                windows = existing
            });
        if (existing.Length == 1)
            return await ActivateAsync(existing[0].Id, ct, app.Name);
        ct.ThrowIfCancellationRequested();
        var processId = await DesktopAppCatalog.StaAsync(() => DesktopAppLauncher.Start(app, ct), ct);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(8))
        {
            ct.ThrowIfCancellationRequested();
            var observed = ListWindows().Where(window => MatchesApplication(app, window.Id)).ToArray();
            if (observed.Length > 0)
                return new(true, $"Opened {app.Name}.", new
                {
                    performed = true,
                    verified = true,
                    app,
                    processId,
                    windows = observed,
                    windowVerified = true
                });
            await Task.Delay(200, ct);
        }
        if (DesktopAppLauncher.Matches(app, processId))
            return new(true, $"Started {app.Name}, but its window is not ready yet. Inspect windows before interacting.", new
            {
                performed = true,
                verified = true,
                app,
                processId,
                windowVerified = false
            });
        return new(false, $"Windows accepted the request to open {app.Name}, but its process or window could not be verified. Inspect windows before trying again.", new
        {
            performed = true,
            verified = false,
            app,
            processId
        });
    }
    private bool MatchesApplication(DesktopApp app, string windowId)
    {
        NativeMethods.WindowIdentity? identity;
        lock (gate)
            windows.TryGetValue(windowId, out identity);
        return identity is not null && DesktopAppLauncher.Matches(app, identity);
    }
    private nint ResolveWindow(string id, bool allowMinimized = false)
    {
        NativeMethods.WindowIdentity? entry;
        lock (gate)
            if (!windows.TryGetValue(id, out entry))
                throw new InvalidOperationException("Unknown window. Run desktop_windows again.");
        NativeMethods.RequireWindowIdentity(entry, allowMinimized);
        return entry.Handle;
    }
    private void RequireTargetForeground(string windowId, nint expected)
    {
        if (ResolveWindow(windowId) != expected)
            throw new InvalidOperationException("The target window changed. Inspect windows again.");
        NativeMethods.RequireForeground(expected);
    }
    public DesktopSnapshot Snapshot(string windowId, CancellationToken cancellationToken = default)
    {
        var hwnd = ResolveWindow(windowId);
        var root = AutomationElement.FromHandle(hwnd) ?? throw new InvalidOperationException("This window has no accessibility tree.");
        var snapshotId = Guid.NewGuid().ToString("N");
        var elements = new Dictionary<string, AutomationElement>();
        var details = new List<DesktopElement>();
        var queue = new Queue<(AutomationElement Element, int Depth)>();
        queue.Enqueue((root, 0));
        var stopwatch = Stopwatch.StartNew();
        while (queue.Count > 0 && details.Count < 200 && stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (element, depth) = queue.Dequeue();
            try
            {
                var current = element.Current;
                if (!current.IsOffscreen)
                {
                    var rectangle = current.BoundingRectangle;
                    var id = "e" + string.Join("-", element.GetRuntimeId().Select(i => i.ToString("X", CultureInfo.InvariantCulture)));
                    elements[id] = element;
                    details.Add(new(id, current.IsPassword ? "[password field]" : current.Name, current.ControlType.ProgrammaticName.Replace("ControlType.", ""), current.AutomationId, current.IsEnabled, current.IsPassword, (int)rectangle.Left, (int)rectangle.Top, (int)rectangle.Width, (int)rectangle.Height));
                }
                if (depth >= 9)
                    continue;
                var child = TreeWalker.ControlViewWalker.GetFirstChild(element);
                while (child is not null && queue.Count < 400)
                {
                    queue.Enqueue((child, depth + 1));
                    child = TreeWalker.ControlViewWalker.GetNextSibling(child);
                }
            }
            catch (ElementNotAvailableException) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
        lock (gate)
            snapshots[windowId] = new(snapshotId, hwnd, DateTimeOffset.UtcNow, elements);
        return new(windowId, snapshotId, details, queue.Count > 0);
    }
    private AutomationElement ResolveElement(JsonElement arguments, nint hwnd)
    {
        var windowId = Text(arguments, "windowId");
        lock (gate)
        {
            if (!snapshots.TryGetValue(windowId, out var state) || state.Handle != hwnd || state.Id != Text(arguments, "snapshotId") || DateTimeOffset.UtcNow - state.Created > TimeSpan.FromSeconds(90))
                throw new InvalidOperationException("Accessibility snapshot expired. Inspect the target again.");
            if (!state.Elements.TryGetValue(Text(arguments, "elementId"), out var element))
                throw new InvalidOperationException("Element is not in this snapshot.");
            var current = element.Current;
            if (!current.IsEnabled || current.IsOffscreen || current.IsPassword)
                throw new InvalidOperationException("Control is disabled, hidden, or a password field; interaction refused.");
            return element;
        }
    }
    private ToolResult Perform(string name, JsonElement arguments, CancellationToken cancellationToken)
    {
        var windowId = Text(arguments, "windowId");
        var hwnd = ResolveWindow(windowId);
        RequireTargetForeground(windowId, hwnd);
        cancellationToken.ThrowIfCancellationRequested();
        var performed = false;
        try
        {
            switch (name)
            {
                case "desktop_click":
                    {
                        var element = ResolveElement(arguments, hwnd);
                        if (!element.TryGetClickablePoint(out var point))
                            throw new InvalidOperationException("Control has no unambiguous clickable point.");
                        var hit = NativeMethods.WindowFromPoint(new((int)point.X, (int)point.Y));
                        if (NativeMethods.GetAncestor(hit, 2) != hwnd)
                            throw new InvalidOperationException("Another window covers the control. Bring the target forward and inspect again.");
                        RequireTargetForeground(windowId, hwnd);
                        cancellationToken.ThrowIfCancellationRequested();
                        NotifyActionVisual(new("click", (int)point.X, (int)point.Y, element.Current.Name));
                        if (element.TryGetCurrentPattern(InvokePattern.Pattern, out var invokePattern) && invokePattern is InvokePattern invoke)
                        {
                            invoke.Invoke();
                            performed = true;
                            break;
                        }
                        if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var togglePattern) && togglePattern is TogglePattern toggle)
                        {
                            var before = toggle.Current.ToggleState;
                            toggle.Toggle();
                            performed = true;
                            Thread.Sleep(80);
                            var verified = toggle.Current.ToggleState != before;
                            return new(verified, verified ? "The control was toggled through Windows accessibility and its new state was verified." : "The toggle action was sent, but its new state could not be verified. Inspect before continuing.", new
                            {
                                performed = true,
                                targetVerified = true,
                                outcomeVerified = verified,
                                physicalPointerMoved = false
                            });
                        }
                        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionPattern) && selectionPattern is SelectionItemPattern selection)
                        {
                            selection.Select();
                            performed = true;
                            Thread.Sleep(80);
                            var verified = selection.Current.IsSelected;
                            return new(verified, verified ? "The item was selected through Windows accessibility and verified." : "The selection action was sent, but its state could not be verified. Inspect before continuing.", new
                            {
                                performed = true,
                                targetVerified = true,
                                outcomeVerified = verified,
                                physicalPointerMoved = false
                            });
                        }
                        NativeMethods.GetCursorPos(out var originalPointer);
                        NativeMethods.SetCursorPos((int)point.X, (int)point.Y);
                        NativeMethods.Send(NativeMethods.Mouse(0x2), NativeMethods.Mouse(0x4));
                        performed = true;
                        Thread.Sleep(60);
                        if (NativeMethods.GetCursorPos(out var afterClick) && Math.Abs(afterClick.X - point.X) < 3 && Math.Abs(afterClick.Y - point.Y) < 3)
                            NativeMethods.SetCursorPos(originalPointer.X, originalPointer.Y);
                        break;
                    }
                case "desktop_type":
                    {
                        var element = ResolveElement(arguments, hwnd);
                        var text = Text(arguments, "text");
                        if (text.Length > 50_000)
                            throw new ArgumentException("Text is too long for one action.");
                        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) || pattern is not ValuePattern value)
                        {
                            if (element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.Document ||
                                !element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern) || textPattern is not TextPattern editable ||
                                editable.DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false)
                                throw new InvalidOperationException("This control does not expose verified editable text. Inspect an editable control or use user-directed dictation.");
                            if (new[] { 0x11, 0x12, 0x10, 0x5b, 0x5c }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0))
                                throw new InvalidOperationException("Release held modifier keys before visible text input.");
                            var before = editable.DocumentRange.GetText(100_000);
                            RequireTargetForeground(windowId, hwnd);
                            cancellationToken.ThrowIfCancellationRequested();
                            element.SetFocus();
                            for (var offset = 0; offset < text.Length;)
                            {
                                RequireTargetForeground(windowId, hwnd);
                                if (new[] { 0x11, 0x12, 0x10, 0x5b, 0x5c }.Any(key => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0))
                                    throw new InvalidOperationException("A modifier key was pressed during text input. No additional text will be sent.");
                                if (!Automation.Compare(element, AutomationElement.FocusedElement))
                                    throw new InvalidOperationException("The exact editable control did not retain keyboard focus. No additional text will be sent.");
                                cancellationToken.ThrowIfCancellationRequested();
                                var end = Math.Min(offset + 32, text.Length);
                                if (end < text.Length && char.IsHighSurrogate(text[end - 1]))
                                    end--;
                                var inputs = new List<NativeMethods.Input>();
                                for (; offset < end; offset++)
                                {
                                    inputs.Add(NativeMethods.Unicode(text[offset]));
                                    inputs.Add(NativeMethods.Unicode(text[offset], true));
                                }
                                performed = true; // SendInput can partially succeed; never retry this action automatically.
                                NativeMethods.Send(inputs.ToArray());
                            }
                            Thread.Sleep(100);
                            cancellationToken.ThrowIfCancellationRequested();
                            RequireTargetForeground(windowId, hwnd);
                            var afterText = editable.DocumentRange.GetText(100_000);
                            var verified = afterText != before && NormalizeLines(afterText).Contains(NormalizeLines(text), StringComparison.Ordinal);
                            return new(verified, verified ? "Visible Unicode text input was verified in the exact editable control. The clipboard was not changed." : "Text input was sent, but its resulting text could not be verified. Inspect before continuing; do not retry automatically.", new
                            {
                                performed,
                                verified,
                                targetVerified = true,
                                clipboardChanged = false
                            });
                        }
                        if (value.Current.IsReadOnly)
                            throw new InvalidOperationException("The target text control is read-only.");
                        var existing = value.Current.Value;
                        RequireTargetForeground(windowId, hwnd);
                        cancellationToken.ThrowIfCancellationRequested();
                        value.SetValue(existing + text);
                        performed = true;
                        if (value.Current.Value != existing + text)
                            return new(false, "Text input was performed but verification failed; do not retry automatically.", new
                            {
                                performed = true,
                                verified = false
                            });
                        break;
                    }
                case "desktop_key":
                    {
                        var chord = Text(arguments, "key");
                        var parts = chord.Split('+');
                        var allowed = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) { ["Enter"] = 13, ["Escape"] = 27, ["Tab"] = 9, ["Left"] = 37, ["Up"] = 38, ["Right"] = 39, ["Down"] = 40, ["Home"] = 36, ["End"] = 35, ["PageUp"] = 33, ["PageDown"] = 34, ["Backspace"] = 8, ["Delete"] = 46, ["A"] = 65, ["C"] = 67 };
                        var modifier = parts.Length == 2 && parts[0].Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ? (ushort)17 : parts.Length == 2 && parts[0].Equals("Shift", StringComparison.OrdinalIgnoreCase) ? (ushort)16 : (ushort)0;
                        if (!allowed.TryGetValue(parts[^1], out var key) || (parts.Length > 1 && modifier == 0) || parts.Length > 2 || (key is 65 or 67 && modifier != 17) || (modifier == 17 && key is not (65 or 67)) || (modifier == 16 && key != 9))
                            throw new ArgumentException("Unsupported key combination.");
                        cancellationToken.ThrowIfCancellationRequested();
                        RequireTargetForeground(windowId, hwnd);
                        if (modifier != 0)
                            NativeMethods.Send(NativeMethods.Key(modifier), NativeMethods.Key(key), NativeMethods.Key(key, true), NativeMethods.Key(modifier, true));
                        else
                            NativeMethods.Send(NativeMethods.Key(key), NativeMethods.Key(key, true));
                        performed = true;
                        break;
                    }
                case "desktop_scroll":
                    {
                        var element = ResolveElement(arguments, hwnd);
                        var direction = Text(arguments, "direction");
                        if (direction is not ("up" or "down"))
                            throw new ArgumentException("Direction must be up or down.");
                        if (!element.TryGetCurrentPattern(ScrollPattern.Pattern, out var pattern) || pattern is not ScrollPattern scroll || !scroll.Current.VerticallyScrollable)
                            throw new InvalidOperationException("Control does not expose a vertical accessibility scrollbar.");
                        var before = scroll.Current.VerticalScrollPercent;
                        cancellationToken.ThrowIfCancellationRequested();
                        RequireTargetForeground(windowId, hwnd);
                        scroll.Scroll(ScrollAmount.NoAmount, direction == "down" ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement);
                        performed = true;
                        return new(true, "Scroll performed.", new
                        {
                            performed = true,
                            verified = scroll.Current.VerticalScrollPercent != before,
                            position = scroll.Current.VerticalScrollPercent
                        });
                    }
            }
            Thread.Sleep(100);
            var foreground = NativeMethods.GetForegroundWindow();
            var after = NativeMethods.IsWindow(hwnd) ? Snapshot(windowId, cancellationToken) : null;
            return new(true, "Input was sent to the verified target. Inspect the returned state to confirm the task outcome before any further action.", new
            {
                performed,
                targetVerified = true,
                outcomeVerified = name == "desktop_type",
                foregroundChanged = foreground != hwnd,
                snapshot = after
            });
        }
        catch (Exception exception) when (performed && exception is not OperationCanceledException)
        {
            return new(false, "Action was performed, but post-action inspection failed. Do not retry automatically. " + exception.Message, new
            {
                performed = true,
                verified = false
            });
        }
    }
    private static string Text(JsonElement arguments, string name) => arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : throw new ArgumentException($"Missing {name}.");
    private static string NormalizeLines(string value) => value.Replace("\r\n", "\n").Replace('\r', '\n');
    private void NotifyActionVisual(DesktopActionVisual visual)
    {
        foreach (Action<DesktopActionVisual> observer in ActionVisual?.GetInvocationList() ?? [])
            try
            {
                observer(visual);
            }
            catch { /* A visual observer cannot affect or authorize desktop input. */ }
    }
}
