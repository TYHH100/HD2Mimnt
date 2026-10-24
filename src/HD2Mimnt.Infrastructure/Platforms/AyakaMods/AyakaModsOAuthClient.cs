using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Platforms.AyakaMods;

/// <summary>Public-client Authorization Code + PKCE; no tool key, client secret or embedded login.</summary>
public sealed class AyakaModsOAuthClient(HttpClient client, TimeProvider? time = null) : IAyakaModsOAuthClient
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly RateLimiter _rateLimiter = new();
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Query(Dictionary<string, string> fields) => string.Join("&", fields.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));

    public async Task<AyakaModsOAuthTokens> LoginAsync(AyakaModsOAuthOptions options, Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        var registered = options.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new HttpListener();
        Uri? redirect = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            redirect = new UriBuilder(registered) { Port = port }.Uri;
            listener.Prefixes.Clear();
            listener.Prefixes.Add(redirect.GetLeftPart(UriPartial.Authority) + "/");
            try { listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 2) { }
            catch (HttpListenerException) { throw new AyakaModsOAuthException("无法监听 A 网 OAuth 本机回调，请重试。"); }
        }
        using var closeOnCancel = timeout.Token.Register(listener.Close);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            openBrowser(new Uri("https://ayakamods.com/oauth2/authorize?" + Query(new()
            {
                ["client_id"] = options.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect!.AbsoluteUri,
                ["scope"] = "mod:read mod_rating:read", ["state"] = state,
                ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
            })));
            string code;
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var request = context.Request;
                var query = request.QueryString;
                var valid = request.HttpMethod == "GET" && request.Url?.Authority == redirect!.Authority &&
                    request.Url.AbsolutePath == redirect.AbsolutePath && IPAddress.IsLoopback(request.RemoteEndPoint.Address) &&
                    query.GetValues("state") is { Length: 1 } states && states[0] == state;
                if (!valid) { await ReplyAsync(context, 400, "Invalid OAuth callback.", timeout.Token); continue; }
                if (query.GetValues("error") is { Length: > 0 })
                {
                    await ReplyAsync(context, 200, "Authorization declined. You may close this page.", timeout.Token);
                    throw new AyakaModsOAuthException("A 网授权被拒绝，请重新登录并允许应用访问。");
                }
                if (query.GetValues("code") is not { Length: 1 } codes || string.IsNullOrWhiteSpace(codes[0]))
                { await ReplyAsync(context, 400, "Missing authorization code.", timeout.Token); continue; }
                code = codes[0]!;
                await ReplyAsync(context, 200, "Authorization received. Return to HD2Mimnt to check the result.", timeout.Token);
                break;
            }
            listener.Close();
            return await RequestAsync(new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = options.ClientId,
                ["code"] = code, ["redirect_uri"] = redirect!.AbsoluteUri, ["code_verifier"] = verifier
            }, null, timeout.Token);
        }
        catch (Exception ex) when (timeout.IsCancellationRequested && ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AyakaModsOAuthException("A 网登录等待超时，请重新登录。");
        }
    }

    public Task<AyakaModsOAuthTokens> RefreshAsync(AyakaModsOAuthOptions options, AyakaModsOAuthTokens tokens,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        return RequestAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = options.ClientId,
            ["refresh_token"] = tokens.RefreshToken }, tokens, cancellationToken);
    }

    private async Task<AyakaModsOAuthTokens> RequestAsync(Dictionary<string, string> fields,
        AyakaModsOAuthTokens? previous, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://ayakamods.com/api/oauth2/token")
        { Content = new FormUrlEncodedContent(fields) };
        await _rateLimiter.WaitIfNeededAsync(token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        _rateLimiter.UpdateFromHeaders(response);
        if (!response.IsSuccessStatusCode)
            throw new AyakaModsOAuthException($"A 网令牌请求失败（HTTP {(int)response.StatusCode}）。",
                previous != null && response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized);
        try
        {
            if (response.Content.Headers.ContentLength is > 65536) throw new JsonException();
            await using var source = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            var bytes = new byte[4096];
            int read;
            while ((read = await source.ReadAsync(bytes, token)) > 0)
            {
                if (buffer.Length + read > 65536) throw new JsonException();
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (!string.Equals(root.GetProperty("token_type").GetString(), "bearer", StringComparison.OrdinalIgnoreCase)) throw new JsonException();
            var access = root.GetProperty("access_token").GetString();
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : previous?.RefreshToken;
            var lifetime = root.GetProperty("expires_in").GetInt64();
            if (string.IsNullOrWhiteSpace(access) || access.Any(char.IsControl) || string.IsNullOrWhiteSpace(refresh) ||
                refresh.Any(char.IsControl) || lifetime <= 0 || lifetime > 31536000) throw new JsonException();
            return new() { AccessToken = access, RefreshToken = refresh, ExpiresAt = _time.GetUtcNow().AddSeconds(lifetime),
                Scope = root.TryGetProperty("scope", out var scope) ? scope.GetString() ?? "" : previous?.Scope ?? "" };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        { throw new AyakaModsOAuthException("A 网令牌响应无效，未保存登录信息。"); }
    }

    private static async Task ReplyAsync(HttpListenerContext context, int status, string message, CancellationToken token)
    {
        using var response = context.Response;
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
        var bytes = Encoding.UTF8.GetBytes(message);
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, token);
    }
}
