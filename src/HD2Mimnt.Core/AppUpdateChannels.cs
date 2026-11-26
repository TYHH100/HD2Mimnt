namespace HD2Mimnt.Core;

/// <summary>应用自更新的发布渠道标识；取值与设置 Updates.Channel 一致。</summary>
public static class AppUpdateChannels
{
    public const string GitHub = "GitHub";

    /// <summary>GitHub Release 的 Codeberg（Forgejo）镜像，供 GitHub 不可达时使用。</summary>
    public const string Codeberg = "Codeberg";

    public static IReadOnlyList<string> All { get; } = [GitHub, Codeberg];

    /// <summary>把设置值规范为已知渠道；无法识别时回落到 GitHub。</summary>
    public static string Normalize(string? value)
        => All.FirstOrDefault(channel => string.Equals(channel, value, StringComparison.OrdinalIgnoreCase))
            ?? GitHub;
}