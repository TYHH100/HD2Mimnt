using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 冲突页：对当前 Profile 的启用模组执行文件路径重叠检测并展示结果
/// </summary>
public partial class ConflictsViewModel : ObservableObject
{
    private readonly ConflictDetectionService _conflictDetectionService;
    private readonly ProfileService _profileService;
    private readonly NotificationService _notificationService;

    private CancellationTokenSource? _scanCts;

    /// <summary>扫描完成（含结果数量），供总览页统计回填。</summary>
    public event EventHandler<int>? ScanCompleted;

    [ObservableProperty]
    private ObservableCollection<ConflictGroupViewModel> _groups = new();

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

        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var profile = await _profileService.GetActiveProfileAsync(cancellationToken);
            if (profile == null)
            {
                HasProfile = false;
                Groups = new ObservableCollection<ConflictGroupViewModel>();
                return;
            }

            HasProfile = true;
            var result = await _conflictDetectionService.ScanProfileAsync(profile.Id, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            Groups = new ObservableCollection<ConflictGroupViewModel>(
                result.Groups.Select(group => new ConflictGroupViewModel(group)));
            HasScanned = true;
            SummaryText = result.SkippedModCount > 0
                ? $"已扫描 {result.ScannedModCount} 个启用模组，发现 {Groups.Count} 组文件冲突（{result.SkippedModCount} 个模组目录缺失或不可访问，已跳过）"
                : $"已扫描 {result.ScannedModCount} 个启用模组，发现 {Groups.Count} 组文件冲突";

            ScanCompleted?.Invoke(this, Groups.Count);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (_scanCts?.Token == cancellationToken)
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
    public IReadOnlyList<ConflictOwnerViewModel> Owners { get; }

    public ConflictGroupViewModel(ConflictGroup group)
    {
        RelativePath = group.RelativePath;
        var owners = group.Owners
            .Select(owner => new ConflictOwnerViewModel(owner))
            .ToList();

        // 部署顺序最大的模组实际覆盖文件、最终生效
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
    public bool IsWinner { get; private set; }

    public ConflictOwnerViewModel(ConflictFileOwner owner)
    {
        ModId = owner.ModId;
        ModName = owner.ModName;
        FullPath = owner.FullPath;
        OrderText = $"加载顺序 #{owner.ModOrder}";
        SizeText = FormatSize(owner.Size);
    }

    public void MarkAsWinner() => IsWinner = true;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1073741824 => $"{bytes / 1073741824.0:0.#} GB",
        >= 1048576 => $"{bytes / 1048576.0:0.#} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        > 0 => $"{bytes} B",
        _ => "未知大小"
    };
}
