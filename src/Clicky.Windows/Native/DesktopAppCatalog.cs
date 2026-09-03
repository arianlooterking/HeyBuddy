using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Clicky.Windows.Native;

public sealed record DesktopApp(string Id, string Name, string Source, string Kind, string? Executable, string? AppUserModelId);
internal sealed record AppRegistration(DesktopApp App, string Fingerprint);

/// <summary>Only installed GUI application registrations become launchable IDs. Shortcut arguments are never executed.</summary>
public sealed class DesktopAppCatalog
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, AppRegistration> issued = new(StringComparer.Ordinal);
    private readonly Func<string, CancellationToken, IReadOnlyList<AppRegistration>> discover;
    public DesktopAppCatalog() : this(Discover) { }
    internal DesktopAppCatalog(Func<string, CancellationToken, IReadOnlyList<AppRegistration>> discover) => this.discover = discover;

    public async Task<IReadOnlyList<DesktopApp>> ListAsync(string? query = null, CancellationToken cancellationToken = default)
    {
        query = query?.Trim() ?? "";
        if (query.Length > 160)
            throw new ArgumentException("App search is limited to 160 characters.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var registrations = await StaAsync(() => discover(query, cancellationToken), cancellationToken);
            var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var matches = registrations.Where(entry => terms.All(term => SearchText(entry.App).Contains(term, StringComparison.OrdinalIgnoreCase)) && MatchesCommonApp(entry.App, query))
                .OrderByDescending(entry => entry.App.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
                .ThenBy(entry => entry.App.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.App.Id, StringComparer.Ordinal)
                .Take(100).ToArray();
            foreach (var registration in matches)
                issued[registration.App.Id] = registration;
            return matches.Select(entry => entry.App).ToArray();
        }
        finally { gate.Release(); }
    }

    internal async Task<AppRegistration> ResolveAsync(string id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!issued.TryGetValue(id, out var previous))
                throw new InvalidOperationException("Unknown app ID. Use desktop_apps and select a returned appId; app names and commands cannot be launched directly.");
            var current = (await StaAsync(() => discover(previous.App.Name, cancellationToken), cancellationToken)).SingleOrDefault(entry => entry.App.Id == id);
            if (current is null || current.Fingerprint != previous.Fingerprint)
            {
                issued.Remove(id);
                throw new InvalidOperationException("The app registration changed or was removed. Search desktop_apps again before launching.");
            }
            return current;
        }
        finally { gate.Release(); }
    }

    internal static string StableId(string kind, string target) => "app-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + target.ToUpperInvariant())))[..24].ToLowerInvariant();
    private static string SearchText(DesktopApp app)
    {
        var name = app.Executable is { } executable ? Path.GetFileNameWithoutExtension(executable) : app.AppUserModelId ?? "";
        var aliases = name.ToLowerInvariant() switch
        {
            "code" or "visual studio code" => "visual studio code vscode vs code editor",
            "msedge" or "microsoft edge" => "microsoft edge browser",
            "chrome" or "google chrome" => "google chrome browser",
            "firefox" or "mozilla firefox" => "mozilla firefox browser",
            "winword" => "microsoft word office",
            "excel" => "microsoft excel office",
            "powerpnt" => "microsoft powerpoint office",
            _ => name.Contains("Calculator", StringComparison.OrdinalIgnoreCase) ? "calculator calc" : name.Contains("Notepad", StringComparison.OrdinalIgnoreCase) ? "notepad" : ""
        };
        return app.Name + " " + name + " " + aliases;
    }
    internal static bool MatchesCommonApp(DesktopApp app, string query)
    {
        var executable = Path.GetFileNameWithoutExtension(app.Executable ?? "");
        return query.ToLowerInvariant() switch
        {
            "word" or "microsoft word" => executable.Equals("winword", StringComparison.OrdinalIgnoreCase) || app.Name.Equals("Word", StringComparison.OrdinalIgnoreCase) || app.Name.Equals("Microsoft Word", StringComparison.OrdinalIgnoreCase),
            "excel" or "microsoft excel" => executable.Equals("excel", StringComparison.OrdinalIgnoreCase) || app.Name.Equals("Excel", StringComparison.OrdinalIgnoreCase) || app.Name.Equals("Microsoft Excel", StringComparison.OrdinalIgnoreCase),
            "powerpoint" or "microsoft powerpoint" => executable.Equals("powerpnt", StringComparison.OrdinalIgnoreCase) || app.Name.Equals("PowerPoint", StringComparison.OrdinalIgnoreCase) || app.Name.Equals("Microsoft PowerPoint", StringComparison.OrdinalIgnoreCase),
            "notepad" => executable.Equals("notepad", StringComparison.OrdinalIgnoreCase) || (app.AppUserModelId?.Contains(".WindowsNotepad_", StringComparison.OrdinalIgnoreCase) ?? false) || app.Name.Equals("Notepad", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static IReadOnlyList<AppRegistration> Discover(string query, CancellationToken ct)
    {
        var found = new Dictionary<string, AppRegistration>(StringComparer.Ordinal);
        bool Relevant(string name) => (query.Length > 0 || found.Count < 100) && query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(term => SearchText(new("", name, "", "", name, null)).Contains(term, StringComparison.OrdinalIgnoreCase));
        void AddExecutable(string name, string path, string source)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
                // Package executables need their registered activation contract, not direct execution.
                if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                    return;
                var id = StableId("exe", path);
                if (found.ContainsKey(id))
                    return;
                if (!IsLaunchableExecutable(path, name))
                    return;
                var file = new FileInfo(path);
                found.TryAdd(id, new(new(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name, source, "desktop", path, null), $"{file.Length}:{file.LastWriteTimeUtc.Ticks}"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        object? shell = null, folder = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            items = ((dynamic)folder!).Items();
            for (var i = 0; i < (int)((dynamic)items).Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(i);
                    var name = (string)((dynamic)item).Name;
                    if (!Relevant(name))
                        continue;
                    string? aumid = Convert.ToString(((dynamic)item).ExtendedProperty("System.AppUserModel.ID"));
                    if (!string.IsNullOrEmpty(aumid) && Regex.IsMatch(aumid, @"^[A-Za-z0-9._-]+![A-Za-z0-9._-]+$"))
                    {
                        var id = StableId("aumid", aumid);
                        found.TryAdd(id, new(new(id, name, "Windows AppsFolder", "packaged", null, aumid), aumid));
                    }
                    else
                    {
                        string? target = Convert.ToString(((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath"));
                        string? arguments = Convert.ToString(((dynamic)item).ExtendedProperty("System.Link.Arguments"));
                        if (!string.IsNullOrEmpty(target) && string.IsNullOrWhiteSpace(arguments))
                            AddExecutable(name, target, "Windows AppsFolder");
                    }
                }
                catch (COMException) { }
                finally { Release(item); }
            }
        }
        catch (Exception error) when (error is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(items); Release(folder); Release(shell); }

        object? links = null;
        try
        {
            links = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            foreach (var location in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
            {
                var root = Environment.GetFolderPath(location);
                if (!Directory.Exists(root))
                    continue;
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var path in Directory.EnumerateFiles(root, "*.lnk", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Relevant(Path.GetFileNameWithoutExtension(path)))
                        continue;
                    object? shortcut = null;
                    try
                    {
                        shortcut = ((dynamic)links!).CreateShortcut(path);
                        if (string.IsNullOrWhiteSpace((string)((dynamic)shortcut).Arguments))
                            AddExecutable(Path.GetFileNameWithoutExtension(path), (string)((dynamic)shortcut).TargetPath, location == Environment.SpecialFolder.Programs ? "User Start menu" : "Shared Start menu");
                    }
                    catch (COMException) { }
                    finally { Release(shortcut); }
                }
            }
        }
        catch (Exception error) when (error is COMException or IOException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(links); }

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var paths = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    foreach (var name in paths?.GetSubKeyNames() ?? [])
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!Relevant(name))
                            continue;
                        using var key = paths!.OpenSubKey(name);
                        if (key?.GetValue(null) is not string path || !File.Exists(path.Trim('"')))
                            continue;
                        var title = FileVersionInfo.GetVersionInfo(path.Trim('"')).FileDescription ?? Path.GetFileNameWithoutExtension(name);
                        AddExecutable(title, path, hive == RegistryHive.CurrentUser ? "User App Paths" : "Machine App Paths");
                    }
                }
                catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException) { }
            }
        foreach (var (name, file) in new[] { ("Notepad", "notepad.exe"), ("Calculator", "calc.exe") })
            if (Relevant(name) && !found.Values.Any(entry => entry.App.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || (entry.App.AppUserModelId?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false)))
                AddExecutable(name, Path.Combine(Environment.SystemDirectory, file), "Windows system app");
        return found.Values.ToArray();
    }

    internal static bool IsLaunchableExecutable(string path, string name)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return false;
        var executable = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (new[] { "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "msiexec", "reg", "conhost", "openconsole", "wt", "windowsterminal" }.Contains(executable) || Regex.IsMatch(name + " " + executable, @"uninstall|unins\d|\bsetup\b|\binstaller\b", RegexOptions.IgnoreCase))
            return false;
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 256 || reader.ReadUInt16() != 0x5A4D)
            return false;
        stream.Position = 0x3C;
        var header = reader.ReadInt32();
        if (header < 64 || header + 94L > stream.Length)
            return false;
        stream.Position = header;
        if (reader.ReadUInt32() != 0x4550)
            return false;
        stream.Position = header + 24 + 68;
        return reader.ReadUInt16() == 2; // IMAGE_SUBSYSTEM_WINDOWS_GUI; never expose command interpreters or console tools.
    }
    internal static Task<T> StaAsync<T>(Func<T> work, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var result = work();
                ct.ThrowIfCancellationRequested();
                completion.TrySetResult(result);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception error) { completion.TrySetException(error); }
        })
        {
            IsBackground = true,
            Name = "HeyBuddy app catalog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(ct);
    }
    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
