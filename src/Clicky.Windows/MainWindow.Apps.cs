using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clicky.Core;
using Clicky.Windows.Native;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Clicky.Windows;

public partial class MainWindow
{
    private int appsPageGeneration;
    private CancellationTokenSource? startupLoading;

    private void ModelStatusChanged(string message)
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            RefreshControls();
            if (!recording && !microphoneTest && !finishingRecording)
                SetStatus(message);
        });
    }

    private async Task PreloadModelAsync()
    {
        if (!app.Settings.PreloadLocalModel || app.Settings.Provider != "local" || !app.Factory.ModelManager.GetStatus().Installed
            || Environment.GetCommandLineArgs().Contains("--self-test"))
            return;
        startupLoading?.Cancel();
        var loading = new CancellationTokenSource();
        startupLoading = loading;
        try
        {
            await app.Factory.ModelManager.StartAsync(loading.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { SetStatus("Local AI could not preload: " + error.Message + " App opening remains available."); }
        finally
        {
            if (ReferenceEquals(startupLoading, loading))
                startupLoading = null;
            loading.Dispose();
        }
    }

    private void ConversationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModeHelp is null)
            return;
        ModeHelp.Text = ModeSelector.SelectedIndex switch
        {
            1 => "Agent runs a task with tools and records its steps. Sensitive actions ask first.",
            2 => "Dictate inserts text into your selected application. Hold Ctrl + Alt + D there.",
            3 => "Chat only answers and draws guidance. It cannot operate apps or use tools.",
            _ => "Auto answers questions and carries out tasks. Sensitive actions ask first."
        };
    }

    private void ShowMicrophoneSettings(object sender, RoutedEventArgs e)
    {
        ShowPage("settings");
        Dispatcher.BeginInvoke(() => PageContent.Children.OfType<FrameworkElement>()
            .FirstOrDefault(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Microphone")?.BringIntoView());
    }

    private async Task ShowAppsAsync()
    {
        var generation = ++appsPageGeneration;
        Note("Open an installed app directly, even while the AI model is loading. In Auto, you can also say ‘open Telegram’ or ask for a longer task.");
        var search = Field("Search installed apps", "");
        var status = Note("Finding installed applications…");
        var list = new StackPanel();
        PageContent.Children.Add(list);
        try
        {
            var applications = await app.Desktop.ListAppsAsync();
            if (currentPage != "apps" || generation != appsPageGeneration)
                return;
            void Refresh()
            {
                list.Children.Clear();
                var query = search.Text.Trim();
                var matches = applications.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                status.Text = matches.Length == 0 ? "No installed application matches. Check its Start menu name or install it separately."
                    : $"{matches.Length} matches{(matches.Length > 60 ? " · showing the first 60; type a name to search all apps" : "")}. Opening is verified against its actual process or window.";
                foreach (var application in matches.Take(60))
                {
                    var row = new DockPanel { Margin = new(0, 0, 0, 8) };
                    var open = new WpfButton { Content = "Open", Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    System.Windows.Automation.AutomationProperties.SetName(open, "Open " + application.Name);
                    DockPanel.SetDock(open, Dock.Right);
                    open.Click += async (_, _) =>
                    {
                        if (busy || recording || finishingRecording || microphoneTest)
                        {
                            SetStatus("Finish or stop the current operation before opening an application.");
                            return;
                        }
                        ShowPage("chat");
                        await Guard(() => LaunchFromPickerAsync(application));
                    };
                    row.Children.Add(open);
                    var label = new StackPanel();
                    label.Children.Add(new TextBlock { Text = application.Name, FontWeight = FontWeights.SemiBold, FontSize = 15 });
                    label.Children.Add(new TextBlock { Text = application.Source, FontSize = 12, Foreground = (Brush)FindResource("Muted"), Margin = new(0, 4, 0, 0) });
                    row.Children.Add(label);
                    list.Children.Add(new Border { Child = row, Padding = new(14), Background = Brushes.White, BorderBrush = (Brush)FindResource("Line"), BorderThickness = new(1), CornerRadius = new(9), Margin = new(0, 0, 0, 8) });
                }
            }
            var searchRevision = 0;
            search.TextChanged += async (_, _) =>
            {
                var revision = ++searchRevision;
                var query = search.Text.Trim();
                await Task.Delay(250);
                if (revision != searchRevision || currentPage != "apps" || generation != appsPageGeneration)
                    return;
                try
                {
                    status.Text = "Searching all installed apps…";
                    var found = await app.Desktop.ListAppsAsync(query);
                    if (revision != searchRevision || currentPage != "apps" || generation != appsPageGeneration)
                        return;
                    applications = found;
                    Refresh();
                }
                catch (Exception error)
                {
                    if (revision == searchRevision && currentPage == "apps" && generation == appsPageGeneration)
                        status.Text = "Could not search apps: " + error.Message;
                }
            };
            Refresh();
        }
        catch (Exception error)
        {
            if (currentPage == "apps" && generation == appsPageGeneration)
                status.Text = "Application discovery failed: " + error.Message + " Reopen Apps & actions to retry.";
        }
    }

    private async Task LaunchFromPickerAsync(DesktopApp application)
    {
        ResetOperation();
        var ct = operation.Token;
        var turnSession = sessionId;
        busy = true;
        SendButton.IsEnabled = false;
        var prompt = "Open " + application.Name;
        var view = BeginConversationTurn(prompt, prompt);
        try
        {
            var watch = Stopwatch.StartNew();
            var run = await ExecuteLaunchAsync(application, prompt, ct);
            ct.ThrowIfCancellationRequested();
            CompleteConversationTurn(view, run.Result);
            AddTaskReceipt(view, run);
            MarkLocalContext();
            SetStatus($"{run.Status} · {watch.Elapsed.TotalSeconds:0.0}s · local app command");
        }
        catch (OperationCanceledException) { CompleteConversationTurn(view, "Stopped. Any completed steps remain in Tasks."); if (sessionId == turnSession) SetStatus("Stopped."); }
        catch (Exception error) { CompleteConversationTurn(view, "Needs attention: " + error.Message); if (sessionId == turnSession) SetStatus(error.Message); }
        finally { busy = false; SendButton.IsEnabled = true; companion?.SetState(""); }
    }

    private Task<AgentRun> ExecuteLaunchAsync(DesktopApp application, string prompt, CancellationToken ct)
        => app.Agents.RunToolAsync(prompt, "desktop_launch", JsonSerializer.SerializeToElement(new
        {
            appId = application.Id
        }),
            app.Tools(), cancellationToken: ct, onProgress: progress => ReportTaskProgress(progress, ct));

    private async Task<(bool Handled, string Text, AgentRun? Run)> TryDirectLaunchAsync(string text, CancellationToken ct)
    {
        var query = AppOpenRequest.Parse(text);
        if (query is null)
            return (false, "", null);
        SetStatus("Finding the installed application…");
        var matches = await app.Desktop.ListAppsAsync(query, ct);
        ct.ThrowIfCancellationRequested();
        var exact = matches.Where(a => a.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1)
            matches = exact;
        if (matches.Count == 0)
            return (true, $"I couldn’t find an installed app named “{query}”. Open Apps & actions to check its name. No application was launched.", null);
        if (matches.Count != 1)
            return (true, "I found several matching applications: " + string.Join(", ", matches.Take(6).Select(a => a.Name)) + ". Choose the exact one in Apps & actions, or use its full name. No application was launched.", null);
        var run = await ExecuteLaunchAsync(matches[0], text, ct);
        return (true, run.Result, run);
    }

    private void ReportTaskProgress(AgentRun progress, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || Dispatcher.HasShutdownStarted || progress.Status is not (RunStatus.Running or RunStatus.AwaitingApproval or RunStatus.Queued))
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (ct.IsCancellationRequested)
                return;
            var detail = string.IsNullOrWhiteSpace(progress.Result) ? "Planning the next step…" : progress.Result;
            SetStatus($"{progress.Status} · {progress.Actions} actions · {detail}");
            companion?.SetState(progress.Status == RunStatus.AwaitingApproval ? "Approval needed" : "Working");
        });
    }

    private WpfTextBox BeginConversationTurn(string displayedText, string modelText, IReadOnlyList<ImageAttachment>? images = null)
    {
        AddMessage("user", displayedText);
        app.Store.AddMessage(sessionId, "chat", "user", displayedText);
        conversation.Add(new("user", modelText, images));
        var view = AddMessage("assistant", "Working…");
        view.Tag = sessionId;
        return view;
    }

    private void CompleteConversationTurn(WpfTextBox view, string text)
    {
        view.Text = text;
        view.FlowDirection = DetectLanguage(text) == "fa" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        var turnSession = view.Tag as string ?? sessionId;
        app.Store.AddMessage(turnSession, "chat", "assistant", text);
        if (turnSession == sessionId)
        {
            conversation.Add(new("assistant", text));
            companion?.SetReply(text);
        }
    }

    private void AddTaskReceipt(WpfTextBox view, AgentRun run)
    {
        if (run.Actions == 0 && run.Status == RunStatus.Completed || view.Parent is not StackPanel parent)
            return;
        var button = new WpfButton { Content = $"View task steps · {run.Actions} actions · {run.Status}", HorizontalAlignment = HorizontalAlignment.Left, Margin = new(0, 10, 0, 0) };
        button.Click += (_, _) => ShowHistory(run.Id);
        parent.Children.Add(button);
    }
}
