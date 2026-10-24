using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

public sealed class ArsenalPackageWriter
{
    public async Task<Hd2IASGeneratedPackage> WriteAsync(string outputPath, Hd2IASRuntimeManifest manifest,
        byte[] source, byte[]? icon, CancellationToken cancellationToken = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        if (!Path.GetExtension(outputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The HD2IAS output must be a ZIP file.");
        if (File.Exists(outputPath)) throw new IOException("The output already exists. Select a new file name.");
        var resourceId = ulong.Parse(manifest.IndependentLua.ResourceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        if (IndependentLuaSourceBuilder.Hash(source) != manifest.IndependentLua.SourceSha256)
            throw new InvalidDataException("HD2IAS source identity mismatch.");
        var route = manifest.Route == Hd2IASRoute.Lua ? "lua" : "native";
        var info = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "HD2ModInfo1", name = $"HD2IAS data {manifest.Version} [{route}]", version = manifest.Version,
            package_type = "lua", pose_route = route, install_with = "Arsenal", requires_loader = true, loader_bundled = false,
            required_loader = "Mod Dynamic Loader OR Bingus Shared Loader (choose one)",
            description = "System Lua only. Replace the previous HD2IAS system data package. Keep character mods and one compatible loader."
        });
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "HD2MimntHd2IAS1", runtime_version = manifest.Version, pose_route = route,
            module = manifest.IndependentLua.Module, resource_id = manifest.IndependentLua.ResourceId,
            identity = manifest.IndependentLua.Identity, body_sha256 = manifest.IndependentLua.BodySha256,
            source_sha256 = manifest.IndependentLua.SourceSha256
        });
        // 在目标旁写入临时文件，所有校验完成后一次移动发布；不复制包内程序或运行时。
        var temporary = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await AddAsync("data/" + StingrayArchiveWriter.PatchName, StingrayArchiveWriter.Build(source, resourceId));
                await AddAsync("data/" + StingrayArchiveWriter.PatchName + ".stream", []);
                await AddAsync("data/" + StingrayArchiveWriter.PatchName + ".gpu_resources", []);
                await AddAsync("mod_info.json", info);
                await AddAsync("hd2mimnt-hd2ias.json", identity);
                if (icon != null) await AddAsync("icon.png", icon);

                async Task AddAsync(string path, byte[] content)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var destination = archive.CreateEntry(path, CompressionLevel.SmallestSize).Open();
                    await destination.WriteAsync(content, cancellationToken);
                }
            }
            using (var archive = ZipFile.OpenRead(temporary))
            {
                var patch = await ReadAsync("data/" + StingrayArchiveWriter.PatchName);
                if (!StingrayArchiveWriter.ReadSource(patch, resourceId).AsSpan().SequenceEqual(source) ||
                    !(await ReadAsync("mod_info.json")).AsSpan().SequenceEqual(info) ||
                    !(await ReadAsync("hd2mimnt-hd2ias.json")).AsSpan().SequenceEqual(identity) ||
                    archive.GetEntry("data/" + StingrayArchiveWriter.PatchName + ".stream")?.Length != 0 ||
                    archive.GetEntry("data/" + StingrayArchiveWriter.PatchName + ".gpu_resources")?.Length != 0)
                    throw new InvalidDataException("Generated HD2IAS package failed read-back validation.");

                async Task<byte[]> ReadAsync(string name)
                {
                    await using var entry = (archive.GetEntry(name) ?? throw new InvalidDataException($"Missing generated entry: {name}")).Open();
                    using var buffer = new MemoryStream(); await entry.CopyToAsync(buffer, cancellationToken); return buffer.ToArray();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, outputPath);
            return new(outputPath, manifest.Version, manifest.Route, manifest.IndependentLua.Module,
                manifest.IndependentLua.Identity, manifest.IndependentLua.SourceSha256);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
