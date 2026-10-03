using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.UI.Services;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly IFileLogService _logService;

    [ObservableProperty] private ObservableCollection<string> _entries = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _errorsOnly;

    private IReadOnlyList<string> _allEntries = Array.Empty<string>();

    public event EventHandler? ExportRequested;
    public event EventHandler? CopyRequested;

    public LogViewModel(IFileLogService logService) => _logService = logService;

    public async Task InitializeAsync() => await RefreshAsync();

    partial void OnErrorsOnlyChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var lines = await _logService.ReadRecentAsync(500);
            _allEntries = lines;
            ApplyFilter();
            StatusMessage = $"已加载最近 {Entries.Count} 条日志";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取日志失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Export() => ExportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Copy() => CopyRequested?.Invoke(this, EventArgs.Empty);

    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        try
        {
            await _logService.ExportAsync(destinationPath, cancellationToken);
            StatusMessage = "日志已导出";
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出日志失败：{ex.Message}";
        }
    }

    public string GetRecentText() => string.Join(Environment.NewLine, Entries);

    private void ApplyFilter()
    {
        var lines = ErrorsOnly
            ? _allEntries.Where(line => line.Contains("[Error]", StringComparison.OrdinalIgnoreCase) || line.Contains("错误", StringComparison.OrdinalIgnoreCase))
            : _allEntries;
        Entries = new ObservableCollection<string>(lines);
    }
}
