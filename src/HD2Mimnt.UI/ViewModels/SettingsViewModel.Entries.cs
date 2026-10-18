using CommunityToolkit.Mvvm.ComponentModel;
using HD2Mimnt.Application.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class SettingsViewModel
{
    private HashSet<string> _hiddenEntries = new(StringComparer.Ordinal);

    public IReadOnlyList<EntryVisibilityOption> NavigationEntries { get; } = new[]
    {
        new EntryVisibilityOption("navigation.profiles", "配置文件"),
        new EntryVisibilityOption("navigation.library", "模组库"),
        new EntryVisibilityOption("navigation.browse", "浏览模组"),
        new EntryVisibilityOption("navigation.mod-creation", "模组创建"),
        new EntryVisibilityOption("navigation.conflicts", "冲突"),
        new EntryVisibilityOption("navigation.tools", "工具")
    };

    public IReadOnlyList<EntryVisibilityOption> SettingsEntries { get; } = new[]
    {
        new EntryVisibilityOption("settings.game", "游戏"),
        new EntryVisibilityOption("settings.profiles", "配置文件"),
        new EntryVisibilityOption("settings.deployment", "部署"),
        new EntryVisibilityOption("settings.library", "模组库"),
        new EntryVisibilityOption("settings.compatibility", "兼容性"),
        new EntryVisibilityOption("settings.downloads", "下载"),
        new EntryVisibilityOption("settings.account", "账号"),
        new EntryVisibilityOption("settings.storage", "存储位置"),
        new EntryVisibilityOption("settings.appearance", "外观"),
        new EntryVisibilityOption("settings.cache", "缓存"),
        new EntryVisibilityOption("settings.logs", "日志")
    };

    public bool ShowProfilesEntry => !_hiddenEntries.Contains("navigation.profiles");
    public bool ShowLibraryEntry => !_hiddenEntries.Contains("navigation.library");
    public bool ShowBrowseEntry => !_hiddenEntries.Contains("navigation.browse");
    public bool ShowModCreationEntry => !_hiddenEntries.Contains("navigation.mod-creation");
    public bool ShowConflictsEntry => !_hiddenEntries.Contains("navigation.conflicts");
    public bool ShowToolsEntry => !_hiddenEntries.Contains("navigation.tools");
    public bool IsEntriesCategoryVisible => SelectedCategory == "入口自定义";

    public IEnumerable<string> SettingsCategories => SettingsEntries
        .Where(entry => !_hiddenEntries.Contains(entry.Id)).Select(entry => entry.Label)
        .Append("入口自定义");

    private void LoadEntryVisibility()
    {
        var selectedCategory = SelectedCategory;
        var hidden = _settingsService.GetSetting(SettingKeys.HiddenEntries, Array.Empty<string>());
        _hiddenEntries = new HashSet<string>(hidden, StringComparer.Ordinal);
        foreach (var entry in NavigationEntries.Concat(SettingsEntries))
            entry.IsVisible = !_hiddenEntries.Contains(entry.Id);
        OnPropertyChanged(nameof(ShowProfilesEntry));
        OnPropertyChanged(nameof(ShowLibraryEntry));
        OnPropertyChanged(nameof(ShowBrowseEntry));
        OnPropertyChanged(nameof(ShowModCreationEntry));
        OnPropertyChanged(nameof(ShowConflictsEntry));
        OnPropertyChanged(nameof(ShowToolsEntry));
        OnPropertyChanged(nameof(SettingsCategories));
        SelectedCategory = SettingsCategories.Contains(selectedCategory) ? selectedCategory : "入口自定义";
    }

    private async Task SaveEntryVisibilityAsync()
    {
        var hidden = NavigationEntries.Concat(SettingsEntries)
            .Where(entry => !entry.IsVisible).Select(entry => entry.Id).ToArray();
        await _settingsService.SetSettingAsync(SettingKeys.HiddenEntries, hidden);
        LoadEntryVisibility();
    }
}

public partial class EntryVisibilityOption(string id, string label) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool _isVisible = true;
}
