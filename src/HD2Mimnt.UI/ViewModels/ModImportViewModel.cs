using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModImportViewModel(ModService modService, SettingsService? settingsService = null) : ObservableObject
{
    public ArchivePasswordsViewModel Passwords { get; } = new(settingsService);
    private TaskCompletionSource<string>? _passwordRequest;
    [ObservableProperty] private bool _hasPasswordRequest;
    [ObservableProperty] private string _passwordArchive = "";

    [RelayCommand]
    private void SubmitPassword()
    {
        if (!string.IsNullOrEmpty(Passwords.Password)) _passwordRequest?.TrySetResult(Passwords.Password);
    }

    private async Task<string> RequestPasswordAsync(string name, CancellationToken token)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _passwordRequest = completion;
            PasswordArchive = name;
            HasPasswordRequest = true;
            IsIndeterminate = false;
        });
        try { return await completion.Task.WaitAsync(token); }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                HasPasswordRequest = false;
                _passwordRequest = null;
            });
        }
    }

    private CancellationTokenSource? _cancellation;
    private int _fileGeneration;
    private TaskCompletionSource? _hd2IrConfirmation;

    [ObservableProperty] private Hd2IrImportPreview? _pendingHd2Ir;
    public bool HasPendingHd2Ir => PendingHd2Ir != null;
    partial void OnPendingHd2IrChanged(Hd2IrImportPreview? value) => OnPropertyChanged(nameof(HasPendingHd2Ir));

    [RelayCommand]
    private void ConfirmHd2Ir() => _hd2IrConfirmation?.TrySetResult();

    private async Task ConfirmHd2IrAsync(Hd2IrImportPreview preview, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _hd2IrConfirmation = completion;
            PendingHd2Ir = preview;
            Step = "确认 HD2IAS 组件组合";
            IsIndeterminate = false;
        });
        try { await completion.Task.WaitAsync(token); }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                PendingHd2Ir = null;
                _hd2IrConfirmation = null;
            });
        }
    }

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect), nameof(CanClose), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand), nameof(CancelCommand))]
    private bool _isImporting;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelling;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _currentFile = string.Empty;
    [ObservableProperty] private string _step = "选择要添加的模组";
    [ObservableProperty] private string _detail = "支持批量选择压缩包，也可以添加模组文件夹。";
    [ObservableProperty] private double _fileProgress;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(RemainingCount))]
    private int _totalCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(RemainingCount))]
    private int _processedCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    private int _successCount;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    private int _failedCount;

    public ObservableCollection<string> Errors { get; } = new();

    /// <summary>整批导入后（自动加入当前配置时）的可选分组；不指定则保持追加到末尾。</summary>
    public ObservableCollection<ProfileGroupChoiceViewModel> ImportGroupChoices { get; } = new();

    [ObservableProperty]
    private bool _isImportGroupSelectionVisible;

    [ObservableProperty]
    private ProfileGroupChoiceViewModel? _selectedImportGroup;

    public Guid ImportGroupProfileId { get; private set; }

    public void ShowImportGroupSelection(Guid profileId, IEnumerable<ProfileSeparator> separators)
    {
        ImportGroupProfileId = profileId;
        ImportGroupChoices.Clear();
        foreach (var separator in separators.OrderBy(item => item.Order))
            ImportGroupChoices.Add(new ProfileGroupChoiceViewModel(separator.Id, separator.Name));
        SelectedImportGroup = ImportGroupChoices.FirstOrDefault();
        IsImportGroupSelectionVisible = false;
    }

    public void DismissImportGroupSelection()
    {
        IsImportGroupSelectionVisible = false;
        ImportGroupChoices.Clear();
        SelectedImportGroup = null;
        ImportGroupProfileId = Guid.Empty;
    }

    private readonly List<Guid> _importedIds = [];
    /// <summary>本批次成功落库的模组 ID；新批次开始时清空。</summary>
    public IReadOnlyList<Guid> ImportedIds => _importedIds;
    public bool CanSelect => !IsImporting;
    public bool CanClose => !IsImporting;
    public bool CanCancel => IsImporting && !IsCancelling;
    public int RemainingCount => Math.Max(0, TotalCount - ProcessedCount);
    public string CountText => $"已处理 {ProcessedCount} / {TotalCount} · 剩余 {RemainingCount} 个";
    public string ResultText => $"成功 {SuccessCount} 个 · 失败 {FailedCount} 个";
    public Task Completion { get; private set; } = Task.CompletedTask;

    public void Open()
    {
        if (!IsOpen && !IsImporting) { Reset(); Passwords.Reload(); }
        IsOpen = true;
    }

    private void Reset()
    {
        HasResult = false;
        Errors.Clear();
        DismissImportGroupSelection();
        _importedIds.Clear();
        TotalCount = ProcessedCount = SuccessCount = FailedCount = 0;
        FileProgress = OverallProgress = 0;
        CurrentFile = string.Empty;
        Step = "选择要添加的模组";
        Detail = "支持批量选择压缩包，也可以添加模组文件夹。";
        IsIndeterminate = IsCancelling = false;
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close() => IsOpen = false;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        Step = "正在取消";
        Detail = "正在停止当前操作并清理临时文件，已添加的模组会保留。";
        _cancellation?.Cancel();
    }

    public Task ImportAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default, ModPlatformLink? sourceLink = null,
        Func<Task>? prepareGroupSelection = null)
    {
        if (IsImporting) return Task.CompletedTask;
        var sources = paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) return Task.CompletedTask;
        Reset();
        TotalCount = sources.Count;
        IsOpen = IsImporting = true;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Completion = RunAsync(sources, _cancellation.Token, sourceLink, prepareGroupSelection);
        return Completion;
    }

    private async Task RunAsync(IReadOnlyList<string> sources, CancellationToken token, ModPlatformLink? sourceLink,
        Func<Task>? prepareGroupSelection)
    {
        if (prepareGroupSelection != null)
        {
            try { await prepareGroupSelection(); }
            catch { /* 分组选择失败时保持原追加逻辑 */ }
        }
        using var passwordScope = PasswordAwareArchiveExtractor.BeginRequest(RequestPasswordAsync);
        var cancelled = false;
        try
        {
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                CurrentFile = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                FileProgress = 0;
                Step = "准备添加";
                Detail = source;
                var generation = ++_fileGeneration;
                var progress = new ImportProgress(value =>
                {
                    void Apply()
                    {
                        // 旧文件或已结束批次的排队回调不能覆盖新状态。
                        if (!IsImporting || IsCancelling || generation != _fileGeneration) return;
                        Step = value.Step;
                        Detail = value.Detail;
                        IsIndeterminate = value.IsIndeterminate;
                        FileProgress = Math.Clamp(value.Percentage, 0, 100);
                        OverallProgress = (ProcessedCount * 100d + FileProgress) / TotalCount;
                    }
                    if (Dispatcher.UIThread.CheckAccess()) Apply();
                    // 后台阶段完成前交付 UI，避免小文件所有阶段排队到批次结束才被丢弃。
                    else Dispatcher.UIThread.Invoke(Apply);
                });
                try
                {
                    var modId = await Task.Run(() => modService.ImportModAsync(source, token, progress, sourceLink, ConfirmHd2IrAsync), token);
                    _importedIds.Add(modId);
                    SuccessCount++;
                    FileProgress = 100;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    FailedCount++;
                    Errors.Add($"{CurrentFile}：{ex.Message}");
                }
                ++_fileGeneration;
                IsIndeterminate = false;
                ProcessedCount++;
                OverallProgress = ProcessedCount * 100d / TotalCount;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }
        finally
        {
            ++_fileGeneration;
            IsIndeterminate = false;
            HasResult = true;
            Step = cancelled ? "添加已取消" : FailedCount > 0 ? "添加结束，部分模组失败" : "添加完成";
            Detail = cancelled ? $"还有 {RemainingCount} 个未完成；已添加的模组会保留。" : "可关闭浮窗查看模组库，或继续添加。";
            IsImporting = IsCancelling = false;
            IsImportGroupSelectionVisible = ImportGroupChoices.Count > 0;
            _cancellation?.Dispose();
            _cancellation = null;
            if (!cancelled && !token.IsCancellationRequested && FailedCount == 0 && Errors.Count == 0
                && !IsImportGroupSelectionVisible
                && settingsService?.GetSetting(SettingKeys.ImportAutoClose, false) == true)
                IsOpen = false;
        }
    }

    private sealed class ImportProgress(Action<ModImportProgress> report) : IProgress<ModImportProgress>
    {
        public void Report(ModImportProgress value) => report(value);
    }
}
