using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel
{
    public bool IsLogCategoryVisible => SelectedCategory == "日志";
    public string[] LogRecordingLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];
    [ObservableProperty] private string _logRecordingLevel = "Warning";

    /// <summary>常用保留天数；日志按天分片，超出后最旧的分片会被删除。</summary>
    public int[] LogRetentionOptions { get; } = [7, 14, 30, 60, 90, 180, 365];
    [ObservableProperty] private int _logRetentionDays = DiagnosticLogSettings.DefaultRetentionDays;

    private void LoadLogSettings()
    {
        var level = _settingsService.GetSetting(DiagnosticLogSettings.LevelKey, "Warning");
        LogRecordingLevel = LogRecordingLevels.Contains(level) ? level : "Warning";

        var retention = _settingsService.GetSetting(
            DiagnosticLogSettings.RetentionKey, DiagnosticLogSettings.DefaultRetentionDays);
        var clamped = DiagnosticLogSettings.ClampRetention(retention);
        LogRetentionDays = LogRetentionOptions.Contains(clamped) ? clamped : DiagnosticLogSettings.DefaultRetentionDays;
    }

    private async Task SaveLogSettingsAsync()
    {
        if (!LogRecordingLevels.Contains(LogRecordingLevel)) return;
        IsLoading = true;
        try
        {
            // 两个键一次提交：避免只保存一半时级别与保留天数互相矛盾。
            await _settingsService.SetSettingsAsync(new Dictionary<string, object>
            {
                [DiagnosticLogSettings.LevelKey] = LogRecordingLevel,
                [DiagnosticLogSettings.RetentionKey] = DiagnosticLogSettings.ClampRetention(LogRetentionDays)
            });
            StatusMessage = "日志设置已保存并生效";
        }
        catch (Exception ex) { StatusMessage = $"保存日志设置失败：{ex.Message}"; }
        finally { IsLoading = false; }
    }
}
