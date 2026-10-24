using System.Text;

namespace HD2Mimnt.Core.IPC;

/// <summary>
/// 长度前缀 JSON 帧的共享读写：4 字节小端长度 + UTF-8 载荷。
/// 短读自动续读；超过 <see cref="MaxMessageBytes"/> 的长度视为协议破坏。
/// </summary>
public static class PipeFrame
{
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    public static async Task SendAsync(Stream stream, string payload, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>读取一帧载荷；返回 null 表示对端关闭；长度非法抛 IOException。</summary>
    public static async Task<string?> ReceiveAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBytes = new byte[4];
        if (!await ReadExactlyAsync(stream, lengthBytes, cancellationToken)) return null;

        var length = BitConverter.ToInt32(lengthBytes, 0);
        if (length is <= 0 or > MaxMessageBytes)
        {
            throw new IOException($"Invalid IPC message length: {length}");
        }

        var payload = new byte[length];
        if (!await ReadExactlyAsync(stream, payload, cancellationToken)) return null;
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (read == 0) return false; // 对端关闭
            offset += read;
        }
        return true;
    }
}
