using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 冲突文件的持有者（模组）信息
/// </summary>
public class ConflictFileOwner
{
    public Guid ModId { get; init; }
    public string ModName { get; init; } = string.Empty;

    /// <summary>该模组在 Profile 中的部署顺序（Order），值越大部署越晚、实际生效。</summary>
    public int ModOrder { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public long Size { get; init; }
}

/// <summary>
/// 一组文件冲突：同一相对路径被多个启用模组提供
/// </summary>
public class ConflictGroup
{
    public string RelativePath { get; init; } = string.Empty;
    public IReadOnlyList<ConflictFileOwner> Owners { get; init; } = Array.Empty<ConflictFileOwner>();
}

/// <summary>
/// 一次冲突扫描的完整结果
/// </summary>
public class ConflictScanResult
{
    public Guid ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public DateTime ScannedAt { get; init; }
    public int ScannedModCount { get; set; }
    public int SkippedModCount { get; set; }
    public List<ConflictGroup> Groups { get; set; } = new();
}

/// <summary>
/// 冲突检测服务：对指定 Profile 内启用模组做文件路径重叠检测。
/// 相对路径相同即视为冲突（后部署的模组覆盖先部署的），与 Helldivers 2 的补丁覆盖机制一致。
/// </summary>
public class ConflictDetectionService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IModRepository _modRepository;

    public ConflictDetectionService(IProfileRepository profileRepository, IModRepository modRepository)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
    }

    public async Task<ConflictScanResult> ScanProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException($"Profile {profileId} not found");

        var result = new ConflictScanResult
        {
            ProfileId = profileId,
            ProfileName = profile.Name,
            ScannedAt = DateTime.UtcNow
        };

        var enabledMods = profile.Mods.Where(m => m.Enabled).ToList();
        var orderById = enabledMods.ToDictionary(m => m.ModId, m => m.Order);
        var modsById = (await _modRepository.GetByIdsAsync(enabledMods.Select(m => m.ModId), cancellationToken))
            .ToDictionary(mod => mod.Id);

        // 相对路径（不区分大小写）→ 持有该文件的模组列表
        var pathOwners = new Dictionary<string, List<ConflictFileOwner>>(StringComparer.OrdinalIgnoreCase);

        foreach (var profileMod in enabledMods)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!modsById.TryGetValue(profileMod.ModId, out var mod)
                || string.IsNullOrWhiteSpace(mod.Path)
                || !Directory.Exists(mod.Path))
            {
                result.SkippedModCount++;
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(mod.Path, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(mod.Path, file).Replace('\\', '/');
                    if (!pathOwners.TryGetValue(relativePath, out var owners))
                    {
                        owners = new List<ConflictFileOwner>();
                        pathOwners[relativePath] = owners;
                    }

                    long size = 0;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }

                    owners.Add(new ConflictFileOwner
                    {
                        ModId = mod.Id,
                        ModName = mod.Name,
                        ModOrder = profileMod.Order,
                        FullPath = file,
                        Size = size
                    });
                }

                result.ScannedModCount++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.SkippedModCount++;
            }
        }

        result.Groups = pathOwners
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => new ConflictGroup
            {
                RelativePath = kv.Key,
                Owners = kv.Value.OrderBy(owner => owner.ModOrder).ToList()
            })
            .OrderBy(group => group.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }
}
