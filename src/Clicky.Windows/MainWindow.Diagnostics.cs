using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Clicky.Core;
using Clicky.Windows.Views;

namespace Clicky.Windows;

public partial class MainWindow
{
    internal void DiagnosticPage(string page) => ShowPage(page);
    internal void DiagnosticText(string text) => AddMessage("assistant", text);
    internal async Task DiagnosticConversationAsync(string prompt)
    {
        ShowPage("chat");
        ModeSelector.SelectedIndex = 0;
        ScreenCheck.IsChecked = false;
        Composer.Text = prompt;
        await SendAsync();
    }
    internal void DiagnosticSettingsSynchronization(ICollection<string> checks)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLICKY_DATA_DIR")))
            throw new InvalidOperationException("Settings diagnostics require an isolated data directory.");
        foregroundTimer.Stop();
        companion ??= new CompanionWindow(app.Settings, () => { });
        ShowPage("settings");
        T Control<T>(string name) where T : FrameworkElement => PageContent.Children.OfType<T>().Single(control => AutomationProperties.GetName(control) == name);
        void Verify(string name, bool passed)
        {
            if (!passed)
                throw new InvalidOperationException(name);
            checks.Add(name);
        }
        var size = Control<TextBox>("Companion size (0.5–2.0)");
        var mascot = (Canvas)((StackPanel)companion.Content).Children[0];
        foreach (var scale in new[] { 1.0, .5 })
        {
            size.Text = scale.ToString(CultureInfo.InvariantCulture);
            Verify($"Size field {scale:0.0} immediately updates memory, disk and companion", app.Settings.CompanionScale == scale && AppSettings.Load().CompanionScale == scale && mascot.LayoutTransform.Value.M11 == scale);
        }
        foreach (var (label, scale) in new[] { ("Normal (100%)", 1.0), ("Small (50%)", .5) })
        {
            var choice = companion.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == label);
            choice.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Verify($"Menu {label} synchronizes the open Settings field", size.Text == scale.ToString(CultureInfo.InvariantCulture) && app.Settings.CompanionScale == scale && AppSettings.Load().CompanionScale == scale && mascot.LayoutTransform.Value.M11 == scale);
        }
        // The full Save button preserves live values but also changes startup and global hooks.
        // Persist only the isolated live settings snapshot here.
        app.Settings.Save();
        Verify("Saving live settings keeps the synchronized half-size choice", size.Text == "0.5" && app.Settings.CompanionScale == .5 && AppSettings.Load().CompanionScale == .5);

        var microphone = Control<ComboBox>("Microphone");
        var alternate = Enumerable.Range(0, microphone.Items.Count).FirstOrDefault(index => index != microphone.SelectedIndex, -1);
        if (alternate < 0)
            throw new InvalidOperationException("A second enumerated microphone selection is required to verify a changed selection.");
        microphone.SelectedIndex = alternate;
        var selectedMicrophone = int.Parse(((string)microphone.SelectedItem).Split('·')[0].Trim(), CultureInfo.InvariantCulture);
        Verify("Microphone selection immediately saves without starting capture", app.Settings.MicrophoneId == selectedMicrophone && AppSettings.Load().MicrophoneId == selectedMicrophone && !app.Speech.IsRecording);
        var language = Control<ComboBox>("Recognition language");
        foreach (var code in new[] { "fa", "tr", "en" })
        {
            language.SelectedItem = code;
            Verify($"Recognition language {code} immediately saves", app.Settings.Language == code && AppSettings.Load().Language == code);
        }
        Verify("Settings regression opened no app windows and captured no audio", !IsVisible && !companion.IsVisible && !app.Speech.IsRecording && !recording && !microphoneTest);
    }
    internal async Task DiagnosticRecordingOwnershipAsync(ICollection<string> checks)
    {
        // Missing speech assets ensure even a regressed guard cannot start real microphone capture.
        if (app.Speech.Assets.RecognitionInstalled)
            throw new InvalidOperationException("Ownership diagnostics require an empty isolated model directory.");
        var activeOperation = operation;
        var activeToken = operation.Token;
        bool Preserved() => ReferenceEquals(operation, activeOperation) && !activeToken.IsCancellationRequested && !app.Speech.IsRecording && !recording;
        void Verify(string name, bool passed)
        {
            if (!passed)
                throw new InvalidOperationException(name);
            checks.Add(name);
        }
        try
        {
            finishingRecording = true;
            var refused = false;
            try
            {
                await TestMicrophoneAsync(new TextBlock());
            }
            catch (InvalidOperationException) { refused = true; }
            Verify("Pending transcription refuses a microphone test without replacing or cancelling its operation", refused && Preserved() && !microphoneTest);
            var manualPreserved = true;
            foreach (var dictation in new[] { false, true })
            {
                BeginRecording(dictation);
                manualPreserved &= Preserved();
            }
            Verify("Pending transcription refuses manual recording without starting capture or cancelling its operation", manualPreserved);

            finishingRecording = false;
            microphoneTest = true;
            const string pendingMessage = "This message must wait for the microphone test.";
            Composer.Text = pendingMessage;
            ModeSelector.SelectedIndex = 0;
            await SendAsync();
            Verify("Typed Send during a microphone test preserves its operation and unsent message", Preserved() && microphoneTest && !busy && Composer.Text == pendingMessage);
        }
        finally { finishingRecording = false; microphoneTest = false; Composer.Clear(); }
    }
}
