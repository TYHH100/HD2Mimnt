using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Platforms.AyakaMods;

/// <summary>Only this client sends the tool key to the fixed Ayaka API origin.</summary>
internal sealed class AyakaModsApiClient(HttpClient client, ILogger? logger = null) : IDisposable
{
    internal static readonly Uri BaseUri = new("https://ayakamods.com/api/");
    private string? _key;
    private readonly SemaphoreSlim _requests = new(1, 1);
    private readonly RateLimiter _rateLimiter = new();
    private int _revision;
    public int Revision => Volatile.Read(ref _revision);
    public string? ApiKey => Volatile.Read(ref _key);
    public bool IsAuthenticated => ApiKey != null;

    public static string ValidateKey(string value)
    {
        if (value.Any(char.IsControl) || string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("请输入有效的 API 密钥，不能包含换行或控制字符。");
        return value.Trim();
    }

    public void SetApiKey(string value)
    { Volatile.Write(ref _key, ValidateKey(value)); Interlocked.Increment(ref _revision); }
    public void ClearApiKey()
    { Volatile.Write(ref _key, null); Interlocked.Increment(ref _revision); }

    public static string Id(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("A 网编号无效。");
        return id.ToString(CultureInfo.InvariantCulture);
    }

    public void EnsureAuthenticated()
    {
        if (!IsAuthenticated) throw new InvalidOperationException("请先在“工具 → 开发工具”保存并应用 A 网 API Key。");
    }

    public async Task<JsonElement> GetAsync(string path, CancellationToken token)
    {
        var revision = Revision;
        var key = ApiKey;
        EnsureAuthenticated();
        await _requests.WaitAsync(token);
        try
        {
            EnsureRevision(revision);
            await _rateLimiter.WaitIfNeededAsync(token);
            EnsureRevision(revision);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, path));
            request.Headers.Add("XF-Api-Key", key);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            _rateLimiter.UpdateFromHeaders(response);
            EnsureRevision(revision);
            if (logger?.IsEnabled(LogLevel.Debug) == true)
                await PlatformResponseLog.WriteAsync(logger, request, response, "Ayaka", Operation(request.RequestUri!),
                    [key], token, preserveApiErrorCodes: true);
            if (!response.IsSuccessStatusCode)
            {
                // Raw bodies stay out of exception history; Debug logs sanitize them separately.
                throw new PlatformRequestException("Ayaka Mods", "访问 A 网 API", response.StatusCode, null);
            }
            if (response.Content.Headers.ContentType?.MediaType is not "application/json")
                throw new InvalidDataException("A 网 API 未返回 JSON，请检查接口是否可用。");
            var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
            EnsureRevision(revision);
            return result;
        }
        finally { _requests.Release(); }
    }
    private static string Operation(Uri uri) => uri.AbsolutePath switch
    {
        var path when path.EndsWith("/versions/", StringComparison.Ordinal) => "获取模组版本与文件列表",
        var path when path.StartsWith("/api/mod-games/", StringComparison.Ordinal) && path.EndsWith("/mods/", StringComparison.Ordinal) => "获取游戏模组列表",
        var path when path.StartsWith("/api/mods/", StringComparison.Ordinal) => "获取模组信息",
        _ => "获取游戏信息"
    };
    public void EnsureRevision(int revision)
    { if (revision != Revision) throw new OperationCanceledException("A 网凭据已更改，请重试。"); }
    public void Dispose() => _requests.Dispose();
}
