using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Clicky.Windows.Native;

internal static class NativeMethods
{
    internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint hwnd, int command);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetApplicationUserModelId(nint process, ref uint length, StringBuilder value);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int length);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] internal static extern bool PrintWindow(nint hwnd, nint deviceContext, uint flags);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsChild(nint parent, nint child);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern nint SetWindowsHookEx(int hook, HookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern nint GetModuleHandle(string? name);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int tokenClass, out int information, int length, out int resultLength);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X, Y; internal Point(int x, int y)
        {
            X = x;
            Y = y;
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookData
    {
        internal uint VkCode, ScanCode, Flags, Time; internal nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseHookData
    {
        internal Point Position; internal uint MouseData, Flags, Time; internal nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type; internal InputUnion Data;
    }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal MouseInput Mouse; [FieldOffset(0)] internal KeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y; internal uint MouseData, Flags, Time; internal nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort Key, Scan; internal uint Flags, Time; internal nint ExtraInfo;
    }
    internal static string Title(nint hwnd)
    {
        var builder = new StringBuilder(1024);
        GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
    internal sealed record WindowIdentity(nint Handle, int HostProcessId, long HostStarted, nint ContentHandle,
        int ContentProcessId, long ContentStarted, string Application, string? AppUserModelId)
    {
        internal bool IsHosted => Handle != ContentHandle;
    }
    internal static WindowIdentity ReadWindowIdentity(nint hwnd, bool allowMinimized = false)
    {
        RequireSafeWindow(hwnd, allowMinimized);
        GetWindowThreadProcessId(hwnd, out var hostPid);
        using var host = Process.GetProcessById((int)hostPid);
        var hostStarted = host.StartTime.ToUniversalTime().Ticks;
        var identity = new WindowIdentity(hwnd, (int)hostPid, hostStarted, hwnd, (int)hostPid,
            hostStarted, host.ProcessName, ProcessAppId(host));
        // UWP frames are owned by the Windows host; the app identity belongs to an actual
        // descendant window. Never infer it from the title, process name or activation PID alone.
        if (!host.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            return identity;
        if (!string.Equals(host.MainModule?.FileName, Path.Combine(Environment.SystemDirectory, "ApplicationFrameHost.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The application frame host identity could not be verified.");
        var candidates = new Dictionary<int, WindowIdentity>();
        var visited = 0;
        var truncated = false;
        Exception? failure = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            if (++visited > 256)
            {
                truncated = true;
                return false;
            }
            if (!IsWindowVisible(child) || !IsChild(hwnd, child) || GetAncestor(child, 2) != hwnd)
                return true;
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid == hostPid || candidates.ContainsKey((int)childPid))
                return true;
            try
            {
                RequireSafeProcess((int)childPid);
                using var content = Process.GetProcessById((int)childPid);
                var appId = ProcessAppId(content);
                if (string.IsNullOrWhiteSpace(appId))
                    throw new InvalidOperationException("The hosted application content has no verifiable application identity.");
                candidates[(int)childPid] = identity with
                {
                    ContentHandle = child,
                    ContentProcessId = (int)childPid,
                    ContentStarted = content.StartTime.ToUniversalTime().Ticks,
                    Application = content.ProcessName,
                    AppUserModelId = appId
                };
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or Win32Exception)
            {
                failure = error;
                return false;
            }
            return true;
        }, 0);
        if (failure is not null || truncated || candidates.Count != 1)
            throw new InvalidOperationException("The hosted application content is unavailable, protected or ambiguous. Inspect windows again.", failure);
        var result = candidates.Values.Single();
        // Enumeration is a snapshot. Recheck the exact host/child relationship before exposing it.
        GetWindowThreadProcessId(hwnd, out var currentHostPid);
        GetWindowThreadProcessId(result.ContentHandle, out var currentContentPid);
        if (!IsWindow(hwnd) || currentHostPid != hostPid || currentContentPid != result.ContentProcessId ||
            !IsChild(hwnd, result.ContentHandle) || GetAncestor(result.ContentHandle, 2) != hwnd)
            throw new InvalidOperationException("The hosted application window changed during inspection.");
        return result;
    }
    internal static void RequireWindowIdentity(WindowIdentity expected, bool allowMinimized = false)
    {
        if (ReadWindowIdentity(expected.Handle, allowMinimized) != expected)
            throw new InvalidOperationException("The target window or its hosted application was replaced. Inspect windows again.");
    }
    internal static void RequireSafeWindow(nint hwnd, bool allowMinimized = false)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || (!allowMinimized && IsIconic(hwnd)) || (DwmGetWindowAttribute(hwnd, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0))
            throw new InvalidOperationException("Target window is unavailable or minimized. Select the visible target again.");
        GetWindowThreadProcessId(hwnd, out var pid);
        RequireSafeProcess((int)pid);
    }
    internal static void RequireSafeProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            using var current = Process.GetCurrentProcess();
            if (process.SessionId != current.SessionId || process.ProcessName is "LockApp" or "LogonUI" or "winlogon")
                throw new InvalidOperationException("Protected or other-session processes cannot be controlled by HeyBuddy.");
            if (!OpenProcessToken(process.Handle, 8, out var token))
                throw new InvalidOperationException("Cannot verify the target's permissions. Select a normal, unelevated window.");
            try
            {
                if (!GetTokenInformation(token, 20, out var elevated, sizeof(int), out _) || elevated != 0)
                    throw new InvalidOperationException("Elevated or protected windows cannot be controlled by HeyBuddy.");
            }
            finally { CloseHandle(token); }
        }
        catch (Win32Exception) { throw new InvalidOperationException("Target window is protected; HeyBuddy cannot safely control it."); }
    }
    internal static void RequireInteractiveDesktop()
    {
        RequireSafeProcess(Environment.ProcessId);
        var foreground = GetForegroundWindow();
        if (foreground == 0)
            throw new InvalidOperationException("Windows has no interactive foreground. Unlock the desktop before opening or activating applications.");
        GetWindowThreadProcessId(foreground, out var pid);
        using var process = Process.GetProcessById((int)pid);
        if (process.ProcessName is "LockApp" or "LogonUI" or "winlogon")
            throw new InvalidOperationException("The desktop is locked. Unlock Windows before opening or activating applications.");
    }
    internal static string? ProcessAppId(Process process)
    {
        var value = new StringBuilder(256);
        uint length = (uint)value.Capacity;
        return GetApplicationUserModelId(process.Handle, ref length, value) == 0 ? value.ToString() : null;
    }
    internal static void RequireForeground(nint hwnd)
    {
        RequireSafeWindow(hwnd);
        if (GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("Focus changed. Bring the requested window to the foreground and retry deliberately.");
    }
    internal static void Send(params Input[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows refused input; no further action was attempted.");
    }
    internal static Input Key(ushort key, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0 } } };
    internal static Input Unicode(char value, bool up = false) => new() { Type = 1, Data = new() { Keyboard = new() { Scan = value, Flags = up ? 6u : 4u } } };
    internal static Input Mouse(uint flags, uint data = 0) => new() { Type = 0, Data = new() { Mouse = new() { Flags = flags, MouseData = data } } };
}
