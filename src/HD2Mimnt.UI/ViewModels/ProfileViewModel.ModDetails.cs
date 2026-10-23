using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Enums;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileViewModel
{
    private readonly DeploymentService? _deploymentService;
    public ModVersionsViewModel LocalVersions { get; }
    public event Func<Task>? ModContentChanged;
    private CancellationTokenSource? _detailLoadCts;
    private Task _detailLoadTask = Task.CompletedTask;

    public event EventHandler<ModItemViewModel>? ModUpdatesRequested;
    public event Func<Task>? ModDeleted;
    public Func<ModItemViewModel, Guid, Task>? AddToOtherProfilesAsync { get; set; }

    [RelayCommand]
    private async Task AddModToOtherProfilesAsync(ProfileModViewModel? mod)
    {
        if (mod is not { IsSeparator: false, SourceInfo: { } source }
            || !ProfileMods.Contains(mod) || SelectedProfile == null || AddToOtherProfilesAsync == null) return;
        await AddToOtherProfilesAsync(source, SelectedProfile.Id);
    }

    [ObservableProperty]
    private ModItemViewModel? _selectedModDetail;

    [ObservableProperty]
    private bool _isDetailPanelOpen;

    [RelayCommand]
    private void ShowModDetail(ProfileModViewModel? profileMod)
    {
        if (LocalVersions.IsBusy) return;
        if (profileMod is not { IsSeparator: false, SourceInfo: { } detail }
            || !ProfileMods.Contains(profileMod)) return;
        CloseDetailPanel();
        detail.CheckCompatibilityCommand = Compatibility == null ? null : new AsyncRelayCommand(() => Compatibility.OpenAsync([detail], detail.Name));
        detail.LocalVersions = LocalVersions;
        LocalVersions.Open(detail);
        detail.IsInCurrentProfile = true;
        detail.IsEnabled = profileMod.Enabled;
        detail.Order = profileMod.Order;
        detail.Icon = profileMod.Icon;
        detail.ToggleEnabledCommand = new RelayCommand(async () =>
        {
            if (!ProfileMods.Contains(profileMod)) return;
            await ToggleModEnabledAsync(profileMod);
            detail.IsEnabled = profileMod.Enabled;
        });
        detail.RemoveFromProfileCommand = new RelayCommand(async () =>
        {
            if (!ProfileMods.Contains(profileMod)) return;
            await RemoveModFromProfileAsync(profileMod);
            if (!ProfileMods.Contains(profileMod)) CloseDetailPanel();
        });
        detail.DeleteCommand = new AsyncRelayCommand(() => DeleteModAsync(profileMod));
        detail.CloseDetailPanelCommand = CloseDetailPanelCommand;
        detail.CheckUpdatesCommand = new AsyncRelayCommand(() =>
        {
            CloseDetailPanel();
            ModUpdatesRequested?.Invoke(this, detail);
            return Task.CompletedTask;
        });
        SelectedModDetail = detail;
        IsDetailPanelOpen = true;
        var cancellation = new CancellationTokenSource();
        _detailLoadCts = cancellation;
        _detailLoadTask = LoadModDetailAsync(profileMod, detail, cancellation);
    }

    private async Task LoadModDetailAsync(ProfileModViewModel profileMod, ModItemViewModel detail,
        CancellationTokenSource cancellation)
    {
        detail.HashError = null;
        detail.IsCalculatingHash = string.IsNullOrWhiteSpace(detail.Hash);
        try
        {
            await profileMod.LoadIconAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            detail.Icon = profileMod.Icon;
            if (_deploymentService != null)
            {
                var deployments = await _deploymentService.GetLatestDeploymentsAsync(cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                detail.IsDeployed = deployments.Any(record => record.ModId == detail.Id
                    && Enum.TryParse<DeploymentStatus>(record.Status, out var status)
                    && status is DeploymentStatus.Completed or DeploymentStatus.PartiallyFailed);
            }
            if (string.IsNullOrWhiteSpace(detail.Hash))
            {
                var hash = await _modService.EnsureModHashAsync(detail.Id, cancellation.Token);
                if (!cancellation.IsCancellationRequested) detail.SetHash(hash);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested) detail.HashError = $"读取详情失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_detailLoadCts, cancellation))
            {
                detail.IsCalculatingHash = false;
                _detailLoadCts = null;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private void CloseDetailPanel()
    {
        LocalVersions.Cancel();
        _detailLoadCts?.Cancel();
        if (SelectedModDetail != null) SelectedModDetail.IsCalculatingHash = false;
        SelectedModDetail = null;
        IsDetailPanelOpen = false;
    }

    public async Task CloseModDetailAsync()
    {
        CloseDetailPanel();
        await _detailLoadTask;
        await LocalVersions.StopAsync();
    }

    [RelayCommand]
    private async Task DeleteModAsync(ProfileModViewModel? profileMod)
    {
        if (profileMod is not { IsSeparator: false } || !ProfileMods.Contains(profileMod)) return;
        if (!await _confirmationService.ConfirmAsync("删除模组", $"确定删除“{profileMod.ModName}”吗？此操作会删除模组文件。")) return;
        try
        {
            await _modService.DeleteModAsync(profileMod.ModId);
            CloseDetailPanel();
            if (ModDeleted != null) await ModDeleted();
            _notificationService.ShowSuccess($"已删除 {profileMod.ModName}", "删除模组");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"删除失败: {ex.Message}", "错误");
        }
    }
}
