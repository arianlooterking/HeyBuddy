using System.Text;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;
using Xunit;

namespace Clicky.Runtime.Tests;

public sealed class RealtimeTests
{
    [Fact]
    public async Task StreamsTranscriptAndPcmWithStructuredToolsAndImages()
    {
        var call = new
        {
            type = "function_call",
            call_id = "c1",
            name = ToolWireNames.Encode("files.list"),
            arguments = "{\"path\":\".\"}"
        };
        var socket = new FakeTransport([
            "{\"type\":\"session.created\"}", "{\"type\":\"session.updated\"}",
            "{\"type\":\"response.output_audio_transcript.delta\",\"delta\":\"Hello\"}",
            "{\"type\":\"response.output_audio.delta\",\"delta\":\"AQI=\"}",
            "{\"type\":\"response.output_audio.delta\",\"delta\":\"AwQ=\"}",
            JsonSerializer.Serialize(new { type = "response.done", response = new { status = "completed", output = new[] { call } } })
        ]);
        var provider = new OpenAiRealtimeProvider(new()
        {
            CloudContentAllowed = true,
            SpeakReplies = true
        }, "mock-token", () => socket);
        var text = new StringBuilder();
        var reply = await provider.CompleteAsync(new([new("user", "See this", [new("AA==")])], [new("files.list", "Read workspace", JsonSchema.Parse("{\"type\":\"object\"}"), RiskLevel.ReadOnly)]), t => text.Append(t), default);
        Assert.Equal("Hello", reply.Text);
        Assert.Equal("Hello", text.ToString());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, Convert.FromBase64String(reply.AudioBase64!));
        Assert.Equal(24000, reply.AudioSampleRate);
        Assert.Equal("files.list", reply.ToolCalls.Single().Name);
        Assert.True(socket.Disposed);
        using var session = JsonDocument.Parse(socket.Sent[0]);
        Assert.Equal("audio", session.RootElement.GetProperty("session").GetProperty("output_modalities")[0].GetString());
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", session.RootElement.GetProperty("session").GetProperty("tools")[0].GetProperty("name").GetString()!);
        Assert.Contains(socket.Sent, s => s.Contains("input_image", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CloudConsentIsCheckedBeforeOpeningWebSocket()
    {
        var socket = new FakeTransport([]);
        var provider = new OpenAiRealtimeProvider(new(), "mock-token", () => socket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new([new("user", "Private", [new("AA==")])]), null, default));
        Assert.False(socket.Connected);
    }

    [Fact]
    public async Task CancellationSendsCancelAndDisposesTheSession()
    {
        var socket = new FakeTransport(["{\"type\":\"session.created\"}", "{\"type\":\"session.updated\"}"]);
        var provider = new OpenAiRealtimeProvider(new(), "mock-token", () => socket);
        using var cancel = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, cancel.Token));
        Assert.Contains(socket.Sent, s => s.Contains("response.cancel", StringComparison.Ordinal));
        Assert.True(socket.Disposed);
    }

    [Fact]
    public async Task FailedResponseNeverReturnsExecutableTools()
    {
        var socket = new FakeTransport(["{\"type\":\"session.created\"}", "{\"type\":\"session.updated\"}", "{\"type\":\"response.done\",\"response\":{\"status\":\"failed\",\"output\":[]}}"]);
        var provider = new OpenAiRealtimeProvider(new(), "mock-token", () => socket);
        await Assert.ThrowsAsync<IOException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, default));
        Assert.True(socket.Disposed);
    }

    [Fact]
    public void WireNamesPreserveDistinctToolIdentities()
    {
        Assert.NotEqual(ToolWireNames.Encode("files.read"), ToolWireNames.Encode("files_read"));
        Assert.NotEqual(ToolWireNames.Encode("google.drive.read"), ToolWireNames.Encode("google_drive.read"));
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", ToolWireNames.Encode(new string('x', 200) + ".read"));
    }

    private sealed class FakeTransport(IEnumerable<string> events) : IRealtimeTransport
    {
        private readonly Queue<string> events = new(events);
        public List<string> Sent { get; } = [];
        public bool Connected
        {
            get; private set;
        }
        public bool Disposed
        {
            get; private set;
        }
        public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Connected = true;
            return Task.CompletedTask;
        }
        public Task SendAsync(string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(message);
            return Task.CompletedTask;
        }
        public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            if (events.TryDequeue(out var value))
                return value;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return null;
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
