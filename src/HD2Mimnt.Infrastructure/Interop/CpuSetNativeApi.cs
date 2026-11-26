using System.ComponentModel;
using System.Runtime.InteropServices;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Interop;

/// <summary>CPU Sets 与进程默认集合的 Win32 绑定；只做调用与解析，不做策略。</summary>
internal static class CpuSetNativeApi
{
    private const int CpuSetInformationType = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemCpuSetInformation
    {
        public uint Size;
        public int Type;
        public uint Id;
        public ushort Group;
        public byte LogicalProcessorIndex;
        public byte CoreIndex;
        public byte LastLevelCacheIndex;
        public byte NumaNodeIndex;
        public byte EfficiencyClass;
        public byte AllFlags;
        public uint Reserved;
        public ulong AllocationTag;

        public readonly bool Parked => (AllFlags & 1) != 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(
        IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSets(
        IntPtr process, [Out] uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSets(
        IntPtr process, uint[]? cpuSetIds, uint cpuSetIdCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessDefaultCpuSetMasks(
        IntPtr process, [Out] GroupAffinityNative[]? cpuSetMasks, ushort cpuSetMaskCount, out ushort requiredMaskCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessDefaultCpuSetMasks(
        IntPtr process, GroupAffinityNative[]? cpuSetMasks, ushort cpuSetMaskCount);

    public static IReadOnlyList<CpuSetSnapshot> Enumerate()
    {
        _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, out var requiredLength, IntPtr.Zero, 0);
        if (requiredLength == 0)
            throw new InvalidOperationException("GetSystemCpuSetInformation returned an empty buffer.");

        var buffer = Marshal.AllocHGlobal((int)requiredLength);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, requiredLength, out var returnedLength, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation failed.");

            var result = new List<CpuSetSnapshot>();
            var offset = 0;
            var structSize = Marshal.SizeOf<SystemCpuSetInformation>();
            while (offset < returnedLength)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0)
                    break;

                var type = Marshal.ReadInt32(buffer, offset + 4);
                if (type == CpuSetInformationType && size >= structSize)
                {
                    var info = Marshal.PtrToStructure<SystemCpuSetInformation>(IntPtr.Add(buffer, offset));
                    result.Add(new CpuSetSnapshot(
                        info.Id, info.Group, info.LogicalProcessorIndex, info.CoreIndex,
                        info.EfficiencyClass, info.Parked));
                }

                offset += size;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static uint[] GetProcessDefaultCpuSets(IntPtr process)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ok = GetProcessDefaultCpuSets(process, null, 0, out var required);
            if (ok && required == 0)
                return [];
            if (required == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessDefaultCpuSets failed.");

            var ids = new uint[required];
            if (GetProcessDefaultCpuSets(process, ids, required, out var actual))
                return ids[..(int)actual];
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetProcessDefaultCpuSets failed.");
    }

    public static void SetProcessDefaultCpuSets(IntPtr process, IReadOnlyCollection<uint> cpuSetIds)
    {
        var ids = cpuSetIds.Count == 0 ? null : cpuSetIds.ToArray();
        if (!SetProcessDefaultCpuSets(process, ids, (uint)cpuSetIds.Count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetProcessDefaultCpuSets failed.");
    }

    /// <summary>
    /// 按处理器组掩码读写进程默认 CPU Sets（Windows 11 / Server 2022+）。
    /// 旧系统没有该导出函数，返回 false 且 error=127（ERROR_PROC_NOT_FOUND）。
    /// </summary>
    public static bool TryGetProcessDefaultCpuSetMasks(IntPtr process, out ProcessorGroupMask[] masks, out int error)
    {
        masks = [];
        error = 0;
        try
        {
            if (!GetProcessDefaultCpuSetMasks(process, null, 0, out var required))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            if (required == 0)
                return true;

            var buffer = new GroupAffinityNative[required];
            if (!GetProcessDefaultCpuSetMasks(process, buffer, required, out _))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            masks = buffer.Select(ToMask).ToArray();
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            error = 127; // ERROR_PROC_NOT_FOUND
            return false;
        }
    }

    public static bool TrySetProcessDefaultCpuSetMasks(IntPtr process, ProcessorGroupMask[] masks, out int error)
    {
        error = 0;
        try
        {
            var native = masks.Length == 0 ? null : masks.Select(ToNative).ToArray();
            if (!SetProcessDefaultCpuSetMasks(process, native, (ushort)masks.Length))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }
        catch (EntryPointNotFoundException)
        {
            error = 127;
            return false;
        }
    }

    private static ProcessorGroupMask ToMask(GroupAffinityNative native)
        => new(native.Group, native.Mask.ToUInt64());

    private static GroupAffinityNative ToNative(ProcessorGroupMask mask)
        => new() { Mask = new UIntPtr(mask.Mask), Group = mask.Group };

    [StructLayout(LayoutKind.Sequential)]
    private struct GroupAffinityNative
    {
        public UIntPtr Mask;
        public ushort Group;
        public ushort Reserved0;
        public ushort Reserved1;
        public ushort Reserved2;
    }
}
