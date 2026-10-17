using System.Text.Json;
using HD2Mimnt.Core.Hd2IAS;

namespace HD2Mimnt.Infrastructure.Hd2IAS;

/// <summary>读取本机版本记录及配置用途；记录声明与载荷哈希验证分别呈现。</summary>
internal static class InstalledBuildReader
{
    public static async Task<Hd2IASInstalledBuild> ReadAsync(string? gamePath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(gamePath)) return new("", Hd2IASBuildState.NotChecked, null, null, "", []);
        var root = Path.GetFullPath(gamePath);
        var path = Path.Combine(root, "bin", "HD2IndependentRig.build.json");
        var directory = Path.Combine(root, "bin", "HD2IndependentRig");
        var configuration = new List<Hd2IASConfigFile>();
        if (Directory.Exists(directory))
            foreach (var file in Directory.EnumerateFiles(directory, "*.ini", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                configuration.Add(new(relative, Classify(relative)));
            }
        configuration.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
        if (!File.Exists(path)) return new(path, Hd2IASBuildState.Missing, null, null, "", configuration);
        var contents = "";
        try
        {
            contents = await File.ReadAllTextAsync(path, token);
            using var json = JsonDocument.Parse(contents);
            var build = json.RootElement;
            if (build.GetProperty("schema").GetString() != "HD2IndependentRigBuild1")
                throw new InvalidDataException("Unsupported HD2IndependentRig build schema.");
            var version = build.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(version) || !Version.TryParse(version, out _))
                throw new InvalidDataException("Invalid installed HD2IAS version.");
            Hd2IASRoute? route = build.TryGetProperty("pose_route", out var value) ? value.GetString() switch
            { "lua" => Hd2IASRoute.Lua, "native" => Hd2IASRoute.Native, _ => null } : null;
            return new(path, Hd2IASBuildState.Read, version, route, contents, configuration);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException or
            InvalidOperationException or IOException or UnauthorizedAccessException)
        { return new(path, Hd2IASBuildState.Invalid, null, null, contents, configuration, error.Message); }
    }

    public static Hd2IASConfigKind Classify(string path) => path.Replace('\\', '/').ToLowerInvariant() switch
    {
        "bin/hd2independentrig/runtime.ini" or "bin/hd2independentrig/free_parts_selections.ini" or
            "bin/hd2independentrig/physics_control.ini" => Hd2IASConfigKind.UserSettings,
        "bin/hd2independentrig/helmet_manifest.ini" or "bin/hd2independentrig/transmog_manifest.ini"
            => Hd2IASConfigKind.SystemCatalog,
        _ => Hd2IASConfigKind.Unclassified
    };

    public static bool PreserveExisting(string path) => path.Replace('\\', '/').StartsWith("bin/HD2IndependentRig/",
        StringComparison.OrdinalIgnoreCase) && path.EndsWith(".ini", StringComparison.OrdinalIgnoreCase) &&
        Classify(path) != Hd2IASConfigKind.SystemCatalog;
}
