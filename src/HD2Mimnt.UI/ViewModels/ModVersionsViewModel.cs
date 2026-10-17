using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Services;

namespace HD2Mimnt.UI.ViewModels;

public partial class ModVersionsViewModel(ModService service, IConfirmationService confirmation, Func<Task> refresh) : ObservableObject
{
    private ModItemViewModel? _target;
    private CancellationTokenSource? _operation;
    public Task Completion { get; private set; } = Task.CompletedTask;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _message = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersions))]
    private IReadOnlyList<LocalModVersionChoice> _versions = Array.Empty<LocalModVersionChoice>();
    [ObservableProperty] private LocalModVersionChoice? _selectedVersion;
    public bool HasVersions => Versions.Count > 0;

    public void Open(ModItemViewModel target)
    {
        if (IsBusy) return;
        _target = target;
        Versions = Array.Empty<LocalModVersionChoice>();
        SelectedVersion = null;
        Message = "";
        Start(LoadAsync);
    }

    private async Task LoadAsync(CancellationToken token)
    {
        Versions = (await service.GetLocalVersionsAsync(_target!.Id, token)).OrderByDescending(v => v.SavedAt)
            .Select(v => new LocalModVersionChoice(v)).ToArray();
        SelectedVersion = Versions.FirstOrDefault();
    }

    public Task UpdateFromArchiveAsync(string path) => Start(async token =>
    {
        var result = await Task.Run(() => service.UpdateFromLocalArchiveAsync(_target!.Id, path, token), token);
        await ApplyAsync(result, token);
    });

    [RelayCommand]
    private Task SwitchAsync() => Start(async token =>
    {
        if (SelectedVersion is not { } version) return;
        if (!await confirmation.ConfirmAsync("切换本地版本", "切换将清理此模组的旧部署，保留当前版本供再次切换。配置中的启用状态和顺序保持不变；请检查模组选项并重新部署。确定切换吗？")) return;
        var result = await Task.Run(() => service.SwitchLocalVersionAsync(_target!.Id, version.Version.Id, token), token);
        await ApplyAsync(result, token);
    });

    [RelayCommand]
    private Task DeleteAsync() => Start(async token =>
    {
        if (SelectedVersion is not { } version) return;
        if (!await confirmation.ConfirmAsync("删除本地版本", "将永久删除所选历史版本的文件以释放空间，当前版本不受影响。确定删除吗？")) return;
        await Task.Run(() => service.DeleteLocalVersionAsync(_target!.Id, version.Version.Id, token), token);
        await LoadAsync(token);
    });

    private async Task ApplyAsync(ModReplacementResult result, CancellationToken token)
    {
        _target!.ApplyContent(result.Mod);
        await _target.LoadIconAsync(CancellationToken.None);
        await refresh();
        await LoadAsync(CancellationToken.None);
        Message = "版本已切换，请检查模组选项并重新部署。" + result.CleanupWarning;
    }

    private Task Start(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return Completion;
        return Completion = RunAsync(action);
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy || _target == null) return;
        using var operation = new CancellationTokenSource();
        _operation = operation;
        IsBusy = true;
        Message = "";
        try { await action(operation.Token); }
        catch (OperationCanceledException) { Message = "操作已取消。"; }
        catch (Exception ex) { Message = ex.Message; }
        finally { IsBusy = false; _operation = null; }
    }

    [RelayCommand] public void Cancel() => _operation?.Cancel();
    public async Task StopAsync() { Cancel(); await Completion; }
}

public sealed record LocalModVersionChoice(LocalModVersion Version)
{
    public string Label => $"{Version.Content.Version} · {Version.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · {Version.Content.Size / 1048576d:0.##} MB · {Version.Id.ToString("N")[..6]}";
}
