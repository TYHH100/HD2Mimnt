using HD2Mimnt.Core.IO;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Infrastructure.Hd2IAS;
using System.IO.Compression;
using System.Security.Cryptography;

namespace HD2Mimnt.Application.Services;

/// <summary>运行时独立安装；生成的 data 通过 ModService 管理与更新。</summary>
public sealed class Hd2IASIntegrationService(IHd2IASPackageReader packages, IHd2IASRuntimeInspector runtime,
    IHd2IASLuaCompiler? compiler = null, IHd2IASUpdateSource? updates = null, string? updateCachePath = null,
    Hd2IASDeploymentStore? deploymentStore = null, HD2Mimnt.Core.Interfaces.IDeploymentRepository? deployments = null,
    ModService? mods = null)
{
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public Task<Hd2IASManagementState> GetManagementStateAsync(string? gamePath, CancellationToken token = default)
        => Task.Run(async () =>
        {
            using var lease = !string.IsNullOrWhiteSpace(gamePath) && deploymentStore != null
                ? await deploymentStore.AcquireAsync(token) : null;
            var build = await runtime.ReadInstalledAsync(gamePath, token);
            var data = mods != null ? await mods.GetHd2IASDataStateAsync(token) : (HasMod: false, HasFiles: false);
            if (string.IsNullOrWhiteSpace(gamePath) || deploymentStore == null) return new(build, false, data.HasMod, data.HasFiles, []);
            return new Hd2IASManagementState(build, await deploymentStore.ReadAsync(gamePath, token) != null, data.HasMod, data.HasFiles,
                await deploymentStore.GetMissingFilesAsync(gamePath, token));
        }, token);

    public Task<Hd2IASUninstallResult> UninstallAsync(string gamePath, IProgress<string>? progress = null,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var store = deploymentStore ?? throw new InvalidOperationException("HD2IAS managed storage is not configured.");
        using var lease = await store.AcquireAsync(token);
        await store.RecoverAsync(gamePath, token);
        return await store.UninstallAsync(gamePath, progress, token);
    }, token);
    public Task<Hd2IASGeneratedPackage> InstallAsync(string packagePath, string gamePath, Hd2IASRoute route,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var store = deploymentStore ?? throw new InvalidOperationException("HD2IAS managed storage is not configured.");
        var library = mods ?? throw new InvalidOperationException("HD2IAS mod library is not configured.");
        using var lease = await store.AcquireAsync(cancellationToken);
        await store.RecoverAsync(gamePath, cancellationToken);
        progress?.Report("校验运行包与游戏兼容性");
        var package = await packages.ReadAsync(packagePath, cancellationToken);
        var inspection = await runtime.InspectAsync(package, gamePath, route, cancellationToken);
        if (!package.IsValid || !inspection.IsGameCompatible)
            throw new InvalidDataException("运行包无效或不兼容当前游戏，安装已停止。");
        if (deployments != null)
        {
            var targets = inspection.FilePlan.Select(file => Path.GetFullPath(file.Path, gamePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var conflicts = (await deployments.GetLatestDeploymentsAsync(cancellationToken)).SelectMany(record => record.Files)
                .Where(file => targets.Contains(Path.GetFullPath(file.TargetPath))).ToArray();
            if (conflicts.Length > 0) throw new IOException("目标仍由普通模组部署记录管理，请先清理原部署，再安装 HD2IAS。");
        }
        var manifest = package.ForRoute(route);
        progress?.Report("生成并验证匹配路线的系统 data");
        var source = await BuildSourceAsync(packagePath, gamePath, manifest, cancellationToken);
        var generated = await store.InstallAsync(packagePath, gamePath, inspection, source, progress, cancellationToken);
        progress?.Report("将系统 data 添加或更新到模组库");
        try
        {
            var mod = await library.UpsertHd2IASDataAsync(generated.Path, generated.Module, manifest.IndependentLua.ResourceId, cancellationToken);
            return generated with { Path = mod.Path, ModId = mod.Id };
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("运行包已安装，但 data 模组管理未完成，请重试：" + error.Message, error);
        }
    }, cancellationToken);

    public Task<Hd2IASInstalledBuild> ReadInstalledAsync(string? gamePath, CancellationToken cancellationToken = default)
        => Task.Run(() => runtime.ReadInstalledAsync(gamePath, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<Hd2IASUpdateVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
        => (updates ?? throw new InvalidOperationException("HD2IAS update source is not configured."))
            .GetVersionsAsync(cancellationToken);

    public Task<string> DownloadUpdateAsync(Hd2IASUpdateVersion version,
        IProgress<HD2Mimnt.Core.Entities.DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        if (updates == null || string.IsNullOrWhiteSpace(updateCachePath))
            throw new InvalidOperationException("HD2IAS update source is not configured.");
        // 文件名只接受版本号；不把远端目录或下载文件名直接用作本地路径。
        if (!version.Version.All(character => char.IsAsciiDigit(character) || character == '.') ||
            !Version.TryParse(version.Version, out _)) throw new InvalidDataException("Invalid HD2IAS version.");
        await _downloadGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(updateCachePath);
            var destination = Path.Combine(updateCachePath, $"HD2IAS-{version.Version}-update.zip");
            if (File.Exists(destination))
            {
                try
                {
                    var cached = await packages.ReadAsync(destination, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cached.IsValid && cached.Version == version.Version) return destination;
                }
                catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or
                    KeyNotFoundException or InvalidOperationException or FormatException)
                { /* 缓存格式损坏时重新下载；成功校验前保留旧文件。 */ }
            }
            var pending = destination + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var source = await updates.DownloadAsync(version, progress, cancellationToken))
                await using (var target = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous))
                    await source.CopyToAsync(target, cancellationToken);
                var package = await packages.ReadAsync(pending, cancellationToken);
                if (!package.IsValid || package.Version != version.Version)
                    throw new InvalidDataException("Downloaded HD2IAS package verification or version check failed.");
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(pending, destination, overwrite: true);
                return destination;
            }
            finally { File.Delete(pending); }
        }
        finally { _downloadGate.Release(); }
    }, cancellationToken);

    public Task<Hd2IASInspection> InspectAsync(string packagePath, string? gamePath, Hd2IASRoute route,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var package = await packages.ReadAsync(packagePath, cancellationToken);
        return await runtime.InspectAsync(package, gamePath, route, cancellationToken);
    }, cancellationToken);

    public Task<Hd2IASGeneratedPackage> GenerateAsync(string packagePath, string gamePath, Hd2IASRoute route,
        string outputPath, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        // 导出到游戏目录会留下未追踪的活动系统包，必须交给后续专用部署流程处理。
        var output = Path.GetFullPath(outputPath);
        if (PathGuard.IsInsideRoot(gamePath, output))
            throw new InvalidOperationException("Export the HD2IAS package outside the game directory.");
        var package = await packages.ReadAsync(packagePath, cancellationToken);
        if (!package.IsValid) throw new InvalidDataException("HD2IAS package verification failed.");
        var manifest = package.ForRoute(route);
        var source = await BuildSourceAsync(packagePath, gamePath, manifest, cancellationToken);
        using var archive = ZipFile.OpenRead(packagePath);
        byte[]? icon = null;
        if (archive.GetEntry("app/Assets/App.png") is { } iconEntry)
        {
            await using var stream = iconEntry.Open(); using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken); icon = buffer.ToArray();
        }
        return await new ArsenalPackageWriter().WriteAsync(output, manifest, source, icon, cancellationToken);
    }, cancellationToken);

    private async Task<byte[]> BuildSourceAsync(string packagePath, string gamePath, Hd2IASRuntimeManifest manifest,
        CancellationToken cancellationToken)
    {
        var prefix = manifest.Route == Hd2IASRoute.Lua ? "RuntimePackage/" : "RuntimePackage/Native/";
        using var archive = ZipFile.OpenRead(packagePath);
        var source = IndependentLuaSourceBuilder.Build(await ReadVerifiedAsync("runtime.lua"),
            await ReadVerifiedAsync("independent_loader.lua"), manifest);
        await (compiler ?? new LuaCompilerValidator()).ValidateAsync(Path.Combine(gamePath, "bin", "lua51.dll"),
            manifest.Lua51Sha256, source, manifest.IndependentLua.Module, cancellationToken);
        return source;

        async Task<byte[]> ReadVerifiedAsync(string name)
        {
            var file = manifest.Files.Single(file => file.Path.Equals(name, StringComparison.OrdinalIgnoreCase));
            await using var stream = (archive.GetEntry(prefix + file.Path) ?? throw new InvalidDataException($"Missing {name}")).Open();
            using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer, cancellationToken);
            var data = buffer.ToArray();
            if (!Convert.ToHexStringLower(SHA256.HashData(data)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"HD2IAS source file changed: {name}");
            return data;
        }
    }
}
