using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Clicky.Core;
using Clicky.Windows.Native;
using Clicky.Windows.Views;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Clicky.Windows;

internal enum VoiceShortcutTransition
{
    None, Start, KeepListening, Finish
}

public partial class MainWindow : Window
{
    private readonly AppServices app;
    private readonly List<ChatMessage> conversation = [];
    private readonly List<string> attachments = [];
    private string sessionId = Guid.NewGuid().ToString("N");
    private string currentPage = "chat";
    private CancellationTokenSource operation = new();
    private CancellationTokenSource? listeningLoop;
    private bool recording, dictating, latching, exiting, busy;
    private DateTime recordingStarted;
    private int recordingMode;
    private nint targetWindow;
    private nint recordingTarget;
    private bool conversationContainsFiles;
    private HotkeyManager? hotkeys;
    private CompanionWindow? companion;
    private ActionCursorWindow? actionCursor;
    private GuidanceWindow? guidance;
    private ScreenCapture? pendingSketch;
    private bool voiceScreenContextPending;
    private nint voiceScreenTargetPending;
    private string? followUpId;
    private readonly DispatcherTimer foregroundTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow(AppServices services)
    {
        app = services;
        InitializeComponent();
        VersionLabel.Text = "HeyBuddy " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3);
        app.Agents.RequestApproval = RequestApproval;
        app.Speech.AudioLevel += UpdateMicrophoneLevel;
        app.Desktop.ActionVisual += ShowDesktopActionVisual;
        app.Speech.Error += SetStatus;
        app.Factory.ModelManager.StatusChanged += ModelStatusChanged;
        app.Agents.RunChanged += run => Dispatcher.BeginInvoke(() => { if (currentPage == "tasks") ShowTasks(); });
        Loaded += OnLoaded;
        foregroundTimer.Tick += (_, _) => RememberExternalWindow();
        foregroundTimer.Start();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var history = app.Store.GetHistory(limit: 100).Where(h => h.Kind == "chat").ToArray();
        if (history.Length > 0)
        {
            sessionId = history[0].SessionId;
            conversationContainsFiles = app.Settings.FileContextSessions.Contains(sessionId);
            foreach (var item in history.Where(h => h.SessionId == sessionId).Reverse())
            {
                AddMessage(item.Role, item.Text);
                conversation.Add(new(item.Role, item.Text));
            }
        }
        ScreenCheck.IsChecked = app.Settings.CaptureScreen;
        ConversationModeChanged(this, null!);
        RefreshControls();
        StartHotkeys();
        _ = PreloadModelAsync();
        companion = new CompanionWindow(app.Settings, ShowAndActivate);
        if (app.Settings.CompanionEnabled)
            companion.Show();
        if (!app.Settings.OnboardingCompleted)
        {
            ShowPage("models");
            SetStatus("Welcome. Install the local model and voices, then try a conversation.");
        }
    }
    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
        {
            ShowWindowAsync(handle, 9);
            SetForegroundWindow(handle);
        }
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            Activate();
            Composer.Focus();
            Keyboard.Focus(Composer);
            Composer.CaretIndex = Composer.Text.Length;
        }, DispatcherPriority.Input);
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint hwnd, int command);
    private void RememberExternalWindow()
    {
        var handle = HotkeyManager.GetForegroundWindow();
        if (handle == 0 || handle == new WindowInteropHelper(this).Handle)
            return;
        GetWindowThreadProcessId(handle, out var pid);
        if (pid != Environment.ProcessId)
            targetWindow = handle;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    private void StartHotkeys()
    {
        hotkeys?.Dispose();
        try
        {
            hotkeys = new(app.Settings);
            hotkeys.ActionInvoked += (action, gesture) => Dispatcher.BeginInvoke(async () => await HandleShortcut(action, gesture));
            hotkeys.PointerClicked += point => Dispatcher.BeginInvoke(() => guidance?.ObserveClick(point));
            hotkeys.Start();
        }
        catch (Exception error) { SetStatus("Shortcuts need attention: " + error.Message); }
    }
    private async Task HandleShortcut(ShortcutAction action, HotkeyGesture gesture)
    {
        if (shortcutRecordersInProgress.Count != 0)
            return;
        if (action == ShortcutAction.EmergencyStop)
        {
            StopAll();
            return;
        }
        if (action == ShortcutAction.Agent && gesture != HotkeyGesture.Released)
        {
            OpenAgentComposer();
            return;
        }
        if (action is not (ShortcutAction.Talk or ShortcutAction.Dictation))
            return;
        try
        {
            var transition = ResolveVoiceShortcutGesture(gesture, recording, latching, DateTime.UtcNow - recordingStarted);
            if (transition == VoiceShortcutTransition.Start)
                BeginRecording(action);
            else if (transition == VoiceShortcutTransition.Finish)
            {
                latching = false;
                await FinishRecording();
            }
            else if (transition == VoiceShortcutTransition.KeepListening)
            {
                latching = true;
                SetStatus(dictating
                    ? "Listening for dictation. Press the shortcut again to finish and insert it."
                    : "Listening. Press the shortcut again to finish and send.");
            }
        }
        catch (Exception error)
        {
            recording = false;
            latching = false;
            TalkButton.Content = "Talk";
            SetMicrophoneActive(false);
            SetStatus(error.Message);
        }
    }
    internal static VoiceShortcutTransition ResolveVoiceShortcutGesture(HotkeyGesture gesture, bool isRecording, bool isLatched, TimeSpan elapsed)
    {
        if (gesture is HotkeyGesture.Pressed or HotkeyGesture.DoubleTap)
            return !isRecording ? VoiceShortcutTransition.Start : isLatched ? VoiceShortcutTransition.Finish : VoiceShortcutTransition.None;
        if (gesture != HotkeyGesture.Released || !isRecording || isLatched)
            return VoiceShortcutTransition.None;
        return elapsed.TotalMilliseconds < 500 ? VoiceShortcutTransition.KeepListening : VoiceShortcutTransition.Finish;
    }
    private void OpenAgentComposer(bool activate = true)
    {
        ShowPage("chat");
        ModeSelector.SelectedIndex = 1;
        SetStatus("Agent composer ready. Type a task, then press Enter or choose Send.");
        if (activate)
            ShowAndActivate();
    }
    private void BeginRecording(ShortcutAction action)
    {
        var forDictation = action == ShortcutAction.Dictation;
        if (microphoneTest || finishingRecording)
        {
            SetStatus("Wait for the current microphone test or transcription to finish before recording again.");
            return;
        }
        if (listeningLoop is not null)
        {
            SetStatus("End hands-free voice before using a manual recording shortcut.");
            return;
        }
        if (busy)
        {
            SetStatus("Wait for this response or stop it before recording.");
            return;
        }
        RememberExternalWindow();
        recordingTarget = targetWindow;
        operation.Cancel();
        app.Speech.Stop();
        app.Speech.StartRecording(partial => Dispatcher.BeginInvoke(() => SetStatus(partial)));
        SetMicrophoneActive(true);
        recording = true;
        dictating = forDictation;
        recordingMode = ModeSelector.SelectedIndex == 2 ? 0 : ModeSelector.SelectedIndex;
        recordingStarted = DateTime.UtcNow;
        TalkButton.Content = "Finish";
        SetStatus(forDictation
            ? "Listening for dictation… tap again to insert, or keep holding and release."
            : "Listening… tap again to send, or keep holding and release.");
    }
    private async Task FinishRecording()
    {
        if (!recording)
            return;
        recording = false;
        finishingRecording = true;
        TalkButton.IsEnabled = false;
        TalkButton.Content = "Talk";
        var destination = recordingTarget;
        ResetOperation();
        var cancellationToken = operation.Token;
        try
        {
            SetStatus("Transcribing on your PC…");
            var text = await app.Speech.StopAndTranscribeAsync(partial => Dispatcher.BeginInvoke(() => SetStatus(partial)), cancellationToken);
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus(app.Speech.LastCaptureStatus?.Message ?? "No words were transcribed. Open Settings and run Test microphone to check the selected input.");
                return;
            }
            if (dictating)
            {
                text = await PrepareDictationAsync(text, cancellationToken);
                await InsertDictationAsync(text, destination, cancellationToken);
                SetStatus("Dictation delivered and saved to History.");
            }
            else
            {
                Composer.Text = text;
                ModeSelector.SelectedIndex = recordingMode;
                voiceScreenContextPending = app.Settings.VoiceScreenContext;
                voiceScreenTargetPending = destination;
                await SendAsync();
            }
        }
        catch (OperationCanceledException) { SetStatus("Stopped."); }
        catch (Exception error) { SetStatus(error.Message + " Your transcript is available in History if it was completed."); }
        finally
        {
            finishingRecording = false;
            latching = false;
            TalkButton.IsEnabled = true;
            SetMicrophoneActive(false);
            companion?.SetState("");
        }
    }
    private async void ToggleTalk(object sender, RoutedEventArgs e)
    {
        await Guard(async () =>
        {
            if (recording)
                await FinishRecording();
            else
                BeginRecording(ModeSelector.SelectedIndex == 2 ? ShortcutAction.Dictation : ShortcutAction.Talk);
        });
    }
    private async void SendMessage(object sender, RoutedEventArgs e) => await Guard(SendAsync);
    private async void ComposerKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            await Guard(SendAsync);
        }
    }
    private async Task<string> ReadAttachmentAsync(string path, Clicky.Runtime.DocumentTools tools, CancellationToken ct)
    {
        if (new FileInfo(path).Length > 30_000_000)
            throw new InvalidOperationException("Use documents smaller than 30 MB.");
        var imported = await tools.ImportAsync(path, ct);
        var budget = Math.Clamp((app.Settings.ContextSize - 2800) / Math.Max(2, attachments.Count * 2), 150, 1800);
        return "Local imported copy: " + imported.Path + "\n" + ContextBudget.ExcerptContext(imported.Text, budget);
    }
    private async Task<ScreenCapture?> CaptureContextAsync(nint expectedWindow = 0)
    {
        if (app.Settings.CaptureMode == "region")
        {
            var selector = new RegionSelectWindow();
            if (selector.ShowDialog() != true)
                throw new OperationCanceledException("Screen selection cancelled.");
            await Task.Delay(180, operation.Token);
            return await Task.Run(() => app.Capture.CaptureRegion(selector.Selection), operation.Token).WaitAsync(TimeSpan.FromSeconds(8), operation.Token);
        }
        var mode = app.Settings.CaptureMode;
        var monitor = app.Settings.SelectedMonitor;
        var expected = expectedWindow != 0 ? expectedWindow : targetWindow;
        return await Task.Run(() => mode == "monitor" ? app.Capture.CaptureMonitor(monitor) : expected != 0 ? app.Capture.CaptureWindow(expected) : throw new InvalidOperationException("Focus the application you want to share, then return to HeyBuddy or use its talk shortcut."), operation.Token).WaitAsync(TimeSpan.FromSeconds(8), operation.Token);
    }
    private WpfTextBox AddMessage(string role, string text)
    {
        Welcome.Visibility = Visibility.Collapsed;
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = role == "user" ? "You" : role == "notice" ? "Needs attention" : "HeyBuddy", FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 0, 0, 6) });
        var body = new WpfTextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(0), MinHeight = 0, FontSize = 15, FlowDirection = DetectLanguage(text) == "fa" ? System.Windows.FlowDirection.RightToLeft : System.Windows.FlowDirection.LeftToRight };
        panel.Children.Add(body);
        Messages.Children.Add(new Border { Child = panel, Padding = new(18, 15, 18, 15), CornerRadius = new(12), Background = new SolidColorBrush(role == "user" ? Color.FromRgb(232, 239, 255) : role == "notice" ? Color.FromRgb(255, 242, 222) : Colors.White), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(role == "user" ? 0 : 1), Margin = new(role == "user" ? 45 : 0, 0, role == "user" ? 0 : 20, 14) });
        ChatScroll.ScrollToEnd();
        return body;
    }
    public static string DetectLanguage(string text) => text.Any(c => c is >= '\u0600' and <= '\u06ff') ? "fa" : text.IndexOfAny(['ç', 'ğ', 'ı', 'İ', 'ö', 'ş', 'ü']) >= 0 ? "tr" : "en";
    private void ScreenChanged(object sender, RoutedEventArgs e)
    {
        if (app is null)
            return;
        app.Settings.CaptureScreen = ScreenCheck.IsChecked == true;
        app.Settings.Save();
    }
    private void NewConversation(object sender, RoutedEventArgs e)
    {
        StopAll();
        sessionId = Guid.NewGuid().ToString("N");
        conversation.Clear();
        conversationContainsFiles = false;
        followUpId = null;
        attachments.Clear();
        pendingSketch = null;
        UpdateAttachments();
        Messages.Children.Clear();
        AddMessage("assistant", "What are we working on?");
        ShowPage("chat");
    }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string page })
            ShowPage(page);
    }
    private void ShowModels(object sender, RoutedEventArgs e) => ShowPage("models");
    private void ShowPage(string page)
    {
        currentPage = page;
        ChatPage.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        OtherScroll.Visibility = page == "chat" ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page switch
        {
            "apps" => "Apps & actions",
            "tasks" => "Tasks",
            "history" => "History",
            "connections" => "Connections",
            "knowledge" => "Memory & skills",
            "models" => "Models & voices",
            "settings" => "Settings",
            _ => "Conversation"
        };
        PageSubtitle.Text = page switch
        {
            "apps" => "Find and open the applications installed on your PC.",
            "tasks" => "Progress, approvals, and everything your agents did.",
            "history" => "Your conversations and dictation stay on this PC.",
            "connections" => "Connect your accounts. Keep control of every action.",
            "knowledge" => "Inspect and edit what HeyBuddy remembers.",
            "models" => "Download once. Run on your computer.",
            "settings" => "Make HeyBuddy work your way.",
            _ => "A little help, right where you work."
        };
        foreach (var button in NavigationItems.Children.OfType<WpfButton>())
            button.Background = button.Tag?.ToString() == page ? new SolidColorBrush(Color.FromRgb(224, 232, 252)) : Brushes.Transparent;
        PageContent.Children.Clear();
        switch (page)
        {
            case "apps":
                _ = ShowAppsAsync();
                break;
            case "tasks":
                ShowTasks();
                break;
            case "history":
                ShowHistory();
                break;
            case "connections":
                ShowConnections();
                break;
            case "knowledge":
                ShowKnowledge();
                break;
            case "models":
                ShowModelSetup();
                break;
            case "settings":
                ShowSettings();
                break;
        }
    }
    private void RefreshControls()
    {
        var state = app.Factory.ModelManager.GetStatus();
        ReadinessText.Text = $"Local AI: {(state.Running ? "ready" : state.Installed ? "installed" : "needs installation")} · Voice: {(app.Speech.IsInstalled ? "installed" : "needs installation")} · App opening: available";
        ProviderLabel.Text = app.Settings.Provider == "local" ? "Local AI · On this PC" : app.Settings.Provider + " · Optional provider";
    }
    private void ResetOperation()
    {
        operation.Cancel();
        operation.Dispose();
        operation = new();
    }
    public void StopAll()
    {
        operation.Cancel();
        listeningLoop?.Cancel();
        startupLoading?.Cancel();
        app.Agents.CancelAll();
        app.Speech.Stop();
        recording = false;
        latching = false;
        voiceScreenContextPending = false;
        voiceScreenTargetPending = 0;
        TalkButton.Content = "Talk";
        SetMicrophoneActive(false);
        companion?.SetReply("");
        actionCursor?.Hide();
        guidance?.Close();
        guidance = null;
        SetStatus("Stopped. No further actions will run.");
    }
    private void StopEverything(object sender, RoutedEventArgs e) => StopAll();
    public void PrepareExit()
    {
        exiting = true;
        StopAll();
        hotkeys?.Dispose();
        foregroundTimer.Stop();
        app.Speech.AudioLevel -= UpdateMicrophoneLevel;
        app.Desktop.ActionVisual -= ShowDesktopActionVisual;
        app.Speech.Error -= SetStatus;
        app.Factory.ModelManager.StatusChanged -= ModelStatusChanged;
        companion?.Close();
        actionCursor?.Close();
        guidance?.Close();
        Close();
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!exiting)
        {
            e.Cancel = true;
            Hide();
        }
    }
    private void SetStatus(string message)
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetStatus(message));
            return;
        }
        StatusText.Text = message;
    }
    private void ShowDesktopActionVisual(DesktopActionVisual visual)
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            actionCursor ??= new ActionCursorWindow(app.Settings.CompanionColor);
            actionCursor.ShowAt(visual.X, visual.Y, app.Settings.CompanionColor);
            companion?.SetState("Acting");
            SetStatus(string.IsNullOrWhiteSpace(visual.Label) ? "Acting on the verified control…" : "Acting on “" + visual.Label + "”…");
        });
    }
    private async Task Guard(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException) { SetStatus("Stopped."); }
        catch (Exception error) { SetStatus(error.Message); System.Windows.MessageBox.Show(this, error.Message, "HeyBuddy needs attention"); }
    }
    private async Task<bool> RequestApproval(ApprovalRequest request, CancellationToken ct)
    {
        string? targetTitle = null;
        if (request.ToolName.StartsWith("desktop_", StringComparison.Ordinal))
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Arguments);
            if (args.RootElement.TryGetProperty("windowId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var windowId = id.GetString();
                targetTitle = await Task.Run(() => app.Desktop.ListWindows().FirstOrDefault(w => w.Id == windowId)?.Title, ct);
                if (targetTitle is null)
                    throw new InvalidOperationException("The requested target window is no longer available. Inspect the desktop again.");
            }
        }
        var approved = await Dispatcher.InvokeAsync(() =>
        {
            var dialog = new ApprovalWindow(request, targetTitle) { Owner = IsVisible ? this : null };
            using var registration = ct.Register(() => Dispatcher.BeginInvoke(() => { if (dialog.IsVisible) dialog.Close(); }));
            return !ct.IsCancellationRequested && dialog.ShowDialog() == true;
        });
        ct.ThrowIfCancellationRequested();
        if (!approved)
            return false;
        // Approval is bound to these exact arguments. Restore only that validated target after the dialog took focus.
        if (request.ToolName is "desktop_click" or "desktop_type" or "desktop_key" or "desktop_scroll")
        {
            using var args = System.Text.Json.JsonDocument.Parse(request.Arguments);
            if (!args.RootElement.TryGetProperty("windowId", out var window) || window.ValueKind != System.Text.Json.JsonValueKind.String)
                throw new InvalidOperationException("The approved action has no valid target window.");
            var activated = await app.Desktop.ActivateWindowAsync(window.GetString()!, ct);
            if (!activated.Success)
                throw new InvalidOperationException("The approved target could not be restored: " + activated.Message);
        }
        return true;
    }
    private void AttachFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Share files with HeyBuddy", Filter = "Documents and images|*.txt;*.md;*.pdf;*.docx;*.xlsx;*.pptx;*.csv;*.json;*.png;*.jpg;*.jpeg;*.webp|All files|*.*" };
        if (dialog.ShowDialog(this) == true)
        {
            attachments.AddRange(dialog.FileNames.Take(8 - attachments.Count));
            UpdateAttachments();
        }
    }
    private void OnDropFiles(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths)
        {
            attachments.AddRange(paths.Where(File.Exists).Take(8 - attachments.Count));
            UpdateAttachments();
            ShowPage("chat");
        }
    }
    private void UpdateAttachments()
    {
        AttachmentLabel.Visibility = attachments.Count == 0 && pendingSketch is null ? Visibility.Collapsed : Visibility.Visible;
        AttachmentLabel.Text = string.Join(" · ", attachments.Select(Path.GetFileName)) + (pendingSketch is null ? "" : " · Annotated screen");
    }
    private void ClearAttachments(object sender, RoutedEventArgs e)
    {
        attachments.Clear();
        pendingSketch = null;
        UpdateAttachments();
    }
    private async void SketchScreen(object sender, RoutedEventArgs e) => await Guard(async () => { ResetOperation(); var capture = await CaptureContextAsync(); if (capture is null) return; var sketch = new SketchWindow(capture) { Owner = this }; if (sketch.ShowDialog() == true) { pendingSketch = sketch.Result; UpdateAttachments(); } });
}
