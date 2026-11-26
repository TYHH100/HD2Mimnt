using System.Runtime.InteropServices;

namespace HD2Mimnt.Core.IPC;

/// <summary>
/// 命名管道存在性快速探测。.NET 的 NamedPipeClientStream.ConnectAsync 连接不存在的管道会等满超时
/// 才抛 OperationCanceledException（.NET 10 实测行为），GetFileAttributesW 则立即返回，
/// 把"服务端未运行"的判定从秒级压到亚毫秒级；真实连接仍用 ConnectAsync 并保留短超时。
/// </summary>
public static class PipeProbe
{
    private const uint InvalidFileAttributes = 0xFFFFFFFF;

    /// <summary>判断管道当前是否有监听实例（\\.\pipe\{name} 可访问即视为在监听）。</summary>
    public static bool Exists(string pipeName)
        => GetFileAttributesW(@"\\.\pipe\" + pipeName) != InvalidFileAttributes;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFileAttributesW(string lpFileName);
}
