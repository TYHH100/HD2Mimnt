using System.Diagnostics;

namespace HD2Mimnt.PerformanceTests;

/// <summary>
/// Performance measurement utilities
/// </summary>
public class PerformanceMeasurement
{
    public string Operation { get; set; } = string.Empty;
    public int DataScale { get; set; }
    public long ElapsedMilliseconds { get; set; }
    public int ResultCount { get; set; }
    public long MemoryBefore { get; set; }
    public long MemoryAfter { get; set; }
    public long MemoryDelta => MemoryAfter - MemoryBefore;
    public DateTime Timestamp { get; set; }

    public override string ToString()
    {
        return $"{Operation,-30} | Scale: {DataScale,6} | Time: {ElapsedMilliseconds,6}ms | " +
               $"Results: {ResultCount,6} | Memory Δ: {MemoryDelta / 1024,8}KB";
    }
}

public class PerformanceTimer : IDisposable
{
    private readonly Stopwatch _stopwatch;
    private readonly PerformanceMeasurement _measurement;
    private readonly Action<PerformanceMeasurement> _onComplete;

    public PerformanceTimer(string operation, int dataScale, Action<PerformanceMeasurement> onComplete)
    {
        _measurement = new PerformanceMeasurement
        {
            Operation = operation,
            DataScale = dataScale,
            Timestamp = DateTime.UtcNow,
            MemoryBefore = GC.GetTotalMemory(false)
        };
        _onComplete = onComplete;
        _stopwatch = Stopwatch.StartNew();
    }

    public void SetResultCount(int count)
    {
        _measurement.ResultCount = count;
    }

    public void Dispose()
    {
        _stopwatch.Stop();
        _measurement.ElapsedMilliseconds = _stopwatch.ElapsedMilliseconds;
        _measurement.MemoryAfter = GC.GetTotalMemory(false);
        _onComplete(_measurement);
    }
}

public class PerformanceReport
{
    public List<PerformanceMeasurement> Measurements { get; } = new();
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public string Environment { get; set; } = string.Empty;

    public void Add(PerformanceMeasurement measurement)
    {
        Measurements.Add(measurement);
    }

    public void PrintSummary()
    {
        Console.WriteLine("\n=== Performance Test Summary ===");
        Console.WriteLine($"Started: {StartTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Completed: {EndTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"Duration: {(EndTime - StartTime).TotalSeconds:F2} seconds");
        Console.WriteLine($"Total Measurements: {Measurements.Count}");
        Console.WriteLine();

        foreach (var m in Measurements)
        {
            Console.WriteLine(m.ToString());
        }
    }

    public string GenerateMarkdownReport()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Performance Test Results");
        sb.AppendLine();
        sb.AppendLine($"**Test Date**: {StartTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"**Duration**: {(EndTime - StartTime).TotalSeconds:F2} seconds");
        sb.AppendLine($"**Environment**: {Environment}");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Operation | Scale | Time (ms) | Results | Memory Δ (KB) |");
        sb.AppendLine("|-----------|------:|----------:|--------:|--------------:|");

        foreach (var m in Measurements)
        {
            sb.AppendLine($"| {m.Operation} | {m.DataScale} | {m.ElapsedMilliseconds} | {m.ResultCount} | {m.MemoryDelta / 1024} |");
        }

        return sb.ToString();
    }
}
