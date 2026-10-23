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
    private readonly ConcurrentDictionary<Guid, Task> _executions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningTasks;
    private readonly ConcurrentDictionary<string, IWorker> _workers;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _taskStateGates;
    private readonly ConcurrentDictionary<Guid, byte> _forceTerminatedTasks;

    public event EventHandler<TaskCompletedEventArgs>? TaskCompleted;
    public event EventHandler? TaskStarted;

    public TaskService(
        ITaskRepository taskRepository,
        IServiceProvider serviceProvider)
    {
        _taskRepository = taskRepository;
        _runningTasks = new ConcurrentDictionary<Guid, CancellationTokenSource>();
        _workers = new ConcurrentDictionary<string, IWorker>();
        _taskStateGates = new ConcurrentDictionary<Guid, SemaphoreSlim>();
        _forceTerminatedTasks = new ConcurrentDictionary<Guid, byte>();
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

        await StartTaskAsync(task, worker, cancellationToken);
    }

    /// <summary>执行仅在本次运行有效的操作；委托和临时授权不进入任务记录。</summary>
    public async Task RunTaskAsync(string name, string type,
        Func<IProgress<TaskProgress>, CancellationToken, Task> execute,
        CancellationToken cancellationToken = default)
    {
        var id = await CreateTaskAsync(name, type, cancellationToken);
        var task = (await GetTaskAsync(id, CancellationToken.None))!;
        var worker = new OperationWorker(type, execute);
        try { await StartTaskAsync(task, worker, cancellationToken); }
        finally { await WaitForTaskAsync(id, CancellationToken.None); }
        worker.Failure?.Throw();
        var result = await GetTaskAsync(id, CancellationToken.None);
        if (result?.Status is Core.Entities.TaskStatus.Cancelled or Core.Entities.TaskStatus.ForceTerminated)
            throw new OperationCanceledException();
        if (result?.Status != Core.Entities.TaskStatus.Completed)
            throw new InvalidOperationException(result?.Message ?? "任务执行失败。");
    }

    private async Task StartTaskAsync(ModTask task, IWorker worker, CancellationToken cancellationToken)
    {
        var taskId = task.Id;

        // 创建任务专用的 CancellationTokenSource
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_runningTasks.TryAdd(taskId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException("任务已在运行");
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executions[taskId] = completion.Task;
        var stateGate = _taskStateGates.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));

        // 在后台执行任务
        _ = Task.Run(async () =>
        {
            try
            {
                await stateGate.WaitAsync();
                try
                {
                    if (_forceTerminatedTasks.ContainsKey(taskId)) return;
                    if (cts.IsCancellationRequested)
                    {
                        task.Status = Core.Entities.TaskStatus.Cancelled;
                        task.Stage = "Cancelled";
                        task.Message = "Task was cancelled before it started";
                        task.EndTime = DateTime.UtcNow;
                        await _taskRepository.UpdateAsync(task, CancellationToken.None);
                        return;
                    }

                    // 更新为运行状态
                    task.Status = Core.Entities.TaskStatus.Running;
                    task.Progress = 0;
                    task.Stage = "Starting";
                    task.Message = "Task started";
                    await _taskRepository.UpdateAsync(task, CancellationToken.None);
                }
                finally { stateGate.Release(); }

                TaskStarted?.Invoke(this, EventArgs.Empty);
                started.TrySetResult();

                // 进度报告线程只做无阻塞暂存，由单飞后台协程在节流下落库；
                // Worker 结束后关闭进度写入，避免覆盖 finally 中的终态。
                var progressInbox = new ConcurrentQueue<TaskProgress>();
                int progressWriteScheduled = 0;
                int progressClosed = 0;

                var progress = new SynchronousProgress<TaskProgress>(p =>
                {
                    if (Volatile.Read(ref progressClosed) != 0) return;
                    progressInbox.Enqueue(p);
                    if (Interlocked.CompareExchange(ref progressWriteScheduled, 1, 0) == 0)
                        _ = PersistProgressAsync();
                });

                async Task PersistProgressAsync()
                {
                    try
                    {
                        while (true)
                        {
                            // 只保留最新值，历史报告直接丢弃
                            TaskProgress? latest = null;
                            while (progressInbox.TryDequeue(out var item)) latest = item;
                            if (latest is null || Volatile.Read(ref progressClosed) != 0) break;
                            await stateGate.WaitAsync();
                            try
                            {
                                if (Volatile.Read(ref progressClosed) != 0) break;
                                task.Progress = latest.Progress;
                                task.Stage = latest.Stage;
                                task.CurrentItem = latest.CurrentItem;
                                task.Message = latest.Message;
                                await _taskRepository.UpdateAsync(task, CancellationToken.None);
                            }
                            finally { stateGate.Release(); }
                            await Task.Delay(200);
                        }
                    }
                    catch
                    {
                        // 进度落库失败不影响任务执行；终态仍由 finally 统一写入
                    }
                    finally
                    {
                        Interlocked.Exchange(ref progressWriteScheduled, 0);
                        // 循环退出后又有新报告则重新调度一次
                        if (Volatile.Read(ref progressClosed) == 0 && !progressInbox.IsEmpty &&
                            Interlocked.CompareExchange(ref progressWriteScheduled, 1, 0) == 0)
                            _ = PersistProgressAsync();
                    }
                }

                // 执行任务
                await worker.ExecuteAsync(task, progress, cts.Token);
                Volatile.Write(ref progressClosed, 1);

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
                started.TrySetResult();
                await stateGate.WaitAsync();
                try
                {
                    if (!_forceTerminatedTasks.ContainsKey(taskId))
                    {
                        await _taskRepository.UpdateAsync(task, CancellationToken.None);
                        _runningTasks.TryRemove(taskId, out _);
                        TaskCompleted?.Invoke(this, new TaskCompletedEventArgs(task));
                    }
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
                finally
                {
                    _runningTasks.TryRemove(taskId, out _);
                    stateGate.Release();
                    cts.Dispose();
                    _taskStateGates.TryRemove(taskId, out _);
                    _forceTerminatedTasks.TryRemove(taskId, out _);
                    completion.TrySetResult();
                    if (completion.Task.IsCompletedSuccessfully)
                        _executions.TryRemove(new KeyValuePair<Guid, Task>(taskId, completion.Task));
                }
            }
        }, CancellationToken.None);

        await started.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    public async Task CancelTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (_runningTasks.TryGetValue(taskId, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
        await WaitForTaskAsync(taskId, cancellationToken);
    }

    public async Task WaitForTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (!_executions.TryGetValue(taskId, out var execution)) return;
        await execution.WaitAsync(cancellationToken);
        _executions.TryRemove(new KeyValuePair<Guid, Task>(taskId, execution));
    }

    public async Task CancelAllAndWaitAsync(CancellationToken cancellationToken = default)
    {
        foreach (var id in _executions.Keys) await CancelTaskAsync(id, cancellationToken);
    }

    /// <summary>
    /// 强制终止任务
    /// </summary>
    public async Task ForceTerminateTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (!_runningTasks.TryGetValue(taskId, out var cts))
        {
            return;
        }

        var stateGate = _taskStateGates.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_runningTasks.ContainsKey(taskId))
            {
                return;
            }

            _forceTerminatedTasks[taskId] = 0;
            cts.Cancel();

            var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
            if (task == null)
            {
                return;
            }

            task.Status = Core.Entities.TaskStatus.ForceTerminated;
            task.Stage = "Terminated";
            task.Message = "Task was force terminated";
            task.EndTime = DateTime.UtcNow;
            await _taskRepository.UpdateAsync(task, cancellationToken);
            _runningTasks.TryRemove(taskId, out _);
            TaskCompleted?.Invoke(this, new TaskCompletedEventArgs(task));
        }
        finally
        {
            stateGate.Release();
            // 任务已结束时移除本方法 GetOrAdd 补建的门控条目，防止字典残留；
            // 不 Dispose 信号量本身，避免与并发等待者竞态。
            if (!_runningTasks.ContainsKey(taskId))
                _taskStateGates.TryRemove(new KeyValuePair<Guid, SemaphoreSlim>(taskId, stateGate));
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

    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SynchronousProgress(Action<T> handler) => _handler = handler;

        public void Report(T value) => _handler(value);
    }

    private sealed class OperationWorker(string type,
        Func<IProgress<TaskProgress>, CancellationToken, Task> execute) : IWorker
    {
        public string Type => type;
        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Failure { get; private set; }

        public async Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken)
        {
            try
            {
                await execute(progress, cancellationToken);
                task.Stage = "Completed";
            }
            catch (Exception error)
            {
                Failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error);
                throw;
            }
        }
    }
}

public sealed class TaskCompletedEventArgs : EventArgs
{
    public ModTask Task { get; }

    public TaskCompletedEventArgs(ModTask task) => Task = task;
}
