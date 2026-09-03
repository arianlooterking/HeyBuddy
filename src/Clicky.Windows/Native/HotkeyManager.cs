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
public sealed class HotkeyManager : IDisposable
{
    private readonly Dictionary<ShortcutAction, Shortcut> shortcuts = new();
    private readonly Dictionary<uint, ShortcutAction> held = new();
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
                    if (held.ContainsKey(data.VkCode))
                        return 1;
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
                else if (up && held.Remove(data.VkCode, out var action))
                {
                    Dispatch(() => ActionInvoked?.Invoke(action, HotkeyGesture.Released));
                    return 1;
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
        GC.SuppressFinalize(this);
    }
    private sealed record Shortcut(uint Key, bool Control, bool Alt, bool Shift, bool Win)
    {
        internal static Shortcut Parse(string value)
        {
            var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || !Enum.TryParse<Forms.Keys>(parts[^1], true, out var key) || (uint)key is 0 or > 254 or 0x10 or 0x11 or 0x12 or 0x5b or 0x5c || !Enum.IsDefined(key))
                throw new ArgumentException($"Invalid shortcut: {value}. Use F1–F24 or modifiers plus a key, for example Ctrl+Alt+Space.");
            var modifiers = parts[..^1];
            if (modifiers.Any(p => !new[] { "Ctrl", "Control", "Alt", "Shift", "Win" }.Contains(p, StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown shortcut modifier in {value}.");
            if (modifiers.Length == 0 && key is not (>= Forms.Keys.F1 and <= Forms.Keys.F24))
                throw new ArgumentException($"Invalid shortcut: {value}. Only F1–F24 can be used without a modifier, so normal typing remains available.");
            bool Has(string modifier) => modifiers.Contains(modifier, StringComparer.OrdinalIgnoreCase);
            return new((uint)key, Has("Ctrl") || Has("Control"), Has("Alt"), Has("Shift"), Has("Win"));
        }
        internal bool Matches(uint key) => key == Key && Down(0x11) == Control && Down(0x12) == Alt && Down(0x10) == Shift && (Down(0x5b) || Down(0x5c)) == Win;
        private static bool Down(int key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
    }
}
