using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Clicky.Core;

namespace Clicky.Windows.Speech;

public sealed record LocalVoice(string Id, string Language, string Name, string ModelPath, string ModelSha256, string ConfigSha256);

public sealed class SpeechAssets
{
    private readonly AppSettings settings;
    private readonly SemaphoreSlim installLock = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    public const string VoiceRevision = "39ab474be869e9181350af6a65e4953eef67aaa0";
    public const string WhisperRevision = "5359861c739e955e79d9a303bcbc70fb988958b1";
    public const string WhisperSha256 = "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b";
    public const string PiperSha256 = "f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea";
    public static IReadOnlyList<LocalVoice> Voices
    {
        get;
    } =
    [
        new("en_US-lessac-medium", "en", "Lessac · English", "en/en_US/lessac/medium/en_US-lessac-medium.onnx", "5efe09e69902187827af646e1a6e9d269dee769f9877d17b16b1b46eeaaf019f", "efe19c417bed055f2d69908248c6ba650fa135bc868b0e6abb3da181dab690a0"),
        new("fa_IR-amir-medium", "fa", "Amir · فارسی", "fa/fa_IR/amir/medium/fa_IR-amir-medium.onnx", "fb815380d969ea372b0b21b0de14421f58fe481047e153e69685d079b6e1a9d1", "75f918a3bf0f57a9179abe725af529f2a5c79d6c899e2a84aec76c685d5dfb9a"),
        new("tr_TR-dfki-medium", "tr", "DFKI · Türkçe", "tr/tr_TR/dfki/medium/tr_TR-dfki-medium.onnx", "2844717f524ab965d3fe86e60562cbb601d3e456836efcc2196cc3a14112a8fb", "13ebd7810f1b61b5027583cf3131a0a233b6ea81c38f2200ebc4ff41c3cca039")
    ];
    public SpeechAssets(AppSettings settings)
    {
        this.settings = settings;
    }
    public string WhisperModelPath => Path.Combine(settings.ModelDirectory, "Speech", "ggml-small.bin");
    public string PiperExecutable => Path.Combine(settings.RuntimeDirectory, "piper-2023.11.14-2", "piper", "piper.exe");
    public string VoicePath(LocalVoice voice) => Path.Combine(settings.ModelDirectory, "Speech", voice.Id + ".onnx");
    public bool RecognitionInstalled => File.Exists(WhisperModelPath);
    public bool VoicesInstalled => File.Exists(PiperExecutable) && Voices.All(v => File.Exists(VoicePath(v)) && File.Exists(VoicePath(v) + ".json"));
    public bool IsInstalled => RecognitionInstalled && VoicesInstalled;
    public Task InstallAsync(IProgress<string> progress, CancellationToken cancellationToken = default) => InstallAsync(progress.Report, cancellationToken);
    public async Task InstallAsync(Action<string>? onProgress = null, CancellationToken cancellationToken = default)
    {
        await installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DownloadAsync($"https://huggingface.co/ggerganov/whisper.cpp/resolve/{WhisperRevision}/ggml-small.bin", WhisperModelPath, WhisperSha256, "Multilingual speech recognition", onProgress, cancellationToken).ConfigureAwait(false);
            var archive = Path.Combine(settings.RuntimeDirectory, "Downloads", "piper_windows_amd64-2023.11.14-2.zip");
            await DownloadAsync("https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip", archive, PiperSha256, "Local Piper voice runtime", onProgress, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(PiperExecutable))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(settings.RuntimeDirectory, "piper-2023.11.14-2");
                Directory.CreateDirectory(target);
                ZipFile.ExtractToDirectory(archive, target, true);
                if (!File.Exists(PiperExecutable))
                    throw new InvalidDataException("Piper archive did not contain the expected Windows runtime.");
            }
            foreach (var voice in Voices)
            {
                var url = $"https://huggingface.co/rhasspy/piper-voices/resolve/{VoiceRevision}/{voice.ModelPath}";
                await DownloadAsync(url, VoicePath(voice), voice.ModelSha256, voice.Name, onProgress, cancellationToken).ConfigureAwait(false);
                await DownloadAsync(url + ".json", VoicePath(voice) + ".json", voice.ConfigSha256, voice.Name + " configuration", onProgress, cancellationToken).ConfigureAwait(false);
                var card = Path.Combine(Path.GetDirectoryName(VoicePath(voice))!, voice.Id + ".MODEL_CARD");
                if (!File.Exists(card))
                {
                    var modelCardUrl = $"https://huggingface.co/rhasspy/piper-voices/resolve/{VoiceRevision}/{voice.ModelPath[..voice.ModelPath.LastIndexOf('/')]}/MODEL_CARD";
                    var content = await Http.GetStringAsync(modelCardUrl, cancellationToken).ConfigureAwait(false);
                    await File.WriteAllTextAsync(card, content, cancellationToken).ConfigureAwait(false);
                }
            }
            onProgress?.Invoke("Local speech recognition and all three voices are ready.");
        }
        finally { installLock.Release(); }
    }
    public static async Task DownloadAsync(string url, string destination, string sha256, string name, Action<string>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination) && await HashMatches(destination, sha256, cancellationToken).ConfigureAwait(false))
        {
            progress?.Invoke(name + " verified.");
            return;
        }
        var part = destination + ".part";
        var existingLength = File.Exists(part) ? new FileInfo(part).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingLength > 0)
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && File.Exists(part))
        {
            if (await HashMatches(part, sha256, cancellationToken).ConfigureAwait(false))
            {
                File.Move(part, destination, true);
                return;
            }
            File.Delete(part);
            await DownloadAsync(url, destination, sha256, name, progress, cancellationToken).ConfigureAwait(false);
            return;
        }
        response.EnsureSuccessStatusCode();
        var append = response.StatusCode == HttpStatusCode.PartialContent && existingLength > 0;
        if (!append)
            existingLength = 0;
        var total = response.Content.Headers.ContentLength.GetValueOrDefault() + existingLength;
        await using (var output = new FileStream(part, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[65536];
            var downloaded = existingLength;
            var lastProgress = 0L;
            int count;
            while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                downloaded += count;
                if (Environment.TickCount64 - lastProgress > 300)
                {
                    progress?.Invoke(total > 0 ? $"{name}: {downloaded * 100 / total}% ({downloaded / 1048576} MB)" : $"{name}: {downloaded / 1048576} MB");
                    lastProgress = Environment.TickCount64;
                }
            }
        }
        if (!await HashMatches(part, sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(part);
            throw new InvalidDataException(name + " failed SHA-256 verification. The incomplete download was removed; retry installation.");
        }
        File.Move(part, destination, true);
        progress?.Invoke(name + " verified.");
    }
    private static async Task<bool> HashMatches(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}
