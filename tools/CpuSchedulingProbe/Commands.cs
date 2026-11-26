using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CpuSchedulingProbe.Native;

namespace CpuSchedulingProbe;

internal static class Commands
{
    public static int Topology()
    {
        var topology = CpuTopology.Load();
        var buffer = CpuSetApi.QueryRequiredLength();
        Console.WriteLine($"CPU: {topology.CpuName}");
        Console.WriteLine(
            $"Buffer probe: required={buffer.RequiredLength} bytes, first error={ProcessApi.DescribeError(buffer.FirstError)}");
        Console.WriteLine(
            $"Sets: {topology.AllSets.Count} total, {topology.UsableSets.Count} usable, {topology.ParkedCount} parked");
        Console.WriteLine(
            $"Groups: {string.Join(", ", topology.UsableSets.GroupBy(set => set.Group).Select(group => $"{group.Key} ({group.Count()})"))}");
        Console.WriteLine($"Cores: {topology.Cores.Count}, logical processors: {topology.UsableSets.Count}");
        Console.WriteLine(
            $"EfficiencyClass: {string.Join(", ", topology.Cores.GroupBy(core => core.EfficiencyClass).OrderBy(group => group.Key).Select(group => $"{group.Key} x{group.Count()} cores"))}");
        Console.WriteLine($"P-Core: {topology.PerformanceCores.Count} physical / {topology.PerformanceLogicalCount} logical");
        Console.WriteLine($"E-Core: {topology.EfficiencyCores.Count} physical / {topology.EfficiencyLogicalCount} logical");
        Console.WriteLine($"Supported: {topology.Supported} ({topology.Reason})");
        Console.WriteLine(topology.StrictModeAvailable
            ? $"Strict mode: available, mask 0x{topology.StrictMask:X}"
            : $"Strict mode: unavailable (P-cores missing, split across groups, or index >= 64)");
        Console.WriteLine();
        Console.WriteLine($"{"Id",6} {"Group",5} {"LP",3} {"Core",4} {"Eff",3} {"Parked",6} Kind");

        foreach (var set in topology.AllSets.OrderBy(set => set.Group).ThenBy(set => set.LogicalProcessorIndex))
        {
            var core = topology.Cores.FirstOrDefault(item => item.Group == set.Group && item.CoreIndex == set.CoreIndex);
            var kind = !topology.Supported || core is null
                ? "?"
                : core.EfficiencyClass == topology.PerformanceEfficiencyClass ? "P" : "E";
            Console.WriteLine(
                $"{set.Id,6} {set.Group,5} {set.LogicalProcessorIndex,3} {set.CoreIndex,4} {set.EfficiencyClass,3} {(set.Parked ? 1 : 0),6} {kind}");
        }

        return 0;
    }

    public static int Sets(string[] args)
    {
        var pid = Args.RequiredInt(args, "--pid");
        var handle = ProcessApi.OpenProcessHandle(ProcessApi.ProcessQueryLimitedInformation, pid);
        if (handle == IntPtr.Zero)
        {
            Console.Error.WriteLine(
                $"OpenProcess failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
            return 1;
        }

        try
        {
            Console.WriteLine($"PID: {pid}");
            Console.WriteLine($"Default CPU sets: {FormatSets(CpuSetApi.GetProcessDefaultCpuSets(handle))}");
            Console.WriteLine(ProcessApi.TryGetAffinity(handle, out var processMask, out var systemMask)
                ? $"Affinity: 0x{processMask:X}, system: 0x{systemMask:X}"
                : $"Affinity: unavailable ({ProcessApi.DescribeError(Marshal.GetLastWin32Error())})");

            var threads = Process.GetProcessById(pid).Threads.Cast<ProcessThread>().ToArray();
            var withSelected = 0;
            Console.WriteLine($"Threads: {threads.Length}");
            foreach (var thread in threads)
            {
                var threadHandle = ProcessApi.OpenThreadHandle(ProcessApi.ThreadQueryLimitedInformation, thread.Id);
                if (threadHandle == IntPtr.Zero)
                {
                    Console.WriteLine(
                        $"  T{thread.Id}: OpenThread failed ({ProcessApi.DescribeError(Marshal.GetLastWin32Error())})");
                    continue;
                }

                try
                {
                    var selected = CpuSetApi.GetThreadSelectedCpuSets(threadHandle);
                    if (selected.Length > 0)
                        withSelected++;
                    Console.WriteLine($"  T{thread.Id}: selected {FormatSets(selected)}");
                }
                finally
                {
                    ProcessApi.Close(threadHandle);
                }
            }

            Console.WriteLine($"Thread-selected non-empty: {withSelected}/{threads.Length}");
            return 0;
        }
        finally
        {
            ProcessApi.Close(handle);
        }
    }

    public static int Apply(string[] args)
    {
        var pid = Args.RequiredInt(args, "--pid");
        var mode = (Args.Option(args, "--mode") ?? "prefer").ToLowerInvariant();
        if (mode is not ("prefer" or "strict"))
            throw new ArgumentException("--mode must be prefer or strict.");

        var topology = CpuTopology.Load();
        var handle = ProcessApi.OpenProcessHandle(
            ProcessApi.ProcessQueryLimitedInformation | ProcessApi.ProcessSetInformation, pid);
        if (handle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"OpenProcess failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
            return 1;
        }

        try
        {
            var state = new ProbeState
            {
                Pid = pid,
                StartTimeUtcTicks = ProcessApi.TryGetStartTimeUtcTicks(pid),
                Mode = mode
            };

            if (mode == "prefer")
            {
                var requested = Args.CpuSetList(args, "--sets") ?? topology.PerformanceCpuSetIds.ToArray();
                if (requested.Length == 0)
                {
                    Console.Error.WriteLine(
                        "Topology has no performance-core CPU sets; pass --sets for a manual probe.");
                    return 1;
                }

                state.OriginalDefaultSets = CpuSetApi.GetProcessDefaultCpuSets(handle);
                CpuSetApi.SetProcessDefaultCpuSets(handle, requested);
                var applied = CpuSetApi.GetProcessDefaultCpuSets(handle);
                state.AppliedSets = applied;

                Console.WriteLine($"PID: {pid}");
                Console.WriteLine($"Original default sets: {FormatSets(state.OriginalDefaultSets)}");
                Console.WriteLine($"Requested sets: {FormatSets(requested)}");
                Console.WriteLine($"Applied sets: {FormatSets(applied)}");
                Console.WriteLine($"Verify: {(SetEquals(applied, requested) ? "match" : "MISMATCH")}");
            }
            else
            {
                var requestedMask = Args.HexMask(args, "--mask") ?? topology.StrictMask;
                if (requestedMask == 0)
                {
                    Console.Error.WriteLine(
                        "Topology has no strict-mode mask; pass --mask for a manual probe.");
                    return 1;
                }

                if (!ProcessApi.TryGetAffinity(handle, out var originalMask, out var systemMask))
                {
                    Console.Error.WriteLine(
                        $"GetProcessAffinityMask failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
                    return 1;
                }

                state.OriginalAffinity = originalMask;
                ProcessApi.SetAffinity(handle, requestedMask);
                ProcessApi.TryGetAffinity(handle, out var appliedMask, out _);
                state.AppliedAffinity = appliedMask;

                Console.WriteLine($"PID: {pid}");
                Console.WriteLine($"Original affinity: 0x{originalMask:X}, system: 0x{systemMask:X}");
                Console.WriteLine($"Requested affinity: 0x{requestedMask:X}");
                Console.WriteLine($"Applied affinity: 0x{appliedMask:X}");
                Console.WriteLine($"Verify: {(appliedMask == requestedMask ? "match" : "MISMATCH")}");
            }

            ProbeStateStore.Save(state);
            Console.WriteLine($"State saved: {ProbeStateStore.PathFor(pid)}");
            return 0;
        }
        finally
        {
            ProcessApi.Close(handle);
        }
    }

    public static int Restore(string[] args)
    {
        var pid = Args.RequiredInt(args, "--pid");
        var force = Args.Flag(args, "--force");
        var state = ProbeStateStore.Load(pid);
        if (state is null)
        {
            Console.Error.WriteLine($"No saved state for PID {pid}.");
            return 1;
        }

        var currentStart = ProcessApi.TryGetStartTimeUtcTicks(pid);
        if (state.StartTimeUtcTicks != 0 && currentStart != 0 && state.StartTimeUtcTicks != currentStart)
        {
            Console.Error.WriteLine("PID was reused by a new process; refusing to restore.");
            return 1;
        }

        var handle = ProcessApi.OpenProcessHandle(
            ProcessApi.ProcessQueryLimitedInformation | ProcessApi.ProcessSetInformation, pid);
        if (handle == IntPtr.Zero)
        {
            Console.Error.WriteLine($"OpenProcess failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
            return 1;
        }

        try
        {
            if (state.Mode == "prefer")
            {
                var current = CpuSetApi.GetProcessDefaultCpuSets(handle);
                if (!SetEquals(current, state.AppliedSets) && !force)
                {
                    Console.Error.WriteLine(
                        $"Current sets {FormatSets(current)} differ from applied {FormatSets(state.AppliedSets)}; use --force to overwrite.");
                    return 2;
                }

                CpuSetApi.SetProcessDefaultCpuSets(handle, state.OriginalDefaultSets);
                var restored = CpuSetApi.GetProcessDefaultCpuSets(handle);
                Console.WriteLine($"Original default sets: {FormatSets(state.OriginalDefaultSets)}");
                Console.WriteLine($"Restored sets: {FormatSets(restored)}");
                Console.WriteLine($"Verify: {(SetEquals(restored, state.OriginalDefaultSets) ? "match" : "MISMATCH")}");
                if (!SetEquals(restored, state.OriginalDefaultSets))
                    return 1;
            }
            else
            {
                if (!ProcessApi.TryGetAffinity(handle, out var currentMask, out _))
                {
                    Console.Error.WriteLine(
                        $"GetProcessAffinityMask failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
                    return 1;
                }

                if (currentMask != state.AppliedAffinity && !force)
                {
                    Console.Error.WriteLine(
                        $"Current affinity 0x{currentMask:X} differs from applied 0x{state.AppliedAffinity:X}; use --force to overwrite.");
                    return 2;
                }

                ProcessApi.SetAffinity(handle, state.OriginalAffinity ?? currentMask);
                ProcessApi.TryGetAffinity(handle, out var restoredMask, out _);
                Console.WriteLine($"Restored affinity: 0x{restoredMask:X}");
                Console.WriteLine($"Verify: {(restoredMask == state.OriginalAffinity ? "match" : "MISMATCH")}");
                if (restoredMask != state.OriginalAffinity)
                    return 1;
            }

            ProbeStateStore.Delete(pid);
            Console.WriteLine("State removed.");
            return 0;
        }
        finally
        {
            ProcessApi.Close(handle);
        }
    }

    public static int StrictTest(string[] args)
    {
        var seconds = Args.Int(args, "--seconds", 10);
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add($"ping -n {seconds + 10} 127.0.0.1 >nul");

        using var child = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start cmd.exe.");
        Thread.Sleep(800);

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
            if (!ProcessApi.TryGetAffinity(handle, out var originalMask, out var systemMask))
            {
                Console.Error.WriteLine(
                    $"GetProcessAffinityMask failed: {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
                return 1;
            }

            var allowed = PickLowestBits(systemMask, 4);
            if (allowed == 0)
            {
                Console.Error.WriteLine("System affinity mask is empty.");
                return 1;
            }

            Console.WriteLine($"PID: {child.Id}");
            Console.WriteLine($"Original affinity: 0x{originalMask:X}, system: 0x{systemMask:X}");

            ProcessApi.SetAffinity(handle, allowed);
            ProcessApi.TryGetAffinity(handle, out var appliedMask, out _);
            Console.WriteLine($"Applied affinity: 0x{appliedMask:X} (requested 0x{allowed:X})");
            Thread.Sleep(1000);

            ProcessApi.SetAffinity(handle, originalMask);
            ProcessApi.TryGetAffinity(handle, out var restoredMask, out _);
            Console.WriteLine($"Restored affinity: 0x{restoredMask:X} (original 0x{originalMask:X})");

            var ok = appliedMask == allowed && restoredMask == originalMask;
            Console.WriteLine($"Result: {(ok ? "PASS" : "FAIL")}");
            return ok ? 0 : 1;
        }
        finally
        {
            ProcessApi.Close(handle);
            TryKill(child);
        }
    }

    public static int Denied()
    {
        string[] names = ["System", "csrss", "wininit", "services", "lsass", "MsMpEng", "smss", "winlogon"];
        var denied = 0;
        var total = 0;

        foreach (var name in names)
        {
            Process? candidate = null;
            try
            {
                candidate = Process.GetProcessesByName(name).FirstOrDefault();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                Console.WriteLine($"{name}: enumeration failed ({ex.Message})");
                continue;
            }

            if (candidate is null)
            {
                Console.WriteLine($"{name}: not running");
                continue;
            }

            foreach (var (label, access) in new[]
                     {
                         ("QUERY_LIMITED", ProcessApi.ProcessQueryLimitedInformation),
                         ("SET_LIMITED", ProcessApi.ProcessSetLimitedInformation)
                     })
            {
                total++;
                var handle = ProcessApi.OpenProcessHandle(access, candidate.Id);
                if (handle == IntPtr.Zero)
                {
                    denied++;
                    Console.WriteLine(
                        $"{name} (PID {candidate.Id}) {label}: DENIED {ProcessApi.DescribeError(Marshal.GetLastWin32Error())}");
                }
                else
                {
                    ProcessApi.Close(handle);
                    Console.WriteLine($"{name} (PID {candidate.Id}) {label}: open succeeded");
                }
            }
        }

        Console.WriteLine($"Denied opens: {denied}/{total} (no setter was called)");
        return 0;
    }

    private static ulong PickLowestBits(ulong mask, int count)
    {
        ulong result = 0;
        var picked = 0;
        for (var bit = 0; bit < 64 && picked < count; bit++)
        {
            if ((mask & (1UL << bit)) == 0)
                continue;
            result |= 1UL << bit;
            picked++;
        }

        return result;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    internal static string FormatSets(IReadOnlyCollection<uint> sets)
        => sets.Count == 0 ? "(none)" : string.Join(",", sets);

    internal static bool SetEquals(IReadOnlyCollection<uint> left, IReadOnlyCollection<uint> right)
        => left.Count == right.Count
           && left.OrderBy(value => value).SequenceEqual(right.OrderBy(value => value));
}