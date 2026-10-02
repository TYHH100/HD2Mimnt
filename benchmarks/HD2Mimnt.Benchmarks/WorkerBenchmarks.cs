using BenchmarkDotNet.Attributes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using ModTaskStatus = HD2Mimnt.Core.Entities.TaskStatus;

namespace HD2Mimnt.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class WorkerBenchmarks
{
    private HD2MimntDbContext? _dbContext;
    private TaskRepository? _taskRepository;
    private string? _testDbPath;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"benchmark_worker_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        await _dbContext.InitializeDatabaseAsync();

        _taskRepository = new TaskRepository(_dbContext);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _dbContext?.Dispose();

        if (!string.IsNullOrEmpty(_testDbPath) && File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }
    }

    [Benchmark]
    public async Task CreateTask()
    {
        var task = new ModTask
        {
            Id = Guid.NewGuid(),
            Status = ModTaskStatus.Waiting,
            StartTime = DateTime.UtcNow,
            Progress = 0
        };

        await _taskRepository!.AddAsync(task);
    }

    [Benchmark]
    public async Task QueryTaskStatus()
    {
        var task = new ModTask
        {
            Id = Guid.NewGuid(),
            Status = ModTaskStatus.Running,
            StartTime = DateTime.UtcNow,
            Progress = 50
        };

        await _taskRepository!.AddAsync(task);
        var retrieved = await _taskRepository!.GetByIdAsync(task.Id);
    }

    [Benchmark]
    public async Task UpdateTaskProgress()
    {
        var task = new ModTask
        {
            Id = Guid.NewGuid(),
            Status = ModTaskStatus.Running,
            StartTime = DateTime.UtcNow,
            Progress = 0
        };

        await _taskRepository!.AddAsync(task);

        // Simulate progress updates
        for (int i = 10; i <= 100; i += 10)
        {
            task.Progress = i;
            await _taskRepository!.UpdateAsync(task);
        }
    }

    [Benchmark]
    public async Task GetAllTasks()
    {
        // Add some tasks
        for (int i = 0; i < 50; i++)
        {
            var task = new ModTask
            {
                Id = Guid.NewGuid(),
                Status = ModTaskStatus.Waiting,
                StartTime = DateTime.UtcNow.AddSeconds(-i),
                Progress = 0
            };

            await _taskRepository!.AddAsync(task);
        }

        var allTasks = await _taskRepository!.GetAllAsync();
    }

    [Benchmark]
    public async Task CancelTask()
    {
        var task = new ModTask
        {
            Id = Guid.NewGuid(),
            Status = ModTaskStatus.Running,
            StartTime = DateTime.UtcNow,
            Progress = 30
        };

        await _taskRepository!.AddAsync(task);

        // Cancel the task
        task.Status = ModTaskStatus.Cancelled;
        task.EndTime = DateTime.UtcNow;
        await _taskRepository!.UpdateAsync(task);
    }

    [Benchmark]
    public async Task GetTaskHistory()
    {
        // Add completed tasks
        for (int i = 0; i < 100; i++)
        {
            var task = new ModTask
            {
                Id = Guid.NewGuid(),
                Status = ModTaskStatus.Completed,
                StartTime = DateTime.UtcNow.AddHours(-i).AddMinutes(1),
                EndTime = DateTime.UtcNow.AddHours(-i).AddMinutes(5),
                Progress = 100
            };

            await _taskRepository!.AddAsync(task);
        }

        var history = await _taskRepository!.GetAllAsync();
    }

    [Benchmark]
    [Arguments(10)]
    [Arguments(50)]
    public async Task ConcurrentTaskCreation(int taskCount)
    {
        var tasks = new List<System.Threading.Tasks.Task>();

        for (int i = 0; i < taskCount; i++)
        {
            var task = System.Threading.Tasks.Task.Run(async () =>
            {
                var modTask = new ModTask
                {
                    Id = Guid.NewGuid(),
                    Status = ModTaskStatus.Waiting,
                    StartTime = DateTime.UtcNow,
                    Progress = 0
                };

                await _taskRepository!.AddAsync(modTask);
            });

            tasks.Add(task);
        }

        await System.Threading.Tasks.Task.WhenAll(tasks);
    }
}
