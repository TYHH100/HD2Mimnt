using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

public sealed record BrowseFeedChoice(ModBrowseFeed Value, string Label);
public sealed record BrowsePlatformChoice(string Id, string Name);

public partial class BrowseModsViewModel(ModBrowseService service, IWebsiteDownloadLauncher website,
    ImagePreviewService imagePreview, NexusModsAccountViewModel? account = null,
    BrowserDownloadHandoffService? browserDownloads = null,
    PlatformDownloadTaskRunner? downloadTasks = null) : ObservableObject
{
    public IReadOnlyList<BrowsePlatformChoice> Platforms { get; } = new[]
    { new BrowsePlatformChoice("nexusmods", "Nexus Mods"), new BrowsePlatformChoice("ayakamods", "Ayaka Mods") };
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAyaka), nameof(IsNexus), nameof(PlatformTitle), nameof(CanSearch), nameof(HasPagination), nameof(DownloadHint), nameof(DownloadButtonText), nameof(WebsiteButtonText), nameof(UseBrowserDownload), nameof(BrowseHelp), nameof(SelectHint))]
    private BrowsePlatformChoice? _selectedPlatform;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseBrowserDownload), nameof(DownloadHint), nameof(DownloadButtonText), nameof(BrowseHelp))]
    private bool _useAyakaApiDownload;
    public bool IsAyaka => SelectedPlatform?.Id == "ayakamods";
    public bool IsNexus => !IsAyaka;
    public bool UseBrowserDownload => IsAyaka && !UseAyakaApiDownload;
    public string PlatformTitle => $"{(IsAyaka ? "Ayaka Mods" : "Nexus Mods")} · Helldivers 2";
    public string BrowseHelp => UiLocalization.Current[UseAyakaApiDownload ? "Ayaka.ApiBrowseHelp" : "Ayaka.BrowseHelp"];
    public string DownloadHint => UiLocalization.Current[IsAyaka ? UseAyakaApiDownload ? "Ayaka.ApiDownloadHint" : "Ayaka.DownloadHint" : "Browse.DownloadHint"];
    public string DownloadButtonText => UiLocalization.Current[UseBrowserDownload ? "BrowserDownload.Open" : "Browse.Download"];
    public string WebsiteButtonText => UiLocalization.Current[IsAyaka ? "Ayaka.OpenWebsite" : "Browse.OpenWebsite"];
    public string SelectHint => UiLocalization.Current[IsAyaka ? "Ayaka.SelectHint" : "Browse.SelectHint"];
    public string BrowserDownloadsDirectory => browserDownloads?.DownloadsDirectory ?? "";
    private BrowserDownloadSession? _browserSession;
    public bool CanSearch => !IsAyaka && !IsBusy;
    public bool HasPagination => IsSearchResults || (IsAyaka && HasLoaded);
    public Task PlatformChange { get; private set; } = Task.CompletedTask;
    private readonly SemaphoreSlim _platformGate = new(1, 1);
    partial void OnSelectedPlatformChanged(BrowsePlatformChoice? value)
    {
        if (value != null && value.Id != service.PlatformId) PlatformChange = ChangePlatformAsync(value);
    }
    private async Task ChangePlatformAsync(BrowsePlatformChoice value)
    {
        await _platformGate.WaitAsync();
        try
        {
            await CloseAsync();
            if (SelectedPlatform != value) return;
            service.SelectPlatform(value.Id);
            Details = null; Files = Array.Empty<PlatformModFile>(); SelectedFile = null; SelectedMod = null;
            _searchResult = null; IsSearchResults = false; SearchText = ModLink = "";
            ScopeDescription = IsAyaka ? "A 网 · Helldivers 2 · 点击加载列表。" : "N 网热门前 5 项。";
        }
        finally { _platformGate.Release(); }
    }
    public NexusModsAccountViewModel? Account { get; } = account;
    private CancellationTokenSource? _operation;
    private CancellationToken _executionToken;
    private bool _isBackgroundDownload;
    private PendingDownload? _pending;
    private PendingDownload? _cancelledWebsiteDownload;
    private CancellationTokenSource? _previewOperation;
    private Task _previews = Task.CompletedTask;
    private CancellationTokenSource? _fullPreviewOperation;
    private Task _fullPreview = Task.CompletedTask;
    private Bitmap? _displayedImage;
    private ModPlatformErrorMessage? _displayError;
    private string _searchedText = "";
    private PlatformSearchResult? _searchResult;
    public Func<string, CancellationToken, Task>? ImportDownloadedArchiveAsync { get; set; }
    public Func<string, ModPlatformLink, CancellationToken, Task>? ImportPlatformArchiveAsync { get; set; }
    private IReadOnlyList<BrowseModCardViewModel> _loaded = Array.Empty<BrowseModCardViewModel>();
    public Task Completion { get; private set; } = Task.CompletedTask;
    public IReadOnlyList<BrowseFeedChoice> Feeds { get; } = new[]
    {
        new BrowseFeedChoice(ModBrowseFeed.Trending, "热门模组"),
        new BrowseFeedChoice(ModBrowseFeed.LatestAdded, "最新发布"),
        new BrowseFeedChoice(ModBrowseFeed.LatestUpdated, "最近更新")
    };
    [ObservableProperty] private BrowseFeedChoice? _selectedFeed;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPreviousPage), nameof(CanNextPage))]
    [NotifyPropertyChangedFor(nameof(HasPagination))]
    private bool _isSearchResults;
    [ObservableProperty] private string _modLink = "";
    [ObservableProperty] private IReadOnlyList<BrowseModCardViewModel> _mods = Array.Empty<BrowseModCardViewModel>();
    [ObservableProperty] private BrowseModCardViewModel? _selectedMod;
    [ObservableProperty] private PlatformModInfo? _details;
    [ObservableProperty] private IReadOnlyList<PlatformModFile> _files = Array.Empty<PlatformModFile>();
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDownloadAndImport))]
    private PlatformModFile? _selectedFile;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    [NotifyPropertyChangedFor(nameof(DisplayMessage))]
    private string _message = "";
    [ObservableProperty] private string _scopeDescription = "N 网热门前 5 项。";
    [ObservableProperty] private bool _hasLoaded;
    [ObservableProperty] private bool _isWaitingForWebsite;
    [ObservableProperty] private bool _isWaitingForBrowser;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadSegments))]
    private IReadOnlyList<DownloadSegmentProgress>? _downloadSegments;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDownloadProgress))]
    private string _downloadProgressText = "";
    [ObservableProperty] private bool _isLoadingImage;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenDetailsCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenLinkCommand))]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanDownload))]
    [NotifyPropertyChangedFor(nameof(CanDownloadAndImport))]
    [NotifyPropertyChangedFor(nameof(CanPreviousPage), nameof(CanNextPage))]
    [NotifyPropertyChangedFor(nameof(CanSearch))]
    private bool _isBusy;

    public bool CanDownload => !IsBusy && Details != null && SelectedFile?.WebsiteFileId != null;
    public bool CanDownloadAndImport => CanDownload && (ImportPlatformArchiveAsync != null || ImportDownloadedArchiveAsync != null);
    public string SuggestedFileName => SelectedFile?.FileName is { Length: > 0 } name ? Path.GetFileName(name) : "mod-download.zip";
    public bool HasDetails => Details != null;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasDownloadSegments => DownloadSegments is { Count: > 0 };
    public bool HasDownloadProgress => HasDownloadSegments || !string.IsNullOrWhiteSpace(DownloadProgressText);
    public bool HasNoResults => HasLoaded && Mods.Count == 0;
    public object DisplayMessage => (object?)_displayError ?? Message;
    public bool CanPreviousPage => !IsBusy && HasPagination && _searchResult?.Page > 1;
    public bool CanNextPage => !IsBusy && HasPagination && _searchResult?.HasNextPage == true;
    partial void OnMessageChanged(string value) => _displayError = null;
    partial void OnDetailsChanged(PlatformModInfo? value) => OnPropertyChanged(nameof(HasDetails));
    partial void OnHasLoadedChanged(bool value)
    { OnPropertyChanged(nameof(HasNoResults)); OnPropertyChanged(nameof(HasPagination)); }
    partial void OnModsChanged(IReadOnlyList<BrowseModCardViewModel> value) => OnPropertyChanged(nameof(HasNoResults));

    public async Task EnsureLoadedAsync()
    {
        SelectedPlatform ??= Platforms[0];
        Account?.RefreshStatus();
        SelectedFeed ??= Feeds[0];
        if (!HasLoaded && !IsBusy) await RefreshCommand.ExecuteAsync(null);
    }

    private bool CanStart() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RefreshAsync() => LoadFeedPageAsync(1);
    private Task LoadFeedPageAsync(int page) => RunAsync(async token =>
    {
        await StopPreviewsAsync();
        await StopFullPreviewAsync();
        token.ThrowIfCancellationRequested();
        var feed = SelectedFeed?.Value ?? ModBrowseFeed.Trending;
        IsSearchResults = false;
        _searchResult = null;
        Details = null;
        Files = Array.Empty<PlatformModFile>();
        SelectedFile = null;
        SelectedMod = null;
        ReleaseImages();
        HasLoaded = false;
        ScopeDescription = IsAyaka ? "A 网 · Helldivers 2 · 点击加载列表。" : feed switch
        {
            ModBrowseFeed.LatestAdded => "N 网最新发布 10 项。",
            ModBrowseFeed.LatestUpdated => "N 网最近更新 10 项。",
            _ => "N 网热门前 5 项。"
        };
        var result = await service.BrowsePageAsync(feed, page, token);
        token.ThrowIfCancellationRequested();
        if (IsAyaka)
        {
            _searchResult = result;
            ScopeDescription = $"A 网 Helldivers 2：共 {result.TotalCount} 项，第 {result.Page} 页。";
        }
        _loaded = result.Mods.Select(info => new BrowseModCardViewModel(info)).ToArray();
        HasLoaded = true;
        Mods = _loaded;
        _previews = LoadPreviewsAsync(_loaded);
    });

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task SearchAsync() => SearchPageAsync(SearchText.Trim(), 1);

    [RelayCommand]
    private Task PreviousPageAsync() => !CanPreviousPage ? Task.CompletedTask : IsAyaka
        ? LoadFeedPageAsync(_searchResult!.Page - 1) : SearchPageAsync(_searchedText, _searchResult!.Page - 1);

    [RelayCommand]
    private Task NextPageAsync() => !CanNextPage ? Task.CompletedTask : IsAyaka
        ? LoadFeedPageAsync(_searchResult!.Page + 1) : SearchPageAsync(_searchedText, _searchResult!.Page + 1);

    private Task SearchPageAsync(string query, int page) => RunAsync(async token =>
    {
        if (query.Length == 0) throw new ArgumentException("请输入模组名或作者后搜索。");
        await StopPreviewsAsync();
        await StopFullPreviewAsync();
        token.ThrowIfCancellationRequested();
        Message = "正在搜索 N 网模组…";
        var result = await service.SearchAsync(query, page, token);
        token.ThrowIfCancellationRequested();
        Details = null; Files = Array.Empty<PlatformModFile>(); SelectedFile = null; SelectedMod = null;
        ReleaseImages();
        _searchedText = query;
        _searchResult = result;
        _loaded = result.Mods.Select(info => new BrowseModCardViewModel(info)).ToArray();
        Mods = _loaded;
        HasLoaded = IsSearchResults = true;
        ScopeDescription = $"N 网搜索“{query}”：共 {result.TotalCount} 项，第 {result.Page} 页。";
        Message = "";
        _previews = LoadPreviewsAsync(_loaded);
    });

    private async Task LoadPreviewsAsync(IReadOnlyList<BrowseModCardViewModel> cards)
    {
        using var cts = new CancellationTokenSource();
        _previewOperation = cts;
        var token = cts.Token;
        foreach (var card in cards)
        {
            try
            {
                var bytes = await service.GetPreviewAsync(card.Info.ThumbnailUrl, token);
                if (bytes == null) continue;
                token.ThrowIfCancellationRequested();
                using var data = new MemoryStream(bytes);
                card.Image = await Task.Run(() => Bitmap.DecodeToWidth(data, 240), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception) { /* 预览失败保留占位，不影响模组列表。 */ }
        }
        _previewOperation = null;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task OpenDetailsAsync(BrowseModCardViewModel? card) => card == null ? Task.CompletedTask : LoadDetailsAsync(card.Info.Id, card);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task OpenLinkAsync() => RunAsync(token => LoadDetailsCoreAsync(service.ParseModId(ModLink), null, token));

    private Task LoadDetailsAsync(string modId, BrowseModCardViewModel? card) => RunAsync(token => LoadDetailsCoreAsync(modId, card, token));

    private async Task LoadDetailsCoreAsync(string modId, BrowseModCardViewModel? card, CancellationToken token)
    {
        Details = null;
        SelectedFile = null;
        Files = Array.Empty<PlatformModFile>();
        SelectedMod = card;
        var result = await service.GetDetailsAsync(modId, token);
        token.ThrowIfCancellationRequested();
        Details = result.Mod;
        Files = result.Files;
        SelectedFile = Files.FirstOrDefault();
        if (Files.Count == 0) Message = "此模组没有可下载的活动文件。";
    }

    [RelayCommand]
    private void OpenWebsite()
    {
        try { website.Open(Details is { } mod ? service.GetModPageUrl(mod.Id) : service.HomePageUrl); }
        catch (Exception ex) { SetError(ex, "打开 N 网"); }
    }

    [RelayCommand]
    private void OpenImagesWebsite()
    {
        if (Details is not { } mod) return;
        try { website.Open(service.GetModPageUrl(mod.Id) + (IsAyaka ? "" : "?tab=images")); }
        catch (Exception ex) { SetError(ex, "打开 N 网"); }
    }

    [RelayCommand]
    private Task PreviewCoverAsync(PlatformModInfo? mod)
        => mod == null ? Task.CompletedTask : _fullPreview = LoadFullPreviewAsync(mod);

    private async Task LoadFullPreviewAsync(PlatformModInfo mod)
    {
        using var cts = new CancellationTokenSource();
        _fullPreviewOperation = cts;
        IsLoadingImage = true;
        Bitmap? bitmap = null;
        try
        {
            var bytes = await service.GetPreviewAsync(mod.ThumbnailUrl, cts.Token);
            if (bytes == null) { Message = "此封面不可预览，图片缺失或超过大小限制。"; return; }
            using var data = new MemoryStream(bytes);
            bitmap = await Task.Run(() => new Bitmap(data), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            // 全尺寸图片交由覆盖层持有，刷新列表不会释放正在显示的缩略图引用。
            imagePreview.Show(bitmap, mod.Name, ownsImage: true);
            _displayedImage = bitmap;
            bitmap = null;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { SetError(ex, "预览模组封面"); }
        finally { bitmap?.Dispose(); _fullPreviewOperation = null; IsLoadingImage = false; }
    }

    private async Task StopFullPreviewAsync()
    {
        _fullPreviewOperation?.Cancel();
        await _fullPreview;
        if (ReferenceEquals(imagePreview.Image, _displayedImage) && _displayedImage != null) imagePreview.Close();
        _displayedImage = null;
    }

    public void CancelImagePreview() => _fullPreviewOperation?.Cancel();

    public Task DownloadAsync(string destination)
    {
        if (UseBrowserDownload) { OpenBrowserDownload(); return Task.CompletedTask; }
        return StartDownloadAsync(destination, import: false);
    }

    public Task DownloadProtocolLinkAsync(string input) => RunAsync(async token =>
    {
        await StopPreviewsAsync();
        await StopFullPreviewAsync();
        Message = "已收到 N 网下载授权，正在获取文件信息…";
        var (mod, file) = await service.ResolveProtocolDownloadAsync(input, token);
        token.ThrowIfCancellationRequested();
        // 先切服务再更新选择，避免触发切平台事件取消当前操作。
        service.SelectPlatform("nexusmods");
        SelectedPlatform = Platforms[0];
        ReleaseImages();
        SelectedMod = null;
        Details = mod;
        Files = new[] { file };
        SelectedFile = file;
        var source = service.CreateSourceLink(mod, file);
        var downloading = true;
        var progress = new InlineProgress<DownloadProgress>(value => Dispatcher.UIThread.Post(() =>
        {
            if (!downloading || _executionToken != token || token.IsCancellationRequested) return;
            ApplyDownloadProgress(value);
        }));
        Message = "正在下载所选文件…";
        await service.DownloadAndImportAsync(mod.Id, file, input, async (archive, importToken) =>
        {
            downloading = false;
            Message = "下载完成，正在导入模组…";
            IsIndeterminate = true;
            ClearDownloadProgress();
            if (ImportPlatformArchiveAsync != null) await ImportPlatformArchiveAsync(archive, source, importToken);
            else await (ImportDownloadedArchiveAsync ?? throw new InvalidOperationException("模组导入不可用。"))(archive, importToken);
        }, progress, token);
        Message = "模组已下载并导入模组库。";
    }, "N 网下载");

    [RelayCommand]
    private Task DownloadAndImportAsync()
        => CanDownloadAndImport ? StartDownloadAsync(null, import: true) : Task.CompletedTask;

    private Task StartDownloadAsync(string? destination, bool import)
    {
        if (!CanDownload) return Task.CompletedTask;
        var modId = Details!.Id;
        var file = SelectedFile!;
        var sourceLink = service.CreateSourceLink(Details, file);
        var useBrowser = UseBrowserDownload;
        Task ImportArchiveAsync(string archive, CancellationToken token)
            => ImportDownloadedArchiveAsync != null ? ImportDownloadedArchiveAsync(archive, token)
                : (ImportPlatformArchiveAsync ?? throw new InvalidOperationException("模组导入不可用。"))(archive, sourceLink, token);
        return RunAsync(async token =>
        {
            if (useBrowser)
            {
                using var session = await (browserDownloads ?? throw new InvalidOperationException("浏览器下载接管不可用。"))
                    .StartAsync(file, token);
                _browserSession = session;
                IsWaitingForBrowser = true;
                Message = $"已在浏览器发起下载：{file.FileName}。完成后将自动导入；也可以选择已下载的文件。";
                try
                {
                    website.Open(service.GetWebsiteDownloadUrl(modId, file));
                    var source = await session.WaitForFileAsync(token);
                    IsWaitingForBrowser = false;
                    Message = "下载完成，正在导入模组…";
                    await BrowserDownloadHandoffService.UseFileAsync(source, file,
                        ImportArchiveAsync, token);
                    Message = "浏览器下载的文件已导入模组库。";
                }
                finally { _browserSession = null; IsWaitingForBrowser = false; }
                return;
            }
            try
            {
                Message = IsAyaka ? "正在检查 A 网下载权限…" : "正在检查 N 网会员状态…";
                var premium = await service.HasDirectDownloadAccessAsync(token);
                token.ThrowIfCancellationRequested();
                string? input = null;
                if (!premium)
                {
                    var pending = new PendingDownload(modId, file);
                    _cancelledWebsiteDownload = null;
                    _pending = pending;
                    IsWaitingForWebsite = true;
                    Message = "等待网页授权：在浏览器完成下载确认，选择 HD2Mimnt 打开链接后自动继续。";
                    website.Open(service.GetWebsiteDownloadUrl(modId, file));
                    input = await pending.Result.Task.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                }
                IsWaitingForWebsite = false;
                Message = IsAyaka ? "正在通过 A 网 API 下载所选文件…" : premium ? "Premium 会员：正在通过 API 下载所选文件…" : "正在下载所选文件…";
                var downloading = true;
                var progress = new InlineProgress<DownloadProgress>(value => Dispatcher.UIThread.Post(() =>
                    {
                        if (!downloading || _executionToken != token || token.IsCancellationRequested) return;
                        ApplyDownloadProgress(value);
                    }));
                if (import)
                {
                    await service.DownloadAndImportAsync(modId, file, input, async (archive, importToken) =>
                    {
                        downloading = false;
                        Message = "下载完成，正在导入模组…";
                        IsIndeterminate = true;
                        ClearDownloadProgress();
                        await ImportArchiveAsync(archive, importToken);
                    }, progress, token);
                    Message = "模组已下载并导入模组库。";
                }
                else
                {
                    await service.DownloadAsync(modId, file, input, destination!, progress, token);
                    Message = "压缩包已保存，可拖入窗口添加到模组库。";
                }
            }
            finally
            {
                if (token.IsCancellationRequested && _pending is { } pending && !pending.Result.Task.IsCompleted)
                    _cancelledWebsiteDownload = pending;
                _pending = null;
                IsWaitingForWebsite = false;
            }
        }, $"{(IsAyaka ? "A 网下载" : "N 网下载")}：{Details.Name}");
    }

    [RelayCommand]
    public void OpenBrowserDownload()
    {
        if (!IsAyaka || !CanDownload) return;
        try
        {
            website.Open(service.GetWebsiteDownloadUrl(Details!.Id, SelectedFile!));
            Message = $"已在浏览器发起下载：{SelectedFile!.FileName}。";
        }
        catch (Exception ex) { SetError(ex, "打开模组网页"); }
    }

    public async Task SelectBrowserFileAsync(string path)
    {
        if (!IsWaitingForBrowser || _browserSession is not { } session || _operation is not { } operation) return;
        try { await session.SelectFileAsync(path, operation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (ReferenceEquals(_browserSession, session)) SetError(ex, "选择浏览器下载文件"); }
    }

    partial void OnSelectedFileChanged(PlatformModFile? value)
    {
        if (IsWaitingForBrowser) _operation?.Cancel();
    }

    public bool TryReceiveProtocolLink(string input, bool ignoreMismatch = false)
    {
        if (_pending is not { } pending || !IsWaitingForWebsite || _operation == null || _executionToken.IsCancellationRequested || pending.Result.Task.IsCompleted)
            return false;
        try
        {
            service.ValidateProtocolTarget(pending.ModId, pending.File, input);
            pending.Result.TrySetResult(input);
        }
        catch (Exception ex)
        {
            if (ignoreMismatch) return false;
            SetError(ex, "接收网页授权");
        }
        return true;
    }

    public bool IsCancelledProtocolLink(string input)
    {
        if (_cancelledWebsiteDownload is not { } cancelled) return false;
        try { service.ValidateProtocolTarget(cancelled.ModId, cancelled.File, input); return true; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private Task RunAsync(Func<CancellationToken, Task> action, string? downloadName = null)
        => IsBusy ? Completion : Completion = RunCoreAsync(action, downloadName);
    private async Task RunCoreAsync(Func<CancellationToken, Task> action, string? downloadName)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        _executionToken = cts.Token;
        _isBackgroundDownload = downloadName != null;
        IsBusy = true;
        Message = "";
        Progress = 0;
        IsIndeterminate = true;
        ClearDownloadProgress();
        try
        {
            if (downloadName != null && downloadTasks != null)
                await downloadTasks.RunAsync(downloadName, token =>
                {
                    _executionToken = token;
                    return action(token);
                }, () => new TaskProgress
                {
                    Progress = Progress, Stage = IsWaitingForWebsite || IsWaitingForBrowser ? "WaitingForWebsite" : "Downloading",
                    CurrentItem = SelectedFile?.Name ?? "", Message = Message
                }, cts.Token);
            else await action(cts.Token);
        }
        catch (OperationCanceledException) { Message = "操作已取消。"; }
        catch (Exception ex) { SetError(ex, IsAyaka ? "浏览 A 网模组" : "浏览 N 网模组"); }
        finally { _operation = null; _executionToken = default; _isBackgroundDownload = false; IsBusy = false; IsIndeterminate = false; }
    }

    private void SetError(Exception exception, string operation)
    {
        Message = ModPlatformErrorFormatter.Format(exception, operation);
        _displayError = new(exception, operation);
        OnPropertyChanged(nameof(DisplayMessage));
    }

    private bool CanCancel() => IsBusy;
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _operation?.Cancel();

    public async Task LeavePageAsync()
    {
        if (Account != null) await Account.StopAsync();
        if (!_isBackgroundDownload)
        {
            _operation?.Cancel();
            await Completion;
        }
        await StopPreviewsAsync();
        await StopFullPreviewAsync();
    }

    public async Task StopAsync()
    {
        if (Account != null) await Account.StopAsync();
        _operation?.Cancel();
        _previewOperation?.Cancel();
        _fullPreviewOperation?.Cancel();
        await Completion;
        await StopPreviewsAsync();
        await StopFullPreviewAsync();
    }

    private async Task StopPreviewsAsync() { _previewOperation?.Cancel(); await _previews; }

    private void ReleaseImages()
    {
        foreach (var mod in _loaded) mod.Image?.Dispose();
        _loaded = Array.Empty<BrowseModCardViewModel>();
        Mods = _loaded;
    }

    public async Task CloseAsync() { await StopAsync(); ReleaseImages(); HasLoaded = false; }

    private sealed record PendingDownload(string ModId, PlatformModFile File)
    {
        public TaskCompletionSource<string> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ApplyDownloadProgress(DownloadProgress value)
    {
        Progress = value.Percentage;
        IsIndeterminate = value.TotalBytes <= 0;
        DownloadProgressText = $"{Size(value.DownloadedBytes)} / {(value.TotalBytes > 0 ? Size(value.TotalBytes) : "?")} · {Size(value.BytesPerSecond)}/s";
        DownloadSegments = value.Segments;
    }

    private void ClearDownloadProgress()
    {
        DownloadProgressText = "";
        DownloadSegments = null;
    }

    private static string Size(long bytes) => bytes >= 1024 * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GiB" :
        bytes >= 1024 * 1024 ? $"{bytes / (1024d * 1024):0.0} MiB" : bytes >= 1024 ? $"{bytes / 1024d:0.0} KiB" : $"{bytes} B";

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}

public partial class BrowseModCardViewModel(PlatformModInfo info) : ObservableObject
{
    public PlatformModInfo Info { get; } = info;
    [ObservableProperty] private Bitmap? _image;
}
