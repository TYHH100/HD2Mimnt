using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms.AyakaMods;

namespace HD2Mimnt.Application.Services;

/// <summary>开发阶段的 A 网密钥入口；沿用平台凭据表和 DPAPI，不进入普通设置。</summary>
public sealed class AyakaModsAuthService(IPlatformRegistry platforms, IPlatformCredentialStore? credentials = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AyakaModsPlatformAdapter Platform => platforms.GetPlatform("ayakamods") as AyakaModsPlatformAdapter
        ?? throw new InvalidOperationException("Ayaka Mods 平台未注册。");
    public bool IsAuthenticated => Platform.IsAuthenticated;
    public bool IsApiKeySaved { get; private set; }

    public async Task ApplyApiKeyAsync(string apiKey, CancellationToken token = default)
    {
        var key = AyakaModsPlatformAdapter.ValidateApiKey(apiKey);
        await _gate.WaitAsync(token);
        try
        {
            if (credentials != null) await credentials.SaveApiKeyAsync("ayakamods", key, token);
            Platform.SetApiKey(key);
            IsApiKeySaved = credentials != null;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearApiKeyAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (credentials != null) await credentials.DeleteApiKeyAsync("ayakamods", token);
            Platform.ClearApiKey(); IsApiKeySaved = false;
        }
        finally { _gate.Release(); }
    }

    public async Task RestoreApiKeyAsync(CancellationToken token = default)
    {
        if (credentials == null) return;
        await _gate.WaitAsync(token);
        try
        {
            var key = await credentials.ReadApiKeyAsync("ayakamods", token);
            if (key == null) { Platform.ClearApiKey(); IsApiKeySaved = false; return; }
            Platform.SetApiKey(key); IsApiKeySaved = true;
        }
        catch { Platform.ClearApiKey(); IsApiKeySaved = false; throw; }
        finally { _gate.Release(); }
    }
}
