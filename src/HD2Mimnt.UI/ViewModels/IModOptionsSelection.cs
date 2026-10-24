using System.Windows.Input;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>
/// 实际管理器和创建预览共用的选项选择界面。
/// HD2IAS 角色包生成组件分组时，一级显示组件列表，二级显示该组件的选项与子选项。
/// </summary>
public interface IModOptionsSelection
{
    bool IsLegacyMode { get; }
    IReadOnlyList<ProfileModOptionCardViewModel> OptionCards { get; }
    IReadOnlyList<ProfileModLegacyChoiceViewModel> LegacyChoices { get; }
    ICommand ToggleOptionCommand { get; }

    /// <summary>是否存在可导航的组件分组（仅 HD2IAS 导入生成）。</summary>
    bool HasGroups { get; }

    /// <summary>当前是否显示一级组件列表（有分组且未进入某组件）。</summary>
    bool ShowGroupList { get; }

    /// <summary>当前是否显示选项卡片（无分组、或在二级组件页）。</summary>
    bool ShowOptionList { get; }

    /// <summary>当前是否处于二级组件选项页。</summary>
    bool IsGroupPage { get; }

    IReadOnlyList<ProfileModOptionGroupViewModel> GroupCards { get; }

    /// <summary>当前页面应显示的选项卡片（一级无分组或二级为当前组件选项）。</summary>
    IReadOnlyList<ProfileModOptionCardViewModel> VisibleOptionCards { get; }

    ICommand OpenGroupCommand { get; }

    ICommand ToggleGroupCommand { get; }

    /// <summary>保存二级页改动并回到一级组件列表。</summary>
    ICommand ReturnCommand { get; }

    void SelectSubOption(ProfileModSubOptionChoiceViewModel? choice);
    void SelectLegacyChoice(ProfileModLegacyChoiceViewModel? choice);
    void ShowImagePreview(Avalonia.Media.Imaging.Bitmap? image, string? title);
}
