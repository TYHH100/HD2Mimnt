using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Columns;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// 归档导入性能基准测试
/// </summary>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class ArchiveImportBenchmarks
{
    private string _testDataPath = null!;

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
        _testDataPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            "TestData",
            "Generated"
        );

        Console.WriteLine($"Test data path: {_testDataPath}");

        // 验证测试文件存在
        if (!Directory.Exists(_testDataPath))
        {
            throw new DirectoryNotFoundException(
                $"Test data directory not found: {_testDataPath}\n" +
                "Please run TestFileGenerator first to generate test files.");
        }
    }

    [Benchmark(Description = "Import 100MB ZIP")]
    public async Task<TestResult> Import_100MB()
    {
        var filePath = Path.Combine(_testDataPath, "test_100mb_flat.zip");
        return await SimulateImportAsync(filePath);
    }

    [Benchmark(Description = "Import 500MB ZIP")]
    public async Task<TestResult> Import_500MB()
    {
        var filePath = Path.Combine(_testDataPath, "test_500mb_flat.zip");
        return await SimulateImportAsync(filePath);
    }

    [Benchmark(Description = "Import 1GB ZIP")]
    public async Task<TestResult> Import_1GB()
    {
        var filePath = Path.Combine(_testDataPath, "test_1gb_flat.zip");
        return await SimulateImportAsync(filePath);
    }

    [Benchmark(Description = "Import Nested 2 Levels")]
    public async Task<TestResult> Import_Nested2()
    {
        var filePath = Path.Combine(_testDataPath, "test_nested_2.zip");
        return await SimulateImportAsync(filePath);
    }

    [Benchmark(Description = "Import Nested 5 Levels")]
    public async Task<TestResult> Import_Nested5()
    {
        var filePath = Path.Combine(_testDataPath, "test_nested_5.zip");
        return await SimulateImportAsync(filePath);
    }

    [Benchmark(Description = "Import 1000 Small Files")]
    public async Task<TestResult> Import_1000Files()
    {
        var filePath = Path.Combine(_testDataPath, "test_1000_files.zip");
        return await SimulateImportAsync(filePath);
    }

    /// <summary>
    /// 模拟导入过程（实际测试中会使用真实服务）
    /// </summary>
    private async Task<TestResult> SimulateImportAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Test file not found: {filePath}");
        }

        var startTime = DateTime.UtcNow;

        // TODO: 这里会调用实际的导入服务
        // var result = await _archiveImportService.ImportAsync(filePath);

        // 目前模拟基本的 ZIP 读取
        using var archive = System.IO.Compression.ZipFile.OpenRead(filePath);
        var entryCount = archive.Entries.Count;

        // 模拟分析每个 entry
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var size = entry.Length;
            // 模拟读取 metadata
            await Task.Yield();
        }

        var duration = DateTime.UtcNow - startTime;

        return new TestResult
        {
            FilePath = filePath,
            FileSize = new FileInfo(filePath).Length,
            EntryCount = entryCount,
            Duration = duration,
            Success = true
        };
    }
}

/// <summary>
/// 测试结果
/// </summary>
public class TestResult
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int EntryCount { get; set; }
    public TimeSpan Duration { get; set; }
    public bool Success { get; set; }
}
