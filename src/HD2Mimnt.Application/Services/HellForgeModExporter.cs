using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Application.Services;

internal enum HellForgeModKind
{
    /// <summary>干净 V1 清单原样作为 cosmetic pack 透传，HellForge Mods 页保留选项面板。</summary>
    Pack,
    /// <summary>HD2Mimnt 解析选项并按 bundle 重编号，导出 hellforge-mod.json + data/ 文件夹模组。</summary>
    Flat
}

internal sealed record HellForgeModExport(HellForgeModKind Kind, string ManifestPath, string Fingerprint);

/// <summary>
/// 把库文件放进导出目录的方式：默认文件复制，同步可传入按设置中的部署方式（硬链接/符号链接）写入的实现。
/// 链接只能做文件级——HellForge 删除模组时会拒绝落在链接目录下的路径（LocalInstall.NoLinks）。
/// </summary>
internal delegate Task HellForgeFilePlace(string sourcePath, string destinationPath, CancellationToken cancellationToken);

/// <summary>
/// 把 HD2Mimnt 库模组导出成 HellForge 认可的形态（直接写入 mods\ 下的临时目录，成功后整体改名）。
/// 干净 V1（完整 Include 映射、无根补丁、Include 目录齐全）透传为 cosmetic pack，保留作者 manifest 与选项；
/// 其余（legacy、缺映射、根补丁、无选项）扁平化为 hellforge-mod.json + data/，用 HD2Mimnt 自己的选项解析与
/// 按 bundle 从 0 重编号保证“HellForge 暂存的内容/顺序 == 官方部署”。HellForge 会话会再次按 bundle 接在
/// 游戏已有补丁之后重编号，因此导出只需保住模组内部相对顺序。不写 game\data。
/// </summary>
internal static class HellForgeModExporter
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static HellForgeModKind ResolveKind(Mod mod) => TryQualifyAsPack(mod, out _) ? HellForgeModKind.Pack : HellForgeModKind.Flat;

    /// <summary>能否原样透传为 cosmetic pack；不能时给出扁平化原因（供诊断/测试）。</summary>
    public static bool TryQualifyAsPack(Mod mod, out string reason)
    {
        reason = string.Empty;
        var manifest = mod.Manifest;
        if (manifest == null) { reason = "无清单"; return false; }
        if (manifest.ManifestVersion != ManifestVersion.V1) { reason = "非 V1 清单"; return false; }
        if (manifest.Options is not { Count: > 0 } options) { reason = "无选项"; return false; }

        foreach (var option in options)
        {
            if (option.Include == null && option.SubOptions is not { Count: > 0 })
            { reason = $"选项“{option.Name}”缺 Include 映射"; return false; }
            if (option.SubOptions != null)
                foreach (var sub in option.SubOptions)
                    if (sub.Include == null)
                    { reason = $"子选项“{sub.Name}”缺 Include 映射"; return false; }
        }

        // HellForge 总是先暂存根补丁；HD2Mimnt 仅在 Include 含 "." 时才部署根目录，二者会错位。
        foreach (var file in Directory.EnumerateFiles(mod.Path, "*", SearchOption.TopDirectoryOnly))
            if (PatchDeploymentPlanner.TryParseTarget(file, out _, out _, out _))
            { reason = "根目录含补丁"; return false; }

        foreach (var relative in PackIncludeDirectories(mod))
            if (!Directory.Exists(Path.Combine(mod.Path, relative)))
            { reason = $"Include 目录缺失：{relative}"; return false; }

        return true;
    }

    public static Task<HellForgeModExport> ExportAsync(Mod mod, string? optionsJson,
        string exportDirectory, CancellationToken cancellationToken = default)
        => ExportAsync(mod, optionsJson, exportDirectory, null, cancellationToken);

    /// <summary>导出到 exportDirectory；place 为 null 时文件复制，否则按传入方式（硬链接/符号链接）落盘。</summary>
    public static Task<HellForgeModExport> ExportAsync(Mod mod, string? optionsJson,
        string exportDirectory, HellForgeFilePlace? place, CancellationToken cancellationToken = default)
        => ExportAsync(mod, optionsJson, exportDirectory, place, null, cancellationToken);

    /// <summary>同上，kind 用于强制导出形态（同 id 的外观包降级为扁平导出时由调用方指定）。</summary>
    public static async Task<HellForgeModExport> ExportAsync(Mod mod, string? optionsJson,
        string exportDirectory, HellForgeFilePlace? place, HellForgeModKind? kind,
        CancellationToken cancellationToken = default)
    {
        kind ??= ResolveKind(mod);
        if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, true);
        Directory.CreateDirectory(exportDirectory);
        place ??= CopyFileAsync;

        string manifestPath;
        if (kind == HellForgeModKind.Pack)
        {
            foreach (var (relative, absolute) in EnumeratePackFiles(mod))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(exportDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await place(absolute, destination, cancellationToken);
            }
            manifestPath = Path.Combine(exportDirectory, "manifest.json");
            // 总是按 HD2Mimnt 的清单合成 pack 清单：复合包（多个子包合并）根目录本来就没有 manifest.json，
            // 只搬 Include 目录会让 HellForge 既看不到清单也看不到根补丁，静默忽略整个目录；
            // 且 HellForge 只认 "Options"/"Guid" 这两个精确键名。先删再写，避免命中硬链接写穿库文件。
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            await File.WriteAllTextAsync(manifestPath, BuildPackManifest(mod, exportDirectory),
                new UTF8Encoding(false), cancellationToken);
        }
        else
        {
            manifestPath = await ExportFlatAsync(mod, optionsJson, exportDirectory, place, cancellationToken);
        }

        var fingerprint = ComputeFingerprint(mod, optionsJson, kind.Value, cancellationToken);
        return new HellForgeModExport(kind.Value, manifestPath, fingerprint);
    }

    private static async Task<string> ExportFlatAsync(Mod mod, string? optionsJson,
        string exportDirectory, HellForgeFilePlace place, CancellationToken cancellationToken)
    {
        var selected = PatchDeploymentPlanner.CollectSelectedFiles(mod, optionsJson, cancellationToken);
        if (selected.Count == 0)
            throw new InvalidDataException($"模组“{mod.Name}”没有可部署的补丁。");
        // 空编号表：每个 bundle 从 0 起按 HD2Mimnt 有效顺序连续编号，HellForge 按 (bundle, 编号) 排序即可复现。
        var entries = PatchDeploymentPlanner.BuildFiles(selected, new Dictionary<string, int>(PathComparer),
            cancellationToken, mod.Path);

        var dataDirectory = Path.Combine(exportDirectory, "data");
        Directory.CreateDirectory(dataDirectory);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await place(entry.SourcePath!, Path.Combine(dataDirectory, entry.FileName), cancellationToken);
        }

        var icon = await TryStageIconAsync(mod, exportDirectory, place, cancellationToken);
        var manifest = new
        {
            format = 1,
            id = FlatModId(mod),
            name = Truncate(string.IsNullOrWhiteSpace(mod.Name) ? mod.Id.ToString("N") : mod.Name, 80),
            version = Truncate(string.IsNullOrWhiteSpace(mod.Version) ? "1.0.0" : mod.Version, 32),
            description = mod.Description ?? string.Empty,
            gameplay = true,
            icon
        };
        var manifestPath = Path.Combine(exportDirectory, "hellforge-mod.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest), cancellationToken);
        return manifestPath;
    }

    /// <summary>扁平化模组 id：hd2mimnt.&lt;modId N 格式&gt;（41 字符，自描述归属，合法 id）。</summary>
    public static string FlatModId(Mod mod) => "hd2mimnt." + mod.Id.ToString("N");

    /// <summary>
    /// HellForge 会为这个模组算出的 id：扁平化为 hd2mimnt.&lt;modId N&gt;；透传 pack 为 pack.&lt;Guid&gt;，
    /// 清单无 Guid 时 pack.&lt;名称 slug&gt;（与上游 CosmeticPacks.Parse 一致）。只用于挑目录名与目录去重，
    /// 同步结束后仍以 <c>mods</c> 列表的实际 id 为准。
    /// </summary>
    public static string ResolveModId(Mod mod, HellForgeModKind kind)
    {
        if (kind == HellForgeModKind.Flat) return FlatModId(mod);
        var guid = mod.Manifest?.Guid ?? Guid.Empty;
        return guid != Guid.Empty ? "pack." + guid.ToString("D") : "pack." + PackSlug(mod.Name);
    }

    /// <summary>与上游一致的 pack 名称 slug：小写、[a-z0-9-] 之外的字符变 '-'，去首尾 '-'，空则 pack，最长 58。</summary>
    private static string PackSlug(string? name)
    {
        var builder = new StringBuilder((name ?? string.Empty).Length);
        foreach (var character in (name ?? string.Empty).ToLowerInvariant())
            builder.Append(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' ? character : '-');
        var slug = builder.ToString().Trim('-');
        if (slug.Length == 0) return "pack";
        return slug.Length > 58 ? slug[..58].TrimEnd('-') : slug;
    }

    /// <summary>
    /// 按 HD2Mimnt 清单合成 HellForge 认得的 pack 清单：键名与上游 CosmeticPacks.ReadManifest 完全一致
    /// （"Options"/"Guid" 大小写敏感）。只写导出目录里真实存在的图片路径，避免 HellForge 指向不存在的文件。
    /// </summary>
    private static string BuildPackManifest(Mod mod, string exportDirectory)
    {
        var manifest = mod.Manifest!;
        var root = new JsonObject
        {
            ["Version"] = 1,
            ["Guid"] = manifest.Guid.ToString("D"),
            ["Name"] = string.IsNullOrWhiteSpace(manifest.Name) ? mod.Name : manifest.Name,
            ["Description"] = manifest.Description ?? string.Empty
        };
        if (ExistingRelative(exportDirectory, manifest.IconPath) is { } icon) root["IconPath"] = icon;

        var source = manifest.Options ?? [];
        var names = PackOptionNames(source);
        var options = new JsonArray();
        for (var i = 0; i < source.Count; i++) options.Add(OptionNode(source[i], names[i], exportDirectory));
        root["Options"] = options;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// 导出清单里的选项名（与 <paramref name="options"/> 一一对应）。HellForge 用选项名做“关闭集合”与“变体选择”的键
    /// （<c>CosmeticPacks.Choice.Off</c> 是 HashSet、<c>Sub</c> 是 Dictionary），重名会互相覆盖；复合包合并后
    /// 各子包的选项平铺到同一层，重名很常见，所以重复出现的名字追加序号（“本体”“本体 (2)”）。
    /// 空名也补一个占位名——HellForge 读到无名选项会直接判整个包无效。
    /// </summary>
    public static IReadOnlyList<string> PackOptionNames(IReadOnlyList<ModOption>? options)
        => UniqueNames(options?.Select(option => option.Name) ?? [], "选项");

    /// <summary>某个选项的变体名（与 <c>option.SubOptions</c> 一一对应），重名同样追加序号。</summary>
    public static IReadOnlyList<string> PackSubOptionNames(ModOption option)
        => UniqueNames(option.SubOptions?.Select(sub => sub.Name) ?? [], "变体");

    private static List<string> UniqueNames(IEnumerable<string?> raw, string fallback)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var value in raw)
        {
            var baseName = string.IsNullOrWhiteSpace(value) ? $"{fallback} {names.Count + 1}" : value.Trim();
            var name = baseName;
            for (var n = 2; !used.Add(name); n++) name = $"{baseName} ({n})";
            names.Add(name);
        }
        return names;
    }

    private static JsonObject OptionNode(ModOption option, string name, string exportDirectory)
    {
        var node = new JsonObject { ["Name"] = name };
        if (!string.IsNullOrWhiteSpace(option.Description)) node["Description"] = option.Description;
        if (ExistingRelative(exportDirectory, option.Image) is { } image) node["Image"] = image;
        if (option.Include != null) node["Include"] = ToRelativeArray(option.Include);
        if (option.SubOptions is { Count: > 0 })
        {
            var names = PackSubOptionNames(option);
            var subs = new JsonArray();
            for (var i = 0; i < option.SubOptions.Count; i++)
                subs.Add(SubOptionNode(option.SubOptions[i], names[i], exportDirectory));
            node["SubOptions"] = subs;
        }
        return node;
    }

    private static JsonObject SubOptionNode(ModSubOption option, string name, string exportDirectory)
    {
        var node = new JsonObject { ["Name"] = name };
        if (!string.IsNullOrWhiteSpace(option.Description)) node["Description"] = option.Description;
        if (ExistingRelative(exportDirectory, option.Image) is { } image) node["Image"] = image;
        if (option.Include != null) node["Include"] = ToRelativeArray(option.Include);
        if (option.SubOptions is { Count: > 0 })
        {
            var names = UniqueNames(option.SubOptions.Select(sub => sub.Name), "变体");
            var subs = new JsonArray();
            for (var i = 0; i < option.SubOptions.Count; i++)
                subs.Add(SubOptionNode(option.SubOptions[i], names[i], exportDirectory));
            node["SubOptions"] = subs;
        }
        return node;
    }

    private static JsonArray ToRelativeArray(IEnumerable<string> paths)
    {
        var array = new JsonArray();
        foreach (var path in paths)
            if (!string.IsNullOrWhiteSpace(path)) array.Add(path.Replace('\\', '/'));
        return array;
    }

    /// <summary>导出目录里存在的相对路径（统一成 / 分隔），不在导出目录内或文件不存在时为 null。</summary>
    private static string? ExistingRelative(string exportDirectory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(exportDirectory));
        var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));
        if (!PathGuard.IsInsideRoot(root, absolute) || !File.Exists(absolute)) return null;
        return Path.GetRelativePath(root, absolute).Replace('\\', '/');
    }

    private static async Task<string?> TryStageIconAsync(Mod mod, string exportDirectory,
        HellForgeFilePlace place, CancellationToken cancellationToken)
    {
        var thumbnail = mod.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(thumbnail) || !File.Exists(thumbnail)) return null;
        var extension = Path.GetExtension(thumbnail).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
        if (!PathGuard.IsInsideRoot(root, Path.GetFullPath(thumbnail))) return null;
        var name = "icon" + extension;
        await place(thumbnail, Path.Combine(exportDirectory, name), cancellationToken);
        return name;
    }

    /// <summary>
    /// 导出布局版本：清单写法/文件组织一改就要 +1，否则已同步过的模组会因指纹未变被跳过，
    /// 旧的（可能不完整的）导出目录会一直留着。旧版本号算出的指纹与新版不同，下次同步会自动重导一次。
    /// </summary>
    private const int ExportFormat = 2;

    /// <summary>内容指纹（stat 级，廉价）：pack 与选项无关（导出内容恒定），flat 含选项快照。
    /// 与 HellForge ModStaging.Same 一样用 长度+修改时间 判断变化。</summary>
    public static string ComputeFingerprint(Mod mod, string? optionsJson, HellForgeModKind kind,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
        builder.Append("fmt").Append(ExportFormat).Append('\n');
        builder.Append(mod.Id.ToString("N")).Append('\n');
        builder.Append(mod.Version).Append('\n');
        builder.Append(kind).Append('\n');
        if (kind == HellForgeModKind.Flat) builder.Append(optionsJson ?? string.Empty).Append('\n');

        IEnumerable<string> files = kind == HellForgeModKind.Pack
            ? EnumeratePackFiles(mod).Select(item => item.Abs)
            : PatchDeploymentPlanner.CollectSelectedFiles(mod, optionsJson, cancellationToken);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists) continue;
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            builder.Append(relative).Append('|').Append(info.Length).Append('|')
                .Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>pack 导出涉及的全部文件：作者 manifest + 所有 Include 目录（含未启用选项，供 HellForge 侧切换）+ 预览图。</summary>
    private static IEnumerable<(string Rel, string Abs)> EnumeratePackFiles(Mod mod)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
        var seen = new HashSet<string>(PathComparer);
        // 根目录的 manifest.json 不搬：导出总要按 HD2Mimnt 清单重写一份（见 ExportAsync），
        // 搬过来也只会在被硬链接时写穿库文件。

        foreach (var relative in PackIncludeDirectories(mod))
        {
            var directory = Path.Combine(root, relative);
            if (!Directory.Exists(directory)) continue;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (seen.Add(rel)) yield return (rel, file);
            }
        }

        foreach (var image in PackImageFiles(mod))
        {
            var absolute = Path.Combine(root, image);
            if (!File.Exists(absolute)) continue;
            var rel = Path.GetRelativePath(root, absolute).Replace('\\', '/');
            if (seen.Add(rel)) yield return (rel, absolute);
        }
    }

    /// <summary>所有选项与子选项声明的 Include 目录（去重，相对模组根，限定根内）。</summary>
    private static List<string> PackIncludeDirectories(Mod mod)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
        var directories = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        if (mod.Manifest?.Options == null) return directories;
        foreach (var option in mod.Manifest.Options)
        {
            Collect(option.Include);
            if (option.SubOptions != null)
                foreach (var sub in option.SubOptions) Collect(sub.Include);
        }
        return directories;

        void Collect(List<string>? include)
        {
            if (include == null) return;
            foreach (var relative in include)
            {
                if (string.IsNullOrWhiteSpace(relative)) continue;
                var absolute = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));
                if (!PathGuard.IsInsideRoot(root, absolute)) continue;
                var normalized = Path.GetRelativePath(root, absolute).Replace('\\', '/');
                if (seen.Add(normalized)) directories.Add(normalized);
            }
        }
    }

    private static IEnumerable<string> PackImageFiles(Mod mod)
    {
        var manifest = mod.Manifest;
        if (manifest == null) yield break;
        if (!string.IsNullOrWhiteSpace(manifest.IconPath)) yield return manifest.IconPath!;
        if (manifest.Options == null) yield break;
        foreach (var option in manifest.Options)
        {
            if (!string.IsNullOrWhiteSpace(option.Image)) yield return option.Image!;
            if (option.SubOptions == null) continue;
            foreach (var sub in option.SubOptions)
                if (!string.IsNullOrWhiteSpace(sub.Image)) yield return sub.Image!;
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    internal static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1 << 20;
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var destinationStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);
        await sourceStream.CopyToAsync(destinationStream, bufferSize, cancellationToken);
    }
}