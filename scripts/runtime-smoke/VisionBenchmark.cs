using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;
using Clicky.Windows.Native;

internal static class VisionBenchmark
{
    private const int MaximumTotalVramMiB = 7168;

    public static async Task RunAsync(string sourcePath)
    {
        var image = ImagePreparation.ForModel(new(Convert.ToBase64String(await File.ReadAllBytesAsync(sourcePath)), "image/png", "HeyBuddy saved application screenshot"), 768);
        var bytes = Convert.FromBase64String(image.Base64);
        var output = Path.Combine(Environment.CurrentDirectory, "scripts", "runtime-smoke", "output");
        Directory.CreateDirectory(output);
        await File.WriteAllBytesAsync(Path.Combine(output, "vision-768.png"), bytes);
        var evidence = new List<object>();
        var baseline = await ReadVramAsync();
        Console.WriteLine($"Baseline total GPU memory: {baseline} MiB. Benchmark ceiling: {MaximumTotalVramMiB} MiB.");
        if (baseline > 3000) throw new InvalidOperationException("Baseline GPU memory exceeds 3,000 MiB; do not compete with another GPU application.");

        foreach (var gpu in new[] { false, true })
        {
            var settings = new AppSettings { Provider = "local", GpuLayers = 24, ContextSize = 8192, CpuThreads = 6, VisionProjectorGpu = gpu };
            await using var factory = new ModelProviderFactory(settings, new EmptyCredentials());
            using var run = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await using var memory = new VramMonitor(run, MaximumTotalVramMiB);
            var mode = gpu ? "gpu" : "cpu";
            try
            {
                await memory.StartAsync();
                var watch = Stopwatch.StartNew();
                await factory.ModelManager.StartAsync(run.Token);
                var startupMs = watch.ElapsedMilliseconds;
                var provider = factory.Create();
                // Initialize text generation before timing the image encoder on either backend.
                await provider.CompleteAsync(new([new("user", "Say ready.")], MaxTokens: 16), null, run.Token);
                var repetitions = gpu ? 2 : 1;
                for (var repetition = 1; repetition <= repetitions; repetition++)
                {
                    watch.Restart();
                    var firstTokenMs = -1L;
                    var reply = await provider.CompleteAsync(new([new("user", "Identify the app name and name two visible buttons in this screenshot. Answer in one brief sentence.", [image])], MaxTokens: 96), _ =>
                    {
                        if (firstTokenMs < 0) firstTokenMs = watch.ElapsedMilliseconds;
                    }, run.Token);
                    var sample = new { mode, repetition, startupMs, firstTokenMs, totalMs = watch.ElapsedMilliseconds, baselineMiB = baseline, peakTotalVramMiB = memory.PeakMiB, memorySamples = memory.Samples, text = reply.Text };
                    evidence.Add(sample);
                    Console.WriteLine(JsonSerializer.Serialize(sample));
                }
            }
            catch (Exception exception)
            {
                var sample = new { mode, error = memory.LimitExceeded ? "Total VRAM exceeded the 7 GiB benchmark ceiling; own worker cancelled." : exception.Message, peakTotalVramMiB = memory.PeakMiB, memorySamples = memory.Samples };
                evidence.Add(sample);
                Console.WriteLine(JsonSerializer.Serialize(sample));
            }
            finally
            {
                await factory.ModelManager.StopAsync();
                Console.WriteLine($"Own {mode} projector worker stopped.");
            }
            if (memory.LimitExceeded) break;
        }

        var finalMemory = await ReadVramAsync();
        var report = new { recordedAt = DateTimeOffset.UtcNow, source = Path.GetFullPath(sourcePath), maximumEdge = 768, imageSha256 = Convert.ToHexString(SHA256.HashData(bytes)), modelRevision = ModelManager.ModelRevision, runtime = ModelManager.RuntimeVersion, settings = new { gpuLayers = 24, contextTokens = 8192, threads = 6 }, maximumTotalVramMiB = MaximumTotalVramMiB, baselineMiB = baseline, afterWorkersStoppedMiB = finalMemory, measurements = evidence };
        var reportPath = Path.Combine(output, "vision-projector-benchmark.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(reportPath);
        Console.WriteLine($"All benchmark workers stopped. Total GPU memory: {finalMemory} MiB.");
    }

    private static ProcessStartInfo SmiStart(bool loop)
    {
        var start = new ProcessStartInfo("nvidia-smi.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--query-gpu=memory.used");
        start.ArgumentList.Add("--format=csv,noheader,nounits");
        start.ArgumentList.Add("--id=0");
        if (loop) start.ArgumentList.Add("--loop-ms=200");
        return start;
    }

    private static async Task<int> ReadVramAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var process = Process.Start(SmiStart(false)) ?? throw new IOException("nvidia-smi could not start.");
        var value = await process.StandardOutput.ReadLineAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return int.Parse(value ?? throw new IOException("nvidia-smi returned no memory measurement."), CultureInfo.InvariantCulture);
    }

    private sealed class VramMonitor(CancellationTokenSource run, int limitMiB) : IAsyncDisposable
    {
        private Process? process;
        private Task? reader;
        private readonly CancellationTokenSource stop = new();
        public int PeakMiB { get; private set; }
        public int Samples { get; private set; }
        public bool LimitExceeded { get; private set; }

        public async Task StartAsync()
        {
            process = Process.Start(SmiStart(true)) ?? throw new IOException("nvidia-smi telemetry could not start.");
            var first = await process.StandardOutput.ReadLineAsync(run.Token);
            Observe(first);
            reader = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var line = await process.StandardOutput.ReadLineAsync(stop.Token);
                        if (line is null) { run.Cancel(); return; }
                        Observe(line);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
        }

        private void Observe(string? line)
        {
            if (!int.TryParse(line, CultureInfo.InvariantCulture, out var used)) throw new IOException("GPU memory telemetry was unavailable; benchmark cancelled.");
            PeakMiB = Math.Max(PeakMiB, used);
            Samples++;
            if (used > limitMiB) { LimitExceeded = true; run.Cancel(); }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            if (process is { HasExited: false }) process.Kill();
            if (reader is not null) await reader;
            process?.Dispose();
            stop.Dispose();
        }
    }
}
