using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Application.Services;

/// <summary>规范顺序为普通模组、依赖加载器模组、BSL/MDL/BML、SmoothBoot；固定区域始终在实际部署末尾。
/// 段位与互斥组来自 LoaderRegistry 配置，按优先级数值通用分段，新增加载器无需改代码。</summary>
public static class BslProfilePolicy
{
    public static BslModRole Role(Mod mod) => BslModDetector.KnownRole(mod.Manifest?.Guid) ?? mod.BslRole ?? BslModRole.None;
    public static bool IsLoader(BslModRole role) => LoaderRegistry.Current.IsMutex(role);
    public static int PriorityRank(BslModRole role) => LoaderRegistry.Current.PriorityRank(role);
    /// <summary>互斥组加载器显示名（配置顺序，如 “BSL / MDL / Boot Mod Loader”）。</summary>
    public static string LoaderNames => LoaderRegistry.Current.LoaderNames;
    /// <summary>末位部署槽位加载器显示名（如 “SmoothBoot”）。</summary>
    public static string SmoothBootNames => LoaderRegistry.Current.SmoothBootNames;

    public static async Task EnsureDetectedAsync(Mod mod, IModRepository repository, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var knownRole = BslModDetector.KnownRole(mod.Manifest?.Guid);
        if (mod.BslRole != null && mod.BslDetectionVersion >= BslModDetector.DetectionVersion
            && (knownRole == null || mod.BslRole == knownRole)) return;
        try
        {
            var role = knownRole ?? await BslModDetector.DetectAsync(mod, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (role == null) return;
            mod.BslRole = role;
            mod.BslDetectionVersion = BslModDetector.DetectionVersion;
            await repository.UpdateAsync(mod, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // 损坏包保持未识别，供下次重试；不阻断其它模组及普通排序。
        }
    }

    public static bool Normalize(Profile profile, IReadOnlyDictionary<Guid, Mod> mods, bool lockLoaders = true)
    {
        var ordered = profile.Mods.OrderBy(item => item.Order)
            .OrderBy(item => lockLoaders && mods.TryGetValue(item.ModId, out var mod) ? PriorityRank(Role(mod)) : 0).ToList();
        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            changed |= ordered[i].Order != i;
            ordered[i].Order = i;
        }
        profile.Mods = ordered;
        return changed;
    }

    /// <summary>用户明确启用一个加载器时切换到它；批量选择多个加载器拒绝写入。</summary>
    public static bool SelectLoader(Profile profile, IReadOnlyDictionary<Guid, Mod> mods, IReadOnlyCollection<Guid> requestedIds)
    {
        var selected = requestedIds.Distinct().Where(id => mods.TryGetValue(id, out var mod) && IsLoader(Role(mod))).ToList();
        if (selected.Count > 1) throw new InvalidOperationException($"{LoaderRegistry.Current.LoaderNames} 为互斥加载器，请只选择一个加载器启用");
        if (selected.Count == 0) return false;
        var changed = false;
        foreach (var item in profile.Mods.Where(item => item.ModId != selected[0] && item.Enabled
                     && mods.TryGetValue(item.ModId, out var mod) && IsLoader(Role(mod))))
        {
            item.Enabled = false;
            changed = true;
        }
        return changed;
    }

    public static List<ProfileMod> DeploymentSequence(Profile profile, IReadOnlyDictionary<Guid, Mod> mods, DeploymentOrder direction, bool lockLoaders = true)
    {
        BslModRole GetRole(ProfileMod item) => mods.TryGetValue(item.ModId, out var mod) ? Role(mod) : BslModRole.None;
        var snapshot = LoaderRegistry.Current;
        var enabled = profile.Mods.Where(item => item.Enabled).OrderBy(item => item.Order).ToList();
        if (!lockLoaders)
            return direction == DeploymentOrder.BottomToTop ? enabled.AsEnumerable().Reverse().ToList() : enabled;
        var normal = enabled.Where(item => GetRole(item) == BslModRole.None);
        if (direction == DeploymentOrder.BottomToTop) normal = normal.Reverse();
        // 依赖 → 各加载器段 → 末位槽位按配置优先级分段；同段保持配置页顺序。
        var rest = enabled.Where(item => GetRole(item) != BslModRole.None)
            .GroupBy(item => snapshot.PriorityRank(GetRole(item)))
            .OrderBy(group => group.Key)
            .SelectMany(group => group);
        return normal.Concat(rest).ToList();
    }
}
