using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>库和配置共用的按需兼容性工作区。关闭和退出均等待后台清理。</summary>
public partial class ModCompatibilityViewModel : ObservableObject, IDisposable
{
    private readonly ModCompatibilityService service;
    private readonly IConfirmationService confirmation;
    private readonly SettingsService? _settings;
    private bool _startupCheck;
    private bool _importCheck;
    public ModCompatibilityViewModel(ModCompatibilityService service, IConfirmationService confirmation, SettingsService? settings = null)
    {
        this.service = service; this.confirmation = confirmation; _settings = settings;
        if (_settings != null) _settings.SettingChanged += CompatibilitySettingChanged;
    }
    public bool EnableBatchRepair => _settings?.GetSetting(SettingKeys.CompatibilityEnableBatchRepair, false) == true;
    private void CompatibilitySettingChanged(object? sender, SettingChangedEventArgs args)
    {
        void Apply()
        {
            if (args.Key == SettingKeys.CompatibilityCheckOnStartup && _startupCheck
                && _settings?.GetSetting(SettingKeys.CompatibilityCheckOnStartup, false) != true) Cancel();
            if (args.Key == SettingKeys.CompatibilityAutoCheckImport && _importCheck
                && _settings?.GetSetting(SettingKeys.CompatibilityAutoCheckImport, false) != true) Cancel();
            if (args.Key == SettingKeys.CompatibilityEnableBatchRepair)
            { OnPropertyChanged(nameof(EnableBatchRepair)); RefreshPresentation(); }
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }
    public void Dispose()
    {
        if (_settings != null) _settings.SettingChanged -= CompatibilitySettingChanged;
        _operation?.Cancel();
    }
    private CancellationTokenSource? _operation;
    private bool _closing;
    private bool _stale;
    private volatile bool _mutating;
    public bool IsMutating => _mutating;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public event Func<Task>? ContentChanged;
    private readonly Dictionary<Guid, CompatibilityModRow> _rows = [];
    private readonly Dictionary<Guid, ModCompatibilityBadgeViewModel> _badges = [];
    public ModCompatibilityBadgeViewModel GetBadge(Guid modId)
    {
        if (!_badges.TryGetValue(modId, out var badge)) _badges.Add(modId, badge = new());
        return badge;
    }
    public ObservableCollection<CompatibilityModRow> Mods { get; } = [];
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    [ObservableProperty] private string _scopeName = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private CompatibilityModRow? _selectedMod;
    [ObservableProperty] private bool _repairMetadata = true;
    [ObservableProperty] private bool _recoverCompanions = true;
    [ObservableProperty] private bool _repairUnits = true;
    [ObservableProperty] private bool _migrateMaterials = true;
    [ObservableProperty] private CompatibilityLodStrategy _lodStrategy = CompatibilityLodStrategy.Automatic;
    [ObservableProperty] private int _selectedTab;
    public bool IsIdle => !IsBusy;
    public bool CanRepairSelected => IsIdle && SelectedMod?.Plan?.CanRepair == true;
    public bool CanRepairScope => EnableBatchRepair && IsIdle && Mods.Any(m => m.Plan?.CanRepair == true);
    public bool CanQuickRepairSelected => IsIdle && SelectedMod?.Report is { Status: not ModCompatibilityStatus.NotApplicable };
    public bool CanQuickRepairScope => EnableBatchRepair && IsIdle && Mods.Any(m => m.Report is { Status: not ModCompatibilityStatus.NotApplicable });
    public bool HasSelectedMod => SelectedMod != null;
    public bool CanPreviewSelected => SelectedMod?.Report is { Status: not ModCompatibilityStatus.NotApplicable };
    public bool IsMixed => LodStrategy == CompatibilityLodStrategy.Mixed;
    public IReadOnlyList<CompatibilityLodStrategy> LodStrategies { get; } = Enum.GetValues<CompatibilityLodStrategy>();
    public IReadOnlyList<CompatibilityDetailRow> Details => SelectedMod?.Details ?? [];
    public IReadOnlyList<CompatibilityDetailRow> PlanDetails => SelectedMod?.PlanDetails ?? [];
    public IReadOnlyList<CompatibilityUnitChoice> UnitChoices => SelectedMod?.UnitChoices ?? [];
    public IReadOnlyList<CompatibilityBackupChoice> Backups => SelectedMod?.Backups ?? [];
    [ObservableProperty] private CompatibilityBackupChoice? _selectedBackup;
    public string SelectedOperation => SelectedMod?.OperationSummary ?? string.Empty;
    public string SelectedBackupDetails => SelectedBackup?.Details ?? string.Empty;
    public IReadOnlyList<CompatibilityLodChoice> LodChoices => LodStrategies.Select(s => new CompatibilityLodChoice(s, UiLocalization.Current["Compatibility.Lod." + s])).ToArray();
    partial void OnSelectedBackupChanged(CompatibilityBackupChoice? value) => OnPropertyChanged(nameof(SelectedBackupDetails));
    public string SelectedSummary => SelectedMod?.Summary ?? string.Empty;
    public string Summary => UiLocalization.Current.Format("Compatibility.ScopeSummary", Mods.Count,
        Mods.Count(m => m.Report?.Status == ModCompatibilityStatus.Compatible),
        Mods.Count(m => m.Report?.Status == ModCompatibilityStatus.Incompatible),
        Mods.Count(m => m.Report?.Status == ModCompatibilityStatus.Unknown),
        Mods.Count(m => m.Report?.Status == ModCompatibilityStatus.Error),
        Mods.Count(m => m.Report?.Status == ModCompatibilityStatus.NotApplicable));

    public Task OpenAsync(IEnumerable<ModItemViewModel> targets, string scope)
    {
        if (_closing) return Completion;
        IsOpen = true;
        if (IsBusy) return Completion;
        ClearRows();
        _stale = false;
        ScopeName = scope;
        Message = string.Empty;
        SelectedTab = 0;
        foreach (var target in targets.DistinctBy(m => m.Id))
        {
            var row = new CompatibilityModRow(target);
            _rows.Add(row.Id, row); Mods.Add(row);
        }
        SelectedMod = Mods.FirstOrDefault();
        return Start(CheckCoreAsync);
    }

    public async Task CheckOnStartupAsync(IEnumerable<ModItemViewModel> targets, NotificationService notifications)
    {
        if (_settings?.GetSetting(SettingKeys.CompatibilityCheckOnStartup, false) != true || IsBusy || IsOpen || _closing) return;
        ClearRows(); _stale = false; ScopeName = UiLocalization.Current["Compatibility.LibraryScope"];
        // 增量：内容戳未变的模组直接回填持久化结果，只重扫其余模组。
        var restored = await Task.Run(() => service.RestorePersistedResultsAsync());
        foreach (var target in targets.DistinctBy(m => m.Id))
        {
            var row = new CompatibilityModRow(target);
            _rows.Add(row.Id, row); Mods.Add(row);
            if (restored.TryGetValue(row.Id, out var report))
            {
                GetBadge(row.Id).ApplyReport(report);
                row.ApplyReport(report);
            }
        }
        if (Mods.Count == 0) return;
        SelectedMod = Mods[0]; SelectedTab = 0; _startupCheck = true;
        try
        {
            var pending = Mods.Where(m => m.Report == null).ToArray();
            if (pending.Length > 0) await Start(token => CheckCoreAsync(pending, token));
            if (Mods.Count > 0 && Mods.All(m => m.Report != null))
                notifications.ShowWithAction(NotificationLevel.Info, Summary, UiLocalization.Current["Settings.Compatibility.ShowResults"],
                    () => Dispatcher.UIThread.Post(() => IsOpen = true), UiLocalization.Current["Compatibility.Title"]);
        }
        finally { _startupCheck = false; }
    }

    /// <summary>导入批次完成后在后台检查本次导入的模组；需要开启导入自动检查设置。</summary>
    public async Task CheckAfterImportAsync(IEnumerable<ModItemViewModel> targets, NotificationService notifications)
    {
        if (_settings?.GetSetting(SettingKeys.CompatibilityAutoCheckImport, false) != true || IsBusy || IsOpen || _closing) return;
        var fresh = targets.DistinctBy(m => m.Id).ToArray();
        if (fresh.Length == 0) return;
        ClearRows(); _stale = false;
        ScopeName = UiLocalization.Current["Compatibility.ImportScope"];
        Message = string.Empty; SelectedTab = 0;
        foreach (var target in fresh)
        {
            var row = new CompatibilityModRow(target);
            _rows.Add(row.Id, row); Mods.Add(row);
        }
        SelectedMod = Mods[0];
        _importCheck = true;
        try
        {
            await Start(token => CheckCoreAsync([.. Mods], token));
            if (Mods.Count > 0 && Mods.All(m => m.Report != null))
                notifications.ShowWithAction(NotificationLevel.Info, Summary, UiLocalization.Current["Settings.Compatibility.ShowResults"],
                    () => Dispatcher.UIThread.Post(() => IsOpen = true), UiLocalization.Current["Compatibility.Title"]);
        }
        finally { _importCheck = false; }
    }

    [RelayCommand] private Task CheckAsync() => Start(CheckCoreAsync);
    private Task CheckCoreAsync(CancellationToken token) => CheckCoreAsync([.. Mods], token);

    private async Task CheckCoreAsync(CompatibilityModRow[] rows, CancellationToken token)
    {
        ProgressText = UiLocalization.Current["Compatibility.Checking"];
        var ids = rows.Select(m => m.Id).ToArray();
        var reports = await Task.Run(() => service.CheckModsAsync(ids, cancellationToken: token), token);
        token.ThrowIfCancellationRequested();
        foreach (var report in reports)
        {
            if (report.ModId is { } modId) GetBadge(modId).ApplyReport(report);
            if (report.ModId is { } id && _rows.TryGetValue(id, out var row))
            {
                foreach (var unit in row.UnitChoices) unit.PropertyChanged -= UnitOptionChanged;
                row.ApplyReport(report);
                foreach (var unit in row.UnitChoices) unit.PropertyChanged += UnitOptionChanged;
            }
        }
        RefreshPresentation();
        if (SelectedMod is { } selected) await LoadBackupsAsync(selected, token);
        Message = UiLocalization.Current["Compatibility.CheckComplete"];
    }

    /// <summary>启动时从持久化摘要回填角标；不需要开启启动检查设置。</summary>
    public async Task RestoreBadgesAsync()
    {
        if (_closing || IsBusy) return;
        await Start(RestoreCoreAsync);
    }

    private async Task RestoreCoreAsync(CancellationToken token)
    {
        var restored = await Task.Run(() => service.RestorePersistedResultsAsync(), token);
        foreach (var (modId, report) in restored)
        {
            if (token.IsCancellationRequested) return;
            GetBadge(modId).ApplyReport(report);
            if (_rows.TryGetValue(modId, out var row)) row.ApplyReport(report);
        }
    }

    private CompatibilityRepairOptions Options(CompatibilityModRow row) => new()
    {
        RepairMetadata = RepairMetadata, RecoverCompanions = RecoverCompanions, RepairUnits = RepairUnits,
        MigrateMaterials = MigrateMaterials, LodStrategy = LodStrategy,
        PreserveModLodUnitIds = row.UnitChoices.Where(u => u.PreserveLod).Select(u => u.Id).ToHashSet()
    };

    [RelayCommand] private Task PreviewSelectedAsync() => Start(token => PreviewAsync(SelectedMod is { } row ? [row] : [], token));
    [RelayCommand] private Task PreviewScopeAsync() => Start(token => PreviewAsync(Mods.ToArray(), token));
    private async Task PreviewAsync(CompatibilityModRow[] rows, CancellationToken token)
    {
        if (!rows.Any(row => row.Report is { Status: not ModCompatibilityStatus.NotApplicable }))
        { Message = UiLocalization.Current["Compatibility.NoModels"]; return; }
        SelectedTab = 1;
        await BuildPlansAsync(rows, token);
        Message = UiLocalization.Current["Compatibility.PreviewComplete"];
    }

    /// <summary>为报告可参与的行生成修复计划；一键修复在执行前也经由这里补建计划。</summary>
    private async Task<bool> BuildPlansAsync(CompatibilityModRow[] rows, CancellationToken token)
    {
        rows = rows.Where(row => row.Report is { Status: not ModCompatibilityStatus.NotApplicable }).ToArray();
        if (rows.Length == 0) { Message = UiLocalization.Current["Compatibility.NoModels"]; return false; }
        ProgressText = UiLocalization.Current["Compatibility.Previewing"];
        // 生成新预览前清除旧计划，取消/失败后不能误用上一次选项的计划。
        foreach (var row in rows) { row.ApplyPlan(null); row.OperationError = null; row.OperationState = null; }
        RefreshPresentation();
        var options = rows.ToDictionary(r => r.Id, Options);
        var progress = Progress(token);
        var results = await Task.Run(() => service.CreateBatchRepairPlanAsync(rows.Select(r => r.Id),
            progress: progress, cancellationToken: token, perModOptions: options), token);
        foreach (var result in results) ApplyBatch(result);
        RefreshPresentation();
        return true;
    }

    [RelayCommand] private Task RepairSelectedAsync() => Start(token => RepairAsync(SelectedMod is { Report: not null } row ? [row] : [], token));
    [RelayCommand] private Task RepairScopeAsync() => !EnableBatchRepair ? Task.CompletedTask
        : Start(token => RepairAsync(Mods.Where(m => m.Report is { Status: not ModCompatibilityStatus.NotApplicable }).ToArray(), token, batch: true));
    private async Task RepairAsync(CompatibilityModRow[] rows, CancellationToken token, bool batch = false)
    {
        if (rows.Length == 0) return;
        if (batch && !EnableBatchRepair) return;
        // 一键修复：尚无有效计划时先在后台生成，再进入数量确认。
        var pendingPlans = rows.Where(row => row.Plan == null
            && row.Report is { Status: not ModCompatibilityStatus.NotApplicable }).ToArray();
        if (pendingPlans.Length > 0 && !await BuildPlansAsync(pendingPlans, token)) return;
        rows = rows.Where(row => row.Plan?.CanRepair == true).ToArray();
        if (rows.Length == 0) { Message = UiLocalization.Current["Compatibility.NothingToRepair"]; return; }
        if (!await confirmation.ConfirmAsync(UiLocalization.Current["Compatibility.Repair"],
            UiLocalization.Current.Format("Compatibility.ConfirmRepair", rows.Length, rows.Sum(r => r.Plan!.ActionCount)))) return;
        token.ThrowIfCancellationRequested();
        var results = new ConcurrentQueue<CompatibilityBatchItem>();
        var options = rows.ToDictionary(r => r.Id, Options);
        _mutating = true;
        try
        {
            ProgressText = UiLocalization.Current["Compatibility.Repairing"];
            var progress = Progress(token, item => results.Enqueue(item));
            await Task.Run(() => service.RepairBatchAsync(rows.Select(r => r.Id), progress: progress,
                cancellationToken: token, perModOptions: options), token);
        }
        finally
        {
            // 文件提交后刷新必须被等待；用户在提交后取消也不能留下陈旧库状态。
            try
            {
                if (results.Any(r => r.Result?.Changed == true) && ContentChanged != null) await ContentChanged();
            }
            finally
            {
                _mutating = false;
                foreach (var row in rows) row.ApplyPlan(null);
                foreach (var item in results) ApplyBatch(item);
                RefreshPresentation();
            }
        }
        await CheckCoreAsync(token);
        Message = UiLocalization.Current.Format("Compatibility.RepairComplete",
            results.Count(r => r.State == CompatibilityBatchState.Repaired), results.Count(r => r.State == CompatibilityBatchState.Failed));
    }

    private IProgress<CompatibilityBatchProgress> Progress(CancellationToken token, Action<CompatibilityBatchItem>? completed = null)
    {
        var operation = _operation;
        long lastUpdate = 0;
        return new InlineProgress(p =>
        {
            completed?.Invoke(p.Item);
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (p.Completed != p.Total && lastUpdate != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(lastUpdate, now).TotalMilliseconds < 100) return;
            lastUpdate = now;
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(operation, _operation) && !token.IsCancellationRequested) ReportProgress(p);
            });
        });
    }
    private void ReportProgress(CompatibilityBatchProgress p)
        => ProgressText = $"{p.Completed} / {p.Total} · {p.Item.Name}";
    private sealed class InlineProgress(Action<CompatibilityBatchProgress> action) : IProgress<CompatibilityBatchProgress>
    { public void Report(CompatibilityBatchProgress value) => action(value); }
    private void ApplyBatch(CompatibilityBatchItem item)
    {
        if (!_rows.TryGetValue(item.ModId, out var row)) return;
        row.ApplyPlan(item.Plan);
        row.OperationError = string.Join(Environment.NewLine, new[] { item.Error }.Concat(item.Result?.RecoveryErrors ?? []).Where(e => !string.IsNullOrWhiteSpace(e)));
        row.OperationState = item.State;
        row.RefreshText();
    }

    [RelayCommand] private Task RefreshBackupsAsync() => Start(async token =>
    { if (SelectedMod is { } row) await LoadBackupsAsync(row, token); });
    private async Task LoadBackupsAsync(CompatibilityModRow row, CancellationToken token)
    {
        row.Backups = (await Task.Run(() => service.GetBackupHistoryAsync(row.Id, token), token)).Select(b => new CompatibilityBackupChoice(b)).ToArray();
        if (SelectedMod == row) { SelectedBackup = row.Backups.FirstOrDefault(); OnPropertyChanged(nameof(Backups)); }
    }
    [RelayCommand] private Task RestoreBackupAsync() => Start(async token =>
    {
        if (SelectedMod is not { } row || SelectedBackup is not { Entry.CanRestore: true } backup) return;
        if (!await confirmation.ConfirmAsync(UiLocalization.Current["Compatibility.Restore"], UiLocalization.Current["Compatibility.ConfirmRestore"])) return;
        await MutateBackupAsync(row, () => service.RestoreBackupAsync(row.Id, backup.Entry.BackupPath, token), token);
    });
    [RelayCommand] private Task RollbackAsync() => Start(async token =>
    {
        if (SelectedMod is not { } row || SelectedBackup is not { Entry.CanRestore: true } backup) return;
        if (!await confirmation.ConfirmAsync(UiLocalization.Current["Compatibility.Rollback"], UiLocalization.Current["Compatibility.ConfirmRollback"])) return;
        await MutateBackupAsync(row, () => service.RollbackToAsync(row.Id, backup.Entry.CreatedAtUtc, token), token);
    });
    private async Task MutateBackupAsync(CompatibilityModRow row, Func<Task<CompatibilityRepairResult>> action, CancellationToken token)
    {
        _mutating = true;
        try
        {
            var result = await Task.Run(action);
            if (result.Changed && ContentChanged != null) await ContentChanged();
            InvalidatePlans();
            row.OperationState = null;
            row.OperationError = string.Join(Environment.NewLine, new[] { result.ErrorMessage }.Concat(result.RecoveryErrors).Where(e => !string.IsNullOrWhiteSpace(e)));
            row.RefreshText();
            OnPropertyChanged(nameof(SelectedOperation));
            await CheckCoreAsync(token);
            Message = UiLocalization.Current[result.Success ? "Compatibility.Restored" : "Compatibility.RestoreFailed"];
        }
        finally { _mutating = false; }
    }
    [RelayCommand] private Task DeleteBackupAsync() => Start(async token =>
    {
        if (SelectedMod is not { } row || SelectedBackup is not { } backup) return;
        if (!await confirmation.ConfirmAsync(UiLocalization.Current["Compatibility.DeleteBackup"], UiLocalization.Current["Compatibility.ConfirmDeleteBackup"])) return;
        await Task.Run(() => service.DeleteBackupAsync(row.Id, backup.Entry.BackupPath, token), token);
        await LoadBackupsAsync(row, token);
    });
    [RelayCommand] private Task CleanBackupsAsync() => Start(async token =>
    {
        if (SelectedMod is not { } row) return;
        if (!await confirmation.ConfirmAsync(UiLocalization.Current["Compatibility.CleanBackups"], UiLocalization.Current["Compatibility.ConfirmCleanBackups"])) return;
        await Task.Run(() => service.CleanOldBackupsAsync(row.Id, 3, token), token);
        await LoadBackupsAsync(row, token);
    });
    [RelayCommand] private Task RefreshNamesAsync() => Start(async token =>
    {
        var names = await Task.Run(() => service.RefreshResourceNamesAsync(token), token);
        await CheckCoreAsync(token);
        if (names.UpdateError is { } error) Message = error;
    });

    private Task Start(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _closing) return Completion;
        return Completion = RunAsync(action);
    }
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        using var operation = new CancellationTokenSource();
        _operation = operation; IsBusy = true; Message = string.Empty;
        try { await action(operation.Token); }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { Message = UiLocalization.Current["Compatibility.Canceled"]; }
        catch (Exception ex) { Message = ex.Message; }
        finally
        {
            _operation = null; ProgressText = string.Empty; IsBusy = false;
            if (_stale) { _stale = false; Invalidate(); }
            RefreshPresentation();
        }
    }
    [RelayCommand] public void Cancel() => _operation?.Cancel();
    private Task _closeTask = Task.CompletedTask;
    [RelayCommand] private Task CloseAsync() => _closing ? _closeTask : _closeTask = CloseCoreAsync();
    private async Task CloseCoreAsync()
    {
        _closing = true;
        try { Cancel(); await Completion; IsOpen = false; ClearRows(); }
        finally { _closing = false; }
    }
    public Task StopAsync() => CloseAsync();
    public void Invalidate()
    {
        if (_mutating) return;
        foreach (var badge in _badges.Values) badge.ApplyReport(null);
        if (IsBusy) { _stale = true; Cancel(); return; }
        foreach (var row in Mods)
        {
            foreach (var unit in row.UnitChoices) unit.PropertyChanged -= UnitOptionChanged;
            row.ApplyReport(null);
        }
        InvalidatePlans();
        if (IsOpen) Message = UiLocalization.Current["Compatibility.Stale"];
        // 内容变化清空角标后，从持久化摘要恢复未变化模组的结果，避免无关模组丢失检查状态。
        Start(RestoreCoreAsync);
    }
    private void ClearRows()
    {
        foreach (var row in Mods) foreach (var unit in row.UnitChoices) unit.PropertyChanged -= UnitOptionChanged;
        _rows.Clear(); Mods.Clear(); SelectedMod = null; RefreshPresentation();
    }
    private void UnitOptionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => InvalidatePlans();
    private void InvalidatePlans() { foreach (var row in Mods) row.ApplyPlan(null); RefreshPresentation(); }
    partial void OnRepairMetadataChanged(bool value) => InvalidatePlans();
    partial void OnRecoverCompanionsChanged(bool value) => InvalidatePlans();
    partial void OnRepairUnitsChanged(bool value) => InvalidatePlans();
    partial void OnMigrateMaterialsChanged(bool value) => InvalidatePlans();
    partial void OnLodStrategyChanged(CompatibilityLodStrategy value) { InvalidatePlans(); OnPropertyChanged(nameof(IsMixed)); }
    partial void OnIsBusyChanged(bool value) => RefreshPresentation();
    partial void OnSelectedModChanged(CompatibilityModRow? value)
    {
        SelectedBackup = value?.Backups.FirstOrDefault();
        RefreshPresentation();
    }
    public void Relocalize()
    {
        foreach (var badge in _badges.Values) badge.RefreshText();
        foreach (var row in Mods) row.Relocalize();
        OnPropertyChanged(nameof(LodChoices));
        RefreshPresentation();
    }
    public void RefreshPresentation()
    {
        foreach (var name in new[] { nameof(Summary), nameof(SelectedSummary), nameof(HasSelectedMod), nameof(CanPreviewSelected), nameof(Details),
            nameof(SelectedOperation), nameof(SelectedBackupDetails), nameof(PlanDetails), nameof(UnitChoices), nameof(Backups), nameof(CanRepairSelected), nameof(CanRepairScope),
            nameof(CanQuickRepairSelected), nameof(CanQuickRepairScope) }) OnPropertyChanged(name);
    }
}

/// <summary>按 Mod ID 共用的会话内角标，关闭检查窗口或重建卡片仍保留结果。</summary>
public sealed class ModCompatibilityBadgeViewModel : ObservableObject
{
    private ModCompatibilityReport? _report;
    public bool HasWarning => _report is { } report
        && (report.Status == ModCompatibilityStatus.Incompatible || report.HasBlockingStructureIssues);
    public string Tooltip => _report == null ? string.Empty : UiLocalization.Current["Compatibility.Title"] + "："
        + BadgeStatus(_report)
        + string.Concat(_report.Issues.Take(3).Select(issue => Environment.NewLine + issue));
    private static string BadgeStatus(ModCompatibilityReport report)
        => report.Status == ModCompatibilityStatus.Incompatible || !report.HasBlockingStructureIssues
            ? CompatibilityPresentation.Status(report)
            : UiLocalization.Current["Compatibility.Corrupted"];
    public void ApplyReport(ModCompatibilityReport? report)
    {
        _report = report;
        OnPropertyChanged(nameof(HasWarning));
        RefreshText();
    }
    public void RefreshText() => OnPropertyChanged(nameof(Tooltip));
}

public sealed partial class CompatibilityModRow(ModItemViewModel mod) : ObservableObject
{
    public Guid Id => mod.Id;
    public string Name => mod.Name;
    public ModCompatibilityReport? Report { get; private set; }
    public CompatibilityFullRepairPlan? Plan { get; private set; }
    public IReadOnlyList<CompatibilityDetailRow> Details { get; private set; } = [];
    public IReadOnlyList<CompatibilityDetailRow> PlanDetails { get; private set; } = [];
    public IReadOnlyList<CompatibilityUnitChoice> UnitChoices { get; private set; } = [];
    public IReadOnlyList<CompatibilityBackupChoice> Backups { get; set; } = [];
    public string? OperationError { get; set; }
    public CompatibilityBatchState? OperationState { get; set; }
    public string OperationSummary => string.Join(Environment.NewLine, new[] { OperationState is { } state ? UiLocalization.Current["Compatibility.State." + state] : null, OperationError }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Status => CompatibilityPresentation.Status(Report);
    public string Summary => Report is { } report ? UiLocalization.Current.Format("Compatibility.ModSummary", Name, Status, report.Patches.Count, report.UnitCount, report.CheckedAtUtc.ToLocalTime())
        + (report.UnitCount > 0 ? Environment.NewLine + CompatibilityPresentation.Coverage(report) : string.Empty) : Name;
    public void ApplyReport(ModCompatibilityReport? report)
    {
        Report = report; ApplyPlan(null);
        var preserved = UnitChoices.Where(u => u.PreserveLod).Select(u => u.Id).ToHashSet();
        UnitChoices = report?.Units.DistinctBy(u => u.ResourceId).Select(u => new CompatibilityUnitChoice(u) { PreserveLod = preserved.Contains(u.ResourceId) }).ToArray() ?? [];
        Details = report == null ? [] : CompatibilityPresentation.Report(report); RefreshText();
    }
    public void ApplyPlan(CompatibilityFullRepairPlan? plan)
    { Plan = plan; PlanDetails = plan == null ? [] : CompatibilityPresentation.Plan(plan); RefreshText(); }
    public void Relocalize()
    {
        Details = Report == null ? [] : CompatibilityPresentation.Report(Report);
        PlanDetails = Plan == null ? [] : CompatibilityPresentation.Plan(Plan);
        RefreshText();
    }
    public void RefreshText() { OnPropertyChanged(nameof(OperationSummary)); OnPropertyChanged(nameof(Status)); OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(OperationError)); }
}
public sealed record CompatibilityLodChoice(CompatibilityLodStrategy Value, string Label);
public sealed record CompatibilityDetailRow(string Title, string Text);
public sealed partial class CompatibilityUnitChoice(CompatibilityUnit unit) : ObservableObject
{
    public ulong Id => unit.ResourceId;
    public string Label => $"{unit.ResourceName ?? "Unit"} · {unit.ResourceId:x16}";
    [ObservableProperty] private bool _preserveLod;
}
public sealed record CompatibilityBackupChoice(CompatibilityBackupEntry Entry)
{
    public string Label => $"{Entry.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {System.IO.Path.GetFileName(Entry.OriginalPath)} · {Entry.Size:N0} B";
    public string Details => $"SHA-256: {Entry.Sha256}\n{Entry.Error ?? (Entry.CanRestore ? UiLocalization.Current["Compatibility.BackupValid"] : UiLocalization.Current["Compatibility.Error"])}\n{Entry.BackupPath}";
}

internal static class CompatibilityPresentation
{
    public static string Status(ModCompatibilityReport? report)
    {
        if (report == null) return UiLocalization.Current["Compatibility.Unchecked"];
        if (report.Status == ModCompatibilityStatus.Unknown)
            return UiLocalization.Current.Format("Compatibility.UnconfirmedReason",
                UiLocalization.Current["Compatibility.Reason." + (report.Reason == ModCompatibilityReason.None ? ModCompatibilityReason.ReferenceUnavailable : report.Reason)]);
        return UiLocalization.Current["Compatibility." + report.Status];
    }
    public static string Coverage(ModCompatibilityReport report) => UiLocalization.Current.Format("Compatibility.ComparisonCoverage",
        report.ComparedUnitCount, report.UnitCount, Math.Max(0, report.UnitCount - report.ComparedUnitCount));
    private static string ActionName(string kind) => kind is "CompanionRecovery" or "MaterialMigration" or "UnitMigration" ? UiLocalization.Current["Compatibility.Action." + kind] : kind;
    private static string Flag(bool value) => UiLocalization.Current[value ? "Compatibility.Yes" : "Compatibility.No"];
    public static IReadOnlyList<CompatibilityDetailRow> Report(ModCompatibilityReport report)
    {
        var rows = new List<CompatibilityDetailRow>();
        rows.Add(new(UiLocalization.Current["Compatibility.CheckTab"], Status(report)));
        if (report.Status == ModCompatibilityStatus.NotApplicable)
        {
            rows.Add(new(UiLocalization.Current["Compatibility.Scope"], UiLocalization.Current["Compatibility.NoModels"]));
            return rows;
        }
        rows.Add(new(UiLocalization.Current["Compatibility.Scope"], UiLocalization.Current["Compatibility.ScopeHelp"]));
        if (report.UnitCount > 0) rows.Add(new(UiLocalization.Current["Compatibility.Source"], Coverage(report)));
        foreach (var issue in report.Issues) rows.Add(new(UiLocalization.Current["Compatibility.Issue"], issue));
        foreach (var patch in report.Patches.Where(patch => patch.ContainsUnitResources != false))
        {
            rows.Add(new(patch.ArchiveName ?? patch.RelativePath, UiLocalization.Current.Format("Compatibility.PatchDetails", patch.RelativePath, patch.FileSize, Flag(patch.HeaderValid && patch.IndexValid), Flag(patch.PayloadRangesValid), Flag(patch.HasStream), Flag(patch.HasGpuResources))));
            foreach (var issue in patch.Issues) rows.Add(new(UiLocalization.Current["Compatibility.Issue"], issue));
            foreach (var unit in patch.Units)
                rows.Add(new($"{unit.ResourceName ?? "Unit"} · {unit.ResourceId:x16}",
                    UiLocalization.Current.Format("Compatibility.UnitDetails", unit.FileName, unit.Version.ToString("x8"), unit.ReferenceVersion?.ToString("x8") ?? "?", UiLocalization.Current["Compatibility.Reference." + (unit.ReferenceStatus?.ToString() ?? "Unknown")], unit.DataSize, unit.StreamSize, unit.GpuSize) + Environment.NewLine
                    + string.Join(Environment.NewLine, (unit.Analysis?.Errors ?? []).Concat(unit.Analysis?.Warnings ?? []))));
        }
        rows.Add(new(UiLocalization.Current["Compatibility.Source"],
            $"{report.ReferenceSource}\n{report.NamesSource}\n{report.NamesUpdatedAtUtc?.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n{report.NamesUpdateError}"));
        return rows;
    }
    public static IReadOnlyList<CompatibilityDetailRow> Plan(CompatibilityFullRepairPlan plan)
        => plan.BlockingReasons.Select(reason => new CompatibilityDetailRow(UiLocalization.Current["Compatibility.Blocked"], reason))
            .Concat(plan.Warnings.Select(warning => new CompatibilityDetailRow(UiLocalization.Current["Compatibility.Warning"], warning)))
            .Concat(plan.Metadata.Actions.Select(a => new CompatibilityDetailRow(UiLocalization.Current["Compatibility.Action." + a.Kind], $"{a.PatchFilePath}\n0x{a.Offset:x} · {a.Width} B · {a.OldValue:x} → {a.NewValue:x}")))
            .Concat(plan.Actions.Select(a => new CompatibilityDetailRow(ActionName(a.Kind), $"{a.RelativePath} · {a.ResourceId:x16}\n{a.Description}\n{a.OldSize:N0} → {a.NewSize:N0} B · {(a.LodStrategy is { } strategy ? UiLocalization.Current["Compatibility.Lod." + strategy] : "")}"))).ToArray();
}
