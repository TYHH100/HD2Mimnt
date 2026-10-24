using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace HD2Mimnt.UI.Services;

/// <summary>
/// 可选的进程诊断采样器。只有明确设置输出路径时才启用。
/// </summary>
public sealed class RuntimeMetricsWriter : IDisposable
{
    private readonly string _path;
    private readonly Timer _timer;
    private readonly object _gate = new();
    private bool _disposed;

    private RuntimeMetricsWriter(string path, TimeSpan interval)
    {
        _path = path;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _timer = new Timer(_ => WriteSample(), null, TimeSpan.Zero, interval);
    }

    public static RuntimeMetricsWriter? StartFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("HD2MIMNT_METRICS_PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        var intervalText = Environment.GetEnvironmentVariable("HD2MIMNT_METRICS_INTERVAL_SECONDS");
        var interval = double.TryParse(intervalText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(Math.Max(1, seconds))
            : TimeSpan.FromSeconds(5);
        return new RuntimeMetricsWriter(Path.GetFullPath(path), interval);
    }

    private void WriteSample()
    {
        if (_disposed) return;

        try
        {
            using var process = Process.GetCurrentProcess();
            var sample = new
            {
                TimestampUtc = DateTime.UtcNow,
                WorkingSetBytes = process.WorkingSet64,
                PrivateBytes = process.PrivateMemorySize64,
                ManagedHeapBytes = GC.GetTotalMemory(false),
                CpuSeconds = process.TotalProcessorTime.TotalSeconds
            };
            var json = JsonSerializer.Serialize(sample) + Environment.NewLine;
            lock (_gate)
            {
                if (!_disposed) File.AppendAllText(_path, json);
            }
        }
        catch
        {
            // 诊断采样失败不应影响主应用。
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
    }
}
