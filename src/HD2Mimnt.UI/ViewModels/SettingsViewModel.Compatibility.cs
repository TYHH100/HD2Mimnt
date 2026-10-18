using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel
{
    private readonly ModCompatibilityService? _compatibilityService;
    private CancellationTokenSource? _compatibilityDataOperation;
    private GameReferenceCacheStatus? _compatibilityDataStatus;
    private bool _compatibilityDataStopping;
    [ObservableProperty] private bool _isCompatibilityDataBusy;
    [ObservableProperty] private string _compatibilityDataProgress = string.Empty;
    [ObservableProperty] private string _compatibilityDataMessage = string.Empty;
    public Task CompatibilityDataTask { get; private set; } = Task.CompletedTask;
    public string CompatibilityDataStatusText
    {
        get
        {
            var loc = _localization ?? UiLocalization.Current;
            if (_compatibilityDataStatus is not { } status) return loc["Settings.Compatibility.Data.NotLoaded"];
            var text = loc["Settings.Compatibility.Data.State." + status.State];
            return status.BuiltAtUtc is { } built
                ? loc.Format("Settings.Compatibility.Data.Status", text, built.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    status.PackageCount, status.UnitCount, status.CachePath)
                : string.Join(Environment.NewLine, new[] { text, status.Error, status.CachePath }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }
    }

    public Task RefreshCompatibilityDataStatusAsync() => RunCompatibilityDataAsync(async token =>
    {
        _compatibilityDataStatus = await _compatibilityService!.GetGameDataStatusAsync(token);
        OnPropertyChanged(nameof(CompatibilityDataStatusText));
    });

    public Task UpdateStaleCompatibilityDataAsync() => RunCompatibilityDataAsync(async token =>
    {
        _compatibilityDataStatus = await _compatibilityService!.RefreshGameDataIfStaleAsync(DataProgress(token), token);
        OnPropertyChanged(nameof(CompatibilityDataStatusText));
    });

    [RelayCommand] private Task BuildCompatibilityDataAsync() => RunCompatibilityDataAsync(async token =>
    {
        _compatibilityDataStatus = await _compatibilityService!.BuildGameDataAsync(true, DataProgress(token), token);
        OnPropertyChanged(nameof(CompatibilityDataStatusText));
        CompatibilityDataMessage = (_localization ?? UiLocalization.Current)["Settings.Compatibility.Data.Built"];
    });

    [RelayCommand] private Task RefreshCompatibilityNamesAsync() => RunCompatibilityDataAsync(async token =>
    {
        var names = await Task.Run(() => _compatibilityService!.RefreshResourceNamesAsync(token), token);
        CompatibilityDataMessage = names.UpdateError ?? (_localization ?? UiLocalization.Current).Format(
            "Settings.Compatibility.Data.NamesUpdated", names.Resources.Count, names.Archives.Count);
    });

    private IProgress<GameReferenceBuildProgress> DataProgress(CancellationToken token)
        => new Progress<GameReferenceBuildProgress>(progress =>
        {
            if (!token.IsCancellationRequested && _compatibilityDataOperation?.Token == token && IsCompatibilityDataBusy)
                CompatibilityDataProgress = $"{progress.Completed:N0} / {progress.Total:N0}";
        });

    private Task RunCompatibilityDataAsync(Func<CancellationToken, Task> action)
    {
        if (_compatibilityService is null || _compatibilityDataStopping || IsCompatibilityDataBusy) return CompatibilityDataTask;
        return CompatibilityDataTask = RunCoreAsync();
        async Task RunCoreAsync()
        {
            using var operation = new CancellationTokenSource();
            _compatibilityDataOperation = operation; IsCompatibilityDataBusy = true;
            CompatibilityDataMessage = string.Empty; CompatibilityDataProgress = string.Empty;
            try { await action(operation.Token); }
            catch (OperationCanceledException) when (operation.IsCancellationRequested)
            { CompatibilityDataMessage = (_localization ?? UiLocalization.Current)["Compatibility.Canceled"]; }
            catch (Exception ex)
            {
                CompatibilityDataMessage = ex.Message;
                _compatibilityDataStatus = null;
                OnPropertyChanged(nameof(CompatibilityDataStatusText));
            }
            finally { _compatibilityDataOperation = null; IsCompatibilityDataBusy = false; CompatibilityDataProgress = string.Empty; }
        }
    }

    [RelayCommand] private void CancelCompatibilityData() => _compatibilityDataOperation?.Cancel();
    public async Task StopCompatibilityDataAsync()
    {
        _compatibilityDataStopping = true;
        try { CancelCompatibilityData(); await CompatibilityDataTask; }
        finally { _compatibilityDataStopping = false; }
    }
}
