using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.ModelPreview;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

public sealed class ModelPreviewService(IModRepository mods, IProfileRepository profiles, IModelPreviewReader reader,
    ILogger<ModelPreviewService>? logger = null)
{
    public async Task<ModelPreviewSource> GetSourceAsync(Guid modId, Guid? profileId = null, CancellationToken cancellationToken = default)
    {
        var mod = await GetModAsync(modId, cancellationToken);
        string? options = null;
        if (profileId is { } id)
        {
            var profile = await profiles.GetByIdAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException($"Profile {id} not found.");
            options = (profile.Mods.FirstOrDefault(item => item.ModId == modId)
                ?? throw new KeyNotFoundException($"Mod {modId} not in profile {id}.")).OptionsJson;
        }
        return new(mod.Id, mod.Name, mod.Manifest, ProfileModOptionResolver.ResolveEffective(mod.Manifest, options));
    }

    public async Task<ModelPreviewResult> LoadAsync(Guid modId, string optionsJson, CancellationToken cancellationToken = default)
    {
        var mod = await GetModAsync(modId, cancellationToken);
        return await Task.Run(async () =>
        {
            var files = PatchDeploymentPlanner.BuildFiles(
                PatchDeploymentPlanner.CollectSelectedFiles(mod, optionsJson, cancellationToken),
                new(StringComparer.OrdinalIgnoreCase), cancellationToken, mod.Path);
            var patches = files.Where(file => file.Suffix.Length == 0).Select(file => new FileInfo(file.SourcePath!)).ToArray();
            var result = await reader.PreviewModelAsync(new DirectoryInfo(mod.Path), patches, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var skeleton in result.Meshes.Select(mesh => mesh.Skinning?.Skeleton).OfType<ModelPreviewSkeleton>()
                         .DistinctBy(skeleton => (skeleton.BonesId, skeleton.StateMachineId)))
            {
                var library = ModelPreviewModAnimationLibraryBuilder.TryBuild(result.PatchAnimationResources, skeleton.BonesId, skeleton.StateMachineId);
                if (library is not null) result.AnimationLibraries.Add(library);
            }
            result.PatchAnimationResources.Clear();
            return result;
        }, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<ulong, TexturePreviewData>> LoadTexturesAsync(Guid modId,
        IReadOnlyList<TextureInspectionItem> textures, IReadOnlyCollection<ulong> textureIds,
        int maxDimension = 1024, CancellationToken cancellationToken = default)
    {
        if (maxDimension is not (512 or 1024 or 2048)) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        var mod = await GetModAsync(modId, cancellationToken);
        var index = ModelPreviewTextureIndex.Create(textures);
        var result = new Dictionary<ulong, TexturePreviewData>();
        long bytes = 0;
        foreach (var id in textureIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes >= 64 * 1024 * 1024) break;
            if (!index.TryGetValue(id, out var texture)) continue;
            TexturePreviewData? decoded;
            try
            {
                var pixelBudget = (int)Math.Min(maxDimension * maxDimension, (64 * 1024 * 1024 - bytes) / 4);
                if (pixelBudget < 256) break;
                decoded = await reader.PreviewTextureAsync(new DirectoryInfo(mod.Path), texture,
                    pixelBudget, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                logger?.LogWarning(error, "Texture {TextureId:x16} could not be previewed", id);
                continue;
            }
            if (decoded is null) continue;
            var size = decoded.BgraPixels?.LongLength ?? (long)decoded.Width * decoded.Height * 4;
            if (size > 64 * 1024 * 1024 - bytes) continue;
            result.Add(id, decoded);
            bytes += size;
        }
        return result;
    }

    private async Task<Mod> GetModAsync(Guid id, CancellationToken token) =>
        await mods.GetByIdAsync(id, token) ?? throw new KeyNotFoundException($"Mod {id} not found.");
}

public sealed record ModelPreviewSource(Guid ModId, string Name, ModManifest? Manifest, ProfileModOptionsState Options);
