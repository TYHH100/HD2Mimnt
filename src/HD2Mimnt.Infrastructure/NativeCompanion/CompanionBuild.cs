namespace HD2Mimnt.Infrastructure.NativeCompanion;

/// <summary>模组包根目录 BUILD.json 声明的原生伴生组件构建元数据（哈希钉扎与签名指纹）。</summary>
public sealed record CompanionBuild(
    string Version,
    string NativePath,
    string NativeSha256,
    string NativeThumbprint,
    string NativeVersion,
    string? PatchSha256,
    bool RequiresSignature);

/// <summary>读取模组包 BUILD.json；缺失或字段不完整一律视为“无伴生组件”，不抛异常。</summary>
public static class CompanionBuildReader
{
    public const string BuildFileName = "BUILD.json";

    /// <summary>解析伴生构建元数据；解析失败、字段缺失或 DLL 源文件不存在时返回 null。</summary>
    public static CompanionBuild? Read(string? modDirectory)
    {
        if (string.IsNullOrWhiteSpace(modDirectory)) return null;
        string path;
        string json;
        try
        {
            path = Path.Combine(modDirectory, BuildFileName);
            if (!File.Exists(path)) return null;
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var build = Parse(json);
        if (build == null) return null;
        // 源文件缺失时同样视为无伴生载荷，调用方据此跳过安装而不是报错。
        return ResolveSourcePath(modDirectory, build) == null ? null : build;
    }

    /// <summary>解析包内源 DLL：官方包的 BUILD.json 只写文件名（install.ps1 自行拼接 Native/ 前缀），
    /// 因此先按相对路径找，再回退到 Native/ 子目录。</summary>
    public static string? ResolveSourcePath(string modDirectory, CompanionBuild build)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(modDirectory, build.NativePath.Replace('/', '\\')),
                     Path.Combine(modDirectory, "Native", build.NativePath.Replace('/', '\\'))
                 })
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                or ArgumentException)
            {
                return null;
            }
        }
        return null;
    }

    public static string NativeSourcePath(string modDirectory, CompanionBuild build)
        => ResolveSourcePath(modDirectory, build)
           ?? Path.Combine(modDirectory, build.NativePath.Replace('/', '\\'));

    private static CompanionBuild? Parse(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            var nativePath = GetRequiredString(root, "native_path");
            var nativeSha256 = GetRequiredString(root, "native_sha256");
            var thumbprint = GetRequiredString(root, "native_thumbprint");
            var version = GetRequiredString(root, "version");
            if (nativePath == null || nativeSha256 == null || thumbprint == null || version == null) return null;
            var nativeVersion = root.TryGetProperty("native_version", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString() : null;
            var patchSha256 = root.TryGetProperty("patch_sha256", out var patch) && patch.ValueKind == System.Text.Json.JsonValueKind.String
                ? patch.GetString() : null;
            var requiresSignature = root.TryGetProperty("requires_signature", out var signature)
                && signature.ValueKind == System.Text.Json.JsonValueKind.True;
            return new CompanionBuild(version, nativePath, nativeSha256, thumbprint, nativeVersion ?? "", patchSha256, requiresSignature);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string? GetRequiredString(System.Text.Json.JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() : null;
}
