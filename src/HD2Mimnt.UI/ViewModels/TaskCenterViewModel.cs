using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.UI.Localization;
using HD2Mimnt.UI.Services;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace HD2Mimnt.UI.ViewModels;

public partial class TaskCenterViewModel : ObservableObject
{
    private readonly TaskService _taskService;
    private readonly IConfirmationService _confirmationService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly Dictionary<Guid, ModTask> _taskIndex = new();
    private CancellationTokenSource? _autoRefreshCts;
    [ObservableProperty] private ObservableCollection<ModTask> _tasks = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasFinishedTasks;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public TaskCenterViewModel(TaskService taskService, IConfirmationService confirmationService)
    {
        _taskService = taskService;
        _confirmationService = confirmationService;
    }

    public void StartAutoRefresh(TimeSpan? interval = null)
    {
        if (_autoRefreshCts != null) return;

        _autoRefreshCts = new CancellationTokenSource();
        _ = RefreshLoopAsync(interval ?? TimeSpan.FromSeconds(1), _autoRefreshCts.Token);
    }

    public void StopAutoRefresh()
    {
        _autoRefreshCts?.Cancel();
        _autoRefreshCts?.Dispose();
        _autoRefreshCts = null;
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        await _loadGate.WaitAsync();
        await ApplyOnUiThreadAsync(() =>
        {
            IsLoading = true;
            HasError = false;
            ErrorMessage = string.Empty;
        });
        try
        {
            var tasks = await _taskService.GetHistoryAsync(100);
            var running = await _taskService.GetRunningTasksAsync();
            var snapshot = running.Concat(tasks).GroupBy(t => t.Id).Select(g => g.First()).ToList();
            var statusMessage = $"运行中 {running.Count()} 个，历史 {tasks.Count()} 个";
            await ApplyOnUiThreadAsync(() =>
            {
                MergeTasks(snapshot);
                StatusMessage = statusMessage;
            });
        }
        catch (Exception ex)
        {
            await ApplyOnUiThreadAsync(() =>
            {
                HasError = true;
                ErrorMessage = $"加载任务失败: {ex.Message}";
                StatusMessage = "任务中心加载失败";
            });
        }
        finally
        {
            await ApplyOnUiThreadAsync(() =>
            {
                IsEmpty = !HasError && Tasks.Count == 0;
                HasFinishedTasks = !HasError && Tasks.Any(TaskService.IsDeletable);
                IsLoading = false;
            });
            _loadGate.Release();
        }
    }

    private static Task ApplyOnUiThreadAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess() || global::Avalonia.Application.Current is null)
        {
            action();
            return Task.CompletedTask;
        }

        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    /// <summary>
    /// 按 Id 合并快照：同一任务沿用原有列表项实例，只更新变化的属性。
    /// 每秒整体重建集合会让列表项容器反复销毁重建，进度条随之从 0 重新动画，看起来一直在跳。
    /// </summary>
    private void MergeTasks(IReadOnlyList<ModTask> snapshot)
    {
        var seen = new HashSet<Guid>(snapshot.Count);
        var ordered = new List<ModTask>(snapshot.Count);
        foreach (var incoming in snapshot)
        {
            if (!seen.Add(incoming.Id)) continue;
            if (!_taskIndex.TryGetValue(incoming.Id, out var existing))
            {
                _taskIndex[incoming.Id] = incoming;
                ordered.Add(incoming);
                continue;
            }

            CopyInto(existing, incoming);
            ordered.Add(existing);
        }

        for (var index = Tasks.Count - 1; index >= 0; index--)
        {
            if (seen.Contains(Tasks[index].Id)) continue;
            _taskIndex.Remove(Tasks[index].Id);
            Tasks.RemoveAt(index);
        }

        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            if (index < Tasks.Count && ReferenceEquals(Tasks[index], item)) continue;
            var current = IndexOf(item);
            if (current < 0) Tasks.Insert(index, item);
            else Tasks.Move(current, index);
        }
    }

    private int IndexOf(ModTask item)
    {
        for (var index = 0; index < Tasks.Count; index++)
            if (ReferenceEquals(Tasks[index], item)) return index;
        return -1;
    }

    private static void CopyInto(ModTask target, ModTask source)
    {
        target.Name = source.Name;
        target.Type = source.Type;
        target.Status = source.Status;
        target.Progress = source.Progress;
        target.Stage = source.Stage;
        target.CurrentItem = source.CurrentItem;
        target.Message = source.Message;
        target.StartTime = source.StartTime;
        target.EndTime = source.EndTime;
        target.Result = source.Result;
    }

    private async Task RefreshLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await LoadAsync();
                await Task.Delay(interval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [RelayCommand]
    public async Task ValidateLibraryAsync()
    {
        try
        {
            var id = await _taskService.CreateTaskAsync(
                UiLocalization.Current["Task.ValidateLibrary"], "Validation");
            await _taskService.StartTaskAsync(id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ApplyOnUiThreadAsync(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
            });
        }
    }

    [RelayCommand]
    private async Task CancelAsync(ModTask? task)
    {
        if (task == null || task.Status != Core.Entities.TaskStatus.Running) return;
        try
        {
            await _taskService.CancelTaskAsync(task.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ApplyOnUiThreadAsync(() =>
            {
                HasError = true;
                ErrorMessage = $"取消任务失败: {ex.Message}";
                StatusMessage = "取消任务失败";
            });
        }
    }

    [RelayCommand]
    private async Task ForceTerminateAsync(ModTask? task)
    {
        if (task == null || task.Status != Core.Entities.TaskStatus.Running) return;
        var localizer = UiLocalization.Current;
        if (!await _confirmationService.ConfirmAsync(localizer["Task.ForceTerminate"],
                localizer["Task.ForceTerminateConfirm"])) return;
        try
        {
            await _taskService.ForceTerminateTaskAsync(task.Id);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ApplyOnUiThreadAsync(() =>
            {
                HasError = true;
                ErrorMessage = ex.Message;
            });
        }
    }

    [RelayCommand]
    private async Task ClearFinishedAsync()
    {
        if (!HasFinishedTasks) return;
        var localizer = UiLocalization.Current;
        if (!await _confirmationService.ConfirmAsync(localizer["UI.ClearCompletedTasks"],
                localizer["UI.ConfirmClearCompletedTasks"])) return;

        await RemoveTasksAsync(async () => { await _taskService.DeleteFinishedTasksAsync(); });
    }

    [RelayCommand]
    private async Task DeleteAsync(ModTask? task)
    {
        if (task == null || !TaskService.IsDeletable(task)) return;
        await RemoveTasksAsync(async () => { await _taskService.DeleteTaskAsync(task.Id); });
    }

    private async Task RemoveTasksAsync(Func<Task> remove)
    {
        try
        {
            await remove();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            await ApplyOnUiThreadAsync(() =>
            {
                HasError = true;
                ErrorMessage = UiLocalization.Current.Format("UI.FailedToDeleteTask", ex.Message);
            });
        }
    }
}
