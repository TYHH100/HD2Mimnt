using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 新版 IAS 系统（HD2IntegratedArmorSystem）页面：填写安装位置，把模组库中的
/// IAS 模型组件独立部署到 Runtime\&lt;随机ID&gt;\armor\rig（旧版 HD2IAS 管理页已废弃）。
/// </summary>
public partial class IasSystemViewModel : ObservableObject, IDisposable
{
    private readonly IIasSystemService _service;
    private readonly UiLocalization _localization;
    private readonly IConfirmationService? _confirmation;
    private IasDeployResult? _deployed;
    private IasUninstallResult? _uninstalled;
    private string? _error;
    private bool _loaded;
    private string _workingKey = "IasSystem.Working";
    private bool _acceptingLocation;
    [ObservableProperty] private string _operationLog = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _location = "";
    [ObservableProperty] private IReadOnlyList<string> _runtimeIds = [];
    [ObservableProperty] private string? _selectedRuntimeId;
    [ObservableProperty] private IReadOnlyList<IasModelComponent> _components = [];
    private CancellationTokenSource? _cancellation;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public IasSystemViewModel(IIasSystemService service, UiLocalization localization, IConfirmationService? confirmation = null)
    {
        _service = service;
        _localization = localization;
        _confirmation = confirmation;
        _localization.PropertyChanged += OnLanguageChanged;
    }

    public bool CanEdit => !IsBusy;
    public bool HasComponents => Components.Count > 0;
    public bool HasDeploymentHint { get; private set; }
    public bool CanDeploy => !IsBusy && _loaded && HasComponents && !string.IsNullOrWhiteSpace(SelectedRuntimeId);
    public bool CanUninstall => !IsBusy && HasDeploymentHint;
    public string ComponentSummary => Components.Count == 0 ? _localization["IasSystem.NoModels"] :
        string.Join("\n", Components.Select(component =>
            $"{component.ModName} · {_localization.Format("IasSystem.FileCount", component.Files.Count)}"));
    public string StatusMessage => IsBusy ? _localization[_workingKey]
        : _error != null ? _localization.Format("IasSystem.Failed", _error)
        : _uninstalled != null ? _localization.Format("IasSystem.Uninstalled", _uninstalled.RemovedCount)
        : _deployed != null ? JoinDeployMessage(_deployed)
        : RuntimeIds.Count == 0 && _loaded && !string.IsNullOrWhiteSpace(Location) ? _localization["IasSystem.NoRuntime"]
        : _localization["IasSystem.SelectLocation"];
    public string CopyFallbackNotice => _deployed is { UsedCopyFallback: true } ? _localization["IasSystem.CopyFallback"] : "";
    public bool HasCopyFallbackNotice => _deployed is { UsedCopyFallback: true };

    /// <summary>读取已保存位置并做首次发现；类别页选择时也可重复调用。</summary>
    public Task InitializeAsync() => OperateAsync("IasSystem.ReadingLocation", async token =>
    {
        if (!_acceptingLocation)
        {
            var saved = await _service.GetInstallLocationAsync(token);
            if (!string.IsNullOrWhiteSpace(saved) && string.IsNullOrWhiteSpace(Location))
            {
                _acceptingLocation = true;
                try { Location = saved; } finally { _acceptingLocation = false; }
            }
        }
        await DiscoverAsync(Location, token);
        _loaded = true;
    });

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RefreshAsync() => OperateAsync("IasSystem.ReadingLocation", async token =>
    {
        if (!string.IsNullOrWhiteSpace(Location))
            await _service.SaveInstallLocationAsync(Location.Trim(), token);
        await DiscoverAsync(Location, token);
        _loaded = true;
    });

    [RelayCommand(CanExecute = nameof(CanDeploy))]
    private Task DeployAsync() => OperateAsync("IasSystem.Working", async token =>
    {
        var location = Location.Trim(); var runtimeId = SelectedRuntimeId!;
        await _service.SaveInstallLocationAsync(location, token);
        var progress = new Progress<string>(message =>
        {
            if (!token.IsCancellationRequested && IsBusy) OperationLog += message + "\n";
        });
        _deployed = await _service.DeployAsync(location, runtimeId, progress, token);
        HasDeploymentHint = true;
        OnPropertyChanged(nameof(CopyFallbackNotice)); OnPropertyChanged(nameof(HasCopyFallbackNotice));
        _uninstalled = null;
    });

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        if (!CanUninstall || _confirmation == null) return;
        if (!await _confirmation.ConfirmAsync(_localization["IasSystem.Uninstall"], _localization["IasSystem.UninstallConfirm"])) return;
        if (!CanUninstall) return;
        await OperateAsync("IasSystem.Uninstalling", async token =>
        {
            var progress = new Progress<string>(message =>
            {
                if (!token.IsCancellationRequested && IsBusy) OperationLog += message + "\n";
            });
            _uninstalled = await _service.UninstallAsync(progress, token);
            _deployed = null;
            HasDeploymentHint = false;
            OnPropertyChanged(nameof(CopyFallbackNotice)); OnPropertyChanged(nameof(HasCopyFallbackNotice));
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _cancellation?.Cancel(); CancelCommand.NotifyCanExecuteChanged(); }
    public async Task CancelAndWaitAsync() { _cancellation?.Cancel(); await Completion; }

    /// <summary>浏览按钮（View 代码后置）选完目录后回填。</summary>
    public void AcceptLocation(string path)
    {
        _acceptingLocation = true;
        try { Location = path; } finally { _acceptingLocation = false; }
        _ = RefreshCommand.ExecuteAsync(null);
    }

    partial void OnLocationChanged(string value)
    {
        if (_acceptingLocation) return;
        // 位置被编辑后旧的 Runtime 发现结果失效，等待刷新或部署时重新持久化。
        RuntimeIds = []; SelectedRuntimeId = null; _loaded = false;
        Refresh();
    }

    partial void OnSelectedRuntimeIdChanged(string? value)
    {
        DeployCommand.NotifyCanExecuteChanged();
        if (_loaded) _ = _service.SaveActiveRuntimeIdAsync(value);
    }
    partial void OnComponentsChanged(IReadOnlyList<IasModelComponent> value) => Refresh();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(StatusMessage));
        RefreshCommand.NotifyCanExecuteChanged(); DeployCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
    }

    private async Task DiscoverAsync(string location, CancellationToken token)
    {
        var ids = string.IsNullOrWhiteSpace(location) ? [] : await _service.DiscoverRuntimeIdsAsync(location.Trim(), token);
        var components = await _service.GetModelComponentsAsync(token);
        if (!string.Equals(location.Trim(), Location.Trim(), StringComparison.Ordinal)) return;
        RuntimeIds = ids;
        var saved = await _service.GetActiveRuntimeIdAsync(token);
        SelectedRuntimeId = ids.FirstOrDefault(id => string.Equals(id, saved, StringComparison.OrdinalIgnoreCase))
            ?? ids.FirstOrDefault(id => id == SelectedRuntimeId) ?? ids.FirstOrDefault();
        Components = components;
        HasDeploymentHint = await _service.HasDeploymentAsync(token);
        OnPropertyChanged(nameof(CanUninstall)); OnPropertyChanged(nameof(ComponentSummary));
        OnPropertyChanged(nameof(HasComponents)); OnPropertyChanged(nameof(CanDeploy));
        UninstallCommand.NotifyCanExecuteChanged(); DeployCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancel() => IsBusy && _cancellation?.IsCancellationRequested == false;

    private string JoinDeployMessage(IasDeployResult result)
    {
        var message = _localization.Format("IasSystem.Deployed", result.DeployedCount, result.TargetRoot);
        return result.SkippedCount > 0
            ? $"{message} {_localization.Format("IasSystem.Skipped", result.SkippedCount)}" : message;
    }

    private async Task OperateAsync(string workingKey, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Completion = completion.Task;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation; _error = null;
        _workingKey = workingKey;
        IsBusy = true; Refresh();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { _error = error.Message; }
        finally { _cancellation = null; IsBusy = false; Refresh(); completion.TrySetResult(); }
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(UiLocalization.Revision)) Refresh();
    }

    private void Refresh()
    {
        foreach (var property in new[] { nameof(StatusMessage), nameof(ComponentSummary), nameof(CopyFallbackNotice),
            nameof(HasCopyFallbackNotice), nameof(HasComponents), nameof(CanDeploy), nameof(CanUninstall) }) OnPropertyChanged(property);
    }

    public void Dispose() { _cancellation?.Cancel(); _localization.PropertyChanged -= OnLanguageChanged; }
}
