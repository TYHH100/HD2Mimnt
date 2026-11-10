using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>应用自更新的发布仓库与安装包命名约定。</summary>
public static class AppUpdateConstants
{
    public const string Repository = "TYHH100/HD2Mimnt";
    // 发布产物按项目名输出为 HD2Mimnt.UI.exe（单文件发布不重写 AssemblyName）。
    public const string MainExecutableName = "HD2Mimnt.UI.exe";
    public const string AssetNameTemplate = "HD2Mimnt-{0}-win-x64.zip";
    public const string ReleasePageUrl = "https://github.com/TYHH100/HD2Mimnt/releases/latest";
}

/// <summary>
/// 应用自更新编排：检查 GitHub Release 新版本，把安装包下载解包到暂存目录并生成
/// PowerShell 安装脚本；脚本在应用退出后覆盖安装目录并重启。安装目录不可写时提前失败。
/// </summary>
public sealed class AppUpdateService(
    IAppUpdateReleaseClient releaseClient,
    IHttpDownloadService downloader,
    StorageSettingsService? storageSettings = null,
    string? stagingRootOverride = null,
    int? currentProcessId = null,
    string? installDirectoryOverride = null,
    ILogger<AppUpdateService>? logger = null) : IAppUpdateService
{
    private static readonly string InstallerScriptFileName = "apply-update.ps1";
    private static readonly string InstallerArchiveFileName = "update.zip";
    private static readonly string StagedDirectoryName = "staged";

    private readonly IAppUpdateReleaseClient _releaseClient = releaseClient;
    private readonly IHttpDownloadService _downloader = downloader;
    private readonly StorageSettingsService? _storageSettings = storageSettings;
    private readonly string? _stagingRootOverride = stagingRootOverride;
    private readonly int? _currentProcessId = currentProcessId ?? Environment.ProcessId;
    private readonly string? _installDirectoryOverride = installDirectoryOverride;

    public async Task<AppUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var release = await _releaseClient.GetLatestReleaseAsync(cancellationToken)
            ?? throw new InvalidOperationException("GitHub 未返回可用的最新版本信息。");
        var candidateVersion = AppUpdateVersion.TryParse(release.TagName, out var parsed)
            ? AppUpdateVersion.Format(parsed)
            : release.TagName.TrimStart('V', 'v');
        var currentVersion = AppInfo.Version;

        var asset = FindInstallerAsset(release, candidateVersion);
        var info = new AppUpdateInfo(candidateVersion, release.Body, string.IsNullOrEmpty(release.HtmlUrl)
            ? AppUpdateConstants.ReleasePageUrl : release.HtmlUrl, asset?.DownloadUrl, asset?.Name, release.PublishedAt);
        return new AppUpdateCheckResult(AppUpdateVersion.IsNewer(currentVersion, candidateVersion), currentVersion, info);
    }

    public async Task<AppUpdateStaging> PrepareAsync(AppUpdateInfo update, IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(update.AssetUrl))
            throw new InvalidOperationException("该版本没有找到可自动安装的 win-x64 安装包，请手动下载。");

        var installDirectory = _installDirectoryOverride ?? AppContext.BaseDirectory;
        EnsureWritable(installDirectory);

        var stagingRoot = _stagingRootOverride
            ?? Path.Combine(_storageSettings?.Current.TempPath ?? Path.GetTempPath(), "AppUpdates");
        Directory.CreateDirectory(stagingRoot);
        CleanupStaleStaging(stagingRoot, update.Version);

        var versionDirectory = Path.Combine(stagingRoot, update.Version);
        Directory.CreateDirectory(versionDirectory);
        var archivePath = Path.Combine(versionDirectory, InstallerArchiveFileName);
        var stagedDirectory = Path.Combine(versionDirectory, StagedDirectoryName);

        try
        {
            await _downloader.DownloadToFileAsync(new Uri(update.AssetUrl), archivePath,
                new DownloadOptions { EnableResume = true }, progress, cancellationToken, resumeKey: $"app-update-{update.Version}");
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(stagedDirectory)) Directory.Delete(stagedDirectory, recursive: true);
            ZipFile.ExtractToDirectory(archivePath, stagedDirectory, overwriteFiles: true);
            if (!File.Exists(Path.Combine(stagedDirectory, AppUpdateConstants.MainExecutableName)))
            {
                throw new InvalidDataException("更新包中缺少主程序，已放弃安装。");
            }

            var scriptPath = WriteInstallerScript(versionDirectory, installDirectory, stagedDirectory);
            return new AppUpdateStaging(update.Version, installDirectory, stagedDirectory, scriptPath);
        }
        catch
        {
            TryCleanup(versionDirectory);
            throw;
        }
    }

    public void LaunchPendingInstaller(AppUpdateStaging staging)
    {
        if (!File.Exists(staging.InstallerScriptPath))
            throw new FileNotFoundException("安装脚本不存在，请重新下载更新。", staging.InstallerScriptPath);
        logger?.LogInformation("启动应用更新安装脚本：{Script}", staging.InstallerScriptPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{staging.InstallerScriptPath}\"",
            UseShellExecute = true,
            CreateNoWindow = true
        });
    }

    /// <summary>按发布约定匹配 win-x64 安装包；找不到返回 null（回退为仅提示手动下载）。</summary>
    private static AppUpdateReleaseAsset? FindInstallerAsset(AppUpdateRelease release, string version)
    {
        var expected = string.Format(AppUpdateConstants.AssetNameTemplate, version);
        return release.Assets.FirstOrDefault(asset =>
            string.Equals(asset.Name, expected, StringComparison.OrdinalIgnoreCase));
    }

    private static void EnsureWritable(string directory)
    {
        if (!Directory.Exists(directory))
            throw new InvalidOperationException("未找到应用安装目录，无法自动更新。");
        try
        {
            var probe = Path.Combine(directory, $".hd2mimnt-update-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException("应用安装目录不可写（可能需要管理员权限），请手动下载更新。", error);
        }
    }

    /// <summary>清理其它版本残留的暂存目录；当前版本目录由异常路径自行清理。</summary>
    private static void CleanupStaleStaging(string stagingRoot, string currentVersion)
    {
        foreach (var directory in Directory.EnumerateDirectories(stagingRoot))
        {
            if (string.Equals(Path.GetFileName(directory), currentVersion, StringComparison.OrdinalIgnoreCase)) continue;
            TryCleanup(directory);
        }
    }

    private static void TryCleanup(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string WriteInstallerScript(string versionDirectory, string installDirectory, string stagedDirectory)
    {
        var scriptPath = Path.Combine(versionDirectory, InstallerScriptFileName);
        // 脚本正文含 PowerShell 花括号，不能用 string.Format；用占位符替换。
        var script = ScriptTemplate
            .Replace("<WAITING_PID>", Quote(_currentProcessId.Value.ToString()))
            .Replace("<TARGET>", Quote(installDirectory))
            .Replace("<SOURCE>", Quote(stagedDirectory))
            .Replace("<EXE_NAME>", Quote(AppUpdateConstants.MainExecutableName));
        // PowerShell 5.1 对无 BOM 文件按 ANSI 解码；路径可能包含非 ASCII 字符，必须写 UTF-8 BOM。
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return scriptPath;

        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    }

    /// <summary>
    /// 安装脚本：等应用退出 → 带重试覆盖安装目录 → 重启应用 → 清理暂存目录与自身。
    /// 占位符依次为：等待退出的 PID、安装目录、暂存目录、主程序名。
    /// </summary>
    private const string ScriptTemplate = """
        $ErrorActionPreference = 'Stop'
        $waitingPid = <WAITING_PID>
        $target = <TARGET>
        $source = <SOURCE>
        $exeName = <EXE_NAME>

        for ($i = 0; $i -lt 60; $i++) {
            $proc = Get-Process -Id $waitingPid -ErrorAction SilentlyContinue
            if ($null -eq $proc) { break }
            Start-Sleep -Seconds 1
        }

        $copied = $false
        for ($attempt = 1; $attempt -le 5 -and -not $copied; $attempt++) {
            try {
                Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
                $copied = $true
            } catch {
                if ($attempt -ge 5) { throw }
                Start-Sleep -Seconds 2
            }
        }

        Start-Process -FilePath (Join-Path $target $exeName) -WorkingDirectory $target
        Start-Sleep -Seconds 3
        Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;
}
