namespace HD2Mimnt.Core.ModelPreview;

/// <summary>
/// The Unit main resource has an explicit MeshInfo table: every GPU stream can contain
/// multiple MeshInfo vertex windows and section index ranges. Keeping that relationship
/// prevents local indices from accidentally addressing the beginning of the whole stream
/// and preserves each mesh's material and Unit transform.
/// </summary>
public sealed record ModelPreviewMaterialLayout(
    IReadOnlyDictionary<int, IReadOnlyList<ModelPreviewMaterialSection>> SectionsByStream,
    IReadOnlyList<ulong> FallbackTextureIds,
    ulong? FallbackColorTextureId,
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot CustomizationSlot = ModelPreviewCustomizationSlot.Unknown,
    ModelPreviewUnitRig? Rig = null);

public sealed record ModelPreviewCustomizationInfo(
    ModelPreviewBodyShape BodyShape,
    ModelPreviewCustomizationSlot Slot);

public enum ModelPreviewBodyShape
{
    Unknown,
    Any,
    Slim,
    Stocky
}

public enum ModelPreviewCustomizationSlot
{
    Unknown,
    Torso,
    Hip,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
    LeftShoulder,
    RightShoulder
}

public static class ModelPreviewBodyShapeParser
{
    public static ModelPreviewBodyShape Parse(string? bodyType)
    {
        if (string.IsNullOrWhiteSpace(bodyType))
            return ModelPreviewBodyShape.Unknown;

        var value = bodyType.Trim();
        const string prefix = "HelldiverCustomizationBodyType_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];

        return value switch
        {
            "Any" => ModelPreviewBodyShape.Any,
            "Slim" => ModelPreviewBodyShape.Slim,
            "Stocky" => ModelPreviewBodyShape.Stocky,
            _ => ModelPreviewBodyShape.Unknown
        };
    }

    public static ModelPreviewCustomizationSlot ParseSlot(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return ModelPreviewCustomizationSlot.Unknown;

        var value = slot.Trim();
        const string prefix = "HelldiverCustomizationSlot_";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            value = value[prefix.Length..];

        return value switch
        {
            "Torso" => ModelPreviewCustomizationSlot.Torso,
            "Hip" => ModelPreviewCustomizationSlot.Hip,
            "LeftArm" => ModelPreviewCustomizationSlot.LeftArm,
            "RightArm" => ModelPreviewCustomizationSlot.RightArm,
            "LeftLeg" => ModelPreviewCustomizationSlot.LeftLeg,
            "RightLeg" => ModelPreviewCustomizationSlot.RightLeg,
            "LeftShoulder" => ModelPreviewCustomizationSlot.LeftShoulder,
            "RightShoulder" => ModelPreviewCustomizationSlot.RightShoulder,
            _ => ModelPreviewCustomizationSlot.Unknown
        };
    }
}

public sealed record ModelPreviewMaterialTextures(
    IReadOnlyList<ulong> TextureIds,
    ulong? ColorTextureId,
    IReadOnlyDictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>? TexturesByRole = null,
    IReadOnlyList<ModelPreviewMaterialInput>? Inputs = null,
    ulong? ParentMaterialId = null,
    bool ColorAlphaIsMetallic = false)
{
    public ModelPreviewMaterialTextureSet ToTextureSet() => new(
        TexturesByRole ?? new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>(),
        TextureIds,
        ColorTextureId,
        Inputs,
        ColorAlphaIsMetallic);
}

/// <summary>
/// One entry in Stingray's parallel material semantic and texture-ID tables.
/// Retaining the semantic hash preserves evidence for material types that the preview
/// does not yet classify into a render role.
/// </summary>
public sealed record ModelPreviewMaterialInput(
    uint SemanticId,
    ulong TextureId,
    ModelPreviewTextureRole Role);

/// <summary>
/// Semantic texture inputs for one material section. A Stingray material can reference
/// several textures; keeping the semantic grouping in the asset graph prevents the
/// renderer from selecting a random normal or mask texture as the visible color map.
/// </summary>
public sealed record ModelPreviewMaterialTextureSet(
    IReadOnlyDictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>> ByRole,
    IReadOnlyList<ulong> AllTextureIds,
    ulong? ColorTextureId,
    IReadOnlyList<ModelPreviewMaterialInput>? Inputs = null,
    /// <summary>BaseColorMetalMap 这类颜色贴图的 Alpha 是金属度而不是透明度：预览既要用它
    /// 取颜色，也要用它取金属，因此单独标记，同时它的 Alpha 不能走假 alpha 回填。</summary>
    bool ColorAlphaIsMetallic = false)
{
    public static ModelPreviewMaterialTextureSet Empty { get; } = new(
        new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>(),
        [],
        null);

    public IReadOnlyList<ulong> Get(ModelPreviewTextureRole role) =>
        ByRole.TryGetValue(role, out var textures) ? textures : [];

    public IEnumerable<ulong> EnumerateRenderableInputs()
    {
        foreach (var role in new[]
        {
            ModelPreviewTextureRole.BaseColor,
            ModelPreviewTextureRole.Emissive,
            ModelPreviewTextureRole.Iridescence,
            ModelPreviewTextureRole.Mask,
            ModelPreviewTextureRole.Normal,
            ModelPreviewTextureRole.NormalOcclusionRoughness,
            ModelPreviewTextureRole.NormalRoughnessOpacity,
            ModelPreviewTextureRole.MetallicRoughnessOcclusion
        })
        {
            foreach (var textureId in Get(role))
                yield return textureId;
        }
    }
}

public enum ModelPreviewTextureRole
{
    Unknown,
    BaseColor,
    Normal,

    /// <summary>HD2 的 Nar / NormalXyAoRoughMap：R、G 是切线空间法线的 xy，B 是环境光遮蔽，
    /// A 是粗糙度。通道顺序来自 SDK 语义名（词序即通道序，双分量法线占 RG），并由游戏
    /// 本体贴图的实测分布交叉验证。它的 A 不是透明度。</summary>
    NormalOcclusionRoughness,

    /// <summary>Mra：R 金属度、G 粗糙度、B 环境光遮蔽，Alpha 未使用。注意与 glTF 的 ORM
    /// 约定（R=遮蔽、G=粗糙、B=金属）顺序不同，直接套用会把金属和遮蔽颠倒。</summary>
    MetallicRoughnessOcclusion,

    /// <summary>NormalXyRoughnessOpacity（玻璃/透明件常用，往往还是材质唯一的贴图输入）：
    /// R、G 是切线空间法线 xy，B 是粗糙度，A 是不透明度。与 Nar 的区别是遮蔽换成粗糙度
    /// 占 B，A 才是透明度，所以着色采样和通用假 alpha 回填都要按另一种布局处理。</summary>
    NormalRoughnessOpacity,
    Mask,
    Emissive,
    Iridescence
}

public sealed record ModelPreviewMaterialSection(
    int MeshInfoIndex,
    int SectionIndex,
    uint VertexOffset,
    uint VertexCount,
    uint IndexOffset,
    uint IndexCount,
    IReadOnlyList<ulong> TextureIds,
    ulong? ColorTextureId,
    bool IsCullingBody,
    ModelPreviewTransform Transform,
    ModelPreviewMaterialTextureSet? MaterialTextures = null,
    ulong? MaterialId = null,
    int LodIndex = -1,
    int MaterialIndex = 0,
    /// <summary>MeshInfo 的挂接变换索引。Unit 变换表与骨骼层级共用同一张表，
    /// 因此对无顶点蒙皮数据的刚性挂接件（弹挂、尾巴、徽章等），该索引即其在
    /// 骨架中的挂接骨骼；负值表示缺失。</summary>
    int TransformIndex = -1,
    ModelPreviewMaterialRenderClass RenderClass = ModelPreviewMaterialRenderClass.Opaque);

public readonly record struct ModelPreviewTransform(
    float M11, float M12, float M13, float M14,
    float M21, float M22, float M23, float M24,
    float M31, float M32, float M33, float M34)
{
    public static ModelPreviewTransform Identity { get; } = new(
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0);

    public (float X, float Y, float Z) TransformPoint(float x, float y, float z) => (
        M11 * x + M12 * y + M13 * z + M14,
        M21 * x + M22 * y + M23 * z + M24,
        M31 * x + M32 * y + M33 * z + M34);
}
