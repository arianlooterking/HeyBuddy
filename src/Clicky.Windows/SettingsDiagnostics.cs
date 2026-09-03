using System.Text.Json;

namespace Clicky.Windows;

internal static class SettingsDiagnostics
{
    internal static async Task RunAsync(string[] args, App application)
    {
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(Path.GetTempPath(), "HeyBuddySettingsChecks");
        Directory.CreateDirectory(output);
        var data = Path.Combine(output, "test-data-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", data);
        var checks = new List<string>();
        var errors = new List<string>();
        try
        {
            await using var services = new AppServices();
            services.Settings.CompanionEnabled = false;
            services.Settings.OnboardingCompleted = true;
            services.Settings.ModelDirectory = Path.Combine(data, "Models");
            services.Settings.RuntimeDirectory = Path.Combine(data, "Runtime");
            var window = new MainWindow(services);
            try
            {
                window.DiagnosticSettingsSynchronization(checks);
                await window.DiagnosticRecordingOwnershipAsync(checks);
            }
            finally { window.PrepareExit(); }
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        await File.WriteAllTextAsync(Path.Combine(output, "settings-result.json"), JsonSerializer.Serialize(new
        {
            Passed = errors.Count == 0,
            Checks = checks,
            Errors = errors,
            IsolatedData = data,
            WindowsShown = false,
            MicrophoneCapture = false,
            FullSaveButtonInvoked = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        application.Shutdown(errors.Count == 0 ? 0 : 1);
    }
}
