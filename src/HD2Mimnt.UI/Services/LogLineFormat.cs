using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 日志文本格式的唯一出口：写入端、界面解析、导出共用同一套规则，
/// 避免两边各自维护一份正则而逐渐漂移。
/// 目标行：
/// <code>
/// [2026-11-09 01:00:51.419 +08:00] [INF] plugin:hello        Hello World 已初始化
/// [2026-11-09 01:01:16.283 +08:00] [DBG] ai:deepseek         AI 请求完成 | model=deepseek-chat in=244 cached=0 out=207 total=451 elapsed=1932
/// </code>
/// 约定：行首两对方括号各表一义（时间 / 等级），消息体内部不再重复级别；
/// 结构化字段统一追加在 " | " 之后，形如 k=v，便于 grep 与批量统计。
/// </summary>
public static class LogLineFormat
{
    /// <summary>类别列宽：短名超过该宽度时截断为 16 字符 + ".."，保证消息列仍能对齐。</summary>
    public const int CategoryWidth = 25;

    /// <summary>单文件上限，达到后滚存为 application-yyyyMMdd-HHmmss.log。</summary>
    public const long MaxFileBytes = 10L * 1024 * 1024;

    /// <summary>保留的滚存文件数量，超出后按文件名（含时间戳）删除最旧的。</summary>
    public const int MaxArchivedFiles = 5;

    /// <summary>结构化字段与正文之间的分隔符。</summary>
    public const string FieldSeparator = " | ";

    public static string Timestamp(DateTimeOffset value) => $"[{value:yyyy-MM-dd HH:mm:ss.fff zzz}]";

    /// <summary>固定三字母级别，宽度一致，级别列天然对齐。</summary>
    public static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "INF",
    };

    /// <summary>
    /// 三字母缩写与旧的完整枚举名都要能解析：旧日志文件里存的是 Information / Warning，
    /// 界面按级别着色和过滤依赖这个结果，解析失败会导致 Debug 行被当成 Information。
    /// </summary>
    public static bool TryParseLevel(string? text, out LogLevel level)
    {
        switch ((text ?? string.Empty).Trim().ToUpperInvariant())
        {
            case "TRC" or "TRACE": level = LogLevel.Trace; return true;
            case "DBG" or "DEBUG": level = LogLevel.Debug; return true;
            case "INF" or "INFO" or "INFORMATION": level = LogLevel.Information; return true;
            case "WRN" or "WARN" or "WARNING": level = LogLevel.Warning; return true;
            case "ERR" or "ERROR": level = LogLevel.Error; return true;
            case "CRT" or "FATAL" or "CRITICAL": level = LogLevel.Critical; return true;
            default: level = LogLevel.Information; return false;
        }
    }

    public static LogLevel ParseLevel(string? text) => TryParseLevel(text, out var level) ? level : LogLevel.Information;

    /// <summary>
    /// 完整类别名 → 定宽短名。规则刻意保持简单可预测：
    /// Plugin.&lt;id&gt; → plugin:&lt;id 末段&gt;；AI 命名空间 → ai:&lt;提供商&gt;；
    /// 其余取末段并去掉 Service/Manager/Provider 等无信息量后缀。
    /// </summary>
    public static string FormatCategory(string? category)
    {
        var name = ShortCategory(category);
        return name.Length <= CategoryWidth ? name.PadRight(CategoryWidth) : name[..(CategoryWidth - 2)] + "..";
    }

    public static string ShortCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "app";
        var name = category.Trim();

        if (name.StartsWith("Plugin.", StringComparison.Ordinal))
        {
            var id = name["Plugin.".Length..];
            if (id.Length == 0) return "plugin";
            var last = id.Split('.')[^1];
            return ("plugin:" + (last.Length == 0 ? id : last)).ToLowerInvariant();
        }

        var segments = name.Split('.');
        var leaf = StripSuffix(segments[^1]);
        if (segments.Length > 1 && segments.Any(segment => string.Equals(segment, "AI", StringComparison.OrdinalIgnoreCase)))
        {
            return ("ai:" + leaf).ToLowerInvariant();
        }

        return leaf.ToLowerInvariant();
    }

    public static string BuildLine(
        DateTimeOffset timestamp,
        LogLevel level,
        string? category,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>>? properties = null)
    {
        var builder = new StringBuilder(96 + message.Length);
        builder.Append(Timestamp(timestamp))
            .Append(" [").Append(Abbreviate(level)).Append("] ")
            .Append(FormatCategory(category))
            .Append(' ')
            .Append(message);

        if (properties is { Count: > 0 })
        {
            builder.Append(FieldSeparator);
            for (var index = 0; index < properties.Count; index++)
            {
                if (index > 0) builder.Append(' ');
                builder.Append(properties[index].Key).Append('=').Append(FormatValue(properties[index].Value));
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 从日志状态里取出结构化字段。Microsoft.Extensions.Logging 的消息模板参数
    /// 以 KeyValuePair 序列形式携带，丢掉它们就只能剩下已渲染的文本。
    /// </summary>
    public static List<KeyValuePair<string, object?>> ExtractFields(object? state)
    {
        var fields = new List<KeyValuePair<string, object?>>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            foreach (var pair in pairs)
            {
                // 只保留业务字段，模板原文本身没有查询价值。
                if (pair.Key == "{OriginalFormat}") continue;
                fields.Add(pair);
            }
        }

        return fields;
    }

    /// <summary>
    /// 模板参数已经渲染进正文时不再重复追加：日志主要给人读，
    /// “被调用 46 次 | Total=46”这种重复只增加噪音。
    /// 需要机器可查的字段请用 BeginScope 显式声明，作用域字段始终保留。
    /// </summary>
    public static bool IsRenderedInMessage(KeyValuePair<string, object?> field, string message)
    {
        var text = FormatValue(field.Value);
        return text.Length > 0 && message.Contains(text, StringComparison.Ordinal);
    }

    private static readonly string[] NameSuffixes =
        ["ViewModel", "Repository", "Generator", "Manager", "Provider", "Service", "Broker", "Host"];

    private static string StripSuffix(string name)
    {
        foreach (var suffix in NameSuffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return name[..^suffix.Length];
            }
        }

        return name;
    }

    private static string FormatValue(object? value)
    {
        if (value is null) return "null";
        var text = value switch
        {
            string asText => asText,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        if (text.Length == 0) return "\"\"";
        // 含空格、分隔符或引号时加引号，保证 k=v 可被空格安全切分。
        return text.Any(character => char.IsWhiteSpace(character) || character is '|' or '"')
            ? "\"" + text.Replace("\"", "\\\"") + "\""
            : text;
    }
}
