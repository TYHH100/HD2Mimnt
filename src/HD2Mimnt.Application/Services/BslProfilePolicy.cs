using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Application.Services;

/// <summary>规范顺序为普通模组、依赖加载器模组、BSL/MDL/BML、SmoothBoot；固定区域始终在实际部署末尾。</summary>
public static class BslProfilePolicy
{
    public static BslModRole Role(Mod mod) => BslModDetector.KnownRole(mod.Manifest?.Guid) ?? mod.BslRole ?? BslModRole.None;
    public static bool IsLoader(BslModRole role) => role is BslModRole.Loader or BslModRole.MdlLoader or BslModRole.BmlLoader;
    public static int PriorityRank(BslModRole role) => role switch
    {
        BslModRole.Dependency => 1,
        BslModRole.Loader or BslModRole.MdlLoader or BslModRole.BmlLoader => 2,
        BslModRole.SmoothBoot => 3,
        _ => 0
    };

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
        if (selected.Count > 1) throw new InvalidOperationException("BSL / MDL / Boot Mod Loader 三选一，请只选择一个加载器启用");
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
        var enabled = profile.Mods.Where(item => item.Enabled).OrderBy(item => item.Order).ToList();
        if (!lockLoaders)
            return direction == DeploymentOrder.BottomToTop ? enabled.AsEnumerable().Reverse().ToList() : enabled;
        var normal = enabled.Where(item => GetRole(item) == BslModRole.None);
        if (direction == DeploymentOrder.BottomToTop) normal = normal.Reverse();
        return normal.Concat(enabled.Where(item => GetRole(item) == BslModRole.Dependency))
            .Concat(enabled.Where(item => IsLoader(GetRole(item))))
            .Concat(enabled.Where(item => GetRole(item) == BslModRole.SmoothBoot)).ToList();
    }
}
