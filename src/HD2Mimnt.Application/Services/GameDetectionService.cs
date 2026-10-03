using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 游戏检测服务
/// 负责检测 Helldivers 2 游戏路径和状态
/// </summary>
public class GameDetectionService
{
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
    /// 自动检测游戏（仅在启动时或用户触发时调用）
    /// </summary>
    public async Task AutoDetectAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("开始自动检测游戏...");

        // 1. 尝试从设置中读取用户指定的路径
        var customPath = await _settingsService.GetSettingAsync<string?>("Game.Path", null);
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _logger?.LogInformation("检查用户设置的路径: {Path}", customPath);
            if (await ValidateGameDirectoryAsync(customPath, cancellationToken))
            {
                await UpdateGameInfoAsync(customPath, GameStatus.Found, cancellationToken);
                return;
            }
        }

        // 2. 检测常见安装位置
        var detectedPath = await DetectFromCommonLocationsAsync(cancellationToken);
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
    /// 验证游戏目录是否有效
    /// </summary>
    public async Task<bool> ValidateGameDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        // 检查关键文件是否存在
        var requiredFiles = new[]
        {
            "helldivers2.exe",
            "Helldivers2.exe",
            "game.exe"
        };

        foreach (var file in requiredFiles)
        {
            var fullPath = Path.Combine(path, file);
            if (File.Exists(fullPath))
            {
                _logger?.LogInformation("找到游戏可执行文件: {File}", file);
                return true;
            }
        }

        // 检查常见的游戏目录结构
        var commonSubdirs = new[] { "data", "bin", "content" };
        var foundCount = commonSubdirs.Count(subdir => Directory.Exists(Path.Combine(path, subdir)));

        if (foundCount >= 2)
        {
            _logger?.LogInformation("检测到游戏目录结构");
            return true;
        }

        return false;
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

    private async Task<string?> DetectFromCommonLocationsAsync(CancellationToken cancellationToken)
    {
        var commonPaths = GetCommonGamePaths();

        foreach (var path in commonPaths)
        {
            if (await ValidateGameDirectoryAsync(path, cancellationToken))
            {
                return path;
            }
        }

        return null;
    }

    private IEnumerable<string> GetCommonGamePaths()
    {
        var paths = new List<string>();

        // Steam 默认路径
        var steamPaths = new[]
        {
            @"C:\Program Files (x86)\Steam\steamapps\common\Helldivers 2",
            @"C:\Program Files\Steam\steamapps\common\Helldivers 2",
            @"D:\Steam\steamapps\common\Helldivers 2",
            @"E:\Steam\steamapps\common\Helldivers 2",
        };
        paths.AddRange(steamPaths);

        // Epic Games 默认路径
        var epicPaths = new[]
        {
            @"C:\Program Files\Epic Games\Helldivers2",
            @"D:\Epic Games\Helldivers2",
        };
        paths.AddRange(epicPaths);

        // 尝试从 Steam 注册表读取
        try
        {
            var steamPath = GetSteamInstallPathFromRegistry();
            if (!string.IsNullOrEmpty(steamPath))
            {
                var steamLibraryPath = Path.Combine(steamPath, "steamapps", "common", "Helldivers 2");
                if (!paths.Contains(steamLibraryPath))
                {
                    paths.Add(steamLibraryPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取 Steam 注册表失败");
        }

        return paths;
    }

    private string? GetSteamInstallPathFromRegistry()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") as string;
        }
        catch
        {
            return null;
        }
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
