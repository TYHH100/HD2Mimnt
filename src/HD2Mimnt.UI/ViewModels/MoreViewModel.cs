using CommunityToolkit.Mvvm.ComponentModel;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 工具页左侧分类导航的状态（纯 UI 状态，内容仍由各子 ViewModel 提供）。
/// </summary>
public partial class MoreViewModel : ObservableObject
{
    public string[] Categories { get; } = { "教程", "插件", "备份与恢复", "管理器迁移", "日志", "外部工具", "开发工具", "关于" };

    [ObservableProperty]
    private string _selectedCategory = "插件";

    public bool IsPluginsVisible => SelectedCategory == "插件";
    public bool IsBackupVisible => SelectedCategory == "备份与恢复";
    public bool IsLogsVisible => SelectedCategory == "日志";
    public bool IsExternalToolsVisible => SelectedCategory == "外部工具";
    public bool IsDevToolsVisible => SelectedCategory == "开发工具";
    public bool IsAboutVisible => SelectedCategory == "关于";
    public bool IsTutorialVisible => SelectedCategory == "教程";
    public bool IsMigrationVisible => SelectedCategory == "管理器迁移";

    partial void OnSelectedCategoryChanged(string value)
    {
        OnPropertyChanged(nameof(IsPluginsVisible));
        OnPropertyChanged(nameof(IsBackupVisible));
        OnPropertyChanged(nameof(IsLogsVisible));
        OnPropertyChanged(nameof(IsExternalToolsVisible));
        OnPropertyChanged(nameof(IsDevToolsVisible));
        OnPropertyChanged(nameof(IsAboutVisible));
        OnPropertyChanged(nameof(IsTutorialVisible));
        OnPropertyChanged(nameof(IsMigrationVisible));
    }
}
