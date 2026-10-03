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

    public ProfileService(IProfileRepository profileRepository, IModRepository modRepository)
    {
        _profileRepository = profileRepository;
        _modRepository = modRepository;
    }

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
            IsActive = false
        };

        await _profileRepository.AddAsync(profile, cancellationToken);
        return profile.Id;
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
    }

    public async Task<Profile> DuplicateProfileAsync(Guid id, string newName, CancellationToken cancellationToken = default)
    {
        return await _profileRepository.DuplicateAsync(id, newName, cancellationToken);
    }

    public async Task SetActiveProfileAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _profileRepository.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException($"Profile {id} not found");

        await _profileRepository.SetActiveProfileAsync(id, cancellationToken);
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
    }
}
