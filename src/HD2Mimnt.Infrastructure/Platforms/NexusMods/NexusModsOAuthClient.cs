using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>系统浏览器、仅本机回调、S256 PKCE 和 Nexus 签名校验。</summary>
public sealed class NexusModsOAuthClient(HttpClient httpClient, TimeProvider? timeProvider = null,
    string? tokenPublicKey = null, string? applicationVersion = null) : INexusModsOAuthClient
{
    // 公钥来自官方 OAuth 指南。这里只验证签名，无法用它签发令牌。
    private const string NexusPublicKey = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAs/57oX8HW8xC+W/etH7J
        PgoSTiGPKZa6Gq3/K/7GgrpJcZhPdr9MTGocb2uLzQBJW+u1XpSgyeKH4JCxxeHF
        3zcUtb7SUg3KnxlR5QUmOnqBvbUuL4opUpfgWUGltASduYqZBJD2WTK8Hvwh9X1v
        ACeqp1zgorZm3f0J2H15TDbzIp9ihCFuthJUFumdzvrt/WvimW2fiyqndTNQwe5h
        XM8hj8cemdWQXCd99qnj7UQkpu+yNisVMHQCsAqXITe6Ehp6IY9eCd4DJKjDvyLc
        3vbY8UL+bcVK5tYAKemZ56uw3q1YdcyqGlItyLi4j4EISdBQaCCqT7YZUhMYzhUd
        1QIDAQAB
        -----END PUBLIC KEY-----
        """;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly RateLimiter _rateLimiter = new();
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private Dictionary<string, RSAParameters> _signingKeys = new(StringComparer.Ordinal);
    private DateTimeOffset _keysExpireAt;
    private readonly string _applicationVersion = applicationVersion ??
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(NexusModsOAuthClient).Assembly).GetName().Version!.ToString();

    public async Task<NexusModsOAuthTokens> LoginAsync(NexusModsOAuthOptions options, Action<Uri> openBrowser,
        CancellationToken cancellationToken = default)
    {
        var redirect = options.Validate();
        ArgumentNullException.ThrowIfNull(openBrowser);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new HttpListener();
        listener.Prefixes.Add(redirect.AbsoluteUri);
        try { listener.Start(); }
        catch (HttpListenerException)
        {
            throw new NexusModsOAuthException("无法监听 OAuth 本机回调，端口可能被占用。请关闭另一个登录窗口后重试。");
        }
        using var closeOnCancel = timeout.Token.Register(listener.Close);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            openBrowser(new Uri("https://users.nexusmods.com/oauth/authorize?" + FormQuery(new()
            {
                ["client_id"] = options.ClientId, ["response_type"] = "code", ["scope"] = "",
                ["redirect_uri"] = options.RedirectUri, ["state"] = state,
                ["code_challenge_method"] = "S256", ["code_challenge"] = challenge
            })));
            string code;
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
                var request = context.Request;
                var query = request.QueryString;
                var valid = request.HttpMethod == "GET" && request.Url?.AbsolutePath == redirect.AbsolutePath &&
                    request.Url.Authority == redirect.Authority &&
                    IPAddress.IsLoopback(request.RemoteEndPoint.Address) &&
                    query.GetValues("state") is { Length: 1 } states && states[0] == state;
                if (!valid)
                {
                    await ReplyAsync(context, 400, "Invalid OAuth callback.", timeout.Token);
                    continue; // 不匹配的回调不能消耗或终止当前登录。
                }
                if (query.GetValues("error") is { Length: > 0 })
                {
                    await ReplyAsync(context, 200, "Authorization cancelled. You may close this page.", timeout.Token);
                    throw new NexusModsOAuthException("N 网授权被拒绝，请重新登录并允许应用访问。");
                }
                if (query.GetValues("code") is not { Length: 1 } codes || string.IsNullOrWhiteSpace(codes[0]))
                {
                    await ReplyAsync(context, 400, "Missing authorization code.", timeout.Token);
                    continue;
                }
                code = codes[0]!;
                await ReplyAsync(context, 200, "Authorization received. Return to HD2Mimnt to check the login result.", timeout.Token);
                break;
            }
            listener.Close();
            return await RequestTokensAsync(new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = options.ClientId,
                ["redirect_uri"] = options.RedirectUri, ["scope"] = "", ["code"] = code,
                ["code_verifier"] = verifier
            }, null, timeout.Token);
        }
        catch (Exception ex) when (timeout.IsCancellationRequested &&
            ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NexusModsOAuthException("N 网登录等待超时，请重新登录。");
        }
    }

    public async Task<NexusModsOAuthTokens> RefreshAsync(NexusModsOAuthOptions options, NexusModsOAuthTokens tokens,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        var refreshed = await RequestTokensAsync(new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = options.ClientId,
            ["refresh_token"] = tokens.RefreshToken
        }, tokens.RefreshToken, cancellationToken);
        if (refreshed.UserId != tokens.UserId)
            throw new NexusModsOAuthException("刷新令牌的账户与原登录账户不同，请重新登录。", true);
        return refreshed;
    }

    private async Task<NexusModsOAuthTokens> RequestTokensAsync(Dictionary<string, string> fields,
        string? previousRefreshToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://users.nexusmods.com/oauth/token")
        { Content = new FormUrlEncodedContent(fields) };
        request.Headers.Add("Application-Name", "HD2Mimnt");
        request.Headers.Add("Application-Version", _applicationVersion);
        await _rateLimiter.WaitIfNeededAsync(token);
        using var response = await httpClient.SendAsync(request, token);
        _rateLimiter.UpdateFromHeaders(response);
        if (!response.IsSuccessStatusCode)
        {
            // 不展示服务端响应，避免回显 code/verifier/refresh_token。
            var rejected = previousRefreshToken != null && (int)response.StatusCode is >= 400 and < 500 &&
                response.StatusCode != HttpStatusCode.TooManyRequests;
            throw new NexusModsOAuthException($"N 网令牌请求失败（HTTP {(int)response.StatusCode}）。" +
                (rejected ? "授权可能已撤销，请重新登录。" : "请稍后重试。"), rejected);
        }
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var data = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var root = data.RootElement;
            if (!root.TryGetProperty("token_type", out var type) ||
                !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
            var access = root.GetProperty("access_token").GetString()!;
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : previousRefreshToken;
            var lifetime = root.GetProperty("expires_in").GetInt64();
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(refresh) ||
                refresh.Any(char.IsControl) || lifetime <= 0 || lifetime > 31536000)
                throw new JsonException();
            var (userId, name, expiry) = await ValidateAccessTokenAsync(access, token);
            var responseExpiry = _time.GetUtcNow().AddSeconds(lifetime);
            return new()
            {
                AccessToken = access, RefreshToken = refresh, UserId = userId, UserName = name,
                ExpiresAt = expiry < responseExpiry ? expiry : responseExpiry
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or CryptographicException or
            ArgumentException or InvalidOperationException or KeyNotFoundException or OverflowException)
        { throw new NexusModsOAuthException("N 网令牌响应或签名无效，未保存登录信息，请重新登录。"); }
    }

    private async Task<(string UserId, string Name, DateTimeOffset Expiry)> ValidateAccessTokenAsync(string access, CancellationToken token)
    {
        var parts = access.Split('.');
        if (parts.Length != 3) throw new JsonException();
        using var header = JsonDocument.Parse(Decode(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256") throw new JsonException();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(tokenPublicKey ?? NexusPublicKey);
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signature = Decode(parts[2]);
        if (!rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            var kid = header.RootElement.TryGetProperty("kid", out var keyId) ? keyId.GetString() : null;
            if (string.IsNullOrWhiteSpace(kid) || !await VerifyCurrentKeyAsync(kid, signed, signature, token))
                throw new CryptographicException();
        }
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        var root = payload.RootElement;
        if (root.GetProperty("iss").GetString() is not ("nexus-user-service" or "https://users.nexusmods.com"))
            throw new JsonException();
        var expiry = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("exp").GetInt64());
        if (expiry <= _time.GetUtcNow() || (root.TryGetProperty("nbf", out var nbf) &&
            DateTimeOffset.FromUnixTimeSeconds(nbf.GetInt64()) > _time.GetUtcNow())) throw new JsonException();
        var id = root.GetProperty("sub").GetString();
        var user = root.GetProperty("user");
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) || userId <= 0 ||
            user.GetProperty("id").GetInt64() != userId) throw new JsonException();
        var name = user.GetProperty("username").GetString();
        if (string.IsNullOrWhiteSpace(name)) throw new JsonException();
        return (id!, name, expiry);
    }

    private async Task<bool> VerifyCurrentKeyAsync(string kid, byte[] signed, byte[] signature, CancellationToken token)
    {
        await _keyGate.WaitAsync(token);
        try
        {
            bool Verify()
            {
                if (!_signingKeys.TryGetValue(kid, out var key)) return false;
                using var rsa = RSA.Create();
                rsa.ImportParameters(key);
                return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }
            if (_time.GetUtcNow() < _keysExpireAt && Verify()) return true;
            // 固定官方 Discovery 公钥地址，不采信令牌内 jku/x5u 提供的地址。
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://users.nexusmods.com/oauth/discovery/keys");
            request.Headers.Add("Application-Name", "HD2Mimnt");
            request.Headers.Add("Application-Version", _applicationVersion);
            await _rateLimiter.WaitIfNeededAsync(token);
            using var response = await httpClient.SendAsync(request, token);
            _rateLimiter.UpdateFromHeaders(response);
            if (!response.IsSuccessStatusCode)
                throw new NexusModsOAuthException($"N 网公钥请求失败（HTTP {(int)response.StatusCode}），请稍后重试。");
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
            var keys = document.RootElement.GetProperty("keys");
            if (keys.GetArrayLength() > 16) throw new JsonException();
            var current = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            foreach (var key in keys.EnumerateArray())
            {
                if (key.GetProperty("kty").GetString() != "RSA" ||
                    (key.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256") ||
                    (key.TryGetProperty("use", out var use) && use.GetString() != "sig")) continue;
                var id = key.GetProperty("kid").GetString();
                if (string.IsNullOrWhiteSpace(id)) throw new JsonException();
                var modulus = Decode(key.GetProperty("n").GetString()!);
                var exponent = Decode(key.GetProperty("e").GetString()!);
                if (modulus.Length is < 256 or > 1024 || exponent.Length is < 1 or > 8) throw new JsonException();
                current.Add(id, new() { Modulus = modulus, Exponent = exponent });
            }
            _signingKeys = current;
            _keysExpireAt = _time.GetUtcNow().AddMinutes(10);
            return Verify();
        }
        finally { _keyGate.Release(); }
    }

    private static async Task ReplyAsync(HttpListenerContext context, int status, string message, CancellationToken token)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var bytes = Encoding.UTF8.GetBytes(message);
        context.Response.ContentLength64 = bytes.Length;
        try { await context.Response.OutputStream.WriteAsync(bytes, token); }
        finally { context.Response.Close(); }
    }

    private static string FormQuery(Dictionary<string, string> fields) => string.Join("&",
        fields.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') +
        new string('=', (4 - value.Length % 4) % 4));
}
