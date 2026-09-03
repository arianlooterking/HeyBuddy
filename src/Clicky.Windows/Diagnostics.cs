using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.Json;
using System.Diagnostics;
using Clicky.Core;

namespace Clicky.Windows;

public static class Diagnostics
{
    public static async Task RunAsync(string[] args, App application)
    {
        if (args.Contains("--settings-only"))
        {
            await SettingsDiagnostics.RunAsync(args, application);
            return;
        }
        var outputIndex = Array.IndexOf(args, "--output");
        var output = outputIndex >= 0 && outputIndex + 1 < args.Length ? Path.GetFullPath(args[outputIndex + 1]) : Path.Combine(Path.GetTempPath(), "ClickyUiChecks");
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", Path.Combine(output, "test-data"));
        var errors = new List<string>();
        var checks = new List<string>();
        var timings = new Dictionary<string, double>();
        try
        {
            await using var services = new AppServices();
            services.Settings.OnboardingCompleted = true;
            services.Settings.CompanionEnabled = false;
            services.Settings.SpeakReplies = false;
            services.Settings.RuntimeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClickyLocal", "Runtime");
            var window = new MainWindow(services);
            application.MainWindow = window;
            window.Show();
            await Task.Delay(700);
            foreach (var size in new[] { (Width: 1120, Height: 780, Name: "desktop"), (Width: 780, Height: 620, Name: "compact") })
            {
                window.Width = size.Width;
                window.Height = size.Height;
                window.UpdateLayout();
                await Task.Delay(200);
                foreach (var page in new[] { "chat", "apps", "settings", "connections", "knowledge", "models", "tasks", "history" })
                {
                    window.DiagnosticPage(page);
                    window.UpdateLayout();
                    await Task.Delay(page == "apps" ? 2500 : 100);
                    Render(window, Path.Combine(output, size.Name + "-" + page + ".png"));
                    checks.Add(size.Name + " " + page + " rendered");
                }
            }
            window.Width = 1120;
            window.Height = 780;
            window.DiagnosticPage("chat");
            if (args.Contains("--live"))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                var elapsed = Stopwatch.StartNew();
                await window.DiagnosticConversationAsync("Reply in Persian in one short sentence introducing yourself as HeyBuddy.");
                timings["uiConversationMs"] = elapsed.Elapsed.TotalMilliseconds;
                if (!services.Store.GetHistory(limit: 10).Any(h => h.Role == "assistant" && MainWindow.DetectLanguage(h.Text) == "fa"))
                    throw new InvalidOperationException("Live Persian response did not reach saved UI history.");
                checks.Add("Live local Persian response streamed, displayed RTL and persisted");
                Render(window, Path.Combine(output, "live-persian.png"));
                // Feed a generated sample through the actual recognition/model/synthesis pipeline.
                // This measures warm service latency without claiming a live microphone or room-acoustics test.
                var sample = await services.Speech.SynthesizeAsync("What is two plus two? Answer in a short sentence.", "en", timeout.Token);
                using var wav = new MemoryStream();
                using (var writer = new NAudio.Wave.WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wav), new NAudio.Wave.WaveFormat(sample.SampleRate, 16, 1)))
                    writer.Write(sample.Pcm, 0, sample.Pcm.Length);
                wav.Position = 0;
                await services.Speech.TranscribeWavAsync(wav, "en", cancellationToken: timeout.Token);
                wav.Position = 0;
                elapsed.Restart();
                var transcript = await services.Speech.TranscribeWavAsync(wav, "en", cancellationToken: timeout.Token);
                timings["warmRecognitionMs"] = elapsed.Elapsed.TotalMilliseconds;
                var modelStart = elapsed.Elapsed.TotalMilliseconds;
                var answer = await services.Provider().CompleteAsync(new([new("user", transcript)], MaxTokens: 96), null, timeout.Token);
                timings["warmVoiceModelMs"] = elapsed.Elapsed.TotalMilliseconds - modelStart;
                var synthStart = elapsed.Elapsed.TotalMilliseconds;
                var audio = await services.Speech.SynthesizeAsync(answer.Text, "en", timeout.Token);
                timings["warmSynthesisMs"] = elapsed.Elapsed.TotalMilliseconds - synthStart;
                timings["warmVoiceResponseAudioReadyMs"] = elapsed.Elapsed.TotalMilliseconds;
                if (audio.Pcm.Length < 1000 || string.IsNullOrWhiteSpace(transcript))
                    throw new InvalidOperationException("Voice pipeline returned no audio/text.");
                checks.Add("Generated English sample → local Whisper → Qwen → Piper PCM: " + transcript + " / " + answer.Text);
                var windowHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                var capturedWindow = await Task.Run(() => services.Capture.CaptureWindow(windowHandle), timeout.Token);
                elapsed.Restart();
                var preparedImage = Native.ImagePreparation.ForModel(capturedWindow.ToAttachment(), services.Settings.VisionMaxEdge);
                var screenReply = await services.Provider().CompleteAsync(new([new("user", "What is the app name shown in this screenshot? Name two visible buttons. Be brief.", [preparedImage])], MaxTokens: 128), null, timeout.Token);
                timings["nativeWindowAnalysisMs"] = elapsed.Elapsed.TotalMilliseconds;
                if (!screenReply.Text.Contains("HeyBuddy", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Actual native window vision check did not recognize the app: " + screenReply.Text);
                checks.Add("Actual WPF window capture analyzed locally: " + screenReply.Text);
                var task = await services.Agents.RunAsync("Create a plain text file named heybuddy-check.txt in the workspace containing exactly Hello from HeyBuddy. Then read that file to verify its content. Use the file tools.", services.Provider(), [services.Documents], cancellationToken: timeout.Token);
                if (task.Status != RunStatus.Completed || !File.Exists(Path.Combine(services.Settings.WorkDirectory, "heybuddy-check.txt")))
                    throw new InvalidOperationException("Live local agent file workflow failed: " + task.Result);
                if (!(await File.ReadAllTextAsync(Path.Combine(services.Settings.WorkDirectory, "heybuddy-check.txt"))).Contains("Hello from HeyBuddy"))
                    throw new InvalidOperationException("Generated file content differs from task.");
                checks.Add("Live local agent created and read a workspace document; " + task.Actions + " actions");
                window.DiagnosticPage("tasks");
                Render(window, Path.Combine(output, "live-agent.png"));
            }
            else
            {
                window.DiagnosticText("سلام. من HeyBuddy هستم و روی همین رایانه به شما کمک می‌کنم.");
                Render(window, Path.Combine(output, "persian-layout.png"));
                checks.Add("Persian fixture uses RTL");
            }
            window.PrepareExit();
        }
        catch (Exception error) { errors.Add(error.ToString()); }
        await File.WriteAllTextAsync(Path.Combine(output, "ui-result.json"), JsonSerializer.Serialize(new
        {
            Passed = errors.Count == 0,
            Live = args.Contains("--live"),
            Checks = checks,
            Timings = timings,
            Errors = errors
        }, new JsonSerializerOptions { WriteIndented = true }));
        application.Shutdown(errors.Count == 0 ? 0 : 1);
    }
    private static void Render(Window window, string file)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
