using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Columns;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.Infrastructure.Archive;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// 归档导入性能基准测试
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class ArchiveImportBenchmarks
{
    private string _testDataPath = null!;
    private ServiceProvider _serviceProvider = null!;
    private SqliteConnection _connection = null!;

    private class Config : ManualConfig
    {
        public Config()
        {
            AddDiagnoser(MemoryDiagnoser.Default);
            AddDiagnoser(ThreadingDiagnoser.Default);

            AddExporter(MarkdownExporter.GitHub);
            AddExporter(HtmlExporter.Default);
            AddExporter(CsvExporter.Default);

            AddColumn(StatisticColumn.Mean);
            AddColumn(StatisticColumn.Error);
            AddColumn(StatisticColumn.StdDev);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);

            AddJob(Job.Default
                .WithWarmupCount(2)      // 预热 2 次
                .WithIterationCount(3)   // 迭代 3 次
                .WithLaunchCount(1));    // 启动 1 次
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        // 查找测试数据目录（向上搜索，因为 BenchmarkDotNet 在子目录中运行）
        var currentDir = Directory.GetCurrentDirectory();
        var searchDir = currentDir;

        // 尝试在当前目录和父目录中查找 TestData
        while (searchDir != null)
        {
            var candidatePath = Path.Combine(searchDir, "TestData", "Generated");
            if (Directory.Exists(candidatePath))
            {
                _testDataPath = candidatePath;
                break;
            }

            var parentDir = Directory.GetParent(searchDir);
            searchDir = parentDir?.FullName;
        }

        if (_testDataPath == null)
        {
            throw new DirectoryNotFoundException(
                $"Test data directory not found. Searched from: {currentDir}\n" +
                "Please run TestFileGenerator first to generate test files.");
        }

        Console.WriteLine($"Test data path: {_testDataPath}");

        // 配置服务
        _serviceProvider = ConfigureServices();

        // 初始化数据库
        InitializeDatabase();

        Console.WriteLine("✓ Services configured and database initialized");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _connection?.Dispose();
        _serviceProvider?.Dispose();
    }

    private ServiceProvider ConfigureServices()
    {
        var services = new ServiceCollection();

        // 配置 InMemory SQLite 数据库
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        // 注册 HD2MimntDbContext (不使用 EF Core)
        var tempDbPath = Path.Combine(Path.GetTempPath(), $"benchmark_{Guid.NewGuid():N}.db");
        services.AddSingleton(new HD2MimntDbContext(tempDbPath));

        // 注册 Repositories
        services.AddScoped<ILogicalModRepository, LogicalModRepository>();
        services.AddScoped<IArchiveAnalysisCacheRepository, ArchiveAnalysisCacheRepository>();
        services.AddScoped<IFileOwnershipRepository, FileOwnershipRepository>();
        services.AddScoped<IDeploymentTransactionRepository, DeploymentTransactionRepository>();

        // 注册 Services
        services.AddScoped<IArchiveInspector, ArchiveInspector>();
        services.AddScoped<IComponentDetector, ComponentDetector>();
        services.AddScoped<IHd2IrDetector, Hd2IrDetector>();
        services.AddScoped<IModMerger, ModMerger>();
        services.AddScoped<DiffEngine>();

        // 注册 Logger（使用 NullLogger 避免日志开销影响性能测试）
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.None));

        return services.BuildServiceProvider();
    }

    private void InitializeDatabase()
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<HD2MimntDbContext>();

        // 创建数据库架构
        var connection = dbContext.GetConnectionAsync().GetAwaiter().GetResult();
    }

    [Benchmark(Description = "Import 100MB ZIP")]
    public async Task<AnalysisResult> Import_100MB()
    {
        var filePath = Path.Combine(_testDataPath, "test_100mb_flat.zip");
        return await AnalyzeArchiveAsync(filePath);
    }

    [Benchmark(Description = "Import 500MB ZIP")]
    public async Task<AnalysisResult> Import_500MB()
    {
        var filePath = Path.Combine(_testDataPath, "test_500mb_flat.zip");
        return await AnalyzeArchiveAsync(filePath);
    }

    [Benchmark(Description = "Import 1GB ZIP")]
    public async Task<AnalysisResult> Import_1GB()
    {
        var filePath = Path.Combine(_testDataPath, "test_1gb_flat.zip");
        return await AnalyzeArchiveAsync(filePath);
    }

    [Benchmark(Description = "Import Nested 2 Levels")]
    public async Task<AnalysisResult> Import_Nested2()
    {
        var filePath = Path.Combine(_testDataPath, "test_nested_2.zip");
        return await AnalyzeArchiveAsync(filePath);
    }

    [Benchmark(Description = "Import Nested 5 Levels")]
    public async Task<AnalysisResult> Import_Nested5()
    {
        var filePath = Path.Combine(_testDataPath, "test_nested_5.zip");
        return await AnalyzeArchiveAsync(filePath);
    }

    [Benchmark(Description = "Import 1000 Small Files")]
    public async Task<AnalysisResult> Import_1000Files()
    {
        var filePath = Path.Combine(_testDataPath, "test_1000_files.zip");
        return await AnalyzeArchiveAsync(filePath);
    }

    /// <summary>
    /// 执行归档分析（使用真实服务）
    /// </summary>
    private async Task<AnalysisResult> AnalyzeArchiveAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Test file not found: {filePath}");
        }

        using var scope = _serviceProvider.CreateScope();

        var inspector = scope.ServiceProvider.GetRequiredService<IArchiveInspector>();
        var detector = scope.ServiceProvider.GetRequiredService<IComponentDetector>();
        var merger = scope.ServiceProvider.GetRequiredService<IModMerger>();

        var startTime = DateTime.UtcNow;

        // Stage 1: 分析归档结构
        var archiveTree = await inspector.InspectAsync(filePath, maxDepth: 5, CancellationToken.None);
        var analysisDuration = DateTime.UtcNow - startTime;

        // Stage 2: 检测组件
        var detectionStart = DateTime.UtcNow;
        var candidates = await detector.ExtractCandidatesAsync(archiveTree, CancellationToken.None);
        var detectionDuration = DateTime.UtcNow - detectionStart;

        // Stage 3: 合并组件为 LogicalMod
        var buildStart = DateTime.UtcNow;
        var mergeResult = await merger.MergeAsync(candidates, CancellationToken.None);
        var buildDuration = DateTime.UtcNow - buildStart;

        var totalDuration = DateTime.UtcNow - startTime;

        return new AnalysisResult
        {
            FilePath = filePath,
            FileSize = new FileInfo(filePath).Length,
            TotalDuration = totalDuration,
            AnalysisDuration = analysisDuration,
            DetectionDuration = detectionDuration,
            BuildDuration = buildDuration,
            ComponentCount = candidates.Count,
            Success = true
        };
    }
}

/// <summary>
/// 分析结果
/// </summary>
public class AnalysisResult
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public TimeSpan TotalDuration { get; set; }
    public TimeSpan AnalysisDuration { get; set; }
    public TimeSpan DetectionDuration { get; set; }
    public TimeSpan BuildDuration { get; set; }
    public int ComponentCount { get; set; }
    public bool Success { get; set; }
}
