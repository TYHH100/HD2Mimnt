using System.Numerics;

namespace HD2Mimnt.ModelConversion.Gmod;

/// <summary>Source 骨骼的 rest 局部变换。Unit 的 TransformInfo 与 HD2IRG1 的 rest_local 必须用同一套算法。</summary>
public static class GmodBonePose
{
    public static Matrix4x4 Local(GmodBone bone)
    {
        var rotation = bone.Rotation.Length() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize(bone.Rotation);
        var matrix = Matrix4x4.CreateFromQuaternion(rotation);
        matrix.M41 = bone.Position.X;
        matrix.M42 = bone.Position.Y;
        matrix.M43 = bone.Position.Z;
        return matrix;
    }
}
