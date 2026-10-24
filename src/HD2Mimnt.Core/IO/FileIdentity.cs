using System.Buffers;
using System.Runtime.InteropServices;

namespace HD2Mimnt.Core.IO;

/// <summary>
/// 增量部署的文件一致性判定。
/// 硬链接部署用文件系统身份判断目标与库源是否同一文件；
/// 复制部署用长度加流式内容比对判断目标是否已是源的最新副本。
/// </summary>
public static class FileIdentity
{
    private const int CompareBufferSize = 1024 * 1024; // 1MiB，与大文件复制缓冲一致

    /// <summary>
    /// 判断两个路径是否指向同一物理文件（同一卷上的同一文件索引）。
    /// 仅硬链接（或同一文件本身）会判定为真；要求目标链接数至少为 2，
    /// 防止库源被移动成目标后误判为可保留。非 Windows 平台不支持，返回 false。
    /// </summary>
    public static bool IsSameFile(string sourcePath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            using var source = File.OpenHandle(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var target = File.OpenHandle(targetPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!TryGetInformation(source, out var sourceInfo) || !TryGetInformation(target, out var targetInfo))
                return false;
            return sourceInfo.VolumeSerialNumber == targetInfo.VolumeSerialNumber
                && ToFileIndex(sourceInfo) == ToFileIndex(targetInfo)
                && targetInfo.NumberOfLinks >= 2;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 流式比较两个文件内容是否一致：长度不同立即返回；相同则逐块比对，
    /// 首个差异处短路返回。用于复制部署判断目标文件是否需要重写。
    /// </summary>
    public static async Task<bool> ContentEqualsAsync(string sourcePath, string targetPath,
        CancellationToken cancellationToken = default)
    {
        var sourceLength = new FileInfo(sourcePath).Length;
        if (new FileInfo(targetPath).Length != sourceLength)
            return false;
        if (sourceLength == 0)
            return true;

        await using var source = new FileStream(sourcePath, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            BufferSize = 1, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await using var target = new FileStream(targetPath, new FileStreamOptions
        {
            Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read,
            BufferSize = 1, Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        var sourceBuffer = ArrayPool<byte>.Shared.Rent(CompareBufferSize);
        var targetBuffer = ArrayPool<byte>.Shared.Rent(CompareBufferSize);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRead = await source.ReadAtLeastAsync(sourceBuffer.AsMemory(0, CompareBufferSize), CompareBufferSize, throwOnEndOfStream: false, cancellationToken);
                var targetRead = await target.ReadAtLeastAsync(targetBuffer.AsMemory(0, CompareBufferSize), CompareBufferSize, throwOnEndOfStream: false, cancellationToken);
                if (sourceRead != targetRead || !sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(targetBuffer.AsSpan(0, targetRead)))
                    return false;
                if (sourceRead == 0)
                    return true;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }

    private static ulong ToFileIndex(in BY_HANDLE_FILE_INFORMATION info)
        => ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;

    private static bool TryGetInformation(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out BY_HANDLE_FILE_INFORMATION information)
    {
        information = default;
        return GetFileInformationByHandle(handle, out information);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}
