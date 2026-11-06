using System.Globalization;
using System.Text;

namespace HD2Mimnt.ModelConversion.Ias;

/// <summary>
/// IAS 运行时文件里的三类稳定身份。全部照抄 HD2 独立封包 SDK 的派生方式，
/// 产物才能被管理器、HD2IR 工具与游戏按同样的键认出来：
/// <list type="bullet">
/// <item><description>项目身份 <c>[0-9a-f]{32}</c>：运行时文件名的前缀（<c>{project}.{archive}.rigbin</c>）。</description></item>
/// <item><description>隔离清单 <c>owned_{24 hex}</c>：由「项目 + Archive」派生，文件名与 <c>isolation_id</c> 必须一致。</description></item>
/// <item><description>差分清单 <c>hd2free_{16 hex}</c>：文件名与 <c>manifest_id</c> 必须一致。</description></item>
/// </list>
/// </summary>
public static class IasIdentity
{
    /// <summary>差分清单标签前缀（free-parts v6）。</summary>
    public const string DifferencePrefix = "hd2free";

    /// <summary>隔离清单标签前缀：本模组「拥有」这些 Unit。</summary>
    public const string OwnedPrefix = "owned";

    /// <summary>BLAKE2s 的个性化参数，与 SDK 的 <c>_stable_label</c> 一致。</summary>
    private static readonly byte[] DifferencePerson = "HD2DfV4"u8.ToArray();

    /// <summary>
    /// 项目身份：32 位小写十六进制。已经是 32 位十六进制时原样保留（允许作者自带 ID）。
    /// </summary>
    public static string Project(string project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        if (project.Length == 32 && IsHex(project)) return project.ToLowerInvariant();
        var digest = Blake2s.Hash(Encoding.UTF8.GetBytes("HD2MimntIas1\0" + project), 16);
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>隔离清单 ID：<c>owned_</c> + blake2s("项目\0Archive", 12)。</summary>
    public static string Owned(string projectId, ulong archiveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var payload = Encoding.UTF8.GetBytes(projectId + "\0" + archiveId.ToString("x16", CultureInfo.InvariantCulture));
        return OwnedPrefix + "_" + Convert.ToHexStringLower(Blake2s.Hash(payload, 12));
    }

    /// <summary>差分清单 ID：<c>hd2free_</c> + blake2s("项目\0Archive", 16, person "HD2DfV4") 的前 16 位。</summary>
    public static string Difference(string projectId, ulong archiveId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var payload = Encoding.UTF8.GetBytes(projectId + "\0" + archiveId.ToString("x16", CultureInfo.InvariantCulture));
        var digest = Blake2s.Hash(payload, 16, DifferencePerson);
        return DifferencePrefix + "_" + Convert.ToHexStringLower(digest)[..16];
    }

    /// <summary>项目身份是否满足 <c>[0-9a-f]{32}</c>（运行时文件名的解析前提）。</summary>
    public static bool IsProjectId(string? value) =>
        value is { Length: 32 } && IsHex(value) && !value.Any(char.IsUpper);

    private static bool IsHex(string value) =>
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}
