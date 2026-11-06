using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Gma;
using HD2Mimnt.ModelConversion.Source.Mdl;

namespace HD2Mimnt.ModelConversion.Gmod.Geometry;

/// <summary>
/// 自描述的完整模型中间包 HD2GEOM1：保留全部骨骼、所有 bodygroup 备选 × skin 家族的
/// 全部网格、每个顶点的位置/法线/UV/切线/三路骨骼权重、附件、动骨参数以及 IAS 选项组合。
/// 采用长度前缀记录，便于后续演进；CRC32 覆盖全部 payload。
/// </summary>
public static class GmodGeometryCodec
{
    // 8-byte magic + seven uint32 fields (version, header size, bone/mesh/attachment counts,
    // payload CRC32 and option count).
    private const int HeaderSize = 36;
    private const uint Version = 1;
    private const uint MetaRecord = 1;
    private const uint BoneRecord = 2;
    private const uint MeshRecord = 3;
    private const uint AttachmentRecord = 4;
    private const uint OptionRecord = 5;
    private const int VertexSize = 64;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] Write(GmodGeometryBundle bundle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(bundle);
        using var payload = new MemoryStream();
        var writer = new Writer(payload);

        writer.Record(MetaRecord, w =>
        {
            var meta = JsonSerializer.SerializeToUtf8Bytes(new MetaDto(
                bundle.ModelPath, bundle.VtxExtension, bundle.LodCount, bundle.SkinFamilyCount,
                bundle.Issues.Select(issue => new IssueDto(issue.Code, issue.Asset, issue.Message, (int)issue.Severity)).ToArray()),
                JsonOptions);
            w.U32((uint)meta.Length);
            w.Bytes(meta);
        });

        foreach (var bone in bundle.Bones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Record(BoneRecord, w =>
            {
                w.String(bone.Name);
                w.I32(bone.Parent);
                w.U32(bone.Flags);
                w.I32(bone.ProceduralType);
                w.I32(bone.PhysicsBone);
                w.Vec3(bone.Position);
                w.Quat(bone.Rotation);
                for (var i = 0; i < 12; i++) w.F32(bone.PoseToBone[i]);
                w.String(bone.SurfaceProp);
                w.U8(bone.Jiggle is null ? (byte)0 : (byte)1);
                if (bone.Jiggle is { } jiggle)
                {
                    w.U32((uint)jiggle.Flags);
                    foreach (var value in JiggleValues(jiggle)) w.F32(value);
                }
            });
        }

        foreach (var mesh in bundle.Meshes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Record(MeshRecord, w =>
            {
                w.String(mesh.Id);
                w.I32(mesh.BodyPart);
                w.I32(mesh.Model);
                w.I32(mesh.Skin);
                w.I32(mesh.Mesh);
                w.I32(mesh.MaterialSlot);
                w.String(mesh.MaterialPath);
                w.String(mesh.ModelName);
                w.U32((uint)mesh.Vertices.Length);
                w.U32((uint)mesh.Indices.Length);
                foreach (var vertex in mesh.Vertices)
                {
                    w.Vec3(vertex.Position);
                    w.Vec3(vertex.Normal);
                    w.F32(vertex.TexCoord.X);
                    w.F32(vertex.TexCoord.Y);
                    w.Quat(vertex.Tangent);
                    for (var lane = 0; lane < 3; lane++) w.F32(lane < vertex.Weights.Length ? vertex.Weights[lane] : 0);
                    for (var lane = 0; lane < 3; lane++) w.U8(lane < vertex.Bones.Length ? vertex.Bones[lane] : (byte)0);
                    w.U8((byte)Math.Min(3, vertex.Bones.Length));
                }
                foreach (var index in mesh.Indices) w.U32((uint)index);
            });
        }

        foreach (var attachment in bundle.Attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Record(AttachmentRecord, w =>
            {
                w.I32(attachment.Index);
                w.I32(attachment.Flags);
                w.I32(attachment.LocalBone);
                w.String(attachment.Name);
                foreach (var value in attachment.LocalMatrix) w.F32(value);
            });
        }

        foreach (var option in bundle.Options)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.Record(OptionRecord, w =>
            {
                w.String(option.OptionId);
                w.U32((uint)option.Choices.Count);
                foreach (var choice in option.Choices)
                {
                    w.String(choice.Key);
                    w.I32(choice.Value);
                }
                w.U32((uint)option.MeshIds.Count);
                foreach (var id in option.MeshIds) w.String(id);
            });
        }

        var payloadBytes = payload.ToArray();
        var result = new byte[HeaderSize + payloadBytes.Length];
        var header = new Writer(new MemoryStream(result, writable: true));
        header.Bytes("HD2GEOM1"u8);
        header.U32(Version);
        header.U32(HeaderSize);
        header.U32((uint)bundle.Bones.Count);
        header.U32((uint)bundle.Meshes.Count);
        header.U32((uint)bundle.Attachments.Count);
        header.U32(Crc32.Compute(payloadBytes, cancellationToken));
        header.U32((uint)bundle.Options.Count);
        payloadBytes.CopyTo(result, HeaderSize);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public static GmodGeometryBundle Read(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new Reader(data);
        Require(reader.Take(8).SequenceEqual("HD2GEOM1"u8), "Invalid HD2GEOM1 magic.");
        Require(reader.U32() == Version, "Unsupported HD2GEOM1 version.");
        Require(reader.U32() == HeaderSize, "Invalid HD2GEOM1 header size.");
        var boneCount = reader.U32();
        var meshCount = reader.U32();
        var attachmentCount = reader.U32();
        var crc = reader.U32();
        var optionCount = reader.U32();
        Require(Crc32.Compute(data[HeaderSize..], cancellationToken) == crc, "HD2GEOM1 payload CRC32 mismatch.");
        Require(boneCount <= 4096 && meshCount <= 1_000_000 && attachmentCount <= 4096 && optionCount <= 4096,
            "HD2GEOM1 counts are implausible.");

        MetaDto meta = new("", "", 0, 0, []);
        var bones = new List<GmodBone>((int)boneCount);
        var meshes = new List<GmodMesh>((int)meshCount);
        var attachments = new List<GmodAttachment>((int)attachmentCount);
        var options = new List<GmodOptionGeometry>((int)optionCount);

        while (reader.Remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = reader.U32();
            var length = reader.U32();
            Require(length <= reader.Remaining, "Truncated HD2GEOM1 record.");
            var record = new Reader(reader.Take((int)length));
            switch (kind)
            {
                case MetaRecord:
                {
                    var bytes = record.Take(record.I32());
                    meta = JsonSerializer.Deserialize<MetaDto>(bytes, JsonOptions)
                        ?? throw new SourceFormatException("HD2GEOM1 metadata is null.");
                    break;
                }
                case BoneRecord:
                {
                    var name = record.String();
                    var parent = record.I32();
                    var flags = record.U32();
                    var procedural = record.I32();
                    var physicsBone = record.I32();
                    var position = record.Vec3();
                    var rotation = record.Rotation();
                    var pose = new float[12];
                    for (var i = 0; i < pose.Length; i++) pose[i] = record.F32();
                    var surface = record.String();
                    MdlJiggleBone? jiggle = null;
                    if (record.U8() != 0)
                    {
                        var jiggleFlags = (MdlJiggleFlags)record.U32();
                        var values = new float[34];
                        for (var i = 0; i < values.Length; i++) values[i] = record.F32();
                        jiggle = BuildJiggle(jiggleFlags, values);
                    }
                    bones.Add(new GmodBone(bones.Count, name, parent, position, rotation, pose, flags,
                        procedural, physicsBone, surface, jiggle));
                    break;
                }
                case MeshRecord:
                {
                    var id = record.String();
                    var bodyPart = record.I32();
                    var model = record.I32();
                    var skin = record.I32();
                    var meshIndex = record.I32();
                    var materialSlot = record.I32();
                    var materialPath = record.String();
                    var modelName = record.String();
                    var vertexCount = record.U32();
                    var indexCount = record.U32();
                    Require(record.Remaining >= vertexCount * VertexSize + indexCount * 4L, "Truncated HD2GEOM1 mesh.");
                    var vertices = new GmodVertex[vertexCount];
                    for (var i = 0; i < vertices.Length; i++)
                    {
                        var position = record.Vec3();
                        var normal = record.Vec3();
                        var uv = new Vector2(record.F32(), record.F32());
                        var tangent = record.Quat();
                        var weights = new[] { record.F32(), record.F32(), record.F32() };
                        var boneIds = new[] { record.U8(), record.U8(), record.U8() };
                        var count = record.U8();
                        Require(count <= 3, "HD2GEOM1 vertex has more than three bone influences.");
                        vertices[i] = new GmodVertex(position, normal, uv, tangent,
                            weights[..count], boneIds[..count]);
                    }
                    var indices = new int[indexCount];
                    for (var i = 0; i < indices.Length; i++) indices[i] = (int)record.U32();
                    meshes.Add(new GmodMesh(id, bodyPart, model, skin, meshIndex, materialSlot,
                        materialPath, modelName, vertices, indices));
                    break;
                }
                case AttachmentRecord:
                {
                    var index = record.I32();
                    var flags = record.I32();
                    var localBone = record.I32();
                    var name = record.String();
                    var matrix = new float[12];
                    for (var i = 0; i < matrix.Length; i++) matrix[i] = record.F32();
                    attachments.Add(new GmodAttachment(index, name, flags, localBone, matrix));
                    break;
                }
                case OptionRecord:
                {
                    var id = record.String();
                    var choiceCount = record.U32();
                    var choices = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (var i = 0; i < choiceCount; i++)
                    {
                        var key = record.String();
                        choices[key] = record.I32();
                    }
                    var meshIdCount = record.U32();
                    var ids = new string[meshIdCount];
                    for (var i = 0; i < ids.Length; i++) ids[i] = record.String();
                    options.Add(new GmodOptionGeometry(id, choices, ids));
                    break;
                }
                default:
                    throw new SourceFormatException($"Unknown HD2GEOM1 record kind {kind}.");
            }
            Require(record.Remaining == 0, "HD2GEOM1 record contains trailing data.");
        }

        Require(bones.Count == boneCount && meshes.Count == meshCount &&
            attachments.Count == attachmentCount && options.Count == optionCount,
            "HD2GEOM1 header counts do not match its records.");
        return new GmodGeometryBundle(meta.ModelPath, meta.VtxExtension, bones.AsReadOnly(),
            meshes.AsReadOnly(), attachments.AsReadOnly(), options.AsReadOnly(), meta.LodCount,
            meta.SkinFamilyCount, meta.Issues.Select(issue => new GmodConversionIssue(issue.Code,
                issue.Asset, issue.Message, (GmodIssueSeverity)issue.Severity)).ToArray().AsReadOnly());
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private sealed record MetaDto(string ModelPath, string VtxExtension, int LodCount, int SkinFamilyCount, IssueDto[] Issues);
    private sealed record IssueDto(string Code, string Asset, string Message, int Severity);

    private static IEnumerable<float> JiggleValues(MdlJiggleBone jiggle)
    {
        yield return jiggle.Length;
        yield return jiggle.TipMass;
        yield return jiggle.YawStiffness;
        yield return jiggle.YawDamping;
        yield return jiggle.PitchStiffness;
        yield return jiggle.PitchDamping;
        yield return jiggle.AlongStiffness;
        yield return jiggle.AlongDamping;
        yield return jiggle.AngleLimit;
        yield return jiggle.MinYaw;
        yield return jiggle.MaxYaw;
        yield return jiggle.YawFriction;
        yield return jiggle.YawBounce;
        yield return jiggle.MinPitch;
        yield return jiggle.MaxPitch;
        yield return jiggle.PitchFriction;
        yield return jiggle.PitchBounce;
        yield return jiggle.BaseMass;
        yield return jiggle.BaseStiffness;
        yield return jiggle.BaseDamping;
        yield return jiggle.BaseMinLeft;
        yield return jiggle.BaseMaxLeft;
        yield return jiggle.BaseLeftFriction;
        yield return jiggle.BaseMinUp;
        yield return jiggle.BaseMaxUp;
        yield return jiggle.BaseUpFriction;
        yield return jiggle.BaseMinForward;
        yield return jiggle.BaseMaxForward;
        yield return jiggle.BaseForwardFriction;
        yield return jiggle.Boing.ImpactSpeed;
        yield return jiggle.Boing.ImpactAngle;
        yield return jiggle.Boing.DampingRate;
        yield return jiggle.Boing.Frequency;
        yield return jiggle.Boing.Amplitude;
    }

    private static MdlJiggleBone BuildJiggle(MdlJiggleFlags flags, float[] v) => new(
        flags, v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9], v[10], v[11], v[12],
        v[13], v[14], v[15], v[16], v[17], v[18], v[19], v[20], v[21], v[22], v[23], v[24],
        v[25], v[26], v[27], v[28], new MdlJiggleBoing(v[29], v[30], v[31], v[32], v[33]));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new SourceFormatException($"HD2GEOM1: {message}");
    }

    private sealed class Writer(Stream stream)
    {
        public void U8(byte value) => stream.WriteByte(value);
        public void U32(uint value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, value); stream.Write(s); }
        public void I32(int value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, value); stream.Write(s); }
        public void F32(float value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, value); stream.Write(s); }
        public void Bytes(ReadOnlySpan<byte> value) => stream.Write(value);
        public void Vec3(Vector3 value) { F32(value.X); F32(value.Y); F32(value.Z); }
        public void Quat(Vector4 value) { F32(value.X); F32(value.Y); F32(value.Z); F32(value.W); }
        public void Quat(Quaternion value) { F32(value.X); F32(value.Y); F32(value.Z); F32(value.W); }

        public void String(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            U32((uint)bytes.Length);
            Bytes(bytes);
        }

        public void Record(uint kind, Action<Writer> body)
        {
            using var buffer = new MemoryStream();
            var nested = new Writer(buffer);
            body(nested);
            var bytes = buffer.ToArray();
            U32(kind);
            U32((uint)bytes.Length);
            Bytes(bytes);
        }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public int Remaining => _remaining.Length;

        public ReadOnlySpan<byte> Take(int length)
        {
            Require(length >= 0 && length <= _remaining.Length, "Truncated record.");
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }

        public byte U8() => Take(1)[0];
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public Vector4 Quat() => new(F32(), F32(), F32(), F32());
        public Quaternion Rotation() => new(F32(), F32(), F32(), F32());

        public string String()
        {
            var length = I32();
            Require(length >= 0 && length <= _remaining.Length, "Invalid string length.");
            var bytes = Take(length);
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { throw new SourceFormatException("HD2GEOM1 string is not UTF-8."); }
        }
    }
}
