using System.Numerics;
using HD2Mimnt.ModelConversion.Gmod.Parts;
using HD2Mimnt.ModelConversion.Gmod.Physics;
using HD2Mimnt.ModelConversion.Gmod.Targets;
using HD2Mimnt.ModelConversion.Ias;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.ModelConversion.Gmod;

public enum GmodIssueSeverity { Info, Warning, Error }

public sealed record GmodConversionIssue(string Code, string Asset, string Message,
    GmodIssueSeverity Severity = GmodIssueSeverity.Warning);

/// <summary>
/// 开关发布方式。Independent 要求每个开关拥有互不重叠的 Native Unit 集合；
/// 多个开关共用同一套模型资源时（GMOD 的常见情况）必须改用 Composite 组合开关。
/// </summary>
public enum GmodSwitchMode { Auto, Independent, Composite }

public sealed record GmodConversionOptions(
    string ProjectId,
    IasArchiveBinding Binding,
    string? IsolationId = null,
    string? ManifestId = null,
    GmodSwitchMode SwitchMode = GmodSwitchMode.Auto,
    bool IncludeSkinSwitch = true,
    bool IncludeRawSource = true,
    bool IncludePhysics = true,
    int MaxRawSourceBytes = 256 * 1024 * 1024,
    float FixedDt = 1f / 60f,
    IReadOnlyDictionary<int, IasPartSlot>? BodyGroupSlots = null,
    IReadOnlyDictionary<string, string>? BoneNameOverrides = null,
    IReadOnlyDictionary<string, IReadOnlyList<ulong>>? NativeUnitsByAxis = null,
    IReadOnlyList<IasPartSlot>? PreferredSlots = null,
    byte[]? RigNeedle = null,
    GmodPartSplit PartSplit = GmodPartSplit.Both,
    ulong? HelmetArchiveId = null,
    IReadOnlyList<string>? ExposedAxes = null,
    int MaxSwitchOptions = 256,
    IReadOnlyList<ulong>? CarrierUnitIds = null,
    IReadOnlyList<GmodArmorTarget>? Targets = null,
    string? IasProjectId = null)
{
    /// <summary>
    /// 运行时项目身份（32 位十六进制）。游戏与管理器按 <c>{project}.{archive}.rigbin</c>
    /// 解析骨架归属，项目名必须落到这个形状，否则配套包里的 rig 认不出 Archive。
    /// </summary>
    public string EffectiveIasProjectId => IasIdentity.Project(IasProjectId ?? ProjectId);

    /// <summary>隔离清单 ID：默认按「项目 + Archive」派生，文件里外的名字因此始终一致。</summary>
    public string EffectiveIsolationId =>
        IsolationId ?? IasIdentity.Owned(EffectiveIasProjectId, Binding.ArchiveId);

    /// <summary>差分清单 ID：默认 <c>hd2free_</c> 稳定标签。</summary>
    public string EffectiveManifestId =>
        ManifestId ?? IasIdentity.Difference(EffectiveIasProjectId, Binding.ArchiveId);

    /// <summary>头盔包的 Archive ID：未显式指定时由身体 Archive ID 派生，保证两包资源 ID 不撞车。</summary>
    public ulong DeriveHelmetArchiveId()
    {
        var seed = Hd2PhysCodec.Fnv1A64(System.Text.Encoding.UTF8.GetBytes(
            $"helmet|{ProjectId}|{Binding.ArchiveId:x16}"));
        var value = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;
        return value == Binding.ArchiveId ? (value == ulong.MaxValue ? 1 : value + 1) : value;
    }
}

/// <summary>完整保留的 Source 顶点：位置、法线、UV、切线、最多三路骨骼权重。</summary>
public sealed record GmodVertex(
    Vector3 Position,
    Vector3 Normal,
    Vector2 TexCoord,
    Vector4 Tangent,
    float[] Weights,
    byte[] Bones);

public sealed record GmodBone(
    int Index,
    string Name,
    int Parent,
    Vector3 Position,
    Quaternion Rotation,
    float[] PoseToBone,
    uint Flags,
    int ProceduralType,
    int PhysicsBone,
    string SurfaceProp,
    MdlJiggleBone? Jiggle);

public sealed record GmodAttachment(int Index, string Name, int Flags, int LocalBone, float[] LocalMatrix);

/// <summary>一个 bodygroup 备选 × skin 家族 × 原始 mesh 的完整几何。</summary>
public sealed record GmodMesh(
    string Id,
    int BodyPart,
    int Model,
    int Skin,
    int Mesh,
    int MaterialSlot,
    string MaterialPath,
    string ModelName,
    GmodVertex[] Vertices,
    int[] Indices)
{
    public int TriangleCount => Indices.Length / 3;
}

/// <summary>一个 IAS 选项对应的完整模型组合，键为 <c>bodygroup:N</c> 或 <c>skin</c>。</summary>
public sealed record GmodOptionGeometry(
    string OptionId,
    IReadOnlyDictionary<string, int> Choices,
    IReadOnlyList<string> MeshIds);

public sealed record GmodGeometryBundle(
    string ModelPath,
    string VtxExtension,
    IReadOnlyList<GmodBone> Bones,
    IReadOnlyList<GmodMesh> Meshes,
    IReadOnlyList<GmodAttachment> Attachments,
    IReadOnlyList<GmodOptionGeometry> Options,
    int LodCount,
    int SkinFamilyCount,
    IReadOnlyList<GmodConversionIssue> Issues);

public sealed record GmodMaterialParameter(string Key, string Value, string? Condition, string? Group);

public sealed record GmodMaterialTexture(string Role, string Parameter, string SourcePath, string? OutputPath);

/// <summary>VMT 全量参数 + Source→HD2 通道角色映射；RawData 由产物中的原始 VMT 逐字节保留。</summary>
public sealed record GmodMaterial(
    string Path,
    string Shader,
    bool IsPatch,
    string? Include,
    IReadOnlyList<GmodMaterialParameter> Parameters,
    IReadOnlyList<GmodMaterialTexture> Textures,
    IReadOnlyDictionary<string, string> ChannelMap);

public sealed record GmodTexture(
    string Path,
    int Width,
    int Height,
    int Format,
    int MipCount,
    bool IsCubeMap,
    byte[]? Png,
    string? DecodeIssue)
{
    /// <summary>mip0 的 RGBA8 像素；写游戏原生贴图资源时需要（PNG 只是中间产物）。</summary>
    public byte[]? Rgba { get; init; }
}

public enum GmodSwitchKind { BodyGroup, Skin, Composite }

public sealed record GmodSwitchOption(string Id, string DisplayName, IReadOnlyDictionary<string, int> Choices);

public sealed record GmodSwitch(
    string Id,
    string DisplayName,
    GmodSwitchKind Kind,
    IasPartSlot Slot,
    IReadOnlyList<GmodSwitchOption> Options);

/// <summary>单个已发布 Unit 的 HD2PHY1 物理资源。</summary>
public sealed record GmodPhysicsProfile(string FileName, ulong UnitId, byte[] Bytes, Hd2PhysSummary Summary);

public sealed record GmodOutputFile(string Path, string Kind, ReadOnlyMemory<byte> Data, string Sha256);

/// <summary>单个部位（身体或头盔）的转换结果；<see cref="Prefix"/> 为空表示单包模式。</summary>
public sealed record GmodConversionPart(
    string Prefix,
    GmodPartKind Kind,
    IasContentDomain Domain,
    ulong ArchiveId,
    IasFreePartsDocument Differences,
    IasRigDocument Rig,
    GmodGeometryBundle Geometry,
    IReadOnlyList<GmodSwitch> Switches,
    IReadOnlyList<GmodPhysicsProfile> Physics)
{
    /// <summary>接管目标护甲整身时，闭包里除载体以外那些 Unit 的占位路由（只写进 isolation 清单）。</summary>
    public IReadOnlyList<IasUnitRoute> CleanupRoutes { get; init; } = [];

    /// <summary>本次转换选定的目标护甲；为空表示使用手填的 Archive / equipment / native。</summary>
    public GmodArmorTarget? Target { get; init; }
}

/// <summary>报告里的单部位统计。</summary>
public sealed record GmodPartReport(
    string Part,
    string Kind,
    string Domain,
    string Archive,
    int MeshCount,
    int VertexCount,
    int TriangleCount,
    int SwitchCount,
    int OptionCount,
    int RigProfileCount,
    int PhysicsChainCount,
    int PhysicsProfileCount,
    int UnitCount = 0,
    int MaterialResourceCount = 0,
    int TextureResourceCount = 0,
    long PackageBytes = 0,
    string TargetArmor = "",
    int CleanupUnitCount = 0);

public sealed record GmodConversionReport(
    string ProjectId,
    string ModelPath,
    int BoneCount,
    int MeshCount,
    int VertexCount,
    int TriangleCount,
    int MaterialCount,
    int TextureCount,
    int DecodedTextureCount,
    int SwitchCount,
    int OptionCount,
    int PhysicsChainCount,
    int PhysicsJointCount,
    int PhysicsProfileCount,
    int RigProfileCount,
    int RawSourceFileCount,
    IReadOnlyList<GmodConversionIssue> Issues,
    IReadOnlyList<GmodPartReport> Parts)
{
    /// <summary>配套的独立骨架运行时包文件名（<c>*.hd2irpack.zip</c>）。</summary>
    public string RuntimePack { get; init; } = "";

    public long RuntimePackBytes { get; init; }

    public string RuntimePackGroupId { get; init; } = "";
}

public sealed record GmodConversionResult(
    IasFreePartsDocument Differences,
    IasRigDocument Rig,
    GmodGeometryBundle Geometry,
    IReadOnlyList<GmodMaterial> Materials,
    IReadOnlyList<GmodTexture> Textures,
    IReadOnlyList<GmodSwitch> Switches,
    IReadOnlyList<GmodPhysicsProfile> Physics,
    IReadOnlyList<GmodOutputFile> Files,
    GmodConversionReport Report,
    IReadOnlyList<GmodConversionPart> Parts);
