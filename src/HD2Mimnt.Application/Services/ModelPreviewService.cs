using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.ModelPreview;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

public sealed class ModelPreviewService(IModRepository mods, IProfileRepository profiles, IModelPreviewReader reader,
    ILogger<ModelPreviewService>? logger = null)
{
    public const int OriginalTextureResolution = 0;
    private const int TextureBudgetBytes = 64 * 1024 * 1024;
    // 预算必须覆盖“贴图数量 × 单张需求”：少一张贴图就是一个无材质网格，比整体降分
    // 辨率严重得多。材质包模组每个变体都带独立贴图组，固定预算在 2048/Original 档会
    // 把后排贴图整体丢弃（实测袜子等部位变成无材质）。超过硬上限时不再丢图，改为把
    // 剩余预算均摊给剩余贴图均匀降档；渲染端另有 GPU 最大尺寸保护。
    private const long MaxTextureBudgetBytes = 2L * 1024 * 1024 * 1024;

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
        if (maxDimension is not (OriginalTextureResolution or 512 or 1024 or 2048))
            throw new ArgumentOutOfRangeException(nameof(maxDimension));
        var mod = await GetModAsync(modId, cancellationToken);
        var index = ModelPreviewTextureIndex.Create(textures);
        var result = new Dictionary<ulong, TexturePreviewData>();
        var requested = new List<(ulong Id, TextureInspectionItem Texture, long Pixels)>();
        foreach (var id in textureIds.Distinct())
        {
            if (!index.TryGetValue(id, out var texture)) continue;
            requested.Add((id, texture, maxDimension == OriginalTextureResolution
                ? Math.Max((long)texture.Width * texture.Height, 256)
                : (long)maxDimension * maxDimension));
        }

        // 用户显式选择分辨率即承诺每张贴图都按该分辨率解码，总预算随请求总量放大；
        // 只有超过硬上限时才按剩余份额降档。
        var requestedPixels = requested.Sum(static item => item.Pixels);
        var budget = Math.Min(Math.Max(TextureBudgetBytes, requestedPixels * 4), MaxTextureBudgetBytes);
        long bytes = 0;
        for (var position = 0; position < requested.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget - bytes < 256 * 4) break;
            var (id, texture, requestedForTexture) = requested[position];
            var remainingCount = requested.Count - position;
            var share = Math.Max((budget - bytes) / 4 / remainingCount, 256);
            var pixelBudget = (int)Math.Min(requestedForTexture, share);
            TexturePreviewData? decoded;
            try
            {
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
            if (size > budget - bytes) continue;
            result.Add(id, decoded);
            bytes += size;
        }
        return result;
    }

    private async Task<Mod> GetModAsync(Guid id, CancellationToken token) =>
        await mods.GetByIdAsync(id, token) ?? throw new KeyNotFoundException($"Mod {id} not found.");
}

public sealed record ModelPreviewSource(Guid ModId, string Name, ModManifest? Manifest, ProfileModOptionsState Options);
