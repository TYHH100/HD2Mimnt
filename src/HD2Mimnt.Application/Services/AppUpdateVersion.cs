namespace HD2Mimnt.Application.Services;

/// <summary>
/// 应用发布版本的解析与比较（semver 语义：主.次.补[-预发布]）。
/// Release 标签带 V 前缀（如 V0.1.1-alpha），程序集版本不带；预发布版本低于同号正式版。
/// </summary>
public static class AppUpdateVersion
{
    /// <summary>解析 "V0.1.1-alpha"、"0.2.0" 之类版本号；无法解析时返回 false。</summary>
    public static bool TryParse(string? value, out AppUpdateVersionParts parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        if (text is "V" or "v") return false;
        if (text[0] is 'V' or 'v') text = text[1..];

        var metadata = text.IndexOf('+');
        if (metadata >= 0) text = text[..metadata];

        var prerelease = string.Empty;
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit) ||
                !int.TryParse(parts[i], out var number) || number < 0)
            {
                return false;
            }
            numbers[i] = number;
        }

        parsed = new AppUpdateVersionParts(numbers[0], numbers[1], numbers[2], prerelease);
        return true;
    }

    /// <summary>输出规范的 "主.次.补[-预发布]" 形式，用于资产名匹配与展示。</summary>
    public static string Format(in AppUpdateVersionParts parts) => parts.Prerelease.Length == 0
        ? $"{parts.Major}.{parts.Minor}.{parts.Patch}"
        : $"{parts.Major}.{parts.Minor}.{parts.Patch}-{parts.Prerelease}";

    /// <summary>候选版本是否比当前版本新；任一版本无法解析时返回 false。</summary>
    public static bool IsNewer(string? currentVersion, string? candidateVersion)
    {
        if (!TryParse(currentVersion, out var current) || !TryParse(candidateVersion, out var candidate)) return false;
        return Compare(candidate, current) > 0;
    }

    /// <summary>按 semver 规则比较：先比主/次/补，再比预发布（有预发布低于无预发布）。</summary>
    public static int Compare(AppUpdateVersionParts left, AppUpdateVersionParts right)
    {
        var core = (left.Major, left.Minor, left.Patch).CompareTo((right.Major, right.Minor, right.Patch));
        if (core != 0) return core;

        if (left.Prerelease.Length == 0 || right.Prerelease.Length == 0)
        {
            // 无预发布的版本号更高；两者都为空时相等。
            return right.Prerelease.Length.CompareTo(left.Prerelease.Length);
        }
        return ComparePrerelease(left.Prerelease, right.Prerelease);
    }

    private static int ComparePrerelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var i = 0; i < Math.Min(leftParts.Length, rightParts.Length); i++)
        {
            var comparison = ComparePrereleaseIdentifier(leftParts[i], rightParts[i]);
            if (comparison != 0) return comparison;
        }
        return leftParts.Length.CompareTo(rightParts.Length);
    }

    private static int ComparePrereleaseIdentifier(string left, string right)
    {
        var leftNumeric = IsNumeric(left);
        var rightNumeric = IsNumeric(right);
        if (leftNumeric && rightNumeric)
        {
            var leftValue = int.Parse(left);
            var rightValue = int.Parse(right);
            return leftValue.CompareTo(rightValue);
        }
        if (leftNumeric) return -1; // 数字标识符低于字母数字标识符。
        if (rightNumeric) return 1;
        return string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
}

/// <summary>解析后的版本号；<see cref="Prerelease"/> 为空表示正式版。</summary>
public readonly record struct AppUpdateVersionParts(int Major, int Minor, int Patch, string Prerelease);
