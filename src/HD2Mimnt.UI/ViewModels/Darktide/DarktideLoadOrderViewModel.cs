using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public partial class DarktideLoadOrderViewModel : DarktidePageViewModelBase
{
    private readonly DarktideManagerService _manager;
    private readonly IDarktideDialogService _dialogs;

    [ObservableProperty] private ObservableCollection<DarktideLoadOrderItemViewModel> _activeItems = new();
    [ObservableProperty] private ObservableCollection<DarktideLoadOrderItemViewModel> _disabledItems = new();
    [ObservableProperty] private ObservableCollection<DarktideDiagnosticItemViewModel> _diagnostics = new();
    [ObservableProperty] private DarktideLoadOrderItemViewModel? _selectedItem;
    [ObservableProperty] private bool _loaderReady;
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _hasDiagnostics;
    [ObservableProperty] private bool _isActiveEmpty;
    [ObservableProperty] private bool _hasDisabled;

    public DarktideLoadOrderViewModel(DarktideManagerService manager, IDarktideDialogService dialogs,
        NotificationService notifications) : base(notifications)
    {
        _manager = manager;
        _dialogs = dialogs;
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
        => await RunAsync(LoadAsync, failTitleKey: "Darktide.LoadOrder.Title");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var scan = await RunOffUiThreadAsync(token => _manager.ScanAsync(token), cancellationToken);
        LoaderReady = scan.Game.Status == DarktideGameStatus.LoaderInstalled;
        var byName = scan.Mods.ToDictionary(mod => mod.InternalName, StringComparer.OrdinalIgnoreCase);
        var active = new List<DarktideLoadOrderItemViewModel>();
        for (var index = 0; index < scan.ActiveOrder.Count; index++)
        {
            var name = scan.ActiveOrder[index];
            byName.TryGetValue(name, out var mod);
            active.Add(new DarktideLoadOrderItemViewModel(name, mod, index, registered: true));
        }
        var disabled = scan.DisabledMods.Select((name, index) =>
        {
            byName.TryGetValue(name, out var mod);
            return new DarktideLoadOrderItemViewModel(name, mod, index, registered: false);
        }).ToList();
        ActiveItems = new ObservableCollection<DarktideLoadOrderItemViewModel>(active);
        DisabledItems = new ObservableCollection<DarktideLoadOrderItemViewModel>(disabled);
        IsActiveEmpty = active.Count == 0;
        HasDisabled = disabled.Count > 0;
        Diagnostics = new ObservableCollection<DarktideDiagnosticItemViewModel>(
            scan.Diagnostics.Select(item => new DarktideDiagnosticItemViewModel(item)));
        HasDiagnostics = Diagnostics.Count > 0;
        SummaryText = UiLocalization.Current.Format("Darktide.LoadOrder.Summary", active.Count, disabled.Count,
            Diagnostics.Count(item => item.IsError));
        var warnings = new List<string>();
        if (scan.Game.RootPath == null || scan.Game.Status == DarktideGameStatus.InvalidPath)
            warnings.Add(UiLocalization.Current["Darktide.LoadOrder.NoGame"]);
        if (scan.Game.AlternativeLoader is { } alternative)
            warnings.Add(UiLocalization.Current.Format("Darktide.LoadOrder.AlternativeLoader", alternative));
        WarningText = string.Join(" ", warnings);
        HasWarning = WarningText.Length > 0;
    }

    [RelayCommand]
    private async Task MoveUpAsync(DarktideLoadOrderItemViewModel? item) => await MoveAsync(item, -1);

    [RelayCommand]
    private async Task MoveDownAsync(DarktideLoadOrderItemViewModel? item) => await MoveAsync(item, 1);

    private async Task MoveAsync(DarktideLoadOrderItemViewModel? item, int offset)
    {
        if (item == null || !item.CanMove) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.MoveAsync(item.InternalName, offset, token), cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.LoadOrder.Title");
    }

    [RelayCommand]
    private async Task DisableAsync(DarktideLoadOrderItemViewModel? item)
    {
        if (item == null) return;
        if (item.IsFramework)
        {
            var confirm = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.LoadOrder.RemoveRegistrationTitle"],
                UiLocalization.Current.Format("Darktide.LoadOrder.RemoveRegistrationMessage", item.InternalName));
            if (!confirm) return;
        }
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.SetEnabledAsync(item.InternalName, false, token), cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.LoadOrder.Title");
    }

    [RelayCommand]
    private async Task EnableAsync(DarktideLoadOrderItemViewModel? item)
    {
        if (item == null) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.SetEnabledAsync(item.InternalName, true, token), cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.LoadOrder.Title");
    }

    public async Task MoveBeforeAsync(string sourceName, string? targetName)
    {
        if (string.Equals(sourceName, targetName, StringComparison.OrdinalIgnoreCase)) return;
        var names = ActiveItems.Select(item => item.InternalName).ToList();
        var sourceIndex = names.FindIndex(name => string.Equals(name, sourceName, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0) return;
        names.RemoveAt(sourceIndex);
        var targetIndex = targetName == null ? names.Count
            : names.FindIndex(name => string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0) targetIndex = names.Count;
        names.Insert(targetIndex, sourceName);
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.ReorderAsync(names, token), cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.LoadOrder.Title");
    }

    [RelayCommand]
    private void OpenLoadOrder()
    {
        var root = _manager.GetGameState().RootPath;
        if (root != null) _dialogs.OpenFile(DarktidePaths.LoadOrderFile(root));
    }

    [RelayCommand]
    private async Task ReloadAsync() => await RefreshAsync();
}
