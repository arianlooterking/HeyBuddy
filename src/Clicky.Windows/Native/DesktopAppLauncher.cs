using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Clicky.Windows.Native;

internal static class DesktopAppLauncher
{
    internal static int Start(DesktopApp app, CancellationToken ct)
    {
        NativeMethods.RequireInteractiveDesktop();
        ct.ThrowIfCancellationRequested();
        if (app.Executable is { } executable)
        {
            // CreateProcess with an exact catalog executable, no shell, arguments, URL or elevation verb.
            using var process = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)! });
            return process?.Id ?? throw new InvalidOperationException("Windows did not return a process for this application.");
        }
        var type = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), true)!;
        var activation = (IApplicationActivationManager)Activator.CreateInstance(type)!;
        try
        {
            ct.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(activation.ActivateApplication(app.AppUserModelId!, null, 2, out var processId));
            return checked((int)processId);
        }
        finally { Marshal.FinalReleaseComObject(activation); }
    }
    internal static bool Matches(DesktopApp app, int processId)
    {
        try
        {
            NativeMethods.RequireSafeProcess(processId);
            using var process = Process.GetProcessById(processId);
            return app.Executable is { } executable
                ? string.Equals(process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase)
                : string.Equals(NativeMethods.ProcessAppId(process), app.AppUserModelId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }
    internal static bool Matches(DesktopApp app, NativeMethods.WindowIdentity window)
    {
        try
        {
            NativeMethods.RequireWindowIdentity(window, allowMinimized: true);
            return Matches(app, window.ContentProcessId);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception) { return false; }
    }
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, uint options, out uint processId);
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint items, out uint processId);
    }
}
