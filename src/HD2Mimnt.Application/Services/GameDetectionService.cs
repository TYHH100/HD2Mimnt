using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 游戏检测服务
/// 负责检测 Helldivers 2 游戏路径和状态
/// </summary>
public class GameDetectionService
{
    /// <summary>通过 Steam 启动 HD2，保留 Steam 的启动选项和启动流程。</summary>
    public void LaunchGame()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "steam://rungameid/553850",
            UseShellExecute = true
        });
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

        // 1. 尝试从设置中读取用户指定的路径
        var customPath = await _settingsService.GetSettingAsync<string?>("Game.Path", null);
        if (!string.IsNullOrWhiteSpace(customPath) && !scanEvenIfConfigured)
        {
            _logger?.LogInformation("使用用户设置的路径: {Path}", customPath);
            var configuredValid = await ValidateGameDirectoryAsync(customPath, cancellationToken);
            await UpdateGameInfoAsync(customPath, configuredValid ? GameStatus.Found : GameStatus.Invalid, cancellationToken);
            return;
        }

        // 2. 未配置（或显式要求重扫）时检测常见安装位置；文件系统探测放后台线程，避免阻塞 UI
        var detectedPath = await Task.Run(
            async () => await DetectFromCommonLocationsAsync(cancellationToken), cancellationToken);
        if (detectedPath != null)
        {
            _logger?.LogInformation("在常见位置找到游戏: {Path}", detectedPath);
            await UpdateGameInfoAsync(detectedPath, GameStatus.Found, cancellationToken);

            // 保存检测到的路径
            await _settingsService.SetSettingAsync("Game.Path", detectedPath);
            return;
        }

        // 3. 未找到游戏
        _logger?.LogWarning("未找到游戏安装路径");
        await UpdateGameInfoAsync(string.Empty, GameStatus.NotFound, cancellationToken);
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

    private bool ValidateGameDirectory(string? path)
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

        if (!Directory.Exists(Path.Combine(path, "data"))
            || !Directory.Exists(Path.Combine(path, "tools"))
            || !Directory.Exists(Path.Combine(path, "bin")))
        {
            return false;
        }

        return Directory.GetFiles(Path.Combine(path, "bin"), "helldivers2.exe").Any();
    }

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
            await _settingsService.SetSettingAsync("Game.Path", path);
            await UpdateGameInfoAsync(path, GameStatus.Found, cancellationToken);
        }
        else
        {
            await UpdateGameInfoAsync(path, GameStatus.Invalid, cancellationToken);
            throw new InvalidOperationException($"Invalid game directory: {path}");
        }
    }

    /// <summary>
    /// 只应用已保存的游戏路径，不扫描常见安装位置。
    /// </summary>
    public async Task<bool> ApplyConfiguredPathAsync(string? path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            await UpdateGameInfoAsync(string.Empty, GameStatus.NotFound, cancellationToken);
            return false;
        }

        var status = await ValidateGameDirectoryAsync(path, cancellationToken)
            ? GameStatus.Found
            : GameStatus.Invalid;
        await UpdateGameInfoAsync(path, status, cancellationToken);
        return status == GameStatus.Found;
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

    private async Task UpdateGameInfoAsync(string path, GameStatus status, CancellationToken cancellationToken)
    {
        var oldInfo = CurrentGameInfo;

        CurrentGameInfo = new GameInfo
        {
            Path = path,
            Status = status,
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
/// 游戏状态变化事件参数
/// </summary>
public class GameStatusChangedEventArgs : EventArgs
{
    public GameStatus OldStatus { get; set; }
    public GameStatus NewStatus { get; set; }
    public GameInfo GameInfo { get; set; } = null!;
}
