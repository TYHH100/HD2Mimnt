using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Vmt;

namespace HD2Mimnt.ModelConversion.Gmod.Materials;

/// <summary>
/// 把 VMT 的全部参数、条件、代理与贴图引用转成 HD2 材质描述；原始 VMT/VTF 字节在产物中逐字节保留。
/// 通道映射是明确的角色建议，不假装把 Source 的 shader/proxy 运行时行为编译进 HD2。
/// </summary>
public static class GmodMaterialBuilder
{
    private static readonly IReadOnlyDictionary<string, string> ChannelRoles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["$basetexture"] = "BaseColor",
            ["$basetexture2"] = "BaseColorSecondary",
            ["$bumpmap"] = "Normal",
            ["$bumpmap2"] = "NormalSecondary",
            ["$normalmap"] = "Normal",
            ["$detail"] = "Detail",
            ["$envmapmask"] = "MetallicRoughnessAO",
            ["$envmapmask2"] = "MetallicRoughnessAOSecondary",
            ["$phongexponenttexture"] = "Roughness",
            ["$phongwarptexture"] = "SpecularWarp",
            ["$lightwarptexture"] = "LightWarp",
            ["$selfillummask"] = "Emission",
            ["$blendmodulatetexture"] = "BlendMask",
            ["$dudvmap"] = "Distortion",
            ["$flowmap"] = "Flow",
            ["$flow_noise_texture"] = "FlowNoise",
            ["$emissiveblendtexture"] = "EmissionBlend",
            ["$emissiveblendflowtexture"] = "EmissionFlow",
            ["$emissiveblendbasetexture"] = "EmissionBase",
            ["$refracttexture"] = "Refraction",
            ["$reflecttexture"] = "Reflection",
            ["$envmap"] = "Environment"
        };

    public static (IReadOnlyList<GmodMaterial> Materials, IReadOnlyList<GmodTexture> Textures,
        IReadOnlyList<GmodConversionIssue> Issues) Build(SourceModelPackage package,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var materials = new List<GmodMaterial>();
        var textures = new List<GmodTexture>();
        var issues = new List<GmodConversionIssue>();

        foreach (var asset in package.Materials.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vmt = asset.Value.Material;
            if (vmt is null)
            {
                issues.Add(new GmodConversionIssue("MaterialNotParsed", asset.Key,
                    "VMT could not be parsed; the original bytes are retained verbatim."));
                materials.Add(new GmodMaterial(asset.Key, "", false, null, [], [],
                    new Dictionary<string, string>(StringComparer.Ordinal)));
                continue;
            }
            var parameters = new List<GmodMaterialParameter>();
            Collect(vmt.Root, null, parameters);
            var references = new List<GmodMaterialTexture>();
            var channels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reference in vmt.TextureReferences)
            {
                var name = Normalize(reference.Path);
                if (name.Equals("env_cubemap", StringComparison.OrdinalIgnoreCase) || name.StartsWith('_'))
                    continue; // Runtime textures are Source-side only; no VTF exists in the addon.
                var sourcePath = VtfPath(reference.Path);
                var role = ChannelRoles.GetValueOrDefault(reference.Parameter, "Auxiliary");
                references.Add(new GmodMaterialTexture(role, reference.Parameter, sourcePath, PngPath(sourcePath)));
                if (!channels.ContainsKey(role)) channels[role] = sourcePath;
                else channels[role] += "|" + sourcePath;
            }
            if (vmt.IsPatch)
                issues.Add(new GmodConversionIssue("PatchMaterialRetained", asset.Key,
                    "Patch/include and conditional parameters are preserved as authored; the converter does not evaluate Source shader conditions."));
            materials.Add(new GmodMaterial(asset.Key, vmt.Shader, vmt.IsPatch, vmt.GetParameter("include"),
                parameters.AsReadOnly(), references.AsReadOnly(), channels));
        }

        foreach (var asset in package.Textures.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vtf = asset.Value.Texture;
            if (vtf is null)
            {
                textures.Add(new GmodTexture(asset.Key, 0, 0, -1, 0, false, null,
                    "VTF could not be parsed; original bytes are retained verbatim."));
                issues.Add(new GmodConversionIssue("TextureNotParsed", asset.Key,
                    "VTF header could not be parsed; original bytes are retained verbatim."));
                continue;
            }
            var (rgba, width, height, decodeIssue) = GmodTextureDecoder.DecodePixels(vtf);
            var png = rgba is null ? null : PngWriter.Write(rgba, width, height);
            textures.Add(new GmodTexture(asset.Key, vtf.Width, vtf.Height, vtf.Format, vtf.MipCount,
                vtf.IsCubeMap, png, decodeIssue) { Rgba = rgba });
            if (decodeIssue is not null)
                issues.Add(new GmodConversionIssue("TextureDecodeFallback", asset.Key, decodeIssue, GmodIssueSeverity.Info));
        }

        return (materials.AsReadOnly(), textures.AsReadOnly(), issues.AsReadOnly());
    }

    private static void Collect(VmtNode node, string? group, List<GmodMaterialParameter> output)
    {
        var currentGroup = node.Value is null ? group ?? node.Key : group;
        foreach (var child in node.Children)
        {
            if (child.Value is not null)
                output.Add(new GmodMaterialParameter(child.Key, child.Value, child.Condition, currentGroup));
            Collect(child, currentGroup, output);
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim().TrimStart('/');

    /// <summary>把 MDL 解析出的材质名解析成 addon 内的 VMT 相对路径。</summary>
    public static string VmtPath(string reference)
    {
        var path = Normalize(reference);
        if (!path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)) path = "materials/" + path;
        if (path.EndsWith(".vmt", StringComparison.OrdinalIgnoreCase)) return path;
        if (path.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        return path + ".vmt";
    }

    /// <summary>把 VMT 的贴图引用解析成 addon 内的 VTF 相对路径（与 SourceModelPackage 一致）。</summary>
    public static string VtfPath(string reference)
    {
        var path = Normalize(reference);
        if (!path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)) path = "materials/" + path;
        if (!path.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)) path += ".vtf";
        return path;
    }

    /// <summary>把 Source 贴图路径转成产物内稳定的 PNG 相对路径。</summary>
    public static string PngPath(string sourcePath)
    {
        var normalized = Normalize(sourcePath);
        if (normalized.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
        return "textures/" + SafePath(normalized) + ".png";
    }

    /// <summary>把任意 addon 相对路径转成安全、确定、无冲突的输出路径。</summary>
    public static string SafePath(string path)
    {
        var normalized = Normalize(path);
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(segment =>
        {
            var chars = segment.Select(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_').ToArray();
            var value = new string(chars).Trim('.', '_');
            return value.Length == 0 ? "unnamed" : value;
        });
        return string.Join('/', segments);
    }
}
