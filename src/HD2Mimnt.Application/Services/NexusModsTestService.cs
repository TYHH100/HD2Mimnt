using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;

namespace HD2Mimnt.Application.Services;

/// <summary>开发工具的认证入口，加密保存并与下载和更新共用注册的平台实例。</summary>
public sealed class NexusModsTestService(IPlatformRegistry platforms, IPlatformCredentialStore? credentials = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsApiKeySaved { get; private set; }
    private NexusModsPlatformAdapter Platform =>
        platforms.GetPlatform("nexusmods") as NexusModsPlatformAdapter
        ?? throw new InvalidOperationException("Nexus Mods 平台未注册。");

    public bool IsAuthenticated => Platform.IsAuthenticated;

    public async Task ApplyApiKeyAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        var key = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ArgumentException("请输入有效的 API 密钥，不能包含换行或控制字符。", nameof(apiKey));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var platform = Platform;
            if (credentials != null) await credentials.SaveApiKeyAsync("nexusmods", key, cancellationToken);
            platform.SetApiKey(key);
            IsApiKeySaved = credentials != null;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearApiKeyAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var platform = Platform;
            if (credentials != null) await credentials.DeleteApiKeyAsync("nexusmods", cancellationToken);
            platform.ClearApiKey();
            IsApiKeySaved = false;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreApiKeyAsync(CancellationToken cancellationToken = default)
    {
        if (credentials == null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = await credentials.ReadApiKeyAsync("nexusmods", cancellationToken);
            if (key == null) { Platform.ClearApiKey(); IsApiKeySaved = false; return; }
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
                throw new InvalidOperationException("已保存的 API 密钥无效，请重新填写。");
            Platform.SetApiKey(key);
            IsApiKeySaved = true;
        }
        catch
        {
            Platform.ClearApiKey();
            IsApiKeySaved = false;
            throw;
        }
        finally { _gate.Release(); }
    }
}
