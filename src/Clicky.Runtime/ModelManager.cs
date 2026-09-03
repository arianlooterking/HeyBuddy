using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Clicky.Core;
using Microsoft.Win32.SafeHandles;

namespace Clicky.Runtime;

public sealed record ModelAsset(string FileName, string Url, string Sha256, long Bytes, bool IsRuntime = false);
public sealed record DownloadProgress(string FileName, long BytesReceived, long TotalBytes, string Stage)
{
    public double Percent => TotalBytes <= 0 ? 0 : 100.0 * BytesReceived / TotalBytes;
}
public sealed record ModelStatus(bool Installed, bool Running, string ModelPath, string RuntimePath, string Message, long DownloadBytes);
public sealed record ModelStartupTiming(double VerificationMilliseconds, double WorkerLoadMilliseconds);
internal sealed record LocalModelConnection(Uri Endpoint, string ApiKey)
{
    public override string ToString() => Endpoint.AbsoluteUri + " (authenticated local worker)";
}

/// <summary>Owns only Clicky's pinned files and its own worker. Does not discover, stop, or change other model servers.</summary>
public sealed class ModelManager : IAsyncDisposable
{
    public const string ModelRevision = "e87f176479d0855a907a41277aca2f8ee7a09523";
    public const string RuntimeVersion = "b10621";
    public static IReadOnlyList<ModelAsset> Catalog
    {
        get;
    } = [
        new("Qwen3.5-4B-Q4_K_M.gguf", $"https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/{ModelRevision}/Qwen3.5-4B-Q4_K_M.gguf", "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4", 2740937888),
        new("mmproj-F16.gguf", $"https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/{ModelRevision}/mmproj-F16.gguf", "cd88edcf8d031894960bb0c9c5b9b7e1fea6ebee02b9f7ce925a00d12891f864", 672423616),
        new("llama-b10621-bin-win-cuda-12.4-x64.zip", "https://github.com/ggml-org/llama.cpp/releases/download/b10621/llama-b10621-bin-win-cuda-12.4-x64.zip", "81c2ff62e14b549cd5c766ccdd5c61f09e821a171655c3047bdccfddc2d1a1e2", 250464283, true),
        new("cudart-llama-bin-win-cuda-12.4-x64.zip", "https://github.com/ggml-org/llama.cpp/releases/download/b10621/cudart-llama-bin-win-cuda-12.4-x64.zip", "8c79a9b226de4b3cacfd1f83d24f962d0773be79f1e7b75c6af4ded7e32ae1d6", 391443627, true)
    ];

    private readonly AppSettings settings;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly SemaphoreSlim installation = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly HttpClient downloadClient = new() { Timeout = Timeout.InfiniteTimeSpan };
    private Process? worker;
    private WorkerJob? job;
    private string? apiKey;
    private Uri? endpoint;
    private string diagnostic = "Local runtime has not started.";
    private bool disposed;
    private readonly Queue<string> recentLines = new();
    private readonly ConcurrentDictionary<string, (long Length, DateTime Time)> verifiedFiles = new(StringComparer.OrdinalIgnoreCase);
    public event Action<string>? StatusChanged;
    public ModelStartupTiming? LastStartupTiming
    {
        get; private set;
    }
    private void SetStatus(string message)
    {
        diagnostic = message;
        StatusChanged?.Invoke(message);
    }

    public ModelManager(AppSettings settings)
    {
        this.settings = settings;
        downloadClient.DefaultRequestHeaders.UserAgent.ParseAdd("HeyBuddy/0.1");
    }

    public string ModelPath => Path.Combine(Path.GetFullPath(settings.ModelDirectory), Catalog[0].FileName);
    public string ProjectorPath => Path.Combine(Path.GetFullPath(settings.ModelDirectory), Catalog[1].FileName);
    public string RuntimePath => Path.Combine(Path.GetFullPath(settings.RuntimeDirectory), RuntimeVersion);
    public string ExecutablePath => Path.Combine(RuntimePath, "llama-server.exe");
    internal string ApiKey => apiKey ?? throw new InvalidOperationException("The local worker is not running.");

    public ModelStatus GetStatus() => new(File.Exists(ModelPath) && File.Exists(ProjectorPath) && File.Exists(ExecutablePath),
        WorkerIsRunning(), ModelPath, ExecutablePath, diagnostic, Catalog.Sum(x => x.Bytes));

    private bool WorkerIsRunning()
    {
        try
        {
            return worker is { HasExited: false };
        }
        catch (InvalidOperationException) { return false; }
    }

    internal async Task<LocalModelConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WorkerIsRunning() || endpoint is null || apiKey is null)
                throw new InvalidOperationException("The local worker stopped before the request began. Retry when ready.");
            return new(endpoint, apiKey);
        }
        finally { lifecycle.Release(); }
    }

    public async Task InstallAsync(IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = operation.Token;
        await installation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(settings.ModelDirectory);
            var downloads = Path.Combine(settings.RuntimeDirectory, "Downloads");
            Directory.CreateDirectory(downloads);
            foreach (var asset in Catalog)
            {
                var path = Path.Combine(asset.IsRuntime ? downloads : settings.ModelDirectory, asset.FileName);
                await DownloadVerifiedAsync(asset, path, progress, cancellationToken).ConfigureAwait(false);
            }
            await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!File.Exists(ExecutablePath) || !File.Exists(Path.Combine(RuntimePath, "clicky-runtime.json")))
                {
                    if (worker is { HasExited: false })
                        throw new InvalidOperationException("Stop the local model before installing its runtime.");
                    Directory.CreateDirectory(RuntimePath);
                    foreach (var asset in Catalog.Where(a => a.IsRuntime))
                    {
                        progress?.Report(new(asset.FileName, asset.Bytes, asset.Bytes, "Installing runtime"));
                        using var archive = ZipFile.OpenRead(Path.Combine(downloads, asset.FileName));
                        foreach (var entry in archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            // Official packages may contain a bin directory. Only Windows binaries are installed.
                            if (!new[] { ".exe", ".dll" }.Contains(Path.GetExtension(entry.Name), StringComparer.OrdinalIgnoreCase))
                                continue;
                            entry.ExtractToFile(Path.Combine(RuntimePath, Path.GetFileName(entry.Name)), true);
                        }
                    }
                    if (!File.Exists(ExecutablePath))
                        throw new InvalidDataException("The verified runtime package did not contain llama-server.exe.");
                    var manifest = new List<object>();
                    foreach (var file in Directory.EnumerateFiles(RuntimePath).Where(p => p.EndsWith(".exe") || p.EndsWith(".dll")))
                        manifest.Add(new
                        {
                            file = Path.GetFileName(file),
                            sha256 = await HashAsync(file, cancellationToken).ConfigureAwait(false)
                        });
                    await File.WriteAllTextAsync(Path.Combine(RuntimePath, "clicky-runtime.json"), JsonSerializer.Serialize(new
                    {
                        version = RuntimeVersion,
                        files = manifest
                    }), cancellationToken).ConfigureAwait(false);
                }
            }
            finally { lifecycle.Release(); }
            SetStatus("Pinned model, vision projector, and CUDA runtime installed and checksum verified.");
            progress?.Report(new("Local AI", Catalog.Sum(x => x.Bytes), Catalog.Sum(x => x.Bytes), "Ready"));
        }
        finally { installation.Release(); }
    }

    public async Task DownloadVerifiedAsync(ModelAsset asset, string destination, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(destination) && await VerifyFileAsync(destination, asset, ct).ConfigureAwait(false))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var part = destination + ".part";
        if (File.Exists(part) && new FileInfo(part).Length > asset.Bytes)
            File.Delete(part);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (offset == asset.Bytes)
                break;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
                if (offset > 0)
                    request.Headers.Range = new RangeHeaderValue(offset, null);
                using var headerDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                using var response = await downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headerDeadline.Token).ConfigureAwait(false);
                headerDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
                response.EnsureSuccessStatusCode();
                if (offset > 0 && response.StatusCode != HttpStatusCode.PartialContent)
                    offset = 0;
                if (response.StatusCode == HttpStatusCode.PartialContent && response.Content.Headers.ContentRange?.From != offset)
                    throw new InvalidDataException("Download server returned an unexpected resume range.");
                await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var output = new FileStream(part, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 128 * 1024, true);
                var buffer = new byte[128 * 1024];
                var nextUpdate = Stopwatch.StartNew();
                while (true)
                {
                    using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    readDeadline.CancelAfter(TimeSpan.FromSeconds(90));
                    var count = await input.ReadAsync(buffer, readDeadline.Token).ConfigureAwait(false);
                    if (count == 0)
                        break;
                    if (offset + count > asset.Bytes)
                        throw new InvalidDataException("Download exceeded its pinned size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                    offset += count;
                    if (nextUpdate.ElapsedMilliseconds >= 200)
                    {
                        progress?.Report(new(asset.FileName, offset, asset.Bytes, "Downloading"));
                        nextUpdate.Restart();
                    }
                }
                if (offset != asset.Bytes)
                    throw new IOException("Download ended early; partial data is saved for resuming.");
                break;
            }
            catch (Exception ex) when (attempt < 2 && !ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        progress?.Report(new(asset.FileName, asset.Bytes, asset.Bytes, "Verifying SHA-256"));
        if (!await VerifyFileAsync(part, asset, ct).ConfigureAwait(false))
        {
            // A bad completed partial cannot be resumed; keep it for diagnosis outside the next download path.
            if (File.Exists(part))
                File.Move(part, part + ".rejected-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true);
            throw new InvalidDataException($"{asset.FileName} failed its published SHA-256 checksum. No unverified file was installed.");
        }
        File.Move(part, destination, true);
        CacheVerified(destination);
    }

    private async Task<bool> VerifyFileAsync(string path, ModelAsset asset, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != asset.Bytes)
            return false;
        if (verifiedFiles.TryGetValue(path, out var old) && old == (info.Length, info.LastWriteTimeUtc))
            return true;
        var verified = string.Equals(await HashAsync(path, ct).ConfigureAwait(false), asset.Sha256, StringComparison.OrdinalIgnoreCase);
        if (verified)
            CacheVerified(path);
        return verified;
    }

    private void CacheVerified(string path)
    {
        var info = new FileInfo(path);
        verifiedFiles[path] = (info.Length, info.LastWriteTimeUtc);
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    public async Task<Uri> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellationToken = operation.Token;
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (worker is { HasExited: false } && endpoint != null)
                return endpoint;
            if (!GetStatus().Installed)
                throw new InvalidOperationException("Local AI needs its model and runtime. Open Models and choose Install local AI (about 4.1 GB download).");
            var verification = Stopwatch.StartNew();
            SetStatus("Verifying local model files…");
            if (!await VerifyFileAsync(ModelPath, Catalog[0], cancellationToken).ConfigureAwait(false) || !await VerifyFileAsync(ProjectorPath, Catalog[1], cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("A model file changed or is incomplete. Use Install local AI to repair it.");
            SetStatus("Verifying the local inference runtime…");
            var manifestPath = Path.Combine(RuntimePath, "clicky-runtime.json");
            if (!File.Exists(manifestPath))
                throw new InvalidDataException("Runtime verification manifest is missing. Run Install local AI to repair it.");
            using (var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false)))
            {
                foreach (var file in manifest.RootElement.GetProperty("files").EnumerateArray())
                {
                    var path = Path.Combine(RuntimePath, Path.GetFileName(file.GetProperty("file").GetString()!));
                    if (!File.Exists(path) || !string.Equals(await HashAsync(path, cancellationToken).ConfigureAwait(false), file.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"A local AI runtime binary changed. Quit HeyBuddy, move this runtime folder aside, then verify the installation again: {RuntimePath}");
                }
            }
            var verificationMilliseconds = verification.Elapsed.TotalMilliseconds;
            var workerLoad = Stopwatch.StartNew();
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            apiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            endpoint = new Uri($"http://127.0.0.1:{port}/v1/");
            var start = new ProcessStartInfo(ExecutablePath) { WorkingDirectory = RuntimePath, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            // Credentials belong in the child environment, never the command line, a config file, or a log.
            start.Environment["LLAMA_API_KEY"] = apiKey;
            foreach (var arg in new[] { "--model", ModelPath, "--mmproj", ProjectorPath, "--host", "127.0.0.1", "--port", port.ToString(),
                "--alias", "qwen3.5-4b", "--ctx-size", Math.Clamp(settings.ContextSize, 2048, 16384).ToString(),
                "--n-gpu-layers", Math.Clamp(settings.GpuLayers, 0, 24).ToString(), "--threads", Math.Clamp(settings.CpuThreads, 1, 16).ToString(),
                "--parallel", "1", "--batch-size", "128", "--ubatch-size", "128", settings.VisionProjectorGpu ? "--mmproj-offload" : "--no-mmproj-offload", "--jinja",
                "--chat-template-kwargs", "{\"enable_thinking\":false}", "--reasoning-format", "deepseek", "--no-webui" })
                start.ArgumentList.Add(arg);
            worker?.Dispose();
            job?.Dispose();
            worker = new Process { StartInfo = start };
            worker.OutputDataReceived += CaptureDiagnostic;
            worker.ErrorDataReceived += CaptureDiagnostic;
            lock (recentLines)
                recentLines.Clear();
            if (!worker.Start())
                throw new IOException("Windows could not start the local model worker.");
            if (OperatingSystem.IsWindows())
                job = WorkerJob.Attach(worker);
            worker.BeginOutputReadLine();
            worker.BeginErrorReadLine();
            SetStatus("Loading Qwen and the vision projector…");
            using var health = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
            health.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(3))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (worker.HasExited)
                    throw new IOException($"Local model worker exited ({worker.ExitCode}). {LastDiagnostic()} Reduce GPU layers or close another GPU application, then retry.");
                try
                {
                    using var result = await health.GetAsync(new Uri(endpoint, "../health"), cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccessStatusCode)
                    {
                        LastStartupTiming = new(verificationMilliseconds, workerLoad.Elapsed.TotalMilliseconds);
                        SetStatus(settings.VisionProjectorGpu ? "Local Qwen is running. Vision uses the GPU." : "Local Qwen is running. Vision uses CPU to leave GPU headroom.");
                        return endpoint;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException) { }
                await Task.Delay(400, cancellationToken).ConfigureAwait(false);
            }
            throw new TimeoutException("The local model did not become ready within three minutes. Check free memory or lower the context size.");
        }
        catch { await StopWorkerAsync().ConfigureAwait(false); throw; }
        finally { lifecycle.Release(); }
    }

    private void CaptureDiagnostic(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;
        var line = apiKey is null ? e.Data : e.Data.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
        lock (recentLines)
        {
            recentLines.Enqueue(line);
            while (recentLines.Count > 8)
                recentLines.Dequeue();
        }
    }
    private string LastDiagnostic()
    {
        lock (recentLines)
            return string.Join(" ", recentLines).Trim()[..Math.Min(1500, string.Join(" ", recentLines).Trim().Length)];
    }
    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopWorkerAsync().ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }
    private async Task StopWorkerAsync()
    {
        if (worker is { HasExited: false })
        {
            worker.Kill(true);
            await worker.WaitForExitAsync().ConfigureAwait(false);
        }
        worker?.Dispose();
        worker = null;
        job?.Dispose();
        job = null;
        apiKey = null;
        endpoint = null;
        SetStatus("Local runtime is stopped.");
    }
    public async Task RemoveModelsAsync(CancellationToken ct = default)
    {
        await StopAsync().ConfigureAwait(false);
        await installation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var asset in Catalog.Where(x => !x.IsRuntime))
            {
                var path = Path.Combine(Path.GetFullPath(settings.ModelDirectory), asset.FileName);
                if (File.Exists(path) && !await VerifyFileAsync(path, asset, ct).ConfigureAwait(false))
                    throw new InvalidDataException("Refusing to remove a file that does not match HeyBuddy's pinned catalog.");
                if (File.Exists(path))
                    File.Delete(path);
            }
            verifiedFiles.Clear();
            SetStatus("HeyBuddy's two model files were removed. Other models were preserved.");
        }
        finally { installation.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        await installation.WaitAsync().ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
        installation.Release();
        downloadClient.Dispose();
        lifecycle.Dispose();
        installation.Dispose();
        lifetime.Dispose();
    }

    private sealed class WorkerJob : SafeHandleZeroOrMinusOneIsInvalid
    {
        private WorkerJob() : base(true) { }
        internal static WorkerJob Attach(Process process)
        {
            var result = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobLimits { Basic = new BasicLimits { LimitFlags = 0x00002000 } }; // kill on job close
            if (result.IsInvalid || !SetInformationJobObject(result, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()) || !AssignProcessToJobObject(result, process.Handle))
            {
                result.Dispose();
                process.Kill(true);
                throw new IOException("Windows could not bind the AI worker lifetime to HeyBuddy.");
            }
            return result;
        }
        protected override bool ReleaseHandle() => CloseHandle(handle);
        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimits
        {
            public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorkingSet, MaxWorkingSet; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperation, WriteOperation, OtherOperation, ReadBytes, WriteBytes, OtherBytes;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern WorkerJob CreateJobObject(IntPtr attributes, string? name);
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(WorkerJob job, int informationClass, ref JobLimits info, uint length);
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(WorkerJob job, IntPtr process);
        [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}
