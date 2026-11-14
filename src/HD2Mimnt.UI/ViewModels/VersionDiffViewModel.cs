using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Localization;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>版本文件内容对比窗口的视图模型；按选中文件懒加载两侧内容。</summary>
public partial class VersionDiffViewModel : ObservableObject
{
    private readonly ModService _service;
    private readonly Guid _modId;
    private readonly Guid _versionId;
    private int _loadVersion;

    public string Title { get; }
    public string Direction { get; }
    public string Summary { get; }
    public IReadOnlyList<VersionFileChangeItem> Files { get; }

    [ObservableProperty] private VersionFileChangeItem? _selectedFile;
    [ObservableProperty] private IReadOnlyList<DiffLineViewModel> _lines = Array.Empty<DiffLineViewModel>();
    [ObservableProperty] private string _contentMessage = "";
    [ObservableProperty] private bool _hasContent;
    [ObservableProperty] private bool _hasMessage;
    [ObservableProperty] private bool _isLoading;

    /// <summary>最近一次内容加载任务，供调用方等待渲染完成。</summary>
    public Task LoadTask { get; private set; } = Task.CompletedTask;

    public VersionDiffViewModel(ModService service, Guid modId, Guid versionId, string title,
        string direction, string summary, IReadOnlyList<VersionFileChangeItem> files)
    {
        _service = service;
        _modId = modId;
        _versionId = versionId;
        Title = title;
        Direction = direction;
        Summary = summary;
        Files = files;
        SelectedFile = files.FirstOrDefault();
    }

    partial void OnSelectedFileChanged(VersionFileChangeItem? value) => LoadTask = LoadAsync(value);

    private async Task LoadAsync(VersionFileChangeItem? file)
    {
        var sequence = Interlocked.Increment(ref _loadVersion);
        Lines = Array.Empty<DiffLineViewModel>();
        ContentMessage = "";
        HasContent = false;
        HasMessage = false;
        if (file == null) return;

        IsLoading = true;
        try
        {
            var content = await _service.ReadLocalVersionFileAsync(_modId, _versionId, file.Path);
            if (sequence != Volatile.Read(ref _loadVersion)) return;
            if (content.TooLarge)
            {
                ShowMessage(UiLocalization.Current["Versions.Diff.TooLarge"]);
                return;
            }
            if (!content.IsText)
            {
                ShowMessage(UiLocalization.Current.Format("Versions.Diff.Binary",
                    VersionFileChangeItem.FormatSize(content.CurrentSize),
                    VersionFileChangeItem.FormatSize(content.SelectedSize),
                    content.FirstDifferenceOffset < 0 ? "—" : $"0x{content.FirstDifferenceOffset:X}",
                    content.DifferingByteCount));
                return;
            }

            var diff = await Task.Run(() => VersionDiffBuilder.Build(content.CurrentText, content.SelectedText));
            Lines = diff.Lines;
            HasContent = diff.Lines.Count > 0;
            if (diff.Approximate)
                ShowMessage(UiLocalization.Current["Versions.Diff.Approximate"]);
            else if (diff.Truncated)
                ShowMessage(UiLocalization.Current.Format("Versions.Diff.Truncated", VersionDiffBuilder.MaxRows));
            else if (!HasContent)
                ShowMessage(UiLocalization.Current["Versions.Compare.Same"]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (sequence == Volatile.Read(ref _loadVersion)) ShowMessage(ex.Message);
        }
        finally
        {
            if (sequence == Volatile.Read(ref _loadVersion)) IsLoading = false;
        }
    }

    private void ShowMessage(string message)
    {
        ContentMessage = message;
        HasMessage = true;
    }
}

/// <summary>并排对比中的一行；修改块左右成对显示，只有一侧有内容时另一侧留空。</summary>
public sealed record DiffLineViewModel(string? LeftNumber, string? RightNumber, string LeftText, string RightText,
    bool LeftChanged, bool RightChanged);
