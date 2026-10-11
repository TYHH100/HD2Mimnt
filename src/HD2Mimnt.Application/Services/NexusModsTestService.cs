using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Platforms;

namespace HD2Mimnt.Application.Services;

/// <summary>开发工具的会话认证入口，与下载和更新检查共用注册的平台实例。</summary>
public sealed class NexusModsTestService(IPlatformRegistry platforms)
{
    private NexusModsPlatformAdapter Platform =>
        platforms.GetPlatform("nexusmods") as NexusModsPlatformAdapter
        ?? throw new InvalidOperationException("Nexus Mods 平台未注册。");

    public bool IsAuthenticated => Platform.IsAuthenticated;

    public void ApplyApiKey(string apiKey)
    {
        var key = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            throw new ArgumentException("请输入有效的 API 密钥，不能包含换行或控制字符。", nameof(apiKey));

        Platform.SetApiKey(key);
    }

    public void ClearApiKey() => Platform.ClearApiKey();
}
