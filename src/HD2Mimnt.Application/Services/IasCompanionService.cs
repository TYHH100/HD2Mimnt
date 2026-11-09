using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Hd2IAS.Companion;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// HD2IAS armor 配套生成编排：从当前部署记录收集 IAS 模组 → 构造上游 HD2ModLibrary1 索引
/// 与 blob 快照 → 调用上游签名 Python 生成器（metadata_only 流程）→ 按上游事务语义落盘。
/// 只写 Runtime\&lt;ID&gt;\armor 下由生成器产出的文件与 deployment.json 的 armor 组件段。
/// </summary>
public class IasCompanionService : IIasCompanionService
{
    private static readonly StringComparison IgnoreCase = StringComparison.OrdinalIgnoreCase;
    private static readonly Lock ShaMemoLock = new();
    private static readonly Dictionary<string, (string Hash, long Length, DateTime Stamp)> ShaMemo = new(StringComparer.OrdinalIgnoreCase);

    private readonly IIasSystemService _system;
    private readonly ModService _mods;
    private readonly IDeploymentRepository _deployments;
    private readonly SettingsService _settings;
    private readonly string _backupRoot;
    private readonly string _cacheRoot;
    private readonly string _obsoleteManifestPath;

    public IasCompanionService(IIasSystemService system, ModService mods,
        IDeploymentRepository deployments, SettingsService settings,
        string backupRoot, string cacheRoot, string obsoleteManifestPath)
    {
        _system = system;
        _mods = mods;
        _deployments = deployments;
        _settings = settings;
        _backupRoot = backupRoot;
        _cacheRoot = cacheRoot;
        _obsoleteManifestPath = obsoleteManifestPath;
    }

    public async Task<IasCompanionStatus> GetStatusAsync(CancellationToken token = default)
    {
        var runtimeRoot = await _system.GetRuntimeRootAsync(token);
        if (runtimeRoot == null) return new IasCompanionStatus(false, false, null, null);
        var receiptPath = Path.Combine(runtimeRoot, "deployment.json");
        var installed = false;
        if (File.Exists(receiptPath))
        {
            try
            {
                var receipt = JsonNode.Parse(await File.ReadAllBytesAsync(receiptPath, token))?.AsObject();
                installed = receipt != null
                    && receipt["schema"]?.GetValue<string>() == "HD2ExternalDeployment1"
                    && IasCompanionApplier.FindArmorComponent(receipt) != null;
            }
            catch (Exception ex) when (ex is JsonException or IOException) { }
        }
        return new IasCompanionStatus(true, installed, runtimeRoot,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(runtimeRoot)));
    }

    public async Task<IasCompanionResult> RegenerateAsync(string? gamePath,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        var resolved = string.IsNullOrWhiteSpace(gamePath) ? _settings.GetEffectiveGamePath() : gamePath;
        if (string.IsNullOrWhiteSpace(resolved))
            throw new InvalidOperationException("未检测到游戏安装目录，无法生成 IAS 配套。");
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
        var runtimeRoot = await _system.GetRuntimeRootAsync(token)
            ?? throw new InvalidOperationException("未配置 HD2IAS 系统安装位置，无法生成配套。");
        var stage = Path.Combine(Path.GetTempPath(), "HD2Mimnt-IAS", Guid.NewGuid().ToString("N"));
        try
        {
            var libraryDir = Path.Combine(stage, "library");
            var blobsDir = Path.Combine(libraryDir, "blobs");
            Directory.CreateDirectory(blobsDir);

            progress?.Report("IAS · 收集已部署的 IAS 模组");
            var indexRows = await BuildLibraryIndexAsync(normalized, blobsDir, token);
            var indexPath = Path.Combine(stage, "library-index.json");
            await File.WriteAllTextAsync(indexPath, JsonSerializer.Serialize(new JsonObject
            {
                ["schema"] = "HD2ModLibrary1",
                ["game"] = normalized,
                ["entries"] = new JsonArray(indexRows.Select(row => (JsonNode)row).ToArray()),
            }), token);

            progress?.Report("IAS · 准备上游运行资源");
            var externalRoot = await _system.GetExternalRuntimeRootAsync(token)
                ?? throw new InvalidOperationException("未配置 HD2IAS 系统安装位置，无法定位上游运行资源。");
            var assetsDir = IasGeneratorAssets.EnsureExtracted(
                Path.Combine(externalRoot, IasGeneratorAssets.ArmorAssetsZip), _cacheRoot);
            var generatorDir = IasGeneratorAssets.EnsureExtracted(
                Path.Combine(externalRoot, IasGeneratorAssets.GeneratorZip), _cacheRoot);
            var (pythonExe, _) = IasGeneratorAssets.ReadGeneratorManifest(generatorDir);
            var request = IasGeneratorAssets.ReadAssetsRequest(assetsDir);

            var outputDir = Path.Combine(stage, "generated");
            request["index"] = FileRow(indexPath);
            request["library"] = libraryDir;
            request["output"] = outputDir;
            request["archives"] = new JsonArray();
            request["installed_mods"] = false;
            request["metadata_only"] = true;
            request["bootstrap"] = new JsonArray();

            progress?.Report("IAS · 生成护甲配套");
            await IasGeneratorRunner.RunAsync(generatorDir, pythonExe, request,
                Path.Combine(stage, "request.json"), progress, token);

            var packageDir = Path.Combine(outputDir, "package");
            AlignSettingsHost(runtimeRoot, packageDir);
            var retained = BuildRetainedTargets(request);

            progress?.Report("IAS · 落盘配套文件");
            var obsoleteTargets = ReadObsoleteManifestTargets();
            var (written, deleted, preserved, transactionPath) = await IasCompanionApplier.ApplyAsync(
                normalized, runtimeRoot, _backupRoot, packageDir, retained, obsoleteTargets, progress, token);
            if (obsoleteTargets.Count > 0 && File.Exists(_obsoleteManifestPath))
                File.Delete(_obsoleteManifestPath);
            return new IasCompanionResult(written, deleted, preserved, transactionPath);
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 按部署记录里的选项快照收集 IAS 模组的 rig 文件（配置页取消勾选的组件不进配套），
    /// 构造 HD2ModLibrary1 条目并把文件复制进 blob 快照。
    /// </summary>
    internal async Task<List<JsonObject>> BuildLibraryIndexAsync(string gamePath, string blobsDir, CancellationToken token)
    {
        var records = await _deployments.GetCurrentDeploymentsAsync(gamePath, token);
        var iasGroups = records
            .Where(record => string.Equals(record.Workflow, Hd2IrIntegrationService.Workflow, IgnoreCase))
            .GroupBy(record => record.ModId)
            .ToList();
        var rows = new List<JsonObject>();
        if (iasGroups.Count == 0) return rows;
        var modsById = (await _mods.GetAllModsAsync(token)).ToDictionary(mod => mod.Id);
        foreach (var group in iasGroups)
        {
            token.ThrowIfCancellationRequested();
            var modId = group.Key;
            if (!modsById.TryGetValue(modId, out var mod) || mod.WorkflowType != Hd2IrIntegrationService.Workflow)
                continue;
            var bundleFiles = new JsonArray();
            var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // 同一模组可能在多个配置里各有一条当前记录：按各自快照收集后按目标去重取并集。
            foreach (var record in group)
            {
                foreach (var file in await Hd2IrIntegrationService.GetNativeFilesAsync(mod, record.OptionsSnapshotJson, token))
                {
                    var target = file.FileName.Replace('\\', '/');
                    if (!target.StartsWith("bin/HD2IndependentRig/", IgnoreCase)) continue;
                    if (!collected.Add(target)) continue;
                    var sha = await FileSha256Async(file.SourcePath!, token);
                    var blob = Path.Combine(blobsDir, sha);
                    if (!File.Exists(blob)) File.Copy(file.SourcePath!, blob, true);
                    bundleFiles.Add(new JsonObject { ["path"] = target, ["sha256"] = sha });
                }
            }
            var bundleId = $"sdkgroup.{modId:N}";
            rows.Add(new JsonObject
            {
                ["id"] = bundleId,
                ["name"] = mod.Name,
                ["enabled"] = true,
                ["error"] = null,
                ["source"] = "HD2Mimnt",
                ["bundle"] = new JsonObject
                {
                    ["schema"] = "HD2ManagedBundle1",
                    ["id"] = bundleId,
                    ["name"] = mod.Name,
                    ["files"] = bundleFiles,
                    ["icon_sha256"] = null,
                },
            });
        }
        return rows;
    }

    /// <summary>
    /// 生成器以 assets 内置 native 组件的哈希写 settings-host.json；运行目录里实际安装的
    /// bridge DLL 可能更新（上游原地更新），复刻上游 Generate 的对齐步骤。
    /// </summary>
    private static void AlignSettingsHost(string runtimeRoot, string packageDir)
    {
        var receiptPath = Path.Combine(runtimeRoot, "deployment.json");
        if (!File.Exists(receiptPath)) return;
        var receipt = JsonNode.Parse(File.ReadAllBytes(receiptPath))?.AsObject();
        var armor = receipt == null ? null : IasCompanionApplier.FindArmorComponent(receipt);
        var installedSha = armor?["files"] is JsonArray rows
            ? rows.OfType<JsonObject>()
                .FirstOrDefault(node => node["path"]?.GetValue<string>()
                    ?.Equals("hd2ias_native_bridge_pluginw64_release.dll", IgnoreCase) == true)
                ?["sha256"]?.GetValue<string>()
            : null;
        if (installedSha == null) return;
        var settingsHostPath = Path.Combine(packageDir, "files", "settings-host.json");
        if (!File.Exists(settingsHostPath)) return;
        var settingsHost = JsonNode.Parse(File.ReadAllBytes(settingsHostPath))?.AsObject();
        var coreSha = settingsHost?["core"]?["sha256"]?.GetValue<string>();
        if (settingsHost == null || coreSha == installedSha) return;
        settingsHost["core"]!["sha256"] = installedSha;
        File.WriteAllText(settingsHostPath, settingsHost.ToJsonString());

        var manifestPath = Path.Combine(packageDir, "external-runtime.json");
        var package = JsonNode.Parse(File.ReadAllBytes(manifestPath))?.AsObject();
        if (package?["files"] is not JsonArray files) return;
        foreach (var node in files.OfType<JsonObject>())
        {
            if (node["path"]?.GetValue<string>() != "settings-host.json") continue;
            var bytes = File.ReadAllBytes(settingsHostPath);
            node["bytes"] = bytes.Length;
            node["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
            break;
        }
        File.WriteAllText(manifestPath, package.ToJsonString());
    }

    /// <summary>retained 目标 = assets 的 runtime_files 目标 + 上游 bootstrap 缓存名（重生成时不覆盖已安装组件）。</summary>
    private static IReadOnlySet<string> BuildRetainedTargets(JsonObject request)
    {
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request["runtime_files"] is JsonArray rows)
        {
            foreach (var node in rows.OfType<JsonObject>())
            {
                if (node["target"] is JsonValue target && target.TryGetValue<string>(out var name))
                    retained.Add(name);
            }
        }
        foreach (var suffix in new[] { "", ".gpu_resources", ".stream" })
            retained.Add($"bootstrap/boot.patch_0{suffix}");
        return retained;
    }

    /// <summary>读取旧版直写部署的 manifest（IAS-System\deployment.json），其 TargetPath 为待清理残留。</summary>
    private IReadOnlyList<string> ReadObsoleteManifestTargets()
    {
        if (!File.Exists(_obsoleteManifestPath)) return [];
        try
        {
            var manifest = JsonNode.Parse(File.ReadAllBytes(_obsoleteManifestPath));
            if (manifest?["Files"] is not JsonArray files) return [];
            return files.OfType<JsonObject>()
                .Select(node => node["TargetPath"]?.GetValue<string>())
                .OfType<string>()
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }

    private static JsonObject FileRow(string path)
    {
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        return new JsonObject
        {
            ["path"] = Path.GetFullPath(path),
            ["bytes"] = info.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(stream)),
        };
    }

    private static async Task<string> FileSha256Async(string path, CancellationToken token)
    {
        var info = new FileInfo(path);
        lock (ShaMemoLock)
        {
            if (ShaMemo.TryGetValue(path, out var memo)
                && memo.Length == info.Length && memo.Stamp == info.LastWriteTimeUtc)
                return memo.Hash;
        }
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
        lock (ShaMemoLock)
        {
            ShaMemo[path] = (hash, info.Length, info.LastWriteTimeUtc);
        }
        return hash;
    }
}
