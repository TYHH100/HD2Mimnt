using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Core.Hd2IAS;
using HD2Mimnt.Core.Services;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>生成普通 data 模组目录，不写游戏目录，不包含运行时文件。</summary>
public static class Hd2IASDataDirectoryWriter
{
    public const string IdentityFile = "hd2mimnt-hd2ias.json";
    public static readonly Guid ManifestGuid = new("03cb684e-371e-4898-b8dd-499c420c7c73");

    public static async Task WriteAsync(string directory, Hd2IASRuntimeManifest manifest, byte[] source,
        CancellationToken token)
    {
        if (IndependentLuaSourceBuilder.Hash(source) != manifest.IndependentLua.SourceSha256)
            throw new InvalidDataException("HD2IAS source identity mismatch.");
        var resource = ulong.Parse(manifest.IndependentLua.ResourceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var data = Path.Combine(directory, "data"); Directory.CreateDirectory(data);
        var patch = Path.Combine(data, StingrayArchiveWriter.PatchName);
        await File.WriteAllBytesAsync(patch, StingrayArchiveWriter.Build(source, resource), token);
        await File.WriteAllBytesAsync(patch + ".stream", [], token);
        await File.WriteAllBytesAsync(patch + ".gpu_resources", [], token);
        if (!StingrayArchiveWriter.ReadSource(await File.ReadAllBytesAsync(patch, token), resource).AsSpan().SequenceEqual(source))
            throw new InvalidDataException("HD2IAS data 回读验证失败。");
        await ManifestPackage.SaveAsync(Path.Combine(directory, "manifest.json"), new JsonObject
        {
            ["Version"] = 1, ["Guid"] = ManifestGuid.ToString(), ["Name"] = "HD2IAS data",
            ["Description"] = "Requires Bingus Shared Loader or Mod Dynamic Loader. HD2IAS system Lua data.",
            ["ModVersion"] = manifest.Version,
            ["Options"] = new JsonArray(new JsonObject { ["Name"] = "data", ["Include"] = new JsonArray("data") })
        }, token);
        await File.WriteAllTextAsync(Path.Combine(directory, IdentityFile), JsonSerializer.Serialize(new
        {
            schema = "HD2MimntHd2IAS1", runtime_version = manifest.Version,
            pose_route = manifest.Route == Hd2IASRoute.Native ? "native" : "lua",
            module = manifest.IndependentLua.Module, resource_id = manifest.IndependentLua.ResourceId,
            identity = manifest.IndependentLua.Identity, body_sha256 = manifest.IndependentLua.BodySha256,
            source_sha256 = manifest.IndependentLua.SourceSha256
        }), token);
    }
}
