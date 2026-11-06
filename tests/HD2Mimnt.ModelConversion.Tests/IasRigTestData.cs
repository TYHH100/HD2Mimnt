using System.Numerics;
using System.Text.Json;
using HD2Mimnt.ModelConversion.Ias;

namespace HD2Mimnt.ModelConversion.Tests;

internal static class IasRigTestData
{
    internal static IasRigDocument Basic() => new(
        [new IasRigSourceBone("root", -1, IasRigSourceFlags.Root, Matrix4x4.Identity)],
        [new IasRigProfile(1,
            [new IasRigTargetBone("bone", -1, -1, 0, IasRigTargetFlags.None, 0, Matrix4x4.Identity)],
            Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), int.MaxValue, 1)]);

    internal static string FixturePath(string name, string extension) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name + extension);

    internal static byte[] Golden(string name) => File.ReadAllBytes(FixturePath(name, ".bin"));

    // The JSON is input to the independent Python producer, NOT output from the C# codec.
    // Its metadata records the producer hash and the resulting binary hash. No SDK code is bundled.
    internal static IasRigDocument FromJson(string name)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(FixturePath(name, ".json")));
        var root = json.RootElement;
        return new IasRigDocument(
            root.GetProperty("source_bones").EnumerateArray().Select(bone => new IasRigSourceBone(
                bone.GetProperty("name").GetString()!, bone.GetProperty("parent").GetInt32(),
                Flags<IasRigSourceFlags>(bone), Matrix(bone.GetProperty("rest_local")))).ToArray(),
            root.GetProperty("profiles").EnumerateArray().Select(profile => new IasRigProfile(
                profile.GetProperty("unit_id").GetUInt64(),
                profile.GetProperty("target_bones").EnumerateArray().Select(bone => new IasRigTargetBone(
                    bone.GetProperty("name").GetString()!, bone.GetProperty("parent").GetInt32(),
                    bone.GetProperty("source_index").GetInt32(), bone.GetProperty("palette_slot").GetUInt32(),
                    Flags<IasRigTargetFlags>(bone), bone.GetProperty("translation_scale").GetSingle(),
                    Matrix(bone.GetProperty("rest_local")))).ToArray(),
                Convert.FromHexString(profile.GetProperty("needle_hex").GetString()!),
                profile.GetProperty("needle_to_palette").GetInt32(), profile.GetProperty("palette_slots").GetUInt32(),
                Flags<IasRigProfileFlags>(profile))
            {
                HandGrips = profile.GetProperty("hand_grips").EnumerateArray().Select(grip => new IasRigHandGrip(
                    grip.GetProperty("hand").GetUInt32(), grip.GetProperty("side").GetUInt32(),
                    Matrix(grip.GetProperty("hand_to_grip")))).ToArray()
            }).ToArray());
    }

    private static T Flags<T>(JsonElement value) where T : struct, Enum
    {
        uint flags = 0;
        foreach (var name in value.GetProperty("flags").EnumerateArray())
            flags |= Convert.ToUInt32(Enum.Parse<T>(name.GetString()!.Replace("_", ""), true));
        return (T)Enum.ToObject(typeof(T), flags);
    }

    private static Matrix4x4 Matrix(JsonElement value)
    {
        var rows = value.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        Assert.Equal(12, rows.Length);
        return new Matrix4x4(
            rows[0], rows[4], rows[8], 0,
            rows[1], rows[5], rows[9], 0,
            rows[2], rows[6], rows[10], 0,
            rows[3], rows[7], rows[11], 1);
    }

    internal static IasRigDocument WithSource(IasRigDocument document, int index, Func<IasRigSourceBone, IasRigSourceBone> update)
    {
        var sources = document.SourceBones.ToArray();
        sources[index] = update(sources[index]);
        return document with { SourceBones = sources };
    }

    internal static IasRigDocument WithProfile(IasRigDocument document, int index, Func<IasRigProfile, IasRigProfile> update)
    {
        var profiles = document.Profiles.ToArray();
        profiles[index] = update(profiles[index]);
        return document with { Profiles = profiles };
    }

    internal static IasRigDocument WithTarget(IasRigDocument document, int index, Func<IasRigTargetBone, IasRigTargetBone> update,
        int profileIndex = 0) => WithProfile(document, profileIndex, profile =>
        {
            var targets = profile.TargetBones.ToArray();
            targets[index] = update(targets[index]);
            return profile with { TargetBones = targets };
        });

    internal static void AssertDocument(IasRigDocument expected, IasRigDocument actual)
    {
        Assert.Equal(expected.SourceBones.ToArray(), actual.SourceBones.ToArray());
        Assert.Equal(expected.Profiles.Count, actual.Profiles.Count);
        for (var i = 0; i < expected.Profiles.Count; i++)
        {
            var left = expected.Profiles[i];
            var right = actual.Profiles[i];
            Assert.Equal(left.UnitId, right.UnitId);
            Assert.Equal(left.Flags, right.Flags);
            Assert.Equal(left.PaletteSlots, right.PaletteSlots);
            Assert.Equal(left.NeedleToPalette, right.NeedleToPalette);
            Assert.Equal(left.Needle, right.Needle);
            Assert.Equal(left.TargetBones.ToArray(), right.TargetBones.ToArray());
            Assert.Equal(left.HandGrips.ToArray(), right.HandGrips.ToArray());
        }
    }
}
