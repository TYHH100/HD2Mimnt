using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using System.Collections.ObjectModel;

namespace HD2Mimnt.UI.ViewModels;

public partial class TaskCenterViewModel : ObservableObject
{
    private readonly TaskService _taskService;
    [ObservableProperty] private ObservableCollection<ModTask> _tasks = new();
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private string _errorMessage = string.Empty;

    public TaskCenterViewModel(TaskService taskService) => _taskService = taskService;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        HasError = false;
        ErrorMessage = string.Empty;
        try
        {
            var tasks = await _taskService.GetHistoryAsync(100);
            var running = await _taskService.GetRunningTasksAsync();
            Tasks = new ObservableCollection<ModTask>(running.Concat(tasks).GroupBy(t => t.Id).Select(g => g.First()));
            StatusMessage = $"运行中 {running.Count()} 个，历史 {tasks.Count()} 个";
        }
        catch (Exception ex)
        {
            HasError = true;
            ErrorMessage = $"加载任务失败: {ex.Message}";
            StatusMessage = "任务中心加载失败";
        }
        finally
        {
            IsEmpty = !HasError && Tasks.Count == 0;
            IsLoading = false;
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
            HasError = true;
            ErrorMessage = $"取消任务失败: {ex.Message}";
            StatusMessage = "取消任务失败";
        }
    }
}
