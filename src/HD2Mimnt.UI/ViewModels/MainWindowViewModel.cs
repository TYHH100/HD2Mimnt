using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly LibraryViewModel _libraryViewModel;
    private readonly ProfileViewModel _profileViewModel;

    [ObservableProperty]
    private string _title = "HD2Mimnt - Helldivers 2 Mod Manager";

    [ObservableProperty]
    private ObservableObject? _currentView;

    [ObservableProperty]
    private int _selectedTabIndex = 0;

    [ObservableProperty]
    private string _statusBarText = "Ready";

    public MainWindowViewModel(
        LibraryViewModel libraryViewModel,
        ProfileViewModel profileViewModel)
    {
        _libraryViewModel = libraryViewModel;
        _profileViewModel = profileViewModel;

        NavigateToLibrary();
    }

    public async Task InitializeAsync()
    {
        await _libraryViewModel.InitializeAsync();
        await _profileViewModel.LoadProfilesAsync();
        StatusBarText = "Application initialized";
    }

    [RelayCommand]
    private void NavigateToLibrary()
    {
        SelectedTabIndex = 0;
        CurrentView = _libraryViewModel;
        StatusBarText = "Library";
    }

    [RelayCommand]
    private void NavigateToProfiles()
    {
        SelectedTabIndex = 1;
        CurrentView = _profileViewModel;
        StatusBarText = "Profiles";
    }

    [RelayCommand]
    private void NavigateToTasks()
    {
        SelectedTabIndex = 2;
        // TODO: 加载 Task View
        StatusBarText = "Tasks";
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        SelectedTabIndex = 3;
        // TODO: 加载 Settings View
        StatusBarText = "Settings";
    }
}
