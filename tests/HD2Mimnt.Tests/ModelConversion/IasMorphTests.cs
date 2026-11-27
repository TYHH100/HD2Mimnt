using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HD2Mimnt.Infrastructure.ModelConversion;
using Xunit;

namespace HD2Mimnt.Tests.ModelConversion;

public class IasMorphTests
{
    [Fact]
    public void Read_ValidSidecar_DecodesDirectoryAndRecords()
    {
        var raw = BuildSidecar();

        var program = IasMorphFormat.Read("face.hd2morph", raw);

        Assert.Equal("face.hd2morph", program.Name);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), program.Sha256);
        Assert.Equal("HD2Morph1", program.Document.Directory.GetProperty("schema").GetString());
        Assert.Equal("5".PadLeft(32, '5'), program.Document.ProjectId);
        var channel = Assert.Single(program.Document.Channels);
        Assert.Equal("Blink", channel.Name);
        Assert.Equal(0, channel.Baseline);
        var mesh = Assert.Single(program.Document.Meshes);
        Assert.Equal("1".PadLeft(16, '1'), mesh.UnitId);
        var record = Assert.Single(Assert.Single(mesh.Deltas).Records);
        Assert.Equal(0, record.Vertex);
        Assert.Equal(0.25f, record.Values[0]);
    }

    [Fact]
    public void Read_DamagedPayload_RejectsIntegrityFailure()
    {
        var raw = BuildSidecar();
        raw[^1] ^= 1;

        Assert.Throws<InvalidDataException>(() => IasMorphFormat.Read("face.hd2morph", raw));
    }

    [Fact]
    public void Inject_SimpleVertexShader_ProducesD3D11ValidatedMorphShader()
    {
        const string source = "float4 main(float4 p : POSITION, float4 n : NORMAL) : SV_POSITION { return p + n * 0.001; }";
        var original = IasNativeMorphShader.CompileHlsl(source, "vs_5_0");
        using var validator = new IasNativeMorphShader.DeviceValidator();

        var injected = IasNativeMorphShader.Inject(original, [64, 68, 72, 76, 80, 84], validator);

        Assert.True(injected.AddedObjectBufferBinding);
        Assert.Equal(96, injected.ObjectBufferSize);
        Assert.True(injected.Code.AsSpan().StartsWith("DXBC"u8));
        Assert.Contains("dcl_constantbuffer", IasNativeMorphShader.Disassemble(injected.Code));
    }

    [Fact]
    public void Blake2s_KnownVector_MatchesUpstream()
    {
        Assert.Equal("0e8ac6520eb9581512e372a3", Convert.ToHexString(IasMorphInputs.Blake2s("abc"u8, 12)).ToLowerInvariant());
    }

    [Fact]
    public void Pair_VerifiedPreviewAndMatchingUnit_AcceptsSource()
    {
        var unitId = "1".PadLeft(16, '1');
        var archiveId = "6".PadLeft(16, '6');
        var projectId = "5".PadLeft(32, '5');
        var resource = new IasResource([1, 2, 3], Array.Empty<byte>(), Array.Empty<byte>());
        var fingerprint = IasHash.ResourceFingerprint(resource);
        var program = new IasMorphSourceProgram("face.hd2morph", new string('a', 64), new IasMorphDocument(
            projectId, archiveId, new string('7', 64), [],
            [new IasMorphMesh(unitId, 0, 0, 0, 0, 1, 1, 1, 12, 0, 12, new string('2', 64),
                new string('3', 64), fingerprint, [], [0, 0, 0], [1, 1, 1], 0, [], [])],
            default));
        var preview = new byte[96];
        "HD2PRV1\0"u8.CopyTo(preview);
        BinaryPrimitives.WriteUInt32LittleEndian(preview.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(preview.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(preview.AsSpan(16), 96);
        Encoding.ASCII.GetBytes(projectId).CopyTo(preview, 32);
        BinaryPrimitives.WriteUInt64LittleEndian(preview.AsSpan(88), ulong.Parse(archiveId, NumberStyles.HexNumber));
        var members = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["face.hd2preview"] = preview };
        var profile = new IasRigProfile(unitId, [], 0, 0, "", 0, []);
        var data = new IasCompanionData(
            new IasCompanionMetadata(new string('1', 32), [new IasCompanionMember(archiveId, [])], null),
            "test", [], [], new Dictionary<string, IasRigProfile> { [unitId] = profile },
            new Dictionary<string, IasProfileTable>(), [], [], new Dictionary<string, IasRig>(),
            new IasFaceAnimation([], [], 0, 0, "", ""), [], new Dictionary<string, (int, string)>());

        var paired = IasMorphInputs.Pair([program], data, members,
            new Dictionary<(ulong, ulong), IasResource> { [(IasResourceTypes.Unit, ulong.Parse(unitId, NumberStyles.HexNumber))] = resource });

        Assert.Single(paired);
    }

    [Fact]
    public void Packager_MorphMaterials_AreWrittenToIndependentPatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2morph-package-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            const ulong originalId = 0x1111111111111111;
            const ulong privateId = 0x2222222222222222;
            const ulong packageId = 0x9ba626afa44a3aa3;
            var packageArchive = IasArchive.Write(new Dictionary<(ulong, ulong), IasResource>
            {
                [(IasResourceTypes.Package, packageId)] = new IasResource(new byte[16], [], [])
            });
            File.WriteAllBytes(Path.Combine(root, "9ba626afa44a3aa3"), packageArchive.Toc);
            File.WriteAllBytes(Path.Combine(root, "9ba626afa44a3aa3.gpu_resources"), packageArchive.Gpu);
            File.WriteAllBytes(Path.Combine(root, "9ba626afa44a3aa3.stream"), packageArchive.Stream);
            var material = new IasResource(new byte[136], [], []);
            var compiled = new IasCompiledModel("Morph", new Dictionary<(ulong, ulong), IasResource>(),
                IasValue.Obj(("version", IasValue.Of(2L)), ("models", IasValue.Of(new[] { IasValue.Obj(("id", IasValue.Of("ias_test"))) }))),
                IasValue.Obj(("protocol", IasValue.Of(1L)), ("id", IasValue.Of("ias_test")),
                    ("module", IasValue.Of("mods/hd2qm/ias_test/v1/catalog")), ("package", IasValue.Of("9ba626afa44a3aa3")),
                    ("materials", IasValue.Of(Array.Empty<IasValue>()))),
                [], [originalId, privateId], [originalId], new Dictionary<string, IasGeometryStats>(),
                MorphMaterials: new Dictionary<(ulong, ulong), IasResource> { [(IasResourceTypes.Material, privateId)] = material });
            using var game = new IasGameData(root);

            var packaged = IasModelPackager.Build(new IasPackageRequest("ias_test", "Morph", "Author", "Description",
                null, null, null, compiled,
                new Dictionary<(ulong, ulong), IasResource> { [(IasResourceTypes.Material, originalId)] = material }, game));

            var morphPatch = Assert.Single(packaged.Archives.Where(archive => archive.BaseName.EndsWith(".patch_3")));
            Assert.True(morphPatch.Entries.ContainsKey((IasResourceTypes.Material, privateId)));
            var registration = (IasValue.Object)packaged.Registration;
            var materials = (IasValue.Array)registration.Entries.Single(entry => entry.Key == "materials").Value;
            Assert.Contains(privateId.ToString("x16"), materials.Items.Select(item => ((IasValue.Text)item).Value));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] BuildSidecar()
    {
        var channelId = "1".PadLeft(32, '1');
        var component = IasValue.Obj(("type", IasValue.Of(0L)), ("format", IasValue.Of(0L)),
            ("index", IasValue.Of(0L)), ("offset", IasValue.Of(0L)), ("size", IasValue.Of(12L)));
        var delta = IasValue.Obj(("channel_id", IasValue.Of(channelId)),
            ("record_offset", IasValue.Of(0L)), ("record_count", IasValue.Of(1L)));
        var mesh = IasValue.Obj(
            ("unit_id", IasValue.Of("1".PadLeft(16, '1'))), ("mesh_index", IasValue.Of(0L)),
            ("lod_index", IasValue.Of(0L)), ("stream_index", IasValue.Of(0L)),
            ("vertex_offset", IasValue.Of(0L)), ("unit_version", IasValue.Of(1L)),
            ("vertex_count", IasValue.Of(1L)), ("stream_vertex_count", IasValue.Of(1L)),
            ("stride", IasValue.Of(12L)), ("vertex_buffer_offset", IasValue.Of(0L)),
            ("vertex_buffer_size", IasValue.Of(12L)), ("stream_sha256", IasValue.Of("2".PadLeft(64, '2'))),
            ("index_sha256", IasValue.Of("3".PadLeft(64, '3'))), ("unit_sha256", IasValue.Of("4".PadLeft(64, '4'))),
            ("components", IasValue.Of(new[] { component })), ("bounds_min", IasValue.Of(new IasValue[] { IasValue.Of(0.0), IasValue.Of(0.0), IasValue.Of(0.0) })),
            ("bounds_max", IasValue.Of(new IasValue[] { IasValue.Of(1.0), IasValue.Of(1.0), IasValue.Of(1.0) })),
            ("deltas", IasValue.Of(new[] { delta })));
        var channel = IasValue.Obj(("id", IasValue.Of(channelId)), ("name", IasValue.Of("Blink")),
            ("source_object", IasValue.Of("Face")), ("relative_key", IasValue.Of("Basis")),
            ("min", IasValue.Of(0.0)), ("max", IasValue.Of(1.0)), ("default", IasValue.Of(0.0)),
            ("baseline", IasValue.Of(0.0)), ("normal_sample_weight", IasValue.Of(1.0)),
            ("has_driver", IasValue.Of(false)), ("muted", IasValue.Of(false)));
        var directory = IasValue.Obj(("schema", IasValue.Of("HD2Morph1")),
            ("coordinate_space", IasValue.Of("mesh_local_pre_skinning")),
            ("normal_mode", IasValue.Of("normalized_linear_delta")),
            ("project_id", IasValue.Of("5".PadLeft(32, '5'))), ("archive_id", IasValue.Of("6".PadLeft(16, '6'))),
            ("source_sha256", IasValue.Of("7".PadLeft(64, '7'))), ("channels", IasValue.Of(new[] { channel })),
            ("meshes", IasValue.Of(new[] { mesh })), ("extension", IasValue.Obj(("note", IasValue.Of("测试")))));
        var json = Encoding.UTF8.GetBytes(IasLua.WriteJson(directory, ensureAscii: false, sortKeys: true, indent: 0));
        var raw = new byte[72 + json.Length + 28];
        "HD2MOR1\0"u8.CopyTo(raw);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), 72);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(16), (ulong)raw.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(24), (ulong)json.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(raw.AsSpan(32), 1);
        json.CopyTo(raw, 72);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(72 + json.Length), 0);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(76 + json.Length), 0.25f);
        SHA256.HashData(raw.AsSpan(72)).CopyTo(raw, 40);
        return raw;
    }
}
