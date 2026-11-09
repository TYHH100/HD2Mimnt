using System.Runtime.InteropServices;
using System.Text;

namespace HD2Mimnt.Core.ModelPreview;

/// <summary>
/// 模型预览结果的二进制包（Worker 写、UI 读）。只包含渲染需要的网格、贴图元数据与错误信息；
/// 动画库/护甲选项当前预览界面不消费，留在 Worker 内，不随包传输。
/// </summary>
public static class ModelPreviewPack
{
    private const int Magic = 0x31564D50; // "PMV1"
    private const int Version = 1;
    private const int MaxElements = 256 * 1024 * 1024;
    private const int MaxItems = 1_000_000;
    private const int MaxStringBytes = 16 * 1024 * 1024;

    public static void Write(Stream stream, ModelPreviewResult result)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(result.Error is not null);
        if (result.Error is not null) writer.Write(result.Error);
        writer.Write(result.SkippedStreams);
        writer.Write(result.PatchFileCount);

        writer.Write(result.Meshes.Count);
        foreach (var mesh in result.Meshes) WriteMesh(stream, writer, mesh);

        writer.Write(result.Textures.Count);
        foreach (var texture in result.Textures) WriteTexture(stream, writer, texture);
    }

    public static ModelPreviewResult Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadInt32() != Magic)
            throw new InvalidDataException("Model preview pack magic mismatch.");
        var version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported model preview pack version {version}.");

        var result = new ModelPreviewResult
        {
            Error = reader.ReadBoolean() ? ReadStringChecked(reader) : null,
            SkippedStreams = reader.ReadInt32(),
            PatchFileCount = reader.ReadInt32()
        };

        var meshCount = ReadCount(reader, "meshes", MaxItems);
        for (var i = 0; i < meshCount; i++) result.Meshes.Add(ReadMesh(stream, reader));

        var textureCount = ReadCount(reader, "textures", MaxItems);
        for (var i = 0; i < textureCount; i++) result.Textures.Add(ReadTexture(reader));
        return result;
    }

    private static void WriteMesh(Stream stream, BinaryWriter writer, ModelPreviewMesh mesh)
    {
        writer.Write(mesh.PatchFile);
        writer.Write(mesh.UnitId);
        writer.Write(mesh.StreamIndex);
        writer.Write(mesh.MeshInfoIndex);
        writer.Write(mesh.SourceVertexOffset);
        writer.Write(mesh.SourceVertexCount);
        writer.Write(mesh.SourceIndexOffset);
        writer.Write(mesh.SourceIndexCount);
        writer.Write((int)mesh.BodyShape);
        writer.Write((int)mesh.CustomizationSlot);
        WriteArray(stream, writer, mesh.Positions);
        WriteNullableArray(stream, writer, mesh.Normals);
        WriteNullableArray(stream, writer, mesh.TextureCoordinates);
        WriteSkinning(stream, writer, mesh.Skinning);
        WriteArray(stream, writer, mesh.TriangleIndices);

        writer.Write(mesh.TextureIds.Count);
        foreach (var id in mesh.TextureIds) writer.Write(id);
        WriteNullable(writer, mesh.ColorTextureId);
        WriteNullable(writer, mesh.MaterialId);
        WriteMaterialTextures(stream, writer, mesh.MaterialTextures);
        writer.Write((int)mesh.RenderClass);
        writer.Write(mesh.ArmorIds.Count);
        foreach (var id in mesh.ArmorIds) writer.Write(id);
        writer.Write(mesh.IsCullingBody);
        writer.Write((int)mesh.RenderStatus);
        writer.Write(mesh.BoundsDiagonal);
        writer.Write(mesh.UvStatusText);
        writer.Write(mesh.PreviewStatusText);
    }

    private static ModelPreviewMesh ReadMesh(Stream stream, BinaryReader reader)
    {
        var mesh = new ModelPreviewMesh
        {
            PatchFile = ReadStringChecked(reader),
            UnitId = reader.ReadUInt64(),
            StreamIndex = reader.ReadInt32(),
            MeshInfoIndex = reader.ReadInt32(),
            SourceVertexOffset = reader.ReadUInt32(),
            SourceVertexCount = reader.ReadUInt32(),
            SourceIndexOffset = reader.ReadUInt32(),
            SourceIndexCount = reader.ReadUInt32(),
            BodyShape = (ModelPreviewBodyShape)reader.ReadInt32(),
            CustomizationSlot = (ModelPreviewCustomizationSlot)reader.ReadInt32(),
            Positions = ReadArray<float>(stream, reader),
            Normals = ReadNullableArray<float>(stream, reader),
            TextureCoordinates = ReadNullableArray<float>(stream, reader),
            Skinning = ReadSkinning(stream, reader),
            TriangleIndices = ReadArray<int>(stream, reader),
            TextureIds = ReadUInt64List(reader),
            ColorTextureId = ReadNullable(reader),
            MaterialId = ReadNullable(reader),
            MaterialTextures = ReadMaterialTextures(stream, reader),
            RenderClass = (ModelPreviewMaterialRenderClass)reader.ReadInt32(),
            ArmorIds = ReadStringList(reader),
            IsCullingBody = reader.ReadBoolean(),
            RenderStatus = (ModelPreviewMeshRenderStatus)reader.ReadInt32(),
            BoundsDiagonal = reader.ReadDouble()
        };
        mesh.UvStatusText = ReadStringChecked(reader);
        mesh.PreviewStatusText = ReadStringChecked(reader);
        return mesh;
    }

    private static void WriteSkinning(Stream stream, BinaryWriter writer, ModelPreviewSkinningData? skinning)
    {
        writer.Write(skinning is not null);
        if (skinning is null) return;
        WriteSkeleton(writer, skinning.Skeleton);
        WriteArray(stream, writer, skinning.TransformIndices);
        WriteArray(stream, writer, skinning.Weights);
    }

    private static ModelPreviewSkinningData? ReadSkinning(Stream stream, BinaryReader reader)
    {
        if (!reader.ReadBoolean()) return null;
        return new ModelPreviewSkinningData
        {
            Skeleton = ReadSkeleton(reader),
            TransformIndices = ReadArray<int>(stream, reader),
            Weights = ReadArray<float>(stream, reader)
        };
    }

    private static void WriteSkeleton(BinaryWriter writer, ModelPreviewSkeleton skeleton)
    {
        writer.Write(skeleton.BonesId);
        writer.Write(skeleton.StateMachineId);
        writer.Write(skeleton.Bones.Count);
        foreach (var bone in skeleton.Bones)
        {
            writer.Write(bone.ParentIndex);
            writer.Write(bone.NameHash);
            var matrix = bone.BindTransform;
            WriteMatrix(writer, matrix);
        }
    }

    private static ModelPreviewSkeleton ReadSkeleton(BinaryReader reader)
    {
        var bonesId = reader.ReadUInt64();
        var stateMachineId = reader.ReadUInt64();
        var count = ReadCount(reader, "bones", MaxItems);
        var bones = new ModelPreviewSkeletonBone[count];
        for (var i = 0; i < count; i++)
        {
            var parentIndex = reader.ReadInt32();
            var nameHash = reader.ReadUInt32();
            bones[i] = new ModelPreviewSkeletonBone(parentIndex, nameHash, ReadMatrix(reader));
        }
        return new ModelPreviewSkeleton
        {
            BonesId = bonesId,
            StateMachineId = stateMachineId,
            Bones = bones
        };
    }

    private static void WriteMatrix(BinaryWriter writer, System.Numerics.Matrix4x4 m)
    {
        writer.Write(m.M11); writer.Write(m.M12); writer.Write(m.M13); writer.Write(m.M14);
        writer.Write(m.M21); writer.Write(m.M22); writer.Write(m.M23); writer.Write(m.M24);
        writer.Write(m.M31); writer.Write(m.M32); writer.Write(m.M33); writer.Write(m.M34);
        writer.Write(m.M41); writer.Write(m.M42); writer.Write(m.M43); writer.Write(m.M44);
    }

    private static System.Numerics.Matrix4x4 ReadMatrix(BinaryReader reader) => new(
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    private static void WriteMaterialTextures(Stream stream, BinaryWriter writer,
        ModelPreviewMaterialTextureSet set)
    {
        writer.Write(set.ByRole.Count);
        foreach (var (role, ids) in set.ByRole)
        {
            writer.Write((int)role);
            writer.Write(ids.Count);
            foreach (var id in ids) writer.Write(id);
        }
        writer.Write(set.AllTextureIds.Count);
        foreach (var id in set.AllTextureIds) writer.Write(id);
        WriteNullable(writer, set.ColorTextureId);
        writer.Write(set.Inputs is not null);
        if (set.Inputs is not null)
        {
            writer.Write(set.Inputs.Count);
            foreach (var input in set.Inputs)
            {
                writer.Write(input.SemanticId);
                writer.Write(input.TextureId);
                writer.Write((int)input.Role);
            }
        }
        writer.Write(set.ColorAlphaIsMetallic);
    }

    private static ModelPreviewMaterialTextureSet ReadMaterialTextures(Stream stream, BinaryReader reader)
    {
        var byRole = new Dictionary<ModelPreviewTextureRole, IReadOnlyList<ulong>>();
        var roleCount = ReadCount(reader, "material roles", MaxItems);
        for (var i = 0; i < roleCount; i++)
        {
            var role = (ModelPreviewTextureRole)reader.ReadInt32();
            byRole[role] = ReadUInt64List(reader);
        }
        var allIds = ReadUInt64List(reader);
        var colorTextureId = ReadNullable(reader);
        List<ModelPreviewMaterialInput>? inputs = null;
        if (reader.ReadBoolean())
        {
            var count = ReadCount(reader, "material inputs", MaxItems);
            EnsureRemaining(reader.BaseStream, (long)count * 16, "material inputs");
            inputs = new List<ModelPreviewMaterialInput>(count);
            for (var i = 0; i < count; i++)
                inputs.Add(new ModelPreviewMaterialInput(reader.ReadUInt32(), reader.ReadUInt64(),
                    (ModelPreviewTextureRole)reader.ReadInt32()));
        }
        return new ModelPreviewMaterialTextureSet(byRole, allIds, colorTextureId, inputs, reader.ReadBoolean());
    }

    private static void WriteTexture(Stream stream, BinaryWriter writer, TextureInspectionItem texture)
    {
        writer.Write(texture.PatchFile);
        writer.Write(texture.PatchPath);
        writer.Write(texture.PatchOrder);
        writer.Write(texture.TocEntryIndex);
        writer.Write(texture.TextureId);
        writer.Write(texture.MainOffset);
        writer.Write(texture.MainSize);
        writer.Write(texture.GpuOffset);
        writer.Write(texture.GpuSize);
        writer.Write(texture.StreamOffset);
        writer.Write(texture.StreamSize);
        writer.Write(texture.Width);
        writer.Write(texture.Height);
        writer.Write(texture.MipCount);
        writer.Write(texture.DxgiFormat);
        writer.Write(texture.PayloadKind);
        writer.Write(texture.PayloadSource);
        writer.Write(texture.AlphaIsMaterialParameter);
        writer.Write((int)texture.PreviewRole);
        writer.Write(texture.PreviewRoleText);
    }

    private static TextureInspectionItem ReadTexture(BinaryReader reader)
    {
        return new TextureInspectionItem
        {
            PatchFile = ReadStringChecked(reader),
            PatchPath = ReadStringChecked(reader),
            PatchOrder = reader.ReadInt32(),
            TocEntryIndex = reader.ReadInt32(),
            TextureId = reader.ReadUInt64(),
            MainOffset = reader.ReadUInt64(),
            MainSize = reader.ReadUInt32(),
            GpuOffset = reader.ReadUInt64(),
            GpuSize = reader.ReadUInt32(),
            StreamOffset = reader.ReadUInt64(),
            StreamSize = reader.ReadUInt32(),
            Width = reader.ReadInt32(),
            Height = reader.ReadInt32(),
            MipCount = reader.ReadInt32(),
            DxgiFormat = reader.ReadInt32(),
            PayloadKind = ReadStringChecked(reader),
            PayloadSource = ReadStringChecked(reader),
            AlphaIsMaterialParameter = reader.ReadBoolean(),
            PreviewRole = (TexturePreviewRole)reader.ReadInt32(),
            PreviewRoleText = ReadStringChecked(reader)
        };
    }

    private static void WriteArray<T>(Stream stream, BinaryWriter writer, T[] values) where T : struct
    {
        writer.Write(values.Length);
        stream.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static void WriteNullableArray<T>(Stream stream, BinaryWriter writer, T[]? values) where T : struct
    {
        writer.Write(values is not null);
        if (values is not null) WriteArray(stream, writer, values);
    }

    private static T[] ReadArray<T>(Stream stream, BinaryReader reader) where T : struct
    {
        var count = ReadCount(reader, typeof(T).Name);
        EnsureRemaining(stream, (long)count * Marshal.SizeOf<T>(), typeof(T).Name);
        var values = new T[count];
        ReadExactly(stream, MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }

    private static T[]? ReadNullableArray<T>(Stream stream, BinaryReader reader) where T : struct =>
        reader.ReadBoolean() ? ReadArray<T>(stream, reader) : null;

    private static void WriteNullable(BinaryWriter writer, ulong? value)
    {
        writer.Write(value is not null);
        if (value is { } id) writer.Write(id);
    }

    private static ulong? ReadNullable(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadUInt64() : null;

    private static List<ulong> ReadUInt64List(BinaryReader reader)
    {
        var count = ReadCount(reader, "uint64 list", MaxItems);
        EnsureRemaining(reader.BaseStream, (long)count * 8, "uint64 list");
        var list = new List<ulong>(count);
        for (var i = 0; i < count; i++) list.Add(reader.ReadUInt64());
        return list;
    }

    private static List<string> ReadStringList(BinaryReader reader)
    {
        var count = ReadCount(reader, "string list", MaxItems);
        var list = new List<string>(count);
        for (var i = 0; i < count; i++) list.Add(ReadStringChecked(reader));
        return list;
    }

    private static string ReadStringChecked(BinaryReader reader)
    {
        var length = Read7BitEncodedInt(reader);
        if (length < 0 || length > MaxStringBytes)
            throw new InvalidDataException($"Invalid string length {length}.");
        EnsureRemaining(reader.BaseStream, length, "string");
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException("Model preview pack ended unexpectedly.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        uint result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var value = reader.ReadByte();
            result |= (uint)(value & 0x7F) << shift;
            if ((value & 0x80) == 0) return (int)result;
        }
        throw new InvalidDataException("Invalid string length prefix.");
    }

    private static int ReadCount(BinaryReader reader, string what, int max = MaxElements)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > max)
            throw new InvalidDataException($"Invalid {what} count {count}.");
        return count;
    }

    private static void EnsureRemaining(Stream stream, long bytes, string what)
    {
        if (!stream.CanSeek) return;
        if (bytes > stream.Length - stream.Position)
            throw new InvalidDataException($"Truncated {what} payload.");
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer[offset..]);
            if (read <= 0) throw new EndOfStreamException("Model preview pack ended unexpectedly.");
            offset += read;
        }
    }
}
