using System.Text.RegularExpressions;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.ModelConversion;

namespace HD2Mimnt.Application.Services;

/// <summary>HD2CL（角色库）原生适配：把 HD2IAS 模组编译为独立角色库模型并自动入库。</summary>
public sealed class Hd2ClConversionService : IHd2ClConversionService
{
    private static readonly Regex PatchName = new("[0-9a-fA-F]{16}\\.patch_\\d+\\z", RegexOptions.Compiled);
    private readonly ModService _mods;
    private readonly ProfileService _profiles;
    private readonly SettingsService _settings;

    public Hd2ClConversionService(ModService mods, ProfileService profiles, SettingsService settings)
    {
        _mods = mods;
        _profiles = profiles;
        _settings = settings;
    }

    public async Task<IReadOnlyList<Hd2ClCandidate>> GetCandidatesAsync(CancellationToken token = default)
    {
        var mods = (await _mods.GetAllModsAsync(token)).ToList();
        return await Task.Run(() =>
        {
            var result = new List<Hd2ClCandidate>();
            foreach (var mod in mods)
            {
                token.ThrowIfCancellationRequested();
                if (!Directory.Exists(mod.Path)) continue;
                var companions = 0;
                foreach (var folder in ComponentFolders(mod.Path))
                {
                    if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(IsCompanionFile))
                        companions++;
                }
                if (companions > 0) result.Add(new Hd2ClCandidate(mod.Id, mod.Name, mod.Path, companions));
            }
            return (IReadOnlyList<Hd2ClCandidate>)result;
        }, token);
    }

    public async Task<Hd2ClConversionResult> ConvertAsync(Guid modId, IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        var mod = await _mods.GetModByIdAsync(modId, token)
                  ?? throw new InvalidOperationException("模组不存在：" + modId);
        var gameRoot = _settings.GetEffectiveGamePath();
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new InvalidOperationException("未检测到游戏安装目录，无法读取原生材质与骨架。");
        var dataRoot = Path.Combine(gameRoot, "data");
        var gameData = Directory.Exists(dataRoot) ? dataRoot : gameRoot;
        progress?.Report("扫描模组组件…");
        var scan = await Task.Run(() => Scan(mod.Path, token), token);
        IasBinary.Require(scan.Companions.Count > 0,
            "该模组没有找到 IAS 配套（rigbin/hd2phys/hd2shared），无法生成角色库模型。");
        IasBinary.Require(scan.Resources.Keys.Any(key => key.Type == IasResourceTypes.Unit),
            "该模组没有找到模型 Unit，无法生成角色库模型。");
        var seed = mod.Manifest is { Guid: var guid } && guid != Guid.Empty ? guid.ToString() : mod.Name;
        var set = IasCompanionSets.Read(scan.Companions, seed);
        var work = Path.Combine(Path.GetTempPath(), "HD2Mimnt-HD2CL", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            using var game = new IasGameData(gameData);
            progress?.Report("编译私有模型、目录与物理数据…");
            var compiled = await Task.Run(() => IasModelCompiler.Compile(new IasCompileRequest(
                "ias_" + set.Data.Metadata.Id, mod.Name, scan.Resources, set.Members, set.Data, set.Routing, game)),
                token);
            var icon = FindIcon(mod, out var iconPath, out var iconMime);
            progress?.Report("生成安装包…");
            var packaged = IasModelPackager.Build(new IasPackageRequest("ias_" + set.Data.Metadata.Id,
                compiled.Name, mod.Author, mod.Description,
                iconPath, icon, iconMime, compiled, scan.Resources, game,
                scan.ArchiveIds.Concat(set.Routing.BodyOwners.Select(owner => owner.Archive))
                    .Concat(set.Data.Metadata.Members.Select(member => member.Archive))
                    .Select(archive => archive.ToLowerInvariant().StartsWith("0x", StringComparison.Ordinal)
                        ? archive[2..] : archive.ToLowerInvariant()).Distinct().ToList()));
            var archive = Path.Combine(work, SafeFileName(packaged.Name) + "_角色库.7z");
            await IasModelPackager.WriteArchiveAsync(archive, packaged.Members, token);
            progress?.Report("导入管理器库…");
            var imported = await _mods.ImportModAsync(archive, token);
            Guid? profileId = null;
            var sourceDisabled = false;
            var profile = await _profiles.GetActiveProfileAsync(token);
            if (profile is not null)
            {
                profileId = profile.Id;
                await _profiles.AddModToProfileAsync(profile.Id, imported, token);
                await _profiles.SetModEnabledAsync(profile.Id, imported, true, token);
                await _profiles.SetModEnabledAsync(profile.Id, modId, false, token);
                sourceDisabled = true;
            }
            progress?.Report(profile is null
                ? "已导入模组库（当前没有配置文件，未禁用源模组）"
                : "已导入、加入当前配置并禁用源 IAS 模组");
            return new Hd2ClConversionResult(imported, packaged.Name, profileId, sourceDisabled);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        }
    }

    private static string SafeFileName(string name)
    {
        var cleaned = Regex.Replace(name, "[<>:\"/\\\\|?*\\x00-\\x1f]", "_").Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "HD2CL" : cleaned;
    }

    private static byte[]? FindIcon(Mod mod, out string? iconPath, out string? mime)
    {
        iconPath = null;
        mime = null;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(mod.ThumbnailPath) && File.Exists(mod.ThumbnailPath)) candidates.Add(mod.ThumbnailPath);
        if (!string.IsNullOrWhiteSpace(mod.Manifest?.IconPath))
        {
            var local = Path.Combine(mod.Path, mod.Manifest.IconPath);
            if (File.Exists(local)) candidates.Add(local);
        }
        var file = candidates.FirstOrDefault(path => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp");
        if (file is null) return null;
        var extension = Path.GetExtension(file).ToLowerInvariant();
        iconPath = "icon" + extension;
        mime = extension switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "image/webp"
        };
        return File.ReadAllBytes(file);
    }

    private sealed record ScanResult(
        Dictionary<(ulong Type, ulong Id), IasResource> Resources,
        List<IReadOnlyDictionary<string, byte[]>> Companions,
        HashSet<string> ArchiveIds);

    private static ScanResult Scan(string root, CancellationToken token)
    {
        var resources = new Dictionary<(ulong Type, ulong Id), IasResource>();
        var companions = new List<IReadOnlyDictionary<string, byte[]>>();
        var archives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in ComponentFolders(root))
        {
            token.ThrowIfCancellationRequested();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                files[Path.GetRelativePath(folder, file).Replace('\\', '/')] = File.ReadAllBytes(file);
            if (files.Count == 0) continue;
            if (files.Any(entry => IsCompanionFile(entry.Key))) companions.Add(files);
            foreach (var name in files.Keys.Where(name => !name.EndsWith(".gpu_resources") && !name.EndsWith(".stream")
                                                          && PatchName.IsMatch(Path.GetFileName(name))).Order(StringComparer.Ordinal))
            {
                files.TryGetValue(name + ".gpu_resources", out var gpu);
                files.TryGetValue(name + ".stream", out var stream);
                archives.Add(Path.GetFileName(name)[..16].ToLowerInvariant());
                foreach (var (key, value) in IasArchive.ReadEntries(files[name], gpu ?? [], stream ?? []))
                {
                    if (resources.TryGetValue(key, out var existing))
                        IasBinary.Require(existing.Toc.SequenceEqual(value.Toc) && existing.Gpu.SequenceEqual(value.Gpu)
                                          && existing.Stream.SequenceEqual(value.Stream), "模组出现冲突的重复资源。");
                    resources[key] = value;
                }
            }
        }
        return new ScanResult(resources, companions, archives);
    }

    private static IEnumerable<string> ComponentFolders(string root)
    {
        var components = Path.Combine(root, "components");
        if (Directory.Exists(components))
        {
            return Directory.EnumerateDirectories(components, "*", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
        }
        var folders = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .Where(folder => !Path.GetFileName(folder).Equals("components", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Any()) folders.Add(root);
        return folders;
    }

    private static bool IsCompanionFile(string name)
        => name.EndsWith(".rigbin", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".hd2phys", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".hd2shared", StringComparison.OrdinalIgnoreCase)
           || name.EndsWith(".hd2pose", StringComparison.OrdinalIgnoreCase)
           || Path.GetFileName(name).Equals("mod_group.json", StringComparison.OrdinalIgnoreCase);
}
