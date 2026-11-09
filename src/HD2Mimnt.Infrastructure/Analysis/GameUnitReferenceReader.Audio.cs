namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>
/// 一个游戏包（如 9ba626afa44a3aa3 或其后缀 .stream 伴生）在 slim DSAR 布局下的只读范围
/// 读取句柄。持有已打开的归档会话，基线生命周期内复用句柄，避免逐条目开关文件；越界或
/// 解码失败一律返回 null 而不是抛异常。
/// </summary>
internal sealed class SlimPackageHandle(Func<long, int, byte[]?> read, Action dispose) : IDisposable
{
    /// <summary>读取包内相对偏移的一段字节。</summary>
    internal byte[]? ReadRange(long offset, int length)
    {
        try
        {
            if (offset < 0 || length <= 0)
                return null;
            return read(offset, length);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose() => dispose();
}

public sealed partial class GameUnitReferenceReader
{
    /// <summary>按包名打开游戏包的范围读取句柄（slim DSAR 布局）。包不存在或归档解析失败返回 null。</summary>
    internal SlimPackageHandle? TryOpenAudioPackage(string gameDirectory, string packageName, CancellationToken token)
    {
        _gate.Wait(token);
        try
        {
            var snapshot = GetSnapshot(gameDirectory, token);
            var package = snapshot.Packages.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, packageName, StringComparison.OrdinalIgnoreCase));
            if (package is not { Items: { Length: > 0 } items })
                return null;
            var session = new ArchiveSession(snapshot, token);
            return new SlimPackageHandle(
                (offset, length) => session.ReadRange(items, (ulong)offset, length),
                session.Dispose);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
