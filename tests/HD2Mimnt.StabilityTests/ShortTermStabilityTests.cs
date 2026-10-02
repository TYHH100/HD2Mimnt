using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.StabilityTests;

/// <summary>
/// 短期稳定性测试 (5-10分钟) - 快速验证
/// </summary>
public class ShortTermStabilityTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _testDbPath;
    private readonly HD2MimntDbContext _dbContext;
    private readonly ResourceMonitor _monitor;
    private bool _disposed;

    public ShortTermStabilityTests(ITestOutputHelper output)
    {
        _output = output;
        _testDbPath = Path.Combine(Path.GetTempPath(), $"stability_test_{Guid.NewGuid()}.db");
        _dbContext = new HD2MimntDbContext(_testDbPath);
        _dbContext.InitializeDatabaseAsync().Wait();

        _monitor = new ResourceMonitor(intervalSeconds: 10);
    }

    [Fact(Skip = "Long-running test - run manually")]
    public async Task SearchStability_5Minutes()
    {
        var modRepo = new ModRepository(_dbContext);
        var searchService = new SearchService(modRepo, NullLogger<SearchService>.Instance);

        // 准备测试数据
        await SeedTestMods(modRepo, 1000);

        var duration = TimeSpan.FromMinutes(5);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var iterations = 0;
        var errors = 0;

        var keywords = new[] { "weapon", "armor", "balance", "cosmetic", "gameplay" };
        var random = new Random();

        _output.WriteLine($"Starting 5-minute search stability test...");

        while (stopwatch.Elapsed < duration)
        {
            try
            {
                var keyword = keywords[random.Next(keywords.Length)];
                var results = await searchService.SearchAsync(keyword);
                iterations++;

                if (iterations % 100 == 0)
                {
                    _output.WriteLine($"Completed {iterations} iterations in {stopwatch.Elapsed.TotalMinutes:F2} minutes");
                }

                await Task.Delay(50); // 模拟真实间隔
            }
            catch (Exception ex)
            {
                errors++;
                _output.WriteLine($"Error at iteration {iterations}: {ex.Message}");
            }
        }

        stopwatch.Stop();
        var report = _monitor.GenerateReport(stopwatch.Elapsed);

        _output.WriteLine($"\n=== Test Completed ===");
        _output.WriteLine($"Duration: {stopwatch.Elapsed.TotalMinutes:F2} minutes");
        _output.WriteLine($"Iterations: {iterations}");
        _output.WriteLine($"Errors: {errors}");
        _output.WriteLine($"Success Rate: {(double)(iterations - errors) / iterations * 100:F2}%");
        _output.WriteLine(report.ToString());

        Assert.True(report.IsHealthy, $"Resource issues detected: {report.Message}");
        Assert.True(errors == 0, $"Encountered {errors} errors during test");
    }

    [Fact(Skip = "Long-running test - run manually")]
    public async Task TaskCreation_5Minutes()
    {
        var taskRepo = new TaskRepository(_dbContext);

        var duration = TimeSpan.FromMinutes(5);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var iterations = 0;
        var errors = 0;

        _output.WriteLine($"Starting 5-minute task creation stability test...");

        while (stopwatch.Elapsed < duration)
        {
            try
            {
                var task = new ModTask
                {
                    Id = Guid.NewGuid(),
                    Name = $"Test Task {iterations}",
                    Type = "Test",
                    Status = Core.Entities.TaskStatus.Running,
                    StartTime = DateTime.UtcNow
                };

                await taskRepo.AddAsync(task);
                iterations++;

                if (iterations % 100 == 0)
                {
                    _output.WriteLine($"Created {iterations} tasks in {stopwatch.Elapsed.TotalMinutes:F2} minutes");
                }

                await Task.Delay(100);
            }
            catch (Exception ex)
            {
                errors++;
                _output.WriteLine($"Error at iteration {iterations}: {ex.Message}");
            }
        }

        stopwatch.Stop();
        var report = _monitor.GenerateReport(stopwatch.Elapsed);

        _output.WriteLine($"\n=== Test Completed ===");
        _output.WriteLine($"Duration: {stopwatch.Elapsed.TotalMinutes:F2} minutes");
        _output.WriteLine($"Tasks Created: {iterations}");
        _output.WriteLine($"Errors: {errors}");
        _output.WriteLine(report.ToString());

        Assert.True(report.IsHealthy, $"Resource issues detected: {report.Message}");
        Assert.True(errors == 0, $"Encountered {errors} errors during test");
    }

    [Fact]
    public async Task ConcurrentTaskCreation_StressTest()
    {
        var taskRepo = new TaskRepository(_dbContext);

        var concurrency = 10;
        var tasksPerThread = 50;
        var totalExpected = concurrency * tasksPerThread;

        _output.WriteLine($"Creating {totalExpected} tasks with {concurrency} concurrent threads...");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var tasks = new List<Task>();
        var successCount = 0;
        var errorCount = 0;
        var lockObj = new object();

        for (int i = 0; i < concurrency; i++)
        {
            var threadId = i;
            tasks.Add(Task.Run(async () =>
            {
                for (int j = 0; j < tasksPerThread; j++)
                {
                    try
                    {
                        var task = new ModTask
                        {
                            Id = Guid.NewGuid(),
                            Name = $"Task-{threadId}-{j}",
                            Type = "Stress",
                            Status = Core.Entities.TaskStatus.Running,
                            StartTime = DateTime.UtcNow
                        };

                        await taskRepo.AddAsync(task);

                        lock (lockObj) { successCount++; }
                    }
                    catch
                    {
                        lock (lockObj) { errorCount++; }
                    }
                }
            }));
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        _output.WriteLine($"\n=== Concurrent Test Results ===");
        _output.WriteLine($"Duration: {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _output.WriteLine($"Total Expected: {totalExpected}");
        _output.WriteLine($"Success: {successCount}");
        _output.WriteLine($"Errors: {errorCount}");
        _output.WriteLine($"Success Rate: {(double)successCount / totalExpected * 100:F2}%");

        // 修复后应该有高成功率
        Assert.True(successCount >= totalExpected * 0.95,
            $"Success rate too low: {successCount}/{totalExpected}");
    }

    private async Task SeedTestMods(ModRepository repository, int count)
    {
        var categories = Enum.GetValues<ModCategory>();
        var tags = new[] { "weapon", "armor", "balance", "cosmetic", "gameplay" };

        for (int i = 0; i < count; i++)
        {
            var mod = new Mod
            {
                Id = Guid.NewGuid(),
                Name = $"Test Mod {i}",
                Version = "1.0.0",
                Author = $"Author {i % 50}",
                Description = $"Description for mod {i}",
                Path = $"C:\\Mods\\Mod{i}",
                Category = categories[i % categories.Length],
                Tags = tags.Take((i % tags.Length) + 1).ToList(),
                WorkflowType = "Legacy",
                ImportedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                Size = 1024 * 1024,
                Hash = Guid.NewGuid().ToString(),
                Source = ModSource.LocalFolder,
                Status = ModStatus.Ready
            };

            await repository.AddAsync(mod);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        _monitor?.Dispose();
        _dbContext?.Dispose();

        if (File.Exists(_testDbPath))
        {
            File.Delete(_testDbPath);
        }

        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
