using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Search;
using HD2Mimnt.Application.Services;
using HD2Mimnt.UI.Services;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HD2Mimnt.UI.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly IFileLogService _logService;
    private readonly SettingsService? _settingsService;
    private static readonly Regex LogLineRegex = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}[.\d]*\s*[+-]\d{2}:\d{2})\s+\[(?<level>[^\]]+)\]\s+(?<logger>[^:]+):\s?(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.Singleline);
    [ObservableProperty] private ObservableCollection<LogEntryViewModel> _entryItems = new();
    [ObservableProperty] private ObservableCollection<string> _entries = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _displayLevel = "Trace";
    [ObservableProperty] private int _recentLimit = 500;
    [ObservableProperty] private LogEntryViewModel? _selectedEntry;
    private IReadOnlyList<LogEntryViewModel> _allEntries = [];
    public string[] DisplayLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    public int[] RecentLimits { get; } = [100, 500, 1000, 2000];
    public string LogFilePath => _logService.LogFilePath;
    public event EventHandler? ExportRequested;
    public event EventHandler? CopyRequested;

    public LogViewModel(IFileLogService logService, SettingsService? settingsService = null)
    {
        _logService = logService;
        _settingsService = settingsService;
        if (_settingsService != null) _settingsService.SettingChanged += OnSearchSettingsChanged;
    }
    public Task InitializeAsync() => RefreshAsync();
    partial void OnErrorsOnlyChanged(bool value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnDisplayLevelChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var lines = await _logService.ReadRecentAsync(Math.Clamp(RecentLimit, 100, 2000) * 10);
            var records = new List<string>();
            foreach (var line in lines)
            {
                if (LogLineRegex.IsMatch(line) || records.Count == 0) records.Add(line);
                else records[^1] += Environment.NewLine + line;
            }
            _allEntries = records.TakeLast(Math.Clamp(RecentLimit, 100, 2000)).Select(ParseEntry).Reverse().ToArray();
            ApplyFilter();
        }
        catch (Exception ex) { StatusMessage = $"读取日志失败：{ex.Message}"; }
        finally { IsLoading = false; }
    }
    [RelayCommand] private void Export() => ExportRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void Copy() => CopyRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void CopySelected()
    {
        if (SelectedEntry != null) CopyRequested?.Invoke(this, new CopyLogEventArgs(SelectedEntry.Raw));
    }
    public async Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        try { await _logService.ExportAsync(destinationPath, cancellationToken); StatusMessage = "日志已导出"; }
        catch (Exception ex) { StatusMessage = $"导出日志失败：{ex.Message}"; }
    }
    public string GetRecentText() => string.Join(Environment.NewLine, Entries);
    private void ApplyFilter()
    {
        Enum.TryParse<LogLevel>(DisplayLevel, out var threshold);
        if (ErrorsOnly) threshold = LogLevel.Error;
        var selectedRaw = SelectedEntry?.Raw;
        var query = SearchText.Trim();
        var options = SearchMatchOptions.FromSettings(_settingsService);
        var filtered = _allEntries.Where(item => item.Level >= threshold &&
            (query.Length == 0 || SearchMatcher.Match(item.Raw, query, options) != SearchMatchKind.None)).ToArray();
        EntryItems = new(filtered);
        Entries = new(filtered.Select(item => item.Raw));
        SelectedEntry = filtered.FirstOrDefault(item => item.Raw == selectedRaw) ?? filtered.FirstOrDefault();
        StatusMessage = $"显示 {filtered.Length} / {_allEntries.Count} 条日志（最新在前）";
    }
    /// <summary>设置页保存搜索选项后立即刷新当前日志筛选。</summary>
    private void OnSearchSettingsChanged(object? sender, SettingChangedEventArgs e)
    {
        if (e.Key is not (SettingKeys.SearchFuzzy or SettingKeys.SearchPinyin)) return;
        if (global::Avalonia.Application.Current == null
            || global::Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) ApplyFilter();
        else global::Avalonia.Threading.Dispatcher.UIThread.Post(ApplyFilter);
    }

    private static LogEntryViewModel ParseEntry(string line)
    {
        var match = LogLineRegex.Match(line);
        if (!match.Success) return new() { Message = line, Raw = line };
        return new()
        {
            TimestampText = match.Groups["timestamp"].Value,
            LevelText = match.Groups["level"].Value,
            Logger = match.Groups["logger"].Value,
            Message = match.Groups["message"].Value,
            Raw = line
        };
    }
}

public sealed class CopyLogEventArgs(string text) : EventArgs { public string Text { get; } = text; }

public sealed class LogEntryViewModel
{
    public string TimestampText { get; init; } = string.Empty;
    public string LevelText { get; init; } = string.Empty;
    public LogLevel Level => Enum.TryParse<LogLevel>(LevelText, true, out var level) ? level : LogLevel.Information;
    public bool IsWarning => Level == LogLevel.Warning;
    public bool IsError => Level is LogLevel.Error or LogLevel.Critical;
    public bool IsInformation => Level == LogLevel.Information;
    public bool IsDiagnostic => Level is LogLevel.Debug or LogLevel.Trace;
    public string Logger { get; init; } = string.Empty;
    public string ShortLogger => Logger[(Logger.LastIndexOf('.') + 1)..];
    public string Message { get; init; } = string.Empty;
    public string Summary => Message.Split(["; Headers=", "\r", "\n"], StringSplitOptions.None)[0];
    public string Raw { get; init; } = string.Empty;
    public string Detail
    {
        get
        {
            var text = Message;
            var headers = text.IndexOf("; Headers=", StringComparison.Ordinal);
            var body = text.IndexOf("; Body=", StringComparison.Ordinal);
            if (headers < 0 || body < headers) return Raw;
            string Pretty(string json)
            {
                try
                {
                    using var document = JsonDocument.Parse(json);
                    return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
                    { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }
                catch (JsonException) { return json; }
            }
            return $"Body:\n{Pretty(text[(body + 7)..])}\n\nHeaders:\n{Pretty(text[(headers + 10)..body])}\n\n{TimestampText} [{LevelText}]\n{Logger}\n{text[..headers]}";
        }
    }
}
