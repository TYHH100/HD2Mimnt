using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HD2Mimnt.Infrastructure.Hd2IAS.Companion;

/// <summary>
/// 上游 ExternalRuntime 资产（ArmorAssets.zip / Generator.zip）的解包与缓存。
/// 资产始终从用户已安装的上游目录读取（跟随上游更新），按内容哈希缓存解包目录。
/// </summary>
public static class IasGeneratorAssets
{
    public const string ArmorAssetsZip = "ArmorAssets.zip";
    public const string GeneratorZip = "Generator.zip";

    private static readonly Dictionary<string, (string Hash, DateTime Stamp)> ZipHashMemo = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock MemoLock = new();

    /// <summary>把 zip 解包（或复用缓存）到 cacheRoot\&lt;内容哈希&gt;，返回解包目录。</summary>
    public static string EnsureExtracted(string zipPath, string cacheRoot)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException($"找不到上游运行资源包：{zipPath}", zipPath);
        var hash = ZipIdentity(zipPath);
        var target = Path.Combine(cacheRoot, hash);
        if (Directory.Exists(target)) return target;
        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(cacheRoot, "." + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!destination.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(staging))
                        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"上游资源包内含越界路径：{entry.FullName}");
                if (entry.FullName.EndsWith('/')) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, true);
            }
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            return target;
        }
        finally
        {
            if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch (IOException) { }
        }
    }

    /// <summary>校验 generator.json 身份并返回 python.exe 路径。</summary>
    public static (string PythonExe, JsonObject Manifest) ReadGeneratorManifest(string generatorRoot)
    {
        var manifestPath = Path.Combine(generatorRoot, "generator.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("上游 Generator 包缺少 generator.json。", manifestPath);
        var manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("generator.json 无效。");
        if (manifest["schema"]?.GetValue<string>() != "HD2ArmorGenerator1"
            || manifest["entry"]?.GetValue<string>() != "python/python.exe"
            || manifest["module"]?.GetValue<string>() != "runtime_generation")
            throw new InvalidDataException("生成器清单身份无效。");
        var python = Path.Combine(generatorRoot, "python", "python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("生成器缺少 python.exe。", python);
        return (python, manifest);
    }

    /// <summary>
    /// 读取 ArmorAssets 的 assets.json（HD2ArmorAssets1），校验 files 清单与磁盘一致，
    /// 返回路径已改写为绝对路径的 request 模板。
    /// </summary>
    public static JsonObject ReadAssetsRequest(string assetsRoot)
    {
        var assetsPath = Path.Combine(assetsRoot, "assets.json");
        if (!File.Exists(assetsPath))
            throw new FileNotFoundException("ArmorAssets 包缺少 assets.json。", assetsPath);
        var root = JsonNode.Parse(File.ReadAllBytes(assetsPath))?.AsObject()
            ?? throw new InvalidDataException("assets.json 无效。");
        if (root["schema"]?.GetValue<string>() != "HD2ArmorAssets1")
            throw new InvalidDataException("运行资源清单格式无效。");
        if (root["files"] is not JsonArray rows || rows.Count == 0)
            throw new InvalidDataException("运行资源清单数量无效。");
        var inventory = new Dictionary<string, IasRuntimeFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in rows)
        {
            var row = node.Deserialize<IasRuntimeFile>() ?? throw new InvalidDataException("运行资源清单记录无效。");
            if (row.PreserveExisting || inventory.ContainsKey(row.Path))
                throw new InvalidDataException($"运行资源清单记录无效：{row.Path}");
            var physical = Path.Combine(assetsRoot, row.Path.Replace('/', '\\'));
            VerifyFile(physical, row.Bytes, row.Sha256, row.Path);
            inventory[row.Path] = row;
        }
        if (root["request"] is not JsonObject template)
            throw new InvalidDataException("assets.json 缺少 request 模板。");
        var request = (JsonObject)template.DeepClone();
        ExpandPaths(request, assetsRoot, inventory);
        return request;
    }

    /// <summary>递归改写 request 中所有 {path,bytes,sha256} 文件行的 path 为绝对路径。</summary>
    private static void ExpandPaths(JsonNode? node, string root, Dictionary<string, IasRuntimeFile> inventory)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) ExpandPaths(item, root, inventory);
            return;
        }
        if (node is not JsonObject obj) return;
        if (obj["path"] is JsonValue value && value.TryGetValue<string>(out var relative)
            && obj.ContainsKey("sha256") && obj.ContainsKey("bytes"))
        {
            if (!inventory.TryGetValue(relative, out var row))
                throw new InvalidDataException($"运行资源引用不在清单内：{relative}");
            if (obj["sha256"]?.GetValue<string>() != row.Sha256 || obj["bytes"]?.GetValue<long>() != row.Bytes)
                throw new InvalidDataException($"运行资源引用与清单不一致：{relative}");
            obj["path"] = Path.GetFullPath(Path.Combine(root, relative.Replace('/', '\\')));
            return;
        }
        foreach (var property in obj.ToArray()) ExpandPaths(property.Value, root, inventory);
    }

    internal static void VerifyFile(string path, long expectedBytes, string expectedSha256, string displayName)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"运行资源缺失：{displayName}", path);
        using var stream = File.OpenRead(path);
        if (stream.Length != expectedBytes)
            throw new InvalidDataException($"运行资源大小不一致：{displayName}");
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"运行资源内容已改变：{displayName}");
    }

    /// <summary>zip 内容 sha256（按 路径+长度+修改时间 进程内记忆，避免每次全量读盘）。</summary>
    private static string ZipIdentity(string zipPath)
    {
        var info = new FileInfo(zipPath);
        lock (MemoLock)
        {
            if (ZipHashMemo.TryGetValue(zipPath, out var memo)
                && memo.Stamp == info.LastWriteTimeUtc && info.Length >= 0)
                return memo.Hash;
        }
        string hash;
        using (var stream = File.OpenRead(zipPath))
            hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        lock (MemoLock)
        {
            ZipHashMemo[zipPath] = (hash, info.LastWriteTimeUtc);
        }
        return hash;
    }
}
