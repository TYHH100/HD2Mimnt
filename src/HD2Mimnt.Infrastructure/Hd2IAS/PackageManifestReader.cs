using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>只读取清单与载荷，不解压、加载或执行包内代码。</summary>
public sealed class PackageManifestReader : IHd2IASPackageReader
{
    internal const string BuildFile = "HD2IndependentRig.build.json";
    internal const string BuildTarget = "bin/" + BuildFile;
    public async Task<Hd2IASPackage> ReadAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(zipPath);
        // Windows 目标路径不区分大小写；重复条目不可由 ZIP 的顺序决定。
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
            if (!entries.TryAdd(entry.FullName.Replace('\\', '/'), entry))
                throw new InvalidDataException($"Duplicate ZIP entry: {entry.FullName}");

        using var luaJson = await ReadJsonAsync("RuntimePackage/package.json");
        using var nativeJson = await ReadJsonAsync("RuntimePackage/Native/package.json");
        using var foundationJson = await ReadJsonAsync("Foundation/foundation.json");
        using var notesJson = await ReadJsonAsync("app/update_notes.json");
        var lua = ParseRuntime(luaJson.RootElement, Hd2IASRoute.Lua);
        var native = ParseRuntime(nativeJson.RootElement, Hd2IASRoute.Native);
        var foundation = ParseFoundation(foundationJson.RootElement);
        var version = Text(notesJson.RootElement, "version");
        if (version != lua.Version || version != native.Version ||
            lua.GameExeSha256 != native.GameExeSha256 || lua.GameExeSha256 != foundation.GameExeSha256 ||
            lua.BundleDatabaseSha256 != native.BundleDatabaseSha256 || lua.BundleDatabaseSha256 != foundation.BundleDatabaseSha256 ||
            lua.Lua51Sha256 != native.Lua51Sha256)
            throw new InvalidDataException("HD2IAS route versions or game fingerprints disagree.");

        var checks = new List<Hd2IASFileCheck>();
        await CheckFilesAsync("RuntimePackage/", lua.Files);
        await CheckFilesAsync("RuntimePackage/Native/", native.Files);
        await CheckFilesAsync("Foundation/", foundation.Files);
        await CheckBuildAsync("RuntimePackage/", lua);
        await CheckBuildAsync("RuntimePackage/Native/", native);
        var actions = notesJson.RootElement.TryGetProperty("manual_actions", out var actionsJson)
            ? actionsJson.EnumerateArray().Select(item => item.GetString() ?? "").ToArray() : [];
        return new(version, lua, native, foundation, checks,
            notesJson.RootElement.TryGetProperty("notes", out var notes) ? notes.GetString() ?? "" : "", actions);

        async Task<JsonDocument> ReadJsonAsync(string path)
        {
            if (!entries.TryGetValue(path, out var entry)) throw new InvalidDataException($"Missing manifest: {path}");
            await using var stream = entry.Open();
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        async Task CheckFilesAsync(string root, IReadOnlyList<Hd2IASFile> files)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = root + file.Path;
                var state = Hd2IASFileState.Missing;
                if (entries.TryGetValue(path, out var entry))
                {
                    await using var stream = entry.Open();
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                    state = hash == file.Sha256 && (file.Bytes == null || file.Bytes == entry.Length)
                        ? Hd2IASFileState.Match : Hd2IASFileState.Unknown;
                }
                checks.Add(new(path, state));
            }
        }

        async Task CheckBuildAsync(string root, Hd2IASRuntimeManifest manifest)
        {
            var file = manifest.Files.SingleOrDefault(file => file.Path.Equals(BuildFile, StringComparison.OrdinalIgnoreCase));
            if (file == null || !checks.Any(check => check.Path == root + file.Path && check.State == Hd2IASFileState.Match)) return;
            using var json = await ReadJsonAsync(root + file.Path);
            RequireSchema(json.RootElement, "HD2IndependentRigBuild1");
            if (Text(json.RootElement, "version") != manifest.Version || Text(json.RootElement, "pose_route") !=
                (manifest.Route == Hd2IASRoute.Native ? "native" : "lua"))
                throw new InvalidDataException("HD2IAS build record disagrees with runtime version or route.");
        }
    }

    private static Hd2IASRuntimeManifest ParseRuntime(JsonElement json, Hd2IASRoute expectedRoute)
    {
        RequireSchema(json, "HD2RuntimeDistribution1");
        var route = Text(json, "pose_route");
        if (route != (expectedRoute == Hd2IASRoute.Lua ? "lua" : "native"))
            throw new InvalidDataException($"Unexpected pose_route: {route}");
        var identity = json.GetProperty("independent_lua");
        var module = Text(identity, "module");
        var resource = Text(identity, "resource_id");
        if (resource.Length != 16 || !resource.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid Lua resource ID.");
        // 构建记录属于系统元数据；仅为这个已知文件补充官方包省略的部署目标。
        var files = ParseFiles(json.GetProperty("files"), foundation: false).Select(file =>
            file.Path.Equals(BuildFile, StringComparison.OrdinalIgnoreCase)
                ? file with { Target = file.Target ?? BuildTarget } : file).ToArray();
        if (files.GroupBy(file => file.Target, StringComparer.OrdinalIgnoreCase).Any(group => group.Key != null && group.Count() > 1) ||
            files.Any(file => file.Path.Equals(BuildFile, StringComparison.OrdinalIgnoreCase) &&
                !file.Target!.Equals(BuildTarget, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Invalid or duplicate HD2IAS build record target.");
        var retired = new List<Hd2IASFile>();
        if (json.TryGetProperty("retired_runtime", out var retiredJson))
            foreach (var file in retiredJson.EnumerateArray())
                retired.Add(new("", SafePath(Text(file, "target")), "", null, Hashes(file, "previous_sha256")));
        return new(Text(json, "version"), expectedRoute, Text(json, "appearance_source"),
            Hash(json, "game_exe_sha256"), Hash(json, "bundle_database_sha256"), Hash(json, "lua51_sha256"),
            new(module, resource, Hash(identity, "body_sha256"), Hash(identity, "identity"), Hash(identity, "source_sha256")),
            files, retired);
    }

    private static Hd2IASFoundationManifest ParseFoundation(JsonElement json)
    {
        RequireSchema(json, "HD2SystemFoundation2");
        return new(Text(json, "version"), Hash(json, "game_exe_sha256"), Hash(json, "bundle_database_sha256"),
            ParseFiles(json.GetProperty("files"), foundation: true));
    }

    private static IReadOnlyList<Hd2IASFile> ParseFiles(JsonElement json, bool foundation)
    {
        var files = new List<Hd2IASFile>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in json.EnumerateArray())
        {
            var path = SafePath(Text(file, "path"));
            var target = foundation ? path : file.TryGetProperty("target", out var value) && value.ValueKind != JsonValueKind.Null
                ? SafePath(value.GetString() ?? "") : null;
            if (!sources.Add(path) || target != null && !targets.Add(target))
                throw new InvalidDataException($"Duplicate manifest path: {path}");
            long? bytes = file.TryGetProperty("bytes", out var size) ? size.GetInt64() : null;
            if (bytes < 0) throw new InvalidDataException($"Invalid file size: {path}");
            files.Add(new(path, target, Hash(file, "sha256"), bytes, Hashes(file, "previous_sha256")));
        }
        if (files.Count == 0 || !files.Any(file => file.Target != null)) throw new InvalidDataException("Empty deployment manifest.");
        return files;
    }

    internal static string SafePath(string path)
    {
        path = path.Replace('\\', '/');
        if (path.Split('/').Any(part => string.IsNullOrWhiteSpace(part) || part is "." or ".." ||
            part.IndexOfAny([':', '*', '?', '"', '<', '>', '|', '\0']) >= 0 || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException($"Invalid relative manifest path: {path}");
        return path;
    }

    private static string Text(JsonElement json, string name)
    {
        var value = json.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new InvalidDataException($"Missing field: {name}");
    }
    private static string Hash(JsonElement json, string name) => ValidateHash(Text(json, name));
    private static string ValidateHash(string value) => value.Length == 64 && value.All(Uri.IsHexDigit)
        ? value.ToLowerInvariant() : throw new InvalidDataException("Invalid SHA-256 in HD2IAS manifest.");
    private static string[] Hashes(JsonElement json, string name) => json.TryGetProperty(name, out var values)
        ? values.EnumerateArray().Select(value => ValidateHash(value.GetString() ?? "")).ToArray() : [];
    private static void RequireSchema(JsonElement json, string schema)
    {
        if (Text(json, "schema") != schema) throw new InvalidDataException($"Unsupported HD2IAS schema (expected {schema}).");
    }
}
