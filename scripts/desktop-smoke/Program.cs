using System.Text.Json;
using Clicky.Core;
using Clicky.Windows.Native;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/desktop-smoke");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var inventory = new Dictionary<string, IReadOnlyList<DesktopApp>>();
        void Verify(string name, bool pass)
        {
            if (!pass) throw new InvalidOperationException(name);
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        try
        {
            var tools = new WindowsDesktopTools();
            Verify("Discovery is read-only; launch/activate are LocalWrite", tools.Tools.Single(tool => tool.Name == "desktop_apps").Risk == RiskLevel.ReadOnly && tools.Tools.Where(tool => tool.Name is "desktop_launch" or "desktop_activate").All(tool => tool.Risk == RiskLevel.LocalWrite));
            Verify("All existing desktop input remains Sensitive", tools.Tools.Where(tool => tool.Name is "desktop_click" or "desktop_type" or "desktop_key" or "desktop_scroll").All(tool => tool.Risk == RiskLevel.Sensitive));
            var launchSchema = tools.Tools.Single(tool => tool.Name == "desktop_launch").InputSchema;
            Verify("Launch schema accepts only an app ID", launchSchema.GetProperty("properties").EnumerateObject().Select(item => item.Name).SequenceEqual(new[] { "appId" }) && !launchSchema.GetProperty("additionalProperties").GetBoolean());
            Verify("Catalog IDs remain stable across path case", DesktopAppCatalog.StableId("exe", @"C:\Apps\Editor.exe") == DesktopAppCatalog.StableId("exe", @"c:\apps\EDITOR.EXE"));

            var first = new AppRegistration(new("app-first", "Shared Name", "User Start menu", "desktop", @"C:\One\app.exe", null), "v1");
            var second = new AppRegistration(new("app-second", "Shared Name", "Machine App Paths", "desktop", @"C:\Two\app.exe", null), "v1");
            IReadOnlyList<AppRegistration> registered = [first, second];
            var catalog = new DesktopAppCatalog((_, _) => registered);
            var duplicates = await catalog.ListAsync("Shared Name");
            Verify("Duplicate app names stay distinct and require an explicit ID", duplicates.Count == 2 && duplicates.Select(app => app.Id).Distinct().Count() == 2);
            registered = [first with { Fingerprint = "v2" }, second];
            var stale = false;
            try { await catalog.ResolveAsync(first.App.Id, CancellationToken.None); }
            catch (InvalidOperationException) { stale = true; }
            Verify("Changed executable registration invalidates a previously issued ID", stale);
            await catalog.ListAsync("Shared Name");
            Verify("Rediscovery authorizes the updated registration", (await catalog.ResolveAsync(first.App.Id, CancellationToken.None)).Fingerprint == "v2");
            registered = [];
            var removed = false;
            try { await catalog.ResolveAsync(second.App.Id, CancellationToken.None); }
            catch (InvalidOperationException) { removed = true; }
            Verify("Removed app registrations cannot be launched", removed);

            var invalidLaunch = await tools.ExecuteAsync("desktop_launch", JsonSerializer.SerializeToElement(new { appId = "notepad.exe --arbitrary-command" }), CancellationToken.None);
            Verify("Unissued app IDs fail without launching", !invalidLaunch.Success);
            var invalidWindow = await tools.ActivateWindowAsync("unissued-window-id");
            Verify("Unknown window IDs fail without activation", !invalidWindow.Success);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var cancelledBeforeLaunch = false;
            try { await tools.ExecuteAsync("desktop_launch", JsonSerializer.SerializeToElement(new { appId = first.App.Id }), cancelled.Token); }
            catch (OperationCanceledException) { cancelledBeforeLaunch = true; }
            Verify("Pre-cancelled actions never reach app launch", cancelledBeforeLaunch);

            var pePath = Path.Combine(output, "fixture.exe");
            var pe = new byte[256];
            BitConverter.GetBytes((ushort)0x5A4D).CopyTo(pe, 0);
            BitConverter.GetBytes(128).CopyTo(pe, 0x3C);
            BitConverter.GetBytes(0x4550).CopyTo(pe, 128);
            BitConverter.GetBytes((ushort)2).CopyTo(pe, 128 + 24 + 68);
            await File.WriteAllBytesAsync(pePath, pe);
            Verify("Catalog accepts GUI executable metadata", DesktopAppCatalog.IsLaunchableExecutable(pePath, "Example application"));
            Verify("Uninstall/setup registrations are excluded", !DesktopAppCatalog.IsLaunchableExecutable(pePath, "Uninstall Example") && !DesktopAppCatalog.IsLaunchableExecutable(pePath, "Example Setup"));
            BitConverter.GetBytes((ushort)3).CopyTo(pe, 128 + 24 + 68);
            await File.WriteAllBytesAsync(pePath, pe);
            Verify("Console executables are excluded", !DesktopAppCatalog.IsLaunchableExecutable(pePath, "Console tool"));
            Verify("Unicode fallback emits text without clipboard or modifier keys", NativeMethods.Unicode('ش').Data.Keyboard is { Key: 0, Scan: 'ش', Flags: 4 } && NativeMethods.Unicode('ش', true).Data.Keyboard.Flags == 6);
            Verify("Word discovery does not substitute unrelated recovery tools", !DesktopAppCatalog.MatchesCommonApp(new("x", "Any Word Password Recovery", "Start menu", "desktop", @"C:\Tools\recovery.exe", null), "word"));

            foreach (var query in new[] { "Notepad", "Calculator", "Visual Studio Code", "Edge", "Telegram", "Word", "Excel", "PowerPoint" })
            {
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                inventory[query] = await tools.ListAppsAsync(query);
                Console.WriteLine($"CATALOG {query}: {inventory[query].Count} choices ({elapsed.Elapsed.TotalMilliseconds:0} ms)");
            }
            Verify("This PC exposes registered Notepad, Calculator, VS Code and Edge", new[] { "Notepad", "Calculator", "Visual Studio Code", "Edge" }.All(query => inventory[query].Count > 0));
            var repeated = await tools.ListAppsAsync("Notepad");
            Verify("Real catalog IDs persist across refresh", inventory["Notepad"].Select(app => app.Id).SequenceEqual(repeated.Select(app => app.Id)));
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = true, checks, inventory, applicationsLaunched = false, foregroundChanged = false, inputSent = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{checks.Count} checks passed. No apps launched; no desktop input sent.");
            return 0;
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { passed = false, checks, error = error.ToString(), inventory }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
