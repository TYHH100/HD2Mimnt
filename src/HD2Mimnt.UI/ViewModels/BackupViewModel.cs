using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Services;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class BackupViewModel : ObservableObject
{
    private readonly BackupService _backupService;
    private readonly IConfirmationService _confirmationService;
    private readonly NotificationService _notificationService;

    [ObservableProperty] private ObservableCollection<BackupInfo> _backups = new();
    [ObservableProperty] private BackupInfo? _selectedBackup;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public Func<Task>? RefreshAfterRestoreAsync { get; set; }

    public BackupViewModel(BackupService backupService, IConfirmationService confirmationService, NotificationService notificationService)
    {
        _backupService = backupService;
        _confirmationService = confirmationService;
        _notificationService = notificationService;
    }

    public async Task InitializeAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            Backups = new ObservableCollection<BackupInfo>(await _backupService.ListBackupsAsync());
            StatusMessage = $"共有 {Backups.Count} 个备份";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取备份失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        IsLoading = true;
        try
        {
            var backup = await _backupService.CreateBackupAsync();
            Backups.Insert(0, backup);
            SelectedBackup = backup;
            StatusMessage = $"备份已创建：{backup.Name}";
            _notificationService.ShowSuccess("数据备份已创建", "备份");
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建备份失败：{ex.Message}";
            _notificationService.ShowError(StatusMessage, "备份");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(BackupInfo? backup)
    {
        if (backup == null || !await _confirmationService.ConfirmAsync("恢复备份", $"恢复“{backup.Name}”会覆盖当前模组库、配置文件和设置，并刷新当前页面。确定继续吗？")) return;
        IsLoading = true;
        var restored = false;
        try
        {
            await _backupService.RestoreBackupAsync(backup.FullPath);
            restored = true;
            if (RefreshAfterRestoreAsync != null) await RefreshAfterRestoreAsync();
            StatusMessage = "备份已恢复，页面数据已同步";
            _notificationService.ShowSuccess("备份已恢复", "备份");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = restored ? $"备份已恢复，但页面刷新失败：{ex.Message}" : $"恢复备份失败：{ex.Message}";
            _notificationService.ShowError(StatusMessage, "备份");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(BackupInfo? backup)
    {
        if (backup == null || !await _confirmationService.ConfirmAsync("删除备份", $"确定删除“{backup.Name}”吗？")) return;
        try
        {
            await _backupService.DeleteBackupAsync(backup.FullPath);
            Backups.Remove(backup);
            if (ReferenceEquals(SelectedBackup, backup)) SelectedBackup = null;
            StatusMessage = "备份已删除";
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除备份失败：{ex.Message}";
            _notificationService.ShowError(StatusMessage, "备份");
        }
    }
}
