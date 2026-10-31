using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 音频预览覆盖层的 ViewModel。加载走 ModelPreviewViewModel 同款"串行化 + 取消代际"模式；
/// 播放状态由本 VM 持有，条目行只镜像 IsPlaying 供 UI 高亮。PlaybackEnded 在音频线程触发，
/// 必须经开封送回 UI 线程后再校验代际与当前条目。
/// </summary>
public partial class AudioPreviewViewModel : ObservableObject, IAsyncDisposable
{
    private readonly AudioPreviewService _service;
    private readonly DispatcherTimer _positionTimer;
    private CancellationTokenSource? _cancellation;
    private Task _operation = Task.CompletedTask;
    private int _generation;
    private Guid _modId;
    private Guid? _profileId;
    private List<AudioEntryRow> _allRows = [];
    private List<AudioGroupHeaderRow> _allHeaders = [];
    private AudioEntryRow? _currentEntry;
    private bool _suppressPositionUpdates;
    private int _playbackGeneration;
    private bool _disposed;
    private AudioInventoryResult? _lastResult;

    public AudioPreviewViewModel(AudioPreviewService service)
    {
        _service = service;
        _positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, OnPositionTimerTick)
        {
            IsEnabled = false,
        };
        UiLocalization.Current.PropertyChanged += OnLanguageChanged;
        service.Playback.PlaybackEnded += OnPlaybackEnded;
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private string _audioFilterText = string.Empty;
    [ObservableProperty] private bool _onlyShowModified;
    [ObservableProperty] private bool _isAudioPlaying;
    [ObservableProperty] private string _audioMessageText = string.Empty;
    [ObservableProperty] private double _audioPositionSeconds;
    [ObservableProperty] private double _audioDurationSeconds;
    [ObservableProperty] private double _audioVolumePercent = 100;

    /// <summary>扁平化的展示行（分组头 + 条目），过滤时整体重建。语音包可达数千条目，
    /// 列表必须是 ListBox 虚拟化，禁止替换为无虚拟化的 ItemsControl+ScrollViewer。</summary>
    public ObservableCollection<AudioRowViewModel> Rows { get; } = [];

    public int TotalCount { get; private set; }
    public int BankCount { get; private set; }
    public int ModifiedCount { get; private set; }
    public bool HasEntries => TotalCount > 0;
    public bool HasAudioMessage => AudioMessageText.Length > 0;

    /// <summary>有游戏原版基线时才显示"只看已替换"过滤和替换标记。</summary>
    public bool HasOriginalComparison { get; private set; }

    public Task Completion => _operation;
    public bool HasError => Error.Length > 0;

    public string Summary => IsBusy ? UiLocalization.Current["AudioPreview.Loading"]
        : !HasEntries ? UiLocalization.Current["AudioPreview.Empty"]
        : UiLocalization.Current.Format("AudioPreview.Summary", TotalCount, BankCount, _lastResult?.PatchCount ?? 0);

    public string CountText => HasEntries
        ? UiLocalization.Current.Format("AudioPreview.Summary", TotalCount, BankCount, _lastResult?.PatchCount ?? 0)
        : string.Empty;

    public string ModifiedCountText => HasOriginalComparison
        ? UiLocalization.Current.Format("AudioPreview.ModifiedCount", ModifiedCount, TotalCount)
        : string.Empty;

    /// <summary>无基线（未配置游戏目录）或预算耗尽的提示；空串表示不显示。</summary>
    public string ComparisonHintText
    {
        get
        {
            if (!HasEntries)
                return string.Empty;
            if (!HasOriginalComparison)
                return UiLocalization.Current["AudioPreview.NoComparison"];
            var uncompared = _lastResult?.UncomparedEntries ?? 0;
            return uncompared > 0
                ? UiLocalization.Current.Format("AudioPreview.UncomparedHint", uncompared)
                : string.Empty;
        }
    }

    public string AudioTimeText
    {
        get
        {
            var position = TimeSpan.FromSeconds(AudioPositionSeconds);
            var duration = TimeSpan.FromSeconds(AudioDurationSeconds);
            return $"{position:hh\\:mm\\:ss} / {duration:hh\\:mm\\:ss}";
        }
    }

    public string PlaybackGlyph => IsAudioPlaying ? "\uE769" : "\uE768";
    public string PlaybackText => UiLocalization.Current[IsAudioPlaying ? "AudioPreview.Pause" : "AudioPreview.Play"];
    public string StopText => UiLocalization.Current["AudioPreview.Stop"];

    partial void OnIsOpenChanged(bool value) => OnPropertyChanged(nameof(Summary));
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(Summary));
    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnAudioMessageTextChanged(string value) => OnPropertyChanged(nameof(HasAudioMessage));
    partial void OnAudioFilterTextChanged(string value) => RebuildRows();
    partial void OnOnlyShowModifiedChanged(bool value) => RebuildRows();

    partial void OnIsAudioPlayingChanged(bool value)
    {
        OnPropertyChanged(nameof(PlaybackGlyph));
        OnPropertyChanged(nameof(PlaybackText));
    }

    partial void OnAudioPositionSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(AudioTimeText));
        // 暂停状态下也允许拖动进度条（NVorbis 的 SamplePosition setter 在暂停时同样有效）。
        if (!_suppressPositionUpdates && _currentEntry is not null && _service.Playback.Duration > TimeSpan.Zero)
            _service.Playback.Seek(value);
    }

    partial void OnAudioDurationSecondsChanged(double value) => OnPropertyChanged(nameof(AudioTimeText));

    partial void OnAudioVolumePercentChanged(double value) => _service.Playback.SetVolume((float)(value / 100.0));

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ModifiedCountText));
        OnPropertyChanged(nameof(ComparisonHintText));
        OnPropertyChanged(nameof(PlaybackText));
        OnPropertyChanged(nameof(StopText));
        RebuildRows();
    }

    public Task OpenAsync(Guid modId, Guid? profileId = null)
    {
        IsOpen = true;
        _modId = modId;
        _profileId = profileId;
        ClearContent();
        return RunAsync(LoadCoreAsync);
    }

    private async Task LoadCoreAsync(CancellationToken token)
    {
        var source = await _service.GetSourceAsync(_modId, _profileId, token);
        token.ThrowIfCancellationRequested();
        Title = source.Name;
        var result = await _service.LoadAsync(_modId, _profileId, token);
        token.ThrowIfCancellationRequested();
        _lastResult = result;
        ApplyInventory(result);
        if (!string.IsNullOrEmpty(result.Error))
            AudioMessageText = UiLocalization.Current.Format("AudioPreview.LoadFailed", result.Error);
        OnPropertyChanged(nameof(Summary));
    }

    private void ApplyInventory(AudioInventoryResult result)
    {
        StopAudioPlayback(clearCurrent: true);
        AudioMessageText = string.Empty;

        _allRows.Clear();
        _allHeaders.Clear();
        foreach (var group in result.Groups)
        {
            var header = new AudioGroupHeaderRow(
                group.BankName is { Length: > 0 } bankName ? bankName
                    : group.BankFileId != 0 ? string.Create(CultureInfo.InvariantCulture, $"Bank 0x{group.BankFileId:X16}")
                    : UiLocalization.Current["AudioPreview.LooseStreamsGroup"],
                group.PatchRelativePath,
                group.Entries.Count);
            _allHeaders.Add(header);
            foreach (var entry in group.Entries)
                _allRows.Add(new AudioEntryRow(entry, header));
        }
        if (_currentEntry is { } current && !_allRows.Contains(current))
            _currentEntry = null;

        TotalCount = _allRows.Count;
        BankCount = result.Groups.Count(static group => group.BankName is not null);
        ModifiedCount = _allRows.Count(static row => row.Model.MatchesOriginal == false);
        HasOriginalComparison = _allRows.Any(static row => row.Model.MatchesOriginal is not null);
        if (!HasOriginalComparison)
            OnlyShowModified = false;

        RebuildRows();
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(BankCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasOriginalComparison));
        OnPropertyChanged(nameof(ModifiedCount));
        OnPropertyChanged(nameof(ModifiedCountText));
        OnPropertyChanged(nameof(ComparisonHintText));
        OnPropertyChanged(nameof(Summary));
    }

    private void RebuildRows()
    {
        // 重建时保持分组头与条目的原始顺序；过滤命中任一条目则该分组头保留。
        Rows.Clear();
        var filter = AudioFilterText.Trim();
        var visibleRows = new List<AudioEntryRow>();
        foreach (var row in _allRows)
        {
            if (OnlyShowModified && row.Model.MatchesOriginal != false)
                continue;
            if (filter.Length > 0 && !row.MatchesFilter(filter))
                continue;
            visibleRows.Add(row);
        }

        var includedHeaders = new HashSet<AudioGroupHeaderRow>();
        foreach (var row in visibleRows)
            includedHeaders.Add(row.Header);

        foreach (var header in _allHeaders)
        {
            if (includedHeaders.Contains(header))
                Rows.Add(header);
        }
        foreach (var row in visibleRows)
            Rows.Add(row);
    }

    [RelayCommand]
    private async Task ToggleEntryPlaybackAsync(AudioEntryRow? row)
    {
        if (_disposed || row is null || !_allRows.Contains(row))
            return;
        if (ReferenceEquals(_currentEntry, row))
        {
            if (_service.Playback.State == AudioPlaybackState.Playing)
            {
                _service.Playback.Pause();
                IsAudioPlaying = false;
                row.SetPlaying(false);
                return;
            }
            if (_service.Playback.State == AudioPlaybackState.Paused)
            {
                _service.Playback.Resume();
                IsAudioPlaying = true;
                row.SetPlaying(true);
                return;
            }
        }

        if (!row.IsPlayable)
        {
            AudioMessageText = row.IssueText;
            return;
        }

        AudioMessageText = string.Empty;
        IsBusy = true;
        var generation = ++_playbackGeneration;
        try
        {
            var (success, error) = await _service.Playback.PlayAsync(row.Model, CancellationToken.None);
            if (_disposed || generation != _playbackGeneration)
                return;
            if (success)
            {
                SetCurrentEntry(row);
                IsAudioPlaying = true;
                row.SetPlaying(true);
                _suppressPositionUpdates = true;
                try
                {
                    AudioPositionSeconds = 0;
                    AudioDurationSeconds = _service.Playback.Duration.TotalSeconds;
                }
                finally
                {
                    _suppressPositionUpdates = false;
                }
                _positionTimer.Start();
            }
            else if (error is not null)
            {
                AudioMessageText = UiLocalization.Current.Format("AudioPreview.PlaybackFailed", error);
            }
        }
        catch (Exception ex)
        {
            if (_disposed || generation != _playbackGeneration)
                return;
            AudioMessageText = UiLocalization.Current.Format("AudioPreview.PlaybackFailed", ex.Message);
        }
        finally
        {
            if (!_disposed && generation == _playbackGeneration)
                IsBusy = false;
        }
    }

    [RelayCommand]
    private Task TogglePlaybackAsync() => _currentEntry is { } entry
        ? ToggleEntryPlaybackAsync(entry)
        : Task.CompletedTask;

    [RelayCommand]
    private void StopAudio() => StopAudioPlayback(clearCurrent: true);

    private void StopAudioPlayback(bool clearCurrent)
    {
        ++_playbackGeneration;
        IsBusy = false;
        _positionTimer.Stop();
        _service.Playback.Stop();
        IsAudioPlaying = false;
        _suppressPositionUpdates = true;
        try
        {
            AudioPositionSeconds = 0;
            AudioDurationSeconds = 0;
        }
        finally
        {
            _suppressPositionUpdates = false;
        }
        if (clearCurrent)
            SetCurrentEntry(null);
        else
            _currentEntry?.SetPlaying(false);
    }

    private void SetCurrentEntry(AudioEntryRow? row)
    {
        if (ReferenceEquals(_currentEntry, row))
            return;
        var old = _currentEntry;
        _currentEntry = row;
        old?.SetPlaying(false);
    }

    private void OnPositionTimerTick(object? sender, EventArgs e)
    {
        if (_service.Playback.State is not (AudioPlaybackState.Playing or AudioPlaybackState.Paused))
            return;
        _suppressPositionUpdates = true;
        try
        {
            AudioPositionSeconds = _service.Playback.Position.TotalSeconds;
            var duration = _service.Playback.Duration.TotalSeconds;
            if (duration > 0 && Math.Abs(AudioDurationSeconds - duration) > 0.001)
                AudioDurationSeconds = duration;
        }
        finally
        {
            _suppressPositionUpdates = false;
        }
    }

    private void OnPlaybackEnded(AudioEntry entry, string? error)
    {
        // PlaybackStopped 在音频线程触发；以下都是 UI 绑定状态，必须封送回 UI 线程。
        void Apply()
        {
            if (_disposed || !ReferenceEquals(_currentEntry?.Model, entry))
                return;
            IsAudioPlaying = false;
            _positionTimer.Stop();
            _suppressPositionUpdates = true;
            try
            {
                // 播放自然结束后进度归零，避免滑块停留在末端。
                AudioPositionSeconds = 0;
            }
            finally
            {
                _suppressPositionUpdates = false;
            }
            SetCurrentEntry(null);
            if (error is not null)
                AudioMessageText = UiLocalization.Current.Format("AudioPreview.PlaybackFailed", error);
        }

        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            Dispatcher.UIThread.Post(Apply);
    }

    private Task RunAsync(Func<CancellationToken, Task> work)
    {
        _cancellation?.Cancel();
        var previous = _operation;
        var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        var generation = ++_generation;
        IsBusy = true; Error = string.Empty;
        _operation = ExecuteAsync();
        return _operation;

        async Task ExecuteAsync()
        {
            try
            {
                await previous;
                cancellation.Token.ThrowIfCancellationRequested();
                await work(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (generation == _generation) Error = error.Message;
            }
            finally
            {
                if (generation == _generation) { IsBusy = false; _cancellation = null; }
                cancellation.Dispose();
            }
        }
    }

    [RelayCommand]
    private void Cancel() => _cancellation?.Cancel();

    [RelayCommand]
    private Task CloseAsync() => StopAsync();

    public async Task StopAsync()
    {
        IsOpen = false;
        var generation = ++_generation;
        _cancellation?.Cancel(); _cancellation = null;
        ClearContent();
        await _operation;
        if (generation == _generation) IsBusy = false;
    }

    private void ClearContent()
    {
        // 注意不清 _modId/_profileId：OpenAsync 先赋值再清理内容，加载要用它们。
        StopAudioPlayback(clearCurrent: true);
        _lastResult = null;
        Title = string.Empty;
        Error = string.Empty;
        AudioMessageText = string.Empty;
        _allRows.Clear();
        _allHeaders.Clear();
        Rows.Clear();
        TotalCount = 0;
        BankCount = 0;
        ModifiedCount = 0;
        HasOriginalComparison = false;
        OnlyShowModified = false;
        OnPropertyChanged(nameof(HasEntries));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(BankCount));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(HasOriginalComparison));
        OnPropertyChanged(nameof(ModifiedCountText));
        OnPropertyChanged(nameof(Summary));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        UiLocalization.Current.PropertyChanged -= OnLanguageChanged;
        _service.Playback.PlaybackEnded -= OnPlaybackEnded;
        _positionTimer.Stop();
        StopAudioPlayback(clearCurrent: true);
        return ValueTask.CompletedTask;
    }
}

/// <summary>音频列表的扁平行基类：分组头行与条目行共用一个虚拟化 ListBox。</summary>
public abstract class AudioRowViewModel : ObservableObject;

public partial class AudioGroupHeaderRow(string header, string patchPath, int entryCount) : AudioRowViewModel
{
    public string Header { get; } = header;
    public string PatchPath { get; } = patchPath;
    public int EntryCount { get; } = entryCount;
}

/// <summary>包装一个解析出的 <see cref="AudioEntry"/> 供列表展示；播放状态由页面 VM 驱动镜像。</summary>
public partial class AudioEntryRow(AudioEntry model, AudioGroupHeaderRow header) : AudioRowViewModel
{
    public AudioEntry Model { get; } = model;
    public AudioGroupHeaderRow Header { get; } = header;

    public bool IsPlayable => Model.IsPlayable;

    public string IdText => Model.SourceId.ToString(CultureInfo.InvariantCulture);

    public string DisplayTitle => $"#{IdText}";

    public string OriginText => Model.Origin switch
    {
        AudioEntryOrigin.BankMedia => "Bank",
        _ => "Stream",
    };

    public string FormatText => Model.SampleRate > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{Model.SampleRate / 1000.0:0.#} kHz · {Model.Channels}ch")
        : "—";

    public string SizeText => Model.SizeBytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{Model.SizeBytes / (1024.0 * 1024.0):0.#} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{Model.SizeBytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{Model.SizeBytes} B"),
    };

    public string ModifiedText => Model.MatchesOriginal switch
    {
        false => UiLocalization.Current["AudioPreview.Modified"],
        true => UiLocalization.Current["AudioPreview.Original"],
        _ => string.Empty,
    };

    /// <summary>已替换角标的强调样式开关（原版条目保持普通外观）。</summary>
    public bool IsReplaced => Model.MatchesOriginal == false;

    public string IssueText => Model.Issue == AudioEntryIssue.None
        ? string.Empty
        : Model.Issue switch
        {
            AudioEntryIssue.NotRiff => UiLocalization.Current["AudioPreview.IssueNotRiff"],
            AudioEntryIssue.NotVorbis => UiLocalization.Current["AudioPreview.IssueNotVorbis"],
            AudioEntryIssue.Truncated => UiLocalization.Current["AudioPreview.IssueTruncated"],
            _ => UiLocalization.Current["AudioPreview.IssueReadFailed"],
        };

    private bool _isPlaying;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (_isPlaying == value)
                return;
            _isPlaying = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlayGlyph));
        }
    }

    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";

    /// <summary>页面 ViewModel 持有播放状态；条目行只为 UI 镜像它。</summary>
    public void SetPlaying(bool playing) => IsPlaying = playing;

    public bool MatchesFilter(string filter)
    {
        if (filter.Length == 0)
            return true;
        return IdText.Contains(filter, StringComparison.Ordinal) ||
               OriginText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
               (Model.BankName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
               Model.PatchRelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase);
    }
}
