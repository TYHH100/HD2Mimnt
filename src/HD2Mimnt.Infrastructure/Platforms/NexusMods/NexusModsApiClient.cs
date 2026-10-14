using HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;
using HD2Mimnt.Core.Entities;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Net.Http.Json;
using System.Globalization;
using System.Net;

namespace HD2Mimnt.Infrastructure.Platforms.NexusMods;

/// <summary>Owns Nexus v3 endpoints, authentication, JSON envelopes and HTTP errors.</summary>
internal sealed class NexusModsApiClient
{
    private const string ApiBaseUrl = "https://api.nexusmods.com/v3";
    internal const string GameDomain = "helldivers2";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    private readonly HttpClient _httpClient;
    private readonly string _applicationVersion;
    private readonly RateLimiter _rateLimiter = new();
    private string? _apiKey;
    private string? _bearerToken;
    private Func<CancellationToken, Task<string>>? _oauthTokenProvider;
    private NexusModsRateLimit _rateLimit = new();
    private int _authenticationRevision;
    private readonly object _quotaGate = new();
    public NexusModsRateLimit RateLimit => Volatile.Read(ref _rateLimit);
    public event EventHandler? RateLimitChanged;

    private void ResetRateLimit()
    {
        lock (_quotaGate)
        {
            Interlocked.Increment(ref _authenticationRevision);
            Volatile.Write(ref _rateLimit, new());
        }
        RateLimitChanged?.Invoke(this, EventArgs.Empty);
    }

    public NexusModsApiClient(HttpClient httpClient, string applicationVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationVersion);
        _httpClient = httpClient;
        _applicationVersion = applicationVersion;
    }

    /// <summary>Sets API-key authentication without modifying shared HTTP headers.</summary>
    public void SetApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _bearerToken = null;
        _oauthTokenProvider = null;
        ResetRateLimit();
    }

    /// <summary>Sets an existing signed JWT; this adapter does not issue tokens.</summary>
    public void SetBearerToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _bearerToken = token;
        _apiKey = null;
        _oauthTokenProvider = null;
        ResetRateLimit();
    }

    public void SetOAuthAuthentication(Func<CancellationToken, Task<string>> tokenProvider)
    {
        _oauthTokenProvider = tokenProvider;
        _apiKey = null;
        _bearerToken = null;
        ResetRateLimit();
    }

    public void ClearApiKey() { _apiKey = null; ResetRateLimit(); }
    public void ClearAuthentication()
    {
        _apiKey = null;
        _bearerToken = null;
        _oauthTokenProvider = null;
        ResetRateLimit();
    }

    public bool IsAuthenticated => _oauthTokenProvider != null || !string.IsNullOrWhiteSpace(_apiKey) || !string.IsNullOrWhiteSpace(_bearerToken);

    public Task<TrendingModsResponse> GetTrendingModsAsync(CancellationToken cancellationToken) =>
        GetDataAsync<TrendingModsResponse>($"/games/{GameDomain}/trending-mods", false, "获取热门模组", cancellationToken);

    public Task<NexusMod> GetModAsync(string modId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return GetDataAsync<NexusMod>($"/games/{GameDomain}/mods/{Uri.EscapeDataString(modId)}", true, "获取模组信息", cancellationToken);
    }

    public Task<ModFilesResponse> GetModFilesAsync(string globalModId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        return GetDataAsync<ModFilesResponse>($"/mods/{Uri.EscapeDataString(globalModId)}/files", true, "获取模组文件列表", cancellationToken);
    }

    public Task<FileVersionsResponse> GetFileVersionsAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        return GetDataAsync<FileVersionsResponse>($"/mod-files/{Uri.EscapeDataString(fileId)}/versions", true, "获取文件版本", cancellationToken);
    }

    public async Task<DownloadResponse> GetDownloadLinkAsync(string versionId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        using var request = CreateApiRequest(HttpMethod.Post,
            $"/mod-file-versions/{Uri.EscapeDataString(versionId)}/download-repacked", true);
        return await SendJsonAsync<DownloadResponse>(request, false, "获取下载链接", cancellationToken);
    }

    // The caller owns the response. Signed archives must not carry API credentials.
    public async Task<HttpResponseMessage> GetArchiveAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void EnsureAuthenticated()
    {
        if (!IsAuthenticated)
            throw new InvalidOperationException("请先在“设置 → 账号”中登录 N 网，或在开发工具应用个人 API Key 测试。");
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string path, bool authenticated)
    {
        var request = new HttpRequestMessage(method, ApiBaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Application-Name", "HD2Mimnt");
        request.Headers.Add("Application-Version", _applicationVersion);
        if (authenticated)
        {
            request.Options.Set(AuthenticationRevisionKey, Volatile.Read(ref _authenticationRevision));
            if (_oauthTokenProvider != null) request.Options.Set(OAuthProviderKey, _oauthTokenProvider);
            else if (_apiKey != null) request.Headers.Add("apikey", _apiKey);
            else if (_bearerToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }
        return request;
    }

    internal async Task<T> GetLegacyDataAsync<T>(string path, string operation, CancellationToken cancellationToken,
        string? downloadKey = null)
    {
        EnsureAuthenticated();
        using var request = CreateApiRequest(HttpMethod.Get, path, true);
        request.RequestUri = new Uri("https://api.nexusmods.com/v1" + path);
        return await SendJsonAsync<T>(request, false, operation, cancellationToken, downloadKey);
    }

    internal async Task<T> SearchGraphAsync<T>(string query, object variables, CancellationToken token)
    {
        using var request = CreateApiRequest(HttpMethod.Post, "", IsAuthenticated);
        request.RequestUri = new Uri("https://api.nexusmods.com/v2/graphql");
        request.Content = JsonContent.Create(new { query, variables });
        return await SendJsonAsync<T>(request, false, "搜索 N 网模组", token);
    }

    private async Task<T> GetDataAsync<T>(string path, bool authenticated, string operation, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Get, path, authenticated);
        return await SendJsonAsync<T>(request, true, operation, cancellationToken);
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, bool enveloped, string operation, CancellationToken cancellationToken,
        string? downloadKey = null)
    {
        await _rateLimiter.WaitIfNeededAsync(cancellationToken);
        if (request.Options.TryGetValue(OAuthProviderKey, out var provider))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await provider(cancellationToken));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _rateLimiter.UpdateFromHeaders(response);
        if ((request.Headers.Authorization != null || request.Headers.Contains("apikey")) &&
            request.Options.TryGetValue(AuthenticationRevisionKey, out var revision) && revision == Volatile.Read(ref _authenticationRevision))
            UpdateRateLimit(response, revision);
        if (!response.IsSuccessStatusCode)
        {
            ProblemDetails? problem = null;
            if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
            {
                try
                {
                    var body = await response.Content.ReadAsStringAsync(cancellationToken);
                    problem = JsonSerializer.Deserialize<ProblemDetails>(body, JsonOptions);
                }
                catch (JsonException) { /* Preserve the HTTP failure if the error body is invalid. */ }
            }
            var reason = string.Join(" · ", new[] { problem?.Title, problem?.Detail, problem?.Message }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            // 服务端可能回显输入，展示前先去掉本次请求的认证值。
            var requestKey = request.Headers.TryGetValues("apikey", out var keyValues) ? keyValues.SingleOrDefault() : null;
            foreach (var secret in new[] { requestKey, request.Headers.Authorization?.Parameter, downloadKey,
                         downloadKey == null ? null : Uri.EscapeDataString(downloadKey) })
                if (!string.IsNullOrEmpty(secret)) reason = reason.Replace(secret, "[已隐藏]", StringComparison.Ordinal);
            if (reason.Length > 1000) reason = reason[..1000] + "…";
            throw new PlatformRequestException("Nexus Mods", operation, response.StatusCode,
                string.IsNullOrWhiteSpace(reason) ? null : reason);
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (enveloped)
        {
            var envelope = await JsonSerializer.DeserializeAsync<Envelope<T>>(stream, JsonOptions, cancellationToken);
            return envelope is null || envelope.Data is null
                ? throw new JsonException("Nexus Mods API 响应缺少 data 字段。") : envelope.Data;
        }
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
            ?? throw new JsonException("Nexus Mods API 返回了空响应。");
    }
    private static readonly HttpRequestOptionsKey<Func<CancellationToken, Task<string>>> OAuthProviderKey = new("NexusOAuthTokenProvider");
    private static readonly HttpRequestOptionsKey<int> AuthenticationRevisionKey = new("NexusAuthenticationRevision");

    private void UpdateRateLimit(HttpResponseMessage response, int revision)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
        int? Count(string name) => int.TryParse(Header(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value >= 0 ? value : null;
        DateTimeOffset? Date(string name)
        {
            var raw = Header(name);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) return date;
            // v1 文档的 Daily-Reset 时区示例使用 +0000。
            if (raw is { Length: >= 5 } && raw[^5] is '+' or '-')
                if (DateTimeOffset.TryParse(raw.Insert(raw.Length - 2, ":"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out date)) return date;
            return null;
        }
        var hourlyLimit = Count("X-RL-Hourly-Limit");
        var hourlyRemaining = Count("X-RL-Hourly-Remaining");
        var dailyLimit = Count("X-RL-Daily-Limit");
        var dailyRemaining = Count("X-RL-Daily-Remaining");
        var hourlyReset = Date("X-RL-Hourly-Reset");
        var dailyReset = Date("X-RL-Daily-Reset");
        var retry = response.Headers.RetryAfter;
        var retryAt = response.StatusCode == HttpStatusCode.TooManyRequests
            ? retry?.Date ?? (retry?.Delta is { } delta ? DateTimeOffset.UtcNow.Add(delta) : (DateTimeOffset?)null) : null;
        if (hourlyLimit == null && hourlyRemaining == null && dailyLimit == null && dailyRemaining == null &&
            hourlyReset == null && dailyReset == null && retryAt == null) return;
        lock (_quotaGate)
        {
            if (revision != _authenticationRevision) return;
            Volatile.Write(ref _rateLimit, new()
            {
                HourlyLimit = hourlyLimit, HourlyRemaining = hourlyRemaining, HourlyReset = hourlyReset,
                DailyLimit = dailyLimit, DailyRemaining = dailyRemaining, DailyReset = dailyReset,
                RetryAt = retryAt, UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        RateLimitChanged?.Invoke(this, EventArgs.Empty);
    }
}
