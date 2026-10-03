# HD2IR Phase 6.1 - 实际性能测试详细计划

**计划日期：** 2026-10-03  
**计划人员：** Claude (Opus 5)  
**优先级：** 高  
**预估时间：** 4-6 小时  
**状态：** 📋 准备中

---

## 一、测试目标

### 1.1 核心目标

验证 Phase 5 的理论性能预测是否符合实际：

| 场景 | 理论预测 | 目标 | 验证方式 |
|-----|---------|------|---------|
| 100MB ZIP | ~3-5s | < 5s | 实际测试 |
| 500MB ZIP | ~15-20s | < 20s | 实际测试 |
| 1GB ZIP | ~30-60s | < 60s | 实际测试 |
| 内存峰值 | ~500MB | < 1GB | 内存监控 |
| CPU 峰值 | ~50-80% | < 80% | CPU 监控 |

### 1.2 次要目标

1. **识别性能瓶颈**
   - 哪个阶段最耗时？
   - I/O 还是 CPU 密集？
   - 内存分配模式如何？

2. **建立性能基线**
   - 为未来优化提供参考
   - 监控性能回归

3. **验证优化机制**
   - 延迟解压是否有效？
   - 快速指纹是否准确？
   - 差异驱动是否高效？

---

## 二、测试环境

### 2.1 硬件环境

**最低配置（开发机）：**
```text
CPU: Intel Core i5 或同等性能
RAM: 16GB
Disk: SSD
OS: Windows 10/11
```

**推荐配置：**
```text
CPU: Intel Core i7 或更高
RAM: 32GB
Disk: NVMe SSD
OS: Windows 11
```

### 2.2 软件环境

**运行时：**
- .NET 10
- Windows 10/11

**测试框架：**
- BenchmarkDotNet 0.13.x
- xUnit 2.x

**分析工具：**
- dotMemory (可选)
- dotTrace (可选)
- PerfView (可选)
- Windows Performance Recorder (可选)

---

## 三、测试文件准备

### 3.1 测试文件生成器

**实现位置：** `tests/HD2Mimnt.PerformanceTests/TestFileGenerator.cs`

**核心功能：**

```csharp
public class TestFileGenerator
{
    /// <summary>
    /// 生成指定大小的 ZIP 文件
    /// </summary>
    public static async Task GenerateTestZipAsync(
        string outputPath, 
        long sizeInMB,
        TestZipStructure structure = TestZipStructure.Flat)
    {
        // 生成指定大小的随机文件
        // 打包成 ZIP
    }
    
    /// <summary>
    /// 生成嵌套 ZIP 结构
    /// </summary>
    public static async Task GenerateNestedZipAsync(
        string outputPath,
        int depth,
        long sizePerLayerInMB = 10)
    {
        // 递归生成嵌套结构
    }
    
    /// <summary>
    /// 生成大量小文件的 ZIP
    /// </summary>
    public static async Task GenerateManySmallFilesZipAsync(
        string outputPath,
        int fileCount,
        long avgFileSizeInKB = 50)
    {
        // 生成大量小文件
    }
    
    /// <summary>
    /// 生成真实场景的测试文件
    /// </summary>
    public static async Task GenerateRealisticHD2IRZipAsync(
        string outputPath,
        RealisticScenario scenario)
    {
        // 模拟真实的 HD2IR 结构
        // 包含 .model, .dds, hd2ir.json 等
    }
}
```

### 3.2 测试文件列表

| 文件名 | 大小 | 结构 | 文件数 | 嵌套层级 | 用途 |
|-------|------|------|--------|---------|------|
| test_100mb_flat.zip | 100MB | 单层 | 50 | 1 | 基准测试 |
| test_500mb_flat.zip | 500MB | 单层 | 100 | 1 | 中型文件 |
| test_1gb_flat.zip | 1GB | 单层 | 200 | 1 | 大型文件 |
| test_nested_2.zip | 100MB | 嵌套 | 50 | 2 | 嵌套测试 |
| test_nested_5.zip | 100MB | 嵌套 | 50 | 5 | 深层嵌套 |
| test_1000_files.zip | 50MB | 单层 | 1000 | 1 | 大量小文件 |
| test_realistic_hd2ir.zip | 200MB | 混合 | 100 | 3 | 真实场景 |
| test_large_model.zip | 500MB | 单层 | 5 | 1 | 大文件场景 |

### 3.3 测试数据目录结构

```text
tests/
└── HD2Mimnt.PerformanceTests/
    ├── TestData/
    │   ├── Generated/           (生成的测试文件)
    │   │   ├── test_100mb_flat.zip
    │   │   ├── test_500mb_flat.zip
    │   │   └── ...
    │   └── Samples/            (真实样本，可选)
    │       └── real_sample.zip
    ├── Results/                (测试结果)
    │   ├── BenchmarkDotNet.Artifacts/
    │   └── Reports/
    └── TestFileGenerator.cs
```

### 3.4 生成脚本

**PowerShell 脚本：** `generate-test-files.ps1`

```powershell
# 生成所有测试文件
dotnet run --project tests/HD2Mimnt.PerformanceTests `
    -- generate-all `
    --output "tests/HD2Mimnt.PerformanceTests/TestData/Generated"
```

---

## 四、基准测试设计

### 4.1 测试套件结构

**位置：** `tests/HD2Mimnt.PerformanceTests/ArchiveImportBenchmarks.cs`

```csharp
[MemoryDiagnoser]
[ThreadingDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, baseline: true)]
public class ArchiveImportBenchmarks
{
    private ArchiveImportService _importService;
    private string _testDataPath;
    
    [GlobalSetup]
    public void Setup()
    {
        // 初始化服务
        // 准备测试环境
    }
    
    [Benchmark(Description = "Import 100MB ZIP")]
    [Arguments("test_100mb_flat.zip")]
    public async Task<ImportResult> Import_100MB()
    {
        var filePath = Path.Combine(_testDataPath, "test_100mb_flat.zip");
        return await _importService.ImportAsync(filePath);
    }
    
    [Benchmark(Description = "Import 500MB ZIP")]
    [Arguments("test_500mb_flat.zip")]
    public async Task<ImportResult> Import_500MB()
    {
        var filePath = Path.Combine(_testDataPath, "test_500mb_flat.zip");
        return await _importService.ImportAsync(filePath);
    }
    
    [Benchmark(Description = "Import 1GB ZIP")]
    [Arguments("test_1gb_flat.zip")]
    public async Task<ImportResult> Import_1GB()
    {
        var filePath = Path.Combine(_testDataPath, "test_1gb_flat.zip");
        return await _importService.ImportAsync(filePath);
    }
    
    [Benchmark(Description = "Import Nested 5 Levels")]
    public async Task<ImportResult> Import_Nested5()
    {
        var filePath = Path.Combine(_testDataPath, "test_nested_5.zip");
        return await _importService.ImportAsync(filePath);
    }
    
    [Benchmark(Description = "Import 1000 Small Files")]
    public async Task<ImportResult> Import_1000Files()
    {
        var filePath = Path.Combine(_testDataPath, "test_1000_files.zip");
        return await _importService.ImportAsync(filePath);
    }
    
    [GlobalCleanup]
    public void Cleanup()
    {
        // 清理测试环境
    }
}
```

### 4.2 分阶段性能测试

**位置：** `tests/HD2Mimnt.PerformanceTests/ArchiveStagesBenchmarks.cs`

```csharp
[MemoryDiagnoser]
public class ArchiveStagesBenchmarks
{
    [Benchmark(Description = "Stage 1: Analyze Archive")]
    public async Task<ArchiveTree> AnalyzeArchive()
    {
        // 仅测试归档分析阶段
        return await _analysisEngine.AnalyzeAsync(testFilePath);
    }
    
    [Benchmark(Description = "Stage 2: Detect Components")]
    public async Task<DetectionResult> DetectComponents()
    {
        // 仅测试组件检测阶段
        return await _detector.DetectAsync(archiveTree);
    }
    
    [Benchmark(Description = "Stage 3: Build LogicalMod")]
    public async Task<LogicalMod> BuildLogicalMod()
    {
        // 仅测试 LogicalMod 构建阶段
        return await _builder.BuildAsync(detectionResult);
    }
    
    [Benchmark(Description = "Stage 4: Calculate Diff")]
    public async Task<DeploymentDiff> CalculateDiff()
    {
        // 仅测试 Diff 计算阶段
        return await _diffEngine.CalculateDiffAsync(desired, sessionId);
    }
    
    [Benchmark(Description = "Stage 5: Deploy Files")]
    public async Task DeployFiles()
    {
        // 仅测试文件部署阶段
        await _deploymentService.DeployAsync(logicalMod, method);
    }
}
```

### 4.3 优化机制验证测试

**位置：** `tests/HD2Mimnt.PerformanceTests/OptimizationBenchmarks.cs`

```csharp
[MemoryDiagnoser]
public class OptimizationBenchmarks
{
    [Benchmark(Description = "Fast Fingerprint vs Full Hash")]
    public async Task FastFingerprintComparison()
    {
        // 对比快速指纹和完整哈希的性能
        var fastFingerprint = await _extractionService.CalculateFastFingerprintAsync(filePath);
        var fullHash = await CalculateFullHashAsync(filePath);
    }
    
    [Benchmark(Description = "Diff-Driven vs Full Deployment")]
    public async Task DiffDrivenComparison()
    {
        // 对比差异驱动和完整部署的性能
        // Empty switch: 应该接近 0ms
        // Full switch: 应该接近传统部署
    }
    
    [Benchmark(Description = "With Cache vs Without Cache")]
    public async Task CacheEffectivenessComparison()
    {
        // 对比有缓存和无缓存的性能
        // 第一次：完整分析
        // 第二次：从缓存读取
    }
}
```

### 4.4 并发性能测试

**位置：** `tests/HD2Mimnt.PerformanceTests/ConcurrencyBenchmarks.cs`

```csharp
[MemoryDiagnoser]
public class ConcurrencyBenchmarks
{
    [Benchmark(Description = "Sequential Import (3 files)")]
    public async Task SequentialImport()
    {
        foreach (var file in testFiles)
        {
            await _importService.ImportAsync(file);
        }
    }
    
    [Benchmark(Description = "Parallel Import (3 files)")]
    public async Task ParallelImport()
    {
        var tasks = testFiles.Select(f => _importService.ImportAsync(f));
        await Task.WhenAll(tasks);
    }
}
```

---

## 五、性能指标收集

### 5.1 BenchmarkDotNet 内置指标

**自动收集：**
- Mean (平均时间)
- Error (误差)
- StdDev (标准差)
- Median (中位数)
- Min/Max (最小/最大)
- Allocated Memory (分配内存)
- Gen0/Gen1/Gen2 (GC 次数)

### 5.2 自定义指标收集

**实现位置：** `tests/HD2Mimnt.PerformanceTests/PerformanceMetrics.cs`

```csharp
public class PerformanceMetrics
{
    public TimeSpan TotalDuration { get; set; }
    
    // 分阶段耗时
    public TimeSpan AnalysisDuration { get; set; }
    public TimeSpan DetectionDuration { get; set; }
    public TimeSpan BuildDuration { get; set; }
    public TimeSpan DiffDuration { get; set; }
    public TimeSpan DeploymentDuration { get; set; }
    
    // 资源使用
    public long PeakMemoryUsage { get; set; }
    public double PeakCpuUsage { get; set; }
    public long DiskReadBytes { get; set; }
    public long DiskWriteBytes { get; set; }
    
    // 业务指标
    public int FilesProcessed { get; set; }
    public int ComponentsDetected { get; set; }
    public int FilesDeployed { get; set; }
    public int CacheHits { get; set; }
    public int CacheMisses { get; set; }
}
```

### 5.3 性能监控工具

**集成 PerformanceCounter（Windows）：**

```csharp
public class PerformanceMonitor : IDisposable
{
    private PerformanceCounter _cpuCounter;
    private PerformanceCounter _ramCounter;
    
    public PerformanceMonitor()
    {
        _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
    }
    
    public double GetCpuUsage() => _cpuCounter.NextValue();
    public long GetAvailableMemory() => (long)_ramCounter.NextValue() * 1024 * 1024;
}
```

---

## 六、测试执行计划

### 6.1 执行步骤

**Step 1: 准备测试环境**
```bash
# 1. 创建测试项目
dotnet new xunit -n HD2Mimnt.PerformanceTests -o tests/HD2Mimnt.PerformanceTests

# 2. 添加 BenchmarkDotNet
dotnet add tests/HD2Mimnt.PerformanceTests package BenchmarkDotNet

# 3. 添加项目引用
dotnet add tests/HD2Mimnt.PerformanceTests reference src/HD2Mimnt.Application
dotnet add tests/HD2Mimnt.PerformanceTests reference src/HD2Mimnt.Infrastructure
```

**Step 2: 生成测试文件**
```bash
# 运行测试文件生成器
dotnet run --project tests/HD2Mimnt.PerformanceTests -- generate-all
```

**Step 3: 运行基准测试**
```bash
# 运行所有基准测试
dotnet run --project tests/HD2Mimnt.PerformanceTests -c Release -- --filter *

# 运行特定测试
dotnet run --project tests/HD2Mimnt.PerformanceTests -c Release -- --filter *Import_100MB*
```

**Step 4: 生成报告**
```bash
# BenchmarkDotNet 自动生成报告到 BenchmarkDotNet.Artifacts/results/
```

### 6.2 测试运行配置

**BenchmarkDotNet 配置：**

```csharp
[Config(typeof(Config))]
public class ArchiveImportBenchmarks
{
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
            
            AddJob(Job.Default
                .WithWarmupCount(3)      // 预热 3 次
                .WithIterationCount(5)   // 迭代 5 次
                .WithLaunchCount(1));    // 启动 1 次
        }
    }
}
```

### 6.3 测试时间估算

| 测试阶段 | 预估时间 | 说明 |
|---------|---------|------|
| 准备环境 | 30 分钟 | 创建项目、安装依赖 |
| 实现生成器 | 60 分钟 | 测试文件生成器 |
| 生成测试文件 | 30 分钟 | 生成所有测试数据 |
| 实现基准测试 | 90 分钟 | 所有测试套件 |
| 运行测试 | 60 分钟 | 执行所有基准测试 |
| 分析结果 | 60 分钟 | 生成报告、分析瓶颈 |
| **总计** | **5.5 小时** | |

---

## 七、性能报告生成

### 7.1 报告结构

**文档：** `docs/HD2IR_PHASE6.1_PERFORMANCE_RESULTS.md`

**章节：**
1. 测试概述
2. 测试环境
3. 测试结果
   - 基准测试结果
   - 分阶段性能
   - 优化机制验证
   - 并发性能
4. 性能对比
   - 理论预测 vs 实际测试
   - 优化前 vs 优化后
5. 瓶颈分析
6. 优化建议
7. 结论

### 7.2 结果可视化

**图表类型：**
1. 柱状图：各场景耗时对比
2. 折线图：文件大小 vs 处理时间
3. 饼图：各阶段耗时占比
4. 面积图：内存使用趋势

**工具：**
- BenchmarkDotNet 内置图表
- Excel / Google Sheets
- Python matplotlib (可选)

### 7.3 验收标准

**成功标准：**
- ✅ 所有测试通过目标耗时
- ✅ 内存峰值在目标范围内
- ✅ CPU 使用率在目标范围内
- ✅ 无内存泄漏
- ✅ 无性能回归

**如果不满足标准：**
1. 详细分析瓶颈
2. 提出优化方案
3. 实施优化
4. 重新测试
5. 更新文档

---

## 八、潜在优化方向

### 8.1 如果性能不达标

**可能的优化方向：**

1. **I/O 优化**
   - 使用内存映射文件
   - 增大缓冲区
   - 异步 I/O
   - 批量读取

2. **CPU 优化**
   - 并行处理
   - 算法优化
   - 减少重复计算
   - 使用 SIMD

3. **内存优化**
   - 对象池
   - ArrayPool
   - Span<T> / Memory<T>
   - 减少分配

4. **架构优化**
   - 流式处理
   - 延迟计算
   - 缓存策略
   - 分片处理

### 8.2 优化优先级

**高优先级（必须优化）：**
- 严重超出目标耗时（>2x）
- 内存泄漏
- 死锁或阻塞

**中优先级（建议优化）：**
- 轻微超出目标耗时（1.2x-2x）
- 内存峰值接近目标
- CPU 使用率偏高

**低优先级（可选优化）：**
- 满足目标但有优化空间
- 边界情况性能

---

## 九、交付物清单

### 9.1 代码

- [ ] `tests/HD2Mimnt.PerformanceTests/` 项目
- [ ] `TestFileGenerator.cs` - 测试文件生成器
- [ ] `ArchiveImportBenchmarks.cs` - 基准测试
- [ ] `ArchiveStagesBenchmarks.cs` - 分阶段测试
- [ ] `OptimizationBenchmarks.cs` - 优化验证测试
- [ ] `ConcurrencyBenchmarks.cs` - 并发测试
- [ ] `PerformanceMetrics.cs` - 指标收集
- [ ] `PerformanceMonitor.cs` - 性能监控

### 9.2 测试数据

- [ ] `test_100mb_flat.zip`
- [ ] `test_500mb_flat.zip`
- [ ] `test_1gb_flat.zip`
- [ ] `test_nested_2.zip`
- [ ] `test_nested_5.zip`
- [ ] `test_1000_files.zip`
- [ ] `test_realistic_hd2ir.zip`

### 9.3 文档

- [ ] `HD2IR_PHASE6.1_PERFORMANCE_RESULTS.md` - 测试结果报告
- [ ] 性能图表
- [ ] 瓶颈分析
- [ ] 优化建议（如需要）

---

## 十、下一步

### 10.1 Phase 6.1 完成后

**如果性能达标：**
- ✅ 验证通过，Phase 6.1 完成
- → 继续 Phase 6.2 (UI 层集成)

**如果性能不达标：**
- 分析瓶颈
- 实施优化
- 重新测试
- 更新文档
- → 继续 Phase 6.2

### 10.2 Phase 6.2 准备

**前置条件：**
- Phase 6.1 完成
- 性能基线建立
- 瓶颈已识别（如有）

**启动条件：**
- 用户确认继续
- 准备 UI 开发环境

---

**计划人员：** Claude (Opus 5)  
**计划日期：** 2026-10-03  
**计划版本：** 1.0  
**状态：** 📋 准备中，等待启动 Phase 6.1
