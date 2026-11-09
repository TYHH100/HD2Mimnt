using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
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
        detail.SaveMetadataHandler = SaveModMetadataAsync;
        detail.IsEditingMetadata = false;
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

    /// <summary>详情面板内联编辑名称/描述：就地更新卡片，并抑制 ModsChanged 触发的整页重载。</summary>
    private async Task<bool> SaveModMetadataAsync(ModItemViewModel mod, string name, string description)
    {
        if (SelectedProfile == null || !ProfileMods.Any(row => row.ModId == mod.Id)) return false;
        if (string.IsNullOrWhiteSpace(name))
        {
            _notificationService.ShowWarning("模组名称不能为空", "编辑模组");
            return false;
        }
        try
        {
            Mod saved;
            _suppressProfilesChangedRefresh = true;
            try { saved = await _modService.UpdateModMetadataAsync(mod.Id, name: name, description: description); }
            finally { _suppressProfilesChangedRefresh = false; }
            mod.ApplyMetadata(saved);
            var row = ProfileMods.FirstOrDefault(row => row.ModId == mod.Id);
            if (row != null) row.ModName = saved.Name;
            ApplyModFilter();
            _notificationService.ShowSuccess("模组信息已更新", "编辑模组");
            return true;
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存失败: {ex.Message}", "编辑模组");
            return false;
        }
    }

    public async Task ChangeModImageAsync(ModItemViewModel mod, string imagePath)
    {
        if (mod.IsChangingImage || !ProfileMods.Any(row => row.ModId == mod.Id)) return;
        mod.IsChangingImage = true;
        try
        {
            string? thumbnailPath;
            _suppressProfilesChangedRefresh = true;
            try { thumbnailPath = await _modService.SetCustomThumbnailAsync(mod.Id, imagePath); }
            finally { _suppressProfilesChangedRefresh = false; }
            if (thumbnailPath == null)
            {
                _notificationService.ShowError("无法使用所选图片，请选择有效的 PNG、JPG 或 BMP 图片。", "更换图片");
                return;
            }
            await ApplyThumbnailAsync(mod, thumbnailPath);
            _notificationService.ShowSuccess("已更换模组图片", "更换图片");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"更换图片失败: {ex.Message}", "更换图片");
        }
        finally { mod.IsChangingImage = false; }
    }

    public async Task RestoreModImageAsync(ModItemViewModel mod)
    {
        if (mod.IsChangingImage || !ProfileMods.Any(row => row.ModId == mod.Id)) return;
        mod.IsChangingImage = true;
        try
        {
            string? thumbnailPath;
            _suppressProfilesChangedRefresh = true;
            try { thumbnailPath = await _modService.GenerateThumbnailAsync(mod.Id); }
            finally { _suppressProfilesChangedRefresh = false; }
            if (thumbnailPath == null)
            {
                _notificationService.ShowWarning("模组目录中没有可用图片，无法恢复默认图片。", "恢复默认图片");
                return;
            }
            await ApplyThumbnailAsync(mod, thumbnailPath);
            _notificationService.ShowSuccess("已恢复模组默认图片", "恢复默认图片");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"恢复默认图片失败: {ex.Message}", "恢复默认图片");
        }
        finally { mod.IsChangingImage = false; }
    }

    private async Task ApplyThumbnailAsync(ModItemViewModel mod, string thumbnailPath)
    {
        mod.SetThumbnailPath(thumbnailPath);
        var row = ProfileMods.FirstOrDefault(row => row.ModId == mod.Id);
        if (row == null)
        {
            await mod.ReloadIconAsync();
            return;
        }
        row.ThumbnailPath = thumbnailPath;
        // 卡片的 Icon 变更会同步回 SourceInfo（详情面板）。
        await row.ReloadIconAsync();
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
