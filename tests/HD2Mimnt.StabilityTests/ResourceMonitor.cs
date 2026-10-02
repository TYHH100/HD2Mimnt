using System.Diagnostics;

namespace HD2Mimnt.StabilityTests;

/// <summary>
/// 资源监控器 - 监控系统资源使用情况
/// </summary>
public class ResourceMonitor : IDisposable
{
    private readonly Process _process;
    private readonly List<ResourceSnapshot> _snapshots = new();
    private readonly Timer? _timer;
    private bool _disposed;

    public ResourceMonitor(int intervalSeconds = 60)
    {
        _process = Process.GetCurrentProcess();

        // 定期采样
        _timer = new Timer(_ => TakeSnapshot(), null, TimeSpan.Zero, TimeSpan.FromSeconds(intervalSeconds));
    }

    public void TakeSnapshot()
    {
        _process.Refresh();

        var snapshot = new ResourceSnapshot
        {
            Timestamp = DateTime.UtcNow,
            WorkingSet = _process.WorkingSet64,
            PrivateMemorySize = _process.PrivateMemorySize64,
            GCTotalMemory = GC.GetTotalMemory(false),
            HandleCount = _process.HandleCount,
            ThreadCount = _process.Threads.Count,
            Gen0Collections = GC.CollectionCount(0),
            Gen1Collections = GC.CollectionCount(1),
            Gen2Collections = GC.CollectionCount(2)
        };

        _snapshots.Add(snapshot);
    }

    public ResourceReport GenerateReport(TimeSpan duration)
    {
        if (_snapshots.Count < 2)
        {
            return new ResourceReport
            {
                Duration = duration,
                IsHealthy = false,
                Message = "Insufficient data for analysis"
            };
        }

        var first = _snapshots.First();
        var last = _snapshots.Last();

        var memoryGrowth = last.WorkingSet - first.WorkingSet;
        var memoryGrowthPercent = (double)memoryGrowth / first.WorkingSet * 100;
        var memoryGrowthPerHour = memoryGrowth / duration.TotalHours;

        var handleGrowth = last.HandleCount - first.HandleCount;
        var threadGrowth = last.ThreadCount - first.ThreadCount;

        var report = new ResourceReport
        {
            Duration = duration,
            InitialWorkingSet = first.WorkingSet,
            FinalWorkingSet = last.WorkingSet,
            MemoryGrowth = memoryGrowth,
            MemoryGrowthPercent = memoryGrowthPercent,
            MemoryGrowthPerHour = memoryGrowthPerHour,
            InitialHandleCount = first.HandleCount,
            FinalHandleCount = last.HandleCount,
            HandleGrowth = handleGrowth,
            InitialThreadCount = first.ThreadCount,
            FinalThreadCount = last.ThreadCount,
            ThreadGrowth = threadGrowth,
            TotalGen0Collections = last.Gen0Collections,
            TotalGen1Collections = last.Gen1Collections,
            TotalGen2Collections = last.Gen2Collections,
            Snapshots = _snapshots.ToList()
        };

        // 健康检查
        report.IsHealthy = true;
        var issues = new List<string>();

        // 内存增长超过 10% per hour 警告
        if (Math.Abs(memoryGrowthPerHour) > first.WorkingSet * 0.1)
        {
            report.IsHealthy = false;
            issues.Add($"Memory growth rate high: {memoryGrowthPerHour / 1024 / 1024:F2} MB/hour");
        }

        // 句柄泄漏检查
        if (handleGrowth > 100)
        {
            report.IsHealthy = false;
            issues.Add($"Potential handle leak: {handleGrowth} handles leaked");
        }

        // 线程泄漏检查
        if (threadGrowth > 10)
        {
            report.IsHealthy = false;
            issues.Add($"Potential thread leak: {threadGrowth} threads leaked");
        }

        report.Message = report.IsHealthy
            ? "No resource leaks detected"
            : string.Join("; ", issues);

        return report;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _timer?.Dispose();
        _disposed = true;

        GC.SuppressFinalize(this);
    }
}

public class ResourceSnapshot
{
    public DateTime Timestamp { get; set; }
    public long WorkingSet { get; set; }
    public long PrivateMemorySize { get; set; }
    public long GCTotalMemory { get; set; }
    public int HandleCount { get; set; }
    public int ThreadCount { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
}

public class ResourceReport
{
    public TimeSpan Duration { get; set; }
    public bool IsHealthy { get; set; }
    public string Message { get; set; } = string.Empty;

    // Memory
    public long InitialWorkingSet { get; set; }
    public long FinalWorkingSet { get; set; }
    public long MemoryGrowth { get; set; }
    public double MemoryGrowthPercent { get; set; }
    public double MemoryGrowthPerHour { get; set; }

    // Handles
    public int InitialHandleCount { get; set; }
    public int FinalHandleCount { get; set; }
    public int HandleGrowth { get; set; }

    // Threads
    public int InitialThreadCount { get; set; }
    public int FinalThreadCount { get; set; }
    public int ThreadGrowth { get; set; }

    // GC
    public int TotalGen0Collections { get; set; }
    public int TotalGen1Collections { get; set; }
    public int TotalGen2Collections { get; set; }

    public List<ResourceSnapshot> Snapshots { get; set; } = new();

    public override string ToString()
    {
        return $@"
=== Resource Report ({Duration.TotalHours:F2} hours) ===
Status: {(IsHealthy ? "✅ HEALTHY" : "❌ ISSUES DETECTED")}
{Message}

Memory:
  Initial: {InitialWorkingSet / 1024 / 1024:F2} MB
  Final: {FinalWorkingSet / 1024 / 1024:F2} MB
  Growth: {MemoryGrowth / 1024 / 1024:F2} MB ({MemoryGrowthPercent:F2}%)
  Growth/Hour: {MemoryGrowthPerHour / 1024 / 1024:F2} MB/hour

Handles:
  Initial: {InitialHandleCount}
  Final: {FinalHandleCount}
  Growth: {HandleGrowth}

Threads:
  Initial: {InitialThreadCount}
  Final: {FinalThreadCount}
  Growth: {ThreadGrowth}

GC Collections:
  Gen0: {TotalGen0Collections}
  Gen1: {TotalGen1Collections}
  Gen2: {TotalGen2Collections}
";
    }
}
