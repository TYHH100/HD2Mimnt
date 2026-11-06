using System.Numerics;

namespace HD2Mimnt.ModelConversion.Ias;

[Flags]
public enum IasRigSourceFlags : uint
{
    None = 0,
    Root = 1
}

[Flags]
public enum IasRigProfileFlags : uint
{
    None = 0,
    DynamicPoseMatch = 1,
    ObserveOnly = 2,
    GenderMale = 4,
    GenderFemale = 8
}

[Flags]
public enum IasRigTargetFlags : uint
{
    None = 0,
    TransferTranslation = 1,
    Attachment = 2,
    LeftHand = 4,
    RightHand = 8,
    Backpack = 16,
    SceneGraphIndex = 32,
    AnchorOnly = 64,
    DiagnosticMotion = 128,
    InheritParentPose = 256,
    SourceGlobalPose = 512,
    SourceParentSpace = 1024
}

// Matrices use row vectors; collections must remain unchanged during codec calls.
public sealed record IasRigDocument(
    IReadOnlyList<IasRigSourceBone> SourceBones,
    IReadOnlyList<IasRigProfile> Profiles);

public sealed record IasRigSourceBone(
    string Name,
    int Parent,
    IasRigSourceFlags Flags,
    Matrix4x4 RestLocal);

public sealed record IasRigProfile(
    ulong UnitId,
    IReadOnlyList<IasRigTargetBone> TargetBones,
    byte[] Needle,
    int NeedleToPalette,
    uint PaletteSlots,
    IasRigProfileFlags Flags = IasRigProfileFlags.None)
{
    public IReadOnlyList<IasRigHandGrip> HandGrips { get; init; } = Array.Empty<IasRigHandGrip>();
}

public sealed record IasRigTargetBone(
    string Name,
    int Parent,
    int SourceIndex,
    uint PaletteSlot,
    IasRigTargetFlags Flags,
    float TranslationScale,
    Matrix4x4 RestLocal);

/// <param name="Hand">Target bone index.</param>
/// <param name="Side">0 = right, 1 = left.</param>
/// <param name="HandToGrip">Row-vector rigid affine transform.</param>
public sealed record IasRigHandGrip(uint Hand, uint Side, Matrix4x4 HandToGrip);
