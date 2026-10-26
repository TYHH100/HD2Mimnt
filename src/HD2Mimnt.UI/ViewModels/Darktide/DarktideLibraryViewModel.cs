using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Application.Services.Darktide;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels.Darktide;

public partial class DarktideLibraryViewModel : DarktidePageViewModelBase
{
    private readonly DarktideManagerService _manager;
    private readonly IDarktideDialogService _dialogs;
    private readonly TaskService _tasks;
    private DarktideModSeparator? _editingSeparator;
    private int _pendingSeparatorOrder;

    [ObservableProperty] private ObservableCollection<DarktideModItemViewModel> _mods = new();
    [ObservableProperty] private ObservableCollection<object> _rows = new();
    [ObservableProperty] private bool _registerAfterImport = true;
    [ObservableProperty] private bool _loaderReady;
    [ObservableProperty] private bool _hasGame;
    [ObservableProperty] private string _gamePathText = "";
    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private string _warningText = "";
    [ObservableProperty] private bool _hasWarning;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _isSeparatorDialogOpen;
    [ObservableProperty] private bool _isSeparatorNameEditable = true;
    [ObservableProperty] private bool _useDefaultSeparatorColor = true;
    [ObservableProperty] private string _separatorTitle = "";
    [ObservableProperty] private string _separatorName = "";
    [ObservableProperty] private string _separatorError = "";
    [ObservableProperty] private Color _separatorColor = Colors.Gray;

    public DarktideLibraryViewModel(DarktideManagerService manager, IDarktideDialogService dialogs,
        TaskService tasks, NotificationService notifications) : base(notifications)
    {
        _manager = manager;
        _dialogs = dialogs;
        _tasks = tasks;
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
        => await RunAsync(LoadAsync, failTitleKey: "Darktide.Library.Title");

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var scan = await RunOffUiThreadAsync(token => _manager.ScanAsync(token), cancellationToken);
        Mods = new ObservableCollection<DarktideModItemViewModel>(scan.Mods.Select(mod => new DarktideModItemViewModel(mod)));
        HasGame = scan.Game.RootPath != null && scan.Game.Status != DarktideGameStatus.InvalidPath;
        LoaderReady = scan.Game.Status == DarktideGameStatus.LoaderInstalled;
        GamePathText = scan.Game.RootPath ?? UiLocalization.Current["Darktide.Game.NotSet"];
        var installed = scan.Mods.Count(mod => mod.Installed);
        var registered = scan.Mods.Count(mod => mod.Registered);
        SummaryText = UiLocalization.Current.Format("Darktide.Library.Summary", Mods.Count, installed, registered);
        WarningText = scan.Game.Notes.Count > 0 ? string.Join(" ", scan.Game.Notes)
            : scan.Game.Status == DarktideGameStatus.LoaderInstalled ? ""
            : UiLocalization.Current["Darktide.Library.NoGame"];
        HasWarning = WarningText.Length > 0;
        var separators = await RunOffUiThreadAsync(token => _manager.GetModSeparatorsAsync(token), cancellationToken);
        Rows = new ObservableCollection<object>(ComposeRows(Mods, separators));
        // 空库时仍要显示已有的空分组，否则建出来的组会“消失”且无法删除。
        IsEmpty = Rows.Count == 0;
    }

    /// <summary>
    /// 把分组按位点插进稳定的模组序列。禁用模组保持原位，所以位点只随增删变化；
    /// 折叠只隐藏成员，成员数仍然计入边界。
    /// </summary>
    private static List<object> ComposeRows(IReadOnlyList<DarktideModItemViewModel> mods,
        IReadOnlyList<DarktideModSeparator> separators)
    {
        var count = mods.Count;
        var ordered = separators.OrderBy(item => item.Order)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var groups = new List<(DarktideSeparatorRowViewModel Row, int Start, int End)>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var model = ordered[index];
            var start = Math.Clamp(model.Order, 0, count);
            var next = index + 1 < ordered.Count ? Math.Clamp(ordered[index + 1].Order, start, count) : count;
            // MemberCount 为空表示延伸到下一个分隔符；显式边界也不得越过下一个分隔符。
            var end = model.MemberCount is { } members ? Math.Clamp(start + members, start, next) : next;
            groups.Add((new DarktideSeparatorRowViewModel(model)
            {
                TintBrush = SolidBrush(model.Color),
                StartOrder = start,
                EndOrder = end
            }, start, end));
        }

        var owner = new DarktideSeparatorRowViewModel?[count];
        foreach (var (row, start, end) in groups)
            for (var index = start; index < end; index++) owner[index] = row;
        foreach (var (row, start, end) in groups)
        {
            row.MemberCount = owner.Count(item => item == row);
            var tint = GroupTint(row.Model.Color);
            var members = new List<DarktideModItemViewModel>();
            for (var index = start; index < end; index++)
                if (owner[index] == row) members.Add(mods[index]);
            for (var position = 0; position < members.Count; position++)
            {
                members[position].IsInsideGroup = true;
                members[position].GroupTintBrush = tint;
                members[position].IsSlabStart = position == 0;
                members[position].IsSlabEnd = position == members.Count - 1;
            }
        }

        var rows = new List<object>();
        var emitted = new HashSet<DarktideSeparatorRowViewModel>();
        for (var index = 0; index <= count; index++)
        {
            foreach (var (row, start, _) in groups)
                if (start == index && emitted.Add(row)) rows.Add(row);
            if (index == count) break;
            if (owner[index] is { IsCollapsed: true }) continue;
            rows.Add(mods[index]);
        }
        return rows;
    }

    private static IBrush? SolidBrush(string color) => Color.TryParse(color, out var value)
        ? new ImmutableSolidColorBrush(value) : null;

    private static IBrush? GroupTint(string color) => Color.TryParse(color, out var value)
        // 不可变画刷没有线程亲和限制，列表可能在后台上下文构建。
        ? new ImmutableSolidColorBrush(Color.FromArgb(38, value.R, value.G, value.B))
        : null;

    private List<string> SequenceNames() => Mods.Select(item => item.InternalName).ToList();

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (!LoaderReady)
        {
            Notifications.ShowWarning(UiLocalization.Current["Darktide.Library.NoGame"], UiLocalization.Current["Darktide.Title"]);
            return;
        }
        await RunAsync(ImportCoreAsync, failTitleKey: "Darktide.Library.Title");
    }

    private async Task ImportCoreAsync(CancellationToken cancellationToken)
    {
        var archive = await _dialogs.PickArchiveAsync();
        if (archive == null) return;
        var inspection = await Task.Run(() => _manager.InspectArchive(archive), cancellationToken);
        if (inspection.Error != null) throw new InvalidDataException(inspection.Error);
        if (inspection.Kind == DarktideArchivePackageKind.CustomInstallRequired)
        {
            Notifications.ShowWarning(UiLocalization.Current.Format("Darktide.Library.CustomInstallRequired",
                    inspection.ExtraFiles.Count, string.Join(", ", inspection.ExtraFiles.Take(4))),
                UiLocalization.Current["Darktide.Library.Title"]);
            return;
        }
        var selected = await _dialogs.PickCandidatesAsync(inspection.Candidates);
        if (selected == null || selected.Count == 0) return;
        if (selected.Any(candidate =>
                candidate.InternalName.Equals(DarktidePaths.DmfFolderName, StringComparison.OrdinalIgnoreCase)))
        {
            Notifications.ShowWarning(UiLocalization.Current["Darktide.Library.DmfUseLoader"],
                UiLocalization.Current["Darktide.Library.Title"]);
            return;
        }

        var installedNames = selected
            .Select(candidate => candidate.InternalName)
            .Where(name => Mods.Any(mod => mod.Installed
                                           && string.Equals(mod.InternalName, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var allowReplace = false;
        if (installedNames.Count > 0)
        {
            allowReplace = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Library.ReplaceTitle"],
                installedNames.Count == 1
                    ? UiLocalization.Current.Format("Darktide.Library.ReplaceMessage", installedNames[0])
                    : UiLocalization.Current.Format("Darktide.Library.ReplaceMessageMultiple",
                        string.Join(", ", installedNames)));
            if (!allowReplace) return;
        }

        HasProgress = true;
        ProgressPercent = 0;
        ProgressText = UiLocalization.Current["Darktide.Library.Importing"];
        IReadOnlyList<DarktideInstallResult> results;
        try
        {
            results = await RunImportTaskAsync(archive, selected, allowReplace, cancellationToken);
        }
        finally
        {
            HasProgress = false;
            ProgressText = "";
        }

        await LoadAsync(cancellationToken);
        if (results.Count == 1)
        {
            var result = results[0];
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Library.ImportCompletedDetail",
                result.InternalName, result.Registered ? UiLocalization.Current["Darktide.Library.Enabled"]
                    : UiLocalization.Current["Darktide.Library.Disabled"]), UiLocalization.Current["Darktide.Library.Title"]);
        }
        else
        {
            Notifications.ShowSuccess(
                UiLocalization.Current.Format("Darktide.Library.BundleCompleted", results.Count),
                UiLocalization.Current["Darktide.Library.Title"]);
        }
        foreach (var warning in results.SelectMany(result => result.Warnings))
            Notifications.ShowWarning(warning, UiLocalization.Current["Darktide.Library.Title"]);
    }

    private async Task<IReadOnlyList<DarktideInstallResult>> RunImportTaskAsync(string archive,
        IReadOnlyList<DarktideArchiveCandidate> selected, bool allowReplace, CancellationToken cancellationToken)
    {
        var names = string.Join(", ", selected.Select(candidate => candidate.InternalName));
        var title = selected.Count == 1 ? $"导入暗潮模组 {names}" : $"导入暗潮模组组合（{selected.Count} 个）";
        IReadOnlyList<DarktideInstallResult> results = Array.Empty<DarktideInstallResult>();
        await _tasks.RunTaskAsync(title, "DarktideImport", async (taskProgress, token) =>
        {
            taskProgress.Report(new TaskProgress
            {
                Progress = 0, Stage = "Import", CurrentItem = names,
                Message = UiLocalization.Current["Darktide.Library.Importing"]
            });
            var progress = CreateUiProgress(value =>
            {
                ProgressPercent = value;
                ProgressText = $"{UiLocalization.Current["Darktide.Library.Importing"]} {value:0}%";
            });
            results = selected.Count == 1
                ? new[]
                {
                    await _manager.ImportAsync(archive, selected[0], allowReplace, RegisterAfterImport,
                        progress, token)
                }
                : await _manager.ImportBundleAsync(archive, selected, allowReplace, progress, token);
            taskProgress.Report(new TaskProgress
            {
                Progress = 100, Stage = "Completed", CurrentItem = names,
                Message = UiLocalization.Current["Darktide.Library.ImportCompleted"]
            });
        }, cancellationToken);
        return results;
    }

    [RelayCommand]
    private async Task ToggleAsync(DarktideModItemViewModel? item)
    {
        if (item == null) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.SetEnabledAsync(item.InternalName, !item.Registered, token),
                cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.Library.Title");
    }

    [RelayCommand]
    private async Task InstallAsync(DarktideModItemViewModel? item)
    {
        if (item == null || !item.InLibrary) return;
        if (item.Installed)
        {
            var replace = await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Library.ReplaceTitle"],
                UiLocalization.Current.Format("Darktide.Library.ReplaceMessage", item.InternalName));
            if (!replace) return;
        }
        await RunAsync(async cancellationToken =>
        {
            HasProgress = true;
            ProgressPercent = 0;
            ProgressText = UiLocalization.Current["Darktide.Library.Installing"];
            try
            {
                await _tasks.RunTaskAsync($"安装暗潮模组 {item.InternalName}", "DarktideInstall",
                    async (taskProgress, token) =>
                    {
                        taskProgress.Report(new TaskProgress { Stage = "Install", CurrentItem = item.InternalName });
                        var progress = CreateUiProgress(value => ProgressPercent = value);
                        await _manager.ReinstallFromLibraryAsync(item.InternalName, RegisterAfterImport, progress, token);
                        taskProgress.Report(new TaskProgress { Progress = 100, Stage = "Completed" });
                    }, cancellationToken);
            }
            finally { HasProgress = false; }
            await LoadAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Library.InstallCompleted", item.InternalName),
                UiLocalization.Current["Darktide.Library.Title"]);
        }, failTitleKey: "Darktide.Library.Title");
    }

    [RelayCommand]
    private async Task UninstallAsync(DarktideModItemViewModel? item)
    {
        if (item == null || !item.Installed) return;
        var message = UiLocalization.Current.Format("Darktide.Library.UninstallMessage", item.InternalName,
            item.Model.InstalledPath ?? "");
        if (!await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Library.UninstallTitle"], message)) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.UninstallAsync(item.InternalName, removeRegistration: true,
                deleteLibraryArchive: false, token), cancellationToken);
            await LoadAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Library.UninstallCompleted", item.InternalName),
                UiLocalization.Current["Darktide.Library.Title"]);
        }, failTitleKey: "Darktide.Library.Title");
    }

    [RelayCommand]
    private async Task RemoveLibraryAsync(DarktideModItemViewModel? item)
    {
        if (item == null || !item.InLibrary) return;
        var message = UiLocalization.Current.Format("Darktide.Library.RemoveLibraryMessage", item.InternalName);
        if (!await _dialogs.ConfirmAsync(UiLocalization.Current["Darktide.Library.RemoveLibraryTitle"], message)) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.RemoveFromLibraryAsync(item.InternalName, deleteArchive: true,
                token), cancellationToken);
            await LoadAsync(cancellationToken);
            Notifications.ShowSuccess(UiLocalization.Current.Format("Darktide.Library.RemoveLibraryCompleted", item.InternalName),
                UiLocalization.Current["Darktide.Library.Title"]);
        }, failTitleKey: "Darktide.Library.Title");
    }

    [RelayCommand]
    private void OpenFolder(DarktideModItemViewModel? item)
    {
        if (item == null) return;
        if (item.Installed && item.Model.InstalledPath != null) _dialogs.OpenFolder(item.Model.InstalledPath);
        else if (item.Model.ArchivePath != null) _dialogs.OpenFile(item.Model.ArchivePath);
    }

    // ── 分组：仅组织库列表显示，不写 mod_load_order.txt ──

    [RelayCommand]
    private void AddGroup() => OpenSeparatorDialog(null, Mods.Count);

    [RelayCommand]
    private void AddGroupAbove(DarktideSeparatorRowViewModel row) => OpenSeparatorDialog(null, row.StartOrder);

    [RelayCommand]
    private void AddGroupBelow(DarktideSeparatorRowViewModel row) => OpenSeparatorDialog(null, row.EndOrder);

    [RelayCommand]
    private void EditGroup(DarktideSeparatorRowViewModel row) => OpenSeparatorDialog(row.Model, row.StartOrder);

    private void OpenSeparatorDialog(DarktideModSeparator? existing, int order)
    {
        _editingSeparator = existing;
        _pendingSeparatorOrder = order;
        SeparatorTitle = UiLocalization.Current[existing == null ? "UI.AddGroupSeparator" : "UI.Rename"];
        SeparatorName = existing?.Name ?? "";
        IsSeparatorNameEditable = true;
        SeparatorError = "";
        UseDefaultSeparatorColor = true;
        if (existing != null && Color.TryParse(existing.Color, out var saved))
        {
            UseDefaultSeparatorColor = false;
            SeparatorColor = saved;
        }
        IsSeparatorDialogOpen = true;
    }

    [RelayCommand]
    private void CancelSeparatorDialog()
    {
        _editingSeparator = null;
        IsSeparatorDialogOpen = false;
    }

    /// <summary>Escape 与取消按钮共用同一出口，关闭后不能残留待提交的编辑目标。</summary>
    public void CloseOverlays()
    {
        if (IsSeparatorDialogOpen) CancelSeparatorDialog();
    }

    [RelayCommand]
    private async Task ConfirmSeparatorAsync()
    {
        if (!IsSeparatorDialogOpen) return;
        var color = UseDefaultSeparatorColor ? "" : $"#{SeparatorColor.R:X2}{SeparatorColor.G:X2}{SeparatorColor.B:X2}";
        try
        {
            if (_editingSeparator is { } existing)
                await _manager.UpdateModSeparatorAsync(existing.Id, SeparatorName, color);
            else
                await _manager.AddModSeparatorAsync(SeparatorName, _pendingSeparatorOrder, SequenceNames(), color);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 名称与颜色在本地校验失败时保留对话框，让用户改完再提交。
            SeparatorError = ex.Message;
            return;
        }
        _editingSeparator = null;
        IsSeparatorDialogOpen = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ToggleGroupCollapseAsync(DarktideSeparatorRowViewModel row)
    {
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.SetModSeparatorCollapsedAsync(row.Id, !row.IsCollapsed,
                SequenceNames(), token), cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.Library.Title");
    }

    [RelayCommand]
    private async Task RemoveGroupAsync(DarktideSeparatorRowViewModel row)
    {
        var message = UiLocalization.Current.Format("Darktide.Library.RemoveGroupMessage", row.Title, row.MemberCount);
        if (!await _dialogs.ConfirmAsync(UiLocalization.Current["Common.Delete"], message)) return;
        await RunAsync(async cancellationToken =>
        {
            await RunOffUiThreadAsync(token => _manager.RemoveModSeparatorAsync(row.Id, SequenceNames(), token),
                cancellationToken);
            await LoadAsync(cancellationToken);
        }, failTitleKey: "Darktide.Library.Title");
    }
}
