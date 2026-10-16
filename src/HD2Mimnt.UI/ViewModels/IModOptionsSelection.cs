using System.Windows.Input;

namespace HD2Mimnt.UI.ViewModels;

/// <summary>实际管理器和创建预览共用的选项选择界面。</summary>
public interface IModOptionsSelection
{
    bool IsLegacyMode { get; }
    IReadOnlyList<ProfileModOptionCardViewModel> OptionCards { get; }
    IReadOnlyList<ProfileModLegacyChoiceViewModel> LegacyChoices { get; }
    ICommand ToggleOptionCommand { get; }
    void SelectSubOption(ProfileModSubOptionChoiceViewModel? choice);
    void SelectLegacyChoice(ProfileModLegacyChoiceViewModel? choice);
    void ShowImagePreview(Avalonia.Media.Imaging.Bitmap? image, string? title);
}
