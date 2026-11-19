using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace HD2Mimnt.Infrastructure.ModelConversion.Mmd;

public sealed record MmdSourceMetadata(
    string? Name = null, string? Key = null, string? Author = null, string? Icon = null, string? Description = null);

public sealed record MmdSourceOption(string Key, string Name, IReadOnlyList<string> Patches);

public sealed record MmdSourceDocument(
    string SourcePath, IReadOnlyDictionary<string, byte[]> Members, IasValue.Object Meta, IReadOnlyList<string> Patches);

/// <summary>合集模组里的单个曲包目录；Key 是目录全路径，可稳定用于再次选择。</summary>
public sealed record MmdSourceModule(string Key, string Name, string Path);

/// <summary>
/// 上游 <c>mmd_adapter._source</c> 与 <c>source_io</c> 的读取子集：
/// 支持目录、zip/7z、单个补丁三件套入口；清单目录外有散落文件时拒绝。
/// </summary>
public static class MmdSourceReader
{
    private static readonly Regex PatchPattern = new("\\A[0-9a-fA-F]{16}\\.patch_\\d+\\z", RegexOptions.CultureInvariant);
    private static readonly Regex ReplacementSplit = new("\\s*替换\\s*", RegexOptions.CultureInvariant);
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private static readonly HashSet<string> ReservedNames = BuildReservedNames();

    private static HashSet<string> BuildReservedNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CON", "PRN", "AUX", "NUL" };
        for (var index = 1; index <= 9; index++)
        {
            names.Add("COM" + index);
            names.Add("LPT" + index);
        }
        return names;
    }

    public static MmdSourceDocument Read(string sourcePath, MmdSourceMetadata? metadata, CancellationToken token)
    {
        var resolved = Path.GetFullPath(sourcePath);
        if (!File.Exists(resolved) && !Directory.Exists(resolved))
            throw new FileNotFoundException("原始 MMD 资源不存在：" + sourcePath);
        var members = ReadMembers(resolved, token);
        var manifests = members.Keys.Where(name => PureName(name) == "manifest.json").ToList();
        IasValue.Object meta;
        if (manifests.Count > 1)
        {
            throw new InvalidDataException("来源有多份模组清单，请选择一套具体 MMD 模组。");
        }
        else if (manifests.Count == 1)
        {
            var prefix = PurePosixDir(manifests[0]);
            prefix = prefix.Length == 0 ? string.Empty : prefix + "/";
            if (members.Keys.Any(name => !name.StartsWith(prefix, StringComparison.Ordinal)))
                throw new InvalidDataException("模组清单目录外还有文件，请选择具体模组目录。");
            var stripped = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (name, raw) in members) stripped[name[prefix.Length..]] = raw;
            members = stripped;
            meta = MmdJson.ParseManifest(members["manifest.json"]);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(metadata?.Name) || string.IsNullOrWhiteSpace(metadata.Key))
                throw new InvalidDataException("来源缺少 manifest.json，请补充名称和稳定来源标识。");
            meta = (IasValue.Object)IasValue.Obj(
                ("Name", IasValue.Of(metadata.Name)),
                ("Guid", IasValue.Of(MmdIdentity.Uuid5("HD2ManualMMDSource/" + metadata.Key))),
                ("Author", IasValue.Of(metadata.Author ?? string.Empty)),
                ("Description", IasValue.Of(metadata.Description ?? string.Empty)));
        }

        if (string.IsNullOrWhiteSpace(MmdJson.Text(meta, "Guid")))
            throw new InvalidDataException("来源缺少稳定 Guid，请补充来源标识。");
        string? icon = null;
        if (!string.IsNullOrWhiteSpace(metadata?.Icon) && File.Exists(metadata.Icon))
        {
            var selected = Path.GetFullPath(metadata.Icon);
            icon = "icon" + Path.GetExtension(selected).ToLowerInvariant();
            members[icon] = File.ReadAllBytes(selected);
        }
        icon ??= ChooseSourceIcon(members, MmdJson.Text(meta, "IconPath"));
        meta = icon is null ? MmdJson.Without(meta, "IconPath") : MmdJson.With(meta, "IconPath", IasValue.Of(icon));

        var patches = members.Keys.Where(name => PatchPattern.IsMatch(PureName(name))).ToList();
        if (patches.Count == 0) throw new InvalidDataException("来源没有动作补丁文件。");
        patches.Sort(ComparePatches);
        return new MmdSourceDocument(resolved, members, meta, patches);
    }

    /// <summary>
    /// 合集模组：管理器导入的多曲包目录里每个子目录各有 manifest.json。
    /// 只返回确实含 MMD 动作或适配标记的曲包，共用运行时等非 MMD 组件不参与选择。
    /// </summary>
    public static IReadOnlyList<MmdSourceModule> FindModules(string sourcePath, CancellationToken token)
    {
        if (!Directory.Exists(sourcePath)) return [];
        var manifests = new List<string>();
        CollectManifests(Path.GetFullPath(sourcePath), manifests, token, 0);
        if (manifests.Count <= 1) return [];
        var modules = new List<MmdSourceModule>();
        foreach (var directory in manifests.Select(Path.GetDirectoryName)!
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (directory is null || !LooksLikeMmdPack(directory, token)) continue;
            var name = Path.GetFileName(directory);
            try
            {
                var meta = MmdJson.ParseManifest(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
                name = MmdJson.Text(meta, "Name") ?? name;
            }
            catch (InvalidDataException) { }
            modules.Add(new MmdSourceModule(directory, name, directory));
        }
        var duplicated = modules.GroupBy(module => module.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        return modules.Select(module => duplicated.Contains(module.Name)
                ? module with { Name = module.Name + "（" + Path.GetFileName(module.Path) + "）" }
                : module)
            .OrderBy(module => module.Name, StringComparer.Ordinal).ToList();
    }

    private static void CollectManifests(string directory, List<string> result, CancellationToken token, int depth)
    {
        if (depth > 6 || result.Count >= 64) return;
        token.ThrowIfCancellationRequested();
        if (File.Exists(Path.Combine(directory, "manifest.json"))) result.Add(Path.Combine(directory, "manifest.json"));
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
            CollectManifests(child, result, token, depth + 1);
        }
    }

    private static bool LooksLikeMmdPack(string directory, CancellationToken token)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.patch_0", SearchOption.AllDirectories)
                     .Where(file => PatchPattern.IsMatch(Path.GetFileName(file))).Take(8))
        {
            token.ThrowIfCancellationRequested();
            byte[] toc;
            try { toc = File.ReadAllBytes(file); }
            catch (IOException) { continue; }
            if (HasMmdContent(toc)) return true;
        }
        return false;
    }

    private static bool HasMmdContent(byte[] toc)
    {
        IReadOnlyDictionary<(ulong Type, ulong Id), IasArchiveRow> rows;
        try { rows = IasArchive.ReadRows(toc); }
        catch (InvalidDataException) { return false; }
        if (rows.Values.Any(row => row.Type == IasResourceTypes.Animation)) return true;
        var mark = Encoding.ASCII.GetBytes(MmdBundle.Mark);
        foreach (var row in rows.Values)
        {
            if (row.Type != IasResourceTypes.Lua) continue;
            var offset = (long)row.TocOffset;
            if (offset < 0 || offset + 8 + mark.Length > toc.LongLength) continue;
            if (toc.AsSpan((int)offset + 8, mark.Length).SequenceEqual(mark)) return true;
        }
        return false;
    }

    /// <summary>上游 <c>_options</c>：按清单选项把补丁分组；没有选项时整包作为一项。</summary>
    public static IReadOnlyList<MmdSourceOption> ReadOptions(IasValue.Object meta, IReadOnlyList<string> patches)
    {
        if (MmdJson.Get(meta, "Options") is not IasValue.Array options)
            return [new MmdSourceOption("default", "完整模组", patches)];
        var folders = patches.Select(PurePosixDir).ToHashSet(StringComparer.Ordinal);
        var result = new List<MmdSourceOption>();
        foreach (var item in options.Items)
        {
            if (item is not IasValue.Object parent) continue;
            foreach (var (node, selected) in Walk(parent, new HashSet<string>(StringComparer.Ordinal), folders))
            {
                var names = patches.Where(patch => selected.Contains(PurePosixDir(patch))).ToList();
                if (names.Count == 0) continue;
                result.Add(new MmdSourceOption((result.Count + 1).ToString(CultureInfo.InvariantCulture),
                    MmdJson.Text(node, "Name") ?? "动作选项", names));
            }
        }
        if (result.Count == 0) throw new InvalidDataException("来源 manifest 的选项没有引用动作补丁。");
        return result;
    }

    /// <summary>上游 <c>source_io.title</c>：裁掉“替换 xxx”与标题末尾的 ▲。</summary>
    public static string Title(IasValue.Object meta)
    {
        var name = MmdJson.Text(meta, "Name") ?? string.Empty;
        var head = ReplacementSplit.Split(name, 2)[0].Trim();
        return head.EndsWith(" ▲", StringComparison.Ordinal) ? head[..^2] : head;
    }

    public static string? ChooseSourceIcon(IReadOnlyDictionary<string, byte[]> members, string? requested)
    {
        var images = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in members.Keys)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (ImageExtensions.Contains(extension)) images[name.ToLowerInvariant()] = name;
        }
        if (images.TryGetValue("icon.png", out var canonical)) return canonical;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var normalized = requested.Replace('\\', '/').ToLowerInvariant();
            if (images.TryGetValue(normalized, out var declared)) return declared;
        }
        var roots = images.Values.Where(name => PurePosixDir(name).Length == 0).ToList();
        foreach (var extension in ImageExtensions)
        {
            var candidate = roots.FirstOrDefault(name =>
                string.Equals(Path.GetFileNameWithoutExtension(name) + extension, "icon" + extension, StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) return candidate;
        }
        return roots.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    private static IEnumerable<(IasValue.Object Node, HashSet<string> Selected)> Walk(
        IasValue.Object node, HashSet<string> inherited, HashSet<string> folders)
    {
        var include = IncludePaths(node);
        var own = folders.Where(folder => include.Any(path => Inside(folder, path)))
            .ToHashSet(StringComparer.Ordinal);
        var children = MmdJson.Get(node, "SubOptions") as IasValue.Array;
        if (children is { Items.Count: > 0 })
        {
            var childPaths = children.Items.OfType<IasValue.Object>().SelectMany(OptionPaths).ToList();
            var common = own.Where(folder => !childPaths.Any(path => Inside(folder, path)))
                .ToHashSet(StringComparer.Ordinal);
            var merged = new HashSet<string>(inherited, StringComparer.Ordinal);
            merged.UnionWith(common);
            foreach (var child in children.Items.OfType<IasValue.Object>())
                foreach (var result in Walk(child, merged, folders)) yield return result;
        }
        else
        {
            var selected = new HashSet<string>(inherited, StringComparer.Ordinal);
            selected.UnionWith(own);
            yield return (node, selected);
        }
    }

    private static IEnumerable<string> OptionPaths(IasValue.Object node)
        => IncludePaths(node).Concat(
            (MmdJson.Get(node, "SubOptions") as IasValue.Array)?.Items.OfType<IasValue.Object>()
                .SelectMany(OptionPaths) ?? []);

    private static IEnumerable<string> IncludePaths(IasValue.Object node)
        => (MmdJson.Get(node, "Include") as IasValue.Array)?.Items.OfType<IasValue.Text>().Select(item => item.Value) ?? [];

    private static bool Inside(string folder, string path)
    {
        if (path.Length == 0 || path == ".") return true;
        return folder == path || folder.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    private static int ComparePatches(string left, string right)
    {
        var compare = string.CompareOrdinal(PurePosixDir(left), PurePosixDir(right));
        if (compare != 0) return compare;
        compare = string.CompareOrdinal(Prefix16(left), Prefix16(right));
        if (compare != 0) return compare;
        return PatchNumber(left).CompareTo(PatchNumber(right));
    }

    private static string Prefix16(string name) => (name.Length <= 16 ? name : name[..16]).ToLowerInvariant();

    private static int PatchNumber(string name)
        => int.Parse(name[(name.LastIndexOf('_') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture);

    private static string PureName(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? path : path[(index + 1)..];
    }

    private static string PurePosixDir(string path)
    {
        var index = path.LastIndexOf('/');
        return index < 0 ? string.Empty : path[..index];
    }

    private static Dictionary<string, byte[]> ReadMembers(string path, CancellationToken token)
    {
        if (File.Exists(path) && PatchPattern.IsMatch(Path.GetFileName(path)))
            return ReadPatchMembers(path, token);
        if (Directory.Exists(path)) return ReadDirectory(path, token);
        return ReadArchive(path, token);
    }

    private static Dictionary<string, byte[]> ReadPatchMembers(string path, CancellationToken token)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var name = Path.GetFileName(path);
        foreach (var suffix in new[] { string.Empty, ".gpu_resources", ".stream" })
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(path + suffix)) result[name + suffix] = File.ReadAllBytes(path + suffix);
        }
        var parent = Path.GetDirectoryName(path)!;
        var manifest = Path.Combine(parent, "manifest.json");
        if (File.Exists(manifest))
        {
            result["manifest.json"] = File.ReadAllBytes(manifest);
            var icon = MmdJson.Text(MmdJson.ParseManifest(result["manifest.json"]), "IconPath");
            if (!string.IsNullOrWhiteSpace(icon))
            {
                var iconPath = Path.Combine(parent, icon.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(iconPath)) result[icon] = File.ReadAllBytes(iconPath);
            }
        }
        foreach (var image in Directory.EnumerateFiles(parent))
        {
            token.ThrowIfCancellationRequested();
            if (!ImageExtensions.Contains(Path.GetExtension(image).ToLowerInvariant())) continue;
            if (File.GetAttributes(image).HasFlag(FileAttributes.ReparsePoint)) continue;
            result[Path.GetFileName(image)] = File.ReadAllBytes(image);
        }
        return result;
    }

    private static Dictionary<string, byte[]> ReadDirectory(string path, CancellationToken token)
    {
        var root = Path.GetFullPath(path);
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     AttributesToSkip = FileAttributes.ReparsePoint
                 }))
        {
            token.ThrowIfCancellationRequested();
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("来源含链接：" + file);
            Add(result, seen, Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllBytes(file));
        }
        return result;
    }

    private static Dictionary<string, byte[]> ReadArchive(string path, CancellationToken token)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var options = new ReaderOptions
        {
            ArchiveEncoding = new ArchiveEncoding { Default = Encoding.GetEncoding(936), Password = Encoding.UTF8 }
        };
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        try
        {
            using var stream = File.OpenRead(path);
            using var archive = ArchiveFactory.OpenArchive(stream, options);
            foreach (var entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();
                var key = entry.Key ?? string.Empty;
                if (entry.IsDirectory || key.EndsWith('/')) continue;
                if (entry.IsEncrypted) throw new InvalidDataException("请选择未加密的原包。");
                var size = entry.Size;
                if (size > 2L * 1024 * 1024 * 1024) throw new InvalidDataException("原包条目过大，已拒绝：" + key);
                total += size;
                if (total > 4L * 1024 * 1024 * 1024) throw new InvalidDataException("原包解压后超过 4 GB，已拒绝。");
                using var source = entry.OpenEntryStream();
                using var buffer = new MemoryStream(size > 0 ? (int)size : 0);
                source.CopyTo(buffer);
                Add(result, seen, key, buffer.ToArray());
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new InvalidDataException("文件不是可读取的 ZIP 或 7z 压缩包；请选择 ZIP、7z 或完整模组目录。", error);
        }
        return result;
    }

    private static void Add(Dictionary<string, byte[]> result, HashSet<string> seen, string name, byte[] raw)
    {
        var key = SafePath(name);
        if (!seen.Add(key)) throw new InvalidDataException("原包有重名文件：" + key);
        result[key] = raw;
    }

    internal static string SafePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains(':') || name.Contains('\\'))
            throw new InvalidDataException("包内路径无效：" + name);
        var parts = new List<string>();
        foreach (var part in name.Split('/'))
        {
            if (part.Length == 0 || part == ".") continue;
            if (part == "..") throw new InvalidDataException("包内路径无效：" + name);
            if (part.EndsWith('.') || part.EndsWith(' ')
                || ReservedNames.Contains(part.Split('.')[0]))
                throw new InvalidDataException("包内 Windows 文件名无效：" + name);
            parts.Add(part);
        }
        if (parts.Count == 0) throw new InvalidDataException("包内路径无效：" + name);
        return string.Join('/', parts);
    }
}

/// <summary>上游 <c>uuid.uuid5(NAMESPACE_URL, ...)</c> 与摘要工具。</summary>
public static class MmdIdentity
{
    private static readonly Guid NamespaceUrl = Guid.Parse("6ba7b811-9dad-11d1-80b4-00c04fd430c8");

    public static string Uuid5(string name)
    {
        var hash = SHA1.HashData(NamespaceUrl.ToByteArray(bigEndian: true)
            .Concat(Encoding.UTF8.GetBytes(name)).ToArray());
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
    }

}
