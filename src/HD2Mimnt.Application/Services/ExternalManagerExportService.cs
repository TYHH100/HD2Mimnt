using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;

namespace HD2Mimnt.Application.Services;

/// <summary>获取当前库与全部配置的快照，交由原生格式写入器迁回其它管理器。</summary>
public sealed class ExternalManagerExportService(IEnumerable<IExternalManagerWriter> writers,
    ModService mods, ProfileService profiles, SettingsService settings)
{
    private readonly IReadOnlyDictionary<ExternalManagerKind, IExternalManagerWriter> _writers = writers.ToDictionary(writer => writer.Manager);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<ExternalMigrationPlan> PreviewAsync(ExternalMigrationSource target, CancellationToken cancellationToken = default)
        => Task.Run(() => PreviewCoreAsync(target, cancellationToken), cancellationToken);

    private async Task<ExternalMigrationPlan> PreviewCoreAsync(ExternalMigrationSource target, CancellationToken token)
    {
        var writer = _writers[target.Manager];
        var plan = new ExternalMigrationPlan { Source = target, SourceIdentity = Path.GetFullPath(target.DataPath) };
        var direction = Enum.TryParse<DeploymentOrder>(await settings.GetSettingAsync(SettingKeys.DefaultDeploymentOrder,
            DeploymentOrder.TopToBottom.ToString()), out var configured) ? configured : DeploymentOrder.TopToBottom;
        var library = (await mods.GetAllModsAsync(token)).ToDictionary(mod => mod.Id);
        foreach (var mod in library.Values)
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.Path))
            {
                plan.Issues.Add(new(mod.Name, $"当前模组目录不存在：{mod.Path}", true));
                continue;
            }
            var manifest = mod.Manifest == null ? new ModManifest { Guid = mod.Id, Name = mod.Name, Version = mod.Version,
                Description = mod.Description, Author = mod.Author, ManifestVersion = ManifestVersion.V1 }
                : JsonSerializer.Deserialize<ModManifest>(JsonSerializer.Serialize(mod.Manifest))!;
            plan.Mods.Add(new ExternalMigrationMod
            {
                SourceId = mod.Id.ToString(), SourcePath = mod.Path, Manifest = manifest, Tags = mod.Tags.ToList(),
                ImagePath = string.IsNullOrWhiteSpace(mod.ThumbnailPath) || !File.Exists(mod.ThumbnailPath) ? null : mod.ThumbnailPath,
                ContentHash = await ModHashService.CalculateAsync(mod.Path, token), Role = BslProfilePolicy.Role(mod)
            });
        }
        var known = plan.Mods.Select(mod => mod.SourceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in (await profiles.GetAllProfilesAsync(token)).OrderBy(profile => profile.SortOrder))
        {
            var snapshot = new ExternalMigrationProfile
            {
                SourceId = profile.Id.ToString(), Name = profile.Name, WasActive = profile.IsActive,
                DeploymentOrder = direction, IsLibraryLayout = profile.IsLibraryLayout,
                IsLuaGroupCollapsed = profile.IsLuaGroupCollapsed, LuaSeparatorColor = profile.LuaSeparatorColor
            };
            foreach (var member in profile.Mods.OrderBy(member => member.Order))
            {
                if (!known.Contains(member.ModId.ToString()))
                    plan.Issues.Add(new(profile.Name, $"配置成员没有可迁回的文件：{member.ModId}", true));
                snapshot.Mods.Add(new(member.ModId.ToString(), member.Enabled,
                    ProfileModOptionResolver.ResolveEffective(library.GetValueOrDefault(member.ModId)?.Manifest, member.OptionsJson)));
            }
            snapshot.Separators.AddRange(profile.Separators.OrderBy(separator => separator.Order).Select(separator =>
                new ExternalMigrationSeparator(separator.Name, separator.Order, separator.IsCollapsed, separator.Color, separator.MemberCount)));
            plan.Profiles.Add(snapshot);
        }
        await writer.InspectAsync(plan, token);
        return plan;
    }

    public Task<ExternalMigrationResult> MigrateAsync(ExternalMigrationPlan plan,
        IProgress<ExternalMigrationProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (plan.Issues.Any(issue => issue.IsBlocking))
                    throw new InvalidDataException(string.Join(Environment.NewLine, plan.Issues.Where(issue => issue.IsBlocking).Select(issue => $"{issue.Item}：{issue.Message}")));
                return await _writers[plan.Source.Manager].WriteAsync(plan, progress, cancellationToken);
            }
            finally { _gate.Release(); }
        }, cancellationToken);
}
