using System.Text.Json;
using System.Text.Json.Serialization;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasPhysicsDomain(string Id, int Count, IReadOnlyList<int> Parents, byte[] Rig, byte[] Physics);

public sealed record IasNativeDataSpec(int Version, string Module, string Sha256);

public sealed record IasNativeDeclaration(string Id, string Key, string Directory, int Count,
    IReadOnlyList<IasRigNode> Nodes);

public sealed record IasNativeDomainDocument(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("parents")] IReadOnlyList<int> Parents,
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("rig")] IasNativePayload Rig,
    [property: JsonPropertyName("physics")] IasNativePayload Physics);

public sealed record IasNativePayload(
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("base64")] string Base64);

public sealed record IasNativeDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("domains")] IReadOnlyList<IasNativeDomainDocument> Domains);

/// <summary>私有物理数据：以 Lua 资源承载 base64 Rig/物理，游戏侧只读数据不执行。</summary>
public static class IasNativeModelData
{
    public const string Engine = "native_companion_v1";
    private const int MaxResource = 4_194_304;
    private const int MaxDomains = 16;
    private static readonly byte[] Prefix = "-- HD2CL_NATIVE_DATA_V1\nreturn [==["u8.ToArray();
    private static readonly byte[] Suffix = "]==]\n"u8.ToArray();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = null,
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static (byte[] Raw, IasNativeDataSpec Spec) Encode(string modelModule, string modelId, IReadOnlyList<IasPhysicsDomain> groups)
    {
        var namespaceId = modelModule.Split('/')[2];
        var rows = new List<IasNativeDomainDocument>(groups.Count);
        foreach (var group in groups)
        {
            var key = IasHash.Sha256Hex(group.Rig.Concat(group.Physics).ToArray());
            rows.Add(new IasNativeDomainDocument(group.Id, group.Count, group.Parents,
                namespaceId + "/" + group.Id + "/" + key[..16], key,
                new IasNativePayload(IasHash.Sha256Hex(group.Rig), Convert.ToBase64String(group.Rig)),
                new IasNativePayload(IasHash.Sha256Hex(group.Physics), Convert.ToBase64String(group.Physics))));
        }
        var document = new IasNativeDocument(1, Engine, modelId, rows);
        var body = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        var raw = Prefix.Concat(body).Concat(Suffix).ToArray();
        if (raw.Length > MaxResource) throw new InvalidDataException("角色物理数据无效：资源超过容量");
        var spec = new IasNativeDataSpec(1, modelModule[..(modelModule.LastIndexOf('/'))] + "/native_data", IasHash.Sha256Hex(raw));
        ValidateRaw(raw, spec, modelId);
        return (raw, spec);
    }

    /// <summary>回读校验：摘要、容器、域身份与二进制骨架必须与登记一致。</summary>
    public static IasNativeDocument ValidateRaw(byte[] raw, IasNativeDataSpec spec, string modelId)
    {
        if (spec.Version != 1 || !raw.AsSpan().StartsWith(Prefix) || !raw.AsSpan().EndsWith(Suffix)
            || IasHash.Sha256Hex(raw) != spec.Sha256)
            throw new InvalidDataException("角色物理数据无效：私有资源摘要或容器不匹配");
        IasBinary.Require(raw.AsSpan().IndexOf("]==]"u8) < 0 || raw.AsSpan().IndexOf("]==]"u8) >= raw.Length - Suffix.Length,
            "数据容器提前结束");
        var body = raw.AsSpan(Prefix.Length, raw.Length - Prefix.Length - Suffix.Length);
        var document = JsonSerializer.Deserialize<IasNativeDocument>(body, JsonOptions)
                       ?? throw new InvalidDataException("角色物理数据无效：JSON");
        IasBinary.Require(document.Version == 1 && document.Engine == Engine && document.Model == modelId, "数据协议或所属模型不匹配");
        IasBinary.Require(document.Domains.Count is >= 1 and <= MaxDomains, "域数量越界");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in document.Domains)
        {
            IasBinary.Require(ulong.TryParse(row.Id, System.Globalization.NumberStyles.HexNumber, null, out _)
                              && row.Key.Length == 64 && row.Id.Length == 16 && seen.Add(row.Id)
                              && row.Directory == document.Domains[0].Directory.Split('/')[0] + "/" + row.Id + "/" + row.Key[..16],
                "域身份或目录无效");
            var rig = Convert.FromBase64String(row.Rig.Base64);
            var physics = Convert.FromBase64String(row.Physics.Base64);
            IasBinary.Require(IasHash.Sha256Hex(rig) == row.Rig.Sha256 && IasHash.Sha256Hex(physics) == row.Physics.Sha256,
                "资源摘要不匹配");
            ValidateBinary(new IasPhysicsDomain(row.Id, row.Count, row.Parents, rig, physics), row.Key);
        }
        return document;
    }

    public static IReadOnlyDictionary<string, IasNativeDeclaration> Declarations(string modelModule, IReadOnlyList<IasPhysicsDomain> groups)
    {
        var namespaceId = modelModule.Split('/')[2];
        var result = new Dictionary<string, IasNativeDeclaration>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var key = IasHash.Sha256Hex(group.Rig.Concat(group.Physics).ToArray());
            result[group.Id] = new IasNativeDeclaration(group.Id, key, namespaceId + "/" + group.Id + "/" + key[..16],
                group.Count, RigNodes(group.Rig));
        }
        return result;
    }

    public static IReadOnlyList<IasRigNode> RigNodes(byte[] rig)
    {
        IasBinary.Require(rig.Length >= 180, "Rig长度不匹配");
        var count = IasBinary.ReadUInt32(rig, 160);
        var rows = new List<IasRigNode>((int)count);
        for (var index = 0; index < count; index++)
        {
            var offset = 180 + index * 132;
            IasBinary.Require(offset + 132 <= rig.Length, "Rig节点");
            var name = IasBinary.Name64(rig.AsSpan(offset, 64));
            var parent = IasBinary.ReadInt32(rig, offset + 64);
            var slot = IasBinary.ReadUInt32(rig, offset + 72);
            var rest = IasBinary.ReadFloats(rig, offset + 84, 12);
            rows.Add(new IasRigNode(name, parent, -1, (int)slot, 0, 1, rest));
        }
        return rows;
    }

    public static void ValidateBinary(IasPhysicsDomain domain, string key)
    {
        var count = domain.Count;
        var parents = domain.Parents;
        IasBinary.Require(count is >= 1 and <= 256, "节点数越界");
        IasBinary.Require(parents.Count == count && Enumerable.Range(0, count).All(index => parents[index] >= -1 && parents[index] < index), "父链无效");
        var rig = domain.Rig;
        var physics = domain.Physics;
        IasBinary.Require(rig.Length == 180 + count * 132 + 16, "Rig长度不匹配");
        IasBinary.Require(rig.AsSpan(0, 8).SequenceEqual("HD2IRG1\0"u8) && IasBinary.ReadUInt32(rig, 8) == 1
                          && IasBinary.ReadUInt32(rig, 12) == 32 && IasBinary.ReadUInt32(rig, 16) == 1
                          && IasBinary.ReadUInt32(rig, 20) == 1 && IasBinary.ReadUInt32(rig, 28) == 0
                          && IasBinary.Crc32(rig.AsSpan(32)) == IasBinary.ReadUInt32(rig, 24), "Rig头或CRC不匹配");
        var uid = IasBinary.ReadUInt64(rig, 152);
        var nodes = IasBinary.ReadUInt32(rig, 160);
        var palette = IasBinary.ReadUInt32(rig, 172);
        IasBinary.Require(uid == ulong.Parse(domain.Id, System.Globalization.NumberStyles.HexNumber) && nodes == count, "Rig身份或骨数不匹配");
        for (var index = 0; index < count; index++)
        {
            var offset = 180 + index * 132;
            IasBinary.Require(IasBinary.ReadInt32(rig, offset + 64) == parents[index]
                              && IasBinary.ReadInt32(rig, offset + 68) == -1
                              && IasBinary.ReadUInt32(rig, offset + 72) < palette
                              && IasBinary.ReadUInt32(rig, offset + 76) == (parents[index] >= 0 ? 256U : 0U)
                              && IasBinary.ReadSingle(rig, offset + 80) == 1
                              && IasBinary.ReadFloats(rig, offset + 84, 12).All(double.IsFinite), "Rig节点不匹配或非有限");
        }
        var rigNodes = RigNodes(rig);
        IasBinary.Require(rigNodes.Select(node => node.Name).Distinct(StringComparer.Ordinal).Count() == count
                          && rigNodes.Select(node => node.PaletteSlot).Distinct().Count() == count, "Rig骨名或调色板槽位重复");
        IasBinary.Require(physics.Length >= IasCompanionReader.PhysicsHeaderSize, "物理头不完整");
        var headerSize = IasBinary.ReadUInt32(physics, 12);
        var length = IasBinary.ReadUInt32(physics, 16);
        var crc = IasBinary.ReadUInt32(physics, 20);
        IasBinary.Require(physics.AsSpan(0, 8).SequenceEqual("HD2PHY1\0"u8) && IasBinary.ReadUInt32(physics, 8) == 1
                          && headerSize == IasCompanionReader.PhysicsHeaderSize && length == (uint)physics.Length
                          && IasBinary.Crc32(physics.AsSpan((int)headerSize)) == crc, "物理头、长度或CRC不匹配");
        var physicsUid = IasBinary.ReadUInt64(physics, 24);
        var fingerprint = IasBinary.ReadUInt64(physics, 32);
        var physicsPalette = IasBinary.ReadUInt32(physics, 40);
        var physicsCount = IasBinary.ReadUInt32(physics, 44);
        IasBinary.Require(physicsUid == uid && physicsPalette == palette && physicsCount == count, "物理与Rig身份不匹配");
        IasBinary.Require(fingerprint == IasCompanionReader.SkeletonFingerprint(new IasRigProfile(domain.Id, rigNodes, (int)palette, 0, "", 0, [])),
            "物理骨架指纹与Rig不匹配");
        var h9 = IasBinary.ReadUInt32(physics, 48);
        var h10 = IasBinary.ReadUInt32(physics, 52);
        var h11 = IasBinary.ReadUInt32(physics, 56);
        var h12 = IasBinary.ReadUInt32(physics, 60);
        var h13 = IasBinary.ReadUInt32(physics, 64);
        var h14 = IasBinary.ReadSingle(physics, 68);
        var h15 = IasBinary.ReadUInt32(physics, 72);
        var h16 = IasBinary.ReadUInt32(physics, 76);
        IasBinary.Require(h9 is >= 1 and <= 1024 && h10 is >= 2 and <= 8192 && h11 <= 1024 && h12 <= 1024 && h13 <= 8192
                          && (h15 & ~2015U) == 0 && float.IsFinite(h14)
                          && h14 is >= 0.004166666666666667f and <= 0.06666666666666667f && h16 == 0,
            "物理表越界或含当前引擎不支持的能力");
        var expected = (long)IasCompanionReader.PhysicsHeaderSize + h9 * 120L + h10 * 164L + h11 * 188L + h12 * 32L + h13 * 32L
                       + ((h15 & 1024) != 0 ? h10 * 8L : 0);
        IasBinary.Require(physics.Length == expected, "物理表长度不匹配");
        IasBinary.Require(IasHash.Sha256Hex(rig.Concat(physics).ToArray()) == key, "域指纹不匹配");
    }
}
