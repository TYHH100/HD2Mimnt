using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CpuSchedulingProbe.Native;

internal static class CpuSetApi
{
    private const int CpuSetInformationType = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemCpuSetInformation
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
    private static extern bool GetThreadSelectedCpuSets(
        IntPtr thread, [Out] uint[]? cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

    public static (uint RequiredLength, int FirstError) QueryRequiredLength()
    {
        _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, out var requiredLength, IntPtr.Zero, 0);
        return (requiredLength, Marshal.GetLastWin32Error());
    }

    public static IReadOnlyList<SystemCpuSetInformation> Enumerate()
    {
        _ = GetSystemCpuSetInformation(IntPtr.Zero, 0, out var requiredLength, IntPtr.Zero, 0);
        if (requiredLength == 0)
            throw new InvalidOperationException("GetSystemCpuSetInformation returned an empty buffer.");

        var buffer = Marshal.AllocHGlobal((int)requiredLength);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, requiredLength, out var returnedLength, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetSystemCpuSetInformation failed.");

            var result = new List<SystemCpuSetInformation>();
            var offset = 0;
            var structSize = Marshal.SizeOf<SystemCpuSetInformation>();
            while (offset < returnedLength)
            {
                var size = Marshal.ReadInt32(buffer, offset);
                if (size <= 0)
                    break;

                var type = Marshal.ReadInt32(buffer, offset + 4);
                if (type == CpuSetInformationType && size >= structSize)
                    result.Add(Marshal.PtrToStructure<SystemCpuSetInformation>(IntPtr.Add(buffer, offset)));

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

    public static void SetProcessDefaultCpuSets(IntPtr process, IReadOnlyList<uint> cpuSetIds)
    {
        var ids = cpuSetIds.Count == 0 ? null : cpuSetIds.ToArray();
        if (!SetProcessDefaultCpuSets(process, ids, (uint)cpuSetIds.Count))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetProcessDefaultCpuSets failed.");
    }

    public static uint[] GetThreadSelectedCpuSets(IntPtr thread)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ok = GetThreadSelectedCpuSets(thread, null, 0, out var required);
            if (ok && required == 0)
                return [];
            if (required == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetThreadSelectedCpuSets failed.");

            var ids = new uint[required];
            if (GetThreadSelectedCpuSets(thread, ids, required, out var actual))
                return ids[..(int)actual];
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "GetThreadSelectedCpuSets failed.");
    }
}