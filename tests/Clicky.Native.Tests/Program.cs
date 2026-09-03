using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using Clicky.Core;
using Clicky.Windows.Native;
using Clicky.Windows.Speech;
using NAudio.Wave;
using Clicky.Windows.Views;
using Forms = System.Windows.Forms;

internal static class Program
{
    private static readonly List<object> Results = [];
    private static readonly List<object> Measurements = [];
    private static readonly string ArtifactDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/native"));
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--speech-diagnostics"))
            return SpeechDiagnosticsChecks.RunAsync(args.Contains("--baseline")).GetAwaiter().GetResult();
        if (args.Length > 1 && args[0] == "--fixture")
        {
            RunFixture(args[1]);
            return 0;
        }
        if (args.Contains("--render"))
        {
            RunSketchCheck().GetAwaiter().GetResult();
            RunImagePreparationChecks().GetAwaiter().GetResult();
            return 0;
        }
        try
        {
            return RunAsync(args.Contains("--speech")).GetAwaiter().GetResult();
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static void RunFixture(string title)
    {
        var form = new Forms.Form { Text = title, Width = 650, Height = 410, StartPosition = Forms.FormStartPosition.Manual, Location = new(120, 140), BackColor = System.Drawing.Color.FromArgb(245, 248, 252), TopMost = true };
        var heading = new Forms.Label { Text = "Clicky native test fixture", Location = new(24, 20), AutoSize = true };
        var text = new Forms.TextBox { Text = "Seed ", AccessibleName = "Dictation target", Location = new(24, 65), Width = 560 };
        var password = new Forms.TextBox { Text = "test-only", AccessibleName = "Password target", UseSystemPasswordChar = true, Location = new(24, 110), Width = 560 };
        var count = 0;
        var counter = new Forms.Label { Text = "Count: 0", AccessibleName = "Count: 0", Location = new(24, 220), AutoSize = true };
        var button = new Forms.Button { Text = "Increment counter", AccessibleName = "Increment counter", Location = new(24, 158), Width = 180, Height = 38 };
        button.Click += (_, _) => { count++; counter.Text = counter.AccessibleName = "Count: " + count; };
        form.Controls.AddRange([heading, text, password, button, counter]);
        form.Shown += (_, _) => { form.Activate(); text.Focus(); };
        Forms.Application.Run(form);
    }
    private static async Task RunSketchCheck()
    {
        Directory.CreateDirectory(ArtifactDirectory);
        await Sta(() =>
        {
            const int width = 1200, height = 800;
            var pixels = new byte[width * height * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 255;
                pixels[index + 1] = 248;
                pixels[index + 2] = 240;
                pixels[index + 3] = 255;
            }
            var original = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(original));
            using var png = new MemoryStream();
            encoder.Save(png);
            var capture = new ScreenCapture(Convert.ToBase64String(png.ToArray()), width, height, 3840, -505, "synthetic-test-display");
            var sketch = new SketchWindow(capture);
            var canvas = Descendant<System.Windows.Controls.InkCanvas>((System.Windows.DependencyObject)sketch.Content)!;
            var stroke = new System.Windows.Ink.Stroke(new System.Windows.Input.StylusPointCollection(new System.Windows.Input.StylusPoint[] { new(90, 90), new(450, 90), new(450, 260) }));
            stroke.DrawingAttributes = new System.Windows.Ink.DrawingAttributes { Color = System.Windows.Media.Colors.Magenta, Width = 14, Height = 14 };
            canvas.Strokes.Add(stroke);
            var result = sketch.RenderCapture();
            Assert(result.Width == width && result.Height == height && result.Left == 3840 && result.Top == -505 && result.MonitorId == capture.MonitorId, "Sketch export changed capture dimensions/origin.");
            using var renderedPng = new MemoryStream(Convert.FromBase64String(result.Base64));
            var rendered = new System.Windows.Media.Imaging.PngBitmapDecoder(renderedPng, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var renderedPixels = new byte[width * height * 4];
            rendered.CopyPixels(renderedPixels, width * 4, 0);
            var marked = (90 * width + 200) * 4;
            var clear = (700 * width + 1000) * 4;
            Assert(renderedPixels[marked + 2] > 180 && renderedPixels[marked + 1] < 120, "Ink stroke was not composited at its original pixel coordinates.");
            Assert(renderedPixels[clear] > 240 && renderedPixels[clear + 1] > 240, "Original screenshot pixels were lost.");
            File.WriteAllBytes(Path.Combine(ArtifactDirectory, "sketch-composition.png"), Convert.FromBase64String(result.Base64));
            File.WriteAllText(Path.Combine(ArtifactDirectory, "sketch-result.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                width,
                height,
                result.Left,
                result.Top,
                strokeVerified = true,
                originalPixelsVerified = true
            }));
            sketch.Close();
            return true;
        });
        Console.WriteLine("PASS Sketch composition keeps the original 1200x800 pixels, negative origin, and ink coordinates.");
    }
    private static T? Descendant<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        if (root is T match)
            return match;
        foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(root).OfType<System.Windows.DependencyObject>())
            if (Descendant<T>(child) is { } found)
                return found;
        return null;
    }
    private static async Task RunImagePreparationChecks()
    {
        await Sta(() =>
        {
            var checks = new List<object>();
            void Verify(string name, Action test)
            {
                test();
                checks.Add(new
                {
                    name,
                    passed = true
                });
                Console.WriteLine("PASS " + name);
            }
            ImageAttachment Sample(int width, int height, string name = "Named capture")
            {
                var pixels = new byte[width * height * 4];
                for (var index = 0; index < pixels.Length; index += 4)
                {
                    pixels[index] = 240;
                    pixels[index + 1] = 160;
                    pixels[index + 2] = 60;
                    pixels[index + 3] = 255;
                }
                var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                return new(Convert.ToBase64String(stream.ToArray()), "image/png", name);
            }
            (int Width, int Height) Dimensions(ImageAttachment image)
            {
                using var input = new MemoryStream(Convert.FromBase64String(image.Base64));
                var bitmap = new System.Windows.Media.Imaging.PngBitmapDecoder(input, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
                return (bitmap.PixelWidth, bitmap.PixelHeight);
            }
            Verify("Model image preparation bounds a 3840x2160 capture to 768x432", () =>
            {
                var original = Sample(3840, 2160, "Monitor with negative origin");
                var stopwatch = Stopwatch.StartNew();
                var prepared = ImagePreparation.ForModel(original);
                Assert(Dimensions(prepared) == (768, 432), "Landscape dimensions or aspect ratio changed incorrectly.");
                Assert(prepared.Name == original.Name && prepared.MimeType == "image/png", "Image name or PNG type was not preserved.");
                Assert(prepared.Base64 != original.Base64, "Oversized image was not resized.");
                checks.Add(new
                {
                    operation = "resize",
                    originalWidth = 3840,
                    originalHeight = 2160,
                    width = 768,
                    height = 432,
                    milliseconds = stopwatch.ElapsedMilliseconds
                });
            });
            Verify("Model image preparation preserves portrait aspect ratio", () =>
            {
                var prepared = ImagePreparation.ForModel(Sample(1000, 2000), 512);
                Assert(Dimensions(prepared) == (256, 512), "Portrait dimensions or aspect ratio changed incorrectly.");
            });
            Verify("Small and exact-limit model images are returned without upscaling or re-encoding", () =>
            {
                var small = Sample(320, 200);
                var exact = Sample(768, 512);
                Assert(ReferenceEquals(ImagePreparation.ForModel(small), small), "Small image was changed.");
                Assert(ReferenceEquals(ImagePreparation.ForModel(exact), exact), "Exact-limit image was changed.");
            });
            Verify("Odd image dimensions keep the maximum edge and aspect ratio within one pixel", () =>
            {
                var prepared = ImagePreparation.ForModel(Sample(1901, 1237));
                var size = Dimensions(prepared);
                Assert(size.Width == 768 && Math.Abs(size.Height - 1237 * (768d / 1901)) <= .5, "Rounding distorted the expected image proportions.");
            });
            Verify("Malformed and oversized encoded images are rejected", () =>
            {
                try
                {
                    ImagePreparation.ForModel(new("not base64"));
                    throw new Exception("Malformed input accepted.");
                }
                catch (InvalidDataException) { }
                try
                {
                    ImagePreparation.ForModel(new(new string('A', ((ImagePreparation.MaximumFileBytes + 2) / 3) * 4 + 4)));
                    throw new Exception("Oversized encoded input accepted.");
                }
                catch (InvalidDataException) { }
                try
                {
                    ImagePreparation.ForModel(new(Convert.ToBase64String([1, 2, 3, 4])));
                    throw new Exception("Non-image data accepted.");
                }
                catch (InvalidDataException) { }
            });
            Verify("Invalid output dimensions are rejected before image decode", () =>
            {
                try
                {
                    ImagePreparation.ForModel(new("not decoded"), 0);
                    throw new Exception("Zero maximum edge accepted.");
                }
                catch (ArgumentOutOfRangeException) { }
                try
                {
                    ImagePreparation.ForModel(new("not decoded"), 4096);
                    throw new Exception("Unbounded maximum edge accepted.");
                }
                catch (ArgumentOutOfRangeException) { }
            });
            Verify("Decoded raster dimensions are checked before a compressed image is expanded", () =>
            {
                // A valid, highly compressed 1-bit PNG exposes its 36 MP dimensions without allocating a full raster in the test.
                using var packed = new MemoryStream();
                using (var compression = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.SmallestSize, true))
                {
                    var row = new byte[751];
                    for (var index = 0; index < 6000; index++)
                        compression.Write(row);
                }
                using var stream = new MemoryStream();
                using var writer = new BinaryWriter(stream);
                writer.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                using var header = new MemoryStream();
                using var headerWriter = new BinaryWriter(header);
                headerWriter.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(6000));
                headerWriter.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(6000));
                headerWriter.Write(new byte[] { 1, 0, 0, 0, 0 });
                headerWriter.Flush();
                PngChunk(writer, "IHDR", header.ToArray());
                PngChunk(writer, "IDAT", packed.ToArray());
                PngChunk(writer, "IEND", []);
                writer.Flush();
                try
                {
                    ImagePreparation.ForModel(new(Convert.ToBase64String(stream.ToArray())));
                    throw new Exception("Oversized decoded raster was accepted.");
                }
                catch (InvalidDataException exception) { Assert(exception.Message.Contains("decoded dimensions", StringComparison.OrdinalIgnoreCase), "The raster was not rejected by the decoded-dimension guard: " + exception.Message); }
            });
            File.WriteAllText(Path.Combine(ArtifactDirectory, "image-preparation-results.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                checks
            }, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        });
    }
    private static void PngChunk(BinaryWriter writer, string name, byte[] data)
    {
        var type = System.Text.Encoding.ASCII.GetBytes(name);
        writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(data.Length));
        writer.Write(type);
        writer.Write(data);
        var crc = uint.MaxValue;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        }
        writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(~crc));
    }
    private static async Task<int> RunAsync(bool includeSpeech)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        var settings = new AppSettings();
        var desktop = new WindowsDesktopTools();
        var captures = new ScreenCaptureService();
        Process? fixture = null;
        Process? secondFixture = null;
        Forms.IDataObject? originalClipboard = null;
        const string sentinel = "Clicky native test clipboard sentinel";
        try
        {
            await Check("DPAPI credential roundtrip, ciphertext, and deletion", () =>
            {
                var directory = Path.Combine(AppPaths.Root, "Runtime", "NativeTests", Guid.NewGuid().ToString("N"));
                var store = new DpapiCredentialStore(directory);
                store.Set("check", "synthetic credential");
                Assert(store.Get("check") == "synthetic credential", "Credential did not round-trip.");
                Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory).Single())).Contains("synthetic credential"), "Credential was stored as plaintext.");
                store.Delete("check");
                Assert(store.Get("check") is null, "Credential was not removed.");
                Directory.Delete(directory);
                return Task.CompletedTask;
            });
            await Check("Shortcut lifecycle and duplicate validation", () =>
            {
                return Sta(() =>
                {
                    using var manager = new HotkeyManager(settings);
                    manager.Start();
                    manager.Dispose();
                    var duplicate = new AppSettings { StopShortcut = settings.TalkShortcut };
                    try
                    {
                        using var invalid = new HotkeyManager(duplicate);
                        throw new Exception("Duplicate shortcut was accepted.");
                    }
                    catch (ArgumentException) { }
                    return true;
                });
            });
            await Check("No microphone access on construction", () =>
            {
                using var speech = new SpeechService(settings);
                Assert(!speech.IsRecording, "Speech service began recording without consent.");
                return Task.CompletedTask;
            });
            var title = "Clicky QA " + Guid.NewGuid().ToString("N");
            fixture = StartFixture(title);
            var window = await FindWindow(desktop, title);
            await Check("Inspect real accessible window and visible controls", async () =>
            {
                var result = await desktop.ExecuteAsync("desktop_snapshot", Args(new
                {
                    windowId = window.Id
                }), default);
                Assert(result.Success && result.Data is DesktopSnapshot snapshot && snapshot.Elements.Any(e => e.Name == "Increment counter") && snapshot.Elements.Any(e => e.Name == "Dictation target"), "Fixture controls were not found.");
            });
            await Check("Private window capture contains rendered content", () =>
            {
                var capture = captures.CaptureWindow((nint)window.Handle);
                Assert(capture.Width > 600 && capture.Height > 350, "Unexpected capture dimensions.");
                File.WriteAllBytes(Path.Combine(ArtifactDirectory, "fixture-window.png"), Convert.FromBase64String(capture.Base64));
                Assert(capture.Left == window.Left && capture.Top == window.Top, "Capture coordinates differ from physical window bounds.");
                return Task.CompletedTask;
            });
            await Check("Private capture preserves physical coordinates on the second monitor", async () =>
            {
                var secondary = captures.GetMonitors().FirstOrDefault(m => !m.IsPrimary);
                if (secondary is null)
                    throw new InvalidOperationException("Second monitor unavailable; mixed-monitor validation is incomplete.");
                SetWindowPos((nint)window.Handle, 0, secondary.Bounds.Left + 60, secondary.Bounds.Top + 60, 650, 410, 0x14);
                await Task.Delay(200);
                var relocated = desktop.ListWindows().Single(w => w.Id == window.Id);
                var capture = captures.CaptureWindow((nint)window.Handle);
                Assert(capture.Left == relocated.Left && capture.Top == relocated.Top, "Physical capture origin drifted between monitors.");
                Assert(capture.MonitorId == secondary.Id, "Capture was tagged with the wrong monitor.");
                File.WriteAllBytes(Path.Combine(ArtifactDirectory, "fixture-second-monitor.png"), Convert.FromBase64String(capture.Base64));
                Measurements.Add(new
                {
                    operation = "monitor-capture",
                    monitor = secondary.Id,
                    dpi = relocated.Dpi,
                    capture.Left,
                    capture.Top,
                    capture.Width,
                    capture.Height
                });
                SetWindowPos((nint)window.Handle, 0, 120, 140, 650, 410, 0x14);
                await Task.Delay(200);
            });
            await Check("Cancellation prevents native input", async () =>
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try
                {
                    await desktop.ExecuteAsync("desktop_key", Args(new
                    {
                        windowId = window.Id,
                        key = "Enter"
                    }), cancelled.Token);
                    throw new Exception("Cancelled action executed.");
                }
                catch (OperationCanceledException) { }
            });
            await Check("Unknown window IDs cannot target arbitrary handles", async () =>
            {
                var result = await desktop.ExecuteAsync("desktop_snapshot", Args(new
                {
                    windowId = "arbitrary"
                }), default);
                Assert(!result.Success, "Unknown window accepted.");
            });
            await Check("Password entry is refused", async () =>
            {
                Activate(window);
                var snapshot = desktop.Snapshot(window.Id);
                var password = snapshot.Elements.First(e => e.Password);
                var result = await desktop.ExecuteAsync("desktop_type", Args(new
                {
                    windowId = window.Id,
                    snapshotId = snapshot.SnapshotId,
                    elementId = password.Id,
                    text = "forbidden"
                }), default);
                Assert(!result.Success && result.Message.Contains("password", StringComparison.OrdinalIgnoreCase), "Password control was not specifically refused: " + result.Message);
            });
            await Check("Verified accessible text insertion", async () =>
            {
                Activate(window);
                var snapshot = desktop.Snapshot(window.Id);
                var target = snapshot.Elements.First(e => e.Name == "Dictation target");
                var result = await desktop.ExecuteAsync("desktop_type", Args(new
                {
                    windowId = window.Id,
                    snapshotId = snapshot.SnapshotId,
                    elementId = target.Id,
                    text = "verified"
                }), default);
                Assert(result.Success, result.Message);
                Assert(ReadText(window) == "Seed verified", "Accessible control text did not match.");
            });
            await Check("Physical click reaches only the verified control", async () =>
            {
                Activate(window);
                var snapshot = desktop.Snapshot(window.Id);
                var target = snapshot.Elements.First(e => e.Name == "Increment counter");
                var result = await desktop.ExecuteAsync("desktop_click", Args(new
                {
                    windowId = window.Id,
                    snapshotId = snapshot.SnapshotId,
                    elementId = target.Id
                }), default);
                Assert(result.Success, result.Message);
                var after = desktop.Snapshot(window.Id);
                Assert(after.Elements.Any(e => e.Name == "Count: 1"), "Click outcome was not visible in accessibility state.");
            });
            await Check("Stale snapshot cannot be reused for another action", async () =>
            {
                Activate(window);
                var old = desktop.Snapshot(window.Id);
                var target = old.Elements.First(e => e.Name == "Increment counter");
                desktop.Snapshot(window.Id);
                var result = await desktop.ExecuteAsync("desktop_click", Args(new
                {
                    windowId = window.Id,
                    snapshotId = old.SnapshotId,
                    elementId = target.Id
                }), default);
                Assert(!result.Success && result.Message.Contains("snapshot expired", StringComparison.OrdinalIgnoreCase), "Stale snapshot was not specifically refused: " + result.Message);
            });
            await Check("Focus changes refuse input into the previous window", async () =>
            {
                var otherTitle = "Clicky focus QA " + Guid.NewGuid().ToString("N");
                secondFixture = StartFixture(otherTitle);
                var other = await FindWindow(desktop, otherTitle);
                Activate(other);
                var result = await desktop.ExecuteAsync("desktop_key", Args(new
                {
                    windowId = window.Id,
                    key = "Enter"
                }), default);
                Assert(!result.Success && result.Message.Contains("Focus changed"), "Input was not refused after a focus change.");
                CloseFixture(secondFixture);
                secondFixture = null;
            });
            await Check("Dictation inserts Persian text and restores clipboard", async () =>
            {
                Activate(window);
                originalClipboard = await Sta(() =>
                {
                    var original = Forms.Clipboard.GetDataObject();
                    if (original is null)
                        return null;
                    var snapshot = new Forms.DataObject();
                    foreach (var format in original.GetFormats(false))
                        if (original.GetData(format, false) is { } value)
                            snapshot.SetData(format, false, value);
                    return (Forms.IDataObject)snapshot;
                });
                await Sta(() => { Forms.Clipboard.SetText(sentinel); return true; });
                var target = FindTextElement(window);
                target.SetFocus();
                await desktop.ExecuteAsync("desktop_key", Args(new
                {
                    windowId = window.Id,
                    key = "End"
                }), default);
                await DictationInserter.InsertAsync(" سلام", (nint)window.Handle);
                Assert(ReadText(window).EndsWith(" سلام", StringComparison.Ordinal), "Persian clipboard insertion failed.");
                await Task.Delay(100);
                Assert(await Sta(() => Forms.Clipboard.GetText()) == sentinel, "Clipboard did not return to the prior value.");
            });
            if (includeSpeech)
            {
                using var speech = new SpeechService(settings);
                speech.Measured += measured => Measurements.Add(measured);
                await Check("Selected audio output accepts PCM and cancellation stops playback", async () =>
                {
                    await speech.PlayPcmAsync(new byte[12000], 24000);
                    using var cancel = new CancellationTokenSource(150);
                    try
                    {
                        await speech.PlayPcmAsync(new byte[24000 * 2 * 3], 24000, cancel.Token);
                        throw new Exception("Playback ignored cancellation.");
                    }
                    catch (OperationCanceledException) { }
                });
                await Check("Speech assets pass SHA-256 verification", () => speech.InstallAsync(_ => { }));
                foreach (var sample in new[] { ("en", "Hello Arian. Clicky is running locally on Windows."), ("fa", "سلام. این برنامه روی کامپیوتر شما کار می کند."), ("tr", "Merhaba. Bu asistan Windows bilgisayarınızda çalışıyor.") })
                {
                    await Check("Real local speech synthesis and transcription: " + sample.Item1, async () =>
                    {
                        var timer = Stopwatch.StartNew();
                        var audio = await speech.SynthesizeAsync(sample.Item2, sample.Item1);
                        Assert(audio.Pcm.Length > audio.SampleRate, "Voice did not generate meaningful audio.");
                        Measurements.Add(new
                        {
                            operation = "synthesis",
                            language = sample.Item1,
                            milliseconds = timer.ElapsedMilliseconds,
                            audioSeconds = audio.Pcm.Length / (2d * audio.SampleRate)
                        });
                        using var wav = new MemoryStream();
                        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wav), new WaveFormat(audio.SampleRate, 16, 1)))
                            writer.Write(audio.Pcm, 0, audio.Pcm.Length);
                        wav.Position = 0;
                        var transcript = await speech.TranscribeWavAsync(wav, sample.Item1);
                        Assert(transcript.Length > 5, "Speech decoder returned no meaningful text.");
                        Measurements.Add(new
                        {
                            operation = "roundtrip",
                            language = sample.Item1,
                            input = sample.Item2,
                            transcript,
                            exact = transcript == sample.Item2
                        });
                        Console.WriteLine("  " + sample.Item1 + ": " + transcript);
                    });
                }
            }
        }
        finally
        {
            await Sta(() => { if (Forms.Clipboard.ContainsText() && Forms.Clipboard.GetText() == sentinel) { if (originalClipboard is null) Forms.Clipboard.Clear(); else Forms.Clipboard.SetDataObject(originalClipboard, true, 3, 60); } return true; });
            if (fixture is not null)
                CloseFixture(fixture);
            if (secondFixture is not null)
                CloseFixture(secondFixture);
            var report = new
            {
                timestamp = DateTimeOffset.UtcNow,
                os = Environment.OSVersion.ToString(),
                runtime = Environment.Version.ToString(),
                displays = captures.GetMonitors(),
                tests = Results,
                measurements = Measurements,
                limitations = new[] { "Speech samples are synthetic, not a human microphone accuracy evaluation.", "Mixed-DPI interaction, human shortcut gestures, and unrelated application flows require separate validation." }
            };
            File.WriteAllText(Path.Combine(ArtifactDirectory, "results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Native checks complete. Results: {Path.Combine(ArtifactDirectory, "results.json")}");
        return Results.Any(result => JsonSerializer.SerializeToElement(result).GetProperty("passed").GetBoolean() == false) ? 1 : 0;
    }
    private static async Task Check(string name, Func<Task> body)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            await body();
            Results.Add(new
            {
                name,
                passed = true,
                milliseconds = timer.ElapsedMilliseconds
            });
            Console.WriteLine("PASS " + name);
        }
        catch (Exception exception) { Results.Add(new { name, passed = false, milliseconds = timer.ElapsedMilliseconds, error = exception.Message }); Console.WriteLine("FAIL " + name + ": " + exception.Message); }
    }
    private static Process StartFixture(string title)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--fixture");
        start.ArgumentList.Add(title);
        return Process.Start(start) ?? throw new InvalidOperationException("Fixture could not start.");
    }
    private static async Task<DesktopWindow> FindWindow(WindowsDesktopTools desktop, string title)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var window = desktop.ListWindows().FirstOrDefault(w => w.Title == title);
            if (window is not null)
                return window;
            await Task.Delay(100);
        }
        throw new InvalidOperationException("Own native fixture did not appear.");
    }
    private static void Activate(DesktopWindow window)
    {
        var hwnd = (nint)window.Handle;
        AutomationElement.FromHandle(hwnd).SetFocus();
        NativeMethods.SetForegroundWindow(hwnd);
        Thread.Sleep(100);
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            // Bootstrap only this test-created window with a physical title-bar click if Windows denies foreground stealing.
            NativeMethods.GetWindowRect(hwnd, out var rectangle);
            var point = new NativeMethods.Point(rectangle.Left + 140, rectangle.Top + 18);
            Assert(NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(point), 2) == hwnd, "Own test window is covered; cannot safely activate it.");
            NativeMethods.GetCursorPos(out var previous);
            NativeMethods.SetCursorPos(point.X, point.Y);
            NativeMethods.Send(NativeMethods.Mouse(0x2), NativeMethods.Mouse(0x4));
            Thread.Sleep(100);
            NativeMethods.GetCursorPos(out var after);
            if (after.X == point.X && after.Y == point.Y)
                NativeMethods.SetCursorPos(previous.X, previous.Y);
        }
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            NativeMethods.GetWindowThreadProcessId(NativeMethods.GetForegroundWindow(), out var foregroundPid);
            var owner = "unavailable";
            try
            {
                owner = Process.GetProcessById((int)foregroundPid).ProcessName;
            }
            catch (ArgumentException) { }
            throw new InvalidOperationException($"Own fixture focus not acquired; foreground belongs to {owner} ({foregroundPid}). Expected 0x{hwnd:X}, actual 0x{NativeMethods.GetForegroundWindow():X}.");
        }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    private static void CloseFixture(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(2000))
                    process.Kill();
            }
        }
        finally { process.Dispose(); }
    }
    private static AutomationElement FindTextElement(DesktopWindow window) => AutomationElement.FromHandle((nint)window.Handle).FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "Dictation target"));
    private static string ReadText(DesktopWindow window) => ((ValuePattern)FindTextElement(window).GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
    private static JsonElement Args(object value) => JsonSerializer.SerializeToElement(value);
    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
    private static Task<T> Sta<T>(Func<T> operation)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { done.SetResult(operation()); } catch (Exception exception) { done.SetException(exception); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
