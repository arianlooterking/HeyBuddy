using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Clicky.Core;
using Clicky.Windows.Speech;
using NAudio.Wave;

internal static class SpeechDiagnosticsChecks
{
    internal static async Task<int> RunAsync(bool baseline)
    {
        var evidence = new List<object>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var settings = new AppSettings { Language = "auto", Voice = "auto", CpuThreads = 6 };
        using var speech = new SpeechService(settings);
        try
        {
            if (!baseline)
            {
                Check("Empty PCM contains no captured frames", !SpeechInputAnalysis.Analyze([]).CanTranscribe);
                Check("Digital silence is rejected", !SpeechInputAnalysis.Analyze(new byte[32000]).HasSignal);
                Check("DC offset is not speech", !SpeechInputAnalysis.Analyze(Pcm(16000, _ => 3000)).HasSignal);
                Check("One-step quantization jitter is not speech", !SpeechInputAnalysis.Analyze(Pcm(16000, i => i % 2 == 0 ? (short)1 : (short)-1)).HasSignal);
                try
                {
                    SpeechInputAnalysis.Analyze(new byte[3]);
                    throw new Exception("Incomplete PCM sample was accepted.");
                }
                catch (ArgumentException) { Check("Incomplete PCM samples are rejected", true); }

                var decodes = 0;
                speech.Measured += _ => decodes++;
                foreach (var (pcm, expected) in new[] { (Array.Empty<byte>(), SpeechCaptureOutcome.NoAudio), (new byte[200], SpeechCaptureOutcome.TooShort), (new byte[32000], SpeechCaptureOutcome.NoSignal), (Pcm(16000, _ => 3000), SpeechCaptureOutcome.NoSignal) })
                {
                    using var silentWav = ToWav(pcm, 16000);
                    var result = await speech.TranscribeWavAsync(silentWav, "auto", cancellationToken: cancellation.Token);
                    Check("Actual service classifies " + expected, result.Length == 0 && speech.LastCaptureStatus.Outcome == expected);
                }
                Check("No-signal recordings never invoke Whisper", decodes == 0);
            }
            foreach (var (language, text) in new[] { ("en", "Hello Arian. Your local assistant is ready to help you today."), ("fa", "سلام آرین. دستیار شما آماده است. این برنامه روی کامپیوتر شما کار می کند."), ("tr", "Merhaba. Bu asistan Windows bilgisayarınızda çalışıyor ve size yardım etmeye hazır.") })
            {
                var audio = await speech.SynthesizeAsync(text, language, cancellation.Token);
                foreach (var recognition in new[] { "auto", language })
                {
                    using var wav = ToWav(audio.Pcm, audio.SampleRate);
                    var watch = Stopwatch.StartNew();
                    var result = await speech.TranscribeWavAsync(wav, recognition, cancellationToken: cancellation.Token);
                    var row = new
                    {
                        test = "synthetic",
                        language,
                        recognition,
                        elapsedMs = watch.ElapsedMilliseconds,
                        text = result,
                        meaningful = result.Length > 5
                    };
                    evidence.Add(row);
                    Console.WriteLine(JsonSerializer.Serialize(row));
                    if (!baseline)
                    {
                        Check(language + " " + recognition + " produces words", result.Length > 5 && speech.LastCaptureStatus.Outcome == SpeechCaptureOutcome.Recognized);
                        Check(language + " " + recognition + " preserves language", speech.LastCaptureStatus.DetectedLanguage == language);
                    }
                }
                if (!baseline && language == "en")
                {
                    var sourcePeak = Enumerable.Range(0, audio.Pcm.Length / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(audio.Pcm, i * 2)));
                    var attenuated = Pcm(audio.Pcm.Length / 2, i => (short)Math.Round(BitConverter.ToInt16(audio.Pcm, i * 2) * (130d / sourcePeak)));
                    using var quietWav = ToWav(attenuated, audio.SampleRate);
                    var quiet = await speech.TranscribeWavAsync(quietWav, "auto", cancellationToken: cancellation.Token);
                    var info = speech.LastCaptureStatus;
                    Check("Speech below the former .012 gate is decoded", info.Peak < .006 && info.Gain > 1 && quiet.Contains("assistant", StringComparison.OrdinalIgnoreCase));
                    evidence.Add(new
                    {
                        test = "quiet-synthetic",
                        input = text,
                        transcript = quiet,
                        info
                    });
                    var analysis = SpeechInputAnalysis.Analyze(Pcm(16000, i => (short)(100 * Math.Sin(i * .06))));
                    var prepared = analysis.Prepare(Pcm(16000, i => (short)(100 * Math.Sin(i * .06))));
                    Check("Quiet normalization is bounded and does not clip", prepared.Gain is > 1 and <= 32 && prepared.Samples.Max(Math.Abs) <= .951);

                    using var cancel = new CancellationTokenSource(100);
                    using var cancelledWav = ToWav(audio.Pcm, audio.SampleRate);
                    var callbacks = 0;
                    try
                    {
                        await speech.TranscribeWavAsync(cancelledWav, "auto", _ => callbacks++, cancel.Token);
                        throw new Exception("Speech decoding ignored cancellation.");
                    }
                    catch (OperationCanceledException) { }
                    var atCancellation = callbacks;
                    await Task.Delay(100, cancellation.Token);
                    Check("Cancellation ends recognition without later transcript callbacks", callbacks == atCancellation);
                }
                if (baseline)
                    break;
            }
            return 0;
        }
        catch (Exception exception)
        {
            evidence.Add(new
            {
                error = exception.Message
            });
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/native"));
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, baseline ? "speech-baseline.json" : "speech-diagnostics.json"), JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        }

        void Check(string name, bool passed)
        {
            evidence.Add(new
            {
                test = name,
                passed
            });
            Console.WriteLine((passed ? "PASS " : "FAIL ") + name);
            if (!passed)
                throw new InvalidOperationException(name);
        }
    }

    internal static MemoryStream ToWav(byte[] pcm, int rate)
    {
        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(new NAudio.Utils.IgnoreDisposeStream(wav), new WaveFormat(rate, 16, 1)))
            writer.Write(pcm, 0, pcm.Length);
        wav.Position = 0;
        return wav;
    }

    private static byte[] Pcm(int count, Func<int, short> sample)
    {
        var result = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var value = sample(index);
            result[index * 2] = (byte)value;
            result[index * 2 + 1] = (byte)(value >> 8);
        }
        return result;
    }
}
