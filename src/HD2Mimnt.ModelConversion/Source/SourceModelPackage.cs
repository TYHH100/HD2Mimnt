using HD2Mimnt.ModelConversion.Source.Gma;
using HD2Mimnt.ModelConversion.Source.Vmt;
using HD2Mimnt.ModelConversion.Source.Vtf;

namespace HD2Mimnt.ModelConversion.Source;

public sealed record SourceReadIssue(string Code, string AssetPath, string Message);
public sealed record SourceMaterialAsset(string Path, ReadOnlyMemory<byte> RawData, VmtFile? Material);
public sealed record SourceTextureAsset(string Path, ReadOnlyMemory<byte> RawData, VtfFile? Texture);

public sealed class SourceModelPackage
{
    private SourceModelPackage(SourceModel model, IReadOnlyList<SourceModelGeometry> alternatives,
        Dictionary<int, string?> materialPaths, Dictionary<string, SourceMaterialAsset> materials,
        Dictionary<string, SourceTextureAsset> textures, byte[]? physicsData, List<SourceReadIssue> issues)
    {
        Model = model;
        Alternatives = alternatives;
        MaterialPaths = materialPaths.AsReadOnly();
        Materials = materials.AsReadOnly();
        Textures = textures.AsReadOnly();
        if (physicsData is not null) PhysicsData = new ReadOnlyMemory<byte>(physicsData);
        Issues = issues.AsReadOnly();
    }

    public SourceModel Model { get; }
    public IReadOnlyList<SourceModelGeometry> Alternatives { get; }
    public IReadOnlyDictionary<int, string?> MaterialPaths { get; }
    public IReadOnlyDictionary<string, SourceMaterialAsset> Materials { get; }
    public IReadOnlyDictionary<string, SourceTextureAsset> Textures { get; }
    public ReadOnlyMemory<byte>? PhysicsData { get; }
    public IReadOnlyList<SourceReadIssue> Issues { get; }

    public static Task<SourceModelPackage> LoadAsync(ISourceAssetProvider provider, string mdlPath,
        CancellationToken cancellationToken = default) => Task.Run(() => Load(provider, mdlPath, cancellationToken), cancellationToken);

    public static SourceModelPackage Load(ISourceAssetProvider provider, string mdlPath, CancellationToken cancellationToken = default)
    {
        var model = SourceModel.Load(provider, mdlPath, cancellationToken);
        var alternatives = model.GetAllAlternatives(cancellationToken);
        var materials = new Dictionary<string, SourceMaterialAsset>(StringComparer.OrdinalIgnoreCase);
        var textures = new Dictionary<string, SourceTextureAsset>(StringComparer.OrdinalIgnoreCase);
        var materialPaths = new Dictionary<int, string?>();
        var issues = model.Warnings.Select(w => new SourceReadIssue("ModelWarning", mdlPath, w)).ToList();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in model.Mdl.Textures)
        {
            var found = false;
            foreach (var directory in model.Mdl.TextureDirectories.Append(string.Empty).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = MaterialPath($"{directory.TrimEnd('/', '\\')}/{texture.Name}".TrimStart('/'), ".vmt");
                if (!provider.TryRead(path, out var bytes, cancellationToken)) continue;
                materialPaths[texture.Index] = path;
                ReadMaterial(path, bytes, 0);
                found = true;
                break;
            }
            if (!found)
            {
                materialPaths[texture.Index] = null;
                issues.Add(new SourceReadIssue("MissingMaterial", texture.Name, "Material not found in any MDL search directory."));
            }
        }
        var phyPath = model.ModelPath[..^4] + ".phy";
        byte[]? physics = provider.TryRead(phyPath, out var phy, cancellationToken) ? phy : null;
        if (physics is not null)
            issues.Add(new SourceReadIssue("PhysicsRetainedRaw", phyPath, "Source collision/ragdoll data is retained; it is not an IAS physics profile."));
        issues.Add(new SourceReadIssue("RuntimeNotConverted", mdlPath,
            "Geometry, material parameters and jiggle parameters are source data only. Shader/proxy behavior, flexes, animation, Lua and physics simulation are not converted."));
        return new SourceModelPackage(model, alternatives, materialPaths, materials, textures, physics, issues);

        void ReadMaterial(string path, byte[] bytes, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (visiting.Contains(path)) throw new SourceFormatException($"Cyclic VMT include: {path}");
            if (materials.ContainsKey(path)) return;
            if (depth > 32) throw new SourceFormatException("VMT includes exceed 32 levels");
            visiting.Add(path);
            VmtFile? material = null;
            try { material = VmtFile.Parse(bytes); }
            catch (SourceFormatException error) { issues.Add(new SourceReadIssue("UnsupportedMaterial", path, error.Message)); }
            materials[path] = new SourceMaterialAsset(path, bytes, material);
            if (material is not null)
            {
                var include = material.GetParameter("include");
                if (material.IsPatch && include is not null)
                {
                    var includePath = MaterialPath(include, ".vmt");
                    if (provider.TryRead(includePath, out var included, cancellationToken))
                        ReadMaterial(includePath, included, depth + 1);
                    else
                        issues.Add(new SourceReadIssue("MissingMaterialInclude", includePath, $"Referenced by {path}"));
                    issues.Add(new SourceReadIssue("MaterialEvaluationRequired", path,
                        "Patch and included materials are preserved separately; insert/replace and conditions are not evaluated."));
                }
                foreach (var reference in material.TextureReferences)
                {
                    var name = reference.Path.Trim();
                    if (name.Equals("env_cubemap", StringComparison.OrdinalIgnoreCase) || name.StartsWith('_'))
                    {
                        issues.Add(new SourceReadIssue("RuntimeTexture", path, $"{reference.Parameter}={name}"));
                        continue;
                    }
                    var texturePath = MaterialPath(name, ".vtf");
                    if (!visited.Add(texturePath)) continue;
                    if (!provider.TryRead(texturePath, out var textureBytes, cancellationToken))
                    {
                        issues.Add(new SourceReadIssue("MissingTexture", texturePath, $"Referenced by {path}: {reference.Parameter}"));
                        continue;
                    }
                    VtfFile? vtf = null;
                    try { vtf = VtfFile.Parse(textureBytes, cancellationToken); }
                    catch (SourceFormatException error) { issues.Add(new SourceReadIssue("UnsupportedTexture", texturePath, error.Message)); }
                    textures.Add(texturePath, new SourceTextureAsset(texturePath, textureBytes, vtf));
                    if (vtf is not null)
                        issues.AddRange(vtf.Warnings.Select(w => new SourceReadIssue("TextureLayoutUnverified", texturePath, w)));
                }
            }
            visiting.Remove(path);
        }
    }

    private static string MaterialPath(string name, string extension)
    {
        var path = GmaArchive.NormalizePath(name);
        if (!path.StartsWith("materials/", StringComparison.OrdinalIgnoreCase)) path = "materials/" + path;
        if (!path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) path += extension;
        return path;
    }
}
