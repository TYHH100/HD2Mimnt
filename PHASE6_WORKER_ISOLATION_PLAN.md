# Phase 6 Worker Process Isolation Implementation Plan

## Overview

This addresses the technical debt from Phase 6: converting in-process workers to separate process execution with IPC communication, meeting the plan's hard requirement: **"Worker 崩了，主程序别跟着一起去世"**

---

## Current State vs. Required State

### Current (In-Process)
```
HD2Mimnt.exe
    ↓ (direct method calls)
ImportWorker, ScanWorker, ValidationWorker
    ↓
TaskService manages in same process
```

**Problem**: Worker crash = UI crash ❌

### Required (Process Isolation)
```
HD2Mimnt.exe (UI + TaskService)
    ↕ IPC (Named Pipes)
HD2Mimnt.Worker.exe (Worker Host)
    ↓
ImportWorker, ScanWorker, ValidationWorker
```

**Goal**: Worker crash ≠ UI crash ✅

---

## Architecture Design

### Components

1. **HD2Mimnt.Worker.exe** (New Project)
   - Console application
   - Hosts worker instances
   - Communicates via Named Pipes
   - Isolated process per task (or shared pool)

2. **WorkerHostManager** (Application Layer)
   - Spawns worker processes
   - Manages IPC connections
   - Routes commands to workers
   - Handles process crashes

3. **IPC Protocol** (Shared)
   - Message format (JSON)
   - Command/Response pattern
   - Progress streaming
   - Cancellation support

### Process Lifecycle

```
1. TaskService.StartTaskAsync(taskId, workerType)
2. WorkerHostManager.SpawnWorkerProcess(taskId)
3. HD2Mimnt.Worker.exe starts with args: --task-id {guid} --worker {type}
4. Worker process connects to named pipe: \\.\pipe\HD2Mimnt.Worker.{taskId}
5. Main app sends ExecuteCommand via IPC
6. Worker executes, streams progress via IPC
7. Worker completes, sends result via IPC
8. Worker process exits (clean shutdown)

On crash:
- Worker process dies unexpectedly
- Main app detects pipe disconnection
- TaskService updates task status to Failed
- UI remains responsive
```

---

## Implementation Phases

### Phase 6.A: IPC Infrastructure

**New Files**:
- `src/HD2Mimnt.Core/IPC/WorkerMessage.cs`
- `src/HD2Mimnt.Core/IPC/WorkerCommand.cs`
- `src/HD2Mimnt.Core/IPC/WorkerResponse.cs`
- `src/HD2Mimnt.Application/IPC/NamedPipeServer.cs`
- `src/HD2Mimnt.Application/IPC/NamedPipeClient.cs`

**Message Format**:
```csharp
public class WorkerMessage
{
    public string MessageType { get; set; } // "Command", "Response", "Progress", "Error"
    public Guid TaskId { get; set; }
    public string? Payload { get; set; } // JSON-serialized content
}

public class ExecuteWorkerCommand
{
    public string WorkerType { get; set; } // "Import", "Scan", "Validation"
    public Dictionary<string, string> Arguments { get; set; }
}

public class WorkerProgressResponse
{
    public int CurrentStep { get; set; }
    public int TotalSteps { get; set; }
    public string? Message { get; set; }
}

public class WorkerCompletedResponse
{
    public bool Success { get; set; }
    public string? Result { get; set; }
    public List<string>? Errors { get; set; }
}
```

**Tests**: 10-12 tests
- Message serialization/deserialization
- Pipe connection establishment
- Send/receive commands
- Handle disconnection

---

### Phase 6.B: Worker Host Executable

**New Project**: `src/HD2Mimnt.Worker/`

**Structure**:
```
HD2Mimnt.Worker/
├── HD2Mimnt.Worker.csproj (OutputType: Exe)
├── Program.cs (entry point)
├── WorkerHost.cs (manages worker lifecycle)
├── WorkerFactory.cs (creates worker instances)
└── IPC/
    └── WorkerIpcClient.cs (communicates with main app)
```

**Program.cs**:
```csharp
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Parse: --task-id {guid} --worker {type} --pipe-name {name}
        var taskId = ParseTaskId(args);
        var workerType = ParseWorkerType(args);
        var pipeName = ParsePipeName(args);
        
        var host = new WorkerHost(taskId, pipeName);
        
        try
        {
            await host.ConnectAsync();
            var worker = WorkerFactory.Create(workerType, host);
            await worker.ExecuteAsync();
            return 0; // Success
        }
        catch (Exception ex)
        {
            await host.SendErrorAsync(ex);
            return 1; // Failure
        }
    }
}
```

**WorkerHost.cs**:
```csharp
public class WorkerHost
{
    private readonly Guid _taskId;
    private readonly NamedPipeClientStream _pipe;
    
    public async Task ConnectAsync()
    {
        await _pipe.ConnectAsync(timeout: 5000);
    }
    
    public async Task<ExecuteWorkerCommand> ReceiveCommandAsync()
    {
        var message = await ReadMessageAsync();
        return JsonSerializer.Deserialize<ExecuteWorkerCommand>(message.Payload);
    }
    
    public async Task SendProgressAsync(int current, int total, string message)
    {
        var progress = new WorkerProgressResponse { ... };
        await SendMessageAsync("Progress", progress);
    }
    
    public async Task SendCompletedAsync(bool success, string? result, List<string>? errors)
    {
        var response = new WorkerCompletedResponse { ... };
        await SendMessageAsync("Completed", response);
    }
}
```

**Tests**: 15-18 tests
- Program argument parsing
- Worker factory creation
- IPC client connection
- Progress reporting
- Error handling

---

### Phase 6.C: Worker Host Manager

**New Files**:
- `src/HD2Mimnt.Application/Services/WorkerHostManager.cs`
- `src/HD2Mimnt.Core/Interfaces/IWorkerHostManager.cs`

**IWorkerHostManager.cs**:
```csharp
public interface IWorkerHostManager
{
    Task<Guid> StartWorkerAsync(string workerType, Dictionary<string, string> arguments, IProgress<TaskProgress> progress, CancellationToken cancellationToken);
    Task<bool> IsWorkerRunningAsync(Guid taskId);
    Task CancelWorkerAsync(Guid taskId);
    Task ForceTerminateWorkerAsync(Guid taskId);
}
```

**WorkerHostManager.cs**:
```csharp
public class WorkerHostManager : IWorkerHostManager
{
    private readonly ConcurrentDictionary<Guid, WorkerProcess> _runningWorkers = new();
    
    public async Task<Guid> StartWorkerAsync(...)
    {
        var taskId = Guid.NewGuid();
        var pipeName = $"HD2Mimnt.Worker.{taskId}";
        
        // Create named pipe server
        var pipeServer = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Message, PipeOptions.Asynchronous);
        
        // Spawn worker process
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "HD2Mimnt.Worker.exe",
            Arguments = $"--task-id {taskId} --worker {workerType} --pipe-name {pipeName}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        
        var process = Process.Start(processStartInfo);
        
        // Wait for connection
        await pipeServer.WaitForConnectionAsync(cancellationToken);
        
        // Send execute command
        var command = new ExecuteWorkerCommand { WorkerType = workerType, Arguments = arguments };
        await SendCommandAsync(pipeServer, command);
        
        // Start progress listener
        var workerProcess = new WorkerProcess(taskId, process, pipeServer, progress);
        _runningWorkers[taskId] = workerProcess;
        
        _ = Task.Run(async () => await ListenForProgressAsync(workerProcess, cancellationToken));
        
        return taskId;
    }
    
    private async Task ListenForProgressAsync(WorkerProcess worker, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await ReadMessageAsync(worker.Pipe, cancellationToken);
                
                if (message.MessageType == "Progress")
                {
                    var progressData = JsonSerializer.Deserialize<WorkerProgressResponse>(message.Payload);
                    worker.Progress?.Report(new TaskProgress { ... });
                }
                else if (message.MessageType == "Completed")
                {
                    var result = JsonSerializer.Deserialize<WorkerCompletedResponse>(message.Payload);
                    worker.Complete(result.Success, result.Result, result.Errors);
                    break;
                }
                else if (message.MessageType == "Error")
                {
                    worker.Fail(message.Payload);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // Worker crashed or pipe disconnected
            worker.Crash(ex.Message);
        }
        finally
        {
            worker.Dispose();
            _runningWorkers.TryRemove(worker.TaskId, out _);
        }
    }
    
    public async Task ForceTerminateWorkerAsync(Guid taskId)
    {
        if (_runningWorkers.TryGetValue(taskId, out var worker))
        {
            worker.Process.Kill();
            worker.Dispose();
            _runningWorkers.TryRemove(taskId, out _);
        }
    }
}

internal class WorkerProcess : IDisposable
{
    public Guid TaskId { get; }
    public Process Process { get; }
    public NamedPipeServerStream Pipe { get; }
    public IProgress<TaskProgress>? Progress { get; }
    
    private TaskCompletionSource<WorkerResult> _completionSource = new();
    
    public Task<WorkerResult> CompletionTask => _completionSource.Task;
    
    public void Complete(bool success, string? result, List<string>? errors)
    {
        _completionSource.SetResult(new WorkerResult { Success = success, Result = result, Errors = errors });
    }
    
    public void Fail(string error)
    {
        _completionSource.SetResult(new WorkerResult { Success = false, Errors = new List<string> { error } });
    }
    
    public void Crash(string error)
    {
        _completionSource.SetResult(new WorkerResult { Success = false, Crashed = true, Errors = new List<string> { $"Worker crashed: {error}" } });
    }
    
    public void Dispose()
    {
        Pipe?.Dispose();
        Process?.Dispose();
    }
}
```

**Tests**: 12-15 tests
- Start worker process
- IPC communication
- Progress reporting
- Worker completion
- Worker crash detection
- Force termination

---

### Phase 6.D: TaskService Integration

**Modify**: `src/HD2Mimnt.Application/Services/TaskService.cs`

**Changes**:
```csharp
public class TaskService : ITaskService
{
    private readonly IWorkerHostManager _workerHostManager;
    
    public async Task StartTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        
        // Get worker arguments
        var arguments = PrepareWorkerArguments(task);
        
        // Start worker in separate process
        var workerTaskId = await _workerHostManager.StartWorkerAsync(
            task.Type,
            arguments,
            new Progress<TaskProgress>(p => OnProgressReceived(taskId, p)),
            cancellationToken
        );
        
        // Update task status
        task.Status = Core.Enums.TaskStatus.Running;
        await _taskRepository.UpdateAsync(task, cancellationToken);
        
        // Wait for completion (in background)
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await WaitForWorkerCompletionAsync(workerTaskId);
                await HandleWorkerCompletion(taskId, result);
            }
            catch (Exception ex)
            {
                await HandleWorkerCrash(taskId, ex);
            }
        });
    }
    
    public async Task ForceTerminateTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await _workerHostManager.ForceTerminateWorkerAsync(taskId);
        
        var task = await _taskRepository.GetByIdAsync(taskId, cancellationToken);
        task.Status = Core.Enums.TaskStatus.ForceTerminated;
        await _taskRepository.UpdateAsync(task, cancellationToken);
    }
}
```

**Tests**: 10-12 tests (modify existing TaskServiceTests)
- Start task with worker process
- Receive progress updates
- Handle worker completion
- Handle worker crash (UI remains responsive)
- Force terminate worker

---

### Phase 6.E: Worker Refactoring

**Modify existing workers**:
- `ImportWorker.cs`
- `ScanWorker.cs`
- `ValidationWorker.cs`

**Changes**:
```csharp
// Before: Constructor injection of repositories
public class ImportWorker : IWorker
{
    private readonly IModService _modService;
    
    public ImportWorker(IModService modService)
    {
        _modService = modService;
    }
}

// After: Workers receive serialized arguments, use WorkerHost for communication
public class ImportWorker : IWorker
{
    private readonly WorkerHost _host;
    
    public ImportWorker(WorkerHost host)
    {
        _host = host;
    }
    
    public async Task ExecuteAsync()
    {
        var command = await _host.ReceiveCommandAsync();
        var sourcePath = command.Arguments["sourcePath"];
        
        // Execute import logic
        await _host.SendProgressAsync(1, 3, "Scanning files...");
        var files = ScanFiles(sourcePath);
        
        await _host.SendProgressAsync(2, 3, "Copying files...");
        CopyFiles(files);
        
        await _host.SendProgressAsync(3, 3, "Completed");
        await _host.SendCompletedAsync(success: true, result: "Import completed", errors: null);
    }
}
```

**Dependency Injection in Worker.exe**:
```csharp
// Worker process needs minimal DI
var services = new ServiceCollection();
services.AddSingleton<HD2MimntDbContext>(); // Read-only access
services.AddSingleton<IModRepository, ModRepository>();
services.AddSingleton<IModService, ModService>();
// ... register other dependencies
```

**Tests**: Modify existing worker tests
- Workers still work with new IPC pattern
- Progress reporting via WorkerHost
- Error handling

---

## Database Changes

No database schema changes required. TaskService already tracks task status.

---

## Testing Strategy

### Unit Tests (60-70 tests total)
- IPC message serialization (10 tests)
- Named pipe communication (12 tests)
- Worker host manager (15 tests)
- Worker process lifecycle (15 tests)
- TaskService integration (12 tests)

### Integration Tests (10-15 tests)
- End-to-end worker execution
- Worker crash recovery (main app survives)
- Multiple workers running concurrently
- Worker cancellation
- Worker force termination

### Crash Isolation Test
```csharp
[Fact]
public async Task WorkerCrash_Should_Not_Crash_MainApp()
{
    // Start a worker
    var taskId = await _taskService.StartTaskAsync(...);
    
    // Worker crashes (simulated by killing process)
    var workerProcess = FindWorkerProcess(taskId);
    workerProcess.Kill();
    
    // Main app should detect crash and mark task as failed
    await Task.Delay(1000);
    var task = await _taskRepository.GetByIdAsync(taskId);
    Assert.Equal(TaskStatus.Failed, task.Status);
    Assert.Contains("crashed", task.Errors[0].ToLower());
    
    // Main app should still be responsive
    var newTaskId = await _taskService.CreateTaskAsync("Test", "Import");
    Assert.NotEqual(Guid.Empty, newTaskId);
}
```

---

## Success Criteria

✅ HD2Mimnt.Worker.exe built successfully  
✅ Worker process spawns on task start  
✅ IPC communication works (commands + progress)  
✅ Worker crash does NOT crash main app  
✅ Worker force termination works  
✅ All existing tests still pass  
✅ 60+ new tests pass  
✅ UI remains responsive during worker execution  

---

## Estimated Effort

- Phase 6.A (IPC Infrastructure): 8 hours
- Phase 6.B (Worker Host Executable): 12 hours
- Phase 6.C (Worker Host Manager): 12 hours
- Phase 6.D (TaskService Integration): 8 hours
- Phase 6.E (Worker Refactoring): 8 hours
- Testing & Bug Fixes: 12 hours

**Total**: 60 hours (~1.5 weeks)

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| IPC performance overhead | Medium | Use binary serialization if needed |
| Pipe connection failures | High | Retry logic, timeout handling |
| Worker startup time | Low | Pre-warm worker pool (future optimization) |
| Breaking existing tests | High | Incremental refactoring, keep old code path temporarily |
| Cross-platform compatibility | Medium | Test on Windows/Linux, use .NET APIs |

---

## Dependencies

- System.IO.Pipes (.NET built-in)
- System.Text.Json (.NET built-in)
- System.Diagnostics.Process (.NET built-in)

---

## Notes

- This implementation provides the foundation for Phase 17 (Plugin System)
- Similar IPC patterns will be reused for plugin hosting
- Worker process isolation is a hard requirement from the plan
- Must be completed before Phase 17 Plugin System
