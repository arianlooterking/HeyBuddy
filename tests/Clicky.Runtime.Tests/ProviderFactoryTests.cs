using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Clicky.Core;
using Xunit;

namespace Clicky.Runtime.Tests;

public sealed class ProviderFactoryTests
{
    [Fact]
    public async Task ReusesTransportAndKeysModelEndpointAndCredential()
    {
        await using var server = new ModelServer();
        var settings = new AppSettings { Provider = "compatible", Endpoint = server.Endpoint, Model = "alpha" };
        var credentials = new Credentials { Token = "test-alpha" };
        await using var factory = new ModelProviderFactory(settings, credentials);
        var alpha = factory.Create();
        for (var i = 0; i < 1000; i++)
            Assert.Same(alpha, factory.Create());
        Assert.Equal(1, factory.CachedClientCount);
        await alpha.CompleteAsync(Request(), null, default);
        await factory.Create().CompleteAsync(Request(), null, default);
        Assert.Equal(1, server.Connections);
        Assert.All(server.Requests, request => Assert.Equal("Bearer test-alpha", request.Authorization));

        settings.Provider = "lmstudio";
        Assert.Same(alpha, factory.Create());
        settings.Model = "beta";
        var beta = factory.Create();
        Assert.NotSame(alpha, beta);
        credentials.Token = "test-beta";
        var newCredential = factory.Create();
        Assert.NotSame(beta, newCredential);
        await newCredential.CompleteAsync(Request(), null, default);
        Assert.Equal("beta", server.Requests.Last().Model);
        Assert.Equal("Bearer test-beta", server.Requests.Last().Authorization);
        settings.Endpoint = "http://127.0.0.1:49153/v1";
        Assert.NotSame(newCredential, factory.Create());
    }

    [Fact]
    public async Task EvictionIsBoundedAndDoesNotInterruptAnExistingTask()
    {
        await using var server = new ModelServer(block: true);
        var settings = new AppSettings { Provider = "compatible", Endpoint = server.Endpoint, Model = "original" };
        await using var factory = new ModelProviderFactory(settings, new Credentials());
        var original = factory.Create();
        var pending = original.CompleteAsync(Request(), null, default);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 30; i++)
        {
            settings.Model = "model-" + i;
            factory.Create();
        }
        Assert.Equal(4, factory.CachedClientCount);
        Assert.False(pending.IsCompleted);
        server.Unblock.TrySetResult();
        Assert.Equal("ok", (await pending).Text);
        // A long-running agent holding an evicted handle retains its original model configuration.
        Assert.Equal("ok", (await original.CompleteAsync(Request(), null, default)).Text);
        Assert.Equal("original", server.Requests.Last().Model);
        Assert.Equal(4, factory.CachedClientCount);
    }

    [Fact]
    public async Task DisposalCancelsActiveRequestsAndPreventsNewOnes()
    {
        await using var server = new ModelServer(block: true);
        var factory = new ModelProviderFactory(new()
        {
            Provider = "compatible",
            Endpoint = server.Endpoint,
            Model = "test"
        }, new Credentials());
        var provider = factory.Create();
        var request = provider.CompleteAsync(Request(), null, default);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await factory.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.Throws<ObjectDisposedException>(() => factory.Create());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(), null, default));
        Assert.Equal(0, factory.CachedClientCount);
    }

    [Fact]
    public async Task DisposalAlsoDrainsAnActiveEvictedProvider()
    {
        await using var server = new ModelServer(block: true);
        var settings = new AppSettings { Provider = "compatible", Endpoint = server.Endpoint, Model = "original" };
        var factory = new ModelProviderFactory(settings, new Credentials());
        var pending = factory.Create().CompleteAsync(Request(), null, default);
        await server.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 10; i++)
        {
            settings.Model = "eviction-" + i;
            factory.Create();
        }
        await factory.DisposeAsync();
        Assert.True(pending.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task RequestCancellationDoesNotPoisonReusableTransport()
    {
        await using var server = new ModelServer();
        await using var factory = new ModelProviderFactory(new()
        {
            Provider = "compatible",
            Endpoint = server.Endpoint,
            Model = "test"
        }, new Credentials());
        var provider = factory.Create();
        Assert.Equal("ok", (await provider.CompleteAsync(Request(), null, default)).Text);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request(), null, cancel.Token));
        Assert.Equal("ok", (await provider.CompleteAsync(Request(), null, default)).Text);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task CachedCloudProviderStillChecksCurrentContentPermission()
    {
        var settings = new AppSettings { Provider = "openai", CloudModel = "test-model", CloudContentAllowed = true };
        await using var factory = new ModelProviderFactory(settings, new Credentials { Token = "test-only-never-sent" });
        var provider = factory.Create();
        settings.CloudContentAllowed = false;
        Assert.Same(provider, factory.Create());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new([new("user", "<document>private</document>")]), null, default));
    }

    [Fact]
    public async Task StatusEventsDescribeExplicitStopWithoutStartingWorker()
    {
        await using var manager = new ModelManager(new());
        var events = new List<string>();
        manager.StatusChanged += events.Add;
        await manager.StopAsync();
        Assert.Equal("Local runtime is stopped.", events.Single());
        Assert.False(manager.GetStatus().Running);
    }

    private static ModelRequest Request() => new([new("user", "Say ok")], MaxTokens: 8);
    private sealed class Credentials : ICredentialStore
    {
        public string? Token
        {
            get; set;
        }
        public string? Get(string name) => Token;
        public void Set(string name, string value) => Token = value;
        public void Delete(string name) => Token = null;
    }

    private sealed class ModelServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new();
        private readonly ConcurrentBag<Task> clients = [];
        private readonly Task accept;
        private int connections;
        public int Connections => Volatile.Read(ref connections);
        public ConcurrentQueue<(string Model, string Authorization)> Requests { get; } = [];
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Unblock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Endpoint
        {
            get;
        }
        public ModelServer(bool block = false)
        {
            listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1";
            if (!block)
                Unblock.TrySetResult();
            accept = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(lifetime.Token);
                    Interlocked.Increment(ref connections);
                    clients.Add(HandleAsync(client));
                }
            }
            catch (OperationCanceledException) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
                    while (!lifetime.IsCancellationRequested)
                    {
                        if (await reader.ReadLineAsync(lifetime.Token) is null)
                            return;
                        int length = 0;
                        var authorization = "";
                        while (await reader.ReadLineAsync(lifetime.Token) is { Length: > 0 } line)
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                length = int.Parse(line[15..]);
                            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                                authorization = line[14..].Trim();
                        }
                        var body = new char[length];
                        await reader.ReadBlockAsync(body, lifetime.Token);
                        using var json = JsonDocument.Parse(new string(body));
                        Requests.Enqueue((json.RootElement.GetProperty("model").GetString()!, authorization));
                        Received.TrySetResult();
                        await Unblock.Task.WaitAsync(lifetime.Token);
                        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: [DONE]\n\n";
                        var response = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {Encoding.UTF8.GetByteCount(sse)}\r\nConnection: keep-alive\r\n\r\n{sse}");
                        await stream.WriteAsync(response, lifetime.Token);
                    }
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            await lifetime.CancelAsync();
            listener.Stop();
            await accept;
            await Task.WhenAll(clients);
            lifetime.Dispose();
        }
    }
}
