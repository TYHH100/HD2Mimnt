using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Migration;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>只读扫描外部软件，导入独立库副本和配置快照；成功项可安全重试，源文件不改动。</summary>
public sealed class ExternalManagerMigrationService
{
    private readonly IReadOnlyDictionary<ExternalManagerKind, IExternalManagerReader> _readers;
    private readonly ModService _mods;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;
    private readonly ILogger<ExternalManagerMigrationService>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ExternalManagerMigrationService(IEnumerable<IExternalManagerReader> readers, ModService mods,
        ProfileService profiles, SettingsService settings, ILogger<ExternalManagerMigrationService>? logger = null)
    {
        _readers = readers.ToDictionary(reader => reader.Manager);
        _mods = mods;
        _profiles = profiles;
        _settings = settings;
        _logger = logger;
    }

    public Task<ExternalMigrationPlan> PreviewAsync(ExternalMigrationSource source, CancellationToken cancellationToken = default)
    {
        if (!_readers.TryGetValue(source.Manager, out var reader))
            throw new NotSupportedException($"未注册外部管理器读取器：{source.Manager}");
        return Task.Run(() => reader.ReadAsync(source, cancellationToken), cancellationToken);
    }

    public Task<ExternalMigrationResult> MigrateAsync(ExternalMigrationPlan plan,
        IProgress<ExternalMigrationProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => MigrateCoreAsync(plan, progress, cancellationToken), cancellationToken);

    private async Task<ExternalMigrationResult> MigrateCoreAsync(ExternalMigrationPlan plan,
        IProgress<ExternalMigrationProgress>? progress, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var identity = MigrationIdentity.Source(plan);
            var result = new ExternalMigrationResult();
            result.Issues.AddRange(plan.Issues);
            var imported = new Dictionary<string, Mod>(StringComparer.OrdinalIgnoreCase);
            var direction = Enum.TryParse<DeploymentOrder>(await _settings.GetSettingAsync(SettingKeys.DefaultDeploymentOrder,
                DeploymentOrder.TopToBottom.ToString()), out var configured) ? configured : DeploymentOrder.TopToBottom;
            var total = plan.Mods.Count + plan.Profiles.Count;
            var completed = 0;
            foreach (var external in plan.Mods)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(completed, total, "准备模组文件", external.Manifest.Name));
                try
                {
                    var (mod, reused) = await _mods.ImportMigratedModAsync(external, identity, token);
                    imported.Add(external.SourceId, mod);
                    result.ModIds.Add(external.SourceId, mod.Id);
                    if (reused) result.ReusedMods++; else result.ImportedMods++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Issues.Add(new(external.Manifest.Name, $"模组迁移失败：{ex.Message}"));
                    _logger?.LogError(ex, "外部模组迁移失败: {ModName}", external.Manifest.Name);
                }
                finally { progress?.Report(new(++completed, total, "处理模组完成", external.Manifest.Name)); }
            }
            token.ThrowIfCancellationRequested();
            await _profiles.InitializeAsync(token);
            foreach (var external in plan.Profiles)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var missing = external.Mods.Where(item => !imported.ContainsKey(item.SourceModId)).Select(item => item.SourceModId).ToList();
                    if (missing.Count > 0) throw new InvalidDataException($"{missing.Count} 个配置成员未成功迁入，保留源配置供重试：{string.Join(", ", missing)}");
                    var profile = MapProfile(external, imported, direction);
                    profile.Id = MigrationIdentity.Create(identity, "profile", external.SourceId.ToUpperInvariant(), JsonSerializer.Serialize(new
                    {
                        external.Name, direction, profile.Mods, profile.IsLibraryLayout,
                        Separators = profile.Separators.Select(item => new { item.Name, item.Order, item.Color, item.IsCollapsed, item.MemberCount }),
                        profile.IsLuaGroupCollapsed, profile.LuaSeparatorColor
                    }));
                    var defaultSourceId = external.IsDefault ? MigrationIdentity.Create(identity, "default-profile", external.SourceId.ToUpperInvariant()) : (Guid?)null;
                    var (id, reused) = await _profiles.ImportMigratedProfileAsync(profile, token, defaultSourceId);
                    result.ProfileIds.Add(external.SourceId, id);
                    if (reused) result.ReusedProfiles++; else result.ImportedProfiles++;
                    if (external.WasActive) result.SourceActiveProfileId = id;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Issues.Add(new(external.Name, $"配置迁移失败：{ex.Message}"));
                    _logger?.LogError(ex, "外部配置迁移失败: {ProfileName}", external.Name);
                }
                finally { progress?.Report(new(++completed, total, "处理配置完成", external.Name)); }
            }
            return result;
        }
        finally { _gate.Release(); }
    }

    private static Profile MapProfile(ExternalMigrationProfile external, IReadOnlyDictionary<string, Mod> mods, DeploymentOrder direction)
    {
        bool IsNormal(ExternalMigrationProfileMod item) => BslProfilePolicy.Role(mods[item.SourceModId]) == BslModRole.None;
        var normalCount = external.Mods.Count(IsNormal);
        var reverse = external.DeploymentOrder.HasValue && external.DeploymentOrder != direction;
        var ordered = external.Mods.Where(IsNormal).ToList();
        if (reverse) ordered.Reverse();
        ordered.AddRange(external.Mods.Where(item => !IsNormal(item)));
        var profile = new Profile
        {
            Name = external.Name, Description = $"从外部管理器迁移（源配置：{external.SourceId}）",
            Mods = ordered.Select((item, index) => new ProfileMod
            {
                ModId = mods[item.SourceModId].Id, Enabled = item.Enabled, Order = index, OptionsJson = item.Options.ToJson()
            }).ToList(),
            IsLuaGroupCollapsed = external.IsLuaGroupCollapsed, LuaSeparatorColor = external.LuaSeparatorColor,
            IsLibraryLayout = external.IsLibraryLayout
        };
        var separators = external.Separators.OrderBy(item => item.Order).ToList();
        for (var index = 0; index < separators.Count; index++)
        {
            var separator = separators[index];
            var start = Math.Clamp(separator.Order, 0, external.Mods.Count);
            var end = Math.Clamp(separator.MemberCount.HasValue ? start + Math.Max(0, separator.MemberCount.Value)
                : index + 1 < separators.Count ? separators[index + 1].Order : external.Mods.Count, start, external.Mods.Count);
            var order = external.Mods.Take(start).Count(IsNormal);
            var count = external.Mods.Skip(start).Take(end - start).Count(IsNormal);
            profile.Separators.Add(new ProfileSeparator
            {
                Id = Guid.NewGuid(), Name = separator.Name, Order = reverse ? normalCount - order - count : order,
                MemberCount = count, IsCollapsed = separator.IsCollapsed, Color = separator.Color
            });
        }
        if (reverse) profile.Separators.Reverse();
        return profile;
    }
}
