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
/// 新版 IAS 系统（HD2IntegratedArmorSystem）页面：填写安装位置、发现 Runtime 目录，
/// 并调用上游生成器把当前部署中的 IAS 模组重新生成为 armor 配套（模型组件不再直写）。
/// </summary>
public partial class IasSystemViewModel : ObservableObject, IDisposable
{
    private readonly IIasSystemService _service;
    private readonly IIasCompanionService _companion;
    private readonly UiLocalization _localization;
    private IasCompanionResult? _generated;
    private IasCompanionStatus? _status;
    private string? _error;
    private bool _loaded;
    private string _workingKey = "IasSystem.ReadingLocation";
    private bool _acceptingLocation;
    [ObservableProperty] private string _operationLog = "";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _location = "";
    [ObservableProperty] private IReadOnlyList<string> _runtimeIds = [];
    [ObservableProperty] private string? _selectedRuntimeId;
    [ObservableProperty] private IReadOnlyList<IasModelComponent> _components = [];
    private CancellationTokenSource? _cancellation;
    public Task Completion { get; private set; } = Task.CompletedTask;

    public IasSystemViewModel(IIasSystemService service, IIasCompanionService companion,
        UiLocalization localization)
    {
        _service = service;
        _companion = companion;
        _localization = localization;
        _localization.PropertyChanged += OnLanguageChanged;
    }

    public bool CanEdit => !IsBusy;
    public bool HasComponents => Components.Count > 0;
    public bool RuntimeResolved => _status?.RuntimeResolved ?? false;
    public bool SystemInstalled => _status?.SystemInstalled ?? false;
    public bool CanGenerate => !IsBusy && _loaded && SystemInstalled;
    public string ComponentSummary => Components.Count == 0 ? _localization["IasSystem.NoModels"] :
        string.Join("\n", Components.Select(component =>
            $"{component.ModName} · {_localization.Format("IasSystem.FileCount", component.Files.Count)}"));
    public string StatusMessage => IsBusy ? _localization[_workingKey]
        : _error != null ? _localization.Format("IasSystem.Failed", _error)
        : _generated != null ? JoinGenerateMessage(_generated)
        : _loaded && RuntimeResolved ? (SystemInstalled
            ? _localization["IasSystem.SystemReady"]
            : _localization["IasSystem.SystemMissing"])
        : RuntimeIds.Count == 0 && _loaded && !string.IsNullOrWhiteSpace(Location) ? _localization["IasSystem.NoRuntime"]
        : _localization["IasSystem.SelectLocation"];

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

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task GenerateAsync() => OperateAsync("IasSystem.Generating", async token =>
    {
        var progress = new Progress<string>(message =>
        {
            if (!token.IsCancellationRequested && IsBusy) OperationLog += message + "\n";
        });
        // 生成阶段可取消；事务落盘阶段不可中断（服务内部处理）。游戏路径由服务自动解析。
        _generated = await _companion.RegenerateAsync(null, progress, token);
    });

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
        // 位置被编辑后旧的 Runtime 发现结果失效，等待刷新或生成时重新持久化。
        RuntimeIds = []; SelectedRuntimeId = null; _loaded = false;
        Refresh();
    }

    partial void OnSelectedRuntimeIdChanged(string? value)
    {
        if (_loaded) _ = _service.SaveActiveRuntimeIdAsync(value);
    }
    partial void OnComponentsChanged(IReadOnlyList<IasModelComponent> value) => Refresh();
    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(StatusMessage));
        RefreshCommand.NotifyCanExecuteChanged(); GenerateCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
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
        _status = await _companion.GetStatusAsync(token);
        OnPropertyChanged(nameof(ComponentSummary));
        OnPropertyChanged(nameof(HasComponents));
        OnPropertyChanged(nameof(RuntimeResolved));
        OnPropertyChanged(nameof(SystemInstalled));
        OnPropertyChanged(nameof(CanGenerate));
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancel() => IsBusy && _cancellation?.IsCancellationRequested == false;

    private string JoinGenerateMessage(IasCompanionResult result)
        => _localization.Format("IasSystem.Generated", result.FilesWritten, result.FilesDeleted, result.FilesPreserved);

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
        foreach (var property in new[] { nameof(StatusMessage), nameof(ComponentSummary), nameof(HasComponents),
            nameof(RuntimeResolved), nameof(SystemInstalled), nameof(CanGenerate) }) OnPropertyChanged(property);
    }

    public void Dispose() { _cancellation?.Cancel(); _localization.PropertyChanged -= OnLanguageChanged; }
}
