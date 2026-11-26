using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel
{
    private IGameCpuSchedulingService? _cpuSchedulingService;
    private CpuTopology? _cpuSchedulingTopology;

    [ObservableProperty] private bool _cpuSchedulingEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CpuSchedulingModeIndex))]
    [NotifyPropertyChangedFor(nameof(CpuSchedulingHintText))]
    [NotifyPropertyChangedFor(nameof(CpuSchedulingMethodText))]
    private CpuSchedulingMode _cpuSchedulingMode = CpuSchedulingMode.Off;
    [ObservableProperty] private bool _cpuSchedulingApplyOnLaunch = true;
    [ObservableProperty] private bool _cpuSchedulingReapplyOnRestart = true;
    [ObservableProperty] private bool _cpuSchedulingMonitorChanges = true;
    [ObservableProperty] private bool _cpuSchedulingRestoreOriginal = true;
    [ObservableProperty] private string _cpuSchedulingStatusText = string.Empty;
    [ObservableProperty] private string _cpuSchedulingDetailText = string.Empty;

    public int CpuSchedulingModeIndex
    {
        get => (int)CpuSchedulingMode;
        set
        {
            if (value is < 0 or > 2)
                return;
            CpuSchedulingMode = (CpuSchedulingMode)value;
            OnPropertyChanged();
        }
    }

    public string CpuSchedulingCpuName =>
        _cpuSchedulingTopology?.CpuName is { Length: > 0 } name ? name : Localize("CpuScheduling.Unknown");

    public string CpuSchedulingPCoreText => _cpuSchedulingTopology is { } topology
        ? $"{topology.PerformanceCoreCount} / {topology.PerformanceLogicalProcessorCount}"
        : "-";

    public string CpuSchedulingECoreText => _cpuSchedulingTopology is { } topology
        ? $"{topology.EfficiencyCoreCount} / {topology.EfficiencyLogicalProcessorCount}"
        : "-";

    public string CpuSchedulingLogicalText => _cpuSchedulingTopology is { } topology
        ? $"{topology.UsableSets.Count} / {topology.AllSets.Count}"
        : "-";

    public string CpuSchedulingMethodText => CpuSchedulingMode switch
    {
        CpuSchedulingMode.PreferPerformanceCores => "CPU Sets",
        CpuSchedulingMode.PerformanceCoresOnly when _cpuSchedulingTopology is { } topology
            && topology.StrictAffinityMask is null
            && topology.UsablePerformanceGroupMasks.Count > 0 => "CPU Set Masks",
        CpuSchedulingMode.PerformanceCoresOnly => "Process Affinity",
        _ => "-"
    };

    public string CpuSchedulingSupportText => _cpuSchedulingTopology switch
    {
        null => Localize("CpuScheduling.Support.NotLoaded"),
        { Supported: true } => Localize("CpuScheduling.Support.Supported"),
        { Reason: var reason } when reason.StartsWith("no heterogeneous", StringComparison.Ordinal)
            => Localize("CpuScheduling.Support.NoHeterogeneous"),
        _ => Localize("CpuScheduling.Support.Unsupported")
    };

    public string CpuSchedulingHintText => CpuSchedulingMode switch
    {
        CpuSchedulingMode.PreferPerformanceCores => Localize("CpuScheduling.HintPrefer"),
        CpuSchedulingMode.PerformanceCoresOnly => Localize("CpuScheduling.HintStrict"),
        _ => Localize("CpuScheduling.HintOff")
    };

    private void InitializeCpuScheduling(IGameCpuSchedulingService? service)
    {
        _cpuSchedulingService = service;
        if (service is not null)
            service.StatusChanged += OnCpuSchedulingStatusChanged;
        LoadCpuScheduling();
    }

    private void DisposeCpuScheduling()
    {
        if (_cpuSchedulingService is not null)
            _cpuSchedulingService.StatusChanged -= OnCpuSchedulingStatusChanged;
        _cpuSchedulingService = null;
    }

    private void LoadCpuScheduling()
    {
        if (_cpuSchedulingService is null)
        {
            CpuSchedulingStatusText = Localize("CpuScheduling.Status.Unsupported");
            CpuSchedulingDetailText = string.Empty;
            return;
        }

        var settings = _cpuSchedulingService.Settings;
        CpuSchedulingEnabled = settings.Enabled;
        CpuSchedulingMode = settings.Mode;
        CpuSchedulingApplyOnLaunch = settings.ApplyOnLaunch;
        CpuSchedulingReapplyOnRestart = settings.ReapplyOnRestart;
        CpuSchedulingMonitorChanges = settings.MonitorChanges;
        CpuSchedulingRestoreOriginal = settings.RestoreOriginal;
        _cpuSchedulingTopology = _cpuSchedulingService.Topology;

        ApplyCpuSchedulingStatus(
            _cpuSchedulingService.Status,
            _cpuSchedulingService.StatusDetail,
            _cpuSchedulingService.CurrentProcessId,
            _cpuSchedulingTopology);
    }

    private async Task SaveCpuSchedulingAsync()
    {
        if (_cpuSchedulingService is null)
            return;

        await _cpuSchedulingService.SaveSettingsAsync(new CpuSchedulingSettings(
            CpuSchedulingEnabled,
            CpuSchedulingMode,
            CpuSchedulingApplyOnLaunch,
            CpuSchedulingReapplyOnRestart,
            CpuSchedulingMonitorChanges,
            CpuSchedulingRestoreOriginal));
        await _cpuSchedulingService.RefreshAsync();
        LoadCpuScheduling();
    }

    [RelayCommand]
    private async Task RefreshCpuSchedulingAsync()
    {
        if (_cpuSchedulingService is null)
            return;

        await _cpuSchedulingService.RefreshTopologyAsync();
        await _cpuSchedulingService.RefreshAsync();
        LoadCpuScheduling();
    }

    [RelayCommand]
    private async Task RestoreCpuSchedulingAsync()
    {
        if (_cpuSchedulingService is null)
            return;

        var result = await _cpuSchedulingService.RestoreCurrentGameAsync();
        CpuSchedulingEnabled = false;
        CpuSchedulingMode = CpuSchedulingMode.Off;
        await SaveCpuSchedulingAsync();
        StatusMessage = result.Success
            ? Localize("CpuScheduling.RestoreDone")
            : Localize("CpuScheduling.RestoreFailed");
    }

    private void OnCpuSchedulingStatusChanged(object? sender, CpuSchedulingStatusChangedEventArgs args)
    {
        void Apply()
        {
            ApplyCpuSchedulingStatus(args.Status, args.Detail, args.ProcessId, args.Topology);
        }

        if (global::Avalonia.Application.Current is null)
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private void ApplyCpuSchedulingStatus(
        CpuSchedulingStatus status, string detail, int? processId, CpuTopology? topology)
    {
        if (topology is not null)
            _cpuSchedulingTopology = topology;

        CpuSchedulingStatusText = Localize("CpuScheduling.Status." + status);
        CpuSchedulingDetailText = string.IsNullOrWhiteSpace(detail)
            ? processId is { } pid ? $"PID {pid}" : string.Empty
            : processId is { } value ? $"PID {value} · {detail}" : detail;

        OnPropertyChanged(nameof(CpuSchedulingCpuName));
        OnPropertyChanged(nameof(CpuSchedulingPCoreText));
        OnPropertyChanged(nameof(CpuSchedulingECoreText));
        OnPropertyChanged(nameof(CpuSchedulingLogicalText));
        OnPropertyChanged(nameof(CpuSchedulingSupportText));
        OnPropertyChanged(nameof(CpuSchedulingMethodText));
        OnPropertyChanged(nameof(CpuSchedulingHintText));
    }

    private void RefreshCpuSchedulingLocalizedText()
    {
        OnPropertyChanged(nameof(CpuSchedulingCpuName));
        OnPropertyChanged(nameof(CpuSchedulingPCoreText));
        OnPropertyChanged(nameof(CpuSchedulingECoreText));
        OnPropertyChanged(nameof(CpuSchedulingLogicalText));
        OnPropertyChanged(nameof(CpuSchedulingSupportText));
        OnPropertyChanged(nameof(CpuSchedulingMethodText));
        OnPropertyChanged(nameof(CpuSchedulingHintText));

        if (_cpuSchedulingService is not null)
        {
            CpuSchedulingStatusText = Localize("CpuScheduling.Status." + _cpuSchedulingService.Status);
        }
    }

    private string Localize(string key) => (_localization ?? UiLocalization.Current)[key];
}