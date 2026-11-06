using System.Numerics;

namespace HD2Mimnt.ModelConversion.Source.Mdl;

[Flags]
public enum MdlBoneFlags
{
    None = 0,
    PhysicallySimulated = 0x1,
    PhysicsProcedural = 0x2,
    AlwaysProcedural = 0x4,
    ScreenAlignSphere = 0x8,
    ScreenAlignCylinder = 0x10,
    UsedByHitbox = 0x100,
    UsedByAttachment = 0x200,
    UsedByVertexLod0 = 0x400,
    UsedByVertexLod1 = 0x800,
    UsedByVertexLod2 = 0x1000,
    UsedByVertexLod3 = 0x2000,
    UsedByVertexLod4 = 0x4000,
    UsedByVertexLod5 = 0x8000,
    UsedByVertexLod6 = 0x10000,
    UsedByVertexLod7 = 0x20000,
    UsedByBoneMerge = 0x40000,
    FixedAlignment = 0x100000,
    HasSaveFrameProcedural = 0x200000,
    HasSaveFrameRotation = 0x400000,
}

public enum MdlProceduralBoneType
{
    None = 0,
    AxisInterp = 1,
    QuatInterp = 2,
    AimAtBone = 3,
    AimAtAttach = 4,
    Jiggle = 5,
}

[Flags]
public enum MdlJiggleFlags
{
    None = 0,
    IsFlexible = 0x01,
    IsRigid = 0x02,
    HasYawConstraint = 0x04,
    HasPitchConstraint = 0x08,
    HasAngleConstraint = 0x10,
    HasLengthConstraint = 0x20,
    HasBaseSpring = 0x40,
    IsBoing = 0x80,
}

/// <summary>Verbatim <c>$jigglebone</c> parameters compiled into <c>mstudiojigglebone_t</c>.</summary>
public sealed record MdlJiggleBone(
    MdlJiggleFlags Flags,
    float Length,
    float TipMass,
    float YawStiffness,
    float YawDamping,
    float PitchStiffness,
    float PitchDamping,
    float AlongStiffness,
    float AlongDamping,
    float AngleLimit,
    float MinYaw,
    float MaxYaw,
    float YawFriction,
    float YawBounce,
    float MinPitch,
    float MaxPitch,
    float PitchFriction,
    float PitchBounce,
    float BaseMass,
    float BaseStiffness,
    float BaseDamping,
    float BaseMinLeft,
    float BaseMaxLeft,
    float BaseLeftFriction,
    float BaseMinUp,
    float BaseMaxUp,
    float BaseUpFriction,
    float BaseMinForward,
    float BaseMaxForward,
    float BaseForwardFriction,
    MdlJiggleBoing Boing);

public sealed record MdlJiggleBoing(
    float ImpactSpeed,
    float ImpactAngle,
    float DampingRate,
    float Frequency,
    float Amplitude);

/// <summary>
/// One <c>mstudiobone_t</c>. <see cref="PoseToBone"/> is the row-major 3x4 inverse bind matrix exactly as
/// stored; <see cref="Position"/> / <see cref="Rotation"/> are the parent-relative bind transform.
/// </summary>
public sealed record MdlBone(
    int Index,
    string Name,
    int ParentIndex,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 EulerRotation,
    Vector3 PositionScale,
    Vector3 RotationScale,
    float[] PoseToBone,
    Quaternion Alignment,
    MdlBoneFlags Flags,
    MdlProceduralBoneType ProceduralType,
    int PhysicsBone,
    string SurfaceProp,
    int Contents,
    MdlJiggleBone? Jiggle);

public sealed record MdlMesh(int Index, int MaterialIndex, int VertexCount, int VertexOffset, int MeshId, Vector3 Center, int[] LodVertexCounts);

/// <summary>One selectable alternative inside a body group (<c>mstudiomodel_t</c>).</summary>
public sealed record MdlModel(int Index, string Name, int VertexCount, int VertexByteOffset, IReadOnlyList<MdlMesh> Meshes)
{
    public bool IsBlank => Meshes.Count == 0 || VertexCount == 0;
}

/// <summary>A <c>$bodygroup</c>: a named switch whose <see cref="Models"/> are mutually exclusive.</summary>
public sealed record MdlBodyPart(int Index, string Name, int Base, IReadOnlyList<MdlModel> Models);

public sealed record MdlTexture(int Index, string Name, int Flags);

public sealed record MdlAttachment(int Index, string Name, int Flags, int LocalBone, float[] LocalMatrix);

public sealed record MdlHeader(
    int Version,
    uint Checksum,
    string Name,
    int Length,
    Vector3 EyePosition,
    Vector3 IllumPosition,
    Vector3 HullMin,
    Vector3 HullMax,
    Vector3 ViewBoundsMin,
    Vector3 ViewBoundsMax,
    int Flags,
    float Mass,
    string SurfaceProp,
    string KeyValues);
