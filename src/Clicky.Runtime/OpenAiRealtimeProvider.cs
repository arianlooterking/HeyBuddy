using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Clicky.Core;

namespace Clicky.Runtime;

/// <summary>A request-scoped GA Realtime session. Microphone STT stays local; explicit text/images are sent only after provider selection and content consent.</summary>
public sealed class OpenAiRealtimeProvider : IModelProvider
{
    private readonly AppSettings settings;
    private readonly string key;
    private readonly Func<IRealtimeTransport> transportFactory;
    public string Name => "OpenAI Realtime";
    public bool IsCloud => true;
    public OpenAiRealtimeProvider(AppSettings settings, string key, Func<IRealtimeTransport>? transportFactory = null)
    {
        this.settings = settings;
        this.key = key;
        this.transportFactory = transportFactory ?? (() => new RealtimeWebSocketTransport());
    }

    public async Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
    {
        ProviderGuard.ValidateRequest(request, true, settings.CloudContentAllowed);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Add an OpenAI API key before choosing Realtime.");
        var model = string.IsNullOrWhiteSpace(settings.CloudModel) ? "gpt-realtime" : settings.CloudModel.Trim();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        await using var transport = transportFactory();
        try
        {
            await transport.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model)), key, deadline.Token).ConfigureAwait(false);
            await WaitForAsync(transport, "session.created", deadline.Token).ConfigureAwait(false);
            var session = new Dictionary<string, object?>
            {
                ["type"] = "realtime",
                ["model"] = model,
                ["output_modalities"] = new[] { settings.SpeakReplies ? "audio" : "text" },
                ["instructions"] = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content)),
                ["max_output_tokens"] = Math.Min(request.MaxTokens, 4096)
            };
            if (settings.SpeakReplies)
            {
                var voices = new[] { "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar" };
                session["audio"] = new
                {
                    output = new
                    {
                        format = new
                        {
                            type = "audio/pcm",
                            rate = 24000
                        },
                        voice = voices.Contains(settings.Voice) ? settings.Voice : "marin",
                        speed = Math.Clamp(settings.SpeechSpeed, 0.25, 1.5)
                    }
                };
            }
            if (request.Tools is { Count: > 0 })
            {
                session["tools"] = request.Tools.Select(t => new { type = "function", name = ToolWireNames.Encode(t.Name), description = t.Description, parameters = t.InputSchema }).ToArray();
                session["tool_choice"] = "auto";
            }
            await SendAsync(transport, new
            {
                type = "session.update",
                session
            }, deadline.Token).ConfigureAwait(false);
            await WaitForAsync(transport, "session.updated", deadline.Token).ConfigureAwait(false);
            foreach (var message in request.Messages.Where(m => m.Role != "system"))
            {
                if (message.Role == "tool")
                {
                    await SendAsync(transport, new
                    {
                        type = "conversation.item.create",
                        item = new
                        {
                            type = "function_call_output",
                            call_id = message.ToolCallId,
                            output = message.Content
                        }
                    }, deadline.Token).ConfigureAwait(false);
                    continue;
                }
                var content = new List<object>();
                if (!string.IsNullOrWhiteSpace(message.Content))
                    content.Add(new
                    {
                        type = message.Role == "assistant" ? "output_text" : "input_text",
                        text = message.Content
                    });
                foreach (var image in message.Images ?? [])
                    content.Add(new
                    {
                        type = "input_image",
                        image_url = $"data:{image.MimeType};base64,{image.Base64}"
                    });
                if (content.Count > 0)
                    await SendAsync(transport, new
                    {
                        type = "conversation.item.create",
                        item = new
                        {
                            type = "message",
                            role = message.Role == "assistant" ? "assistant" : "user",
                            content
                        }
                    }, deadline.Token).ConfigureAwait(false);
                foreach (var call in message.ToolCalls ?? [])
                    await SendAsync(transport, new
                    {
                        type = "conversation.item.create",
                        item = new
                        {
                            type = "function_call",
                            call_id = call.Id,
                            name = ToolWireNames.Encode(call.Name),
                            arguments = call.Arguments
                        }
                    }, deadline.Token).ConfigureAwait(false);
            }
            await SendAsync(transport, new
            {
                type = "response.create"
            }, deadline.Token).ConfigureAwait(false);
            var text = new StringBuilder();
            using var audio = new MemoryStream();
            while (await transport.ReceiveAsync(deadline.Token).ConfigureAwait(false) is { } json)
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!root.TryGetProperty("type", out var type))
                    continue;
                switch (type.GetString())
                {
                    case "error":
                        ThrowRealtimeError(root);
                        break;
                    case "response.output_text.delta":
                    case "response.output_audio_transcript.delta":
                        var delta = root.GetProperty("delta").GetString() ?? "";
                        text.Append(delta);
                        onText?.Invoke(delta);
                        break;
                    case "response.output_audio.delta":
                        var bytes = Convert.FromBase64String(root.GetProperty("delta").GetString() ?? "");
                        if (audio.Length + bytes.Length > 16 * 1024 * 1024)
                            throw new InvalidDataException("Realtime audio exceeded the bounded response limit.");
                        audio.Write(bytes);
                        break;
                    case "response.done":
                        var response = root.GetProperty("response");
                        var status = response.GetProperty("status").GetString();
                        if (status != "completed")
                            throw new IOException($"OpenAI Realtime response was {status ?? "incomplete"}. No actions were executed.");
                        var calls = new List<ToolCall>();
                        if (response.TryGetProperty("output", out var items))
                            foreach (var item in items.EnumerateArray())
                            {
                                if (item.GetProperty("type").GetString() == "function_call")
                                {
                                    var args = item.GetProperty("arguments").GetString() ?? "{}";
                                    using var validArgs = JsonDocument.Parse(args);
                                    if (validArgs.RootElement.ValueKind != JsonValueKind.Object)
                                        throw new InvalidDataException("Realtime tool arguments must be an object.");
                                    calls.Add(new(item.GetProperty("call_id").GetString()!, ToolWireNames.Decode(item.GetProperty("name").GetString()!, request), args));
                                }
                                else if (text.Length == 0 && item.TryGetProperty("content", out var parts))
                                    foreach (var part in parts.EnumerateArray())
                                        if (part.TryGetProperty("transcript", out var transcript))
                                            text.Append(transcript.GetString());
                                        else if (part.TryGetProperty("text", out var completeText))
                                            text.Append(completeText.GetString());
                            }
                        return new(text.ToString(), calls, model, audio.Length == 0 ? null : Convert.ToBase64String(audio.ToArray()), 24000);
                }
            }
            throw new IOException("OpenAI Realtime disconnected before the response completed.");
        }
        catch (OperationCanceledException)
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await SendAsync(transport, new
                {
                    type = "response.cancel"
                }, stopDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException("OpenAI Realtime did not complete within three minutes.");
        }
        catch (WebSocketException) { throw new IOException("Could not complete the OpenAI Realtime connection. Check internet access, API key, account quota, and the selected Realtime model."); }
    }
    private static Task SendAsync(IRealtimeTransport socket, object message, CancellationToken ct) => socket.SendAsync(JsonSerializer.Serialize(message), ct);
    private static async Task WaitForAsync(IRealtimeTransport transport, string expected, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        while (await transport.ReceiveAsync(timeout.Token).ConfigureAwait(false) is { } json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type))
                continue;
            if (type.GetString() == "error")
                ThrowRealtimeError(root);
            if (type.GetString() == expected)
                return;
        }
        throw new IOException($"OpenAI Realtime disconnected while waiting for {expected}.");
    }
    private static void ThrowRealtimeError(JsonElement root)
    {
        var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var value) ? value.GetString() : null;
        throw new IOException(code is "rate_limit_exceeded" or "insufficient_quota" ? "OpenAI Realtime quota or rate limit reached. No provider fallback occurred." : "OpenAI Realtime rejected a session or conversation event. Check the selected model and account permissions.");
    }
}

public interface IRealtimeTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken);
    Task SendAsync(string message, CancellationToken cancellationToken);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

internal sealed class RealtimeWebSocketTransport : IRealtimeTransport
{
    private readonly ClientWebSocket socket = new();
    public Task ConnectAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
    {
        socket.Options.SetRequestHeader("Authorization", "Bearer " + apiKey);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return socket.ConnectAsync(endpoint, cancellationToken);
    }
    public Task SendAsync(string message, CancellationToken cancellationToken) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, cancellationToken);
    public async Task<string?> ReceiveAsync(CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        var buffer = new byte[16384];
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
            if (received.MessageType == WebSocketMessageType.Close)
                return null;
            if (received.MessageType != WebSocketMessageType.Text)
                throw new InvalidDataException("Realtime sent an unexpected binary event.");
            if (message.Length + received.Count > 8 * 1024 * 1024)
                throw new InvalidDataException("Realtime event exceeded its size limit.");
            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
                return Encoding.UTF8.GetString(message.ToArray());
        }
    }
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Request completed", timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
        finally { socket.Abort(); socket.Dispose(); }
    }
}
