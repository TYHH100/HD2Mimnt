using System.Diagnostics;

namespace HD2Mimnt.Cli;

/// <summary>
/// 定位并启动管理器主程序。
/// 可执行文件名固定为 "HD2Mimnt.UI.exe"；候选路径只来自同目录、开发布局或本机环境变量，
/// 启动前逐项校验文件名与存在性，不经过 shell、不携带任何参数。
/// </summary>
internal static class UiLauncher
{
    private const string UiExecutableName = "HD2Mimnt.UI.exe";

    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("HD2MIMNT_UI_EXE") is { Length: > 0 } overridePath
            && Path.GetFileName(overridePath).Equals(UiExecutableName, StringComparison.OrdinalIgnoreCase)
            && File.Exists(overridePath)
            && File.GetAttributes(overridePath).HasFlag(FileAttributes.Directory) == false)
            return overridePath;

        var baseDirectory = AppContext.BaseDirectory;
        // 开发布局回退：CLI 与 UI 各有 bin 目录；UI 项目默认带 RuntimeIdentifier，产物在 net10.0/win-x64 下。
        var devRoots = new[]
        {
            Path.Combine(baseDirectory, "..", "..", "..", "..", "HD2Mimnt.UI", "bin", "Debug", "net10.0"),
            Path.Combine(baseDirectory, "..", "..", "..", "..", "HD2Mimnt.UI", "bin", "Release", "net10.0")
        };
        var candidates = devRoots.SelectMany(root => new[]
        {
            Path.GetFullPath(Path.Combine(root, UiExecutableName)),
            Path.GetFullPath(Path.Combine(root, "win-x64", UiExecutableName))
        });
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>启动主程序；路径必须先经 <see cref="Find"/> 校验。</summary>
    public static bool TryStart(string uiExecutablePath)
    {
        if (!Path.GetFileName(uiExecutablePath).Equals(UiExecutableName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!File.Exists(uiExecutablePath)) return false;

        var start = new ProcessStartInfo
        {
            FileName = uiExecutablePath,
            UseShellExecute = false,
            Arguments = string.Empty,
            CreateNoWindow = true
        };
        using var ignored = Process.Start(start);
        return true;
    }
}
