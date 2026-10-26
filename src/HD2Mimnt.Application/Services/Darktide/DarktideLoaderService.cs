using System.Diagnostics;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Infrastructure.Darktide;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services.Darktide;

public sealed class DarktideLoaderService
{
    private readonly SettingsService _settings;
    private readonly DarktideGameLocator _gameLocator;
    private readonly DarktideReleaseClient _releaseClient;
    private readonly DarktideLoaderPackageInspector _packageInspector;
    private readonly DarktideLoaderInstaller _installer;
    private readonly DarktideStoragePaths _storage;
    private readonly ILogger<DarktideLoaderService>? _logger;

    public DarktideLoaderService(SettingsService settings, DarktideGameLocator gameLocator,
        DarktideReleaseClient releaseClient, DarktideLoaderPackageInspector packageInspector,
        DarktideLoaderInstaller installer, DarktideStoragePaths storage,
        ILogger<DarktideLoaderService>? logger = null)
    {
        _settings = settings;
        _gameLocator = gameLocator;
        _releaseClient = releaseClient;
        _packageInspector = packageInspector;
        _installer = installer;
        _storage = storage;
        _logger = logger;
        _storage.EnsureDirectories();
    }

    public DarktideGameState GetGameState()
        => _gameLocator.Locate(_settings.GetSetting(DarktideSettingsKeys.GamePath, ""));

    public string GetRecordedVersionTag()
        => _settings.GetSetting(DarktideSettingsKeys.LoaderLastInstalledTag, "");

    public string GetRecordedInstallTime()
        => _settings.GetSetting(DarktideSettingsKeys.LoaderLastInstalledAt, "");

    public Task<DarktideReleaseInfo> CheckLatestAsync(CancellationToken cancellationToken = default)
        => _releaseClient.GetLatestAsync(cancellationToken);

    public async Task<DarktideLoaderInstallResult> DownloadAndInstallAsync(DarktideReleaseInfo release,
        DarktideReleasePackageType packageType, IProgress<DarktideDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (release.Error != null) throw new InvalidOperationException(release.Error);
        var package = release.Packages.FirstOrDefault(item => item.Type == packageType)
                      ?? throw new InvalidOperationException(packageType == DarktideReleasePackageType.UpdateOnly
                          ? "该版本没有提供仅更新包。" : "该版本没有提供完整包。");
        var game = GetGameState();
        if (game.RootPath == null || game.Status == DarktideGameStatus.InvalidPath)
            throw new InvalidOperationException("请先选择有效的 Darktide 游戏目录。");

        _storage.EnsureDirectories();
        string? downloadedPath = null;
        try
        {
            var downloaded = await _releaseClient.DownloadPackageAsync(package, _storage.Downloads, progress, cancellationToken);
            downloadedPath = downloaded.FilePath;
            var info = _packageInspector.Inspect(downloaded.FilePath);
            if (info.Error != null || info.Type == null)
                throw new InvalidDataException(info.Error ?? "加载器安装包检查失败。");
            if (info.Type != packageType)
                throw new InvalidDataException($"安装包内容被识别为 {info.Type}，与请求的 {packageType} 不一致。");
            var result = await _installer.InstallAsync(game.RootPath, downloaded.FilePath, info,
                _storage.Temp, _storage.LoaderBackups, cancellationToken);
            if (result.Success)
            {
                await _settings.SetSettingsAsync(new Dictionary<string, object>
                {
                    [DarktideSettingsKeys.LoaderLastInstalledTag] = release.Tag,
                    [DarktideSettingsKeys.LoaderLastInstalledAt] = DateTimeOffset.UtcNow.ToString("o")
                });
                _logger?.LogInformation("DML 已更新到 {Tag}，包类型 {Type}", release.Tag, packageType);
            }
            return result;
        }
        finally
        {
            if (downloadedPath != null)
            {
                try { if (File.Exists(downloadedPath)) File.Delete(downloadedPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogWarning(ex, "清理下载的加载器安装包失败: {Path}", downloadedPath);
                }
            }
        }
    }

    public async Task<DarktideLoaderToggleLaunch> RunToggleScriptAsync(string? gameRoot,
        CancellationToken cancellationToken = default)
    {
        var root = gameRoot ?? GetGameState().RootPath;
        if (string.IsNullOrWhiteSpace(root))
            return new DarktideLoaderToggleLaunch(false, null, null, "未设置游戏目录。");
        var script = DarktidePaths.ToggleScript(root);
        if (!File.Exists(script))
            return new DarktideLoaderToggleLaunch(false, null, null, "toggle_darktide_mods.bat 不存在。");
        if (!File.Exists(DarktidePaths.Patcher(root)))
            return new DarktideLoaderToggleLaunch(false, null, null, "tools/dtkit-patch.exe 不存在，无法运行官方切换脚本。");

        Process? process = null;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c call \"{script}\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            _logger?.LogInformation("启动 DML 官方切换脚本: {Script}", script);
            process = Process.Start(startInfo);
            if (process == null) return new DarktideLoaderToggleLaunch(false, null, null, "无法启动切换脚本。");
            var processId = process.Id;
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("切换脚本仍在运行，已停止等待: {ProcessId}", processId);
                return new DarktideLoaderToggleLaunch(true, processId, null, "脚本已启动；应用已停止等待，请查看控制台窗口。");
            }
            var exitCode = process.ExitCode;
            _logger?.LogInformation("DML 切换脚本退出: {ExitCode}", exitCode);
            return new DarktideLoaderToggleLaunch(true, processId, exitCode,
                exitCode == 0 ? null : $"切换脚本以退出码 {exitCode} 结束，请查看控制台和服务端日志。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger?.LogError(ex, "启动 DML 切换脚本失败");
            return new DarktideLoaderToggleLaunch(false, null, null, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }
}
