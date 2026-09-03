using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clicky.Core;
using ModelContextProtocol.Authentication;

namespace Clicky.Connectors;

/// <summary>Loopback only, response-bound OAuth callback. Never accepts a pasted code without its state.</summary>
public sealed class LoopbackOAuthReceiver
{
    private readonly Action<Uri> _openBrowser;
    public LoopbackOAuthReceiver(Action<Uri>? openBrowser = null) => _openBrowser = openBrowser ??
        (uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }));

    public async Task<AuthorizationResult?> ReceiveAsync(AuthorizationCallbackContext context, CancellationToken cancellationToken)
    {
        var redirect = context.RedirectUri;
        if (redirect.Scheme != "http" || redirect.Host != "127.0.0.1" || redirect.Port < 1024)
            throw new InvalidOperationException("OAuth callbacks must use an unprivileged IPv4 loopback port.");
        if (context.AuthorizationUri.Scheme != "https")
            throw new InvalidOperationException("Authorization must use HTTPS.");
        var expectedState = ParseQuery(context.AuthorizationUri.Query).GetValueOrDefault("state");
        if (string.IsNullOrEmpty(expectedState))
            throw new InvalidOperationException("Authorization response binding is missing.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var listener = new TcpListener(IPAddress.Loopback, redirect.Port);
        listener.Start();
        try
        {
            _openBrowser(context.AuthorizationUri);
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token).ConfigureAwait(false);
                using var stream = client.GetStream();
                using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var buffer = new byte[8192];
                var length = 0;
                while (length < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(length, 1), requestDeadline.Token).ConfigureAwait(false);
                    if (n == 0)
                        break;
                    length += n;
                    if (length >= 4 && buffer.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                        break;
                }
                var header = Encoding.ASCII.GetString(buffer, 0, length);
                var first = header.Split("\r\n")[0].Split(' ');
                var validTarget = first.Length == 3 && first[0] == "GET" && first[1].StartsWith('/') && !first[1].StartsWith("//", StringComparison.Ordinal);
                var callback = validTarget ? new Uri(redirect, first[1]) : null;
                var query = callback is null ? new Dictionary<string, string>() : ParseQuery(callback.Query);
                var state = query.GetValueOrDefault("state");
                var valid = callback?.AbsolutePath == redirect.AbsolutePath && StateMatches(expectedState, state)
                    && header.Split("\r\n").Any(line => line.Equals($"Host: 127.0.0.1:{redirect.Port}", StringComparison.OrdinalIgnoreCase));
                var body = valid ? "Authorization received. You can close this tab and return to HeyBuddy." : "Invalid authorization callback.";
                var response = Encoding.UTF8.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}");
                await stream.WriteAsync(response, deadline.Token).ConfigureAwait(false);
                if (!valid)
                    continue;
                if (query.ContainsKey("error"))
                    throw new UnauthorizedAccessException("Authorization was declined by the provider. Check app registration and consent, then try again.");
                return new AuthorizationResult { Code = query.GetValueOrDefault("code"), State = state, Iss = query.GetValueOrDefault("iss") };
            }
        }
        finally { listener.Stop(); }
    }

    public static bool StateMatches(string expected, string? actual) => actual is not null &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    internal static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (result.ContainsKey(key))
                throw new InvalidDataException("Duplicate OAuth query parameter.");
            result[key] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return result;
    }
}

internal sealed class CredentialTokenCache(ICredentialStore credentials, string key) : ITokenCache
{
    private readonly object _sync = new();
    public ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
            credentials.Set(key, JsonSerializer.Serialize(tokens));
        return ValueTask.CompletedTask;
    }
    public ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var value = credentials.Get(key);
            return ValueTask.FromResult(value is null ? null : JsonSerializer.Deserialize<TokenContainer>(value));
        }
    }
}

internal sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string Scope);

internal sealed class DirectOAuth(ICredentialStore credentials, HttpClient http, LoopbackOAuthReceiver receiver)
{
    private static string TokenKey(ConnectorConfiguration config) => $"connector.{config.Id}.oauth";
    public bool HasTokens(ConnectorConfiguration config) => credentials.Get(TokenKey(config)) is not null;

    public async Task AuthorizeAsync(ConnectorConfiguration config, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.ClientId))
            throw new InvalidOperationException("Enter your own OAuth client ID before authorizing.");
        var spotify = config.Transport == ConnectorTransport.Spotify;
        var redirect = new Uri($"http://127.0.0.1:{config.CallbackPort}/callback/");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirect.AbsoluteUri,
            ["state"] = state,
            ["scope"] = string.Join(' ', config.Scopes),
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        };
        if (!spotify)
        {
            parameters["access_type"] = "offline";
            parameters["prompt"] = "consent";
        }
        var endpoint = spotify ? "https://accounts.spotify.com/authorize" : "https://accounts.google.com/o/oauth2/v2/auth";
        var authorization = new Uri(endpoint + "?" + string.Join('&', parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        var result = await receiver.ReceiveAsync(new()
        {
            AuthorizationUri = authorization,
            RedirectUri = redirect
        }, ct).ConfigureAwait(false);
        if (result is null || !LoopbackOAuthReceiver.StateMatches(state, result.State) || string.IsNullOrWhiteSpace(result.Code))
            throw new UnauthorizedAccessException("Invalid authorization response. No token was requested.");
        await ExchangeAsync(config, new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = result.Code,
            ["redirect_uri"] = redirect.AbsoluteUri,
            ["code_verifier"] = verifier
        }, null, ct).ConfigureAwait(false);
    }

    public async Task<string> GetAccessTokenAsync(ConnectorConfiguration config, CancellationToken ct)
    {
        var raw = credentials.Get(TokenKey(config)) ?? throw new UnauthorizedAccessException("Not authenticated. Authorize this connection first.");
        var token = JsonSerializer.Deserialize<OAuthTokens>(raw) ?? throw new UnauthorizedAccessException("Saved authorization could not be read.");
        if (token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
            return token.AccessToken;
        if (string.IsNullOrEmpty(token.RefreshToken))
            throw new UnauthorizedAccessException("Authorization expired. Sign in again.");
        return (await ExchangeAsync(config, new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token.RefreshToken
        }, token.RefreshToken, ct).ConfigureAwait(false)).AccessToken;
    }

    private async Task<OAuthTokens> ExchangeAsync(ConnectorConfiguration config, Dictionary<string, string> fields, string? oldRefresh, CancellationToken ct)
    {
        fields["client_id"] = config.ClientId;
        var secret = credentials.Get($"connector.{config.Id}.client-secret");
        if (config.Transport == ConnectorTransport.Google && !string.IsNullOrEmpty(secret))
            fields["client_secret"] = secret;
        var endpoint = config.Transport == ConnectorTransport.Spotify ? "https://accounts.spotify.com/api/token" : "https://oauth2.googleapis.com/token";
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(fields), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new UnauthorizedAccessException($"OAuth token exchange failed ({(int)response.StatusCode}). Verify client ID, redirect URI and scopes. Reauthorize if access was revoked.");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tokens = new OAuthTokens(root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var r) ? r.GetString() : oldRefresh,
            DateTimeOffset.UtcNow.AddSeconds(root.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600),
            root.TryGetProperty("scope", out var s) ? s.GetString() ?? "" : string.Join(' ', config.Scopes));
        credentials.Set(TokenKey(config), JsonSerializer.Serialize(tokens));
        return tokens;
    }

    public async Task<bool> RevokeAsync(ConnectorConfiguration config, CancellationToken ct)
    {
        var raw = credentials.Get(TokenKey(config));
        if (raw is null || config.Transport != ConnectorTransport.Google)
            return false;
        var tokens = JsonSerializer.Deserialize<OAuthTokens>(raw);
        if (tokens is null)
            return false;
        using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["token"] = tokens.RefreshToken ?? tokens.AccessToken
        }), ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
