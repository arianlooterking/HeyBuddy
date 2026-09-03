using System.Windows;
using System.Windows.Controls;
using Clicky.Windows.Speech;

namespace Clicky.Windows;

public partial class MainWindow
{
    private bool microphoneTest;
    private bool finishingRecording;
    private string activeMicrophoneName = "";

    private void SetMicrophoneActive(bool active)
    {
        MicrophoneActivity.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        MicrophoneInputLevel.Value = 0;
        if (!active)
            return;
        activeMicrophoneName = app.Speech.IsRecording ? app.Speech.LastCaptureStatus.DeviceName
            : SpeechService.GetMicrophones().FirstOrDefault(d => d.Id == app.Settings.MicrophoneId)?.Name ?? "Selected microphone";
        MicrophoneActivityLabel.Text = activeMicrophoneName + " · waiting for audio";
    }

    private void UpdateMicrophoneLevel(float level)
    {
        if (Dispatcher.HasShutdownStarted)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (MicrophoneActivity.Visibility != Visibility.Visible)
                return;
            var decibels = 20 * Math.Log10(Math.Max(level, .000001));
            MicrophoneInputLevel.Value = Math.Clamp((decibels + 70) / 70 * 100, 0, 100);
            MicrophoneActivityLabel.Text = activeMicrophoneName + (decibels < -70 ? " · no input signal" : $" · input {decibels:0} dB");
        });
    }

    private async Task TestMicrophoneAsync(TextBlock result)
    {
        if (busy || recording || finishingRecording || microphoneTest || listeningLoop is not null || app.Speech.IsRecording)
            throw new InvalidOperationException("Finish the current recording or response before testing the microphone.");
        ResetOperation();
        var token = operation.Token;
        microphoneTest = true;
        TalkButton.IsEnabled = false;
        try
        {
            app.Speech.StartRecording();
            SetMicrophoneActive(true);
            for (var seconds = 8; seconds > 0; seconds--)
            {
                result.Text = $"Speak normally now ({seconds}s): ‘Hello HeyBuddy, can you hear me?’ The meter should move. This test stays on your PC.";
                SetStatus("Microphone test is recording. Stop everything cancels it.");
                await Task.Delay(1000, token);
            }
            result.Text = "Transcribing the microphone sample locally…";
            var text = await app.Speech.StopAndTranscribeAsync(cancellationToken: token);
            token.ThrowIfCancellationRequested();
            var capture = app.Speech.LastCaptureStatus;
            var peak = capture.Peak > 0 ? $"{20 * Math.Log10(capture.Peak):0} dB" : "silent";
            var detail = capture.Message + $"\n{capture.DeviceName} · {capture.AudioSeconds:0.0}s audio · peak {peak}";
            result.Text = string.IsNullOrWhiteSpace(text) ? detail : "Heard: " + text + "\n" + detail;
            SetStatus(string.IsNullOrWhiteSpace(text) ? detail : "Microphone test completed. Recognized words are shown beside the test button.");
        }
        catch (OperationCanceledException)
        {
            result.Text = "Microphone test cancelled. Audio was discarded.";
            throw;
        }
        catch (Exception error)
        {
            result.Text = "Microphone test failed: " + error.Message;
            throw;
        }
        finally
        {
            app.Speech.Stop();
            microphoneTest = false;
            TalkButton.IsEnabled = true;
            SetMicrophoneActive(false);
        }
    }
}
