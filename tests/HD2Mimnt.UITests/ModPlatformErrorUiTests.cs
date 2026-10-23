using System.Net;
using Avalonia.Headless.XUnit;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModPlatformErrors_ShouldPrioritizeUnavailableReasonAndLocalizeGuidance()
    {
        await using var scope = await UiScope.CreateAsync(seedMods: false);
        var localization = scope.Services.GetRequiredService<ILocalizationService>();
        UiLocalization.Current.Attach(localization);
        foreach (var reason in new[] { "Mod not available: 17374", "Not published", "The author of this mod has not published it yet" })
        foreach (var operation in new[] { "获取模组文件列表", "获取会员下载链接", "获取网页授权下载链接" })
        {
            var message = new ModPlatformErrorMessage(
                new PlatformRequestException("Nexus Mods", operation, HttpStatusCode.Forbidden, reason), operation);
            var key = reason.StartsWith("Mod not available") ? "Browse.ModUnavailable" : "Browse.ModNotPublished";
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            {
                await localization.SetLanguageAsync(language);
                var text = message.Localize();
                Assert.Contains("403", text);
                Assert.Contains(localization[key], text);
                Assert.Contains(reason, text);
                Assert.DoesNotContain("Premium", text.Split('\n')[1]);
            }
        }

        await localization.SetLanguageAsync("zh-CN");
        foreach (var reason in new string?[] { null, "This mod is not accessible to this account." })
        {
            var text = new ModPlatformErrorMessage(new PlatformRequestException("Nexus Mods",
                "获取模组文件列表", HttpStatusCode.Forbidden, reason), "获取模组文件列表").Localize();
            Assert.Contains("N 网拒绝访问此信息", text);
            Assert.DoesNotContain("正在审核", text);
        }
    }
}
