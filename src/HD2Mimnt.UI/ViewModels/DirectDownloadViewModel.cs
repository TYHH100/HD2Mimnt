using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class DirectDownloadViewModel : ObservableObject
{
    private readonly DirectDownloadService _service;
    private readonly IBrowserLauncher _browser;
    private CancellationTokenSource? _operation;
    private DownloadRequest? _lastRequest;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public Func<string, CancellationToken, Task>? ImportDownloadedArchiveAsync { get; set; }
    public string[] ProxyModes { get; } = ["跟随系统代理", "不使用代理", "自定义代理"];

    [ObservableProperty] private string _link = "";
    [ObservableProperty] private string _fileName = "";
    [ObservableProperty] private string _destination = "";
    [ObservableProperty] private string _proxyMode = "跟随系统代理";
    [ObservableProperty] private string _proxyAddress = "";
    [ObservableProperty] private string _proxyUserName = "";
    [ObservableProperty] private string _proxyPassword = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _errorDetail = "";
    [ObservableProperty] private string _resolvedFileName = "";
    [ObservableProperty] private string _savedPath = "";
    [ObservableProperty] private bool _needsBrowser;

    public bool CanEdit => !IsBusy;
    public bool IsCustomProxy => ProxyMode == "自定义代理";
    public bool HasSavedFile => !string.IsNullOrWhiteSpace(SavedPath);

    public DirectDownloadViewModel(DirectDownloadService service, IBrowserLauncher browser)
    {
        _service = service;
        _browser = browser;
        var proxy = service.GetProxy();
        ProxyMode = ProxyModes[Enum.IsDefined(proxy.Mode) ? (int)proxy.Mode : 0];
        ProxyAddress = proxy.Address;
        Destination = service.GetDestination();
    }

    partial void OnProxyModeChanged(string value) => OnPropertyChanged(nameof(IsCustomProxy));
    partial void OnSavedPathChanged(string value) => OnPropertyChanged(nameof(HasSavedFile));
    partial void OnLinkChanged(string value) { NeedsBrowser = false; RefreshCommands(); }
    partial void OnDestinationChanged(string value) => RefreshCommands();
    partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanEdit)); RefreshCommands(); }
    private void RefreshCommands()
    {
        DownloadOnlyCommand.NotifyCanExecuteChanged(); DownloadAndImportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); RetryCommand.NotifyCanExecuteChanged(); SaveNetworkCommand.NotifyCanExecuteChanged();
        OpenBrowserCommand.NotifyCanExecuteChanged();
    }
    private bool CanStart() => !IsBusy && !string.IsNullOrWhiteSpace(Link);
    private bool CanDownloadOnly() => CanStart() && !string.IsNullOrWhiteSpace(Destination);
    private bool CanImport() => CanStart() && ImportDownloadedArchiveAsync != null;
    private bool CanCancel() => IsBusy && _operation?.IsCancellationRequested == false;
    private bool CanRetry() => !IsBusy && _lastRequest != null;
    private bool CanSave() => !IsBusy;

    private DownloadProxyOptions ReadProxy() => new(ProxyMode switch
    {
        "不使用代理" => DownloadProxyMode.Direct,
        "自定义代理" => DownloadProxyMode.Custom,
        _ => DownloadProxyMode.System
    }, ProxyAddress, ProxyUserName, ProxyPassword);

    [RelayCommand(CanExecute = nameof(CanDownloadOnly))]
    private Task DownloadOnlyAsync() => StartAsync(false);

    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task DownloadAndImportAsync() => StartAsync(true);

    private Task StartAsync(bool import)
    {
        if (IsBusy) return Completion;
        var request = new DownloadRequest(Link.Trim(), FileName.Trim(), Destination.Trim(), ReadProxy(), import);
        _lastRequest = request;
        return Completion = RunAsync(request);
    }

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _lastRequest == null || IsBusy ? Completion : Completion = RunAsync(_lastRequest);

    private async Task RunAsync(DownloadRequest request)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        IsBusy = true;
        IsIndeterminate = true;
        Progress = 0; ProgressText = ""; SavedPath = ""; ResolvedFileName = ""; ErrorDetail = ""; NeedsBrowser = false;
        StatusMessage = "正在解析下载链接…";
        var downloading = true;
        try
        {
            _ = DirectDownloadService.ParseLink(request.Link);
            request.Proxy.Validate();
            await _service.SavePreferencesAsync(request.Proxy, request.Destination);
            var progress = new InlineProgress<DownloadProgress>(value => Dispatcher.UIThread.Post(() =>
            {
                if (!downloading || !ReferenceEquals(_operation, cts) || cts.IsCancellationRequested) return;
                Progress = value.Percentage;
                IsIndeterminate = value.TotalBytes <= 0;
                ProgressText = $"{Size(value.DownloadedBytes)} / {(value.TotalBytes > 0 ? Size(value.TotalBytes) : "?")} · {Size(value.BytesPerSecond)}/s";
            }));
            Func<string, CancellationToken, Task>? importer = null;
            if (request.Import)
            {
                var import = ImportDownloadedArchiveAsync ?? throw new InvalidOperationException("模组导入不可用。");
                importer = async (archive, token) =>
                {
                    downloading = false;
                    StatusMessage = "下载完成，正在导入模组…";
                    IsIndeterminate = true;
                    await import(archive, token);
                };
            }
            var result = await _service.DownloadAsync(request.Link, request.FileName, request.Destination, request.Proxy,
                importer, progress, info =>
                {
                    ResolvedFileName = info.FileName;
                    StatusMessage = "正在下载文件…";
                }, cts.Token);
            downloading = false;
            Progress = 100;
            if (request.Import) StatusMessage = "模组已下载并导入模组库。";
            else { SavedPath = result; StatusMessage = "文件下载完成。"; }
            _lastRequest = null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || !downloading) { StatusMessage = "下载已取消；服务器支持时，重试可继续下载。"; }
        catch (DirectDownloadResponseException ex)
        {
            NeedsBrowser = ex.RequiresBrowser;
            StatusMessage = NeedsBrowser ? "需要在浏览器完成验证。" : "下载失败。";
            ErrorDetail = ex.Message;
            Progress = 0; ProgressText = ""; ResolvedFileName = "";
        }
        catch (Exception ex) { StatusMessage = "下载失败。"; ErrorDetail = ex.Message; }
        finally { downloading = false; _operation = null; IsBusy = false; IsIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void OpenBrowser()
    {
        try { _browser.Open(DirectDownloadService.ParseLink(Link).AbsoluteUri); }
        catch (Exception ex) { StatusMessage = "打开浏览器失败。"; ErrorDetail = ex.Message; }
    }

    public Task ImportBrowserFileAsync(string path)
        => IsBusy ? Completion : Completion = ImportBrowserFileCoreAsync(path);

    private async Task ImportBrowserFileCoreAsync(string path)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        IsBusy = true; IsIndeterminate = true;
        StatusMessage = "正在导入浏览器下载的文件…";
        ErrorDetail = ""; ProgressText = ""; SavedPath = ""; ResolvedFileName = ""; _lastRequest = null;
        try
        {
            var import = ImportDownloadedArchiveAsync ?? throw new InvalidOperationException("模组导入不可用。");
            await _service.ImportBrowserFileAsync(path, import, cts.Token);
            NeedsBrowser = false; Progress = 100; StatusMessage = "浏览器下载的文件已导入模组库。";
        }
        catch (OperationCanceledException) { StatusMessage = "操作已取消。"; }
        catch (Exception ex) { StatusMessage = "导入失败。"; ErrorDetail = ex.Message; }
        finally { _operation = null; IsBusy = false; IsIndeterminate = false; }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveNetworkAsync()
    {
        IsBusy = true;
        ErrorDetail = "";
        try { await _service.SavePreferencesAsync(ReadProxy(), Destination); StatusMessage = "下载位置与代理设置已保存；代理账号和密码仅本次会话有效。"; }
        catch (Exception ex) { StatusMessage = "保存失败。"; ErrorDetail = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _operation?.Cancel(); CancelCommand.NotifyCanExecuteChanged(); }

    public async Task CancelAndWaitAsync()
    {
        _operation?.Cancel();
        await Completion;
        if (SaveNetworkCommand.ExecutionTask is { } saving) await saving;
        ProxyPassword = ""; ProxyUserName = ""; _lastRequest = null;
        RefreshCommands();
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GiB" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";
    private sealed record DownloadRequest(string Link, string FileName, string Destination, DownloadProxyOptions Proxy, bool Import);
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}
