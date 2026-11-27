using System.Text.Json;

namespace HD2Mimnt.Infrastructure.ModelConversion;

public sealed record IasPose(double[] Q, double[] P);

public sealed record IasSourceBone(string Name, int Parent, uint Flags, double[] RestLocal);

public sealed record IasHandGrip(int Hand, int Side, double[] HandToGrip);

public sealed record IasRigProfile(
    string UnitId,
    IReadOnlyList<IasRigNode> TargetBones,
    int PaletteSlots,
    uint Flags,
    string NeedleHex,
    int NeedleToPalette,
    IReadOnlyList<IasHandGrip> HandGrips);

public sealed record IasRig(
    IReadOnlyList<IasSourceBone> SourceBones,
    IReadOnlyList<IasRigProfile> Profiles,
    uint Extensions,
    string Sha256);

public sealed record IasProfileNode(
    string Name,
    int Parent,
    int Source,
    int Slot,
    bool Inherit,
    double Scale,
    IasPose LocalRest,
    IasPose Rest,
    uint IasFlags);

public sealed record IasProfileGrip(int Hand, int Side, IasPose Rest, string? NativeSocket = null);

public sealed record IasProfileTable(IReadOnlyList<IasProfileNode> Nodes, IReadOnlyList<IasProfileGrip> Grips);

public sealed record IasCompanionFile(string Path, string Sha256);

public sealed record IasCompanionMember(string Archive, IReadOnlyList<IasCompanionFile> Files);

public sealed record IasCompanionMetadata(string Id, IReadOnlyList<IasCompanionMember> Members, string? Source);

public sealed record IasCompanionDescriptor(
    string Schema,
    bool ExplicitOriginalFiles,
    string Id,
    IReadOnlyList<IasCompanionMember> Members);

public sealed record IasSharedConsumer(int[] Mapping, IasPose Basis, string Slot, string Native, string Choice);

public sealed record IasSharedBody(
    string Id,
    string Canonical,
    IReadOnlyDictionary<string, IasSharedConsumer> Consumers,
    string Rig,
    string ConsumerRig,
    string? Physics,
    bool AnimationOnly,
    string? PoseFile,
    IasClothingPoseProgram? ClothingPose);

public sealed record IasCompanionGroup(
    string Id,
    bool Virtual,
    string? File,
    IasProfileTable Profile,
    string? OriginalRig,
    string? PhysicsSha256,
    string? Fingerprint,
    int PaletteSlots,
    bool AnimationOnly,
    IasClothingPoseProgram? ClothingPose,
    IasNativeDeclaration? Native = null);

public sealed record IasProjectedSource(string Name, int Parent, IasPose LocalRest, uint IasFlags, IasPose World);

public sealed record IasCompanionData(
    IasCompanionMetadata Metadata,
    string DescriptorFrom,
    IReadOnlyList<IasProjectedSource> Sources,
    IReadOnlyList<IasSourceBone> RawSources,
    IReadOnlyDictionary<string, IasRigProfile> RawProfiles,
    IReadOnlyDictionary<string, IasProfileTable> RenderProfiles,
    IReadOnlyList<IasCompanionGroup> Groups,
    IReadOnlyList<IasSharedBody> Shared,
    IReadOnlyDictionary<string, IasRig> Documents,
    IasFaceAnimation FaceAnimation,
    IReadOnlyList<IasMorphProgram> MorphPrograms,
    IReadOnlyDictionary<string, (int Bytes, string Sha256)> Files);

public sealed record IasMorphProgram(string Name, string Sha256, int Bytes);

/// <summary>上游 <c>ias_companion</c> 的原生 Rig/物理/共享身体解析。</summary>
public static class IasCompanionReader
{
    private const int RigHeaderSize = 32;
    private const int SourceSize = 120;
    private const int ProfileSize = 28;
    private const int TargetSize = 132;
    private const int GripSize = 64;
    private const int SharedHeaderSize = 148;
    private const int ReferenceSize = 136;
    private const int ConsumerSize = 68;
    private const int SelectionSize = 40;
    public const int PhysicsHeaderSize = 80;

    private static readonly string[] Parts = ["Hips", "LeftArm", "RightArm", "Torso", "Torso_Armor", "LeftLeg", "RightLeg"];

    public static IasRig ReadRig(ReadOnlySpan<byte> raw)
    {
        IasBinary.Require(raw.Length >= RigHeaderSize, "Rig header");
        var version = IasBinary.ReadUInt32(raw, 8);
        var start = IasBinary.ReadUInt32(raw, 12);
        var sourceCount = IasBinary.ReadUInt32(raw, 16);
        var profileCount = IasBinary.ReadUInt32(raw, 20);
        var crc = IasBinary.ReadUInt32(raw, 24);
        var extensions = IasBinary.ReadUInt32(raw, 28);
        IasBinary.Require(raw[..8].SequenceEqual("HD2IRG1\0"u8) && version == 1 && start == RigHeaderSize, "Rig header");
        IasBinary.Require(sourceCount is >= 1 and <= 256 && profileCount is >= 1 and <= 512
                          && (extensions & ~3U) == 0 && IasBinary.Crc32(raw[(int)start..]) == crc, "Rig counts/CRC/extensions");
        var cursor = (int)start;
        var sources = new List<IasSourceBone>((int)sourceCount);
        for (var index = 0; index < sourceCount; index++)
        {
            IasBinary.Require(cursor + SourceSize <= raw.Length, "Rig source truncated");
            var name = IasBinary.Name64(raw.Slice(cursor, 64));
            var parent = IasBinary.ReadInt32(raw, cursor + 64);
            var flags = IasBinary.ReadUInt32(raw, cursor + 68);
            var local = IasBinary.ReadFloats(raw, cursor + 72, 12);
            cursor += SourceSize;
            IasBinary.Require(parent >= -1 && parent < index && (flags & ~1U) == 0 && local.All(double.IsFinite), "source semantics");
            sources.Add(new IasSourceBone(name, parent, flags, local));
        }
        IasBinary.Require(sources.Select(source => source.Name).Distinct(StringComparer.Ordinal).Count() == sources.Count, "duplicate source name");
        var profiles = new List<IasRigProfile>((int)profileCount);
        for (var profileIndex = 0; profileIndex < profileCount; profileIndex++)
        {
            IasBinary.Require(cursor + ProfileSize <= raw.Length, "Rig profile truncated");
            var uid = IasBinary.ReadUInt64(raw, cursor);
            var count = IasBinary.ReadUInt32(raw, cursor + 8);
            var needleLength = IasBinary.ReadUInt32(raw, cursor + 12);
            var needleOffset = IasBinary.ReadInt32(raw, cursor + 16);
            var palette = IasBinary.ReadUInt32(raw, cursor + 20);
            var flags = IasBinary.ReadUInt32(raw, cursor + 24);
            cursor += ProfileSize;
            IasBinary.Require(count is >= 1 and <= 256 && needleLength is >= 16 and <= 1_048_576
                              && palette is >= 1 and <= 4096 && (flags & ~15U) == 0, "profile schema");
            var nodes = new List<IasRigNode>((int)count);
            for (var nodeIndex = 0; nodeIndex < count; nodeIndex++)
            {
                IasBinary.Require(cursor + TargetSize <= raw.Length, "Rig target truncated");
                var name = IasBinary.Name64(raw.Slice(cursor, 64));
                var parent = IasBinary.ReadInt32(raw, cursor + 64);
                var source = IasBinary.ReadInt32(raw, cursor + 68);
                var slot = IasBinary.ReadUInt32(raw, cursor + 72);
                var nodeFlags = IasBinary.ReadUInt32(raw, cursor + 76);
                var scale = IasBinary.ReadSingle(raw, cursor + 80);
                var local = IasBinary.ReadFloats(raw, cursor + 84, 12);
                cursor += TargetSize;
                IasBinary.Require(parent >= -1 && parent < nodeIndex && source >= -1 && source < sourceCount && slot < palette, "target index");
                IasBinary.Require(nodeFlags is 1057 or 288, "unreviewed target flags " + nodeFlags);
                IasBinary.Require((nodeFlags == 288 && parent >= 0) || (nodeFlags == 1057 && source >= 0), "target flag/source semantics");
                IasBinary.Require(float.IsFinite(scale) && local.All(double.IsFinite), "non-finite target");
                nodes.Add(new IasRigNode(name, parent, source, (int)slot, nodeFlags, scale, local));
            }
            IasBinary.Require(nodes.Select(node => node.Name).Distinct(StringComparer.Ordinal).Count() == nodes.Count
                              && nodes.Select(node => node.PaletteSlot).Distinct().Count() == nodes.Count, "duplicate target name/slot");
            IasBinary.Require(cursor + (int)needleLength <= raw.Length, "truncated needle");
            var needle = raw.Slice(cursor, (int)needleLength).ToArray();
            cursor += (int)needleLength;
            profiles.Add(new IasRigProfile($"{uid:x16}", nodes, (int)palette, flags,
                Convert.ToHexString(needle).ToLowerInvariant(), needleOffset, []));
        }
        if ((extensions & 1) != 0)
        {
            IasBinary.Require(cursor + 16 <= raw.Length, "grip extension truncated");
            IasBinary.Require(raw.Slice(cursor, 8).SequenceEqual("HD2GRIP1"u8)
                              && IasBinary.ReadUInt32(raw, cursor + 12) == 0, "grip extension");
            var count = IasBinary.ReadUInt32(raw, cursor + 8);
            cursor += 16;
            IasBinary.Require(count <= 1024, "grip extension");
            for (var index = 0; index < count; index++)
            {
                IasBinary.Require(cursor + GripSize <= raw.Length, "grip truncated");
                var profileIndex = IasBinary.ReadUInt32(raw, cursor);
                var hand = IasBinary.ReadUInt32(raw, cursor + 4);
                var side = IasBinary.ReadUInt32(raw, cursor + 8);
                var reserved = IasBinary.ReadUInt32(raw, cursor + 12);
                var local = IasBinary.ReadFloats(raw, cursor + 16, 12);
                cursor += GripSize;
                IasBinary.Require(profileIndex < profiles.Count && hand < profiles[(int)profileIndex].TargetBones.Count
                                  && side is 0 or 1 && reserved == 0, "grip schema");
                var profile = profiles[(int)profileIndex];
                var grips = profile.HandGrips.ToList();
                grips.Add(new IasHandGrip((int)hand, (int)side, local));
                profiles[(int)profileIndex] = profile with { HandGrips = grips };
            }
        }
        IasBinary.Require(cursor == raw.Length, "unexpected Rig trailing bytes");
        return new IasRig(sources, profiles, extensions, IasHash.Sha256Hex(raw));
    }

    public static IasProfileTable ProfileTable(IasRigProfile profile)
    {
        var world = IasAffine.Worlds(profile.TargetBones);
        var nodes = new List<IasProfileNode>(profile.TargetBones.Count);
        for (var index = 0; index < profile.TargetBones.Count; index++)
        {
            var node = profile.TargetBones[index];
            nodes.Add(new IasProfileNode(node.Name, node.Parent + 1, node.SourceIndex + 1, node.PaletteSlot,
                node.Flags == 288, node.TranslationScale, IasAffine.Pose(node.RestLocal), IasAffine.Pose(world[index]), node.Flags));
        }
        var grips = profile.HandGrips.Select(grip =>
        {
            var pose = IasAffine.Pose(grip.HandToGrip);
            return new IasProfileGrip(grip.Hand + 1, grip.Side, new IasPose(pose.Q, pose.P));
        }).ToList();
        return new IasProfileTable(nodes, grips);
    }

    /// <summary>把原始多来源 Rig 投影成单配置预览 Rig，供私有物理校验使用。</summary>
    public static byte[] PreviewRig(IasRigProfile profile)
    {
        double[] identity = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0];
        var payload = new List<byte>();
        var sourceName = IasBinary.FixedName("__HD2PB_PREVIEW_SOURCE__", 64);
        payload.AddRange(sourceName);
        payload.AddRange(BitConverter.GetBytes(-1));
        payload.AddRange(BitConverter.GetBytes(1));
        foreach (var value in identity) payload.AddRange(BitConverter.GetBytes((float)value));
        payload.AddRange(BitConverter.GetBytes(ulong.Parse(profile.UnitId, System.Globalization.NumberStyles.HexNumber)));
        payload.AddRange(BitConverter.GetBytes((uint)profile.TargetBones.Count));
        payload.AddRange(BitConverter.GetBytes(16U));
        payload.AddRange(BitConverter.GetBytes(0));
        payload.AddRange(BitConverter.GetBytes((uint)profile.PaletteSlots));
        payload.AddRange(BitConverter.GetBytes(0U));
        foreach (var node in profile.TargetBones)
        {
            payload.AddRange(IasBinary.FixedName(node.Name, 64));
            payload.AddRange(BitConverter.GetBytes(node.Parent));
            payload.AddRange(BitConverter.GetBytes(-1));
            payload.AddRange(BitConverter.GetBytes((uint)node.PaletteSlot));
            payload.AddRange(BitConverter.GetBytes(node.Parent >= 0 ? 256U : 0U));
            payload.AddRange(BitConverter.GetBytes(1.0f));
            foreach (var value in node.RestLocal) payload.AddRange(BitConverter.GetBytes((float)value));
        }
        payload.AddRange(new byte[16]);
        var header = new byte[RigHeaderSize];
        "HD2IRG1\0"u8.CopyTo(header);
        BitConverter.GetBytes(1U).CopyTo(header, 8);
        BitConverter.GetBytes((uint)RigHeaderSize).CopyTo(header, 12);
        BitConverter.GetBytes(1U).CopyTo(header, 16);
        BitConverter.GetBytes(1U).CopyTo(header, 20);
        BitConverter.GetBytes(IasBinary.Crc32(payload.ToArray())).CopyTo(header, 24);
        BitConverter.GetBytes(0U).CopyTo(header, 28);
        return header.Concat(payload).ToArray();
    }

    public static IasCompanionData Read(
        IReadOnlyDictionary<string, byte[]> members,
        IasCompanionDescriptor? descriptor,
        bool morphEnabled = true)
    {
        IasBinary.Require(members.Count > 0, "no deployed companion bytes");
        foreach (var (name, data) in members)
        {
            IasBinary.Require(!name.StartsWith('/') && !name.Contains(':')
                              && !name.Replace('\\', '/').Split('/').Contains(".."), "invalid deployed member");
            IasBinary.Require(data is not null, "invalid deployed member");
        }
        var morphPrograms = new List<IasMorphProgram>();
        if (morphEnabled)
        {
            foreach (var (name, data) in members.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                if (name.EndsWith(".hd2morph", StringComparison.OrdinalIgnoreCase))
                    morphPrograms.Add(new IasMorphProgram(name, IasHash.Sha256Hex(data), data.Length));
            }
        }
        var archived = members.ContainsKey("mod_group.json");
        IasCompanionMetadata metadata;
        var descriptorFrom = archived ? "original_mod_group" : "explicit_original_files";
        if (archived)
        {
            metadata = ParseMetadata(members["mod_group.json"]);
        }
        else
        {
            IasBinary.Require(descriptor is not null && descriptor.Schema == "HD2CLExplicitOriginalSelection1"
                              && descriptor.ExplicitOriginalFiles, "normalized descriptor requires a verified IAS manager selection or deployment");
            metadata = new IasCompanionMetadata(descriptor!.Id, descriptor.Members, "explicit_original_selection");
        }
        var listed = new Dictionary<string, IasCompanionFile>(StringComparer.Ordinal);
        foreach (var member in metadata.Members)
        foreach (var file in member.Files)
        {
            IasBinary.Require(listed.TryAdd(file.Path, file), "duplicate group file");
            IasBinary.Require(members.TryGetValue(file.Path, out var raw)
                              && IasHash.Sha256Hex(raw).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase),
                "deployed companion digest differs: " + file.Path);
        }
        var documents = new Dictionary<string, IasRig>(StringComparer.Ordinal);
        var profiles = new Dictionary<string, IasRigProfile>(StringComparer.Ordinal);
        IReadOnlyList<IasSourceBone>? source = null;
        var rigPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, raw) in members)
        {
            if (!name.EndsWith(".rigbin", StringComparison.OrdinalIgnoreCase) || !raw.AsSpan().StartsWith("HD2IRG1\0"u8)) continue;
            var document = ReadRig(raw);
            documents[name] = document;
            IasBinary.Require(source is null || SourcesEqual(source, document.SourceBones), "head/body source tables differ");
            source = document.SourceBones;
            foreach (var profile in document.Profiles)
            {
                IasBinary.Require(!profiles.TryGetValue(profile.UnitId, out var existing) || ProfilesEqual(existing, profile), "conflicting profile");
                profiles[profile.UnitId] = profile;
                rigPaths[profile.UnitId] = name;
            }
        }
        IasBinary.Require(source is not null, "no usable source table");
        var shared = members.Where(entry => entry.Key.EndsWith(".hd2shared", StringComparison.OrdinalIgnoreCase))
            .Select(entry => ReadShared(entry.Value, members, documents)).ToList();
        var poseFiles = shared.Where(item => item.PoseFile is not null).Select(item => item.PoseFile!).ToHashSet(StringComparer.Ordinal);
        var virtualIds = shared.Select(item => item.Canonical).ToHashSet(StringComparer.Ordinal);
        var consumerIds = shared.SelectMany(item => item.Consumers.Keys).ToHashSet(StringComparer.Ordinal);
        var groups = new List<IasCompanionGroup>();
        foreach (var (name, raw) in members)
        {
            if (!name.EndsWith(".hd2phys", StringComparison.OrdinalIgnoreCase)) continue;
            IasBinary.Require(raw.Length >= PhysicsHeaderSize, "物理头不完整");
            var uid = $"{IasBinary.ReadUInt64(raw, 24):x16}";
            IasBinary.Require(profiles.ContainsKey(uid) && !consumerIds.Contains(uid), "physical profile identity");
            var profile = profiles[uid];
            IasBinary.Require(raw[..8].SequenceEqual("HD2PHY1\0"u8) && IasBinary.ReadUInt32(raw, 8) == 1
                              && IasBinary.ReadUInt32(raw, 12) == PhysicsHeaderSize && IasBinary.ReadUInt32(raw, 16) == (uint)raw.Length
                              && IasBinary.Crc32(raw[PhysicsHeaderSize..]) == IasBinary.ReadUInt32(raw, 20), "physical header/CRC");
            IasBinary.Require(IasBinary.ReadUInt64(raw, 32) == SkeletonFingerprint(profile)
                              && IasBinary.ReadUInt32(raw, 40) == (uint)profile.PaletteSlots
                              && IasBinary.ReadUInt32(raw, 44) == (uint)profile.TargetBones.Count, "physical fingerprint/count mismatch");
            var group = new IasCompanionGroup(uid, virtualIds.Contains(uid), name, ProfileTable(profile), rigPaths[uid],
                IasHash.Sha256Hex(raw), $"{IasBinary.ReadUInt64(raw, 32):x16}", profile.PaletteSlots, false, null);
            groups.Add(group);
            var sharedGroup = shared.FirstOrDefault(item => item.Canonical == uid);
            if (sharedGroup?.ClothingPose is not null)
                groups[^1] = group with { ClothingPose = sharedGroup.ClothingPose };
        }
        foreach (var sharedGroup in shared)
        {
            if (!sharedGroup.AnimationOnly) continue;
            var uid = sharedGroup.Canonical;
            IasBinary.Require(groups.All(group => group.Id != uid), "pose-only shared body also contains physical data");
            groups.Add(new IasCompanionGroup(uid, true, null, ProfileTable(profiles[uid]), rigPaths[uid], null, null,
                profiles[uid].PaletteSlots, true, sharedGroup.ClothingPose));
        }
        foreach (var name in members.Keys.Where(name => name.EndsWith(".hd2pose", StringComparison.OrdinalIgnoreCase)
                                                       && !poseFiles.Contains(name)).OrderBy(name => name, StringComparer.Ordinal))
        {
            var uid = IasClothingPose.ReadUnitId(members[name]);
            IasBinary.Require(profiles.ContainsKey(uid) && !virtualIds.Contains(uid) && !consumerIds.Contains(uid),
                "independent clothing pose has no standalone Rig: " + name);
            var candidates = groups.Where(group => group.Id == uid).ToList();
            IasBinary.Require(candidates.Count <= 1, "ambiguous independent clothing pose domain: " + uid);
            var program = IasClothingPose.Read(members[name], profiles[uid]);
            if (candidates.Count == 1)
            {
                IasBinary.Require(candidates[0].ClothingPose is null, "duplicate clothing pose for Unit: " + uid);
                var index = groups.IndexOf(candidates[0]);
                groups[index] = candidates[0] with { ClothingPose = program };
            }
            else
            {
                groups.Add(new IasCompanionGroup(uid, false, null, ProfileTable(profiles[uid]), rigPaths[uid], null, null,
                    profiles[uid].PaletteSlots, true, program));
            }
        }
        var sourceNodes = source.Select(bone => new IasRigNode(bone.Name, bone.Parent, 0, 0, bone.Flags, 1, bone.RestLocal)).ToList();
        var sourceWorld = IasAffine.Worlds(sourceNodes);
        var projectedSources = new List<IasProjectedSource>(source.Count);
        for (var index = 0; index < source.Count; index++)
        {
            var bone = source[index];
            projectedSources.Add(new IasProjectedSource(bone.Name, bone.Parent + 1,
                IasAffine.Pose(bone.RestLocal), bone.Flags, IasAffine.Pose(sourceWorld[index])));
        }
        var face = IasFaceAnimationAudit.Audit(members, source, profiles, morphEnabled);
        var files = members.ToDictionary(entry => entry.Key, entry => (entry.Value.Length, IasHash.Sha256Hex(entry.Value)), StringComparer.Ordinal);
        return new IasCompanionData(metadata, descriptorFrom, projectedSources, source, profiles,
            profiles.Where(pair => !virtualIds.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => ProfileTable(pair.Value), StringComparer.Ordinal),
            groups, shared, documents, face, morphPrograms, files);
    }

    private static IasCompanionMetadata ParseMetadata(byte[] raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
        if (!root.TryGetProperty("members", out var members) || members.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("模型数据检查：missing normalized deployed group descriptor");
        IasBinary.Require(!string.IsNullOrEmpty(id), "missing normalized deployed group descriptor");
        var result = new List<IasCompanionMember>();
        foreach (var member in members.EnumerateArray())
        {
            var archive = member.TryGetProperty("archive", out var archiveValue) ? archiveValue.GetString() ?? string.Empty : string.Empty;
            var files = new List<IasCompanionFile>();
            if (member.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
                foreach (var file in fileArray.EnumerateArray())
                    files.Add(new IasCompanionFile(file.GetProperty("path").GetString() ?? string.Empty,
                        file.GetProperty("sha256").GetString() ?? string.Empty));
            result.Add(new IasCompanionMember(archive, files));
        }
        return new IasCompanionMetadata(id!, result, "original_mod_group");
    }

    public static IasSharedBody ReadShared(byte[] raw, IReadOnlyDictionary<string, byte[]> members, IReadOnlyDictionary<string, IasRig> documents)
    {
        IasBinary.Require(raw.Length >= SharedHeaderSize, "shared header");
        var version = IasBinary.ReadUInt32(raw, 8);
        var size = IasBinary.ReadUInt32(raw, 12);
        var length = IasBinary.ReadUInt32(raw, 16);
        var crc = IasBinary.ReadUInt32(raw, 20);
        var enabled = IasBinary.ReadUInt32(raw, 24);
        var flags = IasBinary.ReadUInt32(raw, 28);
        IasBinary.Require(raw[..8].SequenceEqual("HD2SHR1\0"u8) && size == SharedHeaderSize && length == (uint)raw.Length && enabled == 1
                          && ((version == 2 && flags == 0) || (version == 3 && flags is 2 or 3)), "shared header/capability");
        var revision = raw.AsSpan(112, 32);
        var archive = IasBinary.ReadUInt64(raw, 96);
        var canonical = IasBinary.ReadUInt64(raw, 104);
        var hashInput = raw.AsSpan(32, 64).ToArray()
            .Concat(BitConverter.GetBytes(archive)).Concat(BitConverter.GetBytes(canonical))
            .Concat(raw[SharedHeaderSize..]).ToArray();
        IasBinary.Require(IasBinary.Crc32(raw[SharedHeaderSize..]) == crc && IasHash.Sha256Hex(hashInput).Equals(Convert.ToHexString(revision).ToLowerInvariant(), StringComparison.Ordinal), "shared CRC/revision");
        var cursor = SharedHeaderSize;
        var refs = new string?[4];
        var referenceCount = version == 3 ? 4 : 3;
        for (var index = 0; index < referenceCount; index++)
        {
            IasBinary.Require(cursor + ReferenceSize <= raw.Length, "shared reference truncated");
            var reference = raw.AsSpan(cursor, ReferenceSize);
            var name = IasBinary.Name(reference, 96);
            var bytes = IasBinary.ReadUInt64(raw, cursor + 96);
            var digest = Convert.ToHexString(raw.AsSpan(cursor + 104, 32)).ToLowerInvariant();
            cursor += ReferenceSize;
            if (version == 3 && flags == 2 && index == 1)
            {
                IasBinary.Require(reference.IndexOfAnyExcept((byte)0) < 0, "pose-only shared body declares a physical reference");
                refs[index] = null;
                continue;
            }
            IasBinary.Require(name.Length > 0 && !name.Contains('/') && !name.Contains('\\'), "shared reference path");
            var key = "HD2IndependentRig/" + name;
            IasBinary.Require(members.TryGetValue(key, out var content) && (ulong)content.Length == bytes
                              && IasHash.Sha256Hex(content).Equals(digest, StringComparison.Ordinal), "shared reference changed: " + key);
            refs[index] = key;
        }
        var canonicalDoc = documents.TryGetValue(refs[0]!, out var canonicalRig) ? canonicalRig : throw new InvalidDataException("模型数据检查：shared canonical missing");
        var consumersDoc = documents.TryGetValue(refs[2]!, out var consumersRig) ? consumersRig : throw new InvalidDataException("模型数据检查：shared consumers missing");
        IasBinary.Require(canonicalDoc.Profiles.Count == 1, "canonical profile count");
        var cp = canonicalDoc.Profiles[0];
        IasBinary.Require(cp.UnitId == $"{canonical:x16}" && SourcesEqual(canonicalDoc.SourceBones, consumersDoc.SourceBones), "shared canonical/source mismatch");
        var consumerProfiles = consumersDoc.Profiles.ToDictionary(profile => profile.UnitId, StringComparer.Ordinal);
        var count = IasBinary.ReadUInt32(raw, 144);
        IasBinary.Require(count is >= 1 and <= 512 && count == consumerProfiles.Count, "shared consumer count");
        var canonicalWorld = IasAffine.Worlds(cp.TargetBones);
        var consumers = new Dictionary<string, IasSharedConsumer>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            IasBinary.Require(cursor + ConsumerSize + SelectionSize <= raw.Length, "shared consumer truncated");
            var uid = IasBinary.ReadUInt64(raw, cursor);
            var part = IasBinary.ReadUInt32(raw, cursor + 8);
            var nodeCount = IasBinary.ReadUInt32(raw, cursor + 12);
            var reserved = IasBinary.ReadUInt32(raw, cursor + 16);
            var basis = IasBinary.ReadFloats(raw, cursor + 20, 12);
            cursor += ConsumerSize;
            var consumerPose = IasClothingPose.RigidPose(basis, "shared consumer basis");
            var native = IasBinary.ReadUInt64(raw, cursor);
            var choice = raw.AsSpan(cursor + 8, 32).ToArray();
            cursor += SelectionSize;
            var key = $"{uid:x16}";
            IasBinary.Require(consumerProfiles.TryGetValue(key, out var profile) && !consumers.ContainsKey(key)
                              && part < Parts.Length && reserved == 0, "shared consumer identity");
            IasBinary.Require(nodeCount == profile!.TargetBones.Count, "shared mapping count");
            IasBinary.Require(cursor + 4 * (int)nodeCount <= raw.Length, "shared mapping truncated");
            var mapping = new int[nodeCount];
            for (var nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
            {
                var rawMapping = IasBinary.ReadUInt32(raw, cursor + 4 * nodeIndex);
                mapping[nodeIndex] = rawMapping == uint.MaxValue ? -1 : checked((int)rawMapping);
            }
            cursor += 4 * (int)nodeCount;
            var profileWorld = IasAffine.Worlds(profile.TargetBones);
            var used = new HashSet<int>();
            for (var nodeIndex = 0; nodeIndex < mapping.Length; nodeIndex++)
            {
                var mapped = mapping[nodeIndex];
                if (mapped == -1)
                {
                    mapping[nodeIndex] = 0;
                    continue;
                }
                IasBinary.Require(mapped >= 0 && mapped < canonicalWorld.Length && used.Add(mapped), "shared mapping index");
                IasBinary.Require(profile.TargetBones[nodeIndex].Name == cp.TargetBones[mapped].Name, "shared mapped name");
                var transformed = IasAffine.Product(basis, canonicalWorld[mapped]);
                IasBinary.Require(transformed.Zip(profileWorld[nodeIndex]).Max(pair => Math.Abs(pair.First - pair.Second)) < 0.0002, "shared world Rest");
                mapping[nodeIndex] = mapped + 1;
            }
            consumers[key] = new IasSharedConsumer(mapping, consumerPose, Parts[(int)part], $"{native:x16}",
                Convert.ToHexString(choice).ToLowerInvariant());
        }
        IasBinary.Require(cursor == raw.Length, "shared trailing data");
        var result = new IasSharedBody(Convert.ToHexString(revision).ToLowerInvariant(), $"{canonical:x16}", consumers,
            refs[0]!, refs[2]!, refs[1], refs[1] is null, null, null);
        if (version == 3)
        {
            var poseKey = refs[3]!;
            result = result with { PoseFile = poseKey, ClothingPose = IasClothingPose.Read(members[poseKey], cp) };
        }
        return result;
    }

    internal static ulong SkeletonFingerprint(IasRigProfile profile)
    {
        var raw = new List<byte>();
        raw.AddRange("HD2SKEL1\0"u8.ToArray());
        raw.AddRange(BitConverter.GetBytes((uint)profile.PaletteSlots));
        raw.AddRange(BitConverter.GetBytes((uint)profile.TargetBones.Count));
        foreach (var node in profile.TargetBones.OrderBy(node => node.PaletteSlot).ThenBy(node => node.Name, StringComparer.Ordinal))
        {
            var parent = node.Parent >= 0 ? profile.TargetBones[node.Parent].PaletteSlot : -1;
            raw.AddRange(BitConverter.GetBytes(node.PaletteSlot));
            raw.AddRange(BitConverter.GetBytes(parent));
            raw.AddRange(System.Text.Encoding.UTF8.GetBytes(node.Name));
            raw.Add(0);
        }
        var value = 14695981039346656037UL;
        foreach (var item in raw)
        {
            value ^= item;
            value *= 1099511628211UL;
        }
        return value;
    }

    private static bool SourcesEqual(IReadOnlyList<IasSourceBone> left, IReadOnlyList<IasSourceBone> right)
        => left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name && pair.First.Parent == pair.Second.Parent
            && pair.First.Flags == pair.Second.Flags && pair.First.RestLocal.SequenceEqual(pair.Second.RestLocal));

    private static bool ProfilesEqual(IasRigProfile left, IasRigProfile right)
        => left.UnitId == right.UnitId && left.PaletteSlots == right.PaletteSlots && left.Flags == right.Flags
           && left.NeedleHex == right.NeedleHex && left.NeedleToPalette == right.NeedleToPalette
           && left.TargetBones.Count == right.TargetBones.Count
           && left.TargetBones.Zip(right.TargetBones).All(pair => pair.First == pair.Second);
}
