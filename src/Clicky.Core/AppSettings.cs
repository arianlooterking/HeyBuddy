using System.Text.Json;

namespace Clicky.Core;

public sealed class AppSettings
{
    public string Provider { get; set; } = "local";
    public string ModelDirectory { get; set; } = Environment.GetEnvironmentVariable("CLICKY_MODEL_DIR") ?? Path.Combine(AppPaths.Root, "Models");
    public string RuntimeDirectory { get; set; } = Path.Combine(AppPaths.Root, "Runtime");
    public string Endpoint { get; set; } = "http://127.0.0.1:9000/v1";
    public string Model { get; set; } = "qwen3.5-4b";
    public string CloudModel { get; set; } = "";
    public string AnthropicModel { get; set; } = "";
    public int ContextSize { get; set; } = 8192;
    public int GpuLayers { get; set; } = 24;
    public bool VisionProjectorGpu
    {
        get; set;
    }
    public int CpuThreads { get; set; } = 6;
    public bool PreloadLocalModel { get; set; } = true;
    public bool CloudContentAllowed
    {
        get; set;
    }
    public bool SpeakReplies { get; set; } = true;
    public string Language { get; set; } = "auto";
    public string Voice { get; set; } = "auto";
    public double SpeechSpeed { get; set; } = 1.0;
    public int MicrophoneId { get; set; } = -1;
    public int OutputDeviceId { get; set; } = -1;
    public bool ContinuousListening
    {
        get; set;
    }
    public bool CaptureScreen
    {
        get; set;
    }
    public bool VoiceScreenContext { get; set; } = true;
    public bool ContextualScreenContext { get; set; } = true;
    public bool VisualGuidance { get; set; } = true;
    public string CaptureMode { get; set; } = "window";
    public string SelectedMonitor { get; set; } = "";
    public int VisionMaxEdge { get; set; } = 768;
    public bool CompanionEnabled { get; set; } = true;
    public bool ReducedMotion
    {
        get; set;
    }
    public string CompanionColor { get; set; } = "#386BFF";
    public double CompanionScale { get; set; } = 0.5;
    public bool CompanionDocked
    {
        get; set;
    }
    public string TalkShortcut { get; set; } = "Ctrl+Alt+Space";
    public string DictationShortcut { get; set; } = "Ctrl+Alt+D";
    public string AgentShortcut { get; set; } = "Ctrl+Alt+A";
    public string StopShortcut { get; set; } = "Ctrl+Alt+Escape";
    public bool LaunchAtLogin
    {
        get; set;
    }
    public bool DictationCleanup { get; set; } = true;
    public string WorkDirectory { get; set; } = Path.Combine(AppPaths.Root, "Workspace");
    public bool OnboardingCompleted
    {
        get; set;
    }
    public int HistoryRetentionDays { get; set; } = 90;
    public HashSet<string> FileContextSessions { get; set; } = [];
    public Dictionary<string, string> Dictionary { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.Settings)) ?? new();
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException) { throw new InvalidDataException("Settings could not be read. The file has been preserved; restore its backup or rename it to start with defaults."); }
    }
    public void Save()
    {
        AppPaths.Ensure();
        var temporary = AppPaths.Settings + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(AppPaths.Settings))
            File.Copy(AppPaths.Settings, AppPaths.Settings + ".bak", true);
        File.Move(temporary, AppPaths.Settings, true);
    }
}
public static class AppPaths
{
    public static string Root => Environment.GetEnvironmentVariable("CLICKY_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickyLocal");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Database => Path.Combine(Root, "clicky.db");
    public static string Memory => Path.Combine(Root, "Memory");
    public static string Skills => Path.Combine(Root, "Skills");
    public static void Ensure()
    {
        foreach (var path in new[] { Root, Memory, Skills, Path.Combine(Root, "Logs"), Path.Combine(Root, "Workspace"), Path.Combine(Root, "Backups") })
            Directory.CreateDirectory(path);
    }
}
