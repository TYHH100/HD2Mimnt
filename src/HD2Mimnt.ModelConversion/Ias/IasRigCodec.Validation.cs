using System.Numerics;
using System.Text;

namespace HD2Mimnt.ModelConversion.Ias;

public static partial class IasRigCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static (int Size, uint Extensions, int Grips) Validate(
        IasRigDocument document, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document is null || document.SourceBones is null || document.Profiles is null)
            throw new IasFormatException("Rig document, sources and profiles must not be null.");
        RequireCount(document.SourceBones.Count, 256, "source bones");
        RequireCount(document.Profiles.Count, 512, "profiles");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var sourceGlobals = new double[document.SourceBones.Count][];
        for (var i = 0; i < document.SourceBones.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bone = document.SourceBones[i];
            if (bone is null)
                throw new IasFormatException($"Source bone {i} is null.");
            ValidateName(bone.Name, names);
            ValidateParent(bone.Parent, i);
            Require(((uint)bone.Flags & ~1u) == 0, "Unknown source flags.");
            ValidateMatrix(bone.RestLocal);
            sourceGlobals[i] = Global(bone.RestLocal, bone.Parent < 0 ? null : sourceGlobals[bone.Parent]);
        }

        var units = new HashSet<ulong>();
        uint extensions = 0;
        var gripCount = 0;
        long size = HeaderSize + document.SourceBones.Count * SourceSize;
        foreach (var profile in document.Profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (profile is null || profile.TargetBones is null || profile.Needle is null || profile.HandGrips is null)
                throw new IasFormatException("Profile and its collections must not be null.");
            Require(profile.UnitId != 0 && units.Add(profile.UnitId), "Unit ID must be nonzero and unique.");
            RequireCount(profile.TargetBones.Count, 256, "target bones");
            Require(profile.PaletteSlots is >= 1 and <= 4096, "Palette slots must be 1..4096.");
            Require(profile.Needle.Length is >= 16 and <= MaxNeedleSize, "Needle length must be 16 bytes..1 MiB.");
            var flags = (uint)profile.Flags;
            Require((flags & ~15u) == 0 && (flags & 12u) != 12u, "Unknown or conflicting profile flags.");
            if ((flags & 12u) != 0)
                extensions |= 2;

            names.Clear();
            var slots = new HashSet<uint>();
            var targetGlobals = new double[profile.TargetBones.Count][];
            for (var i = 0; i < profile.TargetBones.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bone = profile.TargetBones[i];
                if (bone is null)
                    throw new IasFormatException($"Target bone {i} is null.");
                ValidateName(bone.Name, names);
                ValidateParent(bone.Parent, i);
                Require(bone.SourceIndex >= -1 && bone.SourceIndex < document.SourceBones.Count,
                    "Target source index is out of range.");
                Require(bone.PaletteSlot < profile.PaletteSlots && slots.Add(bone.PaletteSlot),
                    "Target palette slots must be in range and unique.");
                Require(float.IsFinite(bone.TranslationScale) && bone.TranslationScale is >= 0 and <= 100,
                    "Translation scale must be finite and in 0..100.");
                Require(((uint)bone.Flags & ~2047u) == 0, "Unknown target flags.");
                var scene = Has(bone.Flags, IasRigTargetFlags.SceneGraphIndex);
                var inherit = Has(bone.Flags, IasRigTargetFlags.InheritParentPose);
                var global = Has(bone.Flags, IasRigTargetFlags.SourceGlobalPose);
                var parentSpace = Has(bone.Flags, IasRigTargetFlags.SourceParentSpace);
                Require(!scene || !Has(bone.Flags, IasRigTargetFlags.AnchorOnly),
                    "Scene graph index conflicts with anchor only.");
                Require(!Has(bone.Flags, IasRigTargetFlags.DiagnosticMotion) || scene,
                    "Diagnostic motion requires scene graph index.");
                Require(!inherit || bone.Parent >= 0, "Inherit parent pose requires a parent.");
                Require(!parentSpace || (bone.SourceIndex >= 0 && scene && !inherit && !global &&
                    (bone.Parent < 0 || profile.TargetBones[bone.Parent].SourceIndex >= 0)),
                    "Source parent space requires source/scene and a mapped parent, without inherit/global.");
                Require(!global || (scene && !inherit && bone.SourceIndex >= 0),
                    "Source global pose requires source/scene without inherit.");
                ValidateMatrix(bone.RestLocal);
                var globalRest = Global(bone.RestLocal, bone.Parent < 0 ? null : targetGlobals[bone.Parent]);
                targetGlobals[i] = globalRest;
                if (global)
                {
                    for (var j = 0; j < globalRest.Length; j++)
                        Require(Math.Abs(globalRest[j] - sourceGlobals[bone.SourceIndex][j]) <= 2e-4,
                            "Source global pose rest transform does not match the source.");
                }
            }

            Require(profile.HandGrips.Count <= 2, "Each profile permits at most two hand grips.");
            var sides = new HashSet<uint>();
            foreach (var grip in profile.HandGrips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (grip is null)
                    throw new IasFormatException("Hand grip must not be null.");
                Require(grip.Hand < (uint)profile.TargetBones.Count && grip.Side <= 1 && sides.Add(grip.Side),
                    "Hand grip index/side is invalid or duplicated.");
                var hand = profile.TargetBones[(int)grip.Hand];
                var prefix = grip.Side == 0 ? "r_" : "l_";
                Require(hand.SourceIndex >= 0 && document.SourceBones[hand.SourceIndex].Name == prefix + "hand" &&
                    Has(hand.Flags, IasRigTargetFlags.SourceParentSpace),
                    "Hand grip must map to the correct source hand in source parent space.");
                var chain = new HashSet<string>(StringComparer.Ordinal);
                for (var cursor = (int)grip.Hand; cursor >= 0; cursor = profile.TargetBones[cursor].Parent)
                {
                    var bone = profile.TargetBones[cursor];
                    if (bone.SourceIndex >= 0 && Has(bone.Flags, IasRigTargetFlags.SourceParentSpace))
                        chain.Add(document.SourceBones[bone.SourceIndex].Name);
                }
                Require(chain.Contains(prefix + "shoulder") && chain.Contains(prefix + "elbow") &&
                    chain.Contains(prefix + "hand"), "Hand grip requires a mapped source-parent-space shoulder/elbow/hand chain.");
                ValidateMatrix(grip.HandToGrip);
            }

            gripCount += profile.HandGrips.Count;
            size += ProfileSize + (long)profile.TargetBones.Count * TargetSize + profile.Needle.Length;
        }

        if (gripCount > 0)
        {
            extensions |= 1;
            size += GripHeaderSize + (long)gripCount * GripSize;
        }
        Require(size <= int.MaxValue, "Rig binary is too large.");
        cancellationToken.ThrowIfCancellationRequested();
        return ((int)size, extensions, gripCount);
    }

    private static bool Has(IasRigTargetFlags flags, IasRigTargetFlags bit) => (flags & bit) != 0;

    private static void ValidateName(string name, HashSet<string> names)
    {
        Require(!string.IsNullOrEmpty(name) && !name.Contains('\0'), "Bone name must be nonempty and contain no NUL.");
        // A UTF-16 code unit cannot encode to fewer than one UTF-8 byte. Bound work before encoding.
        Require(name.Length <= 63, "Bone name exceeds 63 UTF-8 bytes.");
        try
        {
            Require(StrictUtf8.GetByteCount(name) <= 63, "Bone name exceeds 63 UTF-8 bytes.");
        }
        catch (EncoderFallbackException)
        {
            throw new IasFormatException("Bone name contains invalid UTF-16.");
        }
        Require(names.Add(name), "Bone names must be unique within their table.");
    }

    private static void ValidateParent(int parent, int index) =>
        Require(parent >= -1 && parent < index, "Parent must be -1 or an earlier bone.");

    private static void ValidateMatrix(Matrix4x4 matrix)
    {
        ReadOnlySpan<float> values =
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44
        ];
        foreach (var value in values)
            Require(float.IsFinite(value), "Transform contains a nonfinite value.");
        Require(matrix.M14 == 0 && matrix.M24 == 0 && matrix.M34 == 0 && matrix.M44 == 1,
            "Transform must be affine in row-vector convention.");
        for (var row = 0; row < 3; row++)
        {
            double lengthSquared = 0;
            for (var k = 0; k < 3; k++)
                lengthSquared += (double)values[row * 4 + k] * values[row * 4 + k];
            Require(Math.Abs(Math.Sqrt(lengthSquared) - 1) <= 0.002, "Rotation rows must have unit length.");
            for (var other = row + 1; other < 3; other++)
            {
                double dot = 0;
                for (var k = 0; k < 3; k++)
                    dot += (double)values[row * 4 + k] * values[other * 4 + k];
                Require(Math.Abs(dot) <= 0.002, "Rotation rows must be orthogonal.");
            }
        }
        var determinant = (double)matrix.M11 * ((double)matrix.M22 * matrix.M33 - (double)matrix.M23 * matrix.M32)
            - (double)matrix.M12 * ((double)matrix.M21 * matrix.M33 - (double)matrix.M23 * matrix.M31)
            + (double)matrix.M13 * ((double)matrix.M21 * matrix.M32 - (double)matrix.M22 * matrix.M31);
        Require(Math.Abs(determinant - 1) <= 0.005, "Rotation determinant must be +1.");
    }

    // Accumulate row-vector 4x3 rest poses in double precision to avoid rounding at every ancestor.
    private static double[] Global(Matrix4x4 local, double[]? parent)
    {
        double[] rows =
        [
            local.M11, local.M12, local.M13,
            local.M21, local.M22, local.M23,
            local.M31, local.M32, local.M33,
            local.M41, local.M42, local.M43
        ];
        if (parent is null)
            return rows;
        var result = new double[12];
        for (var row = 0; row < 4; row++)
            for (var column = 0; column < 3; column++)
            {
                var sum = row == 3 ? parent[9 + column] : 0;
                for (var k = 0; k < 3; k++)
                    sum += rows[row * 3 + k] * parent[k * 3 + column];
                result[row * 3 + column] = sum;
            }
        return result;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new IasFormatException(message);
    }

    private static void RequireCount(int value, int maximum, string field) =>
        Require(value >= 1 && value <= maximum, $"{field} count must be 1..{maximum}.");
}
