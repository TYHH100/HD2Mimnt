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
    private readonly IWorkerHostManager? _workerHostManager;
    private readonly ConcurrentDictionary<Guid, Task> _executions = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _runningTasks;
    private readonly ConcurrentDictionary<string, IWorker> _workers;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _taskStateGates;
    private readonly ConcurrentDictionary<Guid, byte> _forceTerminatedTasks;
    private readonly ConcurrentDictionary<Guid, IReadOnlyDictionary<string, string>> _taskArguments = new();
    private readonly ConcurrentDictionary<Guid, IProgress<TaskProgress>> _taskProgressObservers = new();
    private readonly ConcurrentDictionary<Guid, WorkerPromptHandler> _taskPromptHandlers = new();
    private readonly ConcurrentDictionary<Guid, string> _taskResultData = new();
    // 发起方自己会提示结果（导入/部署/清理浮层），避免再弹一条通用“后台任务已完成”。
    private readonly ConcurrentDictionary<Guid, byte> _tasksWithoutAutoNotification = new();

    public event EventHandler<TaskCompletedEventArgs>? TaskCompleted;
    public event EventHandler? TaskStarted;

    public TaskService(
        ITaskRepository taskRepository,
        IServiceProvider serviceProvider,
        IWorkerHostManager? workerHostManager = null)
    {
        _taskRepository = taskRepository;
        _workerHostManager = workerHostManager;
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
    /// 注册在 HD2Mimnt.Worker 独立进程执行的任务类型；
    /// Worker 崩溃/卡死不影响主程序，取消走优雅通知 + Kill 兜底，强杀直接终止进程。
    /// </summary>
    public void RegisterProcessWorker(string type,
        Func<ModTask, Dictionary<string, string>>? argumentsFactory = null)
    {
        if (_workerHostManager == null)
            throw new InvalidOperationException("进程 Worker 需要 IWorkerHostManager，请在构造 TaskService 时注入。");
        var argumentsFor = argumentsFactory ?? (_ => new Dictionary<string, string>());
        RegisterWorker(new ProcessWorker(type, _workerHostManager,
            task =>
            {
                var merged = new Dictionary<string, string>(argumentsFor(task));
                if (_taskArguments.TryGetValue(task.Id, out var extra))
                {
                    foreach (var pair in extra) merged[pair.Key] = pair.Value;
                }
                return merged;
            },
            taskId => _taskPromptHandlers.GetValueOrDefault(taskId),
            (taskId, data) => _taskResultData[taskId] = data));
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

    /// <summary>创建任务并登记执行参数（只存在内存：任务启动后立即消费，不随任务记录持久化）。</summary>
    public async Task<Guid> CreateTaskAsync(string name, string type,
        IReadOnlyDictionary<string, string>? arguments, CancellationToken cancellationToken = default)
    {
        var id = await CreateTaskAsync(name, type, cancellationToken);
        if (arguments is { Count: > 0 }) _taskArguments[id] = new Dictionary<string, string>(arguments);
        return id;
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

    /// <summary>
    /// 运行一个已注册的进程 Worker：创建任务记录并传入参数、进度观察者和交互处理器，
    /// 返回任务终态与任务私有结果数据（JSON）。取消返回 Cancelled 终态而不抛异常。
    /// </summary>
    public async Task<ProcessTaskOutcome> RunProcessTaskAsync(
        string name,
        string type,
        IReadOnlyDictionary<string, string>? arguments = null,
        IProgress<TaskProgress>? progressObserver = null,
        WorkerPromptHandler? promptHandler = null,
        CancellationToken cancellationToken = default)
    {
        var id = await CreateTaskAsync(name, type, arguments, cancellationToken);
        _tasksWithoutAutoNotification[id] = 0;
        if (progressObserver != null) _taskProgressObservers[id] = progressObserver;
        if (promptHandler != null) _taskPromptHandlers[id] = promptHandler;
        try
        {
            try { await StartTaskAsync(id, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            await WaitForTaskAsync(id, CancellationToken.None);
            var task = await _taskRepository.GetByIdAsync(id, CancellationToken.None);
            _taskResultData.TryRemove(id, out var data);
            return new ProcessTaskOutcome(task, data);
        }
        finally
        {
            _taskProgressObservers.TryRemove(id, out _);
            _taskPromptHandlers.TryRemove(id, out _);
            _taskArguments.TryRemove(id, out _);
            _taskResultData.TryRemove(id, out _);
            _tasksWithoutAutoNotification.TryRemove(id, out _);
        }
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
                    // UI 浮层等调用方观察者只做展示，异常不能影响任务执行与落库
                    if (_taskProgressObservers.TryGetValue(taskId, out var observer))
                    {
                        try { observer.Report(p); } catch { }
                    }
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
                                // 强制终止后进度不得再写回，否则会把 Running 状态复活
                                if (_forceTerminatedTasks.ContainsKey(taskId)) break;
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
                if (!_forceTerminatedTasks.ContainsKey(taskId))
                {
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
            }
            catch (OperationCanceledException)
            {
                // 任务被取消；已强制终止时保留终止终态，执行体不得覆盖
                if (!_forceTerminatedTasks.ContainsKey(taskId))
                {
                    task.Status = Core.Entities.TaskStatus.Cancelled;
                    task.Stage = "Cancelled";
                    task.Message = "Task was cancelled";
                    task.EndTime = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                // 任务失败；已强制终止时保留终止终态
                if (!_forceTerminatedTasks.ContainsKey(taskId))
                {
                    task.Status = Core.Entities.TaskStatus.Failed;
                    task.Stage = "Failed";
                    task.Message = ex.Message;
                    task.Errors.Add(ex.ToString());
                    task.EndTime = DateTime.UtcNow;
                }
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
                        var notifyUser = !_tasksWithoutAutoNotification.TryRemove(taskId, out _);
                        TaskCompleted?.Invoke(this, new TaskCompletedEventArgs(task, notifyUser));
                    }
                }
                catch (Exception error)
                {
                    completion.TrySetException(error);
                }
                finally
                {
                    _runningTasks.TryRemove(taskId, out _);
                    _taskArguments.TryRemove(taskId, out _);
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

        // 进程 Worker 必须先杀进程再持久化终态，否则运行中的 Worker 会继续改游戏/数据库；
        // 但杀进程会让执行体立即收尾，因此必须持有任务闸门后再杀，避免终态被写成 Failed/Cancelled。
        var stateGate = _taskStateGates.GetOrAdd(taskId, _ => new SemaphoreSlim(1, 1));
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!_runningTasks.ContainsKey(taskId))
            {
                return;
            }

            _forceTerminatedTasks[taskId] = 0;

            var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
            if (task != null && _workers.TryGetValue(task.Type, out var registeredWorker)
                && registeredWorker is ProcessWorker processWorker)
            {
                await processWorker.ForceTerminateAsync(taskId);
            }

            cts.Cancel();
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
            var notifyUser = !_tasksWithoutAutoNotification.TryRemove(taskId, out _);
            TaskCompleted?.Invoke(this, new TaskCompletedEventArgs(task, notifyUser));
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
    /// 已结束的任务才能删除记录；运行中和等待中的任务只能取消。
    /// </summary>
    public static bool IsDeletable(ModTask task) =>
        task.Status is not Core.Entities.TaskStatus.Running and not Core.Entities.TaskStatus.Waiting;

    /// <summary>
    /// 删除单条已结束的任务记录
    /// </summary>
    public async Task<bool> DeleteTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        if (task == null || !IsDeletable(task)) return false;
        await _taskRepository.DeleteAsync(taskId, cancellationToken);
        return true;
    }

    /// <summary>
    /// 删除全部已结束的任务记录，返回删除数量
    /// </summary>
    public async Task<int> DeleteFinishedTasksAsync(CancellationToken cancellationToken = default)
    {
        var finished = (await _taskRepository.GetAllAsync(cancellationToken)).Where(IsDeletable).ToList();
        foreach (var task in finished)
        {
            await _taskRepository.DeleteAsync(task.Id, cancellationToken);
        }
        return finished.Count;
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

    /// <summary>把任务执行体桥接到 HD2Mimnt.Worker 独立进程，终态语义与进程内 Worker 一致。</summary>
    private sealed class ProcessWorker(
        string type,
        IWorkerHostManager manager,
        Func<ModTask, Dictionary<string, string>> argumentsFactory,
        Func<Guid, WorkerPromptHandler?> promptHandlerLookup,
        Action<Guid, string> dataSink) : IWorker
    {
        public string Type => type;

        public Task ForceTerminateAsync(Guid taskId) => manager.ForceTerminateWorkerAsync(taskId);

        public async Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken cancellationToken)
        {
            // 不用 Progress<T>（构造时捕获同步上下文会经 UI 调度器转发）；
            // 监听循环线程直接报告，TaskService 的进度收件箱是非阻塞的。
            var bridge = new SynchronousProgress<WorkerTaskProgress>(p => progress.Report(new TaskProgress
            {
                Progress = p.Progress,
                Stage = p.Stage,
                CurrentItem = p.CurrentItem,
                Message = p.Message
            }));

            var id = await manager.StartWorkerAsync(Type, argumentsFactory(task), bridge, cancellationToken, task.Id,
                promptHandlerLookup(task.Id));
            var completion = manager.WaitForCompletionAsync(id, CancellationToken.None);
            try
            {
                // 取消必须立即转发给 Worker 并等待其清理；不能等 Worker 自然结束后才处理。
                var cancelSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var registration = cancellationToken.Register(() => cancelSignal.TrySetResult());
                var winner = await Task.WhenAny(completion, cancelSignal.Task);
                if (winner == cancelSignal.Task)
                {
                    await manager.CancelWorkerAsync(id);
                    try { await completion; } catch { }
                    throw new OperationCanceledException();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                await manager.CancelWorkerAsync(id);
                throw;
            }

            var result = await completion;
            if (result.Cancelled || cancellationToken.IsCancellationRequested)
            {
                await manager.CancelWorkerAsync(id);
                throw new OperationCanceledException();
            }

            task.Errors.AddRange(result.Errors);
            task.Result = result.Result ?? string.Empty;
            if (!string.IsNullOrEmpty(result.Data)) dataSink(task.Id, result.Data);
            if (result.PartiallyFailed)
            {
                // 部分成功：单项失败已写入 Errors，整体仍算完成，保留 PartiallyFailed
                task.Status = Core.Entities.TaskStatus.PartiallyFailed;
                return;
            }

            if (result.Crashed || !result.Success)
            {
                throw new InvalidOperationException(result.Errors.FirstOrDefault()
                    ?? result.Result ?? "Worker process failed");
            }
        }
    }
}

public sealed class TaskCompletedEventArgs : EventArgs
{
    public ModTask Task { get; }

    /// <summary>是否由任务中心统一弹“后台任务”通知；发起方已有自己的结果提示时为 false。</summary>
    public bool NotifyUser { get; }

    public TaskCompletedEventArgs(ModTask task, bool notifyUser = true)
    {
        Task = task;
        NotifyUser = notifyUser;
    }
}

/// <summary>进程 Worker 的运行结果：任务终态记录 + 任务私有结果数据（JSON，可能为空）。</summary>
public sealed record ProcessTaskOutcome(ModTask? Task, string? Data);
