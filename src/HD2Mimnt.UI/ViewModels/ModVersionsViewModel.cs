using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
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
        var deploymentProgress = new Progress<DeploymentProgress>(p => Message = p.Message);
        var result = await Task.Run(() => service.UpdateFromLocalArchiveAsync(
            _target!.Id, path, token, deploymentProgress: deploymentProgress), token);
        await ApplyAsync(result, token);
    });

    [RelayCommand]
    private Task SwitchAsync() => Start(async token =>
    {
        if (SelectedVersion is not { } version) return;
        if (!await confirmation.ConfirmAsync("切换本地版本", "切换后会保留当前版本供再次切换；若该模组当前已部署，将自动增量更新游戏中的文件，配置中的启用状态和顺序保持不变。请检查模组选项。确定切换吗？")) return;
        var deploymentProgress = new Progress<DeploymentProgress>(p => Message = p.Message);
        var result = await Task.Run(() => service.SwitchLocalVersionAsync(
            _target!.Id, version.Version.Id, token, deploymentProgress), token);
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
        var deploymentText = result.Deployment is { } deployment
            ? deployment.Status switch
            {
                DeploymentStatus.Completed =>
                    $"已自动增量部署：新增 {deployment.AddedFiles}、更新 {deployment.UpdatedFiles}、移动 {deployment.MovedFiles}、删除 {deployment.RemovedFiles} 个文件。",
                DeploymentStatus.Cancelled => "自动部署已取消，可稍后重试部署。",
                _ => $"自动部署未完成：{deployment.ErrorMessage ?? deployment.Message}"
            }
            : result.WasDeployed
                ? "当前生效部署或游戏目录未就绪，未自动部署。"
                : string.Empty;
        Message = "版本已切换，请检查模组选项。" + deploymentText + result.CleanupWarning;
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
