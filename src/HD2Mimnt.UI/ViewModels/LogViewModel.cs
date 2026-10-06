using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.UI.Services;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace HD2Mimnt.UI.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly IFileLogService _logService;

    private static readonly Regex LogLineRegex = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}[.\d]*\s*[+-]\d{2}:\d{2})\s+\[(?<level>[^\]]+)\]\s+(?<logger>[^:]+):\s?(?<message>.*)$",
        RegexOptions.Compiled);

    [ObservableProperty] private ObservableCollection<LogEntryViewModel> _entryItems = new();
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
            StatusMessage = $"已加载最近 {EntryItems.Count} 条日志";
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
        EntryItems = new ObservableCollection<LogEntryViewModel>(lines.Select(ParseEntry));
    }

    private static LogEntryViewModel ParseEntry(string line)
    {
        var match = LogLineRegex.Match(line);
        if (!match.Success)
        {
            return new LogEntryViewModel { Message = line, Raw = line };
        }

        var level = match.Groups["level"].Value;
        var logger = match.Groups["logger"].Value;
        var shortLogger = logger.Length == 0 ? logger : logger.Substring(logger.LastIndexOf('.') + 1);
        return new LogEntryViewModel
        {
            TimestampText = match.Groups["timestamp"].Value,
            LevelText = level,
            LevelBrush = LevelToBrush(level),
            Logger = shortLogger,
            Message = match.Groups["message"].Value,
            Raw = line
        };
    }

    private static IBrush LevelToBrush(string level) => level.ToLowerInvariant() switch
    {
        "warning" => Solid("#FBBF24"),
        "error" or "fatal" or "critical" => Solid("#F87171"),
        "debug" or "trace" => Solid("#64748B"),
        "information" or "info" => Solid("#3B82F6"),
        _ => Solid("#9AAAC6")
    };

    private static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex));
}

public sealed class LogEntryViewModel
{
    public string TimestampText { get; init; } = string.Empty;
    public string LevelText { get; init; } = string.Empty;
    public IBrush LevelBrush { get; init; } = Brushes.Gray;
    public string Logger { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Raw { get; init; } = string.Empty;
}
