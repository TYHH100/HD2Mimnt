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
        HasScanned = false;
        HasError = false;
        ErrorMessage = string.Empty;
        SummaryText = "配置或模组已更新，请检查冲突";
        Groups = new();
        Warnings = new();
        RefreshStateProperties();
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
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsLoading) return;

        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var cancellationToken = _scanCts.Token;
        var revision = _revision;

        IsLoading = true;
        HasScanned = false;
        HasError = false;
        ErrorMessage = string.Empty;
        Groups = new();
        Warnings = new();
        SummaryText = "正在读取当前配置的补丁资源…";
        try
        {
            var profile = _targetProfileId is { } targetId
                ? await _profileService.GetProfileByIdAsync(targetId, cancellationToken)
                : await _profileService.GetActiveProfileAsync(cancellationToken);
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
            var result = await _conflictDetectionService.ScanProfileAsync(profile.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _revision) return;

            Groups = new ObservableCollection<ConflictGroupViewModel>(
                result.Groups.Select(group => new ConflictGroupViewModel(group)));
            HasScanned = true;
            Warnings = new(result.Warnings);
            SummaryText = $"已扫描 {result.ScannedModCount} 个启用模组、{result.ScannedPatchCount} 个补丁、{result.ScannedResourceCount} 条资源；"
                + $"发现 {Groups.Count} 组覆盖，其中 {result.DefiniteConflictCount} 组有明确差异"
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
            if (_scanCts == null || _scanCts.Token == cancellationToken)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();
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
