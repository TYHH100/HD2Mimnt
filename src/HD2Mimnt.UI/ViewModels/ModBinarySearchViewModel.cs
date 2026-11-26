using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using TaskStatus = HD2Mimnt.Core.Entities.TaskStatus;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModBinarySearchViewModel : ObservableObject, IDisposable
{
    private readonly ModBinarySearchService _service;
    private readonly ProfileService _profileService;
    private readonly TaskService _taskService;
    private readonly SettingsService _settingsService;
    private readonly IConfirmationService _confirmation;
    private readonly UiLocalization _localization;
    private CancellationTokenSource? _operationCancellation;
    private Task _completion = Task.CompletedTask;
    private string _statusKey = "ModBinarySearch.Ready";
    private object?[] _statusArguments = [];
    private ModBinarySearchStartInfo? _startInfo;
    private bool _isHellForge;
    private bool _temporaryDeploymentAttempted;
    private bool _suspectDisabled;
    private string _officialGamePath = string.Empty;
    private DeploymentMethod _deploymentMethod = DeploymentMethod.Copy;
    private DeploymentOrder _deploymentOrder = DeploymentOrder.TopToBottom;

    public ObservableCollection<ModBinarySearchCandidate> DisabledCandidates { get; } = new();
    public ObservableCollection<string> ExcludedModNames { get; } = new();
    public ObservableCollection<ModBinarySearchHistoryItem> History { get; } = new();
    public Func<Task>? RefreshAfterDeploymentAsync { get; set; }
    public Func<Task>? RefreshAfterProfileChangeAsync { get; set; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private ModBinarySearchStage _stage = ModBinarySearchStage.Idle;
    [ObservableProperty] private string _sourceProfileName = string.Empty;
    [ObservableProperty] private string _temporaryProfileName = string.Empty;
    [ObservableProperty] private int _totalModCount;
    [ObservableProperty] private int _candidateCount;
    [ObservableProperty] private ModBinarySearchTest? _currentTest;
    [ObservableProperty] private ModBinarySearchResult? _result;
    [ObservableProperty] private string _progressMessage = string.Empty;

    public string StatusMessage => _localization.Format(_statusKey, _statusArguments);
    public string StepHeading => CurrentTest == null ? string.Empty : _localization.Format(
        CurrentTest.IsFinalVerification ? "ModBinarySearch.FinalHeading" : "ModBinarySearch.RoundHeading",
        CurrentTest.Round, CurrentTest.DisabledCandidates.Count, CurrentTest.EnabledCandidates.Count + CurrentTest.DisabledCandidates.Count);
    public string ResultMessage => Result == null ? string.Empty : Result.Confirmed
        ? _localization.Format("ModBinarySearch.ResultConfirmed", Result.Suspect?.Name)
        : _localization[Result.Cancelled ? "ModBinarySearch.ResultCancelled" : "ModBinarySearch.ResultNotIsolated"];
    public bool HasSession => Stage is not (ModBinarySearchStage.Idle or ModBinarySearchStage.Completed);
    public bool ShowStart => Stage is ModBinarySearchStage.Idle or ModBinarySearchStage.Completed;
    public bool ShowHistory => History.Count > 0;
    public bool ShowExcluded => ExcludedModNames.Count > 0;
    public bool ShowTest => CurrentTest != null && Stage is
        ModBinarySearchStage.ReadyToDeploy or ModBinarySearchStage.Deploying or
        ModBinarySearchStage.TestDeployFailed or ModBinarySearchStage.AwaitingObservation;
    public bool ShowResult => Result != null && Stage is
        ModBinarySearchStage.AwaitingCleanup or ModBinarySearchStage.CleanupFailed or ModBinarySearchStage.Completed;
    public bool CanDeploy => !IsBusy && CurrentTest != null && Stage is
        ModBinarySearchStage.ReadyToDeploy or ModBinarySearchStage.TestDeployFailed;
    public bool CanObserve => !IsBusy && Stage == ModBinarySearchStage.AwaitingObservation;
    public bool CanCancel => !IsBusy && HasSession && Stage is not
        (ModBinarySearchStage.Cleaning or ModBinarySearchStage.AwaitingCleanup or ModBinarySearchStage.Completed);
    public bool CanCleanup => !IsBusy && Stage is ModBinarySearchStage.AwaitingCleanup or ModBinarySearchStage.CleanupFailed;
    public bool CanDisableSuspect => !IsBusy && !_suspectDisabled && Stage == ModBinarySearchStage.Completed
        && Result?.Confirmed == true && Result.Suspect != null;
    public bool CanStart => !IsBusy && Stage is ModBinarySearchStage.Idle or ModBinarySearchStage.Completed;

    public ModBinarySearchViewModel(ModBinarySearchService service, ProfileService profileService,
        TaskService taskService, SettingsService settingsService, IConfirmationService confirmation,
        UiLocalization localization)
    {
        _service = service;
        _profileService = profileService;
        _taskService = taskService;
        _settingsService = settingsService;
        _confirmation = confirmation;
        _localization = localization;
        _localization.PropertyChanged += OnLanguageChanged;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (!await _confirmation.ConfirmAsync(_localization["ModBinarySearch.Title"],
                _localization["ModBinarySearch.StartConfirm"])) return;
        var source = await _profileService.GetActiveProfileAsync();
        if (source == null)
        {
            SetStatus("ModBinarySearch.NoSource");
            return;
        }
        var running = await _taskService.GetRunningTasksAsync();
        if (running.Any(task => task.Type is "Deployment" or "Cleanup" or "HellForgeSync"))
        {
            SetStatus("ModBinarySearch.BusyTask");
            return;
        }

        await OperateAsync(async token =>
        {
            ResetState();
            _isHellForge = _settingsService.GetGameInstallationMode() == GameInstallationMode.HellForge;
            _officialGamePath = _settingsService.GetEffectiveGamePath();
            if (!_isHellForge && (string.IsNullOrWhiteSpace(_officialGamePath) || !Directory.Exists(_officialGamePath)))
                throw new DirectoryNotFoundException("请先在设置中配置有效的游戏目录。");
            var methodValue = await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentMethod,
                DeploymentMethod.Copy.ToString());
            _deploymentMethod = Enum.TryParse<DeploymentMethod>(methodValue, true, out var parsedMethod) && Enum.IsDefined(parsedMethod)
                ? parsedMethod : DeploymentMethod.Copy;
            var orderValue = await _settingsService.GetSettingAsync(SettingKeys.DefaultDeploymentOrder,
                DeploymentOrder.TopToBottom.ToString());
            _deploymentOrder = orderValue == DeploymentOrder.BottomToTop.ToString()
                ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom;
            SetStatus("ModBinarySearch.Preparing");
            _startInfo = await _service.StartAsync(source.Id, token);
            ApplyStartInfo(_startInfo);
            Stage = ModBinarySearchStage.ReadyToDeploy;
            try
            {
                CurrentTest = await _service.PrepareFirstTestAsync(token);
            }
            catch
            {
                Stage = ModBinarySearchStage.ReadyToDeploy;
                throw;
            }
            SetStatus("ModBinarySearch.CloneReady");
        });
    }

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private Task DeployCurrentTestAsync() => OperateAsync(async token =>
    {
        if (CurrentTest == null || _startInfo == null) return;
        Stage = ModBinarySearchStage.Deploying;
        SetStatus("ModBinarySearch.Deploying");
        try
        {
            _temporaryDeploymentAttempted = true;
            await DeployProfileAsync(_startInfo.TemporaryProfileId, token);
            Stage = ModBinarySearchStage.AwaitingObservation;
            SetStatus("ModBinarySearch.Deployed");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Stage = ModBinarySearchStage.TestDeployFailed;
            SetStatus("ModBinarySearch.DeployCancelled");
        }
        catch (Exception ex)
        {
            Stage = ModBinarySearchStage.TestDeployFailed;
            SetStatus("ModBinarySearch.DeployFailed", ex.Message);
        }
    });

    [RelayCommand(CanExecute = nameof(CanObserve))]
    private Task ProblemRemainsAsync() => RecordObservationAsync(true);

    [RelayCommand(CanExecute = nameof(CanObserve))]
    private Task ProblemGoneAsync() => RecordObservationAsync(false);

    private async Task RecordObservationAsync(bool problemRemains)
    {
        if (CurrentTest == null) return;
        await OperateAsync(async token =>
        {
            SetStatus("ModBinarySearch.Preparing");
            var step = await _service.RecordObservationAsync(CurrentTest.Id, problemRemains, token);
            AddHistory(step.History.LastOrDefault());
            if (step.Result != null)
            {
                Result = step.Result;
                CurrentTest = null;
                Stage = ModBinarySearchStage.AwaitingCleanup;
                SetStatus("ModBinarySearch.CleanupHint");
                return;
            }
            CurrentTest = step.Test;
            Stage = ModBinarySearchStage.ReadyToDeploy;
            ReplaceDisabledCandidates(CurrentTest!.DisabledCandidates);
            SetStatus("ModBinarySearch.CloneReady");
        });
    }

    [RelayCommand(CanExecute = nameof(CanCleanup))]
    private Task FinishCleanupAsync() => CleanupAsync();

    [RelayCommand(CanExecute = nameof(CanDisableSuspect))]
    private async Task DisableSuspectAsync()
    {
        if (Result?.Suspect == null || _startInfo == null) return;
        var suspect = Result.Suspect;
        if (!await _confirmation.ConfirmAsync(_localization["ModBinarySearch.DisableSuspect"],
                _localization.Format("ModBinarySearch.DisableSuspectConfirm", suspect.Name))) return;
        await OperateAsync(async token =>
        {
            var source = await _profileService.GetProfileByIdAsync(_startInfo.SourceProfileId, token)
                ?? throw new InvalidOperationException("源配置已不存在。");
            var member = source.Mods.FirstOrDefault(item => item.ModId == suspect.ModId)
                ?? throw new InvalidOperationException("嫌疑模组已不在源配置中。");
            if (member.Enabled)
                await _profileService.SetModEnabledAsync(source.Id, suspect.ModId, false, token);
            _suspectDisabled = true;
            if (RefreshAfterProfileChangeAsync != null) await RefreshAfterProfileChangeAsync();
            SetStatus("ModBinarySearch.SuspectDisabled", suspect.Name);
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private async Task CancelAsync()
    {
        if (!await _confirmation.ConfirmAsync(_localization["ModBinarySearch.Cancel"],
                _localization["ModBinarySearch.CancelConfirm"])) return;
        await OperateAsync(async token =>
        {
            Result = await _service.CancelAsync(token);
            CurrentTest = null;
            Stage = ModBinarySearchStage.AwaitingCleanup;
            await CleanupCoreAsync(token);
        });
    }

    private async Task CleanupAsync()
    {
        if (_startInfo == null) return;
        if (!await _confirmation.ConfirmAsync(_localization["ModBinarySearch.FinishCleanup"],
                _localization["ModBinarySearch.CleanupConfirm"])) return;
        await OperateAsync(async token =>
        {
            Stage = ModBinarySearchStage.Cleaning;
            SetStatus("ModBinarySearch.Cleaning");
            await CleanupCoreAsync(token);
        });
    }

    private async Task CleanupCoreAsync(CancellationToken token)
    {
        try
        {
            if (_startInfo == null) throw new InvalidOperationException("临时配置信息已丢失。");
            await _service.EnsureSourceUnchangedAsync(token);
            if (_temporaryDeploymentAttempted)
                await DeployProfileAsync(_startInfo.SourceProfileId, token);
            await _service.DeleteTemporaryProfileAsync(_isHellForge ? null : _officialGamePath, token);
            Stage = ModBinarySearchStage.Completed;
            SetStatus("ModBinarySearch.Completed");
        }
        catch (Exception ex)
        {
            Stage = ModBinarySearchStage.CleanupFailed;
            SetStatus("ModBinarySearch.CleanupFailed", ex.Message);
        }
    }

    private async Task DeployProfileAsync(Guid profileId, CancellationToken token)
    {
        Dictionary<string, string> arguments;
        string type;
        if (_isHellForge)
        {
            type = "HellForgeSync";
            arguments = new Dictionary<string, string>
            {
                ["Mode"] = "Sync", ["ProfileId"] = profileId.ToString(), ["Order"] = _deploymentOrder.ToString()
            };
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_officialGamePath) || !Directory.Exists(_officialGamePath))
                throw new DirectoryNotFoundException("请先在设置中配置有效的游戏目录。");
            type = "Deployment";
            arguments = new Dictionary<string, string>
            {
                ["ProfileId"] = profileId.ToString(), ["Method"] = _deploymentMethod.ToString(),
                ["Order"] = _deploymentOrder.ToString(), ["GamePath"] = _officialGamePath
            };
        }

        var progress = new Progress<TaskProgress>(value =>
        {
            if (!string.IsNullOrWhiteSpace(value.Message)) ProgressMessage = value.Message;
        });
        var outcome = await _taskService.RunProcessTaskAsync(
            _localization["ModBinarySearch.TaskName"], type, arguments, progress, cancellationToken: token);
        if (outcome.Task?.Status != TaskStatus.Completed)
            throw new InvalidOperationException(outcome.Task?.Message
                ?? string.Join("；", outcome.Task?.Errors ?? []) ?? "部署失败。");
        if (RefreshAfterDeploymentAsync != null) await RefreshAfterDeploymentAsync();
    }

    private async Task OperateAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation;
        IsBusy = true;
        RefreshCommands();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (Stage != ModBinarySearchStage.CleanupFailed) SetStatus("ModBinarySearch.Cancelled");
        }
        catch (Exception ex) { SetStatus("ModBinarySearch.Failed", ex.Message); }
        finally
        {
            _operationCancellation = null;
            IsBusy = false;
            RefreshCommands();
            completion.TrySetResult();
        }
    }

    public async Task CancelAndWaitAsync()
    {
        _operationCancellation?.Cancel();
        await _completion;
    }

    public async Task<bool> PrepareShutdownAsync()
    {
        if (!HasSession)
        {
            await CancelAndWaitAsync();
            return true;
        }
        if (!await _confirmation.ConfirmAsync(_localization["ModBinarySearch.Title"],
                _localization["ModBinarySearch.ShutdownConfirm"])) return false;
        await CancelAndWaitAsync();
        await OperateAsync(async token =>
        {
            Result = await _service.CancelAsync(token);
            CurrentTest = null;
            Stage = ModBinarySearchStage.AwaitingCleanup;
            await CleanupCoreAsync(token);
        });
        return Stage == ModBinarySearchStage.Completed;
    }

    private void ApplyStartInfo(ModBinarySearchStartInfo info)
    {
        SourceProfileName = info.SourceProfileName;
        TemporaryProfileName = info.TemporaryProfileName;
        TotalModCount = info.TotalModCount;
        CandidateCount = info.CandidateCount;
        ExcludedModNames.Clear();
        foreach (var name in info.ExcludedModNames) ExcludedModNames.Add(name);
    }

    private void ReplaceDisabledCandidates(IReadOnlyList<ModBinarySearchCandidate> candidates)
    {
        DisabledCandidates.Clear();
        foreach (var candidate in candidates) DisabledCandidates.Add(candidate);
    }

    private void AddHistory(ModBinarySearchHistory? history)
    {
        if (history == null) return;
        History.Add(new ModBinarySearchHistoryItem(history, _localization));
        OnPropertyChanged(nameof(ShowHistory));
    }

    private void ResetState()
    {
        _startInfo = null;
        _temporaryDeploymentAttempted = false;
        _suspectDisabled = false;
        _officialGamePath = string.Empty;
        CurrentTest = null;
        Result = null;
        History.Clear();
        DisabledCandidates.Clear();
        ExcludedModNames.Clear();
        SourceProfileName = string.Empty;
        TemporaryProfileName = string.Empty;
        TotalModCount = 0;
        CandidateCount = 0;
        ProgressMessage = string.Empty;
        Stage = ModBinarySearchStage.Idle;
    }

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        OnPropertyChanged(nameof(StatusMessage));
    }

    partial void OnStageChanged(ModBinarySearchStage value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) => RefreshCommands();
    partial void OnCurrentTestChanged(ModBinarySearchTest? value)
    {
        OnPropertyChanged(nameof(StepHeading));
        RefreshCommands();
    }
    partial void OnResultChanged(ModBinarySearchResult? value)
    {
        OnPropertyChanged(nameof(ResultMessage));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowHistory));
        OnPropertyChanged(nameof(ShowExcluded));
        OnPropertyChanged(nameof(ShowTest));
        OnPropertyChanged(nameof(ShowResult));
        OnPropertyChanged(nameof(CanDeploy));
        OnPropertyChanged(nameof(CanObserve));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanCleanup));
        OnPropertyChanged(nameof(CanDisableSuspect));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        DeployCurrentTestCommand.NotifyCanExecuteChanged();
        ProblemRemainsCommand.NotifyCanExecuteChanged();
        ProblemGoneCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        FinishCleanupCommand.NotifyCanExecuteChanged();
        DisableSuspectCommand.NotifyCanExecuteChanged();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(UiLocalization.Revision)) return;
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(StepHeading));
        OnPropertyChanged(nameof(ResultMessage));
        foreach (var item in History) item.RefreshLanguage();
    }

    public void Dispose() => _localization.PropertyChanged -= OnLanguageChanged;
}

public enum ModBinarySearchStage
{
    Idle,
    ReadyToDeploy,
    Deploying,
    TestDeployFailed,
    AwaitingObservation,
    AwaitingCleanup,
    Cleaning,
    CleanupFailed,
    Completed
}

public sealed class ModBinarySearchHistoryItem(ModBinarySearchHistory history, UiLocalization localization) : ObservableObject
{
    public int Round => history.Round;
    public string Summary => localization.Format("ModBinarySearch.HistoryLine", history.Round,
        localization[history.ProblemRemains ? "ModBinarySearch.OutcomeRemains" : "ModBinarySearch.OutcomeGone"],
        string.Join("、", history.DisabledNames), string.Join("、", history.RemainingSuspectNames));
    public void RefreshLanguage() => OnPropertyChanged(nameof(Summary));
}
