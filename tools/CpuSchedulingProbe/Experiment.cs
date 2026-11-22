using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CpuSchedulingProbe.Native;

namespace CpuSchedulingProbe;

internal sealed record Sample(long TimestampMs, int ThreadIndex, int Group, int LogicalProcessor);

internal static class Experiment
{
    public static int Run(string[] args)
    {
        var topology = CpuTopology.Load();
        var baselineMs = Args.Int(args, "--baseline-ms", 2500);
        var appliedMs = Args.Int(args, "--applied-ms", 4000);
        var restoredMs = Args.Int(args, "--restored-ms", 2500);
        var threadCount = Args.Int(args, "--threads", 4);
        var intervalMs = Args.Int(args, "--interval", 20);
        var explicitSets = Args.CpuSetList(args, "--sets");
        var addThreadAfterMs = baselineMs + 2000;
        var totalSeconds = (baselineMs + appliedMs + restoredMs) / 1000 + 8;

        var startInfo = CreateSpinnerStartInfo(totalSeconds, threadCount, intervalMs, addThreadAfterMs);
        using var child = Process.Start(startInfo)
                          ?? throw new InvalidOperationException("Could not start the spinner process.");

        var samples = new ConcurrentQueue<Sample>();
        var reader = Task.Run(async () =>
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                var parts = line.Split(',');
                if (parts.Length != 5 || parts[0] != "S")
                    continue;
                if (long.TryParse(parts[1], out var timestamp)
                    && int.TryParse(parts[2], out var threadIndex)
                    && int.TryParse(parts[3], out var group)
                    && int.TryParse(parts[4], out var logicalProcessor))
                {
                    samples.Enqueue(new Sample(timestamp, threadIndex, group, logicalProcessor));
                }
            }
        });

        var spawnMs = NowMs();
        SleepUntil(spawnMs + baselineMs);
        var baselineSnapshot = Filter(samples, spawnMs + 300, NowMs() - 100);
        var allowed = PickAllowed(topology, baselineSnapshot, 4, explicitSets);
        if (allowed.Length == 0)
        {
            Console.Error.WriteLine("No CPU sets available for the experiment.");
            TryKill(child);
            return 1;
        }

        var knownIds = topology.UsableSets.Select(set => set.Id).ToHashSet();
        var unknownIds = allowed.Where(id => !knownIds.Contains(id)).ToArray();
        if (unknownIds.Length > 0)
        {
            Console.Error.WriteLine($"Unknown CPU set IDs: {string.Join(",", unknownIds)}");
            TryKill(child);
            return 1;
        }

        var handle = ProcessApi.OpenProcessHandle(
            ProcessApi.ProcessQueryLimitedInformation | ProcessApi.ProcessSetInformation, child.Id);
        if (handle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"OpenProcess failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
            TryKill(child);
            return 1;
        }

        try
        {
            Console.WriteLine($"CPU: {topology.CpuName}");
            Console.WriteLine($"Topology supported: {topology.Supported} ({topology.Reason})");
            Console.WriteLine($"Spinner PID: {child.Id}, threads: {threadCount}, interval: {intervalMs}ms");
            Console.WriteLine($"Baseline samples: {baselineSnapshot.Count} [{FormatDistribution(baselineSnapshot)}]");
            Console.WriteLine($"Allowed sets: {FormatAllowed(topology, allowed)}");

            var originalSets = CpuSetApi.GetProcessDefaultCpuSets(handle);
            var threadsBefore = QueryThreadSets(child.Id);

            var applyMark = NowMs();
            CpuSetApi.SetProcessDefaultCpuSets(handle, allowed);
            var appliedSets = CpuSetApi.GetProcessDefaultCpuSets(handle);
            var threadsAfter = QueryThreadSets(child.Id);
            Console.WriteLine($"Original default sets: {Commands.FormatSets(originalSets)}");
            Console.WriteLine($"Applied default sets: {Commands.FormatSets(appliedSets)} (verify: {(Commands.SetEquals(appliedSets, allowed) ? "match" : "MISMATCH")})");
            Console.WriteLine($"Thread-selected sets before: {NonEmptyCount(threadsBefore)}/{threadsBefore.Count}");
            Console.WriteLine($"Thread-selected sets after: {NonEmptyCount(threadsAfter)}/{threadsAfter.Count}");

            SleepUntil(applyMark + appliedMs);

            var restoreMark = NowMs();
            CpuSetApi.SetProcessDefaultCpuSets(handle, originalSets);
            var afterRestore = CpuSetApi.GetProcessDefaultCpuSets(handle);
            Console.WriteLine($"Default sets after restore: {Commands.FormatSets(afterRestore)} (verify: {(Commands.SetEquals(afterRestore, originalSets) ? "match" : "MISMATCH")})");

            SleepUntil(restoreMark + restoredMs);
            var endMark = NowMs();
            TryKill(child);
            reader.Wait(2000);

            var baseline = Filter(samples, spawnMs + 300, applyMark - 100);
            var applied = Filter(samples, applyMark + 250, restoreMark - 100);
            var restored = Filter(samples, restoreMark + 300, endMark);
            var allowedSet = allowed.ToHashSet();
            var allowedKeys = topology.UsableSets
                .Where(set => allowedSet.Contains(set.Id))
                .Select(set => (uint)((set.Group << 8) | set.LogicalProcessorIndex))
                .ToHashSet();

            var appliedOutside = applied.Count(sample => !allowedKeys.Contains(sampleKey(sample)));
            var existing = applied.Where(sample => sample.ThreadIndex < threadCount).ToList();
            var newThread = applied.Where(sample => sample.ThreadIndex >= threadCount).ToList();
            var existingOutside = existing.Count(sample => !allowedKeys.Contains(sampleKey(sample)));
            var newThreadOutside = newThread.Count(sample => !allowedKeys.Contains(sampleKey(sample)));
            var restoredOutside = restored.Count(sample => !allowedKeys.Contains(sampleKey(sample)));

            Console.WriteLine();
            Console.WriteLine($"Baseline window: {baseline.Count} samples [{FormatDistribution(baseline)}]");
            Console.WriteLine($"Applied window: {applied.Count} samples [{FormatDistribution(applied)}]");
            Console.WriteLine($"  in allowed: {applied.Count - appliedOutside}, outside: {appliedOutside}");
            Console.WriteLine($"  existing threads: {existing.Count - existingOutside}/{existing.Count} in allowed [{FormatDistribution(existing)}]");
            Console.WriteLine($"  new thread(s): {newThread.Count - newThreadOutside}/{newThread.Count} in allowed [{FormatDistribution(newThread)}]");
            Console.WriteLine($"Restored window: {restored.Count} samples, outside allowed: {restoredOutside} [{FormatDistribution(restored)}]");

            var conclusion = applied.Count == 0
                ? "INCONCLUSIVE (no samples in the applied window)"
                : appliedOutside == 0
                    ? "YES - process default covers existing threads"
                    : "NO - existing threads ran outside the allowed sets";
            Console.WriteLine();
            Console.WriteLine($"Conclusion: {conclusion}");
            if (newThread.Count > 0)
                Console.WriteLine($"New-thread check: {(newThreadOutside == 0 ? "new threads followed the default sets" : "new threads also ran outside")}");
            Console.WriteLine($"Restore check: {(restoredOutside > 0 ? "restore observed" : "no out-of-set samples after restore")}");
            return 0;
        }
        finally
        {
            ProcessApi.Close(handle);
            TryKill(child);
        }
    }

    private static ProcessStartInfo CreateSpinnerStartInfo(
        int totalSeconds, int threadCount, int intervalMs, int addThreadAfterMs)
    {
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable.");
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(assembly))
                throw new InvalidOperationException("Cannot locate the probe assembly.");
            startInfo.ArgumentList.Add(assembly);
        }

        startInfo.ArgumentList.Add("spinner");
        startInfo.ArgumentList.Add("--seconds");
        startInfo.ArgumentList.Add(totalSeconds.ToString());
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(threadCount.ToString());
        startInfo.ArgumentList.Add("--interval");
        startInfo.ArgumentList.Add(intervalMs.ToString());
        startInfo.ArgumentList.Add("--add-thread-after");
        startInfo.ArgumentList.Add(addThreadAfterMs.ToString());
        return startInfo;
    }

    private static List<Sample> Filter(ConcurrentQueue<Sample> samples, long fromMs, long toMs)
        => samples.Where(sample => sample.TimestampMs >= fromMs && sample.TimestampMs <= toMs)
            .OrderBy(sample => sample.TimestampMs)
            .ToList();

    private static uint[] PickAllowed(
        CpuTopology topology, IReadOnlyList<Sample> baseline, int count, uint[]? explicitSets)
    {
        if (explicitSets is { Length: > 0 })
            return explicitSets;
        if (topology.UsableSets.Count == 0)
            return [];

        var usage = baseline
            .GroupBy(sample => (sample.Group, sample.LogicalProcessor))
            .ToDictionary(group => group.Key, group => group.Count());
        var ids = new List<uint>();

        foreach (var core in topology.UsableSets
                     .GroupBy(set => (set.Group, set.CoreIndex))
                     .OrderBy(group => group.Sum(set => usage.GetValueOrDefault((set.Group, set.LogicalProcessorIndex)))))
        {
            if (ids.Count >= count)
                break;
            var set = core
                .OrderBy(item => usage.GetValueOrDefault((item.Group, item.LogicalProcessorIndex)))
                .First();
            ids.Add(set.Id);
        }

        if (ids.Count < count)
        {
            foreach (var set in topology.UsableSets
                         .OrderBy(item => usage.GetValueOrDefault((item.Group, item.LogicalProcessorIndex))))
            {
                if (ids.Count >= count)
                    break;
                if (!ids.Contains(set.Id))
                    ids.Add(set.Id);
            }
        }

        return ids.ToArray();
    }

    private static List<(int ThreadId, uint[] Sets)> QueryThreadSets(int pid)
    {
        var result = new List<(int ThreadId, uint[] Sets)>();
        try
        {
            using var process = Process.GetProcessById(pid);
            foreach (ProcessThread thread in process.Threads)
            {
                var handle = ProcessApi.OpenThreadHandle(ProcessApi.ThreadQueryLimitedInformation, thread.Id);
                if (handle == IntPtr.Zero)
                {
                    result.Add((thread.Id, []));
                    continue;
                }

                try
                {
                    result.Add((thread.Id, CpuSetApi.GetThreadSelectedCpuSets(handle)));
                }
                finally
                {
                    ProcessApi.Close(handle);
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Thread enumeration failed: {ex.Message}");
        }

        return result;
    }

    private static int NonEmptyCount(IEnumerable<(int ThreadId, uint[] Sets)> threads)
        => threads.Count(thread => thread.Sets.Length > 0);

    private static uint sampleKey(Sample sample) => (uint)((sample.Group << 8) | sample.LogicalProcessor);

    private static string FormatDistribution(IEnumerable<Sample> samples)
    {
        var parts = samples
            .GroupBy(sample => sampleKey(sample))
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key >> 8}:{group.Key & 0xFF}={group.Count()}")
            .ToArray();
        return parts.Length == 0 ? "none" : string.Join(", ", parts);
    }

    private static string FormatAllowed(CpuTopology topology, IEnumerable<uint> ids)
    {
        var byId = topology.UsableSets.ToDictionary(set => set.Id);
        return string.Join(", ", ids.Select(id =>
            byId.TryGetValue(id, out var set)
                ? $"{id}(g{set.Group}/lp{set.LogicalProcessorIndex}/c{set.CoreIndex}/e{set.EfficiencyClass})"
                : $"{id}(unknown)"));
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static void SleepUntil(long targetMs)
    {
        var delay = targetMs - NowMs();
        if (delay > 0)
            Thread.Sleep((int)Math.Min(delay, int.MaxValue));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}