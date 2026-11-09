using System.Text.Json;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Samples.AiSummarizer;

/// <summary>
/// AI 示例插件：通过宿主代理调用 AI，插件自身不接触任何密钥。
///
/// 同时演示声明式界面贡献：
/// - 一个独立页面（导航项）；
/// - 一个浮窗（功能菜单 / 模组库工具栏入口）；
/// - 一个模组右键菜单命令。
/// 所有界面都由管理器渲染，插件只回传区块数据。
/// </summary>
public sealed class AiSummarizerPlugin : IPlugin
{
    private IPluginContext? _context;

    public PluginManifest Manifest { get; } = BuildManifest();

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        _context = context;
        context.Logger.Info("AI 摘要示例：首次调用命令，宿主已按需启动并完成初始化");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        _context?.Logger.Info("AI 摘要示例已停止");
        return Task.CompletedTask;
    }

    private static PluginManifest BuildManifest()
    {
        var manifest = new PluginManifest
        {
            Id = "com.hd2mimnt.samples.ai-summary",
            Name = "AI 摘要示例",
            Version = "1.2.0",
            Author = "HD2Mimnt",
            Description = "把较长的模组说明压缩成要点，演示 ai.use 代理调用与声明式页面/浮窗。",
            ApiVersion = PluginSdkInfo.Version,
            Runtime = "net10.0",
            PermissionMode = PluginPermissionMode.Sandbox,
            // 只声明需求；安装时不会自动授予，必须在权限对话框中显式勾选
            RequestedPermissions = { "ai.use" },
            Assembly = "AiSummarizer.dll",
            EntryType = typeof(AiSummarizerPlugin).FullName!
        };

        manifest.Commands.Add(new PluginCommandDescriptor
        {
            Name = "summarize",
            DisplayName = "AI 摘要",
            Description = "在模组卡片右键菜单中，对模组说明生成三条以内的要点。",
            RequiredPermission = "ai.use",
            Placement = PluginCommandPlacements.ModContextMenu
        });

        // ── 独立页面 ────────────────────────────────────────────────────
        manifest.Ui.Pages.Add(new PluginPageContribution
        {
            Id = "ai-tools",
            Title = "AI 工具",
            Icon = "Plugin",
            Description = "把任意文本压缩成要点。输入内容后点击“生成摘要”，结果会显示在下方。",
            Blocks =
            {
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Markdown,
                    Text = "这是插件贡献的页面：界面由管理器渲染，插件只发送 **Markdown 与区块数据**。\n\n" +
                           "- 插件运行在独立进程，拿不到 API Key\n" +
                           "- 页面、按钮、输入框都由声明描述\n" +
                           "- 结果支持 Markdown 渲染"
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Notice,
                    Tone = PluginUiTones.Info,
                    Title = "提示",
                    Text = "第一次使用需要在「设置 → 插件」中授予 `ai.use` 权限。"
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Input,
                    Id = "source-text",
                    Title = "待摘要的文本",
                    Input = new PluginUiInput
                    {
                        Id = "text",
                        Placeholder = "粘贴模组说明、更新日志或任意长文本…",
                        Multiline = true,
                        Rows = 6
                    }
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Actions,
                    Actions =
                    {
                        new PluginUiAction
                        {
                            Id = "run-summary",
                            Label = "生成摘要",
                            Icon = "Check",
                            Style = PluginUiActionStyles.Primary,
                            Command = "summarize",
                            RequiredPermission = "ai.use"
                        }
                    }
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Divider
                },
                new PluginUiBlock
                {
                    Id = "result",
                    Type = PluginUiBlockTypes.Markdown,
                    Text = "_结果会显示在这里。_"
                }
            }
        });

        // ── 浮窗 ────────────────────────────────────────────────────────
        manifest.Ui.Dialogs.Add(new PluginDialogContribution
        {
            Id = "quick-summary",
            Title = "快速 AI 摘要",
            Description = "不用离开当前页面，粘贴文本后直接生成要点。",
            Blocks =
            {
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Input,
                    Id = "dialog-input",
                    Title = "文本",
                    Input = new PluginUiInput { Id = "text", Multiline = true, Rows = 5, Placeholder = "粘贴内容…" }
                },
                new PluginUiBlock
                {
                    Type = PluginUiBlockTypes.Actions,
                    Actions =
                    {
                        new PluginUiAction
                        {
                            Id = "dialog-run",
                            Label = "生成摘要",
                            Style = PluginUiActionStyles.Primary,
                            Command = "summarize",
                            RequiredPermission = "ai.use"
                        }
                    }
                },
                new PluginUiBlock { Id = "result", Type = PluginUiBlockTypes.Markdown, Text = "_结果会显示在这里。_" }
            }
        });

        // ── 功能菜单 / 模组库工具栏 ──────────────────────────────────────
        manifest.Ui.Menus.Add(new PluginMenuContribution
        {
            Id = "open-quick-summary",
            Placement = PluginCommandPlacements.MainMenu,
            Label = "快速 AI 摘要…",
            Icon = "Plugin",
            Description = "打开浮窗，对任意文本生成摘要。",
            Dialog = "quick-summary",
            RequiredPermission = "ai.use"
        });

        manifest.Ui.Menus.Add(new PluginMenuContribution
        {
            Id = "toolbar-quick-summary",
            Placement = PluginCommandPlacements.LibraryToolbar,
            Label = "快速 AI 摘要",
            Icon = "Plugin",
            Description = "在模组库直接打开 AI 摘要浮窗。",
            Dialog = "quick-summary",
            RequiredPermission = "ai.use"
        });

        return manifest;
    }
}

public sealed class SummarizeCommand : IPluginCommand
{
    /// <summary>单次送入模型的字符上限，避免把整篇文档塞进请求。</summary>
    private const int MaxInputChars = 6000;

    public PluginCommandDescriptor Descriptor { get; } = new()
    {
        Name = "summarize",
        DisplayName = "AI 摘要",
        Description = "对给定文本生成三条以内的要点。",
        RequiredPermission = "ai.use",
        Placement = PluginCommandPlacements.ModContextMenu
    };

    public async Task<PluginResult> ExecuteAsync(
        PluginCommandContext context,
        CancellationToken cancellationToken = default)
    {
        var fromUi = ReadUiInput(context.Arguments, out var hasUiContext);
        var text = string.IsNullOrWhiteSpace(fromUi) ? ReadInput(context.Arguments) : fromUi;

        if (string.IsNullOrWhiteSpace(text))
        {
            return hasUiContext
                ? PluginResult.Ok(null, PluginUiUpdate.Replace(new PluginUiBlock
                {
                    Id = "result",
                    Type = PluginUiBlockTypes.Notice,
                    Tone = PluginUiTones.Warning,
                    Title = "缺少内容",
                    Text = "请先在输入框中填入需要摘要的文本。"
                }))
                : PluginResult.Fail("没有可摘要的内容：请在模组卡片右键菜单中调用，或提供参数 {\"text\":\"...\"}。");
        }

        // 注意：这里永远拿不到、也不需要 API Key。
        // 密钥由主程序持有，插件只提交提示词并接收文本。
        if (!await context.AI.IsAvailableAsync(cancellationToken))
        {
            return PluginResult.Fail("当前未接通 AI 服务，请先在设置中配置提供方。");
        }

        var input = text.Length > MaxInputChars ? text[..MaxInputChars] : text;

        var result = await context.AI.CompleteAsync(
            prompt: $"请把下面的内容压缩成不超过三条要点，直接输出 Markdown 列表：\n\n{input}",
            options: new PluginAiOptions
            {
                SystemPrompt = "你是 Helldivers 2 模组说明编辑，输出简洁、不编造未提供的信息。",
                MaxOutputTokens = 512
            },
            cancellationToken: cancellationToken);

        if (!result.Success)
        {
            context.Logger.Warning($"AI 调用失败：{result.Error}");
            return PluginResult.Fail(result.Error ?? "AI 调用失败");
        }

        context.Logger.Info($"AI 摘要完成，tokens={result.TokensUsed}");

        // 页面/浮窗调用：回传声明式区块，由管理器替换结果区域
        if (hasUiContext)
        {
            return PluginResult.Ok(null, PluginUiUpdate.Replace(new PluginUiBlock
            {
                Id = "result",
                Type = PluginUiBlockTypes.Markdown,
                Text = result.Content ?? string.Empty
            }));
        }

        // 模组右键菜单调用：结果窗口按 Markdown 渲染
        return PluginResult.Ok(result.Content);
    }

    /// <summary>页面/浮窗按钮会把输入框内容放在 <c>ui.values</c> 中。</summary>
    private static string? ReadUiInput(string? argumentsJson, out bool hasUiContext)
    {
        hasUiContext = false;
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("ui", out var ui) || ui.ValueKind != JsonValueKind.Object) return null;

            hasUiContext = true;
            if (!ui.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return null;
            if (!values.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;

            return text.GetString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 宿主从模组右键菜单调用时会传 <c>{ "text": "...", "mod": { ... } }</c>；
    /// 从其它入口调用时也允许只传 <c>text</c>。
    /// </summary>
    private static string? ReadInput(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (doc.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                return text.GetString();
            }

            // 没有现成文本时，用模组元数据拼一段可摘要的内容
            if (doc.RootElement.TryGetProperty("mod", out var mod)
                && mod.ValueKind == JsonValueKind.Object)
            {
                var name = mod.TryGetProperty("name", out var n) ? n.GetString() : null;
                var description = mod.TryGetProperty("description", out var d) ? d.GetString() : null;
                return string.IsNullOrWhiteSpace(description) ? name : $"{name}：{description}";
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
