using System.Numerics;

namespace HD2Mimnt.ModelConversion.Source.Mdl;

public sealed class MdlFile
{
    public const int BoneSize = 216;
    public const int BodyPartSize = 16;
    public const int ModelSize = 148;
    public const int MeshSize = 116;
    public const int TextureSize = 64;
    public const int AttachmentSize = 92;
    private const int SupportedVersion = 48;

    private MdlFile(BinaryBlock block)
    {
        Block = block;
    }

    public BinaryBlock Block { get; }
    public MdlHeader Header { get; private set; } = null!;
    public IReadOnlyList<MdlBone> Bones { get; private set; } = Array.Empty<MdlBone>();
    public IReadOnlyList<MdlBodyPart> BodyParts { get; private set; } = Array.Empty<MdlBodyPart>();
    public IReadOnlyList<MdlTexture> Textures { get; private set; } = Array.Empty<MdlTexture>();
    public IReadOnlyList<string> TextureDirectories { get; private set; } = Array.Empty<string>();
    public IReadOnlyList<short[]> SkinFamilies { get; private set; } = Array.Empty<short[]>();
    public IReadOnlyList<MdlAttachment> Attachments { get; private set; } = Array.Empty<MdlAttachment>();
    public IReadOnlyList<string> Warnings => _warnings;

    private readonly List<string> _warnings = new();

    public static MdlFile Parse(byte[] data, string label = "MDL")
    {
        var file = new MdlFile(new BinaryBlock(data, label));
        file.ParseHeader();
        file.Bones = file.ParseBones();
        file.ParseMaterials();
        file.BodyParts = file.ParseBodyParts();
        file.Attachments = file.ParseAttachments();
        return file;
    }

    /// <summary>Resolves a mesh material slot through a skin family to the material's addon-relative path (without extension).</summary>
    public string ResolveMaterialPath(int materialSlot, int skin = 0)
    {
        var textureIndex = materialSlot;
        if (SkinFamilies.Count > 0)
        {
            if (skin < 0 || skin >= SkinFamilies.Count)
            {
                throw new SourceFormatException($"Skin family {skin} is out of range 0..{SkinFamilies.Count - 1}");
            }

            var row = SkinFamilies[skin];
            if (materialSlot < 0 || materialSlot >= row.Length)
                throw new SourceFormatException($"Material slot {materialSlot} is outside the skin table");
            textureIndex = row[materialSlot];
        }

        if (textureIndex < 0 || textureIndex >= Textures.Count)
            throw new SourceFormatException($"Material slot {materialSlot} references invalid texture {textureIndex}");
        var name = Textures[textureIndex].Name;
        var directory = TextureDirectories.FirstOrDefault(value => value.Length > 0);
        if (directory is null)
        {
            return name.Replace('\\', '/');
        }

        var prefix = directory.Replace('\\', '/').Trim('/');
        return $"{prefix}/{name.Replace('\\', '/').TrimStart('/')}";
    }

    private void ParseHeader()
    {
        var block = Block;
        if (block.Length < 408 || block.Slice(0, 4, "signature") is not [0x49, 0x44, 0x53, 0x54])
        {
            throw new SourceFormatException("MDL signature is not IDST");
        }

        var version = block.I32(4);
        if (version != SupportedVersion)
        {
            throw new SourceFormatException($"Unsupported MDL version {version}; only the verified v{SupportedVersion} layout is supported");
        }

        var length = block.I32(76);
        if (length != block.Length)
        {
            _warnings.Add($"MDL header length {length} differs from file size {block.Length}");
        }

        var surfacePropIndex = block.I32(308);
        var keyValueIndex = block.I32(312);
        var keyValueSize = block.I32(316);
        var keyValues = keyValueIndex > 0 && keyValueSize > 0 && keyValueIndex + keyValueSize <= block.Length
            ? block.CString(keyValueIndex, keyValueSize)
            : string.Empty;

        Header = new MdlHeader(
            version,
            block.U32(8),
            block.FixedString(12, 64),
            length,
            block.Vec3(80),
            block.Vec3(92),
            block.Vec3(104),
            block.Vec3(116),
            block.Vec3(128),
            block.Vec3(140),
            block.I32(152),
            block.F32(328),
            surfacePropIndex > 0 && surfacePropIndex < block.Length ? block.CString(surfacePropIndex) : string.Empty,
            keyValues);
    }

    private List<MdlBone> ParseBones()
    {
        var count = Block.I32(156);
        var start = Block.I32(160);
        if (count < 0 || count > 4096)
        {
            throw new SourceFormatException($"Implausible MDL bone count {count}");
        }

        Block.Check(start, (long)count * BoneSize, "bone table");
        var bones = new List<MdlBone>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = start + i * BoneSize;
            var name = Block.RelativeCString(offset, Block.I32(offset), $"bone_{i}");
            var parent = Block.I32(offset + 4);
            if (parent < -1 || parent >= count || parent == i)
            {
                throw new SourceFormatException($"Bone {i} ({name}) has invalid parent {parent}");
            }

            var proceduralType = (MdlProceduralBoneType)Block.I32(offset + 164);
            var proceduralOffset = Block.I32(offset + 168);
            MdlJiggleBone? jiggle = null;
            if (proceduralType == MdlProceduralBoneType.Jiggle && proceduralOffset != 0)
            {
                jiggle = ParseJiggle(offset + proceduralOffset, i, name);
            }

            var surfacePropOffset = Block.I32(offset + 176);
            bones.Add(new MdlBone(
                i,
                name,
                parent,
                Block.Vec3(offset + 32),
                Block.Quat(offset + 44),
                Block.Vec3(offset + 60),
                Block.Vec3(offset + 72),
                Block.Vec3(offset + 84),
                Block.Floats(offset + 96, 12, $"bone[{i}].poseToBone"),
                Block.Quat(offset + 144),
                (MdlBoneFlags)Block.I32(offset + 160),
                proceduralType,
                Block.I32(offset + 172),
                surfacePropOffset != 0 ? Block.RelativeCString(offset, surfacePropOffset, string.Empty) : string.Empty,
                Block.I32(offset + 180),
                jiggle));
        }

        foreach (var bone in bones)
        {
            var seen = new HashSet<int>();
            for (var index = bone.Index; index != -1; index = bones[index].ParentIndex)
            {
                if (!seen.Add(index)) throw new SourceFormatException($"Bone hierarchy cycle at {bone.Name}");
            }
        }
        return bones;
    }

    private MdlJiggleBone ParseJiggle(int offset, int boneIndex, string boneName)
    {
        const int coreSize = 120;
        const int boingSize = 20;
        Block.Check(offset, coreSize, $"jiggle bone data for bone {boneIndex} ({boneName})");
        var values = Block.Floats(offset + 4, (coreSize - 4) / 4, "jiggle floats");
        var flags = (MdlJiggleFlags)Block.I32(offset);
        Block.Check(offset + coreSize, boingSize, $"jiggle boing data for bone {boneIndex} ({boneName})");
        var boingValues = Block.Floats(offset + coreSize, boingSize / 4, "jiggle boing floats");
        if (values.Concat(boingValues).Any(value => !float.IsFinite(value)))
            throw new SourceFormatException($"Non-finite jiggle parameters for bone {boneName}");
        var boing = new MdlJiggleBoing(boingValues[0], boingValues[1], boingValues[2], boingValues[3], boingValues[4]);

        return new MdlJiggleBone(
            flags,
            values[0], values[1],
            values[2], values[3], values[4], values[5], values[6], values[7],
            values[8],
            values[9], values[10], values[11], values[12],
            values[13], values[14], values[15], values[16],
            values[17], values[18], values[19],
            values[20], values[21], values[22],
            values[23], values[24], values[25],
            values[26], values[27], values[28],
            boing);
    }

    private void ParseMaterials()
    {
        var textureCount = Block.I32(204);
        var textureStart = Block.I32(208);
        var directoryCount = Block.I32(212);
        var directoryStart = Block.I32(216);
        var skinRefCount = Block.I32(220);
        var skinFamilyCount = Block.I32(224);
        var skinStart = Block.I32(228);
        if (textureCount < 0 || textureCount > 4096 || directoryCount < 0 || directoryCount > 1024)
        {
            throw new SourceFormatException("Implausible MDL texture table counts");
        }

        var textures = new List<MdlTexture>(textureCount);
        Block.Check(textureStart, (long)textureCount * TextureSize, "texture table");
        for (var i = 0; i < textureCount; i++)
        {
            var offset = textureStart + i * TextureSize;
            textures.Add(new MdlTexture(i, Block.RelativeCString(offset, Block.I32(offset), $"texture_{i}"), Block.I32(offset + 4)));
        }

        var directories = new List<string>(directoryCount);
        Block.Check(directoryStart, directoryCount * 4L, "cdtexture table");
        for (var i = 0; i < directoryCount; i++)
        {
            var absolute = Block.I32(directoryStart + i * 4);
            directories.Add(absolute > 0 && absolute < Block.Length ? Block.CString(absolute) : string.Empty);
        }

        var families = new List<short[]>();
        if (skinRefCount > 0 && skinFamilyCount > 0)
        {
            if (skinRefCount > 4096 || skinFamilyCount > 4096)
            {
                throw new SourceFormatException("Implausible MDL skin table counts");
            }

            Block.Check(skinStart, (long)skinRefCount * skinFamilyCount * 2, "skin table");
            for (var family = 0; family < skinFamilyCount; family++)
            {
                var row = new short[skinRefCount];
                for (var slot = 0; slot < skinRefCount; slot++)
                {
                    row[slot] = Block.I16(skinStart + (family * skinRefCount + slot) * 2);
                }

                families.Add(row);
            }
        }

        Textures = textures;
        TextureDirectories = directories;
        SkinFamilies = families;
    }

    private List<MdlBodyPart> ParseBodyParts()
    {
        var count = Block.I32(232);
        var start = Block.I32(236);
        if (count < 0 || count > 1024)
        {
            throw new SourceFormatException($"Implausible MDL body part count {count}");
        }

        Block.Check(start, (long)count * BodyPartSize, "body part table");
        var parts = new List<MdlBodyPart>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = start + i * BodyPartSize;
            var name = Block.RelativeCString(offset, Block.I32(offset), $"bodypart_{i}");
            var modelCount = Block.I32(offset + 4);
            var baseValue = Block.I32(offset + 8);
            var modelStart = offset + Block.I32(offset + 12);
            if (modelCount < 0 || modelCount > 4096)
            {
                throw new SourceFormatException($"Body part {i} ({name}) has implausible model count {modelCount}");
            }

            Block.Check(modelStart, (long)modelCount * ModelSize, $"models of body part {name}");
            var models = new List<MdlModel>(modelCount);
            for (var m = 0; m < modelCount; m++)
            {
                var modelOffset = modelStart + m * ModelSize;
                var modelName = Block.FixedString(modelOffset, 64);
                var meshCount = Block.I32(modelOffset + 72);
                var meshStart = modelOffset + Block.I32(modelOffset + 76);
                var vertexCount = Block.I32(modelOffset + 80);
                var vertexByteOffset = Block.I32(modelOffset + 84);
                if (meshCount < 0 || meshCount > 100_000)
                {
                    throw new SourceFormatException($"Model {modelName} has implausible mesh count {meshCount}");
                }

                Block.Check(meshStart, (long)meshCount * MeshSize, $"meshes of model {modelName}");
                var meshes = new List<MdlMesh>(meshCount);
                for (var j = 0; j < meshCount; j++)
                {
                    var meshOffset = meshStart + j * MeshSize;
                    var lodCounts = new int[8];
                    for (var lod = 0; lod < 8; lod++)
                    {
                        lodCounts[lod] = Block.I32(meshOffset + 52 + lod * 4);
                    }

                    meshes.Add(new MdlMesh(
                        j,
                        Block.I32(meshOffset),
                        Block.I32(meshOffset + 8),
                        Block.I32(meshOffset + 12),
                        Block.I32(meshOffset + 32),
                        Block.Vec3(meshOffset + 36),
                        lodCounts));
                }

                models.Add(new MdlModel(m, modelName.Length == 0 ? $"model_{m}" : modelName, vertexCount, vertexByteOffset, meshes));
            }

            parts.Add(new MdlBodyPart(i, name, baseValue, models));
        }

        return parts;
    }

    private List<MdlAttachment> ParseAttachments()
    {
        var count = Block.I32(240);
        var start = Block.I32(244);
        if (count < 0 || count > 4096)
        {
            throw new SourceFormatException($"Implausible MDL attachment count {count}");
        }

        Block.Check(start, (long)count * AttachmentSize, "attachment table");
        var result = new List<MdlAttachment>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = start + i * AttachmentSize;
            result.Add(new MdlAttachment(
                i,
                Block.RelativeCString(offset, Block.I32(offset), $"attachment_{i}"),
                Block.I32(offset + 4),
                Block.I32(offset + 8),
                Block.Floats(offset + 12, 12, $"attachment[{i}].local")));
        }

        return result;
    }
}
