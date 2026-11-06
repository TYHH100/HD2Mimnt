using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>补丁内 Lua 提取摘要：源码脚本与字节码各多少个（同名覆盖不重复计数）。</summary>
public readonly record struct LuaExtractionSummary(int SourceCount, int BytecodeCount);

/// <summary>只读提取补丁容器内的 Lua 资源到派生审查目录，不执行脚本、不修改补丁。
/// 源码 envelope（bodyLength + version==2）按头注释路径或资源 ID 落为 .lua；
/// 其余 Lua 类型载荷（如 Wwise 回调字节码）原样落为 .bytes 供反编译审查。</summary>
public static partial class LuaExtractor
{
    private const ulong LuaType = 0xa14e8dfa2cd117e2;

    [GeneratedRegex(@"^-- HD2-Addon: (mods/[A-Za-z0-9_]+(?:/[A-Za-z0-9_]+)*)\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex AddonPattern();

    public static LuaExtractionSummary Extract(string modDirectory, string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        // 记录已写出的相对路径及其资源身份：同一资源被后续补丁更新时覆盖；不同资源重名时加序号避免丢失。
        var written = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        var sourceCount = 0;
        var bytecodeCount = 0;
        // 后缀数字小的补丁先处理，更新补丁（更高后缀）的内容最终覆盖，与游戏实际加载结果一致。
        var patches = Directory.EnumerateFiles(modDirectory, "*.patch_*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var extension = Path.GetExtension(path);
                return extension.StartsWith(".patch_", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(extension.AsSpan(7), out _);
            })
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        foreach (var patchPath in patches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractPatch(patchPath, outputDirectory, written, ref sourceCount, ref bytecodeCount, cancellationToken);
        }
        return new LuaExtractionSummary(sourceCount, bytecodeCount);
    }

    private static void ExtractPatch(string patchPath, string outputDirectory, Dictionary<string, ulong> written,
        ref int sourceCount, ref int bytecodeCount, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(patchPath);
        Span<byte> header = stackalloc byte[72];
        // 头部残缺的补丁无法解析，直接跳过而不越界读取。
        if (stream.Length < header.Length) return;
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xF0000011) return;
        var start = 72L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) * 32L;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        // 索引损坏的补丁跳过而不中断整模组提取；与 BslModDetector 的严格校验不同，这里以尽量多产出为目标。
        if (start + count * 80L > stream.Length) return;
        Span<byte> entry = stackalloc byte[80];
        for (long index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Position = start + index * 80;
            stream.ReadExactly(entry);
            if (BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]) != LuaType) continue;
            var resourceId = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry[56..]);
            if (size == 0 || offset > (ulong)stream.Length || size > (ulong)stream.Length - offset) continue;
            var payload = new byte[size];
            stream.Position = (long)offset;
            stream.ReadExactly(payload);
            WriteEntry(resourceId, payload, outputDirectory, written, ref sourceCount, ref bytecodeCount);
        }
    }

    private static void WriteEntry(ulong resourceId, byte[] payload, string outputDirectory,
        Dictionary<string, ulong> written, ref int sourceCount, ref int bytecodeCount)
    {
        var content = payload;
        var isSource = false;
        var relativeName = $"{resourceId:X16}.lua";
        if (payload.Length >= 8)
        {
            var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            var version = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
            if (version == 2 && bodyLength <= payload.Length - 8)
            {
                isSource = true;
                // 源码只落正文，剥离 bodyLength + version 的 8 字节 envelope。
                content = payload.AsSpan(8, (int)bodyLength).ToArray();
                // 只取正文前缀匹配头注释，避免大脚本整段转字符串。
                var prefixLength = Math.Min((int)bodyLength, 256);
                var prefix = Encoding.UTF8.GetString(payload, 8, prefixLength);
                var match = AddonPattern().Match(prefix);
                // 注释路径须与资源身份哈希一致才可信，防止拿别的资源名误导审查。
                if (match.Success && BslModDetector.ResourceHash(match.Groups[1].Value) == resourceId)
                    relativeName = match.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar) + ".lua";
            }
        }
        if (!isSource) relativeName = $"{resourceId:X16}.bytes";

        if (written.TryGetValue(relativeName, out var existing) && existing != resourceId)
        {
            var directoryName = Path.GetDirectoryName(relativeName) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(relativeName);
            var extension = Path.GetExtension(relativeName);
            var sequence = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(directoryName, $"{stem}-{sequence}{extension}");
                sequence++;
            }
            while (written.TryGetValue(candidate, out var taken) && taken != resourceId);
            relativeName = candidate;
        }

        var target = Path.Combine(outputDirectory, relativeName);
        var targetDirectory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDirectory)) Directory.CreateDirectory(targetDirectory);
        File.WriteAllBytes(target, content);
        if (written.TryAdd(relativeName, resourceId))
        {
            if (isSource) sourceCount++;
            else bytecodeCount++;
        }
    }
}
