using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SoundFixViewModel : ObservableObject, IDisposable
{
    private readonly SoundFixService _service;
    private readonly IConfirmationService _confirmation;
    private readonly UiLocalization _localization;
    private readonly IBrowserLauncher _browser;
    private CancellationTokenSource? _cancellation;
    private string _statusKey = "SoundFix.Ready";
    private object[] _statusArguments = [];
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private SoundFixModItemViewModel? _selectedMod;
    public ObservableCollection<SoundFixModItemViewModel> Mods { get; } = [];
    public Task Completion { get; private set; } = Task.CompletedTask;
    public bool CanEdit => !IsBusy;
    public string StatusMessage => _localization.Format(_statusKey, _statusArguments);

    public SoundFixViewModel(SoundFixService service, IConfirmationService confirmation, UiLocalization localization, IBrowserLauncher browser)
    {
        _service = service; _confirmation = confirmation; _localization = localization; _browser = browser;
        _localization.PropertyChanged += OnLanguageChanged;
    }

    private bool CanStart() => !IsBusy;
    private bool CanRepair() => !IsBusy && SelectedMod?.Report.CanRepair == true;
    private bool CanRestore() => !IsBusy && SelectedMod?.HasBackup == true;
    partial void OnSelectedModChanged(SoundFixModItemViewModel? value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); RefreshCommands(); }
    private void RefreshCommands()
    {
        ScanCommand.NotifyCanExecuteChanged(); RepairCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task ScanAsync() => OperateAsync(async token =>
    {
        Mods.Clear(); SelectedMod = null;
        var progress = new Progress<SoundFixProgress>(p =>
        {
            if (_cancellation?.Token == token && !token.IsCancellationRequested)
                SetStatus("SoundFix.Progress", p.Completed, p.Total, p.ModName);
        });
        var reports = await _service.ScanLibraryAsync(progress, token);
        foreach (var report in reports)
            Mods.Add(new(report, await _service.HasBackupAsync(report.ModId, token), _localization));
        SelectedMod = Mods.FirstOrDefault();
        SetStatus("SoundFix.Scanned", reports.Count, reports.Sum(r => r.Banks.Count(b => b.State == SoundFixBankState.Repairable)));
    });

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private Task RepairAsync() => MutateAsync(false);

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private Task RestoreAsync() => MutateAsync(true);

    private async Task MutateAsync(bool restore)
    {
        if (SelectedMod is not { } selected || IsBusy) return;
        // 在显示确认框前进入忙碌状态，避免重复点击或选择另一个模组改变本次目标。
        await OperateAsync(async token =>
        {
            if (!await _confirmation.ConfirmAsync(_localization[restore ? "SoundFix.Restore" : "SoundFix.Repair"],
                _localization.Format(restore ? "SoundFix.RestoreConfirm" : "SoundFix.RepairConfirm", selected.Report.Name)).WaitAsync(token))
            { SetStatus("SoundFix.Cancelled"); return; }
            token.ThrowIfCancellationRequested();
            var result = restore ? await _service.RestoreAsync(selected.Report.ModId, token) : await _service.RepairAsync(selected.Report, token);
            // 内容已提交；旧扫描不能再次用于修复。恢复入口仍保留，重新扫描刷新诊断。
            var index = Mods.IndexOf(selected);
            var changed = new SoundFixModReport { ModId = selected.Report.ModId, Name = selected.Report.Name,
                ModPath = selected.Report.ModPath };
            var replacement = new SoundFixModItemViewModel(changed, await _service.HasBackupAsync(changed.ModId), _localization, needsRescan: true);
            if (index >= 0) Mods[index] = replacement;
            SelectedMod = replacement;
            SetStatus(result.Changed ? restore ? "SoundFix.Restored" : "SoundFix.Repaired" : "SoundFix.NoChanges");
        });
    }

    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; IsBusy = true; SetStatus("SoundFix.Working");
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { SetStatus("SoundFix.Cancelled"); }
        catch (Exception ex) { SetStatus("SoundFix.Failed", ex.Message); }
        finally { _cancellation = null; IsBusy = false; completion.TrySetResult(); }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Cancel() => _cancellation?.Cancel();
    public async Task CancelAndWaitAsync() { _cancellation?.Cancel(); await Completion; }

    [RelayCommand]
    private void OpenAuthor()
    {
        try { _browser.Open(SoundFixService.AuthorUrl); }
        catch (Exception ex) { SetStatus("SoundFix.Failed", ex.Message); }
    }

    private void SetStatus(string key, params object[] arguments)
    { _statusKey = key; _statusArguments = arguments; OnPropertyChanged(nameof(StatusMessage)); }
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(UiLocalization.Revision)) return;
        OnPropertyChanged(nameof(StatusMessage));
        foreach (var mod in Mods) mod.RefreshLanguage();
    }
    public void Dispose() { _localization.PropertyChanged -= OnLanguageChanged; _cancellation?.Cancel(); }
}

public sealed class SoundFixModItemViewModel(SoundFixModReport report, bool hasBackup, UiLocalization localization, bool needsRescan = false) : ObservableObject
{
    public SoundFixModReport Report { get; } = report;
    public bool HasBackup { get; } = hasBackup;
    public string Summary => localization.Format("SoundFix.ModSummary", Report.Banks.Count,
        Report.Banks.Count(b => b.State == SoundFixBankState.Repairable), Report.Errors.Count);
    public string Details => needsRescan ? localization["SoundFix.Rescan"] : string.Join("\n", Report.Banks.Select(b =>
        localization.Format("SoundFix.BankLine", b.Name, b.PatchPath, localization["SoundFix.State." + b.State],
            b.MissingEntries, b.MissingMedia, b.CustomEntries, b.CustomMedia)
        + (b.Error is null ? "" : "\n" + localization.Translate(b.Error))).Concat(Report.Errors.Select(error => localization.Translate(error))));
    public void RefreshLanguage() { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Details)); }
}
