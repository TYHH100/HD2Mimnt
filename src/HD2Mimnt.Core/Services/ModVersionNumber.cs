namespace HD2Mimnt.Core.Services;

/// <summary>本地模组使用的数字版本号；压缩包更新按小版本递增。</summary>
public static class ModVersionNumber
{
    public const string Default = "1.0.0";

    /// <summary>主版本不变、次版本加一并清零修订号；无法解析时从 1.0.0 开始。</summary>
    public static string BumpMinor(string? current)
    {
        if (!TryParse(current, out var major, out var minor)) return Default;
        return $"{major}.{minor + 1}.0";
    }

    /// <summary>支持可选 v 前缀与 -/+ 后缀；组件缺失按 0 处理。</summary>
    public static bool TryParse(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        var suffix = text.IndexOfAny(['-', '+', ' ']);
        if (suffix >= 0) text = text[..suffix];
        var parts = text.Split('.');
        if (parts.Length == 0 || !int.TryParse(parts[0], out major) || major < 0) return false;
        if (parts.Length > 1 && (!int.TryParse(parts[1], out minor) || minor < 0)) return false;
        return true;
    }
}
