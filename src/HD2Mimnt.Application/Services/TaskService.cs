using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Collections.Concurrent;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Task Center - 统一的任务管理中心
/// </summary>
public class TaskService
{
    private readonly ITaskRepository _taskRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningTasks;
    private readonly ConcurrentDictionary<string, IWorker> _workers;

    public TaskService(
        ITaskRepository taskRepository,
        IServiceProvider serviceProvider)
    {
        _taskRepository = taskRepository;
        _serviceProvider = serviceProvider;
        _runningTasks = new ConcurrentDictionary<Guid, CancellationTokenSource>();
        _workers = new ConcurrentDictionary<string, IWorker>();
    }

    /// <summary>
    /// 注册 Worker
    /// </summary>
    public void RegisterWorker(IWorker worker)
    {
        _workers[worker.Type] = worker;
    }

    /// <summary>
    /// 创建新任务
    /// </summary>
    public async Task<Guid> CreateTaskAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default)
    {
        var task = new ModTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            Type = type,
            Status = Core.Entities.TaskStatus.Waiting,
            Progress = 0,
            StartTime = DateTime.UtcNow,
            Errors = new List<string>(),
            Stage = "Waiting",
            Message = "Task created"
        };

        await _taskRepository.AddAsync(task, cancellationToken);
        return task.Id;
    }

    /// <summary>
    /// 开始执行任务
    /// </summary>
    public async Task StartTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null)
        {
            throw new InvalidOperationException($"Task {taskId} not found");
        }

        if (!_workers.TryGetValue(task.Type, out var worker))
        {
            throw new InvalidOperationException($"No worker registered for type {task.Type}");
        }

        // 创建任务专用的 CancellationTokenSource
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runningTasks[taskId] = cts;

        // 在后台执行任务
        _ = Task.Run(async () =>
        {
            try
            {
                // 更新为运行状态
                task.Status = Core.Entities.TaskStatus.Running;
                task.Progress = 0;
                task.Stage = "Starting";
                task.Message = "Task started";
                await _taskRepository.UpdateAsync(task, CancellationToken.None);

                // 创建进度报告器
                var progress = new Progress<TaskProgress>(async p =>
                {
                    try
                    {
                        task.Progress = p.Progress;
                        task.Stage = p.Stage;
                        task.CurrentItem = p.CurrentItem;
                        task.Message = p.Message;
                        await _taskRepository.UpdateAsync(task, CancellationToken.None);
                    }
                    catch
                    {
                        // 忽略进度更新错误
                    }
                });

                // 执行任务
                await worker.ExecuteAsync(task, progress, cts.Token);

                // 任务完成 - 但如果 Worker 已经设置了状态（如 PartiallyFailed），则保留
                if (task.Status == Core.Entities.TaskStatus.Running)
                {
                    task.Status = Core.Entities.TaskStatus.Completed;
                }
                task.Progress = 100;
                if (string.IsNullOrEmpty(task.Stage) || task.Stage == "Running")
                {
                    task.Stage = "Completed";
                }
                if (string.IsNullOrEmpty(task.Message))
                {
                    task.Message = "Task completed successfully";
                }
                task.EndTime = DateTime.UtcNow;
            }
            catch (OperationCanceledException)
            {
                // 任务被取消
                task.Status = Core.Entities.TaskStatus.Cancelled;
                task.Stage = "Cancelled";
                task.Message = "Task was cancelled";
                task.EndTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                // 任务失败
                task.Status = Core.Entities.TaskStatus.Failed;
                task.Stage = "Failed";
                task.Message = ex.Message;
                task.Errors.Add(ex.ToString());
                task.EndTime = DateTime.UtcNow;
            }
            finally
            {
                await _taskRepository.UpdateAsync(task, CancellationToken.None);
                _runningTasks.TryRemove(taskId, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);

        // 等待任务状态更新为 Running
        await Task.Delay(100, cancellationToken);
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    public async Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_runningTasks.TryGetValue(taskId, out var cts))
        {
            cts.Cancel();

            // 等待任务完成取消
            await Task.Delay(1000, cancellationToken);
        }
    }

    /// <summary>
    /// 强制终止任务
    /// </summary>
    public async Task ForceTerminateTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_runningTasks.TryRemove(taskId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();

            var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
            if (task != null)
            {
                task.Status = Core.Entities.TaskStatus.ForceTerminated;
                task.Stage = "Terminated";
                task.Message = "Task was force terminated";
                task.EndTime = DateTime.UtcNow;
                await _taskRepository.UpdateAsync(task, cancellationToken);
            }
        }
    }

    /// <summary>
    /// 获取任务状态
    /// </summary>
    public async Task<ModTask?> GetTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        return await _taskRepository.GetByIdAsync(taskId, cancellationToken);
    }

    /// <summary>
    /// 获取所有运行中的任务
    /// </summary>
    public async Task<IEnumerable<ModTask>> GetRunningTasksAsync(CancellationToken cancellationToken = default)
    {
        return await _taskRepository.GetRunningTasksAsync(cancellationToken);
    }

    /// <summary>
    /// 获取任务历史
    /// </summary>
    public async Task<IEnumerable<ModTask>> GetHistoryAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        return await _taskRepository.GetHistoryAsync(limit, cancellationToken);
    }

    /// <summary>
    /// 清理旧任务
    /// </summary>
    public async Task CleanupOldTasksAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var allTasks = await _taskRepository.GetAllAsync(cancellationToken);
        var cutoffTime = DateTime.UtcNow - olderThan;

        foreach (var task in allTasks)
        {
            if ((task.EndTime ?? task.StartTime) < cutoffTime &&
                task.Status != Core.Entities.TaskStatus.Running &&
                task.Status != Core.Entities.TaskStatus.Waiting)
            {
                await _taskRepository.DeleteAsync(task.Id, cancellationToken);
            }
        }
    }
}
