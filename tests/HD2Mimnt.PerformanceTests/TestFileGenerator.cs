using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// 生成性能测试用的测试文件
/// </summary>
public static class TestFileGenerator
{
    /// <summary>
    /// 生成指定大小的 ZIP 文件
    /// </summary>
    public static async Task GenerateTestZipAsync(
        string outputPath,
        long sizeInMB,
        TestZipStructure structure = TestZipStructure.Flat,
        CancellationToken ct = default)
    {
        Console.WriteLine($"Generating {sizeInMB}MB ZIP: {outputPath}...");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 删除已存在的文件
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        var targetBytes = sizeInMB * 1024 * 1024;
        var currentBytes = 0L;
        var fileIndex = 0;

        // 每个文件 2MB
        var fileSizeBytes = 2 * 1024 * 1024;

        while (currentBytes < targetBytes)
        {
            var remainingBytes = targetBytes - currentBytes;
            var thisFileSize = Math.Min(fileSizeBytes, remainingBytes);

            var fileName = structure switch
            {
                TestZipStructure.Flat => $"file_{fileIndex:D4}.dat",
                TestZipStructure.WithFolders => $"folder_{fileIndex / 10}/file_{fileIndex:D4}.dat",
                _ => $"file_{fileIndex:D4}.dat"
            };

            var entry = archive.CreateEntry(fileName, CompressionLevel.Fastest);
            using var entryStream = entry.Open();
            await WriteRandomDataAsync(entryStream, thisFileSize, ct);

            currentBytes += thisFileSize;
            fileIndex++;

            if (fileIndex % 10 == 0)
            {
                Console.WriteLine($"  Progress: {currentBytes / (1024.0 * 1024.0):F1}MB / {sizeInMB}MB");
            }
        }

        Console.WriteLine($"✓ Generated: {outputPath} ({sizeInMB}MB, {fileIndex} files)");
    }

    /// <summary>
    /// 生成嵌套 ZIP 结构
    /// </summary>
    public static async Task GenerateNestedZipAsync(
        string outputPath,
        int depth,
        long sizePerLayerInMB = 10,
        CancellationToken ct = default)
    {
        Console.WriteLine($"Generating nested ZIP (depth={depth}): {outputPath}...");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"hd2mimnt_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 从最内层开始构建
            var currentZip = Path.Combine(tempDir, $"layer_{depth}.zip");
            await GenerateTestZipAsync(currentZip, sizePerLayerInMB, TestZipStructure.Flat, ct);

            // 逐层向外嵌套
            for (int i = depth - 1; i >= 1; i--)
            {
                var nextZip = Path.Combine(tempDir, $"layer_{i}.zip");
                using var archive = ZipFile.Open(nextZip, ZipArchiveMode.Create);

                // 添加一些文件
                for (int j = 0; j < 5; j++)
                {
                    var entry = archive.CreateEntry($"file_{j}.dat", CompressionLevel.Fastest);
                    using var stream = entry.Open();
                    await WriteRandomDataAsync(stream, 1024 * 1024, ct); // 1MB each
                }

                // 嵌套上一层的 ZIP
                archive.CreateEntryFromFile(currentZip, Path.GetFileName(currentZip));

                currentZip = nextZip;
            }

            // 移动到目标位置
            File.Move(currentZip, outputPath, true);
            Console.WriteLine($"✓ Generated nested ZIP: {outputPath} ({depth} layers)");
        }
        finally
        {
            // 清理临时文件
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// 生成大量小文件的 ZIP
    /// </summary>
    public static async Task GenerateManySmallFilesZipAsync(
        string outputPath,
        int fileCount,
        long avgFileSizeInKB = 50,
        CancellationToken ct = default)
    {
        Console.WriteLine($"Generating {fileCount} small files ZIP: {outputPath}...");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        using var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create);

        var fileSizeBytes = avgFileSizeInKB * 1024;

        for (int i = 0; i < fileCount; i++)
        {
            var fileName = $"folder_{i / 100}/file_{i:D5}.dat";
            var entry = archive.CreateEntry(fileName, CompressionLevel.Fastest);

            using var entryStream = entry.Open();
            await WriteRandomDataAsync(entryStream, fileSizeBytes, ct);

            if ((i + 1) % 100 == 0)
            {
                Console.WriteLine($"  Progress: {i + 1} / {fileCount} files");
            }
        }

        Console.WriteLine($"✓ Generated: {outputPath} ({fileCount} files)");
    }

    /// <summary>
    /// 生成真实场景的 HD2IR ZIP
    /// </summary>
    public static async Task GenerateRealisticHD2IRZipAsync(
        string outputPath,
        RealisticScenario scenario,
        CancellationToken ct = default)
    {
        Console.WriteLine($"Generating realistic HD2IR ZIP ({scenario}): {outputPath}...");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"hd2mimnt_realistic_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            // 创建内层 ZIP 文件
            var hd2irZip = Path.Combine(tempDir, "character.hd2irpack.zip");
            var modelZip = Path.Combine(tempDir, "character_model.zip");

            // HD2IR ZIP: hd2ir.json + 一些小文件
            using (var archive = ZipFile.Open(hd2irZip, ZipArchiveMode.Create))
            {
                // hd2ir.json
                var jsonEntry = archive.CreateEntry("hd2ir.json", CompressionLevel.Optimal);
                using (var stream = jsonEntry.Open())
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    await writer.WriteAsync(GenerateSampleHD2IRJson());
                    await writer.FlushAsync();
                } // 确保 stream 关闭

                // 一些配置文件
                for (int i = 0; i < 10; i++)
                {
                    var entry = archive.CreateEntry($"config/setting_{i}.json", CompressionLevel.Optimal);
                    using var stream = entry.Open();
                    await WriteRandomDataAsync(stream, 1024, ct);
                } // 确保所有 entry 都关闭
            }

            // Model ZIP: .model 文件
            using (var archive = ZipFile.Open(modelZip, ZipArchiveMode.Create))
            {
                // 模拟 .model 文件 (100MB)
                var modelEntry = archive.CreateEntry("character_body.model", CompressionLevel.Fastest);
                using (var stream = modelEntry.Open())
                {
                    await WriteRandomDataAsync(stream, 100 * 1024 * 1024, ct);
                } // 确保 stream 关闭

                // 一些 .dds 纹理文件
                for (int i = 0; i < 20; i++)
                {
                    var entry = archive.CreateEntry($"textures/texture_{i}.dds", CompressionLevel.Fastest);
                    using var texStream = entry.Open();
                    await WriteRandomDataAsync(texStream, 5 * 1024 * 1024, ct); // 5MB each
                } // 确保所有 entry 都关闭
            }

            // 创建外层 ZIP
            using (var archive = ZipFile.Open(outputPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(hd2irZip, Path.GetFileName(hd2irZip));
                archive.CreateEntryFromFile(modelZip, Path.GetFileName(modelZip));

                // 添加一个 README
                var readmeEntry = archive.CreateEntry("README.txt", CompressionLevel.Optimal);
                using (var stream = readmeEntry.Open())
                using (var writer = new StreamWriter(stream, Encoding.UTF8))
                {
                    await writer.WriteAsync("Test HD2IR Mod Package\n");
                    await writer.FlushAsync();
                }
            }

            Console.WriteLine($"✓ Generated realistic HD2IR ZIP: {outputPath}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    /// <summary>
    /// 生成所有测试文件
    /// </summary>
    public static async Task GenerateAllTestFilesAsync(string outputDir, CancellationToken ct = default)
    {
        Console.WriteLine("=== Generating All Test Files ===\n");

        var tasks = new[]
        {
            GenerateTestZipAsync(Path.Combine(outputDir, "test_100mb_flat.zip"), 100, TestZipStructure.Flat, ct),
            GenerateTestZipAsync(Path.Combine(outputDir, "test_500mb_flat.zip"), 500, TestZipStructure.Flat, ct),
            GenerateTestZipAsync(Path.Combine(outputDir, "test_1gb_flat.zip"), 1024, TestZipStructure.Flat, ct),
            GenerateNestedZipAsync(Path.Combine(outputDir, "test_nested_2.zip"), 2, 50, ct),
            GenerateNestedZipAsync(Path.Combine(outputDir, "test_nested_5.zip"), 5, 20, ct),
            GenerateManySmallFilesZipAsync(Path.Combine(outputDir, "test_1000_files.zip"), 1000, 50, ct),
            GenerateRealisticHD2IRZipAsync(Path.Combine(outputDir, "test_realistic_hd2ir.zip"),
                RealisticScenario.StandardDualPack, ct),
        };

        await Task.WhenAll(tasks);

        Console.WriteLine("\n=== All Test Files Generated ===");
    }

    /// <summary>
    /// 写入随机数据到流
    /// </summary>
    private static async Task WriteRandomDataAsync(Stream stream, long size, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var written = 0L;

        while (written < size)
        {
            var toWrite = (int)Math.Min(buffer.Length, size - written);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, toWrite));
            await stream.WriteAsync(buffer.AsMemory(0, toWrite), ct);
            written += toWrite;
        }
    }

    /// <summary>
    /// 生成示例 hd2ir.json
    /// </summary>
    private static string GenerateSampleHD2IRJson()
    {
        return """
        {
          "version": "1.0",
          "name": "Test Character",
          "author": "Test Author",
          "description": "Performance test character",
          "components": [
            {
              "type": "model",
              "path": "character_body.model"
            },
            {
              "type": "texture",
              "path": "textures/"
            }
          ]
        }
        """;
    }
}

/// <summary>
/// ZIP 结构类型
/// </summary>
public enum TestZipStructure
{
    Flat,
    WithFolders
}

/// <summary>
/// 真实场景类型
/// </summary>
public enum RealisticScenario
{
    StandardDualPack,
    MultiComponent,
    DeepNested
}
