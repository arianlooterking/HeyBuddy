using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Clicky.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Whisper.net;

namespace Clicky.Windows.Speech;

public sealed record AudioDevice(int Id, string Name);
public sealed record SpeechMeasurement(string Operation, TimeSpan Duration, double AudioSeconds = 0, string Language = "");

/// <summary>Explicit recording only; microphone PCM remains in bounded memory. No cloud speech fallback.</summary>
public sealed class SpeechService : IDisposable
{
    private readonly AppSettings settings;
    private readonly object gate = new();
    private readonly SemaphoreSlim inferenceLock = new(1, 1);
    private readonly SemaphoreSlim voiceLock = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource operation = new();
    private CancellationTokenSource? speechCancellation;
    private WhisperFactory? factory;
    private string? loadedModel;
    private Recording? recording;
    private WaveOutEvent? playback;
    private Process? synthesizer;
    private bool disposed;
    private SpeechCaptureStatus lastCaptureStatus = new(SpeechCaptureOutcome.Idle, "Choose a microphone and start a recording to check its signal.");
    public SpeechCaptureStatus LastCaptureStatus => Volatile.Read(ref lastCaptureStatus);
    public SpeechAssets Assets
    {
        get;
    }
    public bool IsRecording
    {
        get
        {
            lock (gate)
                return recording is { Stopping: false };
        }
    }
    public bool IsInstalled => Assets.IsInstalled;
    public event Action<float>? AudioLevel;
    public event Action? VoiceActivity;
    public event Action<string>? Error;
    public event Action<SpeechMeasurement>? Measured;
    public event Action<SpeechCaptureStatus>? CaptureStatusChanged;
    private void PublishCaptureStatus(SpeechCaptureStatus status)
    {
        Volatile.Write(ref lastCaptureStatus, status);
        CaptureStatusChanged?.Invoke(status);
    }
    private sealed class Recording(WaveInEvent input, SpeechCaptureStatus status)
    {
        internal WaveInEvent Input = input;
        internal readonly SpeechCaptureStatus Status = status;
        internal readonly SpeechInputMeter Meter = new();
        internal readonly object Gate = new();
        internal readonly MemoryStream Pcm = new();
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly CancellationTokenSource PreviewCancellation = new();
        internal bool Stopping;
        internal bool Released;
        internal float Level;
        internal long LastSpeech;
        internal bool HasSpeech;
        internal long Started = Environment.TickCount64;
    }
    public SpeechService(AppSettings settings)
    {
        this.settings = settings;
        Assets = new(settings);
    }
    public static IReadOnlyList<AudioDevice> GetMicrophones() => new[] { new AudioDevice(-1, "Windows default microphone") }.Concat(Enumerable.Range(0, WaveIn.DeviceCount).Select(i => new AudioDevice(i, WaveIn.GetCapabilities(i).ProductName))).ToArray();
    public static IReadOnlyList<AudioDevice> GetOutputDevices() => new[] { new AudioDevice(-1, "Windows default output") }.Concat(Enumerable.Range(0, WaveOut.DeviceCount).Select(i => new AudioDevice(i, WaveOut.GetCapabilities(i).ProductName))).ToArray();
    public Task InstallAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default) => Assets.InstallAsync(onProgress, cancellationToken);
    public Task InstallAsync(IProgress<string> progress, CancellationToken cancellationToken = default) => Assets.InstallAsync(progress.Report, cancellationToken);
    public void StartRecording(Action<string>? onPartial = null, bool preservePlayback = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!Assets.RecognitionInstalled)
            throw new InvalidOperationException("Install the local speech models in Settings before recording.");
        if (!preservePlayback)
            StopPlayback();
        lock (gate)
        {
            if (recording is not null)
                throw new InvalidOperationException("A recording is already active. Finish or cancel it first.");
            if (operation.IsCancellationRequested)
            {
                operation.Dispose();
                operation = new();
            }
            if (WaveIn.DeviceCount == 0)
                throw new InvalidOperationException("No microphone is available. Connect one and allow Windows microphone access for desktop applications.");
            if (settings.MicrophoneId < -1 || settings.MicrophoneId >= WaveIn.DeviceCount)
                throw new InvalidOperationException("The selected microphone is no longer available. Select another microphone in Settings.");
            var input = new WaveInEvent { DeviceNumber = settings.MicrophoneId, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 80, NumberOfBuffers = 3 };
            var deviceName = RecordingDeviceName(settings.MicrophoneId);
            var current = new Recording(input, new(SpeechCaptureOutcome.Recording, "Listening through " + deviceName + ".", settings.MicrophoneId, deviceName, RequestedLanguage: settings.Language));
            recording = current;
            PublishCaptureStatus(current.Status);
            input.DataAvailable += (_, args) =>
            {
                float level;
                SpeechInputAnalysis capture;
                var firstSpeech = false;
                lock (current.Gate)
                {
                    if (current.Stopping)
                        return;
                    current.Pcm.Write(args.Buffer, 0, args.BytesRecorded);
                    var frame = current.Meter.Add(args.Buffer.AsSpan(0, args.BytesRecorded));
                    capture = current.Meter.Snapshot();
                    // Use the changing part of the signal so electrical DC offset does not
                    // make the UI claim that it hears the user.
                    level = (float)frame.DynamicRms;
                    current.Level = level;
                    // This activity hint controls previews and hands-free timing only. Final
                    // recognition evaluates the complete signal and never requires this threshold.
                    if (frame.DynamicRms >= .0005)
                    {
                        firstSpeech = !current.HasSpeech;
                        current.HasSpeech = true;
                        current.LastSpeech = Environment.TickCount64;
                    }
                    if (current.Pcm.Length >= 16000 * 2 * 120)
                    {
                        current.Stopping = true;
                        ThreadPool.QueueUserWorkItem(_ => current.Input.StopRecording());
                    }
                }
                AudioLevel?.Invoke(level);
                if (!current.Stopping)
                    PublishCaptureStatus(current.Status with
                    {
                        AudioSeconds = capture.AudioSeconds,
                        Rms = capture.Rms,
                        Peak = capture.Peak
                    });
                if (firstSpeech)
                    VoiceActivity?.Invoke();
            };
            input.RecordingStopped += (_, args) => { current.Stopping = true; if (args.Exception is not null) current.Stopped.TrySetException(args.Exception); else current.Stopped.TrySetResult(); };
            try
            {
                input.StartRecording();
            }
            catch (Exception exception)
            {
                PublishCaptureStatus(current.Status with
                {
                    Outcome = SpeechCaptureOutcome.Error,
                    Message = "The microphone could not start: " + exception.Message
                });
                recording = null;
                input.Dispose();
                current.Pcm.Dispose();
                current.PreviewCancellation.Dispose();
                throw;
            }
            if (onPartial is not null)
                _ = PreviewAsync(current, onPartial);
        }
    }
    private static string RecordingDeviceName(int id)
    {
        if (id >= 0)
            return WaveIn.GetCapabilities(id).ProductName;
        try
        {
            using var devices = new MMDeviceEnumerator();
            using var device = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            return "Windows default · " + device.FriendlyName;
        }
        catch (System.Runtime.InteropServices.COMException) { return "Windows default microphone"; }
    }
    private async Task PreviewAsync(Recording current, Action<string> onPartial)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, current.PreviewCancellation.Token, operation.Token);
        try
        {
            while (!linked.IsCancellationRequested)
            {
                await Task.Delay(3500, linked.Token).ConfigureAwait(false);
                byte[] bytes;
                lock (current.Gate)
                {
                    if (current.Stopping)
                        return;
                    if (!current.HasSpeech)
                        continue;
                    bytes = current.Pcm.ToArray();
                }
                var text = await TranscribePcmAsync(bytes, null, linked.Token, current.Status.RequestedLanguage, current.Status, reportStatus: false).ConfigureAwait(false);
                if (!linked.IsCancellationRequested)
                    onPartial(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error?.Invoke("Dictation preview: " + exception.Message); }
    }
    public async Task<string> StopAndTranscribeAsync(Action<string>? onPartial = null, CancellationToken cancellationToken = default)
    {
        Recording current;
        lock (gate)
            current = recording ?? throw new InvalidOperationException("No recording is active.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, operation.Token, cancellationToken);
        try
        {
            current.PreviewCancellation.Cancel();
            lock (current.Gate)
            {
                if (!current.Stopping)
                {
                    current.Stopping = true;
                    current.Input.StopRecording();
                }
            }
            await current.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), linked.Token).ConfigureAwait(false);
            byte[] pcm;
            lock (current.Gate)
                pcm = current.Pcm.ToArray();
            return await TranscribePcmAsync(pcm, onPartial, linked.Token, current.Status.RequestedLanguage, current.Status).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            PublishCaptureStatus(LastCaptureStatus with
            {
                Outcome = SpeechCaptureOutcome.Cancelled,
                Message = "Recording was cancelled. Audio was discarded."
            });
            throw;
        }
        catch (Exception exception)
        {
            PublishCaptureStatus(LastCaptureStatus with
            {
                Outcome = SpeechCaptureOutcome.Error,
                Message = "Microphone or speech recognition error: " + exception.Message
            });
            throw;
        }
        finally { ReleaseRecording(current); }
    }
    /// <summary>Call only after explicit hands-free enablement. Returns after one utterance or 30 seconds of silence.</summary>
    public async Task<string> CaptureUtteranceAsync(Action<string>? onPartial = null, CancellationToken cancellationToken = default, bool preservePlayback = false)
    {
        StartRecording(onPartial, preservePlayback);
        Recording current;
        lock (gate)
            current = recording!;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, operation.Token, cancellationToken);
        try
        {
            while (true)
            {
                await Task.Delay(80, linked.Token).ConfigureAwait(false);
                bool done;
                lock (current.Gate)
                {
                    var now = Environment.TickCount64;
                    done = current.Stopping || (current.HasSpeech && now - current.LastSpeech >= 1100 && now - current.Started >= 1400) || now - current.Started >= 30000;
                }
                if (done)
                    break;
            }
            return await StopAndTranscribeAsync(onPartial, linked.Token).ConfigureAwait(false);
        }
        catch { CancelRecording(current); throw; }
    }
    public async Task<string> TranscribeWavAsync(Stream wav, string? language = null, Action<string>? onPartial = null, CancellationToken cancellationToken = default)
    {
        using var reader = new WaveFileReader(wav);
        using var resampler = new MediaFoundationResampler(reader, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        using var pcm = new MemoryStream();
        var buffer = new byte[32000];
        int count;
        while ((count = resampler.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pcm.Write(buffer, 0, count);
            if (pcm.Length > 16000 * 2 * 600)
                throw new InvalidDataException("Audio exceeds the ten-minute transcription limit.");
        }
        return await TranscribePcmAsync(pcm.ToArray(), onPartial, cancellationToken, language).ConfigureAwait(false);
    }
    private async Task<string> TranscribePcmAsync(byte[] pcm, Action<string>? onPartial, CancellationToken cancellationToken, string? languageOverride = null, SpeechCaptureStatus? captureStatus = null, bool reportStatus = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = linked.Token;
        cancellationToken.ThrowIfCancellationRequested();
        var analysis = SpeechInputAnalysis.Analyze(pcm);
        var language = languageOverride ?? settings.Language;
        var status = (captureStatus ?? new(SpeechCaptureOutcome.Idle, "", DeviceName: "Audio input")) with
        {
            AudioSeconds = analysis.AudioSeconds,
            Rms = analysis.Rms,
            Peak = analysis.Peak,
            RequestedLanguage = language
        };
        if (!analysis.CanTranscribe)
        {
            status = analysis.Samples == 0
                ? status with
                {
                    Outcome = SpeechCaptureOutcome.NoAudio,
                    Message = "No audio frames arrived from " + status.DeviceName + ". Check Windows microphone permission for desktop apps, reconnect the device, or choose another microphone."
                }
                : analysis.Samples < 1600
                    ? status with
                    {
                        Outcome = SpeechCaptureOutcome.TooShort,
                        Message = "The recording was shorter than 0.1 seconds. Hold the shortcut until you finish speaking."
                    }
                    : status with
                    {
                        Outcome = SpeechCaptureOutcome.NoSignal,
                        Message = "Audio arrived from " + status.DeviceName + ", but it contained no usable signal. Check mute, the microphone input level, and the selected input device."
                    };
            if (reportStatus)
                PublishCaptureStatus(status);
            return "";
        }
        var prepared = analysis.Prepare(pcm);
        status = status with
        {
            Outcome = analysis.IsQuiet ? SpeechCaptureOutcome.Quiet : SpeechCaptureOutcome.Transcribing,
            Gain = prepared.Gain,
            Message = analysis.IsQuiet ? "Quiet audio received; applying a bounded volume boost and transcribing locally." : "Audio received; transcribing locally."
        };
        if (reportStatus)
            PublishCaptureStatus(status);
        await inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (factory is null || loadedModel != Assets.WhisperModelPath)
            {
                factory?.Dispose();
                factory = null;
                factory = WhisperFactory.FromPath(Assets.WhisperModelPath, new WhisperFactoryOptions { UseGpu = false });
                loadedModel = Assets.WhisperModelPath;
            }
            var builder = factory.CreateBuilder().WithThreads(Math.Clamp(settings.CpuThreads, 1, 12));
            if (language is "en" or "fa" or "tr")
                builder.WithLanguage(language);
            else
                builder.WithLanguage("auto");
            if (settings.Dictionary.Count > 0)
                builder.WithPrompt(string.Join(", ", settings.Dictionary.Values.Take(100)));
            await using var processor = builder.Build();
            var samples = prepared.Samples;
            var text = new StringBuilder();
            var detectedLanguage = "";
            await foreach (var segment in processor.ProcessAsync(samples, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                text.Append(segment.Text);
                detectedLanguage = segment.Language ?? detectedLanguage;
                onPartial?.Invoke(ApplyDictionary(text.ToString().Trim()));
            }
            Measured?.Invoke(new("transcription", started.Elapsed, samples.Length / 16000d, language));
            var result = ApplyDictionary(text.ToString().Trim());
            cancellationToken.ThrowIfCancellationRequested();
            if (reportStatus)
                PublishCaptureStatus(status with
                {
                    Outcome = result.Length == 0 ? SpeechCaptureOutcome.RecognizerEmpty : SpeechCaptureOutcome.Recognized,
                    DetectedLanguage = detectedLanguage,
                    Message = result.Length == 0
                        ? "Audio was received, but the local recognizer returned no words." + (analysis.IsQuiet ? " The signal was quiet; move closer or raise the microphone input level." : " Try a longer phrase, reduce background noise, or choose English, Persian, or Turkish explicitly.")
                        : "Speech recognized locally." + (analysis.IsQuiet ? " The quiet signal was boosted; raising the microphone input level may help." : "")
                });
            return result;
        }
        catch (OperationCanceledException)
        {
            if (reportStatus)
                PublishCaptureStatus(status with
                {
                    Outcome = SpeechCaptureOutcome.Cancelled,
                    Message = "Speech recognition was cancelled. Audio was discarded."
                });
            throw;
        }
        catch (Exception exception)
        {
            if (reportStatus)
                PublishCaptureStatus(status with
                {
                    Outcome = SpeechCaptureOutcome.Error,
                    Message = "Local speech recognition failed: " + exception.Message
                });
            throw;
        }
        finally { inferenceLock.Release(); }
    }
    private string ApplyDictionary(string text)
    {
        foreach (var (from, to) in settings.Dictionary.Where(p => !string.IsNullOrWhiteSpace(p.Key)).OrderByDescending(p => p.Key.Length))
            text = Regex.Replace(text, @"(?<!\p{L})" + Regex.Escape(from) + @"(?!\p{L})", _ => to, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        return text;
    }
    public async Task SpeakAsync(string text, string? language = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        ObjectDisposedException.ThrowIf(disposed, this);
        StopPlayback();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        lock (gate)
            speechCancellation = linked;
        await voiceLock.WaitAsync(linked.Token).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        try
        {
            var audio = await SynthesizeAsync(text, language, linked.Token).ConfigureAwait(false);
            Measured?.Invoke(new("speech-synthesis", started.Elapsed, audio.Pcm.Length / (audio.SampleRate * 2d), audio.Language));
            await PlayCoreAsync(audio.Pcm, audio.SampleRate, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                playback = null;
                if (speechCancellation == linked)
                    speechCancellation = null;
            }
            voiceLock.Release();
        }
    }
    /// <summary>Play 16-bit mono PCM through the selected device. Used by explicitly selected cloud audio providers.</summary>
    public async Task PlayPcmAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        StopPlayback();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
        lock (gate)
            speechCancellation = linked;
        await voiceLock.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            await PlayCoreAsync(pcm, sampleRate, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                playback = null;
                if (speechCancellation == linked)
                    speechCancellation = null;
            }
            voiceLock.Release();
        }
    }
    private async Task PlayCoreAsync(byte[] pcm, int sampleRate, CancellationToken cancellationToken)
    {
        if (sampleRate is < 8000 or > 96000 || pcm.Length % 2 != 0 || pcm.Length > (long)sampleRate * 2 * 600)
            throw new ArgumentException("Audio must be bounded 16-bit mono PCM at 8–96 kHz.");
        if (pcm.Length == 0)
            return;
        if (settings.OutputDeviceId >= WaveOut.DeviceCount)
            throw new InvalidOperationException("The selected audio output is unavailable. Select another device in Settings.");
        using var stream = new MemoryStream(pcm, false);
        using var source = new RawSourceWaveStream(stream, new WaveFormat(sampleRate, 16, 1));
        using var output = new WaveOutEvent { DeviceNumber = settings.OutputDeviceId, DesiredLatency = 120 };
        lock (gate)
            playback = output;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) => { if (args.Exception is not null) completion.TrySetException(args.Exception); else completion.TrySetResult(); };
        output.Init(source);
        using var cancel = cancellationToken.Register(output.Stop);
        cancellationToken.ThrowIfCancellationRequested();
        output.Play();
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task<(byte[] Pcm, int SampleRate, string Language)> SynthesizeAsync(string text, string? language = null, CancellationToken cancellationToken = default)
    {
        if (text.Length > 30000)
            throw new ArgumentException("Spoken text is limited to 30,000 characters per reply.");
        var selectedLanguage = language is "en" or "fa" or "tr" ? language : settings.Language is "en" or "fa" or "tr" ? settings.Language : DetectLanguage(text);
        var voice = SpeechAssets.Voices.FirstOrDefault(v => v.Id == settings.Voice) ?? SpeechAssets.Voices.First(v => v.Language == selectedLanguage);
        if (!File.Exists(Assets.PiperExecutable) || !File.Exists(Assets.VoicePath(voice)))
            throw new InvalidOperationException("Install local voices in Settings before using speech output.");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Assets.VoicePath(voice) + ".json", cancellationToken).ConfigureAwait(false));
        var sampleRate = config.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        var start = new ProcessStartInfo(Assets.PiperExecutable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new UTF8Encoding(false), WorkingDirectory = Path.GetDirectoryName(Assets.PiperExecutable)! };
        foreach (var argument in new[] { "--model", Assets.VoicePath(voice), "--output_raw", "--length_scale", (1 / Math.Clamp(settings.SpeechSpeed, .5, 2)).ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Piper could not start.");
        lock (gate)
            synthesizer = process;
        using var cancellation = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        using var pcm = new MemoryStream();
        try
        {
            var output = process.StandardOutput.BaseStream.CopyToAsync(pcm, cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteLineAsync(text.Replace('\r', ' ').Replace('\n', ' ').AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(output, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
            var detail = await error.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0 || pcm.Length == 0)
                throw new InvalidOperationException("Local voice synthesis failed. " + detail[..Math.Min(detail.Length, 600)]);
            return (pcm.ToArray(), sampleRate, voice.Language);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch (InvalidOperationException) { }
            lock (gate)
                if (synthesizer == process)
                    synthesizer = null;
        }
    }
    private static string DetectLanguage(string text) => text.Any(c => c is >= '\u0600' and <= '\u06ff') ? "fa" : text.IndexOfAny(['ğ', 'Ğ', 'ı', 'İ', 'ş', 'Ş', 'ç', 'Ç', 'ö', 'Ö', 'ü', 'Ü']) >= 0 ? "tr" : "en";
    public void StopPlayback()
    {
        lock (gate)
        {
            try
            {
                speechCancellation?.Cancel();
                playback?.Stop();
                if (synthesizer is { HasExited: false })
                    synthesizer.Kill(true);
            }
            catch (InvalidOperationException) { }
        }
    }
    public void Stop()
    {
        operation.Cancel();
        StopPlayback();
        Recording? current;
        lock (gate)
            current = recording;
        if (current is not null)
            CancelRecording(current);
    }
    private void CancelRecording(Recording current)
    {
        PublishCaptureStatus(current.Status with
        {
            Outcome = SpeechCaptureOutcome.Cancelled,
            Message = "Recording was cancelled. Audio was discarded."
        });
        current.PreviewCancellation.Cancel();
        lock (current.Gate)
        {
            if (!current.Stopping)
            {
                current.Stopping = true;
                current.Input.StopRecording();
            }
        }
        lock (gate)
            if (recording == current)
                recording = null;
        _ = current.Stopped.Task.ContinueWith(_ => ReleaseRecording(current), TaskScheduler.Default);
    }
    private void ReleaseRecording(Recording current)
    {
        lock (gate)
        {
            if (current.Released)
                return;
            current.Released = true;
            if (recording == current)
                recording = null;
            current.Input.Dispose();
            lock (current.Gate)
                current.Pcm.Dispose();
            // Preview token source stays alive until its async callback exits; cancellation does not retain audio.
            current.PreviewCancellation.Cancel();
        }
    }
    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        Stop();
        // Never dispose a whisper context while native decoding is still running.
        _ = Task.Run(async () => { await inferenceLock.WaitAsync().ConfigureAwait(false); try { factory?.Dispose(); factory = null; } finally { inferenceLock.Release(); } });
        GC.SuppressFinalize(this);
    }
}
