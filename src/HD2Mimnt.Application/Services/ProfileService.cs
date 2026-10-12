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

    /// <summary>选项保存的写入闸门：快速连续切换选项时串行化读改写，避免并发 UpdateAsync 互相覆盖。</summary>
    private readonly SemaphoreSlim _optionsWriteGate = new(1, 1);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);

    /// <summary>启动/恢复时保证默认配置存在；激活状态已随每次切换落库，无需退出时再写。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationGate.WaitAsync(cancellationToken);
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
        }
        finally { _initializationGate.Release(); }
    }

    /// <summary>
    /// 配置文件数据变更事件（含分组显示状态）；AffectsConflictResults 区分资源关系与纯显示变更。
    /// 在最终状态写入数据库后触发；UI 订阅方只负责刷新视图，不得在回调中再次持久化。
    /// </summary>
    public event EventHandler<ProfilesChangedEventArgs>? ProfilesChanged;

    public ProfileService(IProfileRepository profileRepository, IModRepository modRepository)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
    }

    private void RaiseProfilesChanged(bool affectsConflictResults = true)
        => ProfilesChanged?.Invoke(this, new ProfilesChangedEventArgs(affectsConflictResults));

    public async Task<Profile?> GetProfileByIdAsync(Guid id, CancellationToken cancellationToken = default)
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
        var changed = enabledIds != null && BslProfilePolicy.SelectLoader(profile, byId, enabledIds);
        return BslProfilePolicy.Normalize(profile, byId) || changed;
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

    public async Task DeleteProfileAsync(Guid id, CancellationToken cancellationToken = default)
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

    public async Task RenameProfileAsync(Guid id, string name, CancellationToken cancellationToken = default)
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

    public async Task<Profile> DuplicateProfileAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        await GetProfileByIdAsync(id, cancellationToken);
        var duplicated = await _profileRepository.DuplicateAsync(id, newName, cancellationToken);
        RaiseProfilesChanged();
        return duplicated;
    }

    public async Task SetActiveProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _profileRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Profile {id} not found");

        await _profileRepository.SetActiveProfileAsync(id, cancellationToken);
        RaiseProfilesChanged();
    }

    public async Task AddModToProfileAsync(Guid profileId, Guid modId, CancellationToken cancellationToken = default)
    {
        await _optionsWriteGate.WaitAsync(cancellationToken);
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
        finally { _optionsWriteGate.Release(); }
    }

    public async Task RemoveModFromProfileAsync(Guid profileId, Guid modId, CancellationToken cancellationToken = default)
        => await RemoveModsFromProfileAsync(profileId, new[] { modId }, cancellationToken);

    public async Task RemoveModsFromProfileAsync(Guid profileId, IReadOnlyCollection<Guid> modIds,
        CancellationToken cancellationToken = default)
    {
        await _optionsWriteGate.WaitAsync(cancellationToken);
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
                    || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None).ToList();
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
        finally { _optionsWriteGate.Release(); }
    }

    public async Task SetModEnabledAsync(Guid profileId, Guid modId, bool enabled, CancellationToken cancellationToken = default)
    {
        await SetModsEnabledAsync(profileId, new[] { modId }, enabled, cancellationToken);
    }

    public async Task SetModsEnabledAsync(
        Guid profileId,
        IReadOnlyCollection<Guid> modIds,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await _optionsWriteGate.WaitAsync(cancellationToken);
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
        finally { _optionsWriteGate.Release(); }
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
        await _optionsWriteGate.WaitAsync(cancellationToken);
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
            _optionsWriteGate.Release();
        }
    }

    public async Task ReorderModsAsync(Guid profileId, List<Guid> modIds, CancellationToken cancellationToken = default)
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

    public async Task ReorderProfileRowsAsync(Guid profileId, IReadOnlyList<Guid> modIds,
        IReadOnlyList<Guid> ordinaryRowIds, CancellationToken cancellationToken = default)
    {
        await _optionsWriteGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var normalIds = profile.Mods.Where(item => !mods.TryGetValue(item.ModId, out var mod)
                || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None).Select(item => item.ModId).ToHashSet();
            var separatorMap = profile.Separators.ToDictionary(item => item.Id);
            if (modIds.Count != profile.Mods.Count || modIds.Distinct().Count() != modIds.Count
                || !modIds.ToHashSet().SetEquals(profile.Mods.Select(item => item.ModId))
                || ordinaryRowIds.Count != normalIds.Count + separatorMap.Count
                || ordinaryRowIds.Distinct().Count() != ordinaryRowIds.Count
                || !ordinaryRowIds.ToHashSet().SetEquals(normalIds.Concat(separatorMap.Keys))
                || !ordinaryRowIds.Where(normalIds.Contains).SequenceEqual(modIds.Where(normalIds.Contains)))
                throw new InvalidOperationException("配置已发生变化，请刷新后重新排序。");

            var modMap = profile.Mods.ToDictionary(item => item.ModId);
            for (var i = 0; i < modIds.Count; i++) modMap[modIds[i]].Order = i;
            BslProfilePolicy.Normalize(profile, mods);
            if (!profile.Mods.Select(item => item.ModId).SequenceEqual(modIds))
                throw new InvalidOperationException("该顺序会破坏 Lua 模组或加载器的固定顺序。");

            var separators = new List<ProfileSeparator>();
            var precedingMods = 0;
            foreach (var id in ordinaryRowIds)
            {
                if (separatorMap.TryGetValue(id, out var separator))
                {
                    separator.Order = precedingMods;
                    separators.Add(separator);
                }
                else precedingMods++;
            }
            profile.Separators = separators;
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
        finally { _optionsWriteGate.Release(); }
    }

    public Task<ProfileSeparator> AddSeparatorAsync(Guid profileId, string name, int order,
        CancellationToken cancellationToken = default)
        => AddSeparatorAsync(profileId, name, order, string.Empty, cancellationToken);

    public async Task<ProfileSeparator> AddSeparatorAsync(Guid profileId, string name, int order, string color,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("分隔符名称不能为空", nameof(name));
        color = NormalizeSeparatorColor(color);
        await _optionsWriteGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            var mods = (await _modRepository.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            var normalCount = profile.Mods.Count(item => !mods.TryGetValue(item.ModId, out var mod)
                || BslProfilePolicy.Role(mod) == HD2Mimnt.Core.Enums.BslModRole.None);
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
        finally { _optionsWriteGate.Release(); }
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
        await _optionsWriteGate.WaitAsync(cancellationToken);
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
        finally { _optionsWriteGate.Release(); }
    }

    public async Task SetSeparatorCollapsedAsync(Guid profileId, Guid separatorId, bool collapsed,
        CancellationToken cancellationToken = default)
    {
        await _optionsWriteGate.WaitAsync(cancellationToken);
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
                separator.IsCollapsed = collapsed;
            }
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged(affectsConflictResults: false);
        }
        finally { _optionsWriteGate.Release(); }
    }

    private static string NormalizeSeparatorColor(string color)
    {
        color = color.Trim();
        if (color.Length == 0) return string.Empty;
        if (color.Length != 7 || color[0] != '#' || !color.Skip(1).All(Uri.IsHexDigit))
            throw new ArgumentException("颜色须为 #RRGGBB 格式", nameof(color));
        return color.ToUpperInvariant();
    }

    public async Task RemoveSeparatorAsync(Guid profileId, Guid separatorId,
        CancellationToken cancellationToken = default)
    {
        await _optionsWriteGate.WaitAsync(cancellationToken);
        try
        {
            var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"Profile {profileId} not found");
            if (profile.Separators.RemoveAll(item => item.Id == separatorId) == 0) return;
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged(affectsConflictResults: false);
        }
        finally { _optionsWriteGate.Release(); }
    }
}
