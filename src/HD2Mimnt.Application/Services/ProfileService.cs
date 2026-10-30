using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

public sealed class ProfilesChangedEventArgs(bool affectsConflictResults = true) : EventArgs
{
    public bool AffectsConflictResults { get; } = affectsConflictResults;
}

/// <summary>
/// Profile Service
/// 处理 Profile 的业务逻辑
/// </summary>
public class ProfileService
{
    private readonly IProfileRepository _profileRepository;
    private readonly IModRepository _modRepository;
    private readonly SettingsService? _settingsService;
    private bool LoaderLockEnabled => _settingsService?.GetSetting(SettingKeys.ProfilesLockLoaders, true) ?? true;

    /// <summary>同一配置的成员、选项、排序、元数据和规范化共用读改写闸门。</summary>
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    /// <summary>启动/恢复时保证默认配置存在；激活状态已随每次切换落库，无需退出时再写。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await _profileRepository.GetAllAsync(cancellationToken)).ToList();
            var defaultProfile = profiles.FirstOrDefault(profile => profile.IsDefault);
            if (defaultProfile == null)
            {
                // 升级时沿用旧版本固定在第一位的最早配置，保留其模组与选项。
                defaultProfile = profiles.OrderBy(profile => profile.CreatedAt).ThenBy(profile => profile.Id).FirstOrDefault();
                if (defaultProfile == null)
                {
                    defaultProfile = new Profile
                    {
                        Id = Guid.NewGuid(), Name = "Default Profiles", IsDefault = true, SortOrder = 0,
                        CreatedAt = DateTime.UtcNow, ModifiedAt = DateTime.UtcNow
                    };
                    await _profileRepository.AddAsync(defaultProfile, cancellationToken);
                }
                else
                {
                    defaultProfile.IsDefault = true;
                    defaultProfile.SortOrder = 0;
                    await _profileRepository.UpdateAsync(defaultProfile, cancellationToken);
                }
            }
            if (profiles.All(profile => !profile.IsActive))
                await _profileRepository.SetActiveProfileAsync(defaultProfile.Id, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// 配置文件数据变更事件（含分组显示状态）；AffectsConflictResults 区分资源关系与纯显示变更。
    /// 在最终状态写入数据库后触发；UI 订阅方只负责刷新视图，不得在回调中再次持久化。
    /// </summary>
    public event EventHandler<ProfilesChangedEventArgs>? ProfilesChanged;

    public ProfileService(IProfileRepository profileRepository, IModRepository modRepository, SettingsService? settingsService = null)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
        _settingsService = settingsService;
    }

    private void RaiseProfilesChanged(bool affectsConflictResults = true)
        => ProfilesChanged?.Invoke(this, new ProfilesChangedEventArgs(affectsConflictResults));

    public Task<Profile?> GetProfileByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => _profileRepository.GetByIdAsync(id, cancellationToken);

    /// <summary>显式修正加载器顺序和分组边界；与所有配置写入串行执行。</summary>
    public async Task<Profile?> NormalizeProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try { return await NormalizeProfileCoreAsync(id, cancellationToken); }
        finally { _mutationGate.Release(); }
    }

    private async Task<Profile?> NormalizeProfileCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var profile = await _profileRepository.GetByIdAsync(id, cancellationToken);
        if (profile != null && await NormalizeBslAsync(profile, cancellationToken))
            await _profileRepository.UpdateAsync(profile, cancellationToken);
        return profile;
    }

    private async Task<bool> NormalizeBslAsync(Profile profile, CancellationToken cancellationToken,
        IReadOnlyCollection<Guid>? enabledIds = null)
    {
        var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken)).ToList();
        foreach (var mod in mods) await BslProfilePolicy.EnsureDetectedAsync(mod, _modRepository, cancellationToken);
        var byId = mods.ToDictionary(mod => mod.Id);
        var changed = LoaderLockEnabled && AdjustSeparatorsForLockedLoaders(profile, byId);
        changed |= enabledIds != null && BslProfilePolicy.SelectLoader(profile, byId, enabledIds);
        return BslProfilePolicy.Normalize(profile, byId, LoaderLockEnabled) || changed;
    }

    private static bool AdjustSeparatorsForLockedLoaders(Profile profile, IReadOnlyDictionary<Guid, Mod> mods)
    {
        if (profile.Separators.Count == 0) return false;
        var ordered = profile.Mods.OrderBy(item => item.Order).ToList();
        var ordinaryCounts = new int[ordered.Count + 1];
        for (var index = 0; index < ordered.Count; index++)
            ordinaryCounts[index + 1] = ordinaryCounts[index]
                + (!mods.TryGetValue(ordered[index].ModId, out var mod)
                    || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None ? 1 : 0);

        // 重新锁定加载器时，先按原完整顺序换算边界，再将固定成员移出普通区域。
        var separators = profile.Separators.OrderBy(item => item.Order).ToArray();
        var changed = false;
        for (var index = 0; index < separators.Length; index++)
        {
            var separator = separators[index];
            var start = Math.Clamp(separator.Order, 0, ordered.Count);
            var end = Math.Clamp(index + 1 < separators.Length ? separators[index + 1].Order : ordered.Count,
                start, ordered.Count);
            var count = Math.Clamp(separator.MemberCount ?? end - start, 0, end - start);
            int? memberCount = separator.MemberCount != null || separator.IsCollapsed
                ? ordinaryCounts[start + count] - ordinaryCounts[start] : null;
            changed |= separator.Order != ordinaryCounts[start] || separator.MemberCount != memberCount;
            separator.Order = ordinaryCounts[start];
            separator.MemberCount = memberCount;
        }
        return changed;
    }

    public async Task<IEnumerable<Profile>> GetAllProfilesAsync(CancellationToken cancellationToken = default)
    {
        return await _profileRepository.GetAllAsync(cancellationToken);
    }

    public async Task<Profile?> GetActiveProfileAsync(CancellationToken cancellationToken = default)
    {
        return await _profileRepository.GetActiveProfileAsync(cancellationToken);
    }

    public async Task<Guid> CreateProfileAsync(string name, string description = "", CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = new Profile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = description,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                IsActive = false,
                SortOrder = await GetNextSortOrderAsync(cancellationToken)
            };

            await _profileRepository.AddAsync(profile, cancellationToken);
            RaiseProfilesChanged();
            return profile.Id;

        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>迁移完整配置快照；来源默认配置合并到现有默认配置，重复快照保留用户后续编辑。</summary>
    internal async Task<(Guid Id, bool Reused)> ImportMigratedProfileAsync(Profile profile, CancellationToken cancellationToken,
        Guid? defaultSourceId = null)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            Profile? targetDefault = null;
            if (defaultSourceId.HasValue)
            {
                targetDefault = (await _profileRepository.GetAllAsync(cancellationToken)).FirstOrDefault(item => item.IsDefault)
                    ?? throw new InvalidOperationException("默认配置尚未初始化");
                if (targetDefault.DefaultMigrationState.GetValueOrDefault(defaultSourceId.Value)?.SnapshotIds.Contains(profile.Id) == true)
                    return (targetDefault.Id, true);
            }
            else
            {
                var existing = await _profileRepository.GetByIdAsync(profile.Id, cancellationToken);
                if (existing != null) return (existing.Id, true);
            }
            if (string.IsNullOrWhiteSpace(profile.Name)) throw new ArgumentException("源配置名称不能为空");
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken)).ToDictionary(mod => mod.Id);
            if (profile.Mods.Select(item => item.ModId).Distinct().Count() != profile.Mods.Count)
                throw new InvalidDataException("源配置包含重复模组成员");
            foreach (var item in profile.Mods)
            {
                if (!mods.TryGetValue(item.ModId, out var mod)) throw new InvalidDataException("源配置的模组成员未成功迁入");
                item.ProfileId = profile.Id;
                item.OptionsJson = ProfileModOptionResolver.ResolveEffective(mod.Manifest, item.OptionsJson).ToJson();
            }
            BslProfilePolicy.SelectLoader(profile, mods, profile.Mods.Where(item => item.Enabled).Select(item => item.ModId).ToList());
            BslProfilePolicy.Normalize(profile, mods, LoaderLockEnabled);
            var normalCount = profile.Mods.Count(item => !LoaderLockEnabled || BslProfilePolicy.Role(mods[item.ModId]) == HD2Mimnt.Core.Enums.BslModRole.None);
            foreach (var separator in profile.Separators)
            {
                separator.ProfileId = profile.Id;
                separator.Order = Math.Clamp(separator.Order, 0, normalCount);
                separator.Color = NormalizeSeparatorColor(separator.Color);
                if (separator.MemberCount.HasValue) separator.MemberCount = Math.Clamp(separator.MemberCount.Value, 0, normalCount - separator.Order);
            }
            profile.LuaSeparatorColor = NormalizeSeparatorColor(profile.LuaSeparatorColor);
            if (targetDefault != null)
            {
                await MergeMigratedDefaultAsync(targetDefault, profile, defaultSourceId!.Value, mods, cancellationToken);
                RaiseProfilesChanged();
                return (targetDefault.Id, false);
            }
            var names = (await _profileRepository.GetAllAsync(cancellationToken)).Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = profile.Name.Trim();
            profile.Name = name;
            for (var index = 2; names.Contains(profile.Name); index++) profile.Name = $"{name} ({index})";
            profile.IsActive = false;
            profile.IsDefault = false;
            profile.SortOrder = await GetNextSortOrderAsync(cancellationToken);
            profile.CreatedAt = profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.AddAsync(profile, cancellationToken);
            RaiseProfilesChanged();
            return (profile.Id, false);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task MergeMigratedDefaultAsync(Profile target, Profile incoming, Guid sourceId,
        Dictionary<Guid, Mod> mods, CancellationToken cancellationToken)
    {
        var snapshotId = incoming.Id;
        var previous = target.DefaultMigrationState.GetValueOrDefault(sourceId) ?? new DefaultProfileMigrationState();
        var importedIds = incoming.Mods.Select(item => item.ModId).ToHashSet();
        importedIds.UnionWith(previous.MemberIds);
        foreach (var mod in await _modRepository.GetByIdsAsync(target.Mods.Select(item => item.ModId), cancellationToken))
            mods.TryAdd(mod.Id, mod);
        bool IsNormal(ProfileMod item) => !mods.TryGetValue(item.ModId, out var mod)
            || !LoaderLockEnabled || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None;

        var oldNormal = target.Mods.OrderBy(item => item.Order).Where(IsNormal).ToList();
        var retained = target.Mods.OrderBy(item => item.Order).Where(item => !importedIds.Contains(item.ModId)).ToList();
        var offset = retained.Count(IsNormal);
        var oldSeparators = target.Separators.OrderBy(item => item.Order).ToList();
        target.Separators = new();
        for (var index = 0; index < oldSeparators.Count; index++)
        {
            var separator = oldSeparators[index];
            if (previous.SeparatorIds.Contains(separator.Id)) continue;
            var start = Math.Clamp(separator.Order, 0, oldNormal.Count);
            var end = Math.Clamp(separator.MemberCount.HasValue ? start + separator.MemberCount.Value
                : index + 1 < oldSeparators.Count ? oldSeparators[index + 1].Order : oldNormal.Count, start, oldNormal.Count);
            separator.Order = oldNormal.Take(start).Count(item => !importedIds.Contains(item.ModId));
            separator.MemberCount = oldNormal.Skip(start).Take(end - start).Count(item => !importedIds.Contains(item.ModId));
            target.Separators.Add(separator);
        }
        foreach (var separator in incoming.Separators)
        {
            separator.ProfileId = target.Id;
            separator.Order += offset;
            target.Separators.Add(separator);
        }
        target.Mods = retained.Concat(incoming.Mods).ToList();
        for (var index = 0; index < target.Mods.Count; index++)
        {
            target.Mods[index].ProfileId = target.Id;
            target.Mods[index].Order = index;
        }
        BslProfilePolicy.SelectLoader(target, mods, incoming.Mods.Where(item => item.Enabled).Select(item => item.ModId).ToList());
        BslProfilePolicy.Normalize(target, mods, LoaderLockEnabled);
        if (incoming.Mods.Any(item => !IsNormal(item)) || incoming.IsLuaGroupCollapsed || incoming.LuaSeparatorColor.Length > 0)
        {
            target.IsLuaGroupCollapsed = incoming.IsLuaGroupCollapsed;
            target.LuaSeparatorColor = incoming.LuaSeparatorColor;
        }
        previous.SnapshotIds.Add(snapshotId);
        previous.SeparatorIds = incoming.Separators.Select(item => item.Id).ToList();
        previous.MemberIds = incoming.Mods.Select(item => item.ModId).ToList();
        target.DefaultMigrationState[sourceId] = previous;
        target.ModifiedAt = DateTime.UtcNow;
        await _profileRepository.UpdateAsync(target, cancellationToken);
    }

    /// <summary>下一个可用排序号（新配置文件依次追加到列表末尾，默认配置在最前）。</summary>
    private async Task<int> GetNextSortOrderAsync(CancellationToken cancellationToken)
    {
        var profiles = await _profileRepository.GetAllAsync(cancellationToken);
        return profiles.Any() ? profiles.Max(p => p.SortOrder) + 1 : 0;
    }

    /// <summary>
    /// 按给定顺序批量重排配置文件。
    /// 最早创建的（默认）配置文件始终固定在第一位，无法被移动或越过。
    /// </summary>
    public async Task ReorderProfilesAsync(IReadOnlyList<Guid> orderedProfileIds, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await _profileRepository.GetAllAsync(cancellationToken)).ToList();
            if (profiles.Count == 0) return;

            var anchor = profiles.FirstOrDefault(profile => profile.IsDefault) ?? profiles
                .OrderBy(p => p.CreatedAt)
                .ThenBy(p => p.Id)
                .First();

            var byId = profiles.ToDictionary(p => p.Id);
            var finalOrder = new List<Guid> { anchor.Id };
            foreach (var id in orderedProfileIds)
            {
                if (id != anchor.Id && byId.ContainsKey(id) && !finalOrder.Contains(id))
                {
                    finalOrder.Add(id);
                }
            }
            // 传入顺序未覆盖的配置文件（理论不存在，兜底）按当前顺序追加
            finalOrder.AddRange(profiles
                .OrderBy(p => p.SortOrder)
                .Select(p => p.Id)
                .Where(id => !finalOrder.Contains(id)));

            for (var index = 0; index < finalOrder.Count; index++)
            {
                var profile = byId[finalOrder[index]];
                if (profile.SortOrder == index) continue;
                profile.SortOrder = index;
                profile.ModifiedAt = DateTime.UtcNow;
                await _profileRepository.UpdateAsync(profile, cancellationToken);
            }

            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public async Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {id} not found");
            if (profile.IsDefault) throw new InvalidOperationException("默认配置文件不能删除");
            if (profile.IsActive)
            {
                throw new InvalidOperationException("不能删除当前激活的配置文件，请先切换到其他配置文件");
            }

            await _profileRepository.DeleteAsync(id, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public async Task RenameProfileAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {id} not found");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("配置文件名称不能为空", nameof(name));
            }

            profile.Name = name.Trim();
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public async Task<Profile> DuplicateProfileAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            await NormalizeProfileCoreAsync(id, cancellationToken);
            var duplicated = await _profileRepository.DuplicateAsync(id, newName, cancellationToken);
            RaiseProfilesChanged();
            return duplicated;
        }
        finally { _mutationGate.Release(); }
    }

    public async Task SetActiveProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {id} not found");

            await _profileRepository.SetActiveProfileAsync(id, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public async Task AddModToProfileAsync(Guid profileId, Guid modId, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null)
            {
                throw new InvalidOperationException($"Profile {profileId} not found");
            }

            var mod = await _modRepository.GetByIdAsync(modId, cancellationToken);
            if (mod == null)
            {
                throw new InvalidOperationException($"Mod {modId} not found");
            }

            // 检查是否已存在
            if (profile.Mods.Any(m => m.ModId == modId))
            {
                return;
            }

            var newOrder = profile.Mods.Any() ? profile.Mods.Max(m => m.Order) + 1 : 0;

            profile.Mods.Add(new ProfileMod
            {
                ProfileId = profileId,
                ModId = modId,
                Enabled = true,
                Order = newOrder
            });

            await NormalizeBslAsync(profile, cancellationToken, new[] { modId });
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public async Task RemoveModFromProfileAsync(Guid profileId, Guid modId, CancellationToken cancellationToken = default)
        => await RemoveModsFromProfileAsync(profileId, new[] { modId }, cancellationToken);

    public async Task RemoveModsFromProfileAsync(Guid profileId, IReadOnlyCollection<Guid> modIds,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null)
            {
                throw new InvalidOperationException($"Profile {profileId} not found");
            }

            var ids = modIds.ToHashSet();
            if (profile.Separators.Count > 0)
            {
                var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                    .ToDictionary(mod => mod.Id);
                var normal = profile.Mods.OrderBy(item => item.Order).Where(item => !mods.TryGetValue(item.ModId, out var mod)
                    || !LoaderLockEnabled || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None).ToList();
                foreach (var separator in profile.Separators)
                {
                    if (separator.MemberCount != null || separator.IsCollapsed)
                    {
                        var count = GetSeparatorMemberCount(profile, separator, normal.Count);
                        separator.MemberCount = count - normal.Skip(separator.Order).Take(count).Count(item => ids.Contains(item.ModId));
                    }
                }
                foreach (var separator in profile.Separators)
                    separator.Order -= normal.Take(separator.Order).Count(item => ids.Contains(item.ModId));
            }
            if (profile.Mods.RemoveAll(mod => ids.Contains(mod.ModId)) > 0)
            {
                await NormalizeBslAsync(profile, cancellationToken);
                profile.ModifiedAt = DateTime.UtcNow;
                await _profileRepository.UpdateAsync(profile, cancellationToken);
                RaiseProfilesChanged();
            }
        }
        finally { _mutationGate.Release(); }
    }

    public async Task SetModEnabledAsync(Guid profileId, Guid modId, bool enabled, CancellationToken cancellationToken = default)
    {
        await SetModsEnabledAsync(profileId, new[] { modId }, enabled, cancellationToken);
    }

    /// <summary>把现有普通模组移动到用户分组末尾；只调整分组归属与顺序，不改启用状态。返回实际移动数量。</summary>
    public async Task<int> MoveModsToGroupAsync(Guid profileId, Guid separatorId, IReadOnlyCollection<Guid> modIds,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null) throw new InvalidOperationException($"Profile {profileId} not found");
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var moved = MoveModsToGroupCore(profile, mods, separatorId, modIds);
            if (moved == 0) return 0;
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
            return moved;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>把模组补齐到配置（已存在则启用），并在同一事务内放入指定用户分组末尾。返回实际移动数量。</summary>
    public async Task<int> AddModsToProfileGroupAsync(Guid profileId, IReadOnlyCollection<Guid> modIds, Guid separatorId,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null) throw new InvalidOperationException($"Profile {profileId} not found");
            if (profile.Separators.All(item => item.Id != separatorId))
                throw new InvalidOperationException("目标分组不存在");

            var distinctModIds = modIds.Distinct().ToArray();
            var existingModIds = (await _modRepository.GetByIdsAsync(distinctModIds, cancellationToken))
                .Select(mod => mod.Id).ToHashSet();
            var missingModId = distinctModIds.FirstOrDefault(modId => !existingModIds.Contains(modId));
            if (missingModId != Guid.Empty) throw new InvalidOperationException($"Mod {missingModId} not found");

            var nextOrder = profile.Mods.Any() ? profile.Mods.Max(item => item.Order) + 1 : 0;
            var changed = false;
            foreach (var modId in distinctModIds)
            {
                var profileMod = profile.Mods.FirstOrDefault(item => item.ModId == modId);
                if (profileMod == null)
                {
                    profile.Mods.Add(new ProfileMod
                    {
                        ProfileId = profileId, ModId = modId, Enabled = true, Order = nextOrder++
                    });
                    changed = true;
                }
                else if (!profileMod.Enabled)
                {
                    profileMod.Enabled = true;
                    changed = true;
                }
            }

            changed |= await NormalizeBslAsync(profile, cancellationToken, distinctModIds);
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var moved = MoveModsToGroupCore(profile, mods, separatorId, distinctModIds);
            changed |= moved > 0;
            if (!changed) return 0;
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
            return moved;
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// 按显示行把普通模组拆成“未分组段 + 分组块”，移动后按原块顺序重建；
    /// 分隔符位置按普通模组计数重算，显式 MemberCount 同步为新块成员数，null 保持延伸到下一分隔符。
    /// 固定区域模组不参与用户分组，直接忽略。
    /// </summary>
    private int MoveModsToGroupCore(Profile profile, IReadOnlyDictionary<Guid, Mod> mods, Guid separatorId,
        IReadOnlyCollection<Guid> modIds)
    {
        var separators = profile.Separators.OrderBy(item => item.Order).ToList();
        var separator = separators.FirstOrDefault(item => item.Id == separatorId)
            ?? throw new InvalidOperationException("目标分组不存在");
        bool IsNormal(ProfileMod item) => !mods.TryGetValue(item.ModId, out var mod)
            || !LoaderLockEnabled || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None;
        var normal = profile.Mods.OrderBy(item => item.Order).Where(IsNormal).ToList();
        var movingIds = modIds.Distinct().ToHashSet();
        var moving = normal.Where(item => movingIds.Contains(item.ModId)).ToList();
        if (moving.Count == 0) return 0;

        var blocks = new List<(ProfileSeparator? Separator, List<ProfileMod> Members)>();
        var ungrouped = new List<ProfileMod>();
        void FlushUngrouped()
        {
            if (ungrouped.Count == 0) return;
            blocks.Add((null, new List<ProfileMod>(ungrouped)));
            ungrouped.Clear();
        }

        ProfileSeparator? current = null;
        var remaining = 0;
        foreach (var row in BuildOrdinaryRows(normal, separators))
        {
            if (row is ProfileSeparator next)
            {
                FlushUngrouped();
                current = next;
                remaining = next.MemberCount ?? int.MaxValue;
                blocks.Add((next, new List<ProfileMod>()));
            }
            else if (row is ProfileMod profileMod)
            {
                if (current != null && remaining > 0)
                {
                    blocks[^1].Members.Add(profileMod);
                    if (remaining != int.MaxValue) remaining--;
                }
                else ungrouped.Add(profileMod);
            }
        }
        FlushUngrouped();

        var targetIndex = blocks.FindIndex(block => ReferenceEquals(block.Separator, separator));
        if (targetIndex < 0) throw new InvalidOperationException("目标分组不存在");
        foreach (var block in blocks) block.Members.RemoveAll(item => movingIds.Contains(item.ModId));
        blocks[targetIndex].Members.AddRange(moving);

        var newNormal = new List<ProfileMod>(normal.Count);
        var separatorOrders = new Dictionary<Guid, int>();
        foreach (var block in blocks)
        {
            if (block.Separator == null)
            {
                newNormal.AddRange(block.Members);
                continue;
            }
            separatorOrders[block.Separator.Id] = newNormal.Count;
            if (block.Separator.MemberCount != null)
                block.Separator.MemberCount = block.Members.Count;
            newNormal.AddRange(block.Members);
        }

        var normalIds = normal.Select(item => item.ModId).ToHashSet();
        var rebuilt = newNormal.Concat(profile.Mods.OrderBy(item => item.Order)
            .Where(item => !normalIds.Contains(item.ModId))).ToList();
        for (var index = 0; index < rebuilt.Count; index++)
            rebuilt[index].Order = index;
        profile.Mods = rebuilt;
        foreach (var item in separators)
        {
            if (separatorOrders.TryGetValue(item.Id, out var order)) item.Order = order;
        }
        return moving.Count;
    }

    private static List<object> BuildOrdinaryRows(IReadOnlyList<ProfileMod> normal,
        IReadOnlyList<ProfileSeparator> separators)
    {
        var rows = new List<object>(normal.Count + separators.Count);
        var next = 0;
        for (var index = 0; index <= normal.Count; index++)
        {
            while (next < separators.Count && Math.Clamp(separators[next].Order, 0, normal.Count) <= index)
                rows.Add(separators[next++]);
            if (index < normal.Count) rows.Add(normal[index]);
        }
        return rows;
    }

    public async Task SetModsEnabledAsync(
        Guid profileId,
        IReadOnlyCollection<Guid> modIds,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null)
            {
                throw new InvalidOperationException($"Profile {profileId} not found");
            }

            var distinctModIds = modIds.Distinct().ToArray();
            var nextOrder = profile.Mods.Any() ? profile.Mods.Max(m => m.Order) + 1 : 0;
            var changed = false;

            // Validate all newly added mods in one repository call so bulk selection does not become N+1 queries.
            var missingModIds = enabled
                ? distinctModIds.Where(modId => profile.Mods.All(m => m.ModId != modId)).ToArray()
                : Array.Empty<Guid>();
            if (missingModIds.Length > 0)
            {
                var existingMods = (await _modRepository.GetByIdsAsync(missingModIds, cancellationToken))
                    .Select(mod => mod.Id)
                    .ToHashSet();
                var missingModId = missingModIds.FirstOrDefault(modId => !existingMods.Contains(modId));
                if (missingModId != Guid.Empty)
                {
                    throw new InvalidOperationException($"Mod {missingModId} not found");
                }
            }

            foreach (var modId in distinctModIds)
            {
                var profileMod = profile.Mods.FirstOrDefault(m => m.ModId == modId);
                if (profileMod == null)
                {
                    if (!enabled)
                    {
                        continue;
                    }

                    profile.Mods.Add(new ProfileMod
                    {
                        ProfileId = profileId,
                        ModId = modId,
                        Enabled = true,
                        Order = nextOrder++
                    });
                    changed = true;
                    continue;
                }

                if (profileMod.Enabled != enabled)
                {
                    profileMod.Enabled = enabled;
                    changed = true;
                }
            }

            // 即使成员已经启用，也要修复旧配置的多个加载器同时启用状态。
            changed |= await NormalizeBslAsync(profile, cancellationToken, enabled ? distinctModIds : null);
            if (changed)
            {
                profile.ModifiedAt = DateTime.UtcNow;
                await _profileRepository.UpdateAsync(profile, cancellationToken);
                RaiseProfilesChanged();
            }
        }
        finally { _mutationGate.Release(); }
    }

    /// <summary>
    /// 保存某配置中某模组的选项选择状态（v0 互斥下拉 / v1 独立开关与子选项）。
    /// 一次读取、一次更新，只改 OptionsJson，不动 Enabled 与 Order——
    /// 配置文件里用户手排的部署顺序绝不能因保存选项而改变。
    /// </summary>
    public async Task SetModOptionsAsync(
        Guid profileId,
        Guid modId,
        ProfileModOptionsState state,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");

            var profileMod = profile.Mods.FirstOrDefault(m => m.ModId == modId)
                ?? throw new InvalidOperationException($"Mod {modId} not found in profile {profileId}");

            var optionsJson = state.ToJson();
            if (profileMod.OptionsJson == optionsJson)
            {
                return;
            }

            profileMod.OptionsJson = optionsJson;
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    public async Task ReorderModsAsync(Guid profileId, List<Guid> modIds, CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
            if (profile == null)
            {
                throw new InvalidOperationException($"Profile {profileId} not found");
            }

            var finalIds = modIds.Distinct().Concat(profile.Mods.OrderBy(item => item.Order)
                .Select(item => item.ModId).Where(id => !modIds.Contains(id))).ToList();
            for (int i = 0; i < finalIds.Count; i++)
            {
                var mod = profile.Mods.FirstOrDefault(m => m.ModId == finalIds[i]);
                if (mod != null)
                {
                    mod.Order = i;
                }
            }

            await NormalizeBslAsync(profile, cancellationToken);
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public Task ReorderProfileRowsAsync(Guid profileId, IReadOnlyList<Guid> modIds,
        IReadOnlyList<Guid> ordinaryRowIds, CancellationToken cancellationToken = default)
        => ReorderProfileRowsAsync(profileId, modIds, ordinaryRowIds, null, cancellationToken);

    public async Task ReorderProfileRowsAsync(Guid profileId, IReadOnlyList<Guid> modIds,
        IReadOnlyList<Guid> ordinaryRowIds, IReadOnlyDictionary<Guid, int>? separatorMemberCounts,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var normalIds = profile.Mods.Where(item => !mods.TryGetValue(item.ModId, out var mod)
                || !LoaderLockEnabled || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None).Select(item => item.ModId).ToHashSet();
            var separatorMap = profile.Separators.ToDictionary(item => item.Id);
            if (modIds.Count != profile.Mods.Count || modIds.Distinct().Count() != modIds.Count
                || !modIds.ToHashSet().SetEquals(profile.Mods.Select(item => item.ModId))
                || ordinaryRowIds.Count != normalIds.Count + separatorMap.Count
                || ordinaryRowIds.Distinct().Count() != ordinaryRowIds.Count
                || !ordinaryRowIds.ToHashSet().SetEquals(normalIds.Concat(separatorMap.Keys))
                || !ordinaryRowIds.Where(normalIds.Contains).SequenceEqual(modIds.Where(normalIds.Contains))
                || separatorMemberCounts?.Any(pair => !separatorMap.ContainsKey(pair.Key) || pair.Value < 0) == true)
                throw new InvalidOperationException("配置已发生变化，请刷新后重新排序。");

            var modMap = profile.Mods.ToDictionary(item => item.ModId);
            for (var i = 0; i < modIds.Count; i++) modMap[modIds[i]].Order = i;
            BslProfilePolicy.Normalize(profile, mods, LoaderLockEnabled);
            if (!profile.Mods.Select(item => item.ModId).SequenceEqual(modIds))
                throw new InvalidOperationException("该顺序会破坏 Lua 模组或加载器的固定顺序。");

            var separators = new List<ProfileSeparator>();
            var precedingMods = 0;
            foreach (var id in ordinaryRowIds)
            {
                if (separatorMap.TryGetValue(id, out var separator))
                {
                    separator.Order = precedingMods;
                    if (separatorMemberCounts?.TryGetValue(id, out var count) == true) separator.MemberCount = count;
                    separators.Add(separator);
                }
                else precedingMods++;
            }
            profile.Separators = separators;
            foreach (var separator in separators.Where(item => item.MemberCount != null))
                separator.MemberCount = GetSeparatorMemberCount(profile, separator, normalIds.Count);
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _mutationGate.Release(); }
    }

    public Task<ProfileSeparator> AddSeparatorAsync(Guid profileId, string name, int order,
        CancellationToken cancellationToken = default)
        => AddSeparatorAsync(profileId, name, order, string.Empty, cancellationToken);

    public async Task<ProfileSeparator> AddSeparatorAsync(Guid profileId, string name, int order, string color,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("分隔符名称不能为空", nameof(name));
        color = NormalizeSeparatorColor(color);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var normalCount = profile.Mods.Count(item => !mods.TryGetValue(item.ModId, out var mod)
                || !LoaderLockEnabled || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None);
            var separator = new ProfileSeparator
            {
                Id = Guid.NewGuid(), ProfileId = profileId, Name = name.Trim(),
                Color = color,
                Order = Math.Clamp(order, 0, normalCount)
            };
            profile.Separators.Add(separator);
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged(affectsConflictResults: false);
            return separator;
        }
        finally { _mutationGate.Release(); }
    }

    public Task RenameSeparatorAsync(Guid profileId, Guid separatorId, string name,
        CancellationToken cancellationToken = default)
        => UpdateSeparatorAsync(profileId, separatorId, name, null, cancellationToken);

    /// <summary>同一次写入更新名称与颜色；Guid.Empty 仅更新固定 Lua 分隔符颜色。</summary>
    public async Task UpdateSeparatorAsync(Guid profileId, Guid separatorId, string name, string? color,
        CancellationToken cancellationToken = default)
    {
        if (separatorId != Guid.Empty && string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("分隔符名称不能为空", nameof(name));
        if (color != null) color = NormalizeSeparatorColor(color);
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            if (separatorId == Guid.Empty)
            {
                if (color != null) profile.LuaSeparatorColor = color;
            }
            else
            {
                var separator = profile.Separators.FirstOrDefault(item => item.Id == separatorId)
                    ?? throw new InvalidOperationException("分隔符不存在");
                separator.Name = name.Trim();
                if (color != null) separator.Color = color;
            }
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged(affectsConflictResults: false);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task SetSeparatorCollapsedAsync(Guid profileId, Guid separatorId, bool collapsed,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            if (separatorId == Guid.Empty)
            {
                if (profile.IsLuaGroupCollapsed == collapsed) return;
                profile.IsLuaGroupCollapsed = collapsed;
            }
            else
            {
                var separator = profile.Separators.FirstOrDefault(item => item.Id == separatorId)
                    ?? throw new InvalidOperationException("分隔符不存在");
                if (separator.IsCollapsed == collapsed) return;
                if (separator.MemberCount == null)
                {
                    var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                        .ToDictionary(mod => mod.Id);
                    var normalCount = profile.Mods.Count(item => !mods.TryGetValue(item.ModId, out var mod)
                        || !LoaderLockEnabled || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None);
                    separator.MemberCount = GetSeparatorMemberCount(profile, separator, normalCount);
                }
                separator.IsCollapsed = collapsed;
            }
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged(affectsConflictResults: false);
        }
        finally { _mutationGate.Release(); }
    }

    private static string NormalizeSeparatorColor(string color)
    {
        color = color.Trim();
        if (color.Length == 0) return string.Empty;
        if (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
            throw new ArgumentException("颜色须为 #RRGGBB 格式", nameof(color));
        return color.ToUpperInvariant();
    }

    private static int GetSeparatorMemberCount(Profile profile, ProfileSeparator separator, int normalCount)
    {
        var next = profile.Separators.OrderBy(item => item.Order).SkipWhile(item => item != separator).Skip(1).FirstOrDefault();
        var available = Math.Max(0, Math.Min(next?.Order ?? normalCount, normalCount) - separator.Order);
        return Math.Clamp(separator.MemberCount ?? available, 0, available);
    }

    public async Task RemoveSeparatorAsync(Guid profileId, Guid separatorId,
        CancellationToken cancellationToken = default)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            if (profile.Separators.RemoveAll(item => item.Id == separatorId) == 0) return;
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged(affectsConflictResults: false);
        }
        finally { _mutationGate.Release(); }
    }
}
