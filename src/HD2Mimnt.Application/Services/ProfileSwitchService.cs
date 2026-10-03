using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Profile 配置切换服务
/// 根据执行文档第 1246-1273 节（配置切换流程）
/// 实现差异驱动的配置切换，只修改差异部分
/// </summary>
public class ProfileSwitchService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IProfileLogicalModRepository _profileLogicalModRepository;
    private readonly ILogicalModRepository _logicalModRepository;
    private readonly IModRepository _modRepository;
    private readonly UnifiedDeploymentService _unifiedDeploymentService;
    private readonly DiffEngine _diffEngine;
    private readonly ILogger<ProfileSwitchService>? _logger;

    public ProfileSwitchService(
        IProfileRepository profileRepository,
        IProfileLogicalModRepository profileLogicalModRepository,
        ILogicalModRepository logicalModRepository,
        IModRepository modRepository,
        UnifiedDeploymentService unifiedDeploymentService,
        DiffEngine diffEngine,
        ILogger<ProfileSwitchService>? logger = null)
    {
        _profileRepository = profileRepository;
        _profileLogicalModRepository = profileLogicalModRepository;
        _logicalModRepository = logicalModRepository;
        _modRepository = modRepository;
        _unifiedDeploymentService = unifiedDeploymentService;
        _diffEngine = diffEngine;
        _logger = logger;
    }

    /// <summary>
    /// 切换到指定配置文件（差异驱动）
    /// 根据执行文档第 1246-1273 节：
    /// 1. 读取目标 Logical Mod Set
    /// 2. 生成 Desired State
    /// 3. 与 Current State 做 Diff
    /// 4. 仅修改差异
    /// </summary>
    public async Task<ProfileSwitchResult> SwitchToProfileAsync(
        Guid targetProfileId,
        DeploymentMethod method,
        string gameDirectory,
        IProgress<ProfileSwitchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new ProfileSwitchResult
        {
            ProfileId = targetProfileId,
            StartTime = DateTime.UtcNow
        };

        try
        {
            _logger?.LogInformation("开始切换配置文件: {ProfileId}", targetProfileId);

            // 1. 读取目标配置文件
            progress?.Report(new ProfileSwitchProgress
            {
                Stage = "LoadingProfile",
                Message = "读取目标配置文件..."
            });

            var targetProfile = await _profileRepository.GetByIdAsync(targetProfileId, cancellationToken);
            if (targetProfile == null)
            {
                throw new InvalidOperationException($"配置文件不存在: {targetProfileId}");
            }

            result.ProfileName = targetProfile.Name;

            // 2. 读取目标 LogicalMod 和传统 Mod 列表
            progress?.Report(new ProfileSwitchProgress
            {
                Stage = "LoadingMods",
                Message = "读取模组列表..."
            });

            var targetLogicalMods = await _profileLogicalModRepository.GetEnabledByProfileIdAsync(targetProfileId, cancellationToken);
            var targetMods = targetProfile.Mods.Where(m => m.Enabled).OrderBy(m => m.Order).ToList();

            result.TotalMods = targetLogicalMods.Count + targetMods.Count;

            _logger?.LogInformation(
                "目标配置: {LogicalModCount} 个 LogicalMod, {ModCount} 个传统 Mod",
                targetLogicalMods.Count, targetMods.Count);

            // 3. 生成 Desired State
            progress?.Report(new ProfileSwitchProgress
            {
                Stage = "GeneratingDesiredState",
                Message = "生成目标状态..."
            });

            var desiredFiles = new List<DeployableFileMapping>();
            var sessionId = Guid.NewGuid();

            // 3.1 收集 LogicalMod 的可部署文件
            foreach (var profileLogicalMod in targetLogicalMods)
            {
                var logicalMod = await _logicalModRepository.GetByIdAsync(profileLogicalMod.LogicalModId, cancellationToken);
                if (logicalMod == null)
                {
                    _logger?.LogWarning("LogicalMod 不存在: {LogicalModId}", profileLogicalMod.LogicalModId);
                    continue;
                }

                var deployableSource = new LogicalModDeployableSource(logicalMod);
                var files = await deployableSource.GetDeployableFilesAsync(cancellationToken);
                desiredFiles.AddRange(files);
            }

            // 3.2 收集传统 Mod 的可部署文件
            foreach (var profileMod in targetMods)
            {
                var mod = await _modRepository.GetByIdAsync(profileMod.ModId, cancellationToken);
                if (mod == null)
                {
                    _logger?.LogWarning("Mod 不存在: {ModId}", profileMod.ModId);
                    continue;
                }

                var deployableSource = new TraditionalModDeployableSource(mod);
                var files = await deployableSource.GetDeployableFilesAsync(cancellationToken);
                desiredFiles.AddRange(files);
            }

            result.TotalFiles = desiredFiles.Count;

            // 4. 与 Current State 做 Diff
            progress?.Report(new ProfileSwitchProgress
            {
                Stage = "CalculatingDiff",
                Message = "计算差异..."
            });

            var diff = await _diffEngine.CalculateDiffAsync(desiredFiles, sessionId, cancellationToken);

            result.FilesToAdd = diff.FilesToAdd.Count;
            result.FilesToUpdate = diff.FilesToUpdate.Count;
            result.FilesToRemove = diff.FilesToRemove.Count;
            result.FilesUnchanged = diff.FilesUnchanged.Count;

            _logger?.LogInformation(
                "Diff: Add={Add}, Update={Update}, Remove={Remove}, Unchanged={Unchanged}",
                diff.FilesToAdd.Count, diff.FilesToUpdate.Count, diff.FilesToRemove.Count, diff.FilesUnchanged.Count);

            // 5. 检查是否为空切换
            if (_diffEngine.IsEmptyDiff(diff))
            {
                _logger?.LogInformation("配置一致，无需切换");
                result.Message = "配置一致，无需切换";
                result.EndTime = DateTime.UtcNow;
                return result;
            }

            // 6. 仅修改差异
            progress?.Report(new ProfileSwitchProgress
            {
                Stage = "ApplyingChanges",
                Message = "应用差异..."
            });

            // 使用 UnifiedDeploymentService 部署整个期望状态
            // UnifiedDeploymentService 内部已经通过 DiffEngine 处理了差异
            // 这里我们需要批量部署所有启用的模组
            result.DeployedMods = new List<string>();
            int completedMods = 0;

            // 6.1 部署 LogicalMods
            foreach (var profileLogicalMod in targetLogicalMods)
            {
                var logicalMod = await _logicalModRepository.GetByIdAsync(profileLogicalMod.LogicalModId, cancellationToken);
                if (logicalMod == null) continue;

                var deployableSource = new LogicalModDeployableSource(logicalMod);

                var deployResult = await _unifiedDeploymentService.DeployModSourceAsync(
                    deployableSource,
                    method,
                    gameDirectory,
                    sessionId,
                    targetProfileId,
                    new Progress<UnifiedDeploymentProgress>(p =>
                    {
                        progress?.Report(new ProfileSwitchProgress
                        {
                            Stage = "DeployingMod",
                            Message = $"部署 LogicalMod: {logicalMod.Name} - {p.Message}",
                            CurrentMod = logicalMod.Name,
                            CompletedMods = completedMods,
                            TotalMods = result.TotalMods
                        });
                    }),
                    cancellationToken);

                result.DeployedMods.Add(logicalMod.Name);
                completedMods++;
            }

            // 6.2 部署传统 Mods
            foreach (var profileMod in targetMods)
            {
                var mod = await _modRepository.GetByIdAsync(profileMod.ModId, cancellationToken);
                if (mod == null) continue;

                var deployableSource = new TraditionalModDeployableSource(mod);

                var deployResult = await _unifiedDeploymentService.DeployModSourceAsync(
                    deployableSource,
                    method,
                    gameDirectory,
                    sessionId,
                    targetProfileId,
                    new Progress<UnifiedDeploymentProgress>(p =>
                    {
                        progress?.Report(new ProfileSwitchProgress
                        {
                            Stage = "DeployingMod",
                            Message = $"部署 Mod: {mod.Name} - {p.Message}",
                            CurrentMod = mod.Name,
                            CompletedMods = completedMods,
                            TotalMods = result.TotalMods
                        });
                    }),
                    cancellationToken);

                result.DeployedMods.Add(mod.Name);
                completedMods++;
            }

            // 7. 标记配置文件为活动状态
            await _profileRepository.SetActiveProfileAsync(targetProfileId, cancellationToken);

            result.Message = "配置切换完成";
            result.EndTime = DateTime.UtcNow;

            _logger?.LogInformation(
                "配置切换完成: {ProfileName}, 耗时={Duration}ms",
                targetProfile.Name,
                (result.EndTime - result.StartTime).TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "配置切换失败: {ProfileId}", targetProfileId);
            result.Message = $"切换失败: {ex.Message}";
            result.EndTime = DateTime.UtcNow;
            throw;
        }
    }
}

/// <summary>
/// 配置切换结果
/// </summary>
public class ProfileSwitchResult
{
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int TotalMods { get; set; }
    public int TotalFiles { get; set; }
    public int FilesToAdd { get; set; }
    public int FilesToUpdate { get; set; }
    public int FilesToRemove { get; set; }
    public int FilesUnchanged { get; set; }
    public List<string> DeployedMods { get; set; } = new();
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 配置切换进度
/// </summary>
public class ProfileSwitchProgress
{
    public string Stage { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CurrentMod { get; set; } = string.Empty;
    public int CompletedMods { get; set; }
    public int TotalMods { get; set; }
}
