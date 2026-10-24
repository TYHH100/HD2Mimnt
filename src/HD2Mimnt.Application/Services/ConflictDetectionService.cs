using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Resources;
using HD2Mimnt.Infrastructure.Analysis;
using System.Runtime.InteropServices;

namespace HD2Mimnt.Application.Services;

public class ConflictFileOwner
{
    public Guid ModId { get; init; }
    public string ModName { get; init; } = string.Empty;
    /// <summary>实际部署位置，0-based；已应用部署方向与固定区域规则。</summary>
    public int ModOrder { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string TargetFileName { get; init; } = string.Empty;
    public long Size { get; init; }
    public uint StreamSize { get; init; }
    public uint GpuSize { get; init; }
    public uint? UnitVersion { get; init; }
}

/// <summary>相同 Type ID + Resource ID 被多个启用模组提供。</summary>
public class ConflictGroup
{
    public ulong ResourceId { get; init; }
    public ulong TypeId { get; init; }
    public string RelativePath => $"0x{ResourceId:X16}";
    public string ResourceTypeName => ResourceTypeCatalog.GetDisplayName(TypeId);
    public IReadOnlyList<ConflictFileOwner> Owners { get; init; } = Array.Empty<ConflictFileOwner>();
    public int ModCount => Owners.Select(owner => owner.ModId).Distinct().Count();
    // 保留同一模组的所有补丁供展示，差异判断使用该模组最终提供的资源。
    public bool IsDefiniteConflict => Owners.GroupBy(owner => owner.ModId).Select(group => group.Last())
        .Select(owner => (owner.UnitVersion, owner.Size, owner.StreamSize, owner.GpuSize)).Distinct().Count() > 1;
}

public sealed record ConflictScanWarning(string ModName, string FilePath, string Message);

public class ConflictScanResult
{
    public Guid ProfileId { get; init; }
    public string ProfileName { get; init; } = string.Empty;
    public DateTime ScannedAt { get; init; }
    public int ScannedModCount { get; set; }
    public int SkippedModCount { get; set; }
    public int ScannedPatchCount { get; set; }
    public int CachedPatchCount { get; set; }
    public int ScannedResourceCount { get; set; }
    public List<ConflictGroup> Groups { get; set; } = new();
    public List<ConflictScanWarning> Warnings { get; } = new();
    public int DefiniteConflictCount => Groups.Count(group => group.IsDefiniteConflict);
}

/// <summary>移植旧版资源覆盖检测；按真实部署文件集分析所有资源类型，不按文件名猜测冲突。</summary>
public class ConflictDetectionService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IModRepository _modRepository;
    private readonly SettingsService? _settingsService;
    private readonly PatchResourceIndexCache _indexCache = new();

    private sealed record PatchOwner(Mod Mod, int Order, string Path, string Target);
    private readonly record struct ResourceOccurrence(PatchOwner Source, uint Size, uint Stream, uint Gpu, uint? Version)
    {
        public ConflictFileOwner ToOwner() => new()
        {
            ModId = Source.Mod.Id, ModName = Source.Mod.Name, ModOrder = Source.Order,
            FullPath = Source.Path, TargetFileName = Source.Target,
            Size = Size, StreamSize = Stream, GpuSize = Gpu, UnitVersion = Version
        };
    }
    private struct ResourceOwners
    {
        public ResourceOccurrence First;
        public List<ResourceOccurrence>? Repeated;
    }

    public ConflictDetectionService(IProfileRepository profileRepository, IModRepository modRepository, SettingsService? settingsService = null)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
        _settingsService = settingsService;
    }

    public async Task<ConflictScanResult> ScanProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
            ?? throw new InvalidOperationException($"配置 {profileId} 不存在");
        var result = new ConflictScanResult { ProfileId = profileId, ProfileName = profile.Name, ScannedAt = DateTime.UtcNow };
        var modsById = (await _modRepository.GetByIdsAsync(profile.Mods.Where(item => item.Enabled).Select(item => item.ModId), cancellationToken))
            .ToDictionary(mod => mod.Id);
        foreach (var mod in modsById.Values)
            await BslProfilePolicy.EnsureDetectedAsync(mod, _modRepository, cancellationToken);
        var savedDirection = _settingsService == null ? DeploymentOrder.TopToBottom.ToString()
            : await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString());
        var sequence = BslProfilePolicy.DeploymentSequence(profile, modsById,
            savedDirection == DeploymentOrder.BottomToTop.ToString() ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom,
            _settingsService?.GetSetting(SettingKeys.ProfilesLockLoaders, true) ?? true);

        return await Task.Run(() =>
        {
            var resources = new Dictionary<(ulong Type, ulong Id), ResourceOwners>();
            var nextIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var position = 0; position < sequence.Count; position++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var profileMod = sequence[position];
                if (!modsById.TryGetValue(profileMod.ModId, out var mod) || !Directory.Exists(mod.Path))
                {
                    result.SkippedModCount++;
                    result.Warnings.Add(new(mod?.Name ?? profileMod.ModId.ToString(), mod?.Path ?? "", "模组目录不可访问"));
                    continue;
                }
                List<PatchDeploymentPlanner.FileEntry> files;
                try
                {
                    var selection = ProfileModOptionResolver.ResolveDeployPlan(mod.Manifest,
                        ProfileModOptionResolver.ResolveEffective(mod.Manifest, profileMod.OptionsJson));
                    if (selection.Scope == OptionDeployScope.InvalidSelection)
                        throw new InvalidDataException("模组选项无效");
                    files = PatchDeploymentPlanner.BuildFiles(
                        PatchDeploymentPlanner.CollectSelectedFiles(mod, profileMod.OptionsJson, cancellationToken), nextIndexes, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    result.SkippedModCount++;
                    result.Warnings.Add(new(mod.Name, mod.Path, ex.Message));
                    continue;
                }
                result.ScannedModCount++;
                // BuildFiles 数值排序与部署一致；伴随文件和占位文件不重复扫描。
                foreach (var file in files.Where(file => file.SourcePath != null
                             && PatchDeploymentPlanner.TryParseTarget(file.FileName, out _, out _, out var suffix) && suffix.Length == 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var entries = _indexCache.Read(file.SourcePath!, cancellationToken, out var cached);
                        if (cached) result.CachedPatchCount++;
                        result.ScannedPatchCount++;
                        result.ScannedResourceCount += entries.Count;
                        var source = new PatchOwner(mod, position, file.SourcePath!, file.FileName);
                        foreach (var entry in entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var key = (entry.TypeId, entry.ResourceId);
                            var occurrence = new ResourceOccurrence(source, entry.DataSize, entry.StreamSize, entry.GpuSize, entry.UnitVersion);
                            ref var owners = ref CollectionsMarshal.GetValueRefOrAddDefault(resources, key, out var exists);
                            if (!exists) owners.First = occurrence;
                            else
                            {
                                // 唯一资源不分配 List 或展示对象；仅重复资源保留全部持有者。
                                owners.Repeated ??= [owners.First];
                                owners.Repeated.Add(occurrence);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        result.Warnings.Add(new(mod.Name, file.SourcePath!, ex.Message));
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            result.Groups = resources.Where(pair =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return pair.Value.Repeated is { } repeated
                        && repeated.Any(owner => owner.Source.Mod.Id != pair.Value.First.Source.Mod.Id);
                })
                .OrderBy(pair => pair.Key.Type).ThenBy(pair => pair.Key.Id)
                .Select(pair => new ConflictGroup { TypeId = pair.Key.Type, ResourceId = pair.Key.Id,
                    Owners = pair.Value.Repeated!.Select(owner => owner.ToOwner()).ToArray() }).ToList();
            return result;
        }, cancellationToken);
    }
}
