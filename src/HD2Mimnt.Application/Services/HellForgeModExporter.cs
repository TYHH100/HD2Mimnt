using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// 把 HD2Mimnt 库模组导出成 HellForge 认可的形态（mods\ 导入前的缓存目录）。
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

    public static async Task<HellForgeModExport> ExportAsync(Mod mod, string? optionsJson,
        string exportDirectory, CancellationToken cancellationToken = default)
    {
        var kind = ResolveKind(mod);
        if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory, true);
        Directory.CreateDirectory(exportDirectory);

        string manifestPath;
        if (kind == HellForgeModKind.Pack)
        {
            foreach (var (relative, absolute) in EnumeratePackFiles(mod))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(exportDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(absolute, destination, cancellationToken);
            }
            manifestPath = Path.Combine(exportDirectory, "manifest.json");
        }
        else
        {
            manifestPath = await ExportFlatAsync(mod, optionsJson, exportDirectory, cancellationToken);
        }

        var fingerprint = ComputeFingerprint(mod, optionsJson, kind, cancellationToken);
        return new HellForgeModExport(kind, manifestPath, fingerprint);
    }

    private static async Task<string> ExportFlatAsync(Mod mod, string? optionsJson,
        string exportDirectory, CancellationToken cancellationToken)
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
            await CopyFileAsync(entry.SourcePath!, Path.Combine(dataDirectory, entry.FileName), cancellationToken);
        }

        var icon = TryStageIcon(mod, exportDirectory);
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

    private static string? TryStageIcon(Mod mod, string exportDirectory)
    {
        var thumbnail = mod.ThumbnailPath;
        if (string.IsNullOrWhiteSpace(thumbnail) || !File.Exists(thumbnail)) return null;
        var extension = Path.GetExtension(thumbnail).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) return null;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
        if (!PathGuard.IsInsideRoot(root, Path.GetFullPath(thumbnail))) return null;
        var name = "icon" + extension;
        File.Copy(thumbnail, Path.Combine(exportDirectory, name), true);
        return name;
    }

    /// <summary>内容指纹（stat 级，廉价）：pack 与选项无关（导出内容恒定），flat 含选项快照。
    /// 与 HellForge ModStaging.Same 一样用 长度+修改时间 判断变化。</summary>
    public static string ComputeFingerprint(Mod mod, string? optionsJson, HellForgeModKind kind,
        CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();
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
        var manifest = Path.Combine(root, "manifest.json");
        if (File.Exists(manifest) && seen.Add("manifest.json"))
            yield return ("manifest.json", manifest);

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

    private static async Task CopyFileAsync(string source, string destination, CancellationToken cancellationToken)
    {
        const int bufferSize = 1 << 20;
        await using var sourceStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        await using var destinationStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize, FileOptions.Asynchronous);
        await sourceStream.CopyToAsync(destinationStream, bufferSize, cancellationToken);
    }
}