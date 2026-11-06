using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HD2Mimnt.ModelConversion.Ias;

namespace HD2Mimnt.ModelConversion.Gmod.Packaging;

/// <summary>
/// 一份 <c>.hd2irpack.zip</c>：独立骨架运行时（HD2IndependentRig）的配套包。
///
/// IAS 护甲除 <c>data/</c> 补丁外，还需要把 rigbin / 物理文件 / 差分与隔离清单放进
/// <c>bin/HD2IndependentRig/</c>；社区与 HD2IR 工具把这个目录打成 <c>xxx.hd2irpack.zip</c>，
/// 根目录带一份 <c>mod_group.json</c>（<c>HD2RuntimeGroup1</c>）说明本次导出的成员与校验值。
/// </summary>
public sealed record GmodRuntimePack(string FileName, IReadOnlyList<GmodOutputFile> Files, string GroupId,
    int MemberCount, int RigFileCount, int PhysicsFileCount);

/// <summary>
/// 把一次转换产出的 rigbin / hd2phys / difference / isolation 打成一个可导入模组库的
/// <c>.hd2irpack.zip</c>，与 <c>package/</c> data 模组配套使用。
///
/// 结构对齐 HD2 独立封包 SDK（<c>runtime_group.py</c>）：
/// 条目只能在 <c>HD2IndependentRig/</c> 下，文件名必须落在
/// <c>[A-Za-z0-9_-][A-Za-z0-9_.-]*.(rigbin|hd2phys|hd2shared|hd2pose|hd2preview|ini|hd2author)</c>，
/// 每个 <c>.rigbin</c> 必须叫 <c>{32 位项目}.{16 位 Archive}.rigbin</c>，
/// 否则导入端会判定「配套项目身份无效」。
/// </summary>
public static partial class GmodRuntimePackBuilder
{
    /// <summary>包内运行时目录名；部署后落到游戏 <c>bin/HD2IndependentRig/</c>。</summary>
    public const string RigDirectory = "HD2IndependentRig";

    public const string GroupSchema = "HD2RuntimeGroup1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>zip 内条目的固定时间戳：同样的输入必须得到逐字节相同的包。</summary>
    private static readonly DateTimeOffset FixedTimestamp =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>导入端允许的运行时文件名（与 SDK 的 LEAF 正则一致）。</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-][A-Za-z0-9_.-]*\.(rigbin|hd2phys|hd2shared|hd2pose|hd2preview|ini|hd2author)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeLeaf();

    /// <param name="projectId">运行时项目身份（32 位十六进制），写进 <c>members[].project</c>。</param>
    /// <param name="displayName">人类可读的项目名，只用于组的显示名。</param>
    /// <param name="exportedAt">写进 <c>mod_group.json</c> 的导出时间；管理器会校验它存在且可解析，
    /// 缺字段会被判成「Mod 组身份、名称或时间无效」而整包拒收。</param>
    public static GmodRuntimePack Build(string projectId, string displayName,
        IReadOnlyList<GmodConversionPart> parts, IReadOnlyList<GmodOutputFile> files,
        DateTimeOffset? exportedAt = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(files);
        if (parts.Count == 0) throw new ArgumentException("打包运行时至少需要一个部位。", nameof(parts));
        if (!IasIdentity.IsProjectId(projectId))
            throw new ArgumentException($"运行时项目身份必须是 32 位十六进制：{projectId}", nameof(projectId));

        var entries = new List<(string Path, byte[] Data)>();
        var members = new List<object>();
        var rigFiles = 0;
        var physicsFiles = 0;

        foreach (var part in parts)
        {
            var prefix = part.Prefix;
            var companion = files.Where(file => file.Kind == "ias" &&
                file.Path.StartsWith(prefix + "ias/", StringComparison.Ordinal)).ToArray();
            var physics = files.Where(file => file.Kind == "physics" &&
                file.Path.StartsWith(prefix + "physics/", StringComparison.Ordinal)).ToArray();

            var rigbin = companion.FirstOrDefault(file => file.Path.EndsWith(".rigbin", StringComparison.Ordinal));
            var isolation = companion.FirstOrDefault(file => file.Path.EndsWith(".isolation.ini", StringComparison.Ordinal));
            var difference = companion.FirstOrDefault(file => file.Path.EndsWith(".difference.ini", StringComparison.Ordinal));

            var partFiles = new List<(string Path, byte[] Data)>();
            if (rigbin is not null) partFiles.Add((rigbin.Path, rigbin.Data.ToArray()));
            if (difference is not null) partFiles.Add((difference.Path, difference.Data.ToArray()));
            if (isolation is not null) partFiles.Add((isolation.Path, isolation.Data.ToArray()));
            foreach (var file in physics) partFiles.Add((file.Path, file.Data.ToArray()));
            if (partFiles.Count == 0) continue;

            var memberFiles = new List<object>(partFiles.Count);
            foreach (var (path, data) in partFiles.OrderBy(entry => Leaf(entry.Path), StringComparer.Ordinal))
            {
                var name = Leaf(path);
                if (!RuntimeLeaf().IsMatch(name))
                    throw new ArgumentException($"运行时文件名不符合 HD2IndependentRig 规则：{name}", nameof(files));
                var entry = RigDirectory + "/" + name;
                entries.Add((entry, data));
                memberFiles.Add(new { path = entry, sha256 = Convert.ToHexStringLower(SHA256.HashData(data)) });
            }
            if (rigbin is not null) rigFiles++;
            physicsFiles += physics.Length;
            members.Add(new
            {
                project = projectId,
                archive = part.ArchiveId.ToString("x16", CultureInfo.InvariantCulture),
                files = memberFiles
            });
        }

        if (entries.Count == 0)
            throw new ArgumentException("没有任何 rig/物理/清单文件可以打进运行时包。", nameof(parts));

        var groupId = DeriveGroupId(projectId, parts.Select(part => part.ArchiveId));
        var group = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = GroupSchema,
            id = groupId,
            name = displayName,
            // SDK 的 write_group 一定写 exported_at，导入端把它当作「时间」校验项；
            // 缺这个字段整包会被判无效，所以即使时间戳是唯一的不确定项也必须写。
            exported_at = (exportedAt ?? DateTimeOffset.Now).ToString("o", CultureInfo.InvariantCulture),
            members
        }, Json);
        entries.Add(("mod_group.json", group));

        var fileName = displayName + ".hd2irpack.zip";
        var zip = Zip(entries);
        var output = new GmodOutputFile(fileName, "runtime-pack", zip,
            Convert.ToHexStringLower(SHA256.HashData(zip)));
        return new GmodRuntimePack(fileName, [output], groupId, members.Count, rigFiles, physicsFiles);
    }

    /// <summary>运行时目录里的相对文件名：<c>body/ias/xxx.rigbin</c> → <c>xxx.rigbin</c>。</summary>
    private static string Leaf(string path)
    {
        var name = path.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }

    /// <summary>组 ID：项目 + 全部成员 Archive 决定，重复转换得到同一个 ID，导入即更新。</summary>
    private static string DeriveGroupId(string projectId, IEnumerable<ulong> archives)
    {
        var builder = new StringBuilder(projectId);
        foreach (var archive in archives.Order())
            builder.Append('|').Append(archive.ToString("x16", CultureInfo.InvariantCulture));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("hd2mimnt-gmod-ir|" + builder));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static byte[] Zip(IReadOnlyList<(string Path, byte[] Data)> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, data) in entries.OrderBy(entry => entry.Path, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                entry.LastWriteTime = FixedTimestamp;
                using var target = entry.Open();
                target.Write(data);
            }
        }
        return stream.ToArray();
    }
}
