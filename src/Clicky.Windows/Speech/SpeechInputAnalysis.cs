namespace Clicky.Windows.Speech;

public enum SpeechCaptureOutcome
{
    Idle, Recording, NoAudio, TooShort, NoSignal, Quiet, Transcribing, Recognized, RecognizerEmpty, Cancelled, Error
}

/// <summary>Audio metadata only. No PCM or transcript is retained in capture diagnostics.</summary>
public sealed record SpeechCaptureStatus(SpeechCaptureOutcome Outcome, string Message, int DeviceId = -1,
    string DeviceName = "", double AudioSeconds = 0, double Rms = 0, double Peak = 0, double Gain = 1,
    string RequestedLanguage = "auto", string DetectedLanguage = "");

public sealed record SpeechInputAnalysis(int Samples, double AudioSeconds, double Rms, double Peak,
    double Mean, double DynamicRms, double MaximumFrameRms, bool HasSignal)
{
    public bool IsQuiet => HasSignal && MaximumFrameRms < .012;
    public bool CanTranscribe => Samples >= 1600 && HasSignal;

    public static SpeechInputAnalysis Analyze(ReadOnlySpan<byte> pcm)
    {
        var meter = new SpeechInputMeter();
        const int frameBytes = 2560; // Same 80ms frames used by the 16kHz microphone capture.
        for (var offset = 0; offset < pcm.Length; offset += frameBytes)
            meter.Add(pcm.Slice(offset, Math.Min(frameBytes, pcm.Length - offset)));
        return meter.Snapshot();
    }

    public (float[] Samples, double Gain) Prepare(ReadOnlySpan<byte> pcm)
    {
        if ((pcm.Length & 1) != 0)
            throw new ArgumentException("Audio must contain complete 16-bit PCM samples.");
        // Quiet input is still decoded. A bounded gain helps USB microphones with low output;
        // exact silence and DC-only signals never reach this amplification step.
        var gain = HasSignal && IsQuiet ? Math.Min(32, .04 / DynamicRms) : 1;
        var centeredPeakBound = Peak + Math.Abs(Mean);
        if (centeredPeakBound > 0)
            gain = Math.Min(gain, .95 / centeredPeakBound);
        var samples = new float[pcm.Length / 2];
        if (HasSignal)
            for (var index = 0; index < samples.Length; index++)
                samples[index] = (float)(((short)(pcm[index * 2] | pcm[index * 2 + 1] << 8) / 32768d - Mean) * gain);
        return (samples, gain);
    }
}

/// <summary>Incremental signal meter; energy indicates an audio signal, not proof of human speech.</summary>
internal sealed class SpeechInputMeter
{
    private int count;
    private double sum;
    private double squares;
    private double peak;
    private double minimum = double.PositiveInfinity;
    private double maximum = double.NegativeInfinity;
    private double maximumFrameRms;

    internal SpeechInputAnalysis Add(ReadOnlySpan<byte> pcm)
    {
        if ((pcm.Length & 1) != 0)
            throw new ArgumentException("Audio must contain complete 16-bit PCM samples.");
        double frameSquares = 0, frameSum = 0;
        for (var index = 0; index < pcm.Length; index += 2)
        {
            var sample = (short)(pcm[index] | pcm[index + 1] << 8) / 32768d;
            frameSquares += sample * sample;
            frameSum += sample;
            minimum = Math.Min(minimum, sample);
            maximum = Math.Max(maximum, sample);
            peak = Math.Max(peak, Math.Abs(sample));
        }
        var frameCount = pcm.Length / 2;
        if (frameCount > 0)
            maximumFrameRms = Math.Max(maximumFrameRms, Math.Sqrt(frameSquares / frameCount));
        count += frameCount;
        sum += frameSum;
        squares += frameSquares;
        var frameMean = frameSum / Math.Max(1, frameCount);
        var frameDynamic = Math.Sqrt(Math.Max(0, frameSquares / Math.Max(1, frameCount) - frameMean * frameMean));
        // One quantization step of jitter is not a usable microphone signal.
        return new(frameCount, frameCount / 16000d, Math.Sqrt(frameSquares / Math.Max(1, frameCount)), 0,
            frameMean, frameDynamic, 0, frameDynamic > 1d / 32768);
    }

    internal SpeechInputAnalysis Snapshot()
    {
        var mean = sum / Math.Max(1, count);
        var dynamic = Math.Sqrt(Math.Max(0, squares / Math.Max(1, count) - mean * mean));
        var hasSignal = count > 0 && dynamic > 1d / 32768 && maximum - minimum > 4d / 32768;
        return new(count, count / 16000d, Math.Sqrt(squares / Math.Max(1, count)), peak, mean, dynamic, maximumFrameRms, hasSignal);
    }
}
