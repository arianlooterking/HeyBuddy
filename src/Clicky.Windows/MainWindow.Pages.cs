using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clicky.Core;
using Clicky.Connectors;
using Clicky.Runtime;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;

namespace Clicky.Windows;

public partial class MainWindow
{
    private TextBlock Note(string text, bool heading = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new(0, heading ? 18 : 5, 0, heading ? 10 : 12), Foreground = heading ? (Brush)FindResource("Ink") : (Brush)FindResource("Muted"), FontSize = heading ? 18 : 13, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
        PageContent.Children.Add(block);
        return block;
    }
    private WpfTextBox Field(string label, string value, bool multiline = false)
    {
        PageContent.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        var input = new WpfTextBox { Text = value, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden, MinHeight = multiline ? 115 : 36, MaxHeight = multiline ? 300 : 40 };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        PageContent.Children.Add(input);
        return input;
    }
    private WpfComboBox Choice(string label, IEnumerable<string> options, string current)
    {
        PageContent.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label") });
        var input = new WpfComboBox { ItemsSource = options.ToArray(), SelectedItem = current };
        if (input.SelectedIndex < 0)
            input.SelectedIndex = 0;
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        PageContent.Children.Add(input);
        return input;
    }
    private WpfCheckBox Check(string text, bool value)
    {
        var input = new WpfCheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 390 }, IsChecked = value };
        PageContent.Children.Add(input);
        return input;
    }
    private WpfButton ActionButton(string label, Func<Task> work, bool primary = false)
    {
        var button = new WpfButton { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 365 }, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new(0, 13, 0, 5) };
        if (primary)
            button.Style = (Style)FindResource("Primary");
        button.Click += async (_, _) => { button.IsEnabled = false; try { await Guard(work); } finally { button.IsEnabled = true; } };
        PageContent.Children.Add(button);
        return button;
    }
    private static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    private void ShowTasks()
    {
        PageContent.Children.Clear();
        var runs = app.Store.GetRuns().Where(r => r.Actions > 0 || r.Status != RunStatus.Completed).ToArray();
        if (runs.Length == 0)
        {
            Note("Give HeyBuddy a task from the conversation composer. You’ll see its progress and approval requests here.");
            return;
        }
        foreach (var run in runs)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = run.Prompt, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = $"{run.Status} · {run.Actions} actions · {run.UpdatedAt.ToLocalTime():g}", Foreground = (Brush)FindResource("Muted"), Margin = new(0, 7, 0, 9), FontSize = 12 });
            var detail = new WpfTextBox { Text = run.Result, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0), Background = Brushes.Transparent, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 0 };
            panel.Children.Add(detail);
            var row = new WrapPanel { Margin = new(0, 12, 0, 0) };
            var stop = new WpfButton { Content = "Stop task", IsEnabled = run.Status is RunStatus.Running or RunStatus.AwaitingApproval or RunStatus.Queued, Margin = new(0, 0, 8, 0) };
            stop.Click += (_, _) => app.Agents.Cancel(run.Id);
            row.Children.Add(stop);
            var follow = new WpfButton { Content = "Follow up / retry", Margin = new(0, 0, 8, 0) };
            follow.Click += (_, _) => { followUpId = run.Id; ShowPage("chat"); ModeSelector.SelectedIndex = 1; Composer.Text = "Continue this task, checking current state before any new action. "; Composer.Focus(); };
            row.Children.Add(follow);
            var inspect = new WpfButton { Content = "View steps" };
            inspect.Click += (_, _) => ShowHistory(run.Id);
            row.Children.Add(inspect);
            panel.Children.Add(row);
            PageContent.Children.Add(new Border { Child = panel, Background = Brushes.White, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1), CornerRadius = new(12), Padding = new(19), Margin = new(0, 0, 0, 14) });
        }
    }
    private void ShowHistory(string? selectedSession = null)
    {
        currentPage = "history";
        ChatPage.Visibility = Visibility.Collapsed;
        OtherScroll.Visibility = Visibility.Visible;
        PageTitle.Text = "History";
        PageContent.Children.Clear();
        var search = Field("Search saved text", "");
        var list = new StackPanel();
        PageContent.Children.Add(list);
        void Refresh()
        {
            list.Children.Clear();
            foreach (var entry in app.Store.GetHistory(search.Text, selectedSession, 200))
            {
                var row = new StackPanel { Margin = new(0, 18, 0, 4) };
                row.Children.Add(new TextBlock { Text = $"{entry.Kind} · {entry.Role} · {entry.CreatedAt.ToLocalTime():g}", FontSize = 12, Foreground = (Brush)FindResource("Muted") });
                var text = new WpfTextBox { Text = entry.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 190, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new(0, 7, 0, 0), FlowDirection = DetectLanguage(entry.Text) == "fa" ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight };
                row.Children.Add(text);
                list.Children.Add(row);
            }
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock { Text = "No saved entries match.", Margin = new(0, 20, 0, 0) });
        }
        search.TextChanged += (_, _) => Refresh();
        Refresh();
        ActionButton("Export history", async () => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Markdown|*.md", FileName = "heybuddy-history.md" }; if (dialog.ShowDialog(this) == true) { await File.WriteAllTextAsync(dialog.FileName, string.Join("\n\n---\n\n", app.Store.GetHistory(search.Text, selectedSession, 5000).Reverse().Select(h => $"## {h.Role} · {h.CreatedAt:g}\n\n{h.Text}"))); SetStatus("History exported."); } });
        ActionButton("Delete saved conversations and dictation", () => { if (System.Windows.MessageBox.Show(this, "Permanently delete saved conversation and dictation text? Task audit records will remain.", "Delete history", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { app.Store.DeleteHistory(); Refresh(); } return Task.CompletedTask; });
    }
    private void ShowKnowledge()
    {
        Note("Your profile and skills are plain Markdown files on this PC. Changes apply to the next request.");
        var profile = Field("My profile", app.Knowledge.ReadProfile(), true);
        profile.MinHeight = 170;
        ActionButton("Save profile", () => { app.Knowledge.SaveProfile(profile.Text); SetStatus("Profile saved locally."); return Task.CompletedTask; }, true);
        Note("Skills", true);
        var saved = app.Knowledge.GetSkills();
        var choices = Choice("Edit a skill", new[] { "Create a new skill" }.Concat(saved.Select(s => s.Name)), "Create a new skill");
        var name = Field("Skill name", "");
        var content = Field("Instructions", "", true);
        var enabled = Check("Use this skill in conversations and tasks", true);
        choices.SelectionChanged += (_, _) => { var skill = saved.FirstOrDefault(s => s.Name == choices.SelectedItem?.ToString()); name.Text = skill?.Name ?? ""; content.Text = skill?.Content ?? ""; enabled.IsChecked = skill?.Enabled ?? true; };
        ActionButton("Save skill", () => { app.Knowledge.SaveSkill(name.Text.Trim(), content.Text, enabled.IsChecked == true); ShowKnowledgePage(); return Task.CompletedTask; }, true);
        ActionButton("Open local knowledge folder", () => { OpenPath(AppPaths.Root); return Task.CompletedTask; });
    }
    private void ShowKnowledgePage()
    {
        PageContent.Children.Clear();
        ShowKnowledge();
        SetStatus("Skill saved.");
    }
    private void ShowModelSetup()
    {
        var state = app.Factory.ModelManager.GetStatus();
        Note("Qwen3.5 4B · Vision and conversation", true);
        Note(state.Installed ? "Installed. The local engine starts automatically when needed." : "Download the 4-bit model, vision projector and Windows GPU engine. About 4 GB of downloads; allow extra space for installation.");
        Note("Model files: " + app.Settings.ModelDirectory);
        var progress = new TextBlock { Text = state.Message, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 5, 0, 5) };
        PageContent.Children.Add(progress);
        ActionButton(state.Installed ? "Verify local AI installation" : "Install local AI", async () => { ResetOperation(); await app.Factory.ModelManager.InstallAsync(new Progress<DownloadProgress>(p => { progress.Text = $"{p.Stage} · {p.FileName} · {p.Percent:0}%"; }), operation.Token); progress.Text = "Verified and ready. Start a conversation to load the model."; app.Settings.OnboardingCompleted = true; app.Settings.Save(); }, true);
        ActionButton("Load and test local AI", async () => { ResetOperation(); var sw = Stopwatch.StartNew(); var old = app.Settings.Provider; app.Settings.Provider = "local"; try { var result = await app.Provider().CompleteAsync(new([new("user", "Reply with exactly: HeyBuddy is ready.")], MaxTokens: 32), null, operation.Token); progress.Text = $"{result.Text} · {sw.Elapsed.TotalSeconds:0.0}s including model startup"; } finally { app.Settings.Provider = old; } });
        ActionButton("Unload model and release GPU memory", async () => { await app.Factory.ModelManager.StopAsync(); progress.Text = "Model unloaded. It will load again when requested."; });
        ActionButton("Remove downloaded model files", async () => { if (System.Windows.MessageBox.Show(this, "Remove only HeyBuddy's Qwen model and projector? You can download them again. Conversation data is kept.", "Remove model", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) { await app.Factory.ModelManager.RemoveModelsAsync(); ShowPage("models"); } });
        Note("Local speech and voices", true);
        Note("Whisper recognizes speech locally. Piper reads replies aloud in English, Persian, and Turkish. Voice files are downloaded once; microphone recordings are not kept.");
        ActionButton("Install / verify speech and all three voices", async () => { ResetOperation(); SetStatus("Installing local speech… use Stop everything to cancel."); await app.Speech.InstallAsync(new Progress<string>(SetStatus), operation.Token); SetStatus("Speech files installed and verified."); }, true);
        var language = Choice("Preview language", ["en", "fa", "tr"], "en");
        ActionButton("Play local voice preview", async () => { ResetOperation(); var code = language.SelectedItem?.ToString() ?? "en"; await app.Speech.SpeakAsync(code switch { "fa" => "سلام. من اینجا هستم تا به شما کمک کنم.", "tr" => "Merhaba. Sana yardım etmek için buradayım.", _ => "Hello. I'm here to help you with what you're working on." }, code, operation.Token); });
        ActionButton("Open model folder", () => { Directory.CreateDirectory(app.Settings.ModelDirectory); OpenPath(app.Settings.ModelDirectory); return Task.CompletedTask; });
    }
    private void ShowSettings()
    {
        Note("AI provider", true);
        var provider = Choice("Use this provider", ["local", "compatible", "openai", "openai-realtime", "anthropic"], app.Settings.Provider);
        var endpoint = Field("Compatible server URL", app.Settings.Endpoint);
        var model = Field("Compatible model ID", app.Settings.Model);
        var cloudModel = Field("OpenAI model ID", app.Settings.CloudModel);
        var anthropicModel = Field("Anthropic model ID", app.Settings.AnthropicModel);
        Note("Enter your own provider model IDs. Optional cloud calls are charged by that provider. HeyBuddy never switches providers automatically.");
        PageContent.Children.Add(new TextBlock { Text = "API key for selected provider (leave blank to keep stored key)", Style = (Style)FindResource("Label") });
        var key = new PasswordBox();
        PageContent.Children.Add(key);
        key.IsEnabled = app.Settings.Provider != "local";
        provider.SelectionChanged += (_, _) => { key.Clear(); key.IsEnabled = provider.SelectedItem?.ToString() != "local"; };
        var cloudContent = Check("Allow screen and file content in cloud requests", app.Settings.CloudContentAllowed);
        Note("Voice and dictation", true);
        var speak = Check("Read replies aloud", app.Settings.SpeakReplies);
        var language = Choice("Recognition language", ["auto", "en", "fa", "tr"], app.Settings.Language);
        var voice = Choice("Voice", ["auto", "en_US-lessac-medium", "fa_IR-amir-medium", "tr_TR-dfki-medium"], app.Settings.Voice);
        var speed = Field("Speaking speed (0.5–2.0)", app.Settings.SpeechSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var microphones = Speech.SpeechService.GetMicrophones();
        var outputs = Speech.SpeechService.GetOutputDevices();
        var microphone = Choice("Microphone", microphones.Select(d => d.Id + " · " + d.Name), microphones.FirstOrDefault(d => d.Id == app.Settings.MicrophoneId) is { } mic ? mic.Id + " · " + mic.Name : "");
        var output = Choice("Audio output", outputs.Select(d => d.Id + " · " + d.Name), outputs.FirstOrDefault(d => d.Id == app.Settings.OutputDeviceId) is { } speaker ? speaker.Id + " · " + speaker.Name : "");
        Note("Microphone and recognition language changes are saved immediately. Test the microphone here before dictating into another application.");
        var microphoneResult = Note("Choose your microphone, click Test microphone, then speak while the input meter moves.");
        System.Windows.Automation.AutomationProperties.SetName(microphoneResult, "Microphone test result");
        ActionButton("Test microphone (8 seconds)", () => TestMicrophoneAsync(microphoneResult));
        var restoringInputSelection = false;
        microphone.SelectionChanged += (_, _) =>
        {
            if (restoringInputSelection || microphone.SelectedIndex < 0)
                return;
            var previous = app.Settings.MicrophoneId;
            if (recording || finishingRecording || microphoneTest || listeningLoop is not null)
            {
                restoringInputSelection = true;
                microphone.SelectedIndex = microphones.ToList().FindIndex(d => d.Id == previous);
                restoringInputSelection = false;
                SetStatus("Finish or stop recording before changing the microphone.");
                return;
            }
            try
            {
                app.Settings.MicrophoneId = microphones[microphone.SelectedIndex].Id;
                app.Settings.Save();
                microphoneResult.Text = "Microphone saved: " + microphones[microphone.SelectedIndex].Name + ". Run the test to check its input.";
                SetStatus("Microphone selection saved. The next recording uses this input.");
            }
            catch (Exception error) { app.Settings.MicrophoneId = previous; SetStatus("Could not save the microphone: " + error.Message); }
        };
        language.SelectionChanged += (_, _) =>
        {
            if (language.SelectedItem is not string selected)
                return;
            var previous = app.Settings.Language;
            try
            {
                app.Settings.Language = selected;
                app.Settings.Save();
                SetStatus("Recognition language saved for the next transcription.");
            }
            catch (Exception error) { app.Settings.Language = previous; SetStatus("Could not save the language: " + error.Message); }
        };
        var cleanup = Check("Clean up dictation with the selected AI provider", app.Settings.DictationCleanup);
        var dictionary = Field("Personal dictionary — one original = replacement per line", string.Join("\n", app.Settings.Dictionary.Select(p => p.Key + " = " + p.Value)), true);
        Note("Screen and companion", true);
        var capture = Choice("Share", ["window", "monitor", "region"], app.Settings.CaptureMode);
        var visionSize = Field("Screen/image quality: longest edge in pixels (384–1536)", app.Settings.VisionMaxEdge.ToString());
        Note("768 pixels keeps local screen analysis faster. Use a small selected region for fine text, or increase quality for detail. Drawings keep their original screen coordinates.");
        var monitors = app.Capture.GetMonitors();
        var monitor = Choice("Monitor", monitors.Select(m => m.Id), app.Settings.SelectedMonitor);
        var showCompanion = Check("Show cursor companion", app.Settings.CompanionEnabled);
        var reduced = Check("Reduce motion", app.Settings.ReducedMotion);
        var color = CompanionColorField();
        var companionScale = Field("Companion size (0.5–2.0)", app.Settings.CompanionScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Note("0.5 is half-size. Companion size updates and saves immediately; reply text stays readable. While listening, the arrow becomes a small voice meter that responds to the microphone.");
        companionScale.TextChanged += (_, _) =>
        {
            if (!double.TryParse(companionScale.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var scale) || !double.IsFinite(scale) || scale is < .5 or > 2 || scale == app.Settings.CompanionScale)
                return;
            var previous = app.Settings.CompanionScale;
            try
            {
                app.Settings.CompanionScale = scale;
                app.Settings.Save();
                companion?.ApplySettings();
                SetStatus($"Cursor size saved and applied: {scale:P0}.");
            }
            catch (Exception error) { app.Settings.CompanionScale = previous; SetStatus("Could not save the cursor size: " + error.Message); }
        };
        void RefreshCompanionScale(double scale) => companionScale.Text = scale.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (companion is not null)
        {
            companion.ScaleChanged += RefreshCompanionScale;
            companionScale.Unloaded += (_, _) => companion.ScaleChanged -= RefreshCompanionScale;
        }
        Note("Keyboard shortcuts", true);
        Note(ShortcutRecorder.Instructions + " Each action needs a different shortcut. A one-button shortcut replaces that button's normal use while HeyBuddy runs. A standalone modifier cannot also be used in another HeyBuddy combination. Windows keys and system-reserved combinations remain unavailable.");
        var talk = ShortcutField("Talk", app.Settings.TalkShortcut, settings => settings.TalkShortcut, (settings, value) => settings.TalkShortcut = value);
        var dictate = ShortcutField("Dictate", app.Settings.DictationShortcut, settings => settings.DictationShortcut, (settings, value) => settings.DictationShortcut = value);
        var agent = ShortcutField("Open agent composer", app.Settings.AgentShortcut, settings => settings.AgentShortcut, (settings, value) => settings.AgentShortcut = value);
        var stop = ShortcutField("Emergency stop", app.Settings.StopShortcut, settings => settings.StopShortcut, (settings, value) => settings.StopShortcut = value);
        Note("Storage and resources", true);
        var directory = Field("Model folder", app.Settings.ModelDirectory);
        var workspace = Field("Agent workspace", app.Settings.WorkDirectory);
        var threads = Field("CPU threads (1–12)", app.Settings.CpuThreads.ToString());
        var preload = Check("Load local AI when HeyBuddy starts", app.Settings.PreloadLocalModel);
        Note("Reduces the wait for your first AI reply. Uses your configured GPU and memory limits; app-opening commands work without loading AI.");
        var gpu = Field("GPU layers (0–24, reserved desktop headroom)", Math.Min(24, app.Settings.GpuLayers).ToString());
        var context = Field("Context tokens (2048–16384)", app.Settings.ContextSize.ToString());
        var gpuVision = Check("Use GPU for image processing", app.Settings.VisionProjectorGpu);
        Note("Faster screen analysis with additional GPU memory. CPU image processing remains available when other applications need that memory.");
        var retention = Field("History retention days (0 keeps history)", app.Settings.HistoryRetentionDays.ToString());
        var login = Check("Start HeyBuddy when I sign in", app.Settings.LaunchAtLogin);
        ActionButton("Save settings", async () =>
        {
            if (busy || app.Store.GetRuns().Any(r => r.Status is RunStatus.Running or RunStatus.AwaitingApproval))
                throw new InvalidOperationException("Finish or stop the active task before changing model and resource settings.");
            var parsedSpeed = double.Parse(speed.Text, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(parsedSpeed) || parsedSpeed is < 0.5 or > 2)
                throw new ArgumentException("Speaking speed must be from 0.5 to 2.0.");
            var parsedScale = double.Parse(companionScale.Text, System.Globalization.CultureInfo.InvariantCulture);
            if (!double.IsFinite(parsedScale) || parsedScale is < .5 or > 2)
                throw new ArgumentException("Companion size must be from 0.5 to 2.0.");
            var parsedThreads = int.Parse(threads.Text);
            var parsedGpu = int.Parse(gpu.Text);
            var parsedContext = int.Parse(context.Text);
            var parsedRetention = int.Parse(retention.Text);
            var parsedVisionSize = int.Parse(visionSize.Text);
            if (parsedVisionSize is < 384 or > 1536)
                throw new ArgumentException("Screen image size must be from 384 to 1536 pixels.");
            var parsedOutput = outputs[output.SelectedIndex].Id;
            var parsedDirectory = Path.GetFullPath(directory.Text);
            var parsedWorkspace = Path.GetFullPath(workspace.Text);
            if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out var parsedEndpoint) || parsedEndpoint.Scheme is not ("http" or "https") || (!parsedEndpoint.IsLoopback && parsedEndpoint.Scheme != "https"))
                throw new ArgumentException("Use a loopback HTTP URL or an HTTPS URL for the compatible server.");
            if (parsedThreads is < 1 or > 12 || parsedGpu is < 0 or > 24 || parsedContext is < 2048 or > 16384 || parsedRetention < 0)
                throw new ArgumentException("Use the resource ranges shown beside each field.");
            if (!TryCompanionColor(color.Text, out _))
                throw new ArgumentException("Choose a companion color from the palette or enter a complete hex color such as #386BFF.");
            var candidate = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(app.Settings))!;
            candidate.TalkShortcut = talk.Text;
            candidate.DictationShortcut = dictate.Text;
            candidate.AgentShortcut = agent.Text;
            candidate.StopShortcut = stop.Text;
            ValidateShortcutSettings(candidate);
            var parsedDictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in dictionary.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pair.Length != 2 || pair[0].Length == 0)
                    throw new ArgumentException("Each dictionary line must be original = replacement.");
                parsedDictionary[pair[0]] = pair[1];
            }
            startupLoading?.Cancel();
            await app.Factory.ModelManager.StopAsync();
            app.Settings.Provider = provider.SelectedItem?.ToString() ?? "local";
            app.Settings.Endpoint = endpoint.Text.Trim();
            app.Settings.Model = model.Text.Trim();
            app.Settings.CloudModel = cloudModel.Text.Trim();
            app.Settings.AnthropicModel = anthropicModel.Text.Trim();
            if (!string.IsNullOrWhiteSpace(key.Password))
                app.Credentials.Set("provider." + (app.Settings.Provider == "openai-realtime" ? "openai" : app.Settings.Provider), key.Password);
            key.Clear();
            app.Settings.CloudContentAllowed = cloudContent.IsChecked == true;
            app.Settings.SpeakReplies = speak.IsChecked == true;
            // Language, microphone, cursor size and color already save through their live handlers.
            // Keep their current values if they changed while model shutdown was awaited.
            app.Settings.Voice = voice.SelectedItem?.ToString() ?? "auto";
            app.Settings.SpeechSpeed = parsedSpeed;
            app.Settings.OutputDeviceId = parsedOutput;
            app.Settings.DictationCleanup = cleanup.IsChecked == true;
            app.Settings.Dictionary = parsedDictionary;
            app.Settings.CaptureMode = capture.SelectedItem?.ToString() ?? "window";
            app.Settings.SelectedMonitor = monitor.SelectedItem?.ToString() ?? "";
            app.Settings.CompanionEnabled = showCompanion.IsChecked == true;
            app.Settings.ReducedMotion = reduced.IsChecked == true;
            app.Settings.VisionMaxEdge = parsedVisionSize;
            app.Settings.TalkShortcut = candidate.TalkShortcut;
            app.Settings.DictationShortcut = candidate.DictationShortcut;
            app.Settings.AgentShortcut = candidate.AgentShortcut;
            app.Settings.StopShortcut = candidate.StopShortcut;
            app.Settings.ModelDirectory = parsedDirectory;
            app.Settings.WorkDirectory = parsedWorkspace;
            app.Settings.CpuThreads = parsedThreads;
            app.Settings.PreloadLocalModel = preload.IsChecked == true;
            app.Settings.GpuLayers = parsedGpu;
            app.Settings.ContextSize = parsedContext;
            app.Settings.HistoryRetentionDays = parsedRetention;
            app.Settings.VisionProjectorGpu = gpuVision.IsChecked == true;
            app.Settings.LaunchAtLogin = login.IsChecked == true;
            SetStartup(app.Settings.LaunchAtLogin);
            app.Settings.Save();
            RefreshControls();
            if (shortcutRecordersInProgress.Count == 0)
                StartHotkeys();
            companion?.ApplySettings();
            if (app.Settings.CompanionEnabled)
                companion?.Show();
            else
                companion?.Hide();
            SetStatus("Settings saved. Model resource changes take effect on its next load.");
            _ = PreloadModelAsync();
        }, true);
        ActionButton("Back up local data", () => { var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "SQLite backup|*.db", FileName = "heybuddy-backup.db" }; if (dialog.ShowDialog(this) == true) { app.Store.Backup(dialog.FileName); SetStatus("Database backed up. Memory and skills remain separately inspectable in the data folder."); } return Task.CompletedTask; });
        ActionButton("Open local data folder", () => { OpenPath(AppPaths.Root); return Task.CompletedTask; });
    }
    private static void SetStartup(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("ClickyLocal", "\"" + Environment.ProcessPath + "\"");
        else
            key.DeleteValue("ClickyLocal", false);
    }
    private void ShowConnections()
    {
        Note("A connection becomes verified after a real read test. Account services require authentication; public research does not. Account access and service limits remain yours to control.");
        foreach (var group in app.Connectors.Catalog.GroupBy(c => c.Group))
        {
            Note(group.Key, true);
            foreach (var entry in group)
            {
                var saved = app.Connectors.Configurations.FirstOrDefault(c => c.CatalogId == entry.Id);
                var grid = new Grid { Margin = new(0, 0, 0, 10) };
                grid.ColumnDefinitions.Add(new());
                grid.ColumnDefinitions.Add(new()
                {
                    Width = GridLength.Auto
                });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
                text.Children.Add(new TextBlock { Text = saved is null ? entry.Supported ? "Not configured · " + entry.Description : "Compatibility note · " + entry.Description : saved.LastVerifiedAt is not null ? app.Connectors.GetStatus(saved.Id) + " · Last verified " + saved.LastVerifiedAt.Value.ToLocalTime().ToString("g") + " · " + saved.Account : saved.LastTestMessage, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 5, 18, 0), TextWrapping = TextWrapping.Wrap });
                grid.Children.Add(text);
                var edit = new WpfButton { Content = saved is null ? entry.Supported ? "Set up" : "Details" : "Manage", VerticalAlignment = VerticalAlignment.Center };
                edit.Click += (_, _) => ShowConnectorEditor(entry, saved);
                Grid.SetColumn(edit, 1);
                grid.Children.Add(edit);
                PageContent.Children.Add(new Border { Child = grid, Background = Brushes.White, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(0, 0, 0, 1), Padding = new(16, 15, 16, 9) });
            }
        }
    }
    private void ShowConnectorEditor(ConnectorCatalogEntry entry, ConnectorConfiguration? existing)
    {
        PageContent.Children.Clear();
        PageTitle.Text = entry.Name;
        ActionButton("Back to all connections", () => { ShowPage("connections"); return Task.CompletedTask; });
        Note(entry.Description);
        Note(entry.SetupInstructions);
        ActionButton("Open official setup documentation", () => { OpenPath(entry.DocumentationUrl); return Task.CompletedTask; });
        if (!entry.Supported)
            return;
        var configuration = existing is null ? ConnectorConfiguration.FromCatalog(entry) : JsonSerializer.Deserialize<ConnectorConfiguration>(JsonSerializer.Serialize(existing))!;
        var name = Field("Connection name", configuration.Name);
        var transport = Choice("Transport", entry.Transport is ConnectorTransport.Http or ConnectorTransport.Stdio ? ["Http", "Stdio"] : [entry.Transport.ToString()], configuration.Transport.ToString());
        var endpoint = Field("Server URL", configuration.Endpoint);
        var auth = Choice("Authentication", ["None", "OAuth", "Bearer"], configuration.AuthMode.ToString());
        var clientId = Field("OAuth client ID, when required", configuration.ClientId);
        var scopes = Field("Scopes (one per line)", string.Join("\n", configuration.Scopes), true);
        var callbackPort = Field("OAuth callback port (49152–65535)", configuration.CallbackPort.ToString());
        PageContent.Children.Add(new TextBlock { Text = "Bearer token (leave blank to keep stored credential)", Style = (Style)FindResource("Label") });
        var secret = new PasswordBox();
        PageContent.Children.Add(secret);
        PageContent.Children.Add(new TextBlock { Text = "OAuth client secret, only if your provider requires it", Style = (Style)FindResource("Label") });
        var clientSecret = new PasswordBox();
        PageContent.Children.Add(clientSecret);
        var command = Field("Local executable (stdio connections)", configuration.Command);
        var arguments = Field("Executable arguments (one per line)", string.Join("\n", configuration.Arguments), true);
        var localPath = Field("Local folder / application path", configuration.LocalPath);
        var workingDirectory = Field("Local server working directory (optional)", configuration.WorkingDirectory);
        var enabled = Check("Enable this connection for agents", configuration.Enabled);
        Note("Tools from local executables can access your PC. Use only a reviewed server. Remote tool writes will require approval.");
        var resultText = Note(configuration.LastTestMessage);
        async Task Save()
        {
            configuration = app.Connectors.Configurations.FirstOrDefault(c => c.Id == configuration.Id) ?? configuration;
            if (!int.TryParse(callbackPort.Text, out var port) || port is < 49152 or > 65535)
                throw new ArgumentException("Choose a callback port from 49152 through 65535.");
            configuration.CallbackPort = port;
            configuration.WorkingDirectory = workingDirectory.Text.Trim();
            configuration.Name = name.Text.Trim();
            configuration.Transport = Enum.Parse<ConnectorTransport>(transport.SelectedItem!.ToString()!);
            configuration.Endpoint = endpoint.Text.Trim();
            configuration.AuthMode = Enum.Parse<ConnectorAuthMode>(auth.SelectedItem!.ToString()!);
            configuration.ClientId = clientId.Text.Trim();
            configuration.Scopes = scopes.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            configuration.Command = command.Text.Trim();
            configuration.Arguments = arguments.Text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            configuration.LocalPath = localPath.Text.Trim();
            configuration.Enabled = enabled.IsChecked == true;
            await app.Connectors.SaveAsync(configuration);
            if (!string.IsNullOrWhiteSpace(secret.Password))
            {
                app.Connectors.SetSecret(configuration.Id, "token", secret.Password);
                secret.Clear();
            }
            if (!string.IsNullOrWhiteSpace(clientSecret.Password))
            {
                app.Connectors.SetSecret(configuration.Id, "client-secret", clientSecret.Password);
                clientSecret.Clear();
            }
        }
        ActionButton("Save connection", async () => { await Save(); resultText.Text = "Saved. Test the connection to verify access."; }, true);
        ActionButton("Sign in with this service", async () => { await Save(); ResetOperation(); resultText.Text = "Complete the sign-in in your browser. Stop everything cancels."; var test = await app.Connectors.AuthorizeAsync(configuration.Id, operation.Token); resultText.Text = $"{test.Status}: {test.Message} · {test.ToolCount} tools"; });
        ActionButton("Test connection and read access", async () => { await Save(); ResetOperation(); resultText.Text = "Testing…"; var test = await app.Connectors.TestAsync(configuration.Id, operation.Token); resultText.Text = $"{test.Status}: {test.Message} · {test.ToolCount} tools"; });
        ActionButton("Tool permissions and local credentials", () => { var saved = app.Connectors.Configurations.FirstOrDefault(c => c.Id == configuration.Id) ?? throw new InvalidOperationException("Save this connection first."); ResetOperation(); new Views.ConnectorToolsWindow(app.Connectors, saved, operation.Token) { Owner = this }.ShowDialog(); configuration = app.Connectors.Configurations.First(c => c.Id == configuration.Id); return Task.CompletedTask; });
        ActionButton("Disconnect and remove saved authorization", async () => { if (System.Windows.MessageBox.Show(this, "Disconnect this account and remove HeyBuddy's saved credentials? Review the provider's account settings to revoke any remaining grants.", "Disconnect", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { await app.Connectors.RevokeAsync(configuration.Id); resultText.Text = "Disconnected; local authorization removed."; } });
    }
}
