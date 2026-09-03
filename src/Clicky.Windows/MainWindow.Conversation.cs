using System.Text;
using Clicky.Core;
using Clicky.Windows.Native;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Clicky.Windows;

public partial class MainWindow
{
    private async Task SendAsync()
    {
        if (microphoneTest)
        {
            SetStatus("Finish or stop the microphone test before sending a message.");
            return;
        }
        if (busy || string.IsNullOrWhiteSpace(Composer.Text))
            return;
        var text = Composer.Text.Trim();
        var mode = ModeSelector.SelectedIndex;
        if (mode == 2)
        {
            var destination = targetWindow;
            ResetOperation();
            app.Store.AddMessage(sessionId, "dictation", "user", text);
            await InsertDictationAsync(text, destination, operation.Token);
            Composer.Clear();
            SetStatus("Delivered to the selected application. Transcript saved in History.");
            return;
        }
        ResetOperation();
        var ct = operation.Token;
        var turnSession = sessionId;
        // Derive completion evidence only from the owner's request, never attachment or tool content.
        var requiredCompletion = ActionIntent.RequiredCompletion(text);
        busy = true;
        SendButton.IsEnabled = false;
        WpfTextBox? view = null;
        var savedReply = false;
        try
        {
            // Only the owner's entire typed/spoken request enters this route, never file or tool content.
            if (mode == 0 && attachments.Count == 0 && pendingSketch is null && ScreenCheck.IsChecked != true && AppOpenRequest.Parse(text) is not null)
            {
                view = BeginConversationTurn(text, text);
                Composer.Clear();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var direct = await TryDirectLaunchAsync(text, ct);
                ct.ThrowIfCancellationRequested();
                CompleteConversationTurn(view, direct.Text);
                savedReply = true;
                if (direct.Run is not null)
                    AddTaskReceipt(view, direct.Run);
                MarkLocalContext();
                SetStatus($"{direct.Run?.Status.ToString() ?? "Needs attention"} · {watch.Elapsed.TotalSeconds:0.0}s · local app command");
                return;
            }
            var provider = app.Provider();
            var toolMode = mode is 0 or 1;
            if (provider.IsCloud && (conversationContainsFiles || ScreenCheck.IsChecked == true || pendingSketch is not null || attachments.Count > 0 || toolMode) && !app.Settings.CloudContentAllowed)
                throw new InvalidOperationException("Auto and Agent can share local tool results with your selected provider. Choose Local AI, use Chat only for ordinary questions, or explicitly allow screen and file content in cloud requests in Settings.");
            ScreenCapture? capture = pendingSketch ?? (ScreenCheck.IsChecked == true ? await CaptureContextAsync() : null);
            ct.ThrowIfCancellationRequested();
            var userContent = text;
            var containsFiles = attachments.Count > 0 || capture is not null;
            foreach (var path in attachments)
            {
                if (Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
                    continue;
                var extracted = await ReadAttachmentAsync(path, app.Documents, ct);
                userContent += $"\n\n<document untrusted=\"true\" name={System.Text.Json.JsonSerializer.Serialize(Path.GetFileName(path))}>\n{extracted}\n</document>";
            }
            var images = new List<ImageAttachment>();
            if (capture is not null)
                images.Add(ImagePreparation.ForModel(capture.ToAttachment(), app.Settings.VisionMaxEdge));
            foreach (var file in attachments.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp"))
            {
                if (new FileInfo(file).Length > 12_000_000)
                    throw new InvalidOperationException("Use images smaller than 12 MB.");
                var bytes = await File.ReadAllBytesAsync(file, ct);
                images.Add(ImagePreparation.ForModel(new(Convert.ToBase64String(bytes), Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".jpeg" ? "image/jpeg" : Path.GetExtension(file).ToLowerInvariant() == ".webp" ? "image/webp" : "image/png", Path.GetFileName(file)), app.Settings.VisionMaxEdge));
            }
            ct.ThrowIfCancellationRequested();
            var previousMessages = conversation.TakeLast(24).ToArray();
            Composer.Clear();
            attachments.Clear();
            pendingSketch = null;
            UpdateAttachments();
            if (mode == 1)
            {
                // Agent mode remains independent of the foreground conversation. Stop everything cancels it through AgentRunner.
                _ = app.Agents.RunAsync(userContent, provider, app.Tools(), app.Knowledge.Context(), followUpId,
                    images: images.Count == 0 ? null : images, contextTokens: app.Settings.ContextSize, requireAction: true,
                    requireStateChange: requiredCompletion is not null, requiredCompletion: requiredCompletion, previousMessages: previousMessages);
                followUpId = null;
                ShowPage("tasks");
                SetStatus("Task started. You can keep using HeyBuddy; its steps and approval requests appear in Tasks.");
                return;
            }
            view = BeginConversationTurn(text, userContent, images.Count == 0 ? null : images);
            if (containsFiles)
                MarkLocalContext();
            SetStatus("Thinking with " + provider.Name + "…");
            companion?.SetState("Thinking");
            var buffer = new StringBuilder();
            var replyView = view;
            void Stream(string delta)
            {
                if (ct.IsCancellationRequested)
                    return;
                buffer.Append(delta);
                var snapshot = GuidanceParser.Parse(buffer.ToString()).Text;
                Dispatcher.BeginInvoke(() => { if (!ct.IsCancellationRequested) { replyView.Text = snapshot; ChatScroll.ScrollToEnd(); } });
            }
            ModelReply? reply = null;
            AgentRun? run = null;
            if (toolMode)
            {
                // Tool output can contain local information even when the user attached no files.
                MarkLocalContext();
                run = await app.Agents.RunAsync(userContent, provider, app.Tools(), app.Knowledge.Context(), cancellationToken: ct,
                    images: images.Count == 0 ? null : images, contextTokens: app.Settings.ContextSize,
                    requireAction: mode == 0 && ActionIntent.RequiresExecution(text),
                    requireStateChange: mode == 0 && requiredCompletion is not null,
                    requiredCompletion: mode == 0 ? requiredCompletion : null,
                    onProgress: progress => ReportTaskProgress(progress, ct), onText: Stream, previousMessages: previousMessages);
            }
            else
            {
                var requestMessages = new List<ChatMessage> { new("system", PromptCatalog.Conversation + "\nUser-managed memory and skills:\n" + ContextBudget.ExcerptContext(app.Knowledge.Context(), 700)) };
                requestMessages.AddRange(conversation.TakeLast(24));
                reply = await provider.CompleteAsync(ContextBudget.Fit(new(requestMessages), app.Settings.ContextSize), Stream, ct);
            }
            ct.ThrowIfCancellationRequested();
            var parsed = GuidanceParser.Parse(run?.Result ?? reply?.Text ?? "The provider returned no answer. Please retry.");
            CompleteConversationTurn(view, parsed.Text);
            savedReply = true;
            if (run is not null)
                AddTaskReceipt(view, run);
            if (capture is not null && parsed.Commands.Count > 0)
            {
                guidance?.Close();
                guidance = new(capture, parsed.Commands);
                guidance.Show();
            }
            SetStatus(run is { Status: not RunStatus.Completed } ? $"{run.Status} · Review task steps for the outcome." : "Ready · " + provider.Name);
            if (app.Settings.SpeakReplies && !string.IsNullOrWhiteSpace(parsed.Text) && (run is null || run.Status == RunStatus.Completed))
            {
                companion?.SetState("Speaking");
                if (reply?.AudioBase64 is not null)
                    await app.Speech.PlayPcmAsync(Convert.FromBase64String(reply.AudioBase64), reply.AudioSampleRate, ct);
                else
                    await app.Speech.SpeakAsync(parsed.Text, DetectLanguage(parsed.Text), ct);
            }
        }
        catch (OperationCanceledException)
        {
            if (view is not null && !savedReply)
                CompleteConversationTurn(view, "Stopped. No further actions will run; any completed steps remain in Tasks.");
            if (sessionId == turnSession)
                SetStatus("Stopped. Your conversation is saved.");
        }
        catch (Exception error)
        {
            if (view is not null && !savedReply)
                CompleteConversationTurn(view, "Needs attention: " + error.Message);
            else if (sessionId == turnSession)
                AddMessage("notice", error.Message);
            if (sessionId == turnSession)
                SetStatus(error.Message);
        }
        finally
        {
            for (var i = 0; i < conversation.Count; i++)
                if (conversation[i].Images is not null)
                    conversation[i] = conversation[i] with
                    {
                        Images = null
                    };
            busy = false;
            SendButton.IsEnabled = true;
            companion?.SetState("");
            RefreshControls();
        }
    }

    private void MarkLocalContext()
    {
        conversationContainsFiles = true;
        if (app.Settings.FileContextSessions.Add(sessionId))
            app.Settings.Save();
    }
}
