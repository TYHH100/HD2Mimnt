using CommunityToolkit.Mvvm.ComponentModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class ProfileModLegacyChoiceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class LibraryModChoiceViewModel : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _version = string.Empty;
    [ObservableProperty] private string _author = string.Empty;
}

public enum ProfileCompareStatus
{
    Common,
    OnlyLeft,
    OnlyRight,
    EnabledStateDifferent
}

public partial class ProfileCompareItemViewModel : ObservableObject
{
    [ObservableProperty] private Guid _modId;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _leftState = "-";
    [ObservableProperty] private string _rightState = "-";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private ProfileCompareStatus _status;
    [ObservableProperty] private bool _isVisible = true;
}
