using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModOnlineViewModel(ModOnlineService service, Func<Task> refresh,
    IWebsiteDownloadLauncher? websiteLauncher = null) : ObservableObject
{
    private Guid _modId;
    private ModPlatformLink? _link;
    private CancellationTokenSource? _operation;
    private volatile bool _isImporting;
    private PendingAuthorization? _pendingAuthorization;
    private readonly IWebsiteDownloadLauncher _websiteLauncher = websiteLauncher ?? new WebsiteDownloadLauncher();
    public Task Completion { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _targetName = "";
    [ObservableProperty] private string _localVersion = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private bool _useWebsiteAuthorization = true;
    [ObservableProperty] private bool _isWaitingForWebsite;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;
    [ObservableProperty] private IReadOnlyList<PlatformModFile> _files = Array.Empty<PlatformModFile>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(VersionMessage))]
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    private PlatformModFile? _selectedFile;

    public bool CanDownload => !IsBusy && SelectedFile != null &&
        (!UseWebsiteAuthorization || !string.IsNullOrWhiteSpace(SelectedFile.WebsiteFileId));
    public string VersionMessage => SelectedFile is not { } file ? "没有可下载文件。" :
        string.IsNullOrWhiteSpace(file.Version) || string.IsNullOrWhiteSpace(LocalVersion) ? "版本信息不足，无法判断更新。" :
        _link?.InstalledFileVersionId == file.Id ? "已安装所选文件版本。" :
        file.WebsiteFileId != null && _link?.InstalledWebsiteFileId == file.WebsiteFileId ? "已安装所选网页文件。" :
        file.WebsiteFileId != null && _link?.InstalledWebsiteFileId != null ? "所选网页文件与已安装文件不同，可下载并替换。" :
        file.Version == LocalVersion ? "版本号相同；可重新下载或替换。" : "所选版本与本地不同，可下载并替换。";

    public string SuggestedDownloadFileName => SelectedFile?.FileName is { Length: > 0 } name &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? name : UseWebsiteAuthorization ? "mod-download" : "mod-update.zip";

    public async Task OpenAsync(ModItemViewModel mod)
    {
        if (IsBusy) return;
        _modId = mod.Id;
        _link = mod.PlatformLink;
        if (_link?.PlatformId != "nexusmods") UseWebsiteAuthorization = false;
        TargetName = mod.Name;
        LocalVersion = mod.Version;
        Files = Array.Empty<PlatformModFile>();
        SelectedFile = null;
        Message = "";
        IsOpen = true;
        await CheckCommand.ExecuteAsync(null);
    }

    private bool CanCheck() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunAsync("检查更新", async token =>
    {
        Message = "正在检查 N 网文件版本…";
        var result = await service.CheckAsync(_modId, token, UseWebsiteAuthorization);
        _link = result.Mod.PlatformLink;
        LocalVersion = result.Mod.Version;
        Files = result.Files;
        SelectedFile = Files.FirstOrDefault();
        Message = Files.Count == 0 ? "N 网没有可下载的活动文件。" : UseWebsiteAuthorization
            ? "选择文件后点击下载或更新，将自动打开该文件的 N 网网页。"
            : "已获取文件列表，默认选择主文件。";
    });

    partial void OnUseWebsiteAuthorizationChanged(bool value)
    {
        _operation?.Cancel();
        Files = Array.Empty<PlatformModFile>();
        SelectedFile = null;
        Message = "下载方式已切换，请重新检查更新。";
    }
    /// <summary>由启动参数/单实例管道在 UI 线程调用；只有当前等待的文件可继续。</summary>
    public bool TryReceiveProtocolLink(string input)
    {
        var pending = _pendingAuthorization;
        if (!IsOpen || pending == null || !IsWaitingForWebsite || _operation?.IsCancellationRequested != false || pending.Result.Task.IsCompleted)
            return false;
        try
        {
            service.ValidateProtocolTarget(pending.Link, pending.File, input);
            pending.Result.TrySetResult(input);
            Message = "已收到网页授权，正在验证账户并继续…";
        }
        catch (Exception ex) { Message = ModPlatformErrorFormatter.Format(ex, "接收网页授权") + "\n仍在等待所选文件的有效授权。"; }
        return true;
    }

    private async Task<(PlatformModFile File, string? ProtocolLink)> AuthorizeAsync(
        ModPlatformLink link, PlatformModFile file, CancellationToken token)
    {
        if (!UseWebsiteAuthorization) return (file, null);
        var pending = new PendingAuthorization(link, file);
        _pendingAuthorization = pending;
        IsWaitingForWebsite = true;
        Message = "等待网页授权：在浏览器完成下载确认，选择 HD2Mimnt 打开链接后将自动继续。";
        try
        {
            _websiteLauncher.Open(service.GetWebsiteDownloadUrl(link, file));
            var input = await pending.Result.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            IsWaitingForWebsite = false;
            Message = "正在验证网页授权所属账户…";
            var resolved = await service.ResolveProtocolFileAsync(_modId, link, input, token);
            token.ThrowIfCancellationRequested();
            if (resolved.Id != pending.File.Id) throw new InvalidOperationException("授权文件与所选文件不同。");
            return (resolved, input);
        }
        finally { _pendingAuthorization = null; IsWaitingForWebsite = false; }
    }

    partial void OnSelectedFileChanged(PlatformModFile? value)
    {
        if (_pendingAuthorization is { } pending && value?.Id != pending.File.Id) _operation?.Cancel();
    }

    private bool CanUpdate() => CanDownload && !string.IsNullOrWhiteSpace(SelectedFile?.Version);
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private Task UpdateAsync()
    {
        var file = SelectedFile!;
        var link = _link!;
        return RunAsync("下载更新包", async token =>
        {
            var authorized = await AuthorizeAsync(link, file, token);
            Message = "正在下载更新包…";
            var result = await service.UpdateAsync(_modId, link, authorized.File, DownloadReporter(token),
                new InlineProgress<ModImportProgress>(p =>
                {
                    _isImporting = true;
                    Publish(token, () =>
                    {
                        Progress = p.Percentage;
                        IsIndeterminate = p.IsIndeterminate;
                        Message = p.Step + " · " + p.Detail;
                    });
                }), token, authorized.ProtocolLink);
            _link = result.Mod.PlatformLink;
            LocalVersion = result.Mod.Version;
            OnPropertyChanged(nameof(VersionMessage));
            await refresh();
            Message = "模组已替换，请检查模组选项。" + (result.DeploymentWasCleaned ? "旧部署已清理，请重新部署。" : "")
                + result.CleanupWarning;
        });
    }

    public Task DownloadAsync(string path)
    {
        if (!CanDownload || _link == null) return Task.CompletedTask;
        var file = SelectedFile!;
        var link = _link;
        return RunAsync("下载压缩包", async token =>
        {
            var authorized = await AuthorizeAsync(link, file, token);
            Message = "正在下载压缩包…";
            await service.DownloadAsync(_modId, link, authorized.File, path, DownloadReporter(token), token, authorized.ProtocolLink);
            Message = "压缩包已保存：" + path;
        });
    }

    private IProgress<DownloadProgress> DownloadReporter(CancellationToken token) =>
        new InlineProgress<DownloadProgress>(p => Publish(token, () =>
        {
            if (_isImporting) return;
            Progress = p.Percentage;
            IsIndeterminate = p.TotalBytes <= 0;
            Message = p.TotalBytes > 0 ? $"下载中 · {p.DownloadedBytes / 1048576.0:F1} / {p.TotalBytes / 1048576.0:F1} MB"
                : $"下载中 · {p.DownloadedBytes / 1048576.0:F1} MB";
        }));

    private void Publish(CancellationToken token, Action action) => Dispatcher.UIThread.Post(() =>
    {
        if (IsOpen && IsBusy && _operation?.Token == token && !token.IsCancellationRequested) action();
    });

    private Task RunAsync(string operation, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return Completion;
        return Completion = RunCoreAsync(operation, action);
    }
    private async Task RunCoreAsync(string operation, Func<CancellationToken, Task> action)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        _isImporting = false;
        IsBusy = true;
        Progress = 0;
        IsIndeterminate = true;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Message = "操作已取消，原模组未替换。"; }
        catch (Exception ex)
        {
            Message = ModPlatformErrorFormatter.Format(ex, operation);
        }
        finally { _operation = null; IsBusy = false; IsIndeterminate = false; }
    }

    private bool CanCancel() => IsBusy;
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() { _operation?.Cancel(); Message = "正在取消并清理…"; }

    [RelayCommand]
    private async Task CloseAsync()
    {
        _operation?.Cancel();
        await Completion;
        IsOpen = false;
    }
    private sealed record PendingAuthorization(ModPlatformLink Link, PlatformModFile File)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
