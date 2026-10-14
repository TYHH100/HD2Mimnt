namespace HD2Mimnt.Core.Interfaces;

/// <summary>平台凭据的本地加密存储，不通过普通设置或日志传递。历史 ApiKey 方法名也接收 OAuth 凭据封装。</summary>
public interface IPlatformCredentialStore
{
    Task SaveApiKeyAsync(string platformId, string apiKey, CancellationToken cancellationToken = default);
    Task<string?> ReadApiKeyAsync(string platformId, CancellationToken cancellationToken = default);
    Task DeleteApiKeyAsync(string platformId, CancellationToken cancellationToken = default);
}
