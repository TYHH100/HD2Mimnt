using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Samples.ThemeShowcase;

/// <summary>
/// 主题示例插件：演示声明式“设计令牌”换肤。
///
/// 插件不提交任何样式代码，只提供颜色 / 圆角 / 字号的字面量覆盖；
/// 管理器按白名单校验后合并到资源字典，还原时整体移除。
/// 因此插件既能显著改变外观，又无法破坏控件语义。
/// </summary>
public sealed class ThemeShowcasePlugin : IPlugin
{
    public PluginManifest Manifest { get; } = BuildManifest();

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        context.Logger.Info("主题示例：首次调用命令，宿主已按需启动并完成初始化（应用主题：设置 → 插件 → 主题按钮）");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    private static PluginManifest BuildManifest()
    {
        var manifest = new PluginManifest
        {
            Id = "com.hd2mimnt.samples.theme",
            Name = "主题示例",
            Version = "1.0.0",
            Author = "HD2Mimnt",
            Description = "演示用设计令牌覆盖配色、圆角与字号：不改代码，也能换一套外观。",
            ApiVersion = PluginSdkInfo.Version,
            Runtime = "net10.0",
            PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = { "ui.theme" },
            Assembly = "ThemeShowcase.dll",
            EntryType = typeof(ThemeShowcasePlugin).FullName!
        };

        manifest.Ui.Pages.Add(new PluginPageContribution
        {
            Id = "theme-guide",
            Title = "主题示例",
            Icon = "Sliders",
            Description = "这是一页完全由声明描述的页面，用来展示主题令牌支持的范围。",
            Blocks =
            {
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Markdown,
                    Text = "## 设计令牌\n\n" +
                           "主题只接受**白名单令牌**，值必须是字面量，插件无法写入样式代码。\n\n" +
                           "在「设置 → 插件」页面点击主题按钮即可应用，随时可以一键恢复默认。"
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Facts,
                    Facts =
                    {
                        new PluginUiFact { Label = "颜色", Value = "accent / surface0-3 / border / textPrimary / textSecondary / success / warning / error" },
                        new PluginUiFact { Label = "圆角", Value = "cardCornerRadius / overlayCornerRadius / notificationCornerRadius" },
                        new PluginUiFact { Label = "字号", Value = "pageTitleFontSize / sectionTitleFontSize / bodyFontSize / captionFontSize" }
                    }
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Notice,
                    Tone = PluginUiTones.Info,
                    Title = "边界",
                    Text = "超出白名单或格式非法的令牌会被忽略，并在应用结果中提示。"
                }
            }
        });

        manifest.Ui.Themes.Add(new PluginThemeContribution
        {
            Id = "midnight",
            Name = "午夜蓝",
            Description = "偏冷的深色配色，适合夜间使用。",
            Tokens = new Dictionary<string, string>
            {
                ["accent"] = "#4C8DFF",
                ["accentStrong"] = "#7AAEFF",
                ["surface0"] = "#0B0F16",
                ["surface1"] = "#121824",
                ["surface2"] = "#1A2230",
                ["surface3"] = "#242E40",
                ["border"] = "#2E3A4C",
                ["textPrimary"] = "#E6EDF7",
                ["textSecondary"] = "#A9B6C8",
                ["textMuted"] = "#74839A",
                ["cardCornerRadius"] = "10",
                ["bodyFontSize"] = "14"
            }
        });

        manifest.Ui.Themes.Add(new PluginThemeContribution
        {
            Id = "amber",
            Name = "琥珀暖光",
            Description = "高对比暖色，突出强调色与卡片层次。",
            Tokens = new Dictionary<string, string>
            {
                ["accent"] = "#FFB020",
                ["accentStrong"] = "#FFC94D",
                ["surface1"] = "#161310",
                ["surface2"] = "#201B15",
                ["textPrimary"] = "#FFF4E3",
                ["textSecondary"] = "#D8C7AE",
                ["cardCornerRadius"] = "14",
                ["overlayCornerRadius"] = "14",
                ["sectionTitleFontSize"] = "20"
            }
        });

        return manifest;
    }
}
