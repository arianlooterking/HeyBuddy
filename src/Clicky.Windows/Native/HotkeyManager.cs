using System.ComponentModel;
using System.Runtime.InteropServices;
using Clicky.Core;
using Forms = System.Windows.Forms;

namespace Clicky.Windows.Native;

public enum ShortcutAction
{
    Talk, Dictation, Agent, EmergencyStop
}
public enum HotkeyGesture
{
    Pressed, Released, DoubleTap
}

internal static class ShortcutKeyPolicy
{
    internal static bool IsBindable(Forms.Keys key)
    {
        var virtualKey = (uint)key;
        return Enum.IsDefined(key) && virtualKey is > 0 and <= 254 && virtualKey is not (
            0x01 or 0x02 or 0x04 or 0x05 or 0x06 or // Mouse buttons.
            0x10 or 0x11 or 0x12 or 0x5b or 0x5c or // Generic modifiers and Windows keys.
            0xe5 or 0xe7); // IME process and synthetic packet input.
    }

    internal static bool IsStandaloneModifier(Forms.Keys key) => key is
        Forms.Keys.LShiftKey or Forms.Keys.RShiftKey or
        Forms.Keys.LControlKey or Forms.Keys.RControlKey or
        Forms.Keys.LMenu or Forms.Keys.RMenu;

    internal static bool TryParseKey(string value, out Forms.Keys key)
    {
        var normalized = value.Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        key = normalized switch
        {
            "SHIFT" or "LEFTSHIFT" or "LSHIFTKEY" => Forms.Keys.LShiftKey,
            "RIGHTSHIFT" or "RSHIFTKEY" => Forms.Keys.RShiftKey,
            "CTRL" or "CONTROL" or "LEFTCTRL" or "LEFTCONTROL" or "LCONTROLKEY" => Forms.Keys.LControlKey,
            "RIGHTCTRL" or "RIGHTCONTROL" or "RCONTROLKEY" => Forms.Keys.RControlKey,
            "ALT" or "LEFTALT" or "LMENU" => Forms.Keys.LMenu,
            "RIGHTALT" or "RMENU" => Forms.Keys.RMenu,
            _ => 0
        };
        return key != 0 || Enum.TryParse(value, true, out key);
    }

    internal static string FormatKey(Forms.Keys key) => key switch
    {
        Forms.Keys.LShiftKey => "Left Shift",
        Forms.Keys.RShiftKey => "Right Shift",
        Forms.Keys.LControlKey => "Left Ctrl",
        Forms.Keys.RControlKey => "Right Ctrl",
        Forms.Keys.LMenu => "Left Alt",
        Forms.Keys.RMenu => "Right Alt",
        _ => key.ToString()
    };

    internal static bool CanUseWithoutModifiers(Forms.Keys key) => key is not (Forms.Keys.Escape or Forms.Keys.Tab);
}

public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<ShortcutAction, Shortcut> shortcuts = new();
    private readonly Dictionary<uint, ShortcutAction> held = new();
    private readonly HashSet<uint> physicalKeysDown = [];
    private readonly Dictionary<ShortcutAction, long> previousTap = new();
    private readonly NativeMethods.HookProc keyboardCallback;
    private readonly NativeMethods.HookProc mouseCallback;
    private nint keyboardHook, mouseHook;
    private SynchronizationContext? context;
    public event Action<ShortcutAction, HotkeyGesture>? ActionInvoked;
    public event Action<System.Drawing.Point>? PointerClicked;
    public static nint GetForegroundWindow() => NativeMethods.GetForegroundWindow();
    public HotkeyManager(AppSettings settings)
    {
        shortcuts[ShortcutAction.Talk] = Shortcut.Parse(settings.TalkShortcut);
        shortcuts[ShortcutAction.Dictation] = Shortcut.Parse(settings.DictationShortcut);
        shortcuts[ShortcutAction.Agent] = Shortcut.Parse(settings.AgentShortcut);
        shortcuts[ShortcutAction.EmergencyStop] = Shortcut.Parse(settings.StopShortcut);
        if (shortcuts.Values.Distinct().Count() != shortcuts.Count)
            throw new ArgumentException("Every global shortcut must be different.");
        foreach (var first in shortcuts.Values)
        {
            if (shortcuts.Values.Any(other => !ReferenceEquals(first, other) && first.ConflictsWith(other)))
                throw new ArgumentException("A standalone Shift, Ctrl, or Alt button cannot also be the modifier for another HeyBuddy shortcut. Choose a combination that uses a different modifier.");
        }
        keyboardCallback = OnKeyboard;
        mouseCallback = OnMouse;
    }
    /// <summary>Call on the UI thread, which must keep pumping messages.</summary>
    public void Start()
    {
        if (keyboardHook != 0)
            return;
        context = SynchronizationContext.Current;
        var module = NativeMethods.GetModuleHandle(null);
        keyboardHook = NativeMethods.SetWindowsHookEx(13, keyboardCallback, module, 0);
        if (keyboardHook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register keyboard shortcuts.");
        mouseHook = NativeMethods.SetWindowsHookEx(14, mouseCallback, module, 0);
        if (mouseHook == 0)
        {
            Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not observe walkthrough clicks.");
        }
    }
    private void Dispatch(Action action)
    {
        if (context != null)
            context.Post(_ => action(), null);
        else
            ThreadPool.QueueUserWorkItem(_ => action());
    }
    private nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<NativeMethods.KeyboardHookData>(lParam);
            if ((data.Flags & 0x10) == 0)
            {
                var down = wParam == 0x100 || wParam == 0x104;
                var up = wParam == 0x101 || wParam == 0x105;
                if (down)
                {
                    if (!physicalKeysDown.Add(data.VkCode))
                        return held.ContainsKey(data.VkCode) ? 1 : NativeMethods.CallNextHookEx(keyboardHook, code, wParam, lParam);
                    foreach (var (action, shortcut) in shortcuts)
                    {
                        if (!shortcut.Matches(data.VkCode))
                            continue;
                        held[data.VkCode] = action;
                        var now = Environment.TickCount64;
                        var doubleTap = previousTap.TryGetValue(action, out var previous) && now - previous <= 400;
                        previousTap[action] = doubleTap ? 0 : now;
                        Dispatch(() => ActionInvoked?.Invoke(action, doubleTap ? HotkeyGesture.DoubleTap : HotkeyGesture.Pressed));
                        return 1;
                    }
                }
                else if (up)
                {
                    physicalKeysDown.Remove(data.VkCode);
                    if (held.Remove(data.VkCode, out var action))
                    {
                        Dispatch(() => ActionInvoked?.Invoke(action, HotkeyGesture.Released));
                        return 1;
                    }
                }
            }
        }
        return NativeMethods.CallNextHookEx(keyboardHook, code, wParam, lParam);
    }
    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && wParam == 0x202)
        {
            var data = Marshal.PtrToStructure<NativeMethods.MouseHookData>(lParam);
            if ((data.Flags & 1) == 0)
            {
                var point = new System.Drawing.Point(data.Position.X, data.Position.Y);
                Dispatch(() => PointerClicked?.Invoke(point));
            }
        }
        return NativeMethods.CallNextHookEx(mouseHook, code, wParam, lParam);
    }
    public void Dispose()
    {
        if (keyboardHook != 0)
            NativeMethods.UnhookWindowsHookEx(keyboardHook);
        if (mouseHook != 0)
            NativeMethods.UnhookWindowsHookEx(mouseHook);
        keyboardHook = mouseHook = 0;
        held.Clear();
        physicalKeysDown.Clear();
        GC.SuppressFinalize(this);
    }
    private sealed record Shortcut(uint Key, bool Control, bool Alt, bool Shift, bool Win)
    {
        internal static Shortcut Parse(string value)
        {
            var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !ShortcutKeyPolicy.TryParseKey(parts[^1], out var key) || !ShortcutKeyPolicy.IsBindable(key))
                throw new ArgumentException($"Invalid shortcut: {value}. Choose one keyboard key or a key combination, for example Left Shift, F8, 1 or Ctrl+Alt+D.");
            var modifiers = parts[..^1];
            if (ShortcutKeyPolicy.IsStandaloneModifier(key))
            {
                if (modifiers.Length != 0)
                    throw new ArgumentException($"Invalid shortcut: {value}. A modifier can be used by itself, or with a non-modifier key such as Ctrl+Shift+D.");
                return new((uint)key, false, false, false, false);
            }
            if (modifiers.Any(p => !new[] { "Ctrl", "Control", "Alt", "Shift", "Win" }.Contains(p, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown shortcut modifier in {value}.");
            if (modifiers.Length == 0 && !ShortcutKeyPolicy.CanUseWithoutModifiers(key))
                throw new ArgumentException($"Invalid shortcut: {value}. Escape cancels shortcut recording and Tab moves between controls; add a modifier to use either key.");
            bool Has(string modifier) => modifiers.Contains(modifier, StringComparer.OrdinalIgnoreCase);
            return new((uint)key, Has("Ctrl") || Has("Control"), Has("Alt"), Has("Shift"), Has("Win"));
        }
        internal bool Matches(uint key)
        {
            if (key != Key)
                return false;
            var trigger = (Forms.Keys)Key;
            var standalone = ShortcutKeyPolicy.IsStandaloneModifier(trigger);
            // AltGr is reported by Windows as Right Alt with a synthetic Control state on many layouts.
            // Treat that state as part of the exact Right Alt button instead of requiring users to disable AltGr.
            var ignoreControl = standalone && trigger is Forms.Keys.LControlKey or Forms.Keys.RControlKey or Forms.Keys.RMenu;
            var ignoreAlt = standalone && trigger is Forms.Keys.LMenu or Forms.Keys.RMenu;
            var ignoreShift = standalone && trigger is Forms.Keys.LShiftKey or Forms.Keys.RShiftKey;
            return (ignoreControl || Down(0x11) == Control) &&
                (ignoreAlt || Down(0x12) == Alt) &&
                (ignoreShift || Down(0x10) == Shift) &&
                (Down(0x5b) || Down(0x5c)) == Win;
        }

        internal bool ConflictsWith(Shortcut other)
        {
            var trigger = (Forms.Keys)Key;
            if (!ShortcutKeyPolicy.IsStandaloneModifier(trigger) || ShortcutKeyPolicy.IsStandaloneModifier((Forms.Keys)other.Key))
                return false;
            return trigger switch
            {
                Forms.Keys.LShiftKey or Forms.Keys.RShiftKey => other.Shift,
                Forms.Keys.LControlKey or Forms.Keys.RControlKey => other.Control,
                Forms.Keys.LMenu => other.Alt,
                Forms.Keys.RMenu => other.Alt || other.Control,
                _ => false
            };
        }

        private static bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
