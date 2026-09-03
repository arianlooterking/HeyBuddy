using System.Reflection;
using System.Runtime.InteropServices;
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
    private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
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
            string Transition(HotkeyGesture gesture, bool recording, bool latched, double milliseconds) =>
                typeof(MainWindow).GetMethod("ResolveVoiceShortcutGesture", PrivateStatic)!
                    .Invoke(null, [gesture, recording, latched, TimeSpan.FromMilliseconds(milliseconds)])!.ToString()!;
            Verify("A quick voice shortcut press starts recording", Transition(HotkeyGesture.Pressed, false, false, 0) == "Start");
            Verify("A quick voice shortcut release keeps listening", Transition(HotkeyGesture.Released, true, false, 120) == "KeepListening");
            Verify("The next shortcut press finishes tap-to-toggle recording", Transition(HotkeyGesture.Pressed, true, true, 900) == "Finish");
            Verify("A held voice shortcut finishes when released", Transition(HotkeyGesture.Released, true, false, 700) == "Finish");
            Verify("A release after tap-to-toggle cannot stop recording twice", Transition(HotkeyGesture.Released, true, true, 1000) == "None");
            bool Capture(bool explicitlyEnabled, bool voiceTurn, bool contextualEnabled, ScreenTurnKind intent) =>
                (bool)typeof(MainWindow).GetMethod("ShouldCaptureScreen", PrivateStatic)!.Invoke(null, [explicitlyEnabled, voiceTurn, contextualEnabled, intent])!;
            Verify("Every Talk turn can include its focused app without the persistent Screen toggle", Capture(false, true, true, ScreenTurnKind.None));
            Verify("Typed location questions include the focused app", Capture(false, false, true, ScreenTurnKind.Locate));
            Verify("Ordinary typed questions do not capture a screen unexpectedly", !Capture(false, false, true, ScreenTurnKind.None));
            Verify("The explicit screen toggle always includes the selected screen source", Capture(true, false, false, ScreenTurnKind.None));
            var locateInstruction = (string)typeof(MainWindow).GetMethod("ScreenInstruction", PrivateStatic)!.Invoke(null, [ScreenTurnKind.Locate, false])!;
            Verify("Location requests require visual guidance without authorizing a click", locateInstruction.Contains("guidance block", StringComparison.Ordinal) && locateInstruction.Contains("Do not click", StringComparison.Ordinal));

            var starts = 0;
            var ends = 0;
            var messages = new List<string>();
            var recorder = (WpfTextBox)Activator.CreateInstance(RecorderType, PrivateInstance, null,
                ["Ctrl+Alt+Space", new Func<bool>(() => { starts++; return true; }), new Action(() => ends++), new Action<string>(messages.Add)], null)!;
            bool Listening() => (bool)RecorderType.GetProperty("IsRecording", PrivateInstance)!.GetValue(recorder)!;
            bool KeyDown(Key key, ModifierKeys modifiers) => (bool)Invoke(recorder, "RecordKeyDown", key, modifiers)!;
            bool KeyUp(Key key, ModifierKeys modifiers, Func<Key, bool>? held = null) => (bool)Invoke(recorder, "RecordKeyUp", key, modifiers, held ?? (_ => false))!;
            void Release(ModifierKeys modifiers, Func<Key, bool>? held = null) => Invoke(recorder, "CompleteAfterRelease", modifiers, held ?? (_ => false));
            static AppSettings IsolatedShortcut(string talk) => new() { TalkShortcut = talk, DictationShortcut = "F21", AgentShortcut = "F22", StopShortcut = "F23" };

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

            foreach (var (key, modifier, expected) in new[]
            {
                (Key.LeftShift, ModifierKeys.Shift, "Left Shift"),
                (Key.RightShift, ModifierKeys.Shift, "Right Shift"),
                (Key.LeftCtrl, ModifierKeys.Control, "Left Ctrl"),
                (Key.RightCtrl, ModifierKeys.Control, "Right Ctrl"),
                (Key.LeftAlt, ModifierKeys.Alt, "Left Alt"),
                (Key.RightAlt, ModifierKeys.Alt, "Right Alt")
            })
            {
                Invoke(recorder, "BeginRecording");
                KeyDown(key, modifier);
                Verify($"{expected} remains a candidate until its physical release", Listening() && recorder.Text.EndsWith('…'));
                KeyUp(key, ModifierKeys.None);
                using var parsed = new HotkeyManager(IsolatedShortcut(recorder.Text));
                Verify($"{expected} records and parses as its own button", recorder.Text == expected && !Listening());
            }
            Invoke(recorder, "BeginRecording");
            KeyDown(Key.LeftCtrl, ModifierKeys.Control);
            KeyDown(Key.RightAlt, ModifierKeys.Control | ModifierKeys.Alt);
            Verify("AltGr is presented as the exact Right Alt button", recorder.Text == "Right Alt…");
            KeyUp(Key.RightAlt, ModifierKeys.Control, key => key == Key.LeftCtrl);
            Verify("Right Alt waits for AltGr's synthetic Control release", Listening());
            KeyUp(Key.LeftCtrl, ModifierKeys.None);
            using (var parsedAltGr = new HotkeyManager(IsolatedShortcut(recorder.Text)))
            {
            }
            Verify("Right Alt records from the AltGr event sequence", recorder.Text == "Right Alt" && !Listening());
            var modifierBinding = recorder.Text;
            var escapeHandled = KeyDown(Key.Escape, ModifierKeys.None);
            Verify("Plain Escape outside recording is not intercepted", !escapeHandled && !Listening() && recorder.Text == modifierBinding);

            Invoke(recorder, "BeginRecording");
            escapeHandled = KeyDown(Key.Escape, ModifierKeys.None);
            Verify("Plain Escape cancels and preserves the previous binding", !Listening() && recorder.Text == modifierBinding && starts == ends);
            Verify("Plain Escape is consumed instead of becoming a shortcut", escapeHandled);

            Invoke(recorder, "BeginRecording");
            var tabHandled = KeyDown(Key.Tab, ModifierKeys.None);
            Verify("Plain Tab cancels, preserves the binding and remains available for focus navigation", !tabHandled && !Listening() && recorder.Text == modifierBinding);

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

            foreach (var (key, expected) in new[]
            {
                (Key.A, "A"),
                (Key.D7, "D7"),
                (Key.Space, "Space"),
                (Key.Enter, "Enter"),
                (Key.Home, "Home"),
                (Key.PageDown, "Next"),
                (Key.Left, "Left"),
                (Key.OemPlus, "Oemplus")
            })
            {
                Invoke(recorder, "BeginRecording");
                KeyDown(key, ModifierKeys.None);
                Release(ModifierKeys.None);
                using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = recorder.Text });
                Verify($"Bare {expected} records and parses", recorder.Text.Equals(expected, StringComparison.OrdinalIgnoreCase) && !Listening());
            }
            Verify("Actual parser rejects generic modifiers and standalone Windows keys", new[] { "ShiftKey", "ControlKey", "Menu", "LWin", "RWin" }
                .All(binding => Refuses(() => { using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = binding }); })));
            Verify("Actual parser rejects mouse and synthetic pseudo-keys", new[] { "LButton", "RButton", "MButton", "XButton1", "XButton2", "ProcessKey", "Packet" }
                .All(binding => Refuses(() => { using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = binding }); })));
            Verify("Actual parser reserves plain Escape for recorder cancellation", Refuses(() => { using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = "Escape" }); }));
            Verify("Actual parser reserves plain Tab for focus navigation", Refuses(() => { using var parsed = new HotkeyManager(new AppSettings { TalkShortcut = "Tab" }); }));
            foreach (var (binding, exactKey, oppositeKey) in new[]
            {
                ("Left Shift", System.Windows.Forms.Keys.LShiftKey, System.Windows.Forms.Keys.RShiftKey),
                ("Right Shift", System.Windows.Forms.Keys.RShiftKey, System.Windows.Forms.Keys.LShiftKey),
                ("Left Ctrl", System.Windows.Forms.Keys.LControlKey, System.Windows.Forms.Keys.RControlKey),
                ("Right Ctrl", System.Windows.Forms.Keys.RControlKey, System.Windows.Forms.Keys.LControlKey),
                ("Left Alt", System.Windows.Forms.Keys.LMenu, System.Windows.Forms.Keys.RMenu),
                ("Right Alt", System.Windows.Forms.Keys.RMenu, System.Windows.Forms.Keys.LMenu)
            })
            {
                using var modifierManager = new HotkeyManager(IsolatedShortcut(binding));
                var pressed = 0;
                var released = 0;
                modifierManager.ActionInvoked += (_, gesture) =>
                {
                    if (gesture == HotkeyGesture.Pressed)
                        Interlocked.Increment(ref pressed);
                    if (gesture == HotkeyGesture.Released)
                        Interlocked.Increment(ref released);
                };
                var nativeMethods = typeof(MainWindow).Assembly.GetType("Clicky.Windows.Native.NativeMethods")!;
                var hookDataType = nativeMethods.GetNestedType("KeyboardHookData", BindingFlags.NonPublic)!;
                var hookData = Activator.CreateInstance(hookDataType)!;
                var virtualKeyField = hookDataType.GetField("VkCode", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var hookDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(hookDataType));
                try
                {
                    virtualKeyField.SetValue(hookData, (uint)oppositeKey);
                    Marshal.StructureToPtr(hookData, hookDataPointer, false);
                    Invoke(modifierManager, "OnKeyboard", 0, (nint)0x100, hookDataPointer);
                    Invoke(modifierManager, "OnKeyboard", 0, (nint)0x101, hookDataPointer);
                    Thread.Sleep(100);
                    Verify($"{oppositeKey} does not trigger the {binding} preference", pressed == 0 && released == 0);

                    virtualKeyField.SetValue(hookData, (uint)exactKey);
                    Marshal.StructureToPtr(hookData, hookDataPointer, false);
                    var downResult = (nint)Invoke(modifierManager, "OnKeyboard", 0, (nint)0x100, hookDataPointer)!;
                    SpinWait.SpinUntil(() => Volatile.Read(ref pressed) == 1, TimeSpan.FromSeconds(1));
                    var upResult = (nint)Invoke(modifierManager, "OnKeyboard", 0, (nint)0x101, hookDataPointer)!;
                    SpinWait.SpinUntil(() => Volatile.Read(ref released) == 1, TimeSpan.FromSeconds(1));
                    Verify($"{binding} is consumed and dispatches press and release", downResult == 1 && upResult == 1 && pressed == 1 && released == 1);
                }
                finally { Marshal.FreeHGlobal(hookDataPointer); }
            }
            using (var bareKeyManager = new HotkeyManager(new AppSettings { TalkShortcut = "A" }))
            {
                var invoked = 0;
                bareKeyManager.ActionInvoked += (_, _) => Interlocked.Increment(ref invoked);
                var physicalKeysDown = (HashSet<uint>)typeof(HotkeyManager).GetField("physicalKeysDown", PrivateInstance)!.GetValue(bareKeyManager)!;
                physicalKeysDown.Add((uint)System.Windows.Forms.Keys.A);
                var nativeMethods = typeof(MainWindow).Assembly.GetType("Clicky.Windows.Native.NativeMethods")!;
                var hookDataType = nativeMethods.GetNestedType("KeyboardHookData", BindingFlags.NonPublic)!;
                var hookData = Activator.CreateInstance(hookDataType)!;
                hookDataType.GetField("VkCode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hookData, (uint)System.Windows.Forms.Keys.A);
                var hookDataPointer = Marshal.AllocHGlobal(Marshal.SizeOf(hookDataType));
                try
                {
                    Marshal.StructureToPtr(hookData, hookDataPointer, false);
                    Invoke(bareKeyManager, "OnKeyboard", 0, (nint)0x100, hookDataPointer);
                    Thread.Sleep(100);
                    Verify("A repeat cannot become a shortcut after its unmatched first key-down", invoked == 0);
                    Invoke(bareKeyManager, "OnKeyboard", 0, (nint)0x101, hookDataPointer);
                }
                finally { Marshal.FreeHGlobal(hookDataPointer); }
            }

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
            Invoke(window, "OpenAgentComposer", false);
            Verify("Agent shortcut prepares a visible Agent-mode composer state", ((ComboBox)window.FindName("ModeSelector")).SelectedIndex == 1 && ((FrameworkElement)window.FindName("ChatPage")).Visibility == Visibility.Visible && ((TextBlock)window.FindName("StatusText")).Text.StartsWith("Agent composer ready", StringComparison.Ordinal));
            Invoke(window, "ShowSettings");
            var page = (StackPanel)window.FindName("PageContent");
            var screenOptions = page.Children.OfType<CheckBox>().Where(control =>
            {
                var label = control.Content is TextBlock textBlock ? textBlock.Text : control.Content?.ToString();
                return label?.Contains("focused app", StringComparison.OrdinalIgnoreCase) == true || label?.Contains("pointers", StringComparison.OrdinalIgnoreCase) == true;
            }).ToArray();
            Verify("Screen coaching defaults expose voice context, typed screen context, and visual guidance controls", screenOptions.Length == 3 && screenOptions.All(option => option.IsChecked == true));
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
            Invoke(fields[0], "BeginRecording");
            Invoke(fields[0], "RecordKeyDown", Key.F18, ModifierKeys.None);
            Invoke(fields[0], "CompleteAfterRelease", ModifierKeys.None, new Func<Key, bool>(_ => false));
            Verify("A completed shortcut saves immediately without the page Save button", fields[0].Text == "F18" && services.Settings.TalkShortcut == "F18" && AppSettings.Load().TalkShortcut == "F18");
            Verify("Immediate saving in an unshown fixture does not install global hooks", typeof(MainWindow).GetField("hotkeys", PrivateInstance)!.GetValue(window) is null);
            var duplicate = new AppSettings { TalkShortcut = "Control+Alt+D" };
            Verify("Save detects duplicate bindings including modifier aliases", Refuses(() => Invoke(window, "ValidateShortcutSettings", duplicate)));
            var duplicateBare = new AppSettings { TalkShortcut = "A", DictationShortcut = "a" };
            Verify("Save detects duplicate bare-key bindings", Refuses(() => Invoke(window, "ValidateShortcutSettings", duplicateBare)));
            var reserved = new AppSettings { TalkShortcut = "Win+L" };
            Verify("Save rejects reserved Windows combinations", Refuses(() => Invoke(window, "ValidateShortcutSettings", reserved)));
            var overlappingModifier = new AppSettings { TalkShortcut = "Left Ctrl", DictationShortcut = "Ctrl+D" };
            Verify("Save rejects a standalone modifier that would also start another shortcut", Refuses(() => Invoke(window, "ValidateShortcutSettings", overlappingModifier)));
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
            var listeningIndicator = mascot.Children.OfType<Canvas>().Single(control => control.Name == "ListeningIndicator");
            var listeningDisc = listeningIndicator.Children.OfType<System.Windows.Shapes.Ellipse>().ElementAt(1);
            var listeningBars = listeningIndicator.Children.OfType<System.Windows.Shapes.Rectangle>().ToArray();
            companion.SetListening(true);
            var restingHeights = listeningBars.Select(bar => bar.Height).ToArray();
            companion.SetAudioLevel(.1f);
            Verify("Listening replaces the pointer with a half-size live voice indicator", pointer.Visibility == Visibility.Collapsed && listeningIndicator.Visibility == Visibility.Visible && mascot.Width == 36 && mascot.Height == 36 && ((SolidColorBrush)listeningDisc.Fill).Color == Color.FromRgb(201, 17, 133));
            Verify("Real audio level expands the three voice bars", listeningBars.Zip(restingHeights).All(pair => pair.First.Height > pair.Second));
            companion.SetReply("This bubble must wait until listening stops.");
            Verify("Listening stays compact without a text bubble covering the cursor", ((Border)((StackPanel)companion.Content).Children[1]).Visibility == Visibility.Collapsed);
            companion.SetListening(false);
            Verify("Stopping listening restores the half-size pointer", pointer.Visibility == Visibility.Visible && listeningIndicator.Visibility == Visibility.Collapsed && mascot.Width == 48 && mascot.Height == 62);
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
