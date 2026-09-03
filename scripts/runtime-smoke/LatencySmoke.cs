using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;

internal static class LatencySmoke
{
    internal static async Task RunAsync(string label)
    {
        if (label is not ("before" or "after" or "run")) throw new ArgumentException("Use before, after or run as the benchmark label.");
        var settings = new AppSettings { Provider = "local", GpuLayers = 24, ContextSize = 8192, CpuThreads = 6, VisionProjectorGpu = false };
        var rows = new List<object>();
        await using var factory = new ModelProviderFactory(settings, new EmptyCredentials());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            var watch = Stopwatch.StartNew();
            await factory.ModelManager.StartAsync(cancellation.Token);
            rows.Add(new { test = "cold-manager-start", milliseconds = watch.Elapsed.TotalMilliseconds, stages = factory.ModelManager.LastStartupTiming });
            watch.Restart();
            for (var i = 0; i < 1000; i++) await factory.ModelManager.StartAsync(cancellation.Token);
            rows.Add(new { test = "warm-manager-start-1000", milliseconds = watch.Elapsed.TotalMilliseconds });
            for (var i = 1; i <= 5; i++)
            {
                var firstText = -1d;
                watch.Restart();
                var reply = await factory.Create().CompleteAsync(new([new("user", $"Reply exactly: Local text check {i} succeeded.")], MaxTokens: 64), _ => { if (firstText < 0) firstText = watch.Elapsed.TotalMilliseconds; }, cancellation.Token);
                if (!reply.Text.Contains($"Local text check {i} succeeded", StringComparison.Ordinal)) throw new InvalidOperationException("Text benchmark returned an incorrect response.");
                var row = new { test = "text", sample = i, milliseconds = watch.Elapsed.TotalMilliseconds, firstTextMs = firstText, text = reply.Text };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
            }
            for (var i = 1; i <= 5; i++)
            {
                var tool = new ToolDefinition("files.list", "List files in the approved local workspace.", JsonSchema.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""), RiskLevel.ReadOnly);
                watch.Restart();
                var reply = await factory.Create().CompleteAsync(new([new("system", "Use the supplied tool when asked to list files. Do not invent results."), new("user", $"Check number {i}: list the files in workspace directory '.' using files.list.")], [tool], MaxTokens: 2048), null, cancellation.Token);
                var call = reply.ToolCalls.Single();
                if (call.Name != "files.list" || JsonSchema.Parse(call.Arguments).GetProperty("path").GetString() != ".") throw new InvalidOperationException("Tool-selection benchmark returned an incorrect call.");
                var row = new { test = "tool-selection", sample = i, milliseconds = watch.Elapsed.TotalMilliseconds, tool = call.Name };
                rows.Add(row); Console.WriteLine(JsonSerializer.Serialize(row));
            }
            await factory.ModelManager.StopAsync();
            watch.Restart();
            await factory.ModelManager.StartAsync(cancellation.Token);
            rows.Add(new { test = "same-manager-restart", milliseconds = watch.Elapsed.TotalMilliseconds, stages = factory.ModelManager.LastStartupTiming });
        }
        finally { await factory.ModelManager.StopAsync(); }

        var compatible = new AppSettings { Provider = "compatible", Endpoint = "http://127.0.0.1:49151/v1", Model = "test-only-no-network" };
        await using var clients = new ModelProviderFactory(compatible, new EmptyCredentials());
        var weak = new List<WeakReference<IModelProvider>>();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var creation = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++) weak.Add(new(clients.Create()));
        var creationMs = creation.Elapsed.TotalMilliseconds;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var distinct = new HashSet<IModelProvider>(ReferenceEqualityComparer.Instance);
        foreach (var item in weak) if (item.TryGetTarget(out var provider)) distinct.Add(provider);
        rows.Add(new { test = "compatible-create-1000-no-network", milliseconds = creationMs, allocatedBytes, retainedDistinctProviders = distinct.Count });
        var report = new { label, date = DateTimeOffset.UtcNow, runtime = ModelManager.RuntimeVersion, model = ModelManager.ModelRevision, gpuLayers = 24, context = 8192, threads = 6, workersStopped = !factory.ModelManager.GetStatus().Running, measurements = rows };
        var output = Path.Combine(Environment.CurrentDirectory, "scripts", "runtime-smoke", "output"); Directory.CreateDirectory(output);
        var path = Path.Combine(output, "latency-" + label + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        Console.WriteLine(path);
    }
}
