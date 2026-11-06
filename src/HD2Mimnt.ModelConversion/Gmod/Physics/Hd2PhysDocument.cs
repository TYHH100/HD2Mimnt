using System.Numerics;

namespace HD2Mimnt.ModelConversion.Gmod.Physics;

/// <summary>角限类型，与 HD2PHY1 的 LIMIT_KINDS 一致。</summary>
public enum Hd2PhysLimitKind
{
    None = 0,
    Cone = 1,
    Hinge = 2,
    Spherical = 3
}

/// <summary>角限参考方向来源，与 HD2PHY1 的 ReferenceMode 一致。</summary>
public enum Hd2PhysReferenceMode
{
    AnimatedSegment = 0,
    ParentRest = 1,
    ExplicitLocal = 2,
    ExplicitFrame = 3
}

public enum Hd2PhysColliderKind
{
    Sphere = 0,
    Capsule = 1,
    Plane = 2
}

/// <summary>导出到物理调色板的骨骼。RestHead 为模型空间绝对坐标。</summary>
public sealed record Hd2PhysBone(
    string Name,
    int PaletteSlot,
    int ParentSlot,
    Vector3 RestHead,
    float[]? RestBasis = null);

public sealed record Hd2PhysJoint(
    string Bone,
    float Radius = 0.02f,
    Hd2PhysLimitKind LimitKind = Hd2PhysLimitKind.None,
    float LimitAngle = 0.5235988f,
    float? LimitAngle2 = null,
    Vector3? LimitAxis = null,
    float StiffnessScale = 1f,
    float DampingScale = 1f,
    Hd2PhysReferenceMode ReferenceMode = Hd2PhysReferenceMode.AnimatedSegment,
    Vector3? ReferenceDirection = null,
    Vector4? ReferenceFrame = null,
    bool OverrideCollisionFilter = false,
    uint GroupMask = 1,
    uint CollisionMask = uint.MaxValue);

public sealed record Hd2PhysLinkCollision(
    int LocalJointIndex,
    float Radius = 0.01f,
    uint GroupMask = 1,
    uint CollisionMask = uint.MaxValue);

public sealed record Hd2PhysChain(
    string Name,
    IReadOnlyList<Hd2PhysJoint> Joints,
    float Stiffness = 8f,
    float Damping = 0.25f,
    Vector3? Gravity = null,
    Vector3? Wind = null,
    uint GroupMask = 1,
    uint CollisionMask = uint.MaxValue,
    bool RootSwing = false,
    bool LocalBend = false);

public sealed record Hd2PhysChainLink(
    string Name,
    string ChainA,
    string ChainB,
    float Coupling = 1f,
    float MaxCorrection = 0.25f,
    bool Strip = false,
    IReadOnlyList<Hd2PhysLinkCollision>? Collisions = null);

public sealed record Hd2PhysCollider(
    string Name,
    string Bone = "",
    Hd2PhysColliderKind Kind = Hd2PhysColliderKind.Sphere,
    string BoneB = "",
    uint GroupMask = 1,
    uint CollisionMask = uint.MaxValue,
    bool Inside = false,
    bool Dynamic = false,
    Vector3? A = null,
    Vector3? B = null,
    float Radius = 0.1f);

/// <summary>单个已发布 Unit 的 HD2PHY1 物理项目。</summary>
public sealed record Hd2PhysDocument(
    ulong UnitId,
    uint PaletteSlots,
    IReadOnlyList<Hd2PhysBone> Bones,
    IReadOnlyList<Hd2PhysChain> Chains,
    float FixedDt = 1f / 60f,
    IReadOnlyList<Hd2PhysCollider>? Colliders = null,
    IReadOnlyList<Hd2PhysChainLink>? ChainLinks = null);

public sealed record Hd2PhysSummary(
    ulong UnitId,
    ulong SkeletonFingerprint,
    uint PaletteSlots,
    int TargetBones,
    int Chains,
    int Joints,
    int Colliders,
    int ChainLinks,
    int LinkCollisions,
    uint Capabilities,
    uint PayloadCrc32,
    int Bytes);

/// <summary>HD2PHY1 文档或二进制违反格式合同。</summary>
public sealed class Hd2PhysFormatException(string message) : Exception(message);
