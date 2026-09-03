using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;

internal static class RecoverySmoke
{
    public static async Task RunAsync()
    {
        var output = Path.Combine(Environment.CurrentDirectory, "scripts", "runtime-smoke", "output");
        var isolated = Path.Combine(output, "recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(isolated);
        var store = new AppStore(isolated);
        var session = Guid.NewGuid().ToString("N");
        store.AddMessage(session, "chat", "user", "Preserve this committed history record across a local worker crash.");
        var previous = store.GetHistory(session: session).Single();

        var settings = new AppSettings { Provider = "local", GpuLayers = 24, ContextSize = 8192, CpuThreads = 6, VisionProjectorGpu = false, WorkDirectory = Path.Combine(isolated, "Workspace") };
        await using var factory = new ModelProviderFactory(settings, new EmptyCredentials());
        using var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        WorkerIdentity? original = null;
        WorkerIdentity? recovered = null;
        string? failure = null;
        string? replyText = null;
        var recoveryMs = 0L;
        var historyPreserved = false;
        try
        {
            var endpoint = await factory.ModelManager.StartAsync(lifetime.Token);
            original = await ProveOwnershipAsync(endpoint, factory.ModelManager, lifetime.Token);
            Console.WriteLine(JsonSerializer.Serialize(new { check = "original-worker-owned-loopback", original }));
            using var ownedProcess = Process.GetProcessById(original.Pid);
            var startedAt = ownedProcess.StartTime;
            var firstText = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = factory.Create();
            var inFlight = provider.CompleteAsync(new([new("user", "Count from 1 to 2000 in order, writing every number separately. Continue without commentary or abbreviations.")], MaxTokens: 2048), _ => firstText.TrySetResult(), lifetime.Token);
            await firstText.Task.WaitAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            var confirmed = await ProveOwnershipAsync(endpoint, factory.ModelManager, lifetime.Token);
            if (confirmed.Pid != original.Pid || ownedProcess.HasExited || ownedProcess.StartTime != startedAt)
                throw new InvalidOperationException("Worker identity changed before the crash test; no process was terminated.");
            // The exact PID has both the expected executable and this smoke process as its parent.
            // Terminate that one process only; never enumerate or kill another worker/service.
            ownedProcess.Kill(entireProcessTree: false);
            await ownedProcess.WaitForExitAsync(lifetime.Token);
            Console.WriteLine($"Terminated only owned worker PID {original.Pid} during a streaming response.");
            try
            {
                await inFlight;
                throw new InvalidOperationException("The interrupted inference was incorrectly reported as complete.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException)
            {
                failure = exception.GetType().Name + ": " + exception.Message;
                Console.WriteLine("Interrupted inference correctly failed: " + failure);
            }

            var watch = Stopwatch.StartNew();
            var reply = await provider.CompleteAsync(new([new("user", "Say exactly: Local recovery is working.")], MaxTokens: 32), null, lifetime.Token);
            recoveryMs = watch.ElapsedMilliseconds;
            replyText = reply.Text;
            if (!replyText.Contains("Local recovery is working", StringComparison.Ordinal))
                throw new InvalidOperationException("Recovered local response did not satisfy the verification request.");
            recovered = await ProveOwnershipAsync(await factory.ModelManager.StartAsync(lifetime.Token), factory.ModelManager, lifetime.Token);
            if (recovered.Pid == original.Pid)
                throw new InvalidOperationException("A new owned worker PID was expected after the forced crash.");
            var reopened = new AppStore(isolated);
            var after = reopened.GetHistory(session: session).Single();
            historyPreserved = after == previous;
            if (!historyPreserved)
                throw new InvalidOperationException("The previously committed history record changed or disappeared.");
            reopened.AddMessage(session, "chat", "assistant", reply.Text);
            Console.WriteLine(JsonSerializer.Serialize(new { check = "new-worker-recovers-and-history-survives", recovered, recoveryMs, replyText, historyPreserved }));
        }
        finally
        {
            await factory.ModelManager.StopAsync();
            var stopped = !factory.ModelManager.GetStatus().Running;
            if (recovered is not null)
            {
                try { using var process = Process.GetProcessById(recovered.Pid); stopped &= process.HasExited; }
                catch (ArgumentException) { }
            }
            var evidence = new { recordedAt = DateTimeOffset.UtcNow, passed = recovered is not null && historyPreserved && stopped, dataDirectory = isolated, original, interruptedRequestFailure = failure, recovered, recoveryMs, replyText, historyPreserved, workersStopped = stopped, modelRevision = ModelManager.ModelRevision, runtimeVersion = ModelManager.RuntimeVersion, settings = new { gpuLayers = 24, contextTokens = 8192, cpuThreads = 6, visionProjectorGpu = false } };
            var path = Path.Combine(output, "recovery-smoke.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(path);
            Console.WriteLine("Test-owned model worker stopped; isolated SQLite evidence retained.");
        }
    }

    private static async Task<WorkerIdentity> ProveOwnershipAsync(Uri endpoint, ModelManager manager, CancellationToken ct)
    {
        if (endpoint.Host != "127.0.0.1" || !manager.GetStatus().Running)
            throw new InvalidOperationException("Expected a running app-managed loopback worker.");
        var helper = Path.Combine(Environment.CurrentDirectory, "scripts", "runtime-smoke", "FindOwnedWorker.ps1");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", helper, "-Port", endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Worker ownership inspection did not start.");
        var text = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
            throw new IOException("Worker ownership inspection failed: " + error);
        var identity = JsonSerializer.Deserialize<WorkerIdentity>(text) ?? throw new IOException("Worker ownership inspection returned no record.");
        if (identity.ParentPid != Environment.ProcessId || !string.Equals(Path.GetFullPath(identity.ExecutablePath), Path.GetFullPath(manager.ExecutablePath), StringComparison.OrdinalIgnoreCase) || identity.Port != endpoint.Port || identity.LocalAddress != "127.0.0.1")
            throw new InvalidOperationException("Listener executable, parent, or loopback endpoint does not match this test. No process was terminated.");
        return identity;
    }

    private sealed record WorkerIdentity(int Pid, int ParentPid, string ExecutablePath, string LocalAddress, int Port);
}
