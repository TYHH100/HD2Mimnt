using HD2Mimnt.Infrastructure.Platforms.NexusMods.Models;
using System.Net.Http.Headers;
using System.Text.Json;

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
    private readonly RateLimiter _rateLimiter = new();
    private string? _apiKey;
    private string? _bearerToken;

    public NexusModsApiClient(HttpClient httpClient) => _httpClient = httpClient;

    /// <summary>Sets API-key authentication without modifying shared HTTP headers.</summary>
    public void SetApiKey(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _apiKey = apiKey;
        _bearerToken = null;
    }

    /// <summary>Sets an existing signed JWT; this adapter does not issue tokens.</summary>
    public void SetBearerToken(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        _bearerToken = token;
        _apiKey = null;
    }

    public void ClearApiKey() => _apiKey = null;
    public void ClearAuthentication()
    {
        _apiKey = null;
        _bearerToken = null;
    }

    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_apiKey) || !string.IsNullOrWhiteSpace(_bearerToken);

    public Task<TrendingModsResponse> GetTrendingModsAsync(CancellationToken cancellationToken) =>
        GetDataAsync<TrendingModsResponse>($"/games/{GameDomain}/trending-mods", false, cancellationToken);

    public Task<NexusMod> GetModAsync(string modId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        ArgumentException.ThrowIfNullOrWhiteSpace(modId);
        return GetDataAsync<NexusMod>($"/games/{GameDomain}/mods/{Uri.EscapeDataString(modId)}", true, cancellationToken);
    }

    public Task<ModFilesResponse> GetModFilesAsync(string globalModId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        return GetDataAsync<ModFilesResponse>($"/mods/{Uri.EscapeDataString(globalModId)}/files", true, cancellationToken);
    }

    public Task<FileVersionsResponse> GetFileVersionsAsync(string fileId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        return GetDataAsync<FileVersionsResponse>($"/mod-files/{Uri.EscapeDataString(fileId)}/versions", true, cancellationToken);
    }

    public async Task<DownloadResponse> GetDownloadLinkAsync(string versionId, CancellationToken cancellationToken)
    {
        EnsureAuthenticated();
        using var request = CreateApiRequest(HttpMethod.Post,
            $"/mod-file-versions/{Uri.EscapeDataString(versionId)}/download-repacked", true);
        return await SendJsonAsync<DownloadResponse>(request, false, cancellationToken);
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
            throw new InvalidOperationException("Nexus Mods 此操作需要 API Key 或 Bearer JWT 认证。");
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string path, bool authenticated)
    {
        var request = new HttpRequestMessage(method, ApiBaseUrl + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Application-Name", "HD2Mimnt");
        if (authenticated)
        {
            if (_apiKey != null) request.Headers.Add("apikey", _apiKey);
            else if (_bearerToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
        }
        return request;
    }

    private async Task<T> GetDataAsync<T>(string path, bool authenticated, CancellationToken cancellationToken)
    {
        using var request = CreateApiRequest(HttpMethod.Get, path, authenticated);
        return await SendJsonAsync<T>(request, true, cancellationToken);
    }

    private async Task<T> SendJsonAsync<T>(HttpRequestMessage request, bool enveloped, CancellationToken cancellationToken)
    {
        await _rateLimiter.WaitIfNeededAsync(cancellationToken);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        _rateLimiter.UpdateFromHeaders(response);
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
            throw new HttpRequestException(
                $"Nexus Mods API ({(int)response.StatusCode}): {problem?.Title ?? response.ReasonPhrase} {problem?.Detail}".TrimEnd(),
                null, response.StatusCode);
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
}
