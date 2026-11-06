using System.Globalization;

namespace HD2Mimnt.ModelConversion.Hd2;

/// <summary>
/// 为补丁里的资源分配稳定且互不重复的 64 位 ID。
///
/// 规则：<c>FNV-1a(64)</c> over <c>hd2mimnt|种类|archive|键</c>。掺入 Archive ID 是为了让
/// body / helmet 两个包各自持有独立的材质与贴图资源——若两包写同一个 ID，模组库的冲突检测
/// 会把它们判成同一资源的重复提供。
/// </summary>
public sealed class Hd2ResourceIdAllocator(ulong archiveId)
{
    private readonly HashSet<ulong> _used = [];

    public ulong ArchiveId { get; } = archiveId;

    /// <summary>分配一个资源 ID；哈希落到 0 或撞到已用 ID 时按递增盐重新散列，保证非零且唯一。</summary>
    public ulong Allocate(string kind, string key)
    {
        var seed = string.Create(CultureInfo.InvariantCulture, $"hd2mimnt|{kind}|{ArchiveId:x16}|{key}");
        var id = StingrayHash.Fnv1A64(seed);
        for (var salt = 0; id == 0 || !_used.Add(id); salt++)
        {
            if (salt > 1024)
                throw new Hd2FormatException($"Could not allocate a unique resource ID for {kind} \"{key}\".");
            id = StingrayHash.Fnv1A64(string.Create(CultureInfo.InvariantCulture, $"{seed}#{salt}"));
        }
        return id;
    }

    /// <summary>登记一个外部已确定的 ID（例如 IAS 路由指定的 Published Unit ID），后续分配不会重复占用。</summary>
    public bool Reserve(ulong id) => id != 0 && _used.Add(id);
}
