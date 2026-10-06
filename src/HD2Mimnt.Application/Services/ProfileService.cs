using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

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

    /// <summary>
    /// 配置文件数据变更事件（新建/删除/改名/切换/排序/模组成员与启用状态变化）。
    /// 在最终状态写入数据库后触发；UI 订阅方只负责刷新视图，不得在回调中再次持久化。
    /// </summary>
    public event EventHandler? ProfilesChanged;

    public ProfileService(IProfileRepository profileRepository, IModRepository modRepository)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
    }

    private void RaiseProfilesChanged() => ProfilesChanged?.Invoke(this, EventArgs.Empty);

    public async Task<Profile?> GetProfileByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _profileRepository.GetByIdAsync(id, cancellationToken);
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

        var anchor = profiles
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

        profile.ModifiedAt = DateTime.UtcNow;
        await _profileRepository.UpdateAsync(profile, cancellationToken);
        RaiseProfilesChanged();
    }

    public async Task RemoveModFromProfileAsync(Guid profileId, Guid modId, CancellationToken cancellationToken = default)
    {
        var profile = await _profileRepository.GetByIdAsync(profileId, cancellationToken);
        if (profile == null)
        {
            throw new InvalidOperationException($"Profile {profileId} not found");
        }

        var modToRemove = profile.Mods.FirstOrDefault(m => m.ModId == modId);
        if (modToRemove != null)
        {
            profile.Mods.Remove(modToRemove);
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
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

        if (changed)
        {
            profile.ModifiedAt = DateTime.UtcNow;
            await _profileRepository.UpdateAsync(profile, cancellationToken);
            RaiseProfilesChanged();
        }
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

        for (int i = 0; i < modIds.Count; i++)
        {
            var mod = profile.Mods.FirstOrDefault(m => m.ModId == modIds[i]);
            if (mod != null)
            {
                mod.Order = i;
            }
        }

        profile.ModifiedAt = DateTime.UtcNow;
        await _profileRepository.UpdateAsync(profile, cancellationToken);
        RaiseProfilesChanged();
    }
}
