using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel
{
    public string AppVersion => AppInfo.Version;

    /// <summary>应用自更新由主窗口共享，设置页只负责展示入口。</summary>
    public AppUpdateViewModel? AppUpdate { get; }

    public bool IsSaveBarVisible => !IsAccountCategoryVisible && !IsAboutCategoryVisible;

    /// <summary>第三方组件与外部工具清单；许可证缺失时明确标注，不冒充开源许可。</summary>
    public IReadOnlyList<ThirdPartyLicense> Licenses { get; } = new[]
    {
        new ThirdPartyLicense("Avalonia", "MIT", "https://github.com/AvaloniaUI/Avalonia"),
        new ThirdPartyLicense("FluentAvalonia", "MIT", "https://github.com/amwx/FluentAvalonia"),
        new ThirdPartyLicense("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new ThirdPartyLicense("Microsoft .NET / Microsoft.Extensions", "MIT", "https://github.com/dotnet/runtime"),
        new ThirdPartyLicense("SharpCompress", "MIT", "https://github.com/adamhathcock/sharpcompress"),
        new ThirdPartyLicense("K4os.Compression.LZ4", "MIT", "https://github.com/MiloszKrajewski/K4os.Compression.LZ4"),
        new ThirdPartyLicense("BCnEncoder.Net", "MIT OR Unlicense", "https://github.com/Nominom/BCnEncoder.NET"),
        new ThirdPartyLicense("MoonSharp", "BSD-3-Clause", "https://github.com/xanathar/moonsharp"),
        new ThirdPartyLicense("NAudio", "MS-PL", "https://github.com/naudio/NAudio"),
        new ThirdPartyLicense("NVorbis", "MIT", "https://github.com/NVorbis/NVorbis"),
        new ThirdPartyLicense("Ww2Ogg.Core", "BSD-3-Clause", "https://github.com/coconutbird/ww2ogg-core"),
        new ThirdPartyLicense("7-Zip", "LGPL-2.1+ / BSD", "https://www.7-zip.org/"),
        new ThirdPartyLicense("Helldivers2ModManager (TYHH100 / teutinsa)", "Apache-2.0",
            "https://github.com/TYHH100/Helldivers2ModManager",
            "https://github.com/teutinsa/Helldivers2ModManager"),
        new ThirdPartyLicense("hd2-repatcher (RaidingForPants)", "MIT", "https://github.com/RaidingForPants/hd2-repatcher"),
        new ThirdPartyLicense("HD2SDK-CommunityEdition", "未附上游许可证", "https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition"),
        new ThirdPartyLicense("潜潜音效修复软件", "未附上游许可证", "https://space.bilibili.com/157681854")
    };

    [RelayCommand]
    private void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        _browserLauncher.Open(url);
    }
}

/// <param name="Url">本项目实际使用的仓库地址。</param>
/// <param name="UpstreamUrl">上游原始仓库地址；仅当本项目是其衍生版本时提供。</param>
public sealed record ThirdPartyLicense(string Name, string License, string Url, string? UpstreamUrl = null);
