using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 面向 UI 的兼容性门面。模组读取仍通过 Repository，文件扫描和修复委托给基础设施分析器。
/// </summary>
public sealed partial class ModCompatibilityService
{
    private const int MaxPersistedIssues = 8;

    private readonly IModRepository _modRepository;
    private readonly IModCompatibilityAnalyzer _analyzer;
    private readonly ModService _mods;
    private readonly IGameUnitReferenceReader? _gameReferences;
    private readonly SettingsService? _settings;
    private readonly ICompatibilityResourceNames? _names;
    private readonly ICompatibilityResultStore? _resultStore;

    public ModCompatibilityService(IModRepository modRepository, IModCompatibilityAnalyzer analyzer, ModService mods,
        IGameUnitReferenceReader? gameReferences = null, SettingsService? settings = null, ICompatibilityResourceNames? names = null,
        ICompatibilityResultStore? resultStore = null)
    {
        _modRepository = modRepository;
        _analyzer = analyzer;
        _mods = mods;
        _gameReferences = gameReferences;
        _settings = settings;
        _names = names;
        _resultStore = resultStore;
    }

    public async Task<ModCompatibilityReport> CheckModAsync(
        Guid modId,
        uint? referenceVersion = null,
        CancellationToken cancellationToken = default)
    {
        var mod = await _modRepository.GetByIdAsync(modId, cancellationToken)
            ?? throw new KeyNotFoundException($"模组 {modId} 不存在。");
        var report = await _analyzer.AnalyzeAsync(mod.Path, referenceVersion, mod.Id,
            referenceVersion.HasValue ? "provided" : null, cancellationToken);
        if (!referenceVersion.HasValue) await ApplyGameReferencesAsync([report], cancellationToken);
        await ApplyNamesAsync([report], cancellationToken);
        await PersistReportsAsync([report], cancellationToken);
        return report;
    }

    public async Task<IReadOnlyList<ModCompatibilityReport>> CheckModsAsync(
        IEnumerable<Guid> modIds,
        uint? referenceVersion = null,
        CancellationToken cancellationToken = default)
    {
        var ids = modIds.Distinct().ToArray();
        var mods = (await _modRepository.GetByIdsAsync(ids, cancellationToken)).ToDictionary(m => m.Id);
        var reports = new List<ModCompatibilityReport>(ids.Length);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mods.TryGetValue(id, out var mod))
            {
                reports.Add(new ModCompatibilityReport { ModId = id, Status = ModCompatibilityStatus.Error,
                    CheckedAtUtc = DateTime.UtcNow, Issues = ["模组不存在。"] });
                continue;
            }
            reports.Add(await _analyzer.AnalyzeAsync(mod.Path, referenceVersion, mod.Id,
                referenceVersion.HasValue ? "provided" : null, cancellationToken));
        }

        if (!referenceVersion.HasValue) await ApplyGameReferencesAsync(reports, cancellationToken);
        await ApplyNamesAsync(reports, cancellationToken);
        await PersistReportsAsync(reports, cancellationToken);
        return reports;
    }

    public Task<CompatibilityResourceNames> RefreshResourceNamesAsync(CancellationToken cancellationToken = default)
        => _names?.GetAsync(true, cancellationToken) ?? Task.FromResult(new CompatibilityResourceNames());

    private async Task ApplyNamesAsync(IReadOnlyList<ModCompatibilityReport> reports, CancellationToken token)
    {
        if (_names is null || !reports.Any(r => r.UnitCount > 0)) return;
        var names = await _names.GetAsync(cancellationToken: token);
        foreach (var report in reports)
        {
            token.ThrowIfCancellationRequested();
            report.NamesUpdatedAtUtc = names.UpdatedAtUtc;
            report.NamesSource = names.Source;
            report.NamesUpdateError = names.UpdateError;
            foreach (var unit in report.Units) unit.ResourceName = names.Resources.GetValueOrDefault(unit.ResourceId);
            foreach (var patch in report.Patches)
            {
                var file = Path.GetFileName(patch.RelativePath);
                if (file.Length >= 16 && ulong.TryParse(file.AsSpan(0, 16), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)) patch.ArchiveName = names.Archives.GetValueOrDefault(id);
            }
        }
    }

    private async Task ApplyGameReferencesAsync(IReadOnlyList<ModCompatibilityReport> reports, CancellationToken token)
    {
        if (_gameReferences is null || _settings is null) return;
        var ids = reports.SelectMany(report => report.Units).Select(unit => unit.ResourceId).Distinct().ToArray();
        if (ids.Length == 0) return;
        var gamePath = _settings.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            foreach (var report in reports.Where(report => report.UnitCount > 0))
            {
                if (report.Status == ModCompatibilityStatus.Unknown) report.Reason = ModCompatibilityReason.GamePathNotConfigured;
                report.Issues.Add("尚未配置游戏目录，无法比较当前游戏 Unit 版本。");
            }
            return;
        }

        var references = await _gameReferences.ReadAsync(gamePath, ids, token);
        foreach (var report in reports.Where(report => report.UnitCount > 0))
        {
            token.ThrowIfCancellationRequested();
            report.ReferenceSource = references.Source;
            report.Issues.AddRange(references.Issues);
            var compared = 0;
            var mismatch = false;
            foreach (var unit in report.Units)
            {
                token.ThrowIfCancellationRequested();
                references.Units.TryGetValue(unit.ResourceId, out var reference);
                unit.ReferenceStatus = references.IndexComplete
                    ? reference?.Status ?? GameUnitReferenceStatus.Missing : GameUnitReferenceStatus.Error;
                unit.ReferenceVersion = unit.ReferenceStatus == GameUnitReferenceStatus.Found ? reference?.Version : null;
                unit.ReferencePackages = reference?.Packages ?? [];
                if (unit.ReferenceStatus == GameUnitReferenceStatus.Found && unit.ReferenceVersion.HasValue)
                {
                    compared++;
                    if (unit.Version == unit.ReferenceVersion.Value) continue;
                    mismatch = true;
                    report.Issues.Add($"{unit.FileName}: Unit {unit.ResourceId:x16} 版本 {unit.Version:x8} "
                        + $"与游戏参考 {unit.ReferenceVersion.Value:x8} 不同。");
                }
                else
                {
                    report.Issues.Add($"{unit.FileName}: Unit {unit.ResourceId:x16} 游戏参考状态为 {unit.ReferenceStatus}。"
                        + (reference?.ErrorMessage is { } error ? $" {error}" : string.Empty));
                }
            }
            report.ComparedUnitCount = compared;
            if (report.Status != ModCompatibilityStatus.Error)
            {
                // 这里只报告模型版本比较；TOC/GPU 等深度诊断保留给详情和修复校验。
                report.Status = mismatch ? ModCompatibilityStatus.Incompatible
                    : compared > 0 ? ModCompatibilityStatus.Compatible : ModCompatibilityStatus.Unknown;
                report.Reason = report.Status != ModCompatibilityStatus.Unknown ? ModCompatibilityReason.None
                    : !references.IndexComplete || report.Units.Any(unit => unit.ReferenceStatus == GameUnitReferenceStatus.Error)
                        ? ModCompatibilityReason.ReferenceUnavailable : ModCompatibilityReason.NoComparableUnits;
            }
        }
    }

    /// <summary>读取持久化摘要，内容戳仍匹配的模组返回轻量报告（不含逐补丁/逐 Unit 详情），供启动或内容失效后回填角标。</summary>
    public async Task<IReadOnlyDictionary<Guid, ModCompatibilityReport>> RestorePersistedResultsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_resultStore is null) return new Dictionary<Guid, ModCompatibilityReport>();
        var mods = (await _modRepository.GetAllAsync(cancellationToken))
            .Where(mod => !string.IsNullOrWhiteSpace(mod.Path)).ToDictionary(mod => mod.Id);
        var restored = new Dictionary<Guid, ModCompatibilityReport>();
        foreach (var row in await _resultStore.GetAllAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mods.TryGetValue(row.ModId, out var mod) || row.ContentStamp != ModContentStamp.Calculate(mod.Path)) continue;
            restored[row.ModId] = new ModCompatibilityReport
            {
                ModId = row.ModId,
                ModPath = mod.Path,
                Status = row.Status,
                Reason = row.Reason,
                UnitCount = row.UnitCount,
                ComparedUnitCount = row.ComparedUnitCount,
                HasBlockingStructureIssues = row.HasBlockingIssues,
                ReferenceSource = row.ReferenceSource,
                CheckedAtUtc = row.CheckedAtUtc,
                Issues = [.. row.Issues]
            };
        }
        return restored;
    }

    private async Task PersistReportsAsync(IReadOnlyList<ModCompatibilityReport> reports, CancellationToken token)
    {
        if (_resultStore is null || reports.Count == 0) return;
        var rows = new List<PersistedCompatibilityResult>(reports.Count);
        foreach (var report in reports)
        {
            if (report.ModId is not { } modId) continue;
            rows.Add(new PersistedCompatibilityResult
            {
                ModId = modId,
                Status = report.Status,
                Reason = report.Reason,
                UnitCount = report.UnitCount,
                ComparedUnitCount = report.ComparedUnitCount,
                HasBlockingIssues = report.HasBlockingStructureIssues,
                ReferenceSource = report.ReferenceSource,
                Issues = report.Issues.Take(MaxPersistedIssues).ToArray(),
                CheckedAtUtc = report.CheckedAtUtc,
                ContentStamp = ModContentStamp.Calculate(report.ModPath)
            });
        }
        if (rows.Count > 0) await _resultStore.UpsertAsync(rows, token);
    }

    public async Task<CompatibilityRepairPlan> CreateRepairPlanAsync(
        Guid modId, CancellationToken cancellationToken = default)
        => await _analyzer.CreateRepairPlanAsync(
            await ResolvePathAsync(modId, cancellationToken), cancellationToken);

    public async Task<CompatibilityRepairResult> RepairAsync(
        Guid modId, CancellationToken cancellationToken = default)
        => await _mods.MutateCompatibilityAsync(modId,
            path => _analyzer.RepairAsync(path, cancellationToken), cancellationToken);

    public async Task<IReadOnlyList<string>> GetBackupsAsync(Guid modId, CancellationToken cancellationToken = default)
        => await _analyzer.GetBackupsAsync(await ResolvePathAsync(modId, cancellationToken), cancellationToken);

    public Task<CompatibilityRepairResult> RestoreBackupAsync(Guid modId, string backupPath,
        CancellationToken cancellationToken = default)
        => _mods.MutateCompatibilityAsync(modId,
            path => _analyzer.RestoreBackupAsync(path, backupPath, cancellationToken), cancellationToken);

    private async Task<string> ResolvePathAsync(Guid modId, CancellationToken cancellationToken)
        => (await _modRepository.GetByIdAsync(modId, cancellationToken)
            ?? throw new KeyNotFoundException($"模组 {modId} 不存在。")).Path;
}
