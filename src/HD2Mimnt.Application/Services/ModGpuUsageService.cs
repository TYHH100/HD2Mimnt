using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IO;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Application.Services;

public readonly record struct GpuResourceBytes(long TextureBytes, long ModelBytes, long OtherBytes)
{
    public long TotalBytes => checked(TextureBytes + ModelBytes + OtherBytes);
    public static GpuResourceBytes operator +(GpuResourceBytes left, GpuResourceBytes right)
        => new(checked(left.TextureBytes + right.TextureBytes), checked(left.ModelBytes + right.ModelBytes),
            checked(left.OtherBytes + right.OtherBytes));
}

public sealed record ModGpuUsage(Guid ModId, GpuResourceBytes Bytes, int ResourceCount, IReadOnlyList<string> Warnings)
{
    public bool IsComplete => Warnings.Count == 0;
}

public sealed record ProfileGpuUsage(Guid ProfileId, ModGpuUsage Total,
    IReadOnlyDictionary<Guid, ModGpuUsage> Mods, IReadOnlyDictionary<Guid, GpuResourceBytes> EffectiveBytes);

/// <summary>统计所选资源的静态载荷；不是运行时驻留显存，也不是相对原版的显存增量。</summary>
public sealed class ModGpuUsageService(IProfileRepository profiles, IModRepository mods, SettingsService? settings = null)
{
    private const ulong TextureType = 0xcd4238c6a0c69e32;
    private const ulong UnitType = 0xe0a48d0be9a7453f;
    private readonly PatchResourceIndexCache _cache = new(readTextureMetadata: true);
    private sealed record Resource(Guid ModId, GpuResourceBytes Bytes);
    private sealed record Scan(ModGpuUsage Usage, Dictionary<(ulong Type, ulong Id), Resource> Resources);

    public Task<IReadOnlyDictionary<Guid, ModGpuUsage>> ScanModsAsync(IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        var requested = ids.Distinct().ToArray();
        return Task.Run<IReadOnlyDictionary<Guid, ModGpuUsage>>(async () =>
        {
            var sources = (await mods.GetByIdsAsync(requested, cancellationToken)).ToDictionary(mod => mod.Id);
            var result = new Dictionary<Guid, ModGpuUsage>();
            foreach (var id in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result[id] = sources.TryGetValue(id, out var mod) ? ScanMod(mod, null, cancellationToken).Usage
                    : new(id, default, 0, ["找不到模组"]);
            }
            return result;
        }, cancellationToken);
    }

    public Task<ProfileGpuUsage> ScanProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
        => Task.Run(async () =>
        {
            var profile = await profiles.GetByIdAsync(profileId, cancellationToken)
                ?? throw new InvalidOperationException($"配置 {profileId} 不存在");
            var sources = (await mods.GetByIdsAsync(profile.Mods.Select(item => item.ModId), cancellationToken))
                .ToDictionary(mod => mod.Id);
            foreach (var mod in sources.Values)
                await BslProfilePolicy.EnsureDetectedAsync(mod, mods, cancellationToken);
            var direction = settings?.GetSetting(SettingKeys.DefaultDeploymentOrder, DeploymentOrder.TopToBottom.ToString());
            var sequence = BslProfilePolicy.DeploymentSequence(profile, sources,
                direction == DeploymentOrder.BottomToTop.ToString() ? DeploymentOrder.BottomToTop : DeploymentOrder.TopToBottom,
                settings?.IsLoaderOrderLocked() ?? true);
            var scans = new Dictionary<Guid, Scan>();
            // 禁用项保留参考值，但不参与最终资源覆盖和总计。
            foreach (var item in profile.Mods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scans[item.ModId] = sources.TryGetValue(item.ModId, out var mod)
                    ? ScanMod(mod, item.OptionsJson, cancellationToken)
                    : new(new(item.ModId, default, 0, ["找不到模组"]), new());
            }
            var final = new Dictionary<(ulong Type, ulong Id), Resource>();
            var warnings = new List<string>();
            foreach (var item in sequence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scan = scans[item.ModId];
                warnings.AddRange(scan.Usage.Warnings);
                foreach (var entry in scan.Resources) final[entry.Key] = entry.Value;
            }
            var total = default(GpuResourceBytes);
            var effective = new Dictionary<Guid, GpuResourceBytes>();
            foreach (var resource in final.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += resource.Bytes;
                effective[resource.ModId] = effective.GetValueOrDefault(resource.ModId) + resource.Bytes;
            }
            return new ProfileGpuUsage(profileId, new(Guid.Empty, total, final.Count, warnings),
                scans.ToDictionary(pair => pair.Key, pair => pair.Value.Usage), effective);
        }, cancellationToken);

    private Scan ScanMod(Mod mod, string? optionsJson, CancellationToken token)
    {
        var warnings = new List<string>();
        var resources = new Dictionary<(ulong Type, ulong Id), Resource>();
        try
        {
            token.ThrowIfCancellationRequested();
            if (!Directory.Exists(mod.Path)) throw new DirectoryNotFoundException("模组目录不可访问");
            var plan = ProfileModOptionResolver.ResolveDeployPlan(mod.Manifest,
                ProfileModOptionResolver.ResolveEffective(mod.Manifest, optionsJson));
            if (plan.Scope == OptionDeployScope.InvalidSelection) throw new InvalidDataException("模组选项无效");
            if (plan.Scope == OptionDeployScope.DirectoryList)
                foreach (var relative in plan.Directories)
                {
                    var directory = Path.GetFullPath(Path.Combine(mod.Path, relative));
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mod.Path));
                    if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase) && !PathGuard.IsInsideRoot(root, directory))
                        throw new InvalidDataException("选项目录超出模组目录");
                    if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"选项目录不存在：{relative}");
                }
            var files = PatchDeploymentPlanner.BuildFiles(PatchDeploymentPlanner.CollectSelectedFiles(mod, optionsJson, token),
                new(StringComparer.OrdinalIgnoreCase), token);
            if (files.Count == 0 && (plan.Scope != OptionDeployScope.DirectoryList || plan.Directories.Count > 0))
                warnings.Add("所选目录中未找到可统计的 Stingray 补丁，请检查清单映射或资源类型");
            foreach (var file in files.Where(file => file.SourcePath != null
                && PatchDeploymentPlanner.TryParseTarget(file.FileName, out _, out _, out var suffix) && suffix.Length == 0))
            {
                token.ThrowIfCancellationRequested();
                var path = file.SourcePath!;
                try
                {
                    var entries = _cache.Read(path, token, out _);
                    var gpuLength = GetLength(path + ".gpu_resources");
                    var streamLength = GetLength(path + ".stream");
                    foreach (var entry in entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!Fits(entry.GpuOffset, entry.GpuSize, gpuLength)
                            || !Fits(entry.StreamOffset, entry.StreamSize, streamLength))
                            warnings.Add($"{Path.GetFileName(path)} · 0x{entry.ResourceId:X16}：伴随数据缺失或超出范围");
                        var bytes = new GpuResourceBytes(0, 0, entry.GpuSize);
                        if (entry.TypeId == UnitType) bytes = new(0, entry.GpuSize, 0);
                        else if (entry.TypeId == TextureType)
                        {
                            var declared = (long)entry.GpuSize + entry.StreamSize;
                            var texture = entry.TextureMemory;
                            bytes = new(texture?.Bytes ?? declared, 0, 0);
                            var inlineBytes = texture is { } info
                                ? Math.Max(0L, (long)entry.DataSize - DdsTextureMemoryReader.TextureHeaderOffset - info.HeaderBytes) : 0;
                            if (texture == null || texture.Value.Bytes != declared + inlineBytes)
                                warnings.Add($"{Path.GetFileName(path)} · 0x{entry.ResourceId:X16}：纹理格式未确认或载荷长度不匹配");
                        }
                        // 保留零 GPU 字节的资源，它也可能覆盖前一个版本。
                        resources[(entry.TypeId, entry.ResourceId)] = new(mod.Id, bytes);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    warnings.Add($"{Path.GetFileName(path)}：{ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            warnings.Add(ex.Message);
        }
        var total = default(GpuResourceBytes);
        foreach (var resource in resources.Values) total += resource.Bytes;
        return new(new(mod.Id, total, resources.Count, warnings), resources);
    }

    private static long GetLength(string path) => File.Exists(path) ? new FileInfo(path).Length : -1;
    private static bool Fits(ulong offset, uint size, long length)
        => size == 0 || length >= 0 && offset <= (ulong)length && size <= (ulong)length - offset;
}
