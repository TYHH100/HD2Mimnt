using BenchmarkDotNet.Running;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// 性能测试程序入口
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.WriteLine("HD2Mimnt Performance Tests");
        Console.WriteLine("==========================\n");

        // 检查命令行参数
        if (args.Length > 0)
        {
            var command = args[0].ToLowerInvariant();

            if (command == "generate" || command == "generate-all")
            {
                return await GenerateTestFilesAsync(args);
            }
        }

        // 运行基准测试
        Console.WriteLine("Running BenchmarkDotNet tests...\n");

        var summary = BenchmarkRunner.Run<ArchiveImportBenchmarks>();

        Console.WriteLine("\nBenchmark completed!");
        Console.WriteLine($"Results: {summary.ResultsDirectoryPath}");

        return 0;
    }

    /// <summary>
    /// 生成测试文件
    /// </summary>
    private static async Task<int> GenerateTestFilesAsync(string[] args)
    {
        Console.WriteLine("Generating test files...\n");

        var outputDir = args.Length > 1 && args[1] == "--output"
            ? args[2]
            : Path.Combine(Directory.GetCurrentDirectory(), "TestData", "Generated");

        Console.WriteLine($"Output directory: {outputDir}\n");

        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        try
        {
            await TestFileGenerator.GenerateAllTestFilesAsync(outputDir);

            Console.WriteLine("\n✓ All test files generated successfully!");

            // 显示生成的文件列表
            Console.WriteLine("\nGenerated files:");
            var files = Directory.GetFiles(outputDir, "*.zip");
            foreach (var file in files.OrderBy(f => f))
            {
                var info = new FileInfo(file);
                var sizeMB = info.Length / (1024.0 * 1024.0);
                Console.WriteLine($"  - {Path.GetFileName(file),-30} {sizeMB,10:F2} MB");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n✗ Error generating test files: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
