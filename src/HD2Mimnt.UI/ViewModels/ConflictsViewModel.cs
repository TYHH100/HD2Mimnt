using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 冲突页：展示当前配置的资源覆盖、元数据差异和预期生效者。
/// </summary>
public partial class ConflictsViewModel : ObservableObject
{
    private readonly ConflictDetectionService _conflictDetectionService;
    private readonly ProfileService _profileService;
    private readonly NotificationService _notificationService;

    private CancellationTokenSource? _scanCts;
    private Guid? _targetProfileId;
    private int _revision;
    private bool _automaticChecking;
    private bool _automaticInvocation;
    private bool _automaticScanActive;
    private CancellationTokenSource? _automaticCts;
    private Task? _automaticTask;
    public ConflictCardIndex CardIndex { get; private set; } = ConflictCardIndex.Empty;

    public void SetAutomaticChecking(bool enabled)
    {
        if (_automaticChecking == enabled) return;
        _automaticChecking = enabled;
        if (enabled) QueueAutomaticScan();
        else
        {
            _automaticCts?.Cancel();
            if (_automaticScanActive) _scanCts?.Cancel();
        }
    }

    private void QueueAutomaticScan()
    {
        _automaticCts?.Cancel();
        if (!_automaticChecking || !_targetProfileId.HasValue) return;
        var cts = new CancellationTokenSource();
        _automaticCts = cts;
        _automaticTask = RunAutomaticScanAsync(cts);
    }

    private async Task RunAutomaticScanAsync(CancellationTokenSource cts)
    {
        try
        {
            // 合并连续启停、选项及排序变更；始终等待上一轮完成清理，避免并发读取磁盘。
            await Task.Delay(1500, cts.Token);
            if (ScanCommand.ExecutionTask is { IsCompleted: false } pending) await pending;
            cts.Token.ThrowIfCancellationRequested();
            if (!_automaticChecking || !_targetProfileId.HasValue) return;
            _automaticInvocation = true;
            Task scan;
            try { scan = ScanCommand.ExecuteAsync(null); }
            finally { _automaticInvocation = false; }
            await scan;
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (_automaticCts == cts) _automaticCts = null;
            cts.Dispose();
        }
    }

    [ObservableProperty] private string _profileName = string.Empty;

    public void SetProfile(Guid? id, string? name)
    {
        ProfileName = name ?? string.Empty;
        HasProfile = id.HasValue;
        if (_targetProfileId == id) return;
        _targetProfileId = id;
        InvalidateResults();
    }

    public void InvalidateResults()
    {
        _revision++;
        _scanCts?.Cancel();
        CardIndex = ConflictCardIndex.Empty;
        HasScanned = false;
        HasError = false;
        ErrorMessage = string.Empty;
        SummaryText = "配置或模组已更新，请检查冲突";
        Groups = new();
        Warnings = new();
        RefreshStateProperties();
        QueueAutomaticScan();
    }

    /// <summary>扫描完成（含结果数量），供总览页统计回填。</summary>
    public event EventHandler<int>? ScanCompleted;

    [ObservableProperty]
    private ObservableCollection<ConflictGroupViewModel> _groups = new();

    [ObservableProperty]
    private ObservableCollection<ConflictScanWarning> _warnings = new();

    public bool HasWarnings => Warnings.Count > 0;
    public string NoConflictsText => HasWarnings ? "已读取的资源中未发现跨模组覆盖" : "未检测到跨模组资源覆盖";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasScanned;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _hasProfile;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    public bool IsScanning => IsLoading;
    public bool IsNoProfileStateVisible => !IsLoading && !HasProfile;
    public bool IsNotScannedStateVisible => !IsLoading && HasProfile && !HasScanned && !HasError;
    public bool IsErrorStateVisible => !IsLoading && HasError;
    public bool IsResultsVisible => !IsLoading && HasProfile && HasScanned && !HasError;
    public bool IsNoConflictsVisible => IsResultsVisible && Groups.Count == 0;
    public bool IsConflictsVisible => IsResultsVisible && Groups.Count > 0;

    public string ProfileScanStatus => IsScanning ? "正在检查启用模组…"
        : HasError ? ErrorMessage
        : HasScanned ? (Groups.Count > 0 ? $"发现 {Groups.Count} 组资源覆盖，已标记相关卡片" : NoConflictsText)
            + (HasWarnings ? $"；{Warnings.Count} 项未能读取，检测不完整" : "")
        : SummaryText;

    public bool HasProfileScanStatus => !string.IsNullOrEmpty(ProfileScanStatus);

    public ConflictsViewModel(
        ConflictDetectionService conflictDetectionService,
        ProfileService profileService,
        NotificationService notificationService)
    {
        _conflictDetectionService = conflictDetectionService;
        _profileService = profileService;
        _notificationService = notificationService;
    }

    partial void OnIsLoadingChanged(bool value) => RefreshStateProperties();
    partial void OnHasScannedChanged(bool value) => RefreshStateProperties();
    partial void OnHasErrorChanged(bool value) => RefreshStateProperties();
    partial void OnHasProfileChanged(bool value) => RefreshStateProperties();
    partial void OnWarningsChanged(ObservableCollection<ConflictScanWarning> value)
    {
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(NoConflictsText));
        RefreshProfileScanStatus();
    }

    partial void OnSummaryTextChanged(string value) => RefreshProfileScanStatus();
    partial void OnErrorMessageChanged(string value) => RefreshProfileScanStatus();

    private void RefreshProfileScanStatus()
    {
        OnPropertyChanged(nameof(ProfileScanStatus));
        OnPropertyChanged(nameof(HasProfileScanStatus));
    }

    private void RefreshStateProperties()
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsNoProfileStateVisible));
        OnPropertyChanged(nameof(IsNotScannedStateVisible));
        OnPropertyChanged(nameof(IsErrorStateVisible));
        OnPropertyChanged(nameof(IsResultsVisible));
        OnPropertyChanged(nameof(IsNoConflictsVisible));
        OnPropertyChanged(nameof(IsConflictsVisible));
        RefreshProfileScanStatus();
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsLoading) return;
        _automaticScanActive = _automaticInvocation;
        if (!_automaticInvocation) _automaticCts?.Cancel();

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        using var scanCts = new CancellationTokenSource();
        _scanCts = scanCts;
        var cancellationToken = scanCts.Token;
        var revision = _revision;

        IsLoading = true;
        CardIndex = ConflictCardIndex.Empty;
        HasScanned = false;
        HasError = false;
        ErrorMessage = string.Empty;
        Groups = new();
        Warnings = new();
        SummaryText = "正在读取当前配置的补丁资源…";
        try
        {
            var targetProfileId = _targetProfileId;
            var profile = await Task.Run(async () => targetProfileId is { } targetId
                ? await _profileService.GetProfileByIdAsync(targetId, cancellationToken)
                : await _profileService.GetActiveProfileAsync(cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _revision) return;
            if (profile == null)
            {
                HasProfile = false;
                Groups = new ObservableCollection<ConflictGroupViewModel>();
                return;
            }

            HasProfile = true;
            ProfileName = profile.Name;
            var result = await Task.Run(() => _conflictDetectionService.ScanProfileAsync(profile.Id, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _revision) return;

            // 大量冲突的文字格式化和卡片关系归并同样在后台完成。
            var presentation = await Task.Run(() =>
            {
                var groups = result.Groups.Select(group =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new ConflictGroupViewModel(group);
                }).ToArray();
                return (Groups: groups, Index: ConflictCardIndex.Create(groups, cancellationToken),
                    Definite: groups.Count(group => group.IsDefiniteConflict));
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _revision) return;
            CardIndex = presentation.Index;
            Groups = new(presentation.Groups);
            HasScanned = true;
            Warnings = new(result.Warnings);
            SummaryText = $"已扫描 {result.ScannedModCount} 个启用模组、{result.ScannedPatchCount} 个补丁、{result.ScannedResourceCount} 条资源；"
                + $"发现 {Groups.Count} 组覆盖，其中 {presentation.Definite} 组有明确差异"
                + (HasWarnings ? $"（{Warnings.Count} 项未能读取，检测不完整）" : "");

            ScanCompleted?.Invoke(this, Groups.Count);
        }
        catch (OperationCanceledException)
        {
            if (revision == _revision) SummaryText = "检测已取消，请重新检查";
        }
        catch (Exception ex)
        {
            if (_scanCts?.Token == cancellationToken && revision == _revision)
            {
                HasError = true;
                ErrorMessage = $"冲突检测失败: {ex.Message}";
                _notificationService.ShowError(ErrorMessage, "冲突");
            }
        }
        finally
        {
            if (_scanCts == scanCts)
            {
                _scanCts = null;
                IsLoading = false;
                _automaticScanActive = false;
            }
        }
    }

    [RelayCommand]
    private void CancelScan()
    {
        _automaticCts?.Cancel();
        _scanCts?.Cancel();
    }

    public async Task StopScanAsync()
    {
        // 关闭时先禁用调度，避免延迟任务在退出过程中重新启动扫描。
        _automaticChecking = false;
        _automaticCts?.Cancel();
        _scanCts?.Cancel();
        if (ScanCommand.ExecutionTask is { } pending) await pending;
        if (_automaticTask is { } automatic) await automatic;
    }
}

public class ConflictGroupViewModel
{
    public string RelativePath { get; }
    public string ResourceTypeName { get; }
    public string TypeIdText { get; }
    public string StatusText { get; }
    public bool IsDefiniteConflict { get; }
    public int ModCount { get; }
    public IReadOnlyList<ConflictOwnerViewModel> Owners { get; }

    public ConflictGroupViewModel(ConflictGroup group)
    {
        RelativePath = group.RelativePath;
        ResourceTypeName = group.ResourceTypeName;
        TypeIdText = $"Type ID：0x{group.TypeId:X16}";
        IsDefiniteConflict = group.IsDefiniteConflict;
        StatusText = IsDefiniteConflict ? "明确差异" : "潜在覆盖（元数据相同，内容仍可能不同）";
        ModCount = group.ModCount;
        var owners = group.Owners
            .Select(owner => new ConflictOwnerViewModel(owner))
            .ToList();

        // 服务已按实际部署和补丁顺序排列；最后一项预期生效。
        if (owners.Count > 0)
        {
            owners[^1].MarkAsWinner();
        }

        Owners = owners;
    }
}

public class ConflictOwnerViewModel
{
    public Guid ModId { get; }
    public string ModName { get; }
    public string FullPath { get; }
    public string OrderText { get; }
    public string SizeText { get; }
    public string PatchText { get; }
    public string VersionText { get; }
    public bool IsWinner { get; private set; }

    public ConflictOwnerViewModel(ConflictFileOwner owner)
    {
        ModId = owner.ModId;
        ModName = owner.ModName;
        FullPath = owner.FullPath;
        OrderText = $"部署顺序 #{owner.ModOrder + 1}";
        SizeText = $"主数据 {FormatSize(owner.Size)} / Stream {FormatSize(owner.StreamSize)} / GPU {FormatSize(owner.GpuSize)}";
        PatchText = $"{System.IO.Path.GetFileName(owner.FullPath)} → {owner.TargetFileName}";
        VersionText = owner.UnitVersion is { } version ? $"Unit 版本 {version}" : string.Empty;
    }

    public void MarkAsWinner() => IsWinner = true;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824.0:0.#} GB",
        >= 1048576 => $"{bytes / 1048576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        > 0 => $"{bytes} B",
        _ => "0 B"
    };
}
