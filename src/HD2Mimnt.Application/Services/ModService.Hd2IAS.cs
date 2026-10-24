using System.Globalization;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Infrastructure.Hd2IAS;

namespace HD2Mimnt.Application.Services;

public partial class ModService
{
    private readonly SemaphoreSlim _hd2IASDataGate = new(1, 1);

    public async Task<(bool HasMod, bool HasFiles)> GetHd2IASDataStateAsync(CancellationToken token = default)
    {
        var hasMod = false;
        foreach (var mod in await GetAllModsAsync(token))
        {
            try
            {
                if (!await IsHd2IASDataAsync(mod, "mods/hd2ir/runtime", "70217982548cf034", token)) continue;
                hasMod = true;
                if (await HasHd2IASPatchAsync(mod.Path, "mods/hd2ir/runtime", "70217982548cf034", token)) return (true, true);
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            { /* 无法读取的普通模组不应打断运行时缺失检测，也不能按名称认作系统 data。 */ }
        }
        return (hasMod, false);
    }

    /// <summary>生成的系统 data 与普通模组共用导入、原位更新及配置引用语义。</summary>
    public async Task<Mod> UpsertHd2IASDataAsync(string directory, string module, string resourceId,
        CancellationToken token = default)
    {
        await _hd2IASDataGate.WaitAsync(token);
        try
        {
            var matches = new List<Mod>();
            foreach (var mod in await GetAllModsAsync(token))
                if (await IsHd2IASDataAsync(mod, module, resourceId, token)) matches.Add(mod);
            if (matches.Count > 1)
                throw new InvalidOperationException("模组库存在多份相同模块的 HD2IAS data，请先保留一份后再更新。");
            if (matches.Count == 1)
                return (await ReplaceArchiveCoreAsync(matches[0].Id, directory, null, null, token, null, null,
                    forceReplacement: !await HasHd2IASPatchAsync(matches[0].Path, module, resourceId, token))).Mod;
            var id = await ImportModAsync(directory, token);
            return await GetModByIdAsync(id, CancellationToken.None)
                ?? throw new InvalidOperationException("HD2IAS data 保存后无法读取。");
        }
        finally { _hd2IASDataGate.Release(); }
    }

    private static async Task<bool> IsHd2IASDataAsync(Mod mod, string module, string resourceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mod.WorkflowType == Hd2IrIntegrationService.Workflow || mod.LogicalMod != null) return false;
        if (mod.Manifest?.Guid == Hd2IASDataDirectoryWriter.ManifestGuid) return true;
        if (!Directory.Exists(mod.Path)) return false;
        var marker = Path.Combine(mod.Path, Hd2IASDataDirectoryWriter.IdentityFile);
        if (File.Exists(marker))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(marker, token));
            var root = json.RootElement;
            return root.TryGetProperty("schema", out var schema) && schema.GetString() == "HD2MimntHd2IAS1"
                && root.TryGetProperty("module", out var name) && name.GetString() == module
                && root.TryGetProperty("resource_id", out var resource) && resource.GetString()?.Equals(resourceId, StringComparison.OrdinalIgnoreCase) == true;
        }
        // 兼容已手动导入的旧系统 data；只接管单资源、源码带明确模块入口的包。
        return await HasHd2IASPatchAsync(mod.Path, module, resourceId, token);
    }

    private static async Task<bool> HasHd2IASPatchAsync(string directory, string module, string resourceId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Directory.Exists(directory)) return false;
        var patches = Directory.EnumerateFiles(directory, "*.patch_*", SearchOption.AllDirectories)
            .Where(path => PatchDeploymentPlanner.TryParseTarget(path, out _, out _, out var suffix) && suffix.Length == 0).Take(2).ToArray();
        if (patches.Length != 1) return false;
        try
        {
            var resource = ulong.Parse(resourceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var entries = HD2Mimnt.Infrastructure.Analysis.PatchResourceIndexReader.Read(patches[0], token);
            if (entries.Count != 1 || entries[0].ResourceId != resource || entries[0].TypeId != 0xa14e8dfa2cd117e2UL) return false;
            var source = StingrayArchiveWriter.ReadSource(await File.ReadAllBytesAsync(patches[0], token),
                resource);
            return Encoding.UTF8.GetString(source).StartsWith("-- HD2-Addon: " + module + "\n", StringComparison.Ordinal);
        }
        catch (InvalidDataException) { return false; }
    }
}
