using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 游戏检测服务
/// 负责检测 Helldivers 2 游戏路径和状态
/// </summary>
public class GameDetectionService
{
    /// <summary>按当前游戏来源启动：官方走 Steam，HellForge 优先用 CLI 直接开始会话。</summary>
    public async Task<GameLaunchKind> LaunchGameAsync(CancellationToken cancellationToken = default)
    {
        if (IsHellForgeMode)
            return await LaunchHellForgeAsync(cancellationToken);

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "steam://rungameid/553850",
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException("无法通过 Steam 启动 Helldivers 2，请确认 Steam 已安装并登录。", ex);
        }
        return GameLaunchKind.Steam;
    }

    public bool IsHellForgeMode => _settingsService.GetGameInstallationMode() == GameInstallationMode.HellForge;

    private async Task<GameLaunchKind> LaunchHellForgeAsync(CancellationToken cancellationToken)
    {
        var configured = _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty);
        if (!GameInstallationPaths.TryResolveHellForgeRoot(configured, out var root))
            throw new InvalidOperationException("请先在设置中选择有效的 HellForge 安装目录。");
        var cli = Path.Combine(root, "app", "cli", "HellForge.Cli.exe");
        if (File.Exists(cli) && await SupportsPlayCommandAsync(cli, cancellationToken))
        {
            var startInfo = new ProcessStartInfo(cli)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("play");
            startInfo.ArgumentList.Add("--install");
            startInfo.ArgumentList.Add(root);
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("无法启动 HellForge CLI。");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0)
            {
                var detail = string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                    ? $"HellForge CLI 退出码 {process.ExitCode}。"
                    : detail);
            }
            _logger?.LogInformation("HellForge 会话启动命令已完成: {Output}", output);
            return GameLaunchKind.HellForgeSession;
        }

        var launcher = Path.Combine(root, "HellForge.exe");
        if (!File.Exists(launcher))
            launcher = Path.Combine(root, "app", "launcher", "HellForge.Launcher.exe");
        if (!File.Exists(launcher))
            throw new InvalidOperationException("当前 HellForge 版本没有可用的 CLI 会话命令，也未找到启动器。");
        using var launcherProcess = Process.Start(new ProcessStartInfo(launcher)
        {
            WorkingDirectory = root,
            UseShellExecute = true
        });
        _logger?.LogInformation("当前 HellForge CLI 不支持 play，已打开启动器: {Launcher}", launcher);
        return GameLaunchKind.HellForgeLauncher;
    }

    private static async Task<bool> SupportsPlayCommandAsync(string cli, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo(cli)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--help");
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await process.WaitForExitAsync(CancellationToken.None); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { await Task.WhenAll(outputTask, errorTask); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
                if (cancellationToken.IsCancellationRequested) throw;
                return false;
            }
            var help = (await outputTask) + (await errorTask);
            return help.Split('\n').Any(line =>
                line.TrimStart().StartsWith("play ", StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private readonly SettingsService _settingsService;
    private readonly ILogger<GameDetectionService>? _logger;

    public GameInfo? CurrentGameInfo { get; private set; }

    public event EventHandler<GameStatusChangedEventArgs>? GameStatusChanged;

    public GameDetectionService(
        SettingsService settingsService,
        ILogger<GameDetectionService>? logger = null)
    {
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <summary>
    /// 自动检测游戏（仅在启动时或用户触发时调用）。
    /// 已配置路径时只校验并应用该路径、不再扫描；scanEvenIfConfigured 为 true（如设置页"自动识别"按钮）才强制重新扫描。
    /// </summary>
    public async Task AutoDetectAsync(CancellationToken cancellationToken = default, bool scanEvenIfConfigured = false)
    {
        _logger?.LogInformation("开始自动检测游戏...");

        // HellForge 是独立的游戏副本，只应用用户配置，不扫描 Steam 库。
        if (IsHellForgeMode)
        {
            _logger?.LogInformation("HellForge 模式：只应用已配置目录");
            await ApplyConfiguredGameAsync(cancellationToken);
            return;
        }

        // 1. 尝试从设置中读取用户指定的路径
        var customPath = await _settingsService.GetSettingAsync<string?>(SettingKeys.GamePath, null);
        if (!string.IsNullOrWhiteSpace(customPath) && !scanEvenIfConfigured)
        {
            _logger?.LogInformation("使用用户设置的路径: {Path}", customPath);
            var configuredValid = await ValidateGameDirectoryAsync(customPath, cancellationToken);
            await UpdateGameInfoAsync(customPath, configuredValid ? GameStatus.Found : GameStatus.Invalid,
                GameInstallationMode.Official, cancellationToken);
            return;
        }

        // 2. 未配置（或显式要求重扫）时检测常见安装位置；文件系统探测放后台线程，避免阻塞 UI
        var detectedPath = await Task.Run(
            async () => await DetectFromCommonLocationsAsync(cancellationToken), cancellationToken);
        if (detectedPath != null)
        {
            _logger?.LogInformation("在常见位置找到游戏: {Path}", detectedPath);
            // 保存检测到的路径
            await _settingsService.SetSettingAsync(SettingKeys.GamePath, detectedPath);
            await UpdateGameInfoAsync(detectedPath, GameStatus.Found, GameInstallationMode.Official, cancellationToken);
            return;
        }

        // 3. 未找到游戏
        _logger?.LogWarning("未找到游戏安装路径");
        await UpdateGameInfoAsync(string.Empty, GameStatus.NotFound, GameInstallationMode.Official, cancellationToken);
    }

    /// <summary>
    /// 验证游戏目录是否有效。
    /// 校验语义移植自旧版管理器：目录名为 Helldivers 2，且同时包含
    /// data、tools、bin 子目录与 bin\helldivers2.exe 主程序。
    /// </summary>
    public async Task<bool> ValidateGameDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var valid = ValidateGameDirectory(path);
        if (!valid)
        {
            _logger?.LogInformation("游戏目录校验未通过: {Path}", path);
        }

        return await Task.FromResult(valid);
    }

    /// <summary>按来源校验游戏目录；HellForge 可接受安装根目录或其 game 子目录。</summary>
    public Task<bool> ValidateGameInstallationAsync(GameInstallationMode mode, string path,
        CancellationToken cancellationToken = default)
    {
        var valid = mode == GameInstallationMode.HellForge
            ? ValidateHellForgeInstallation(path)
            : ValidateGameDirectory(path);
        if (!valid)
        {
            _logger?.LogInformation("{Mode} 游戏目录校验未通过: {Path}", mode, path);
        }

        return Task.FromResult(valid);
    }

    private static bool ValidateGameDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        var directory = new DirectoryInfo(path);
        if (!string.Equals(directory.Name, "Helldivers 2", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return GameInstallationPaths.ValidateGameFiles(path);
    }

    private static bool ValidateHellForgeInstallation(string? path)
        => GameInstallationPaths.TryResolveHellForgeRoot(path, out var root)
           && GameInstallationPaths.ValidateGameFiles(Path.Combine(root, "game"));

    /// <summary>
    /// 获取游戏版本（可选功能）
    /// </summary>
    public async Task<string?> GetGameVersionAsync(string gamePath, CancellationToken cancellationToken = default)
    {
        // TODO: 从游戏文件中读取版本信息
        // 这通常需要解析特定的版本文件或读取 exe 的版本信息
        return await Task.FromResult<string?>("Unknown");
    }

    /// <summary>
    /// 手动设置游戏路径
    /// </summary>
    public async Task SetGamePathAsync(string path, CancellationToken cancellationToken = default)
    {
        if (await ValidateGameDirectoryAsync(path, cancellationToken))
        {
            await _settingsService.SetSettingsAsync(new Dictionary<string, object>
            {
                [SettingKeys.GameMode] = GameInstallationMode.Official.ToString(),
                [SettingKeys.GamePath] = path
            });
            await UpdateGameInfoAsync(path, GameStatus.Found, GameInstallationMode.Official, cancellationToken);
        }
        else
        {
            await UpdateGameInfoAsync(path, GameStatus.Invalid, GameInstallationMode.Official, cancellationToken);
            throw new InvalidOperationException($"Invalid game directory: {path}");
        }
    }

    /// <summary>
    /// 设置页的两种来源目录与自动检测选项一起提交。只校验当前来源，另一个目录仅保留供切换。
    /// </summary>
    public async Task SavePreferencesAsync(GameInstallationMode mode, string officialPath, string hellForgePath,
        bool autoDetect, CancellationToken cancellationToken = default)
    {
        officialPath = (officialPath ?? string.Empty).Trim();
        hellForgePath = (hellForgePath ?? string.Empty).Trim();

        if (mode == GameInstallationMode.HellForge)
        {
            if (!string.IsNullOrEmpty(hellForgePath))
            {
                if (!await ValidateGameInstallationAsync(mode, hellForgePath, cancellationToken))
                {
                    var invalidPath = GameInstallationPaths.ResolveHellForgeGameDirectory(hellForgePath) ?? hellForgePath;
                    await UpdateGameInfoAsync(invalidPath, GameStatus.Invalid, mode, cancellationToken);
                    throw new InvalidOperationException($"Invalid HellForge installation directory: {hellForgePath}");
                }
                if (GameInstallationPaths.TryResolveHellForgeRoot(hellForgePath, out var root))
                    hellForgePath = root;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _settingsService.SetSettingsAsync(new Dictionary<string, object>
            {
                [SettingKeys.GameMode] = mode.ToString(),
                [SettingKeys.GamePath] = officialPath,
                [SettingKeys.HellForgePath] = hellForgePath,
                [SettingKeys.GameAutoDetect] = autoDetect
            });
            var effective = string.IsNullOrEmpty(hellForgePath)
                ? string.Empty
                : GameInstallationPaths.ResolveHellForgeGameDirectory(hellForgePath) ?? string.Empty;
            await UpdateGameInfoAsync(effective,
                string.IsNullOrEmpty(effective) ? GameStatus.NotFound : GameStatus.Found, mode, cancellationToken);
            return;
        }

        if (!string.IsNullOrEmpty(officialPath) && !await ValidateGameDirectoryAsync(officialPath, cancellationToken))
        {
            await UpdateGameInfoAsync(officialPath, GameStatus.Invalid, mode, cancellationToken);
            throw new InvalidOperationException($"Invalid game directory: {officialPath}");
        }
        if (string.IsNullOrEmpty(officialPath) && autoDetect)
            officialPath = await Task.Run(() => DetectFromCommonLocationsAsync(cancellationToken), cancellationToken) ?? string.Empty;
        cancellationToken.ThrowIfCancellationRequested();
        await _settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.GameMode] = mode.ToString(),
            [SettingKeys.GamePath] = officialPath,
            [SettingKeys.HellForgePath] = hellForgePath,
            [SettingKeys.GameAutoDetect] = autoDetect
        });
        await UpdateGameInfoAsync(officialPath,
            string.IsNullOrEmpty(officialPath) ? GameStatus.NotFound : GameStatus.Found, mode, cancellationToken);
    }

    /// <summary>
    /// 只应用已保存的官方游戏路径，不扫描常见安装位置。
    /// </summary>
    public async Task<bool> ApplyConfiguredPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await UpdateGameInfoAsync(string.Empty, GameStatus.NotFound, GameInstallationMode.Official, cancellationToken);
            return false;
        }

        var status = await ValidateGameDirectoryAsync(path, cancellationToken)
            ? GameStatus.Found
            : GameStatus.Invalid;
        await UpdateGameInfoAsync(path, status, GameInstallationMode.Official, cancellationToken);
        return status == GameStatus.Found;
    }

    /// <summary>
    /// 按设置中的当前来源只应用已保存目录，不扫描 Steam 安装。
    /// </summary>
    public async Task<bool> ApplyConfiguredGameAsync(CancellationToken cancellationToken = default)
    {
        if (!IsHellForgeMode)
            return await ApplyConfiguredPathAsync(
                _settingsService.GetSetting(SettingKeys.GamePath, string.Empty), cancellationToken);

        var configured = _settingsService.GetSetting(SettingKeys.HellForgePath, string.Empty);
        if (string.IsNullOrWhiteSpace(configured))
        {
            await UpdateGameInfoAsync(string.Empty, GameStatus.NotFound, GameInstallationMode.HellForge, cancellationToken);
            return false;
        }

        var valid = await ValidateGameInstallationAsync(
            GameInstallationMode.HellForge, configured, cancellationToken);
        var gamePath = GameInstallationPaths.ResolveHellForgeGameDirectory(configured) ?? configured;
        await UpdateGameInfoAsync(gamePath, valid ? GameStatus.Found : GameStatus.Invalid,
            GameInstallationMode.HellForge, cancellationToken);
        return valid;
    }

    private async Task<string?> DetectFromCommonLocationsAsync(CancellationToken cancellationToken)
    {
        foreach (var path in GetCandidateGamePaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ValidateGameDirectory(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// 候选安装路径，按优先级排列：Steam 注册表与库文件夹扫描 → 常见静态路径 → 全盘符兜底。
    /// 检测语义移植自旧版管理器 SettingsPageViewModel.DetectGame。
    /// </summary>
    private IEnumerable<string> GetCandidateGamePaths()
    {
        var candidates = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && !candidates.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(path);
            }
        }

        // 1. Steam 注册表定位安装目录，再扫描所有 Steam 库
        var steamPath = GetSteamInstallPathFromRegistry();
        if (!string.IsNullOrWhiteSpace(steamPath))
        {
            foreach (var library in GetSteamLibraryFolders(steamPath))
            {
                Add(Path.Combine(library, "steamapps", "common", "Helldivers 2"));
            }
        }

        // 2. 常见静态安装路径
        Add(@"C:\Program Files (x86)\Steam\steamapps\common\Helldivers 2");
        Add(@"C:\Program Files\Steam\steamapps\common\Helldivers 2");
        Add(@"C:\Program Files\Epic Games\Helldivers2");

        // 3. 全盘符兜底扫描
        foreach (var drive in Environment.GetLogicalDrives())
        {
            var root = drive.TrimEnd(Path.DirectorySeparatorChar);
            if (string.Equals(root, "C:", StringComparison.OrdinalIgnoreCase))
            {
                Add(@"C:\Program Files (x86)\Steam\steamapps\common\Helldivers 2");
            }
            Add(Path.Combine(root, "Steam", "steamapps", "common", "Helldivers 2"));
            Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "Helldivers 2"));
        }

        return candidates;
    }

    /// <summary>
    /// Steam 安装目录：HKCU SteamPath → HKLM InstallPath → HKLM Wow6432Node InstallPath。
    /// </summary>
    private string? GetSteamInstallPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using (var currentUserKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (currentUserKey?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
                {
                    return steamPath;
                }
            }

            foreach (var subKey in new[] { @"Software\Valve\Steam", @"Software\Wow6432Node\Valve\Steam" })
            {
                using var localMachineKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
                if (localMachineKey?.GetValue("InstallPath") is string installPath && Directory.Exists(installPath))
                {
                    return installPath;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取 Steam 注册表失败");
        }

        return null;
    }

    /// <summary>
    /// 枚举 Steam 库：Steam 根目录本身是库，其余来自 steamapps\libraryfolders.vdf。
    /// </summary>
    private IEnumerable<string> GetSteamLibraryFolders(string steamPath)
    {
        var libraries = new List<string>();
        void AddLibrary(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)
                && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                libraries.Add(path);
            }
        }

        AddLibrary(steamPath);

        try
        {
            var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                var content = File.ReadAllText(vdfPath);
                foreach (Match match in Regex.Matches(content, @"""path""\s*""([^""]+)"""))
                {
                    AddLibrary(match.Groups[1].Value.Replace(@"\\", @"\"));
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "解析 libraryfolders.vdf 失败");
        }

        return libraries;
    }

    private async Task UpdateGameInfoAsync(string path, GameStatus status, GameInstallationMode source,
        CancellationToken cancellationToken)
    {
        var oldInfo = CurrentGameInfo;

        CurrentGameInfo = new GameInfo
        {
            Path = path,
            Status = status,
            Source = source,
            LastChecked = DateTime.UtcNow,
            Version = status == GameStatus.Found
                ? await GetGameVersionAsync(path, cancellationToken)
                : null
        };

        // 触发状态变化事件
        GameStatusChanged?.Invoke(this, new GameStatusChangedEventArgs
        {
            OldStatus = oldInfo?.Status ?? GameStatus.NotFound,
            NewStatus = status,
            GameInfo = CurrentGameInfo
        });

        _logger?.LogInformation("游戏状态更新: {Status}, 路径: {Path}", status, path);
    }
}

/// <summary>
/// 游戏信息
/// </summary>
public class GameInfo
{
    public string Path { get; set; } = string.Empty;
    public string? Version { get; set; }
    public GameStatus Status { get; set; }
    public GameInstallationMode Source { get; set; }
    public DateTime LastChecked { get; set; }
}

/// <summary>
/// 游戏状态
/// </summary>
public enum GameStatus
{
    NotFound,
    Found,
    Invalid,
    Checking
}

/// <summary>
/// 启动结果。旧版 HellForge 没有 CLI play 命令时只能打开启动器。
/// </summary>
public enum GameLaunchKind
{
    Steam,
    HellForgeSession,
    HellForgeLauncher
}

/// <summary>
/// 游戏状态变化事件参数
/// </summary>
public class GameStatusChangedEventArgs : EventArgs
{
    public GameStatus OldStatus { get; set; }
    public GameStatus NewStatus { get; set; }
    public GameInfo GameInfo { get; set; } = null!;
}
