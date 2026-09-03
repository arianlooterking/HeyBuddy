using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Clicky.Core;
using Clicky.Windows;
using Clicky.Windows.Native;
using Clicky.Windows.Views;
using WpfTextBox = System.Windows.Controls.TextBox;

internal static class Program
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type RecorderType = typeof(MainWindow).Assembly.GetType("Clicky.Windows.ShortcutRecorder")!;

    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/settings-controls-smoke");
        Directory.CreateDirectory(output);
        var isolatedData = Path.Combine(output, "test-data-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CLICKY_DATA_DIR", isolatedData);
        var checks = new List<string>();
        var errors = new List<string>();
        var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown, Resources = LoadAppStyles() };
        MainWindow? window = null;
        AppServices? services = null;
        void Verify(string name, bool passed)
        {
            if (!passed)
                throw new InvalidOperationException(name);
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        try
        {
            var starts = 0;
            var ends = 0;
            var messages = new List<string>();
            var recorder = (WpfTextBox)Activator.CreateInstance(RecorderType, PrivateInstance, null,
                ["Ctrl+Alt+Space", new Func<bool>(() => { starts++; return true; }), new Action(() => ends++), new Action<string>(messages.Add)], null)!;
            bool Listening() => (bool)RecorderType.GetProperty("IsRecording", PrivateInstance)!.GetValue(recorder)!;
            void KeyDown(Key key, ModifierKeys modifiers) => Invoke(recorder, "RecordKeyDown", key, modifiers);
            void Release(ModifierKeys modifiers, Func<Key, bool>? held = null) => Invoke(recorder, "CompleteAfterRelease", modifiers, held ?? (_ => false));

            Verify("Shortcut controls are read-only and keyboard focusable", recorder.IsReadOnly && recorder.Focusable);
            KeyDown(Key.Enter, ModifierKeys.None);
            Verify("Enter activates recording and suspends callbacks exactly once", Listening() && starts == 1 && ends == 0);
            KeyDown(Key.LeftCtrl, ModifierKeys.Control);
            KeyDown(Key.F8, ModifierKeys.Control | ModifierKeys.Alt);
            Verify("Function-key combination displays while waiting for release", recorder.Text.Contains("Ctrl+Alt+F8", StringComparison.Ordinal) && Listening());
            Release(ModifierKeys.Control);
            Verify("Held modifier prevents shortcut restoration", Listening() && ends == 0);
            Release(ModifierKeys.None, key => key == Key.F8);
            Verify("Held trigger key prevents shortcut restoration", Listening() && ends == 0);
            Release(ModifierKeys.None);
            Verify("Release records valid binding and restores once", recorder.Text == "Ctrl+Alt+F8" && !Listening() && ends == 1);
            Release(ModifierKeys.None);
            Verify("Repeated release never restores twice", ends == 1);
            using (var parsed = new HotkeyManager(new AppSettings { TalkShortcut = recorder.Text }))
            {
            }
            Verify("Recorded function binding is accepted by actual HotkeyManager", true);

            Invoke(recorder, "BeginRecording");
            KeyDown(Key.A, ModifierKeys.None);
            Verify("Unmodified letters are rejected while recording remains active", Listening() && recorder.Text.Contains("modifier", StringComparison.Ordinal));
            KeyDown(Key.Escape, ModifierKeys.None);
            Release(ModifierKeys.None);
            Verify("Plain Escape cancels and preserves the previous binding", !Listening() && recorder.Text == "Ctrl+Alt+F8" && ends == 2);

            Invoke(recorder, "BeginRecording");
            KeyDown(Key.Escape, ModifierKeys.Control | ModifierKeys.Alt);
            Release(ModifierKeys.None);
            Verify("Modified Escape can be recorded for emergency stop", recorder.Text == "Ctrl+Alt+Escape" && !Listening());

            Invoke(recorder, "BeginRecording");
            KeyDown(Key.L, ModifierKeys.Windows);
            Verify("Windows lock combination is refused with an explanation", Listening() && messages[^1].Contains("reserved", StringComparison.Ordinal));
            Invoke(recorder, "CancelRecording");
            Release(ModifierKeys.None);
            Verify("Cancelling after a reserved binding keeps the previous shortcut", recorder.Text == "Ctrl+Alt+Escape" && !Listening());

            foreach (var key in new[] { Key.F1, Key.F8, Key.F24 })
            {
                Invoke(recorder, "BeginRecording");
                KeyDown(key, ModifierKeys.None);
                Release(ModifierKeys.None);
                using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = recorder.Text });
                Verify($"Bare function key {key} records and parses", recorder.Text == key.ToString() && !Listening());
            }
            Verify("Actual parser rejects bare letters", Refuses(() => { using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = "A" }); }));

            foreach (var key in new[] { Key.F1, Key.F24, Key.D7, Key.Home, Key.OemPlus, Key.PageDown })
            {
                Invoke(recorder, "BeginRecording");
                KeyDown(key, ModifierKeys.Control | ModifierKeys.Shift);
                Release(ModifierKeys.None);
                using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = recorder.Text });
                Verify($"Supported key {key} round-trips through the actual shortcut parser", !Listening() && recorder.Text.StartsWith("Ctrl+Shift+", StringComparison.Ordinal));
            }
            var previousBinding = recorder.Text;
            Invoke(recorder, "BeginRecording");
            KeyDown(Key.F9, ModifierKeys.Control);
            Invoke(recorder, "CancelRecording");
            Release(ModifierKeys.None);
            Verify("Losing focus or cancelling a pending capture restores its previous value", !Listening() && recorder.Text == previousBinding);

            Invoke(recorder, "BeginRecording");
            KeyDown(Key.F10, ModifierKeys.Control);
            // Simulate cancellation with a held chord, then a physical release after focus left.
            Invoke(recorder, "CancelWithKeyState", ModifierKeys.Control, new Func<Key, bool>(_ => true));
            Verify("Cancelled recording remains suspended while physical keys are still held", Listening());
            Release(ModifierKeys.None, _ => false);
            Verify("Physical release after focus loss restores callbacks and the previous binding", !Listening() && recorder.Text == previousBinding && starts == ends);

            services = new AppServices();
            services.Settings.OnboardingCompleted = true;
            services.Settings.CompanionEnabled = false;
            services.Settings.ModelDirectory = Path.Combine(isolatedData, "empty-models");
            services.Settings.RuntimeDirectory = Path.Combine(isolatedData, "empty-runtime");
            services.Settings.Save();
            window = new MainWindow(services);
            ((DispatcherTimer)typeof(MainWindow).GetField("foregroundTimer", PrivateInstance)!.GetValue(window)!).Stop();
            var companion = new CompanionWindow(services.Settings, () => { });
            typeof(MainWindow).GetField("companion", PrivateInstance)!.SetValue(window, companion);
            Invoke(window, "ShowSettings");
            var page = (StackPanel)window.FindName("PageContent");
            var fields = page.Children.OfType<WpfTextBox>().Where(control => RecorderType.IsInstanceOfType(control)).ToArray();
            Verify("Settings uses four genuine recorder controls", fields.Length == 4 && fields.Select(AutomationProperties.GetName).SequenceEqual(new[] { "Talk", "Dictate", "Open agent composer", "Emergency stop" }));
            Invoke(fields[0], "BeginRecording");
            Verify("Save refuses incomplete capture without starting global hooks", Refuses(() => Invoke(window, "ValidateShortcutSettings", services.Settings)) && typeof(MainWindow).GetField("hotkeys", PrivateInstance)!.GetValue(window) is null);
            var operation = (CancellationTokenSource)typeof(MainWindow).GetField("operation", PrivateInstance)!.GetValue(window)!;
            ((Task)Invoke(window, "HandleShortcut", ShortcutAction.EmergencyStop, HotkeyGesture.Pressed)!).GetAwaiter().GetResult();
            Verify("Already-queued global shortcut callbacks are ignored during recording", !operation.IsCancellationRequested);
            Invoke(fields[0], "CancelRecording");
            Invoke(fields[0], "CompleteAfterRelease", ModifierKeys.None, new Func<Key, bool>(_ => false));
            Verify("An unshown Settings fixture never installs hooks after recording", typeof(MainWindow).GetField("hotkeys", PrivateInstance)!.GetValue(window) is null);
            var duplicate = new AppSettings { TalkShortcut = "Control+Alt+D" };
            Verify("Save detects duplicate bindings including modifier aliases", Refuses(() => Invoke(window, "ValidateShortcutSettings", duplicate)));
            var reserved = new AppSettings { TalkShortcut = "Win+L" };
            Verify("Save rejects reserved Windows combinations", Refuses(() => Invoke(window, "ValidateShortcutSettings", reserved)));
            Invoke(window, "ValidateShortcutSettings", services.Settings);

            var color = page.Children.OfType<WpfTextBox>().Single(control => AutomationProperties.GetName(control) == "Companion color");
            color.Text = "#19A7C2";
            var mascot = (Canvas)((StackPanel)companion.Content).Children[0];
            var pointer = (System.Windows.Shapes.Path)mascot.Children[0];
            Verify("Arbitrary RGB color immediately persists and updates the companion", services.Settings.CompanionColor == "#19A7C2" && AppSettings.Load().CompanionColor == "#19A7C2" && ((SolidColorBrush)pointer.Fill).Color == Color.FromRgb(25, 167, 194));
            color.Text = "#12";
            Verify("Partial or invalid hex preserves the last valid color", services.Settings.CompanionColor == "#19A7C2" && AppSettings.Load().CompanionColor == "#19A7C2");
            color.Text = "#19A7C2";
            Invoke(window, "ChooseCompanionColor", color, new Func<Color, Color?>(_ => null));
            Verify("Cancelling the palette does not change field, settings or disk", color.Text == "#19A7C2" && services.Settings.CompanionColor == "#19A7C2" && AppSettings.Load().CompanionColor == "#19A7C2");
            Invoke(window, "ChooseCompanionColor", color, new Func<Color, Color?>(_ => Color.FromRgb(201, 17, 133)));
            Verify("Confirmed palette choice shares immediate field and preview persistence", color.Text == "#C91185" && services.Settings.CompanionColor == "#C91185" && AppSettings.Load().CompanionColor == "#C91185" && ((SolidColorBrush)pointer.Fill).Color == Color.FromRgb(201, 17, 133));
            using (var dialog = (System.Windows.Forms.ColorDialog)typeof(MainWindow).GetMethod("CreateCompanionColorDialog", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Color.FromRgb(201, 17, 133)])!)
                Verify("Native palette opens its full custom RGB controls", dialog.FullOpen && dialog.AnyColor && dialog.AllowFullOpen && dialog.Color.R == 201 && dialog.Color.G == 17 && dialog.Color.B == 133);
            Verify("Half-size preference remains unchanged", services.Settings.CompanionScale == .5 && mascot.LayoutTransform.Value.M11 == .5);
            RenderControls(fields, color, page, output);
            Verify("Recorder and palette controls render without showing a window", !application.Windows.OfType<Window>().Any(item => item.IsVisible) && !services.Speech.IsRecording && !services.Factory.ModelManager.GetStatus().Running);
        }
        catch (Exception error) { errors.Add(error.ToString()); Console.Error.WriteLine(error); }
        finally
        {
            window?.PrepareExit();
            services?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                passed = errors.Count == 0,
                checks,
                errors,
                isolatedData,
                windowsShown = false,
                nativeDialogShown = false,
                globalHooksStarted = false,
                keyboardInputSent = false,
                microphoneCapture = false,
                fullSaveButtonInvoked = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"{checks.Count} checks passed. Evidence: {output}");
        return errors.Count == 0 ? 0 : 1;
    }

    private static object? Invoke(object target, string name, params object[] arguments)
    {
        try
        {
            return target.GetType().GetMethod(name, PrivateInstance)!.Invoke(target, arguments);
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static ResourceDictionary LoadAppStyles()
    {
        // A plain Application cannot schedule the real app's tray, window or model startup.
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SettingsControls.AppStyles.xaml")!;
        var document = XDocument.Load(stream);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = document.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", xaml), resources.Nodes());
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private static bool Refuses(Action work)
    {
        try
        {
            work();
            return false;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return true; }
    }

    private static void RenderControls(WpfTextBox[] fields, WpfTextBox color, StackPanel page, string output)
    {
        var sample = new StackPanel { Background = Brushes.White, Width = 460, Margin = new(20) };
        sample.Children.Add(new TextBlock { Text = "Keyboard shortcuts", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 12) });
        foreach (var field in fields)
        {
            page.Children.Remove(field);
            sample.Children.Add(new TextBlock { Text = AutomationProperties.GetName(field), Margin = new(0, 8, 0, 4) });
            sample.Children.Add(field);
        }
        sample.Children.Add(new TextBlock { Text = "Companion color", Margin = new(0, 20, 0, 4) });
        page.Children.Remove(color);
        sample.Children.Add(color);
        var palette = page.Children.OfType<StackPanel>().Single(panel => panel.Children.OfType<System.Windows.Controls.Button>().Any(button => AutomationProperties.GetName(button) == "Choose companion color from full palette"));
        page.Children.Remove(palette);
        sample.Children.Add(palette);
        sample.Measure(new(500, double.PositiveInfinity));
        sample.Arrange(new Rect(sample.DesiredSize));
        sample.UpdateLayout();
        var bitmap = new RenderTargetBitmap(500, (int)Math.Ceiling(sample.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(sample);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, "controls.png"));
        encoder.Save(stream);
    }
}
