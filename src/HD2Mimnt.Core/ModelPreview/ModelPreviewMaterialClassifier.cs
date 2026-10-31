namespace HD2Mimnt.Core.ModelPreview;

/// <summary>
/// How the preview renderer should shade a material section. The game resolves the real
/// shader program at runtime; the preview cannot execute modded shader binaries, so this
/// classification approximates each shader family's visible outcome with the generic
/// preview shader (albedo map + optional emissive map + simple lighting).
/// </summary>
public enum ModelPreviewMaterialRenderClass
{
    /// <summary>Default lit shading with alpha cutout from the color map (armor, weapons).</summary>
    Opaque = 0,

    /// <summary>Additive glow panels (emissive/bloom/reticle): unlit, blended additively so
    /// black texels contribute nothing — the closest static stand-in for the game's additive pass.</summary>
    Emissive,

    /// <summary>Glass/translucent/flowing surfaces: unlit and alpha-blended even when the
    /// color map carries no alpha channel.</summary>
    Translucent,

    /// <summary>Scope overlays and other HUD surfaces: opaque geometry rendered without lighting.</summary>
    Unlit
}

/// <summary>
/// Classifies material resources into a <see cref="ModelPreviewMaterialRenderClass"/>.
/// Primary signal is the parent material ID stored at offset 0x18 of the material resource
/// (layout documented in CompatibilityResourceTransforms) — it identifies the shader family
/// a material was authored against. Fingerprints below were extracted from the stock game
/// materials shipped in the HD2SDK Community Edition and AQ Edition "materials" folders.
/// </summary>
public static class ModelPreviewMaterialClassifier
{
    private static readonly IReadOnlyDictionary<ulong, ModelPreviewMaterialRenderClass> Catalog =
        new Dictionary<ulong, ModelPreviewMaterialRenderClass>
        {
            // Emissive/bloom family: Community Edition "emissive" and AQ Edition "bloom" share
            // this parent (the legacy emissive template, also referenced by CompatibilityResourceTransforms).
            [0xD3701FC725106C09] = ModelPreviewMaterialRenderClass.Emissive,
            [0x2D488B210A54DF9D] = ModelPreviewMaterialRenderClass.Emissive, // AQ reticle

            [0x84E5DE1CC6B74ADA] = ModelPreviewMaterialRenderClass.Translucent, // CE translucent / AQ glass
            [0xE2CC05A4988B951D] = ModelPreviewMaterialRenderClass.Translucent, // AQ flowing
            [0x110199A9C3401242] = ModelPreviewMaterialRenderClass.Translucent, // AQ translucent_flowing

            [0x4CE07F2F0F432035] = ModelPreviewMaterialRenderClass.Unlit, // CE scope
            [0x6B9F33361B025760] = ModelPreviewMaterialRenderClass.Unlit, // AQ cutout_scope

            // Opaque lit families. alphaclip+ (0xC6042E3403385D40) equals CompatibilityResourceTransforms's
            // "current emissive" armor template: it is modern armor with opacity clip and optional
            // emissive detail, NOT an additive glow surface — the renderer still samples its emissive
            // map, but the section itself must stay lit.
            [0xEF9B3FC2FC1BA61E] = ModelPreviewMaterialRenderClass.Opaque, // advanced (CE advanced + all AQ advanced_* color variants)
            [0xD51D2A873865D4E5] = ModelPreviewMaterialRenderClass.Opaque, // basic
            [0x7712EE7808E0EC50] = ModelPreviewMaterialRenderClass.Opaque, // modern basic+ (CE basic+, CE basic+OLD_SEAF, AQ basic+Fixed)
            [0x2F9C50D9129837FE] = ModelPreviewMaterialRenderClass.Opaque, // legacy basic+ (CE basic+OLD, AQ basic+)
            [0xD84D04634A1E2F60] = ModelPreviewMaterialRenderClass.Opaque, // alphaclip
            [0xC6042E3403385D40] = ModelPreviewMaterialRenderClass.Opaque, // alphaclip+ (modern armor)
            [0xF5EBD93181FDF00C] = ModelPreviewMaterialRenderClass.Opaque, // armorlut
            [0x54AE9CE1A8FAFE8B] = ModelPreviewMaterialRenderClass.Opaque, // original (Community Edition)
            [0x8F669F365F24594E] = ModelPreviewMaterialRenderClass.Opaque, // original (AQ Edition)
        };

    /// <summary>
    /// Catalog lookup first, then a semantic fallback for materials whose parent ID is missing
    /// or unknown (modded templates not in the SDK material sets): a material that binds an
    /// emissive map but no base color is almost certainly a glow surface, everything else
    /// renders as regular lit opaque geometry.
    /// </summary>
    public static ModelPreviewMaterialRenderClass Classify(
        ulong? parentMaterialId,
        ModelPreviewMaterialTextureSet? textures)
    {
        if (parentMaterialId is { } id && Catalog.TryGetValue(id, out var renderClass))
            return renderClass;

        if (textures is not null &&
            textures.Get(ModelPreviewTextureRole.Emissive).Count > 0 &&
            textures.Get(ModelPreviewTextureRole.BaseColor).Count == 0)
            return ModelPreviewMaterialRenderClass.Emissive;

        return ModelPreviewMaterialRenderClass.Opaque;
    }
}
