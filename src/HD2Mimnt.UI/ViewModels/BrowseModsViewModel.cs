using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public sealed record BrowseFeedChoice(ModBrowseFeed Value, string Label);

public partial class BrowseModsViewModel(ModBrowseService service, IWebsiteDownloadLauncher website,
    ImagePreviewService imagePreview) : ObservableObject
{
    private CancellationTokenSource? _operation;
    private PendingDownload? _pending;
    private CancellationTokenSource? _previewOperation;
    private Task _previews = Task.CompletedTask;
    private CancellationTokenSource? _fullPreviewOperation;
    private Task _fullPreview = Task.CompletedTask;
    private Bitmap? _displayedImage;
    private ModPlatformErrorMessage? _displayError;
    private string _searchedText = "";
    private PlatformSearchResult? _searchResult;
    public Func<string, CancellationToken, Task>? ImportDownloadedArchiveAsync { get; set; }
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
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
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
    private bool _isBusy;

    public bool CanDownload => !IsBusy && Details != null && SelectedFile?.WebsiteFileId != null;
    public bool CanDownloadAndImport => CanDownload && ImportDownloadedArchiveAsync != null;
    public string SuggestedFileName => SelectedFile?.FileName is { Length: > 0 } name ? Path.GetFileName(name) : "mod-download.zip";
    public bool HasDetails => Details != null;
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool HasNoResults => HasLoaded && Mods.Count == 0;
    public object DisplayMessage => (object?)_displayError ?? Message;
    public bool CanPreviousPage => !IsBusy && IsSearchResults && _searchResult?.Page > 1;
    public bool CanNextPage => !IsBusy && IsSearchResults && _searchResult?.HasNextPage == true;
    partial void OnMessageChanged(string value) => _displayError = null;
    partial void OnDetailsChanged(PlatformModInfo? value) => OnPropertyChanged(nameof(HasDetails));
    partial void OnHasLoadedChanged(bool value) => OnPropertyChanged(nameof(HasNoResults));
    partial void OnModsChanged(IReadOnlyList<BrowseModCardViewModel> value) => OnPropertyChanged(nameof(HasNoResults));

    public async Task EnsureLoadedAsync()
    {
        SelectedFeed ??= Feeds[0];
        if (!HasLoaded && !IsBusy) await RefreshCommand.ExecuteAsync(null);
    }

    private bool CanStart() => !IsBusy;
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task RefreshAsync() => RunAsync(async token =>
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
        ScopeDescription = feed switch
        {
            ModBrowseFeed.LatestAdded => "N 网最新发布 10 项。",
            ModBrowseFeed.LatestUpdated => "N 网最近更新 10 项。",
            _ => "N 网热门前 5 项。"
        };
        var result = await service.BrowseAsync(feed, token);
        token.ThrowIfCancellationRequested();
        _loaded = result.Mods.Select(info => new BrowseModCardViewModel(info)).ToArray();
        HasLoaded = true;
        Mods = _loaded;
        _previews = LoadPreviewsAsync(_loaded);
    });

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task SearchAsync() => SearchPageAsync(SearchText.Trim(), 1);

    [RelayCommand]
    private Task PreviousPageAsync() => CanPreviousPage ? SearchPageAsync(_searchedText, _searchResult!.Page - 1) : Task.CompletedTask;

    [RelayCommand]
    private Task NextPageAsync() => CanNextPage ? SearchPageAsync(_searchedText, _searchResult!.Page + 1) : Task.CompletedTask;

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
        try { website.Open(Details is { } mod ? service.GetModPageUrl(mod.Id) : "https://www.nexusmods.com/helldivers2/mods/"); }
        catch (Exception ex) { SetError(ex, "打开 N 网"); }
    }

    [RelayCommand]
    private void OpenImagesWebsite()
    {
        if (Details is not { } mod) return;
        try { website.Open(service.GetModPageUrl(mod.Id) + "?tab=images"); }
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
        => StartDownloadAsync(destination, import: false);

    [RelayCommand]
    private Task DownloadAndImportAsync()
        => CanDownloadAndImport ? StartDownloadAsync(null, import: true) : Task.CompletedTask;

    private Task StartDownloadAsync(string? destination, bool import)
    {
        if (!CanDownload) return Task.CompletedTask;
        var modId = Details!.Id;
        var file = SelectedFile!;
        return RunAsync(async token =>
        {
            try
            {
                Message = "正在检查 N 网会员状态…";
                var premium = await service.HasDirectDownloadAccessAsync(token);
                token.ThrowIfCancellationRequested();
                string? input = null;
                if (!premium)
                {
                    var pending = new PendingDownload(modId, file);
                    _pending = pending;
                    IsWaitingForWebsite = true;
                    Message = "等待网页授权：在浏览器完成下载确认，选择 HD2Mimnt 打开链接后自动继续。";
                    website.Open(service.GetWebsiteDownloadUrl(modId, file));
                    input = await pending.Result.Task.WaitAsync(token);
                    token.ThrowIfCancellationRequested();
                }
                IsWaitingForWebsite = false;
                Message = premium ? "Premium 会员：正在通过 API 下载所选文件…" : "正在下载所选文件…";
                var downloading = true;
                var progress = new InlineProgress<DownloadProgress>(value => Dispatcher.UIThread.Post(() =>
                    {
                        if (!downloading || _operation?.Token != token || token.IsCancellationRequested) return;
                        Progress = value.Percentage;
                        IsIndeterminate = value.TotalBytes <= 0;
                    }));
                if (import)
                {
                    var importArchive = ImportDownloadedArchiveAsync!;
                    await service.DownloadAndImportAsync(modId, file, input, async (archive, importToken) =>
                    {
                        downloading = false;
                        Message = "下载完成，正在导入模组…";
                        IsIndeterminate = true;
                        await importArchive(archive, importToken);
                    }, progress, token);
                    Message = "模组已下载并导入模组库。";
                }
                else
                {
                    await service.DownloadAsync(modId, file, input, destination!, progress, token);
                    Message = "压缩包已保存，可拖入窗口添加到模组库。";
                }
            }
            finally { _pending = null; IsWaitingForWebsite = false; }
        });
    }

    public bool TryReceiveProtocolLink(string input)
    {
        if (_pending is not { } pending || !IsWaitingForWebsite || _operation?.IsCancellationRequested != false || pending.Result.Task.IsCompleted)
            return false;
        try
        {
            service.ValidateProtocolTarget(pending.ModId, pending.File, input);
            pending.Result.TrySetResult(input);
        }
        catch (Exception ex) { SetError(ex, "接收网页授权"); }
        return true;
    }

    private Task RunAsync(Func<CancellationToken, Task> action) => IsBusy ? Completion : Completion = RunCoreAsync(action);
    private async Task RunCoreAsync(Func<CancellationToken, Task> action)
    {
        using var cts = new CancellationTokenSource();
        _operation = cts;
        IsBusy = true;
        Message = "";
        Progress = 0;
        IsIndeterminate = true;
        try { await action(cts.Token); }
        catch (OperationCanceledException) { Message = "操作已取消。"; }
        catch (Exception ex) { SetError(ex, "浏览 N 网模组"); }
        finally { _operation = null; IsBusy = false; IsIndeterminate = false; }
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

    public async Task StopAsync()
    {
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
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }
}

public partial class BrowseModCardViewModel(PlatformModInfo info) : ObservableObject
{
    public PlatformModInfo Info { get; } = info;
    [ObservableProperty] private Bitmap? _image;
}
