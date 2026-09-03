using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Clicky.Core;
using Clicky.Windows.Native;
using FormsKeys = System.Windows.Forms.Keys;

namespace Clicky.Windows;

public partial class MainWindow
{
    private readonly HashSet<ShortcutRecorder> shortcutRecordersInProgress = [];
    private bool restoreShortcutHooks;

    private ShortcutRecorder ShortcutField(string label, string value)
    {
        PageContent.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        ShortcutRecorder? recorder = null;
        recorder = new(value, () => BeginShortcutRecording(recorder!), () => EndShortcutRecording(recorder!), SetStatus);
        recorder.Style = (Style)FindResource(typeof(System.Windows.Controls.TextBox));
        System.Windows.Automation.AutomationProperties.SetName(recorder, label);
        System.Windows.Automation.AutomationProperties.SetHelpText(recorder, ShortcutRecorder.Instructions);
        EventHandler deactivate = (_, _) => recorder.CancelRecording();
        recorder.Loaded += (_, _) => Deactivated += deactivate;
        recorder.Unloaded += (_, _) => Deactivated -= deactivate;
        PageContent.Children.Add(recorder);
        return recorder;
    }

    private bool BeginShortcutRecording(ShortcutRecorder recorder)
    {
        if (busy || recording || finishingRecording || microphoneTest || listeningLoop is not null ||
            app.Store.GetRuns().Any(run => run.Status is RunStatus.Running or RunStatus.AwaitingApproval))
        {
            SetStatus("Finish or stop the active task or recording before changing a shortcut.");
            return false;
        }
        if (shortcutRecordersInProgress.Count == 0)
        {
            restoreShortcutHooks = hotkeys is not null;
            hotkeys?.Dispose();
            hotkeys = null;
        }
        shortcutRecordersInProgress.Add(recorder);
        return true;
    }

    private void EndShortcutRecording(ShortcutRecorder recorder)
    {
        shortcutRecordersInProgress.Remove(recorder);
        if (shortcutRecordersInProgress.Count != 0 || !restoreShortcutHooks)
            return;
        restoreShortcutHooks = false;
        if (!exiting)
            StartHotkeys();
    }

    private void ValidateShortcutSettings(AppSettings settings)
    {
        if (shortcutRecordersInProgress.Count != 0)
            throw new InvalidOperationException("Finish recording the shortcut and release all keys before saving settings.");
        foreach (var value in new[] { settings.TalkShortcut, settings.DictationShortcut, settings.AgentShortcut, settings.StopShortcut })
            ShortcutRecorder.ValidateBinding(value);
        using var validate = new HotkeyManager(settings);
    }
}

/// <summary>Records through focused WPF input only. It never sends keys or installs a hook.</summary>
internal sealed class ShortcutRecorder : System.Windows.Controls.TextBox
{
    internal const string Instructions = "Click to record, or focus this field and press Enter. Press F1–F24, or hold Ctrl, Alt, Shift or Win with another key, then release. Escape cancels; modifier+Escape can be recorded. Save settings to apply.";
    private readonly Func<bool> begin;
    private readonly Action end;
    private readonly Action<string> report;
    private readonly HashSet<Key> heldKeys = [];
    private readonly DispatcherTimer releaseTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private string previous = "";
    private string? captured;
    private bool waitingForRelease;
    private DateTime deadline;
    internal bool IsRecording
    {
        get; private set;
    }

    internal ShortcutRecorder(string value, Func<bool> begin, Action end, Action<string> report)
    {
        this.begin = begin;
        this.end = end;
        this.report = report;
        Text = value;
        IsReadOnly = true;
        IsUndoEnabled = false;
        Cursor = Cursors.Hand;
        MinHeight = 36;
        MaxHeight = 40;
        ToolTip = Instructions;
        ContextMenu = null;
        InputMethod.SetIsInputMethodEnabled(this, false);
        PreviewMouseLeftButtonDown += (_, args) => { Focus(); BeginRecording(); args.Handled = true; };
        PreviewKeyDown += (_, args) =>
        {
            var key = args.Key == Key.System ? args.SystemKey : args.Key == Key.ImeProcessed ? args.ImeProcessedKey : args.Key;
            args.Handled = RecordKeyDown(key, Keyboard.Modifiers);
        };
        PreviewKeyUp += (_, args) =>
        {
            var key = args.Key == Key.System ? args.SystemKey : args.Key;
            if (!IsRecording)
                return;
            heldKeys.Remove(key);
            CompleteAfterRelease(PhysicalModifiers(), IsPhysicalKeyDown);
            args.Handled = true;
        };
        LostKeyboardFocus += (_, _) => CancelRecording();
        Unloaded += (_, _) => CancelRecording();
        releaseTimer.Tick += (_, _) =>
        {
            if (!waitingForRelease && DateTime.UtcNow >= deadline)
                CancelRecording();
            CompleteAfterRelease(PhysicalModifiers(), IsPhysicalKeyDown);
        };
    }

    internal void BeginRecording()
    {
        if (IsRecording || !begin())
            return;
        previous = Text;
        captured = null;
        heldKeys.Clear();
        waitingForRelease = false;
        IsRecording = true;
        deadline = DateTime.UtcNow.AddSeconds(30);
        Text = "Press a shortcut…";
        SetResourceReference(BorderBrushProperty, "Accent");
        releaseTimer.Start();
        report("Listening for a shortcut. Press a function key or a modifier plus a key. Escape cancels. HeyBuddy shortcuts are paused until all keys are released.");
    }

    internal bool RecordKeyDown(Key key, ModifierKeys modifiers)
    {
        if (!IsRecording)
        {
            if (modifiers == ModifierKeys.None && key is Key.Enter or Key.Space)
            {
                BeginRecording();
                return true;
            }
            return false;
        }
        if (key is not (Key.None or Key.System or Key.ImeProcessed or Key.DeadCharProcessed))
            heldKeys.Add(key);
        if (waitingForRelease)
            return true;
        if (modifiers == ModifierKeys.None && key is Key.Escape or Key.Tab)
        {
            CancelRecording();
            return key != Key.Tab;
        }
        if (IsModifier(key))
        {
            Text = ModifierText(modifiers) + "…";
            return true;
        }
        if (!TryFormat(key, modifiers, out var binding))
        {
            Text = "Press F1–F24 or a modifier + key";
            report("Use F1–F24 alone, or Ctrl, Alt, Shift or Win plus another key. Bare letters remain available for normal typing.");
            return true;
        }
        try
        {
            ValidateBinding(binding);
        }
        catch (ArgumentException error)
        {
            Text = "Choose a different shortcut…";
            report(error.Message);
            return true;
        }
        captured = binding;
        Text = binding + " · release keys";
        waitingForRelease = true;
        return true;
    }

    internal void CancelRecording() => CancelWithKeyState(PhysicalModifiers(), IsPhysicalKeyDown);

    internal void CancelWithKeyState(ModifierKeys modifiers, Func<Key, bool> isKeyDown)
    {
        if (!IsRecording)
            return;
        captured = null;
        Text = previous;
        waitingForRelease = true;
        CompleteAfterRelease(modifiers, isKeyDown);
    }

    internal void CompleteAfterRelease(ModifierKeys modifiers, Func<Key, bool> isKeyDown)
    {
        // Do not restore global hooks while part of a captured or cancelled chord is held.
        if (!IsRecording || !waitingForRelease || modifiers != ModifierKeys.None || heldKeys.Any(isKeyDown))
            return;
        var accepted = captured is not null;
        Text = captured ?? previous;
        IsRecording = false;
        waitingForRelease = false;
        heldKeys.Clear();
        releaseTimer.Stop();
        ClearValue(BorderBrushProperty);
        end();
        report(accepted ? $"Recorded {Text}. Save settings to apply it." : "Shortcut recording cancelled. The previous shortcut is kept.");
    }

    internal static bool TryFormat(Key key, ModifierKeys modifiers, out string binding)
    {
        binding = "";
        if (modifiers == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24) ||
            (modifiers & ~(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows)) != 0 || IsModifier(key))
            return false;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey is <= 0 or > 254 || !Enum.IsDefined((FormsKeys)virtualKey))
            return false;
        binding = ModifierText(modifiers) + ((FormsKeys)virtualKey).ToString();
        return true;
    }

    internal static void ValidateBinding(string binding)
    {
        var parts = binding.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var modifiers = ModifierKeys.None;
        foreach (var part in parts.SkipLast(1))
            modifiers |= part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModifierKeys.Control,
                "ALT" => ModifierKeys.Alt,
                "SHIFT" => ModifierKeys.Shift,
                "WIN" => ModifierKeys.Windows,
                _ => throw new ArgumentException("Use Ctrl, Alt, Shift or Win plus a key for each shortcut.")
            };
        if (parts.Length == 0 || !Enum.TryParse<FormsKeys>(parts[^1], true, out var formsKey) ||
            !TryFormat(KeyInterop.KeyFromVirtualKey((int)formsKey), modifiers, out _))
            throw new ArgumentException($"Invalid shortcut: {binding}. Use F1–F24 or a modifier plus a key, for example Ctrl+Alt+F8.");
        var reserved = (modifiers & ModifierKeys.Windows) != 0 && formsKey == FormsKeys.L ||
            modifiers == (ModifierKeys.Control | ModifierKeys.Alt) && formsKey == FormsKeys.Delete ||
            modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && formsKey == FormsKeys.Escape ||
            modifiers == ModifierKeys.Alt && formsKey is FormsKeys.Tab or FormsKeys.F4 ||
            modifiers == ModifierKeys.Control && formsKey == FormsKeys.Escape;
        if (reserved)
            throw new ArgumentException($"{binding} is reserved for a Windows action. Choose another combination, such as Ctrl+Alt+F8.");
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    private static bool IsPhysicalKeyDown(Key key) => (NativeMethods.GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(key)) & 0x8000) != 0;
    private static ModifierKeys PhysicalModifiers() =>
        (IsPhysicalKeyDown(Key.LeftCtrl) || IsPhysicalKeyDown(Key.RightCtrl) ? ModifierKeys.Control : ModifierKeys.None) |
        (IsPhysicalKeyDown(Key.LeftAlt) || IsPhysicalKeyDown(Key.RightAlt) ? ModifierKeys.Alt : ModifierKeys.None) |
        (IsPhysicalKeyDown(Key.LeftShift) || IsPhysicalKeyDown(Key.RightShift) ? ModifierKeys.Shift : ModifierKeys.None) |
        (IsPhysicalKeyDown(Key.LWin) || IsPhysicalKeyDown(Key.RWin) ? ModifierKeys.Windows : ModifierKeys.None);
    private static string ModifierText(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
        (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") +
        (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
        (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "");
}
