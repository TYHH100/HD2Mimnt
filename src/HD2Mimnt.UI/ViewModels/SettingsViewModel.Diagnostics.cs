using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel
{
    public bool IsLogCategoryVisible => SelectedCategory == "日志";
    public string[] LogRecordingLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error", "Critical", "None"];
    [ObservableProperty] private string _logRecordingLevel = "Warning";

    private void LoadLogSettings()
    {
        var level = _settingsService.GetSetting(DiagnosticLogSettings.LevelKey, "Warning");
        LogRecordingLevel = LogRecordingLevels.Contains(level) ? level : "Warning";
    }

    private async Task SaveLogSettingsAsync()
    {
        if (!LogRecordingLevels.Contains(LogRecordingLevel)) return;
        IsLoading = true;
        try
        {
            await _settingsService.SetSettingAsync(DiagnosticLogSettings.LevelKey, LogRecordingLevel);
            StatusMessage = "日志设置已保存并生效";
        }
        catch (Exception ex) { StatusMessage = $"保存日志设置失败：{ex.Message}"; }
        finally { IsLoading = false; }
    }
}
