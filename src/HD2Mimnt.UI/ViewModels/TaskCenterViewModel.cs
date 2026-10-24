using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using System.Collections.ObjectModel;
using Avalonia.Threading;

namespace HD2Mimnt.UI.ViewModels;

public partial class TaskCenterViewModel : ObservableObject
{
    private readonly TaskService _taskService;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private CancellationTokenSource? _autoRefreshCts;
    [ObservableProperty] private ObservableCollection<ModTask> _tasks = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public TaskCenterViewModel(TaskService taskService) => _taskService = taskService;

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
                Tasks = new ObservableCollection<ModTask>(snapshot);
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
            await ApplyOnUiThreadAsync(()
                => IsEmpty = !HasError && Tasks.Count == 0);
            await ApplyOnUiThreadAsync(() => IsLoading = false);
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
}
