using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public partial class DarktideProfilesViewModel : DarktidePageViewModelBase
{
    private readonly DarktideManagerService _manager;
    private readonly IDarktideDialogService _dialogs;
    private readonly TaskService _tasks;
    private bool _loading;

    [ObservableProperty] private ObservableCollection<DarktideProfileItemViewModel> _profiles = new();
    [ObservableProperty] private DarktideProfileItemViewModel? _selectedProfile;
    [ObservableProperty] private ObservableCollection<string> _previewLines = new();
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _loaderReady;
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _isEmpty;

    public DarktideProfilesViewModel(DarktideManagerService manager, IDarktideDialogService dialogs,
        TaskService tasks, NotificationService notifications) : base(notifications)
    {
        _manager = manager;
        _dialogs = dialogs;
        _tasks = tasks;
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
        => await RunAsync(LoadAsync, failTitleKey: "Darktide.Profiles.Title");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        _loading = true;
        try
        {
            var profiles = await RunOffUiThreadAsync(token => _manager.GetProfilesAsync(token), cancellationToken);
            var selectedName = SelectedProfile?.Name;
            Profiles = new ObservableCollection<DarktideProfileItemViewModel>(
                profiles.Select(profile => new DarktideProfileItemViewModel(profile)));
            IsEmpty = Profiles.Count == 0;
            SelectedProfile = Profiles.FirstOrDefault(profile => string.Equals(profile.Name, selectedName,
                               StringComparison.OrdinalIgnoreCase)) ?? Profiles.FirstOrDefault();
            var game = _manager.GetGameState();
            LoaderReady = game.Status == DarktideGameStatus.LoaderInstalled;
            var warnings = new List<string>();
            if (!LoaderReady) warnings.Add(UiLocalization.Current["Darktide.Profiles.NoGame"]);
            if (game.AlternativeLoader is { } alternative)
                warnings.Add(UiLocalization.Current.Format("Darktide.LoadOrder.AlternativeLoader", alternative));
            WarningText = string.Join(" ", warnings);
            HasWarning = WarningText.Length > 0;
        }
        finally { _loading = false; }
        await LoadPreviewAsync(cancellationToken);
    }

    private async Task LoadPreviewAsync(CancellationToken cancellationToken)
    {
        PreviewLines = new ObservableCollection<string>();
        HasPreview = false;
        if (SelectedProfile == null || !LoaderReady) return;
        try
        {
            var preview = await RunOffUiThreadAsync(token => _manager.PreviewApplyProfileAsync(SelectedProfile.Name, token),
                cancellationToken);
            PreviewLines = new ObservableCollection<string>(FormatPreview(preview));
            HasPreview = true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            PreviewLines.Add(ex.Message);
            HasPreview = true;
        }
    }

    partial void OnSelectedProfileChanged(DarktideProfileItemViewModel? value)
    {
        if (!_loading && !IsBusy) _ = LoadPreviewAsync(CancellationToken.None);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!LoaderReady) return;
        var name = await _dialogs.PromptAsync("Darktide.Profiles.CreateTitle", "Darktide.Profiles.NameLabel");
        if (name == null) return;
        var exists = Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            var overwrite = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Profiles.OverwriteTitle"],
                UiLocalization.Current.Format("Darktide.Profiles.OverwriteMessage", name));
            if (!overwrite) return;
        }
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.CreateOrSaveProfileAsync(name, exists, token), cancellationToken);
            await LoadAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Profiles.Saved", name),
                UiLocalization.Current["Darktide.Profiles.Title"]);
        }, failTitleKey: "Darktide.Profiles.Title");
    }

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        if (!LoaderReady) return;
        var name = await _dialogs.PromptAsync("Darktide.Profiles.SaveTitle", "Darktide.Profiles.NameLabel",
            SelectedProfile?.Name ?? "");
        if (name == null) return;
        var exists = Profiles.Any(profile => string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (exists)
        {
            var overwrite = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Profiles.OverwriteTitle"],
                UiLocalization.Current.Format("Darktide.Profiles.OverwriteMessage", name));
            if (!overwrite) return;
        }
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.CreateOrSaveProfileAsync(name, exists, token), cancellationToken);
            await LoadAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Profiles.Saved", name),
                UiLocalization.Current["Darktide.Profiles.Title"]);
        }, failTitleKey: "Darktide.Profiles.Title");
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        var profile = SelectedProfile;
        if (profile == null || !LoaderReady) return;
        DarktideProfileApplyPreview preview;
        try
        {
            preview = await RunOffUiThreadAsync(token => _manager.PreviewApplyProfileAsync(profile.Name, token),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Notifications.ShowError(ex.Message, UiLocalization.Current["Darktide.Profiles.Title"]);
            return;
        }
        var lines = FormatPreview(preview);
        if (lines.Count == 0) lines.Add(UiLocalization.Current["Darktide.Profiles.NoChanges"]);
        var confirmed = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Profiles.ApplyTitle"],
            string.Join(Environment.NewLine, lines));
        if (!confirmed) return;
        await RunAsync(async cancellationToken =>
        {
            DarktideProfileApplyResult? result = null;
            await _tasks.RunTaskAsync($"切换暗潮配置档 {profile.Name}", "DarktideProfile",
                async (progress, token) =>
                {
                    progress.Report(new TaskProgress { Stage = "Apply", CurrentItem = profile.Name });
                    result = await RunOffUiThreadAsync(t => _manager.ApplyProfileAsync(profile.Name, t), token);
                    progress.Report(new TaskProgress { Progress = 100, Stage = "Completed" });
                }, cancellationToken);
            await LoadAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Profiles.Applied", profile.Name,
                result?.Enabled.Count ?? 0), UiLocalization.Current["Darktide.Profiles.Title"]);
            foreach (var missing in result?.Missing ?? Array.Empty<string>())
                Notifications.ShowWarning(UiLocalization.Current.Format("Darktide.Profiles.MissingMod", missing),
                    UiLocalization.Current["Darktide.Profiles.Title"]);
        }, failTitleKey: "Darktide.Profiles.Title");
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var profile = SelectedProfile;
        if (profile == null) return;
        var confirmed = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Profiles.DeleteTitle"],
            UiLocalization.Current.Format("Darktide.Profiles.DeleteMessage", profile.Name));
        if (!confirmed) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.DeleteProfileAsync(profile.Name, token), cancellationToken);
            SelectedProfile = null;
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.Profiles.Title");
    }

    private static List<string> FormatPreview(DarktideProfileApplyPreview preview)
    {
        var lines = new List<string>();
        if (preview.ToEnable.Count > 0)
            lines.Add(UiLocalization.Current.Format("Darktide.Profiles.PreviewEnable", string.Join(", ", preview.ToEnable)));
        if (preview.ToDisable.Count > 0)
            lines.Add(UiLocalization.Current.Format("Darktide.Profiles.PreviewDisable", string.Join(", ", preview.ToDisable)));
        if (preview.OrderChanged)
            lines.Add(UiLocalization.Current["Darktide.Profiles.PreviewOrder"]);
        if (preview.Missing.Count > 0)
            lines.Add(UiLocalization.Current.Format("Darktide.Profiles.PreviewMissing", string.Join(", ", preview.Missing)));
        if (preview.UnknownEntries.Count > 0)
            lines.Add(UiLocalization.Current.Format("Darktide.Profiles.PreviewUnknown", string.Join(", ", preview.UnknownEntries)));
        return lines;
    }
}
