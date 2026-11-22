using System.Diagnostics;
using CpuSchedulingProbe.Native;

namespace CpuSchedulingProbe;

internal static class Spinner
{
    public static int Run(string[] args)
    {
        var seconds = Args.Int(args, "--seconds", 20);
        var threadCount = Args.Int(args, "--threads", 4);
        var interval = Args.Int(args, "--interval", 20);
        var addThreadAfterMs = Args.Int(args, "--add-thread-after", -1);

        var pidFile = Args.Option(args, "--pid-file");
        if (pidFile is not null)
            File.WriteAllText(pidFile, Environment.ProcessId.ToString());

        using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        var writeGate = new object();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var workers = new List<Thread>();
        var nextIndex = 0;

        void StartWorker()
        {
            var index = nextIndex++;
            var worker = new Thread(() => Work(index)) { IsBackground = true, Name = $"spinner-{index}" };
            workers.Add(worker);
            worker.Start();
        }

        void Work(int index)
        {
            while (!stop.IsCancellationRequested)
            {
                var spinUntil = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 0.002);
                while (Stopwatch.GetTimestamp() < spinUntil && !stop.IsCancellationRequested)
                    Thread.SpinWait(50);

                ProcessApi.GetCurrentProcessorNumberEx(out var processor);
                lock (writeGate)
                {
                    writer.WriteLine(
                        $"S,{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{index},{processor.Group},{processor.Number}");
                }

                if (stop.IsCancellationRequested)
                    break;
                Thread.Sleep(interval);
            }
        }

        for (var i = 0; i < threadCount; i++)
            StartWorker();

        var started = Stopwatch.StartNew();
        var added = addThreadAfterMs < 0;
        while (!stop.IsCancellationRequested)
        {
            if (!added && started.ElapsedMilliseconds >= addThreadAfterMs)
            {
                StartWorker();
                added = true;
            }

            Thread.Sleep(10);
        }

        foreach (var worker in workers)
            worker.Join(2000);

        lock (writeGate)
        {
            writer.WriteLine($"E,{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},{workers.Count}");
        }

        return 0;
    }
}

internal static class Args
{
    public static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return i + 1 < args.Length ? args[i + 1] : null;
            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return args[i][(name.Length + 1)..];
        }

        return null;
    }

    public static int Int(string[] args, string name, int fallback)
        => int.TryParse(Option(args, name), out var value) ? value : fallback;

    public static int RequiredInt(string[] args, string name)
        => int.TryParse(Option(args, name), out var value)
            ? value
            : throw new ArgumentException($"Missing or invalid {name}.");

    public static uint[]? CpuSetList(string[] args, string name)
    {
        var raw = Option(args, name);
        if (raw is null)
            return null;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => uint.TryParse(part, out var id) ? id : throw new ArgumentException($"Invalid CPU set id: {part}"))
            .ToArray();
    }

    public static ulong? HexMask(string[] args, string name)
    {
        var raw = Option(args, name);
        if (raw is null)
            return null;

        var text = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value)
            ? value
            : throw new ArgumentException($"Invalid hex mask: {raw}");
    }

    public static bool Flag(string[] args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}