using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Migration;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public sealed record MigrationManagerChoice(ExternalManagerKind Kind, string Name);

public partial class ManagerMigrationViewModel : ObservableObject
{
    private readonly ExternalManagerMigrationService _import;
    private readonly ExternalManagerExportService _export;
    private readonly SettingsService _settings;
    private ExternalMigrationPlan? _plan;
    private CancellationTokenSource? _cancellation;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public Func<Task>? RefreshImportedDataAsync { get; set; }
    public MigrationManagerChoice[] Managers { get; } = [new(ExternalManagerKind.Helldivers2ModManager, "Helldivers2ModManager"),
        new(ExternalManagerKind.Echelon, "Echelon"), new(ExternalManagerKind.HD2Arsenal, "HD2Arsenal")];
    public ObservableCollection<ExternalMigrationIssue> Issues { get; } = [];

    [ObservableProperty] private MigrationManagerChoice? _selectedManager;
    [ObservableProperty] private bool _isExport;
    [ObservableProperty] private string _dataPath = string.Empty;
    [ObservableProperty] private string _installationPath = string.Empty;
    [ObservableProperty] private string _modsPath = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private double _progressValue;
    public bool CanEdit => !IsBusy;
    public bool IsHd2MM => SelectedManager?.Kind == ExternalManagerKind.Helldivers2ModManager;

    public ManagerMigrationViewModel(ExternalManagerMigrationService import, ExternalManagerExportService export, SettingsService settings)
    {
        _import = import; _export = export; _settings = settings;
        SelectedManager = Managers[0];
    }

    partial void OnSelectedManagerChanged(MigrationManagerChoice? value)
    {
        if (value == null) return;
        var prefix = "Migration." + value.Kind + ".";
        DataPath = _settings.GetSetting(prefix + "DataPath", value.Kind switch
        {
            ExternalManagerKind.Echelon => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Echelon"),
            ExternalManagerKind.HD2Arsenal => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hd2arsenal"),
            _ => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HD2ModManager", "Mods")
        });
        InstallationPath = _settings.GetSetting(prefix + "InstallationPath", "");
        ModsPath = _settings.GetSetting(prefix + "ModsPath", "");
        OnPropertyChanged(nameof(IsHd2MM)); InvalidatePlan();
    }
    partial void OnDataPathChanged(string value) => InvalidatePlan();
    partial void OnInstallationPathChanged(string value) => InvalidatePlan();
    partial void OnModsPathChanged(string value) => InvalidatePlan();
    partial void OnIsExportChanged(bool value) => InvalidatePlan();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); PreviewCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }
    private void InvalidatePlan() { _plan = null; Summary = ""; Issues.Clear(); PreviewCommand.NotifyCanExecuteChanged(); RunCommand.NotifyCanExecuteChanged(); }
    private bool CanStart() => !IsBusy && SelectedManager != null && !string.IsNullOrWhiteSpace(DataPath)
        && (!IsHd2MM || !string.IsNullOrWhiteSpace(InstallationPath));
    private bool CanCancel() => IsBusy && _cancellation?.IsCancellationRequested == false;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task PreviewAsync() => OperateAsync(false);
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RunAsync() => OperateAsync(true);
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _cancellation?.Cancel(); CancelCommand.NotifyCanExecuteChanged(); }

    public async Task CancelAndWaitAsync() { _cancellation?.Cancel(); await Completion; }

    private async Task OperateAsync(bool execute)
    {
        if (!CanStart()) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        IsBusy = true; ProgressValue = 0; StatusMessage = UiLocalization.Current["Migration.Scanning"];
        var importStarted = false;
        try
        {
            var source = new ExternalMigrationSource(SelectedManager!.Kind, DataPath.Trim(),
                string.IsNullOrWhiteSpace(InstallationPath) ? null : InstallationPath.Trim(), string.IsNullOrWhiteSpace(ModsPath) ? null : ModsPath.Trim());
            if (!execute || _plan == null)
                _plan = IsExport ? await _export.PreviewAsync(source, cancellation.Token) : await _import.PreviewAsync(source, cancellation.Token);
            Issues.Clear(); foreach (var issue in _plan.Issues) Issues.Add(issue);
            Summary = UiLocalization.Current.Format("Migration.PreviewSummary", _plan.Mods.Count, _plan.Profiles.Count,
                _plan.Profiles.Sum(profile => profile.Separators.Count + (string.IsNullOrWhiteSpace(profile.LuaSeparatorColor) ? 0 : 1)), _plan.Issues.Count);
            if (!execute || _plan.Issues.Any(issue => issue.IsBlocking))
            { StatusMessage = UiLocalization.Current[_plan.Issues.Any(issue => issue.IsBlocking) ? "Migration.Blocked" : "Migration.PreviewReady"]; return; }
            var prefix = "Migration." + source.Manager + ".";
            await _settings.SetSettingAsync(prefix + "DataPath", DataPath);
            await _settings.SetSettingAsync(prefix + "InstallationPath", InstallationPath);
            await _settings.SetSettingAsync(prefix + "ModsPath", ModsPath);
            var progress = new Progress<ExternalMigrationProgress>(value =>
            {
                if (!IsBusy || _cancellation != cancellation) return;
                ProgressValue = value.Total == 0 ? 0 : 100.0 * value.Completed / value.Total;
                StatusMessage = $"{UiLocalization.Current.Translate(value.Stage)} · {value.Item}";
            });
            importStarted = !IsExport;
            var result = IsExport ? await _export.MigrateAsync(_plan, progress, cancellation.Token) : await _import.MigrateAsync(_plan, progress, cancellation.Token);
            if (importStarted && RefreshImportedDataAsync != null)
            {
                await RefreshImportedDataAsync();
                importStarted = false;
            }
            Issues.Clear(); foreach (var issue in result.Issues) Issues.Add(issue);
            ProgressValue = 100;
            StatusMessage = UiLocalization.Current.Format("Migration.Completed", result.ImportedMods, result.ImportedProfiles,
                result.ReusedMods + result.ReusedProfiles, result.Issues.Count);
            if (result.BackupPaths.Count > 0) Summary += Environment.NewLine + UiLocalization.Current.Format("Migration.BackupsSaved", string.Join(Environment.NewLine, result.BackupPaths));
            _plan = null;
        }
        catch (OperationCanceledException) { StatusMessage = UiLocalization.Current["Migration.Cancelled"]; _plan = null; }
        catch (Exception ex) { StatusMessage = UiLocalization.Current.Format("Migration.Failed", ex.Message); _plan = null; }
        finally
        {
            try
            {
                // 部分提交后取消也要让库页看到已经保存的模组。
                if (importStarted && RefreshImportedDataAsync != null) await RefreshImportedDataAsync();
            }
            finally { _cancellation = null; IsBusy = false; completion.TrySetResult(); }
        }
    }
}
