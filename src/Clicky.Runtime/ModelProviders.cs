using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clicky.Core;

namespace Clicky.Runtime;

public sealed class ModelProviderFactory : IAsyncDisposable
{
    private readonly AppSettings settings;
    private readonly ICredentialStore credentials;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<ProviderKey, CachedProvider> clients = [];
    private long accessSequence;
    private bool disposed;
    private int activeRequests;
    private TaskCompletionSource? requestsDrained;
    public int CachedClientCount
    {
        get
        {
            lock (gate)
                return clients.Count;
        }
    }
    public ModelManager ModelManager
    {
        get;
    }
    public ModelProviderFactory(AppSettings settings, ICredentialStore credentials)
    {
        this.settings = settings;
        this.credentials = credentials;
        ModelManager = new(settings);
    }
    public IModelProvider Create()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var provider = settings.Provider.Trim().ToLowerInvariant();
            if (provider == "local")
                return Cached(new("local", "", "qwen3.5-4b", ""), "Local · Qwen3.5 4B", false, () => new ManagedLocalProvider(ModelManager, settings));
            if (provider == "openai-realtime")
                return new OpenAiRealtimeProvider(settings, RequiredCredential("provider.openai"));
            if (provider is "anthropic" or "claude")
            {
                var anthropicToken = RequiredCredential("provider.anthropic");
                var selectedModel = settings.AnthropicModel.Trim();
                if (selectedModel.Length == 0)
                    throw new InvalidOperationException("Choose an Anthropic model identifier in AI settings.");
                return Cached(new("anthropic", "https://api.anthropic.com/v1/messages", selectedModel, Fingerprint(anthropicToken)), "Anthropic", true, () => new AnthropicProvider(settings, anthropicToken, model: selectedModel));
            }
            var uri = provider == "openai" ? new Uri("https://api.openai.com/v1/") : ProviderGuard.ValidateEndpoint(settings.Endpoint);
            if (provider is not ("openai" or "compatible" or "openai-compatible" or "lmstudio" or "lm-studio"))
                throw new InvalidOperationException($"Unknown model provider: {settings.Provider}.");
            var model = provider == "openai" ? settings.CloudModel : settings.Model;
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Choose the model identifier in AI settings before sending a request.");
            var token = provider == "openai" ? RequiredCredential("provider.openai") : credentials.Get("provider.compatible");
            var name = provider == "openai" ? "OpenAI" : "OpenAI compatible";
            return Cached(new(provider == "openai" ? "openai" : "compatible", uri.AbsoluteUri, model.Trim(), Fingerprint(token)), name,
                !ProviderGuard.IsLiteralLoopback(uri), () => new OpenAiCompatibleProvider(uri, model.Trim(), token, settings, name));
        }
    }
    private IModelProvider Cached(ProviderKey key, string name, bool cloud, Func<IModelProvider> create)
    {
        if (!clients.TryGetValue(key, out var cached))
        {
            if (clients.Count >= 4)
            {
                var oldest = clients.MinBy(pair => pair.Value.LastAccess);
                clients.Remove(oldest.Key);
                oldest.Value.Retire();
            }
            clients.Add(key, cached = new(name, cloud, create, lifetime.Token, RequestStarted, RequestEnded));
        }
        cached.LastAccess = ++accessSequence;
        return cached;
    }
    private static string Fingerprint(string? token) => string.IsNullOrEmpty(token) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private sealed record ProviderKey(string Kind, string Endpoint, string Model, string CredentialFingerprint);
    private void RequestStarted()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            activeRequests++;
        }
    }
    private void RequestEnded()
    {
        lock (gate)
        {
            if (--activeRequests == 0)
            {
                requestsDrained?.TrySetResult();
                requestsDrained = null;
            }
        }
    }
    private string RequiredCredential(string key) => credentials.Get(key) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Add the {key.Replace("provider.", "")} API key in Settings before using this provider.");
    public async ValueTask DisposeAsync()
    {
        CachedProvider[] retiring;
        Task drained;
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            retiring = clients.Values.ToArray();
            clients.Clear();
            drained = activeRequests == 0 ? Task.CompletedTask : (requestsDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var client in retiring)
            client.Retire();
        await drained.ConfigureAwait(false);
        await ModelManager.DisposeAsync().ConfigureAwait(false);
        lifetime.Dispose();
    }

    /// <summary>Eviction releases idle transports; an existing task can still finish with its original configuration.</summary>
    private sealed class CachedProvider(string name, bool cloud, Func<IModelProvider> create, CancellationToken lifetime, Action requestStarted, Action requestEnded) : IModelProvider
    {
        private readonly object gate = new();
        private IModelProvider? current;
        private int active;
        private bool retired;
        public long LastAccess
        {
            get; set;
        }
        public string Name => name;
        public bool IsCloud => cloud;
        public async Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
            linked.Token.ThrowIfCancellationRequested();
            requestStarted();
            try
            {
                return await CompleteCoreAsync(request, onText, linked.Token).ConfigureAwait(false);
            }
            finally { requestEnded(); }
        }
        private async Task<ModelReply> CompleteCoreAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
        {
            IModelProvider provider;
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                provider = current ??= create();
                active++;
            }
            try
            {
                return await provider.CompleteAsync(request, onText, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (gate)
                {
                    if (--active == 0)
                    {
                        if (retired)
                            ReleaseClient();
                    }
                }
            }
        }
        public void Retire()
        {
            lock (gate)
            {
                retired = true;
                if (active == 0)
                    ReleaseClient();
            }
        }
        private void ReleaseClient()
        {
            (current as IDisposable)?.Dispose();
            current = null;
        }
    }
}

public static class ProviderGuard
{
    public static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The model endpoint must be an absolute HTTP(S) base URL without embedded credentials, a query, or a fragment.");
        if (uri.Scheme != "https" && !IsLiteralLoopback(uri))
            throw new ArgumentException("Remote model endpoints require HTTPS. Unencrypted HTTP is supported only on localhost or a loopback IP.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    public static bool IsLiteralLoopback(Uri uri) => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    public static void ValidateRequest(ModelRequest request, bool isCloud, bool cloudContentAllowed)
    {
        if (request.Messages.Count == 0)
            throw new ArgumentException("A conversation needs at least one message.");
        if (request.MaxTokens is < 1 or > 32768)
            throw new ArgumentException("Reply token limit must be between 1 and 32768.");
        if (isCloud && !cloudContentAllowed && (request.Messages.Any(m => m.Images?.Count > 0 || m.Role == "tool" || m.Content.Contains("<document", StringComparison.OrdinalIgnoreCase)) || request.Tools?.Count > 0))
            throw new InvalidOperationException("Cloud access to screen, documents, or agent tools is off. Review the content and explicitly enable cloud content in AI settings first.");
        if (request.Messages.Sum(m => m.Images?.Count ?? 0) > 8)
            throw new ArgumentException("Send at most eight images in a request.");
        foreach (var img in request.Messages.SelectMany(m => m.Images ?? []))
            if (img.MimeType is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif") || img.Base64.Length > 20_000_000)
                throw new ArgumentException("Images must be PNG, JPEG, WebP, or GIF, and under 15 MB each.");
    }
    internal static Exception ProviderError(HttpStatusCode status, string provider) => new HttpRequestException(status switch
    {
        HttpStatusCode.Unauthorized => $"{provider} rejected the credential. Update its API key in settings.",
        HttpStatusCode.Forbidden => $"{provider} denied access to the selected model or account.",
        HttpStatusCode.TooManyRequests => $"{provider} rate limit or quota reached. Wait or check the account; HeyBuddy will not switch providers.",
        HttpStatusCode.NotFound => $"{provider} could not find the endpoint or model. Check the model ID and base URL.",
        HttpStatusCode.BadRequest => $"{provider} rejected this request. The selected model may not support images or tool calls, or its context limit may be exceeded.",
        _ => $"{provider} returned HTTP {(int)status}. The request was not reported as successful."
    }, null, status);

    internal static async Task<Exception> ProviderErrorAsync(HttpResponseMessage response, string provider, bool local, CancellationToken ct)
    {
        var generic = ProviderError(response.StatusCode, provider);
        if (!local || response.Content.Headers.ContentType?.MediaType is not { } mediaType
            || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
            return generic;
        const int maximumBytes = 8192;
        var bytes = new byte[maximumBytes + 1];
        var count = 0;
        try
        {
            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                count += read;
            }
            if (count > maximumBytes)
                return generic;
            using var json = JsonDocument.Parse(bytes.AsMemory(0, count));
            var error = json.RootElement;
            if (error.ValueKind != JsonValueKind.Object)
                return generic;
            if (error.TryGetProperty("error", out var nested))
                error = nested;
            var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String
                ? detail.GetString() ?? "" : "";
            var diagnostic = LocalDiagnostic(message);
            return diagnostic is null ? generic : new HttpRequestException(generic.Message + " Local server diagnostic: " + diagnostic, null, response.StatusCode);
        }
        catch (Exception error) when (error is JsonException or IOException) { return generic; }
    }

    private static string? LocalDiagnostic(string message)
    {
        // Only return our own reviewed diagnostic text, never arbitrary echoed prompts, tokens,
        // filenames, URLs, HTML, or other provider body content. The JSON input is bounded above.
        foreach (var known in new[]
        {
            "System message must be at the beginning.", "No user query found in messages.",
            "No messages provided.", "Unexpected message role.", "Unexpected content type.",
            "Unexpected item type in content.", "System message cannot contain images.", "System message cannot contain videos."
        })
            if (message.Contains(known, StringComparison.OrdinalIgnoreCase))
                return known;
        if (message.Contains("context", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("exceed", StringComparison.OrdinalIgnoreCase) || message.Contains("too large", StringComparison.OrdinalIgnoreCase)
                || message.Contains("too long", StringComparison.OrdinalIgnoreCase)))
            return "The request exceeds the model's context capacity. Reduce context or tool output, or increase the configured context size.";
        if (message.Contains("out of memory", StringComparison.OrdinalIgnoreCase) || message.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
            return "The local runtime could not allocate enough memory. Reduce GPU layers or context size and retry.";
        if (message.Contains("jinja", StringComparison.OrdinalIgnoreCase) || message.Contains("chat template", StringComparison.OrdinalIgnoreCase))
            return "The model's chat template rejected the message format.";
        if (message.Contains("json.exception.type_error", StringComparison.OrdinalIgnoreCase))
            return "The local server rejected a JSON field type in this request.";
        return null;
    }
}

internal sealed class ManagedLocalProvider(ModelManager manager, AppSettings settings) : IModelProvider, IDisposable
{
    private readonly SemaphoreSlim requests = new(1, 1);
    private OpenAiCompatibleProvider? client;
    private LocalModelConnection? connection;
    public string Name => "Local · Qwen3.5 4B";
    public bool IsCloud => false;
    public async Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
    {
        await requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await manager.GetConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (client is null || connection != current)
            {
                client?.Dispose();
                client = new OpenAiCompatibleProvider(current.Endpoint, "qwen3.5-4b", current.ApiKey, settings, Name);
                connection = current;
            }
            return await client.CompleteAsync(request, onText, cancellationToken).ConfigureAwait(false);
        }
        finally { requests.Release(); }
    }
    public void Dispose()
    {
        client?.Dispose();
        requests.Dispose();
    }
}

public sealed class OpenAiCompatibleProvider : IModelProvider, IDisposable
{
    private readonly HttpClient client;
    private readonly Uri endpoint;
    private readonly string model;
    private readonly AppSettings settings;
    public string Name
    {
        get;
    }
    public bool IsCloud => !ProviderGuard.IsLiteralLoopback(endpoint);

    public OpenAiCompatibleProvider(Uri endpoint, string model, string? token, AppSettings settings, string name = "OpenAI compatible", HttpMessageHandler? handler = null)
    {
        this.endpoint = ProviderGuard.ValidateEndpoint(endpoint.AbsoluteUri);
        this.model = model;
        this.settings = settings;
        Name = name;
        client = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = IsCloud }) : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        if (!string.IsNullOrEmpty(token))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
    public async Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
    {
        ProviderGuard.ValidateRequest(request, IsCloud, settings.CloudContentAllowed);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(IsCloud ? 3 : 8));
        var payload = BuildPayload(request, model, !IsCloud);
        if (endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("max_tokens");
            payload["max_completion_tokens"] = request.MaxTokens;
        }
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "chat/completions")) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw await ProviderGuard.ProviderErrorAsync(response, Name, !IsCloud, timeout.Token).ConfigureAwait(false);
            var text = new StringBuilder();
            var calls = new SortedDictionary<int, StreamTool>();
            var sawDone = false;
            await foreach (var data in SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), timeout.Token))
            {
                if (data == "[DONE]")
                {
                    sawDone = true;
                    break;
                }
                using var json = JsonDocument.Parse(data);
                if (json.RootElement.TryGetProperty("error", out _))
                    throw new IOException($"{Name} reported an error during streaming.");
                if (!json.RootElement.TryGetProperty("choices", out var choices))
                    continue;
                foreach (var choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("delta", out var delta))
                        continue;
                    if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        var fragment = content.GetString()!;
                        text.Append(fragment);
                        onText?.Invoke(fragment);
                    }
                    if (delta.TryGetProperty("tool_calls", out var toolCalls))
                        foreach (var call in toolCalls.EnumerateArray())
                        {
                            var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                            if (!calls.TryGetValue(index, out var current))
                                calls[index] = current = new();
                            if (call.TryGetProperty("id", out var id))
                                current.Id.Append(id.GetString());
                            if (!call.TryGetProperty("function", out var function))
                                continue;
                            if (function.TryGetProperty("name", out var n))
                                current.Name.Append(n.GetString());
                            if (function.TryGetProperty("arguments", out var a))
                                current.Arguments.Append(a.GetString());
                        }
                }
            }
            if (!sawDone)
                throw new IOException($"{Name} connection ended before the response finished. Partial text was not saved as a completed reply.");
            return new(text.ToString(), calls.Values.Select(c => c.ToToolCall()).Select(c => IsCloud ? c with { Name = ToolWireNames.Decode(c.Name, request) } : c).ToList(), model);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{Name} took too long to respond. Retry or reduce the request size.");
        }
    }
    internal static Dictionary<string, object?> BuildPayload(ModelRequest request, string model, bool local)
    {
        var messages = request.Messages.Select(m =>
        {
            var value = new Dictionary<string, object?> { ["role"] = m.Role, ["content"] = m.Content };
            if (m.Images is { Count: > 0 })
                value["content"] = new object[] { new { type = "text", text = m.Content } }.Concat(m.Images.Select(i => (object)new { type = "image_url", image_url = new { url = $"data:{i.MimeType};base64,{i.Base64}" } })).ToArray();
            if (m.ToolCalls is { Count: > 0 })
                value["tool_calls"] = m.ToolCalls.Select(t => new { id = t.Id, type = "function", function = new { name = local ? t.Name : ToolWireNames.Encode(t.Name), arguments = t.Arguments } }).ToArray();
            if (m.ToolCallId != null)
                value["tool_call_id"] = m.ToolCallId;
            return value;
        }).ToArray();
        var payload = new Dictionary<string, object?> { ["model"] = model, ["messages"] = messages, ["stream"] = true, ["max_tokens"] = request.MaxTokens };
        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(t => new { type = "function", function = new { name = local ? t.Name : ToolWireNames.Encode(t.Name), description = t.Description, parameters = t.InputSchema } }).ToArray();
            payload["tool_choice"] = "auto";
        }
        if (local)
            payload["chat_template_kwargs"] = new
            {
                enable_thinking = false
            };
        return payload;
    }
    public void Dispose() => client.Dispose();
}

public sealed class AnthropicProvider : IModelProvider, IDisposable
{
    private readonly AppSettings settings;
    private readonly string model;
    private readonly HttpClient client;
    public string Name => "Anthropic";
    public bool IsCloud => true;
    public AnthropicProvider(AppSettings settings, string token, HttpMessageHandler? handler = null, string? model = null)
    {
        this.settings = settings;
        this.model = model ?? settings.AnthropicModel;
        client = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Add("x-api-key", token);
        client.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }
    public async Task<ModelReply> CompleteAsync(ModelRequest request, Action<string>? onText, CancellationToken cancellationToken)
    {
        ProviderGuard.ValidateRequest(request, true, settings.CloudContentAllowed);
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Choose an Anthropic model identifier in AI settings.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(JsonSerializer.Serialize(BuildPayload(request)), Encoding.UTF8, "application/json") };
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw ProviderGuard.ProviderError(response.StatusCode, Name);
            var text = new StringBuilder();
            var calls = new SortedDictionary<int, StreamTool>();
            var complete = false;
            await foreach (var data in SseReader.ReadAsync(await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false), timeout.Token))
            {
                using var json = JsonDocument.Parse(data);
                var root = json.RootElement;
                if (!root.TryGetProperty("type", out var type))
                    continue;
                switch (type.GetString())
                {
                    case "error":
                        throw new IOException("Anthropic returned a streaming error. Check its service status and retry.");
                    case "message_stop":
                        complete = true;
                        break;
                    case "content_block_start":
                        var block = root.GetProperty("content_block");
                        if (block.GetProperty("type").GetString() == "tool_use")
                        {
                            var c = new StreamTool();
                            c.Id.Append(block.GetProperty("id").GetString());
                            c.Name.Append(block.GetProperty("name").GetString());
                            calls[root.GetProperty("index").GetInt32()] = c;
                        }
                        break;
                    case "content_block_delta":
                        var delta = root.GetProperty("delta");
                        if (delta.TryGetProperty("text", out var t))
                        {
                            text.Append(t.GetString());
                            onText?.Invoke(t.GetString()!);
                        }
                        if (delta.TryGetProperty("partial_json", out var partial) && calls.TryGetValue(root.GetProperty("index").GetInt32(), out var tool))
                            tool.Arguments.Append(partial.GetString());
                        break;
                }
            }
            if (!complete)
                throw new IOException("Anthropic connection ended before the reply completed.");
            return new(text.ToString(), calls.Values.Select(c => c.ToToolCall()).Select(c => c with { Name = ToolWireNames.Decode(c.Name, request) }).ToList(), settings.AnthropicModel);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new TimeoutException("Anthropic took too long to respond."); }
    }
    private Dictionary<string, object?> BuildPayload(ModelRequest request)
    {
        var messages = new List<Dictionary<string, object?>>();
        foreach (var m in request.Messages.Where(m => m.Role != "system"))
        {
            var content = new List<object>();
            if (m.Role == "tool")
                content.Add(new
                {
                    type = "tool_result",
                    tool_use_id = m.ToolCallId,
                    content = m.Content
                });
            else
            {
                if (!string.IsNullOrEmpty(m.Content))
                    content.Add(new
                    {
                        type = "text",
                        text = m.Content
                    });
                foreach (var i in m.Images ?? [])
                    content.Add(new
                    {
                        type = "image",
                        source = new
                        {
                            type = "base64",
                            media_type = i.MimeType,
                            data = i.Base64
                        }
                    });
                foreach (var t in m.ToolCalls ?? [])
                    content.Add(new
                    {
                        type = "tool_use",
                        id = t.Id,
                        name = ToolWireNames.Encode(t.Name),
                        input = JsonSchema.Parse(t.Arguments)
                    });
            }
            var role = m.Role == "assistant" ? "assistant" : "user";
            if (messages.LastOrDefault() is { } previous && (string?)previous["role"] == role)
                ((List<object>)previous["content"]!).AddRange(content);
            else
                messages.Add(new()
                {
                    ["role"] = role,
                    ["content"] = content
                });
        }
        var payload = new Dictionary<string, object?> { ["model"] = model, ["max_tokens"] = request.MaxTokens, ["stream"] = true, ["messages"] = messages, ["system"] = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content)) };
        if (request.Tools is { Count: > 0 })
            payload["tools"] = request.Tools.Select(t => new { name = ToolWireNames.Encode(t.Name), description = t.Description, input_schema = t.InputSchema }).ToArray();
        return payload;
    }
    public void Dispose() => client.Dispose();
}

internal sealed class StreamTool
{
    public StringBuilder Id { get; } = new();
    public StringBuilder Name { get; } = new();
    public StringBuilder Arguments { get; } = new();
    public ToolCall ToToolCall()
    {
        if (Name.Length == 0)
            throw new InvalidDataException("The model returned an unnamed tool call. No action was taken.");
        var arguments = Arguments.Length == 0 ? "{}" : Arguments.ToString();
        using var json = JsonDocument.Parse(arguments);
        if (json.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Tool arguments must be a JSON object.");
        return new(Id.Length == 0 ? Guid.NewGuid().ToString("N") : Id.ToString(), Name.ToString(), arguments);
    }
}

internal static class SseReader
{
    internal static async IAsyncEnumerable<string> ReadAsync(Stream stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return data.ToString().TrimEnd('\n');
                    data.Clear();
                }
                continue;
            }
            if (line.StartsWith("data:", StringComparison.Ordinal))
                data.Append(line.AsSpan(5).TrimStart()).Append('\n');
            if (data.Length > 8_000_000)
                throw new InvalidDataException("Provider streamed an oversized event.");
        }
        if (data.Length > 0)
            yield return data.ToString().TrimEnd('\n');
    }
}
