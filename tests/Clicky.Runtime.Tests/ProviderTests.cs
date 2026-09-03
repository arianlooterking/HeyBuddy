using System.Net;
using System.Text;
using System.Text.Json;
using Clicky.Core;
using Clicky.Runtime;
using Xunit;

namespace Clicky.Runtime.Tests;

public sealed class ProviderTests
{
    [Theory]
    [InlineData("http://example.com/v1")]
    [InlineData("https://user:secret@example.com/v1")]
    [InlineData("file:///tmp/model")]
    [InlineData("https://example.com/v1?key=secret")]
    [InlineData("http://10.0.0.1/v1")]
    public void RejectsUnsafeModelEndpoints(string endpoint) => Assert.Throws<ArgumentException>(() => ProviderGuard.ValidateEndpoint(endpoint));

    [Theory]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://localhost:1234/v1/")]
    [InlineData("https://example.com/v1")]
    public void AcceptsLoopbackOrHttps(string endpoint) => Assert.EndsWith("/", ProviderGuard.ValidateEndpoint(endpoint).AbsoluteUri);

    [Fact]
    public async Task StreamsTextAndStructuredToolsWithoutLosingHistory()
    {
        var handler = new FakeHandler("""
data: {"choices":[{"delta":{"content":"Hello "}}]}

data: {"choices":[{"delta":{"content":"world"}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call1","function":{"name":"files.read","arguments":"{\"path\":"}}]}}]}

data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"note.txt\"}"}}]}}]}

data: [DONE]

""");
        using var provider = new OpenAiCompatibleProvider(new Uri("http://127.0.0.1:7777/v1"), "local", null, new(), handler: handler);
        var streamed = new StringBuilder();
        var response = await provider.CompleteAsync(new([
            new("user", "Read the note"),
            new("assistant", "", ToolCalls: [new("older", "files.list", "{}")]),
            new("tool", "note.txt", ToolCallId: "older")]), text => streamed.Append(text), default);
        Assert.Equal("Hello world", response.Text);
        Assert.Equal(response.Text, streamed.ToString());
        Assert.Equal("files.read", response.ToolCalls.Single().Name);
        Assert.Equal("note.txt", JsonSchema.Parse(response.ToolCalls[0].Arguments).GetProperty("path").GetString());
        using var payload = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("older", payload.RootElement.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("older", payload.RootElement.GetProperty("messages")[1].GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task TruncatedStreamCannotBeReportedAsCompleted()
    {
        using var provider = new OpenAiCompatibleProvider(new Uri("http://127.0.0.1:7777/v1"), "local", null, new(), handler: new FakeHandler("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n"));
        await Assert.ThrowsAsync<IOException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, default));
    }

    [Fact]
    public async Task CloudContentRequiresOptInBeforeAnyHttpRequest()
    {
        var handler = new FakeHandler("");
        using var provider = new OpenAiCompatibleProvider(new Uri("https://api.example.com/v1"), "cloud", null, new(), handler: handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new([new("user", "Read this", [new("AA==")])]), null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new([new("user", "<document>private</document>")]), null, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteAsync(new([new("tool", "private document", ToolCallId: "x")]), null, default));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task QuotaErrorsDoNotFallbackOrLeakProviderBody()
    {
        var handler = new FakeHandler("a server echoed confidential data", HttpStatusCode.TooManyRequests);
        using var provider = new OpenAiCompatibleProvider(new Uri("http://127.0.0.1:7777/v1"), "local", null, new(), handler: handler);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, default));
        Assert.Contains("quota", error.Message);
        Assert.DoesNotContain("confidential", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task LocalStructuredErrorExplainsTemplateFailureWithoutEchoingBodySecrets()
    {
        var body = JsonSerializer.Serialize(new
        {
            error = new
            {
                code = 500,
                message = "System message must be at the beginning. echoed-token-private-123 <document>private contents</document>",
                type = "server_error"
            }
        });
        var handler = new FakeHandler(body, HttpStatusCode.InternalServerError, "application/json");
        using var provider = new OpenAiCompatibleProvider(new Uri("http://127.0.0.1:7777/v1"), "local", "echoed-token-private-123", new(), handler: handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, default));

        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Contains("Local server diagnostic: System message must be at the beginning.", error.Message);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain("<document>", error.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("cloud-json")]
    [InlineData("html")]
    [InlineData("oversized-json")]
    [InlineData("unknown-json")]
    public async Task ErrorDiagnosticsNeverEchoCloudHtmlOversizedOrUnknownContent(string scenario)
    {
        var message = scenario == "unknown-json" ? "confidential-secret-value" : "System message must be at the beginning. confidential-secret-value";
        if (scenario == "oversized-json")
            message += new string('x', 9000);
        var body = scenario == "html" ? "<html>" + message + "</html>" : JsonSerializer.Serialize(new
        {
            error = new
            {
                message
            }
        });
        var handler = new FakeHandler(body, HttpStatusCode.InternalServerError, scenario == "html" ? "text/html" : "application/json");
        using var provider = new OpenAiCompatibleProvider(new Uri(scenario == "cloud-json" ? "https://model.example.com/v1" : "http://127.0.0.1:7777/v1"), "model", null, new(), handler: handler);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, default));

        Assert.DoesNotContain("confidential", error.Message);
        Assert.DoesNotContain("Local server diagnostic", error.Message);
        Assert.Contains("HTTP 500", error.Message);
    }

    [Fact]
    public async Task CancellationPreventsARequest()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var handler = new FakeHandler("data: [DONE]\n\n");
        using var provider = new OpenAiCompatibleProvider(new Uri("http://127.0.0.1:7777/v1"), "local", null, new(), handler: handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(new([new("user", "Hello")]), null, cancel.Token));
    }

    [Fact]
    public async Task AnthropicStreamsToolUseAndToolResultHistory()
    {
        var handler = new FakeHandler("""
data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call2","name":"files.list","input":{}}}

data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\".\"}"}}

data: {"type":"message_stop"}

""");
        using var provider = new AnthropicProvider(new()
        {
            AnthropicModel = "user-selected-model",
            CloudContentAllowed = true
        }, "test-token", handler);
        var reply = await provider.CompleteAsync(new([new("system", "System"), new("user", "List"), new("assistant", "", ToolCalls: [new("old", "files.list", "{}")]), new("tool", "note.txt", ToolCallId: "old")]), null, default);
        Assert.Equal("call2", reply.ToolCalls.Single().Id);
        using var payload = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("tool_result", payload.RootElement.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("type").GetString());
    }

    private sealed class FakeHandler(string sse, HttpStatusCode status = HttpStatusCode.OK, string mediaType = "text/event-stream") : HttpMessageHandler
    {
        public string? LastBody
        {
            get; private set;
        }
        public int Calls
        {
            get; private set;
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(status)
            {
                Content = new StringContent(sse, Encoding.UTF8, mediaType)
            };
        }
    }
}
