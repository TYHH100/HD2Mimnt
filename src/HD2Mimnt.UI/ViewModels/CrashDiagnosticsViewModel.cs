using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public partial class CrashDiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly CrashDiagnosticsService _diagnostics;
    private readonly CrashDeploymentHistoryService _history;
    private readonly GameDetectionService _game;
    private readonly UiLocalization _localization;
    private CancellationTokenSource? _cancellation;
    private CrashDiagnosisReport? _report;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public ObservableCollection<CrashDumpFile> Dumps { get; } = [];
    public ObservableCollection<CrashDeploymentSnapshot> History { get; } = [];
    public ObservableCollection<string> ClueLines { get; } = [];
    public ObservableCollection<string> ChangeLines { get; } = [];
    public ObservableCollection<string> FrameLines { get; } = [];

    [ObservableProperty] private CrashDumpFile? _selectedDump;
    [ObservableProperty] private CrashDeploymentSnapshot? _selectedHistory;
    [ObservableProperty] private string _dumpPath = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _crashDetails = string.Empty;
    [ObservableProperty] private string _comparisonDetails = string.Empty;
    [ObservableProperty] private string _contextWarning = string.Empty;
    [ObservableProperty] private string _modulesText = string.Empty;

    public bool CanEdit => !IsBusy;
    public bool ToolAvailable => _diagnostics.IsAvailable;
    public bool HasReport => _report != null;
    public bool HasNoClues => ClueLines.Count == 0;
    public bool HasNoChanges => ChangeLines.Count == 0;

    public CrashDiagnosticsViewModel(CrashDiagnosticsService diagnostics, CrashDeploymentHistoryService history,
        GameDetectionService game, UiLocalization localization)
    {
        _diagnostics = diagnostics; _history = history; _game = game; _localization = localization;
        _localization.PropertyChanged += OnLanguageChanged;
    }

    partial void OnSelectedDumpChanged(CrashDumpFile? value) { if (value != null) DumpPath = value.Path; }
    partial void OnDumpPathChanged(string value)
    {
        if (SelectedDump != null && !SelectedDump.Path.Equals(value, StringComparison.OrdinalIgnoreCase)) SelectedDump = null;
        _report = null; RefreshReport(); AnalyzeCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedHistoryChanged(CrashDeploymentSnapshot? value) => MarkKnownGoodCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); RefreshCommand.NotifyCanExecuteChanged(); AnalyzeCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); MarkKnownGoodCommand.NotifyCanExecuteChanged();
    }
    private bool CanStart() => !IsBusy;
    private bool CanAnalyze() => !IsBusy && ToolAvailable && !string.IsNullOrWhiteSpace(DumpPath);
    private bool CanCancel() => IsBusy && _cancellation?.IsCancellationRequested == false;
    private bool CanMarkKnownGood() => !IsBusy && SelectedHistory is { Status: "Completed", KnownGood: false };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RefreshAsync() => OperateAsync(async token =>
    {
        var dumps = await _diagnostics.FindDumpsAsync(_game.CurrentGameInfo?.Path, token);
        var history = await _history.GetRecentAsync(token);
        Dumps.Clear(); foreach (var dump in dumps) Dumps.Add(dump);
        ReplaceHistory(history);
        if (string.IsNullOrWhiteSpace(DumpPath)) SelectedDump = Dumps.FirstOrDefault();
        StatusMessage = _localization.Format("Crash.Found", Dumps.Count, History.Count);
    });

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => OperateAsync(async token =>
    {
        _report = null; RefreshReport();
        _report = await _diagnostics.AnalyzeAsync(DumpPath.Trim(), _game.CurrentGameInfo?.Path, token);
        RefreshReport(); StatusMessage = _localization["Crash.Complete"];
    });

    [RelayCommand(CanExecute = nameof(CanMarkKnownGood))]
    private Task MarkKnownGoodAsync() => OperateAsync(async token =>
    {
        await _history.MarkKnownGoodAsync(SelectedHistory!.SessionId, token);
        ReplaceHistory(await _history.GetRecentAsync(token));
        // 基准改变后清除旧结论，下一次分析使用新的历史标记。
        _report = null; RefreshReport(); StatusMessage = _localization["Crash.MarkedGood"];
    });

    public Task ExportAsync(string path)
    {
        var report = _report;
        return report == null || IsBusy ? Task.CompletedTask : OperateAsync(async token =>
        {
            await _diagnostics.ExportAsync(report, path, token);
            StatusMessage = _localization.Format("Crash.Exported", path);
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _cancellation?.Cancel(); CancelCommand.NotifyCanExecuteChanged(); }
    public async Task CancelAndWaitAsync() { _cancellation?.Cancel(); await Completion; }

    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        IsBusy = true; StatusMessage = _localization["Crash.Working"];
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { StatusMessage = _localization["Crash.Cancelled"]; }
        catch (Exception error) { StatusMessage = _localization.Format("Crash.Failed", error.Message); }
        finally { _cancellation = null; IsBusy = false; completion.TrySetResult(); }
    }

    private void ReplaceHistory(IReadOnlyList<CrashDeploymentSnapshot> values)
    {
        var selected = SelectedHistory?.SessionId;
        History.Clear(); foreach (var snapshot in values) History.Add(snapshot);
        SelectedHistory = History.FirstOrDefault(snapshot => snapshot.SessionId == selected) ?? History.FirstOrDefault();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(UiLocalization.Revision)) RefreshReport(); }

    private void RefreshReport()
    {
        ClueLines.Clear(); ChangeLines.Clear(); FrameLines.Clear();
        Summary = CrashDetails = ComparisonDetails = ContextWarning = ModulesText = string.Empty;
        if (_report is { } report)
        {
            var analysis = report.Analysis;
            Summary = _localization[!report.IsHelldiversDump ? "Crash.NotGame" : report.Assessment == CrashAssessment.ModClues ? "Crash.ModClues" : "Crash.Inconclusive"];
            CrashDetails = _localization.Format("Crash.Details", Unknown(analysis.Reason), Unknown(analysis.Address),
                Unknown(analysis.MainModule), analysis.CapturedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? _localization["Crash.Unknown"]);
            foreach (var clue in report.Clues)
            {
                var line = _localization.Format("Crash." + clue.Kind, clue.Module, string.Join("、", clue.ModNames));
                if (clue.Ambiguous) line += " " + _localization["Crash.Ambiguous"];
                if (clue.ApproximateStack) line += " " + _localization["Crash.ScanFrame"];
                ClueLines.Add(line);
            }
            var warnings = new List<string>();
            if (analysis.Frames.Count == 0 || analysis.Frames.Any(frame => string.IsNullOrWhiteSpace(frame.Function))) warnings.Add(_localization["Crash.MissingSymbols"]);
            if (report.DeploymentAfterDump) warnings.Add(_localization["Crash.LaterDeployment"]);
            if (analysis.CapturedUtc == null) warnings.Add(_localization["Crash.NoTimestamp"]);
            if (report.ContextError != null) warnings.Add(_localization.Format("Crash.ContextFailed", report.ContextError));
            if (report.Comparison is { } comparison)
            {
                var statusKey = "Crash.Status" + comparison.Deployment.Status;
                var status = _localization[statusKey];
                ComparisonDetails = _localization.Format("Crash.Deployment", comparison.Deployment.Display,
                    status == statusKey ? comparison.Deployment.Status : status);
                if (comparison.Baseline is { } baseline)
                {
                    ComparisonDetails += "\n" + _localization.Format(baseline.KnownGood ? "Crash.GoodBaseline" : "Crash.PreviousBaseline", baseline.Display);
                    if (!baseline.KnownGood) warnings.Add(_localization["Crash.UnverifiedBaseline"]);
                    foreach (var change in comparison.Changes)
                        ChangeLines.Add(_localization.Format("Crash.Change", change.Name,
                            string.Join("、", change.Kinds.Select(kind => _localization["Crash.Change" + kind]))));
                }
                else warnings.Add(_localization["Crash.NoBaseline"]);
                if (comparison.Deployment.Status != "Completed") warnings.Add(_localization["Crash.IncompleteDeployment"]);
            }
            else ComparisonDetails = _localization["Crash.NoHistory"];
            ContextWarning = string.Join("\n", warnings);
            foreach (var frame in analysis.Frames)
                FrameLines.Add($"#{frame.Index}  {Unknown(frame.Module)} + {Unknown(frame.ModuleOffset)}  {frame.Function}  [{Unknown(frame.Trust)}]");
            ModulesText = string.Join("\n", analysis.Modules.Select(module => $"{module.Name}  {module.Version}  {module.BaseAddress}–{module.EndAddress}"));
        }
        OnPropertyChanged(nameof(HasReport)); OnPropertyChanged(nameof(HasNoClues)); OnPropertyChanged(nameof(HasNoChanges));
    }

    private string Unknown(string value) => string.IsNullOrWhiteSpace(value) ? _localization["Crash.Unknown"] : value;
    public void Dispose() { _cancellation?.Cancel(); _localization.PropertyChanged -= OnLanguageChanged; }
}
