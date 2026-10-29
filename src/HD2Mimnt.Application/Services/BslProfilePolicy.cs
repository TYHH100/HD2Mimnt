using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Application.Services;

/// <summary>规范顺序为普通模组、依赖加载器模组、从属加载器（Chef Shared Loader）、主加载器（BSL/MDL/BML）、SmoothBoot；固定区域始终在实际部署末尾。
/// 段位、互斥组与托管关系来自 LoaderRegistry 配置，按优先级数值通用分段，新增加载器无需改代码。</summary>
public static class BslProfilePolicy
{
    public static BslModRole Role(Mod mod) => BslModDetector.KnownRole(mod.Manifest?.Guid) ?? mod.BslRole ?? BslModRole.None;
    /// <summary>受管理的加载器：主互斥组成员，或由主加载器托管的从属加载器。</summary>
    public static bool IsLoader(BslModRole role) => LoaderRegistry.Current.IsLoader(role);
    public static bool IsPrimaryLoader(BslModRole role) => LoaderRegistry.Current.IsPrimary(role);
    public static bool IsGuestLoader(BslModRole role) => LoaderRegistry.Current.IsGuest(role);
    public static bool CanHost(BslModRole host, BslModRole guest) => LoaderRegistry.Current.CanHost(host, guest);
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

    /// <summary>用户明确启用加载器时切换到所选组合；批量选择只允许一个主加载器，从属加载器必须与其托管者搭配。</summary>
    public static bool SelectLoader(Profile profile, IReadOnlyDictionary<Guid, Mod> mods, IReadOnlyCollection<Guid> requestedIds)
    {
        var snapshot = LoaderRegistry.Current;
        BslModRole RoleOf(Guid id) => mods.TryGetValue(id, out var mod) ? Role(mod) : BslModRole.None;

        var selectedIds = requestedIds.Distinct().Where(id => IsLoader(RoleOf(id))).ToHashSet();
        if (selectedIds.Count == 0) return false;
        var selectedRoles = selectedIds.Select(RoleOf).ToList();
        var duplicate = selectedRoles.GroupBy(role => role).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"{snapshot.DisplayNameOf(duplicate.Key)} 只能启用一份，请停用重复副本");
        var primaries = selectedRoles.Where(IsPrimaryLoader).Distinct().ToList();
        if (primaries.Count > 1)
            throw new InvalidOperationException($"{snapshot.LoaderNames} 为互斥加载器，请只选择一个加载器启用");
        foreach (var guest in selectedRoles.Where(IsGuestLoader))
            if (primaries.Count > 0 && !snapshot.CanHost(primaries[0], guest))
                throw new InvalidOperationException(
                    $"{snapshot.GuestLoaderNames} 只能与 {snapshot.HostLoaderNames} 同时启用，请调整加载器组合");

        // 已启用的加载器中保留与本次选择互相成全的组合：BML 在用时启用 CSL 不关闭 BML，反之亦然。
        var keep = new HashSet<Guid>(selectedIds);
        foreach (var item in profile.Mods.Where(item => item.Enabled && !keep.Contains(item.ModId)))
        {
            var role = RoleOf(item.ModId);
            if (IsGuestLoader(role) && keep.Any(id => snapshot.CanHost(RoleOf(id), role))
                || IsPrimaryLoader(role) && keep.Any(id => IsGuestLoader(RoleOf(id)) && snapshot.CanHost(role, RoleOf(id))))
                keep.Add(item.ModId);
        }

        var changed = false;
        foreach (var item in profile.Mods.Where(item => item.Enabled && IsLoader(RoleOf(item.ModId)) && !keep.Contains(item.ModId)))
        {
            item.Enabled = false;
            changed = true;
        }
        return changed;
    }

    /// <summary>部署前校验加载器组合：主加载器互斥、从属加载器必须有其托管者、同角色只允许一份。</summary>
    public static void ValidateLoaderCombination(IEnumerable<BslModRole> roles)
    {
        var snapshot = LoaderRegistry.Current;
        var loaders = roles.Where(IsLoader).ToList();
        var duplicate = loaders.GroupBy(role => role).FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
            throw new InvalidOperationException($"同一配置只能部署一份 {snapshot.DisplayNameOf(duplicate.Key)}，请停用重复副本");
        var primaries = loaders.Where(IsPrimaryLoader).Distinct().ToList();
        if (primaries.Count > 1)
            throw new InvalidOperationException($"{snapshot.LoaderNames} 互斥，同一配置只能启用一个加载器，请停用另一份或重复副本");
        if (primaries.Count == 1)
        {
            var incompatible = loaders.Where(IsGuestLoader).FirstOrDefault(guest => !snapshot.CanHost(primaries[0], guest));
            if (incompatible != BslModRole.None)
                throw new InvalidOperationException(
                    $"{snapshot.GuestLoaderNames} 只能与 {snapshot.HostLoaderNames} 同时部署，请调整加载器组合");
        }
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
