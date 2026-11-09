using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Samples.LuaHybrid.Tools;

/// <summary>
/// 混合插件里的“底层”一半：只做脚本不方便做的事（哈希、字节统计），
/// 不实现 IPlugin 入口——宿主按命令库加载，由 main.lua 决定何时调用。
///
/// 它仍然跑在隔离的插件进程里，同样拿不到密钥；能看到的只有脚本显式传进来的参数。
/// </summary>
public sealed class HybridDigestCommand : IPluginCommand
{
    public PluginCommandDescriptor Descriptor => new()
    {
        Name = "hybrid-digest",
        DisplayName = "文本指纹",
        Description = "计算 SHA-256 与字节统计，供 Lua 脚本合并进报告。",
        // 该命令不直接出现在管理器菜单里，只由脚本调用
        Placement = PluginCommandPlacements.PluginPage
    };

    public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
    {
        var text = ReadText(context.Arguments);
        if (string.IsNullOrEmpty(text))
        {
            return Task.FromResult(PluginResult.Fail("没有收到要计算的文本"));
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var lines = text.Split('\n').Length;

        context.Logger.Info($"[HybridTools] 计算指纹：{bytes.Length} 字节");

        var ui = PluginUiUpdate.Replace(new PluginUiBlock
        {
            Type = PluginUiBlockTypes.Facts,
            Title = "DLL 计算结果",
            Facts = new List<PluginUiFact>
            {
                new() { Label = "SHA-256", Value = hash },
                new() { Label = "字节数", Value = bytes.Length.ToString() },
                new() { Label = "行数", Value = lines.ToString() },
                new() { Label = "字符数", Value = text.Length.ToString() }
            }
        });

        // 短消息给脚本做提示，完整数据放在声明式界面里
        return Task.FromResult(PluginResult.Ok($"指纹 {hash[..12]}…", ui));
    }

    /// <summary>参数里取 text 字段；取不到就退化为整段参数的原文。</summary>
    private static string ReadText(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // 非 JSON 参数按原文处理
        }

        return argumentsJson;
    }
}
