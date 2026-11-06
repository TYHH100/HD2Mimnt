using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Gmod.Physics;

/// <summary>
/// 严格 little-endian HD2PHY1 v1 编解码器。线格式与 HD2-PhysBoneTool 的
/// <c>physbone/core/format_v1.py::build</c> 一致（80 字节头，无隐式对齐填充）。
/// </summary>
public static class Hd2PhysCodec
{
    private const int HeaderSize = 80;
    private const int ChainSize = 120;
    private const int JointSize = 164;
    private const int ColliderSize = 188;
    private const int ChainLinkSize = 32;
    private const int LinkCollisionSize = 32;
    private const int JointFilterSize = 8;
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private const uint CapReferenceDirection = 1u << 0;
    private const uint CapReferenceFrame = 1u << 1;
    private const uint CapDynamicColliders = 1u << 2;
    private const uint CapChainGroupLinks = 1u << 3;
    private const uint CapLinkCollisions = 1u << 4;
    private const uint CapRootSwing = 1u << 6;
    private const uint CapLocalBend = 1u << 7;
    private const uint CapStripLinks = 1u << 8;
    private const uint CapDualEndpointCapsules = 1u << 9;
    private const uint CapCollisionFilters = 1u << 10;

    private const uint ChainFlagRootSwing = 1u << 2;
    private const uint ChainFlagLocalBend = 1u << 3;
    private const uint LinkFlagStrip = 1u << 0;
    private const uint ColliderFlagInside = 1u << 0;
    private const uint ColliderFlagDynamic = 1u << 1;
    private const uint ColliderFlagDualEndpoint = 1u << 2;

    private const float WirePi = 3.14159274f; // float-rounded pi, matching struct.pack('<f', math.pi)
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Vector3 DefaultGravity = new(0, 0, -9.81f);
    private static readonly Vector3 DefaultDirection = new(0, 1, 0);
    private static readonly Vector4 IdentityFrame = new(0, 0, 0, 1);

    public static (byte[] Bytes, Hd2PhysSummary Summary) Write(Hd2PhysDocument document,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document is null) throw new Hd2PhysFormatException("HD2PHY1 document is null.");
        Require(document.UnitId != 0, "Unit ID must be nonzero.");
        Require(document.PaletteSlots is >= 1 and <= 4096, "Palette slots must be 1..4096.");
        Require(float.IsFinite(document.FixedDt) && document.FixedDt is >= 1f / 240f and <= 1f / 15f,
            "Fixed dt must be finite and within 1/240..1/15.");
        Require(document.Bones is { Count: >= 1 and <= 256 }, "Bone count must be 1..256.");
        Require(document.Chains is { Count: >= 1 and <= 1024 }, "Chain count must be 1..1024.");

        var boneNames = new HashSet<string>(StringComparer.Ordinal);
        var boneSlots = new Dictionary<int, string>();
        var bones = new Dictionary<string, Hd2PhysBone>(StringComparer.Ordinal);
        foreach (var bone in document.Bones)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Name64(bone.Name, "bone", allowEmpty: false);
            Require(boneNames.Add(bone.Name), $"Duplicate physics bone name: {bone.Name}");
            Require(bone.PaletteSlot >= 0 && bone.PaletteSlot < document.PaletteSlots, "Bone palette slot is out of range.");
            Require(bone.ParentSlot >= -1 && bone.ParentSlot < document.PaletteSlots, "Bone parent slot is out of range.");
            Require(Finite(bone.RestHead), "Bone rest head contains non-finite values.");
            if (bone.RestBasis is { } basis)
            {
                Require(basis.Length == 9 && basis.All(float.IsFinite), "Bone rest basis must contain nine finite values.");
            }
            Require(boneSlots.TryAdd(bone.PaletteSlot, bone.Name), "Duplicate physics bone palette slot.");
            bones.Add(bone.Name, bone);
        }
        foreach (var bone in document.Bones)
            Require(bone.ParentSlot < 0 || boneSlots.ContainsKey(bone.ParentSlot), "Bone parent slot is not exported.");

        // Chains are serialized in strict UTF-8 byte order; wire consumers rely on it.
        var chains = document.Chains.OrderBy(chain => chain.Name, Utf8ByteComparer.Instance).ToArray();
        var chainNames = new HashSet<string>(StringComparer.Ordinal);
        var chainIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var chain in chains)
        {
            Name64(chain.Name, "chain");
            Require(chainNames.Add(chain.Name), $"Duplicate physics chain name: {chain.Name}");
            Require(chain.Joints is { Count: >= 2 and <= 64 }, $"Chain {chain.Name} needs 2..64 joints including its fixed driver.");
            Require(Finite(chain.Stiffness, 0, 1000) && Finite(chain.Damping, 0, 1), "Chain stiffness/damping is out of range.");
            Require(chain.GroupMask != 0, "Chain group mask cannot be zero.");
            chainIndex[chain.Name] = chainIndex.Count;
        }

        using var payload = new MemoryStream();
        var writer = new Writer(payload);
        var allJoints = new List<JointRecord>();
        var drivers = new HashSet<string>(StringComparer.Ordinal);
        var simulated = new HashSet<string>(StringComparer.Ordinal);
        uint capabilities = 0;

        foreach (var chain in chains)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gravity = chain.Gravity ?? DefaultGravity;
            var wind = chain.Wind ?? Vector3.Zero;
            Require(Finite(gravity) && Finite(wind), "Chain gravity/wind is out of range.");
            if (chain.CollisionMask == 0) capabilities |= CapCollisionFilters;
            if (chain.RootSwing) capabilities |= CapRootSwing;
            if (chain.LocalBend) capabilities |= CapLocalBend;
            var chainFlags = (chain.RootSwing ? ChainFlagRootSwing : 0u) | (chain.LocalBend ? ChainFlagLocalBend : 0u);
            writer.Name(chain.Name);
            writer.U32((uint)allJoints.Count);
            writer.U32((uint)chain.Joints.Count);
            writer.F32(chain.Stiffness);
            writer.F32(chain.Damping);
            writer.Vec3(gravity);
            writer.Vec3(wind);
            writer.U32(chain.GroupMask);
            writer.U32(chain.CollisionMask);
            writer.U32(chainFlags);
            writer.U32(0);

            Hd2PhysBone? previous = null;
            for (var localIndex = 0; localIndex < chain.Joints.Count; localIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var joint = chain.Joints[localIndex];
                if (joint is null || !bones.TryGetValue(joint.Bone, out var bone))
                    throw new Hd2PhysFormatException($"Chain {chain.Name} references an unexported bone: {joint?.Bone}");
                if (previous is not null)
                {
                    Require(bone.ParentSlot == previous.PaletteSlot,
                        $"Chain {chain.Name} joint {localIndex} does not follow a direct parent-child path.");
                    Require(Vector3.Distance(bone.RestHead, previous.RestHead) >= 1e-6f,
                        $"Chain {chain.Name} joint {localIndex} has a zero-length rest segment.");
                }
                var isSimulated = localIndex > 0 || chain.RootSwing;
                if (isSimulated)
                    Require(simulated.Add(joint.Bone), $"Bone {joint.Bone} is simulated by two physics chains.");
                else
                    drivers.Add(joint.Bone);

                var radius = FiniteValue(joint.Radius, 0, 10, $"{chain.Name}[{localIndex}].radius");
                var limitAngle = FiniteValue(joint.LimitAngle, 0, WirePi, $"{chain.Name}[{localIndex}].limit_angle");
                var limitAngle2 = FiniteValue(joint.LimitAngle2 ?? limitAngle, 0, WirePi, $"{chain.Name}[{localIndex}].limit_angle2");
                var limitAxis = joint.LimitAxis ?? new Vector3(1, 0, 0);
                Require(Finite(limitAxis), "Joint limit axis contains non-finite values.");
                if (joint.LimitKind is Hd2PhysLimitKind.Hinge or Hd2PhysLimitKind.Spherical)
                    Require(limitAxis.Length() >= 1e-6f, "Joint limit axis cannot be zero.");
                var direction = Normalize(joint.ReferenceDirection ?? DefaultDirection, "reference direction");
                var frame = NormalizeQuaternion(joint.ReferenceFrame ?? IdentityFrame, "reference frame");
                var mode = joint.ReferenceMode;
                Require(localIndex != 0 || mode == Hd2PhysReferenceMode.AnimatedSegment,
                    "The fixed driver joint only supports ANIMATED_SEGMENT.");
                Require(joint.LimitKind != Hd2PhysLimitKind.None || mode == Hd2PhysReferenceMode.AnimatedSegment,
                    "A joint without an angle limit cannot declare an explicit reference direction.");
                if (mode is Hd2PhysReferenceMode.ParentRest or Hd2PhysReferenceMode.ExplicitLocal)
                    capabilities |= CapReferenceDirection;
                else if (mode == Hd2PhysReferenceMode.ExplicitFrame)
                    capabilities |= CapReferenceFrame;
                var stiffnessScale = FiniteValue(joint.StiffnessScale, 0, 100, "stiffness_scale");
                var dampingScale = FiniteValue(joint.DampingScale, 0, 100, "damping_scale");

                uint nodeGroup = 0, nodeMask = 0;
                if (joint.OverrideCollisionFilter)
                {
                    Require(localIndex > 0 && joint.GroupMask != 0,
                        "Only simulated joints may override collision filters, with a nonzero group.");
                    nodeGroup = joint.GroupMask;
                    nodeMask = joint.CollisionMask;
                    capabilities |= CapCollisionFilters;
                }

                // The writer emits chain records first, then joint records. Accumulate joint bytes separately.
                allJoints.Add(new JointRecord(bone, radius, (uint)joint.LimitKind, limitAngle,
                    limitAngle2, limitAxis, stiffnessScale, dampingScale, direction, frame, (byte)mode,
                    nodeGroup, nodeMask));
                previous = bone;
            }
        }
        Require(allJoints.Count <= 8192, "Joint count exceeds 8192.");
        Require(!drivers.Overlaps(simulated), "A simulated bone cannot drive another physics chain.");

        // Chain records were written inline above; joint records follow in one contiguous block.
        // Rebuild the payload so chain and joint blocks stay in the wire-required order.
        var chainBlock = payload.ToArray();
        using var jointStream = new MemoryStream();
        var jointWriter = new Writer(jointStream);
        foreach (var joint in allJoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            jointWriter.Name(joint.Bone.Name);
            jointWriter.U64(Fnv1A64(StrictUtf8.GetBytes(joint.Bone.Name)));
            jointWriter.U32((uint)joint.Bone.PaletteSlot);
            jointWriter.F32(joint.Radius);
            jointWriter.U32(joint.LimitKind);
            jointWriter.F32(joint.LimitAngle);
            jointWriter.F32(joint.LimitAngle2);
            jointWriter.Vec3(joint.LimitAxis);
            jointWriter.F32(joint.StiffnessScale);
            jointWriter.F32(joint.DampingScale);
            jointWriter.Vec3(joint.Bone.RestHead);
            jointWriter.U32(0);
            jointWriter.U8(joint.Mode);
            jointWriter.U8(0); // reference_flags must be zero in this wire version
            jointWriter.U16(0);
            jointWriter.Vec3(joint.Direction);
            jointWriter.Quat(joint.Frame);
            jointWriter.U32(0);
        }

        var colliders = (document.Colliders ?? []).OrderBy(collider => collider.Name, StringComparer.Ordinal).ToArray();
        using var colliderStream = new MemoryStream();
        var colliderWriter = new Writer(colliderStream);
        var colliderNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var collider in colliders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Name64(collider.Name, "collider");
            Require(colliderNames.Add(collider.Name), $"Duplicate collider name: {collider.Name}");
            Require(collider.GroupMask != 0, "Collider group mask cannot be zero.");
            if (collider.CollisionMask == 0) capabilities |= CapCollisionFilters;
            if (collider.Dynamic) capabilities |= CapDynamicColliders;
            if (collider.BoneB.Length != 0) capabilities |= CapDualEndpointCapsules;
            var a = collider.A ?? Vector3.Zero;
            var b = collider.B ?? new Vector3(0, 0, 1);
            var radius = FiniteValue(collider.Radius, 0, 10, "collider radius");
            Require(Finite(a) && Finite(b), "Collider geometry contains non-finite values.");
            Require(collider.Dynamic == simulated.Contains(collider.Bone),
                "Collider dynamic flag must match its attachment bone category.");
            Require(!collider.Dynamic || collider.Kind != Hd2PhysColliderKind.Plane, "Dynamic planes are unsupported.");
            Require(!collider.Dynamic || !collider.Inside, "Dynamic colliders cannot use inside.");
            int slot = -1, slotB = -1;
            ulong boneHash = 0;
            if (collider.Bone.Length != 0)
            {
                Name64(collider.Bone, "collider.bone");
                Require(bones.TryGetValue(collider.Bone, out var attached), "Collider references an unexported bone.");
                slot = attached!.PaletteSlot;
                boneHash = Fnv1A64(StrictUtf8.GetBytes(collider.Bone));
            }
            else
            {
                Require(!collider.Dynamic, "Unit-root colliders cannot be dynamic.");
            }
            if (collider.BoneB.Length != 0)
            {
                Name64(collider.BoneB, "collider.bone_b");
                Require(collider.Kind == Hd2PhysColliderKind.Capsule && !collider.Dynamic && collider.Bone.Length != 0,
                    "Dual-endpoint capsules require two fixed bones.");
                Require(bones.TryGetValue(collider.BoneB, out var end) && !simulated.Contains(collider.BoneB),
                    "Dual-endpoint capsule end must be a fixed exported bone.");
                slotB = end!.PaletteSlot;
            }
            uint flags = (collider.Inside ? ColliderFlagInside : 0u)
                | (collider.Dynamic ? ColliderFlagDynamic : 0u)
                | (collider.BoneB.Length != 0 ? ColliderFlagDualEndpoint : 0u);
            colliderWriter.Name(collider.Name);
            colliderWriter.Name(collider.Bone);
            colliderWriter.U64(boneHash);
            colliderWriter.I32(slot);
            colliderWriter.U32((uint)collider.Kind);
            colliderWriter.U32(collider.GroupMask);
            colliderWriter.U32(collider.CollisionMask);
            colliderWriter.U32(flags);
            colliderWriter.Vec3(a);
            colliderWriter.Vec3(b);
            colliderWriter.F32(radius);
            colliderWriter.I32(slotB + 1);
        }

        var links = (document.ChainLinks ?? []).OrderBy(link => link.Name, Utf8ByteComparer.Instance).ToArray();
        using var linkStream = new MemoryStream();
        var linkWriter = new Writer(linkStream);
        using var collisionStream = new MemoryStream();
        var collisionWriter = new Writer(collisionStream);
        var linkNames = new HashSet<string>(StringComparer.Ordinal);
        var linkPairs = new HashSet<(int, int)>();
        var linkCollisionCount = 0;
        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Name64(link.Name, "chain link");
            Require(linkNames.Add(link.Name), $"Duplicate chain link name: {link.Name}");
            if (!chainIndex.TryGetValue(link.ChainA, out var a) || !chainIndex.TryGetValue(link.ChainB, out var b))
                throw new Hd2PhysFormatException("Chain link references an unknown chain.");
            Require(a != b, "A chain link must connect two different chains.");
            var pair = (Math.Min(a, b), Math.Max(a, b));
            Require(linkPairs.Add(pair), "Duplicate or reversed chain link pair.");
            var coupling = FiniteValue(link.Coupling, 0, 1, "chain link coupling");
            var correction = FiniteValue(link.MaxCorrection, 0, 10, "chain link max_correction");
            Require(coupling > 0 && correction > 0, "Chain link coupling/correction must be positive.");
            var children = (link.Collisions ?? []).OrderBy(child => child.LocalJointIndex).ToArray();
            if (link.Strip)
            {
                capabilities |= CapStripLinks;
                Require(chains[a].Joints.Count == chains[b].Joints.Count, "Strip links require equal chain lengths.");
            }
            if (children.Length != 0)
                Require(chains[a].Joints.Count == chains[b].Joints.Count, "Link collisions require equal chain lengths.");
            var seen = new HashSet<int>();
            foreach (var child in children)
            {
                Require(child.LocalJointIndex >= 1 && child.LocalJointIndex < chains[a].Joints.Count && seen.Add(child.LocalJointIndex),
                    "Link collision indices must be contiguous and start at 1.");
                Require(child.GroupMask != 0 && child.CollisionMask != 0, "Link collision masks cannot be zero.");
                var childRadius = FiniteValue(child.Radius, 0, 10, "link collision radius");
                Require(childRadius > 0, "Link collision radius must be positive.");
            }
            if (children.Length != 0)
                Require(seen.SetEquals(Enumerable.Range(1, chains[a].Joints.Count - 1)),
                    "Link collisions must cover every simulated joint.");
            var linkFlags = link.Strip ? LinkFlagStrip : 0u;
            linkWriter.U32((uint)pair.Item1);
            linkWriter.U32((uint)pair.Item2);
            linkWriter.F32(coupling);
            linkWriter.F32(correction);
            linkWriter.U32(linkFlags);
            linkWriter.U32((uint)linkCollisionCount);
            linkWriter.U32((uint)children.Length);
            linkWriter.U32(0);
            foreach (var child in children)
            {
                collisionWriter.U32((uint)(links.Length == 0 ? 0 : Array.IndexOf(links, link)));
                collisionWriter.U32((uint)child.LocalJointIndex);
                collisionWriter.U32(child.GroupMask);
                collisionWriter.F32(child.Radius);
                collisionWriter.U32(child.CollisionMask);
                collisionWriter.U32(0);
                collisionWriter.U32(0);
                collisionWriter.U32(0);
            }
            linkCollisionCount += children.Length;
        }
        Require(linkCollisionCount <= 8192, "Link collision count exceeds 8192.");
        if (links.Length != 0) capabilities |= CapChainGroupLinks;
        if (linkCollisionCount != 0) capabilities |= CapLinkCollisions;

        using var full = new MemoryStream();
        full.Write(chainBlock);
        full.Write(jointStream.ToArray());
        full.Write(colliderStream.ToArray());
        full.Write(linkStream.ToArray());
        full.Write(collisionStream.ToArray());
        if ((capabilities & CapCollisionFilters) != 0)
        {
            var tail = new byte[allJoints.Count * JointFilterSize];
            for (var i = 0; i < allJoints.Count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(i * JointFilterSize), allJoints[i].GroupMask);
                BinaryPrimitives.WriteUInt32LittleEndian(tail.AsSpan(i * JointFilterSize + 4), allJoints[i].CollisionMask);
            }
            full.Write(tail);
        }
        var payloadBytes = full.ToArray();
        var crc = Crc32.Compute(payloadBytes, cancellationToken);
        var fingerprint = SkeletonFingerprint(document.PaletteSlots, document.Bones);
        var fileSize = HeaderSize + payloadBytes.Length;
        var result = new byte[fileSize];
        var header = new Writer(new MemoryStream(result, writable: true));
        header.Bytes("HD2PHY1\0"u8);
        header.U32(1);
        header.U32(HeaderSize);
        header.U32((uint)fileSize);
        header.U32(crc);
        header.U64(document.UnitId);
        header.U64(fingerprint);
        header.U32(document.PaletteSlots);
        header.U32((uint)document.Bones.Count);
        header.U32((uint)chains.Length);
        header.U32((uint)allJoints.Count);
        header.U32((uint)colliders.Length);
        header.U32((uint)links.Length);
        header.U32((uint)linkCollisionCount);
        header.F32(document.FixedDt);
        header.U32(capabilities);
        header.U32(0);
        payloadBytes.CopyTo(result, HeaderSize);

        var summary = new Hd2PhysSummary(document.UnitId, fingerprint, document.PaletteSlots,
            document.Bones.Count, chains.Length, allJoints.Count, colliders.Length, links.Length,
            linkCollisionCount, capabilities, crc, fileSize);
        cancellationToken.ThrowIfCancellationRequested();
        return (result, summary);
    }

    /// <summary>严格读取并检查线格式；返回用于校验的 inspection view（作者名称等不进入 wire 的字段不还原）。</summary>
    public static Hd2PhysInspection Read(ReadOnlySpan<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new Reader(data);
        Require(reader.Take(8).SequenceEqual("HD2PHY1\0"u8), "Invalid HD2PHY1 magic.");
        Require(reader.U32() == 1, "Unsupported HD2PHY1 version.");
        Require(reader.U32() == HeaderSize, "Invalid HD2PHY1 header size.");
        var fileSize = reader.U32();
        Require(fileSize == data.Length, "HD2PHY1 file size mismatch.");
        var crc = reader.U32();
        var unitId = reader.U64();
        var fingerprint = reader.U64();
        var paletteSlots = reader.U32();
        var boneCount = reader.U32();
        var chainCount = reader.U32();
        var jointCount = reader.U32();
        var colliderCount = reader.U32();
        var linkCount = reader.U32();
        var linkCollisionCount = reader.U32();
        var fixedDt = reader.F32();
        var capabilities = reader.U32();
        Require(reader.U32() == 0, "HD2PHY1 header reserved field must be zero.");
        Require(paletteSlots is >= 1 and <= 4096 && boneCount is >= 1 and <= 256 &&
            chainCount is >= 1 and <= 1024 && jointCount <= 8192 && colliderCount <= 1024 &&
            linkCount <= 1024 && linkCollisionCount <= 8192, "HD2PHY1 counts are out of range.");
        Require(Crc32.Compute(data[HeaderSize..], cancellationToken) == crc, "HD2PHY1 payload CRC32 mismatch.");
        var expectedSize = HeaderSize + chainCount * ChainSize + jointCount * JointSize +
            colliderCount * ColliderSize + linkCount * ChainLinkSize + linkCollisionCount * LinkCollisionSize +
            ((capabilities & CapCollisionFilters) != 0 ? jointCount * JointFilterSize : 0);
        Require(expectedSize == data.Length, "HD2PHY1 payload layout does not match its counts.");

        var chains = new List<Hd2PhysChain>();
        var bones = new Dictionary<int, Hd2PhysBone>();
        for (var i = 0; i < chainCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = reader.Name();
            var start = reader.U32();
            var count = reader.U32();
            var stiffness = reader.F32();
            var damping = reader.F32();
            var gravity = reader.Vec3();
            var wind = reader.Vec3();
            var groupMask = reader.U32();
            var collisionMask = reader.U32();
            var flags = reader.U32();
            Require(reader.U32() == 0, "HD2PHY1 chain reserved field must be zero.");
            Require(count is >= 2 and <= 64 && start + count <= jointCount, "HD2PHY1 chain joint range is invalid.");
            Require(Finite(stiffness, 0, 1000) && Finite(damping, 0, 1), "HD2PHY1 chain parameters are out of range.");
            Require(groupMask != 0, "HD2PHY1 chain group mask is zero.");
            chains.Add(new Hd2PhysChain(name, Array.Empty<Hd2PhysJoint>(), stiffness, damping, gravity, wind,
                groupMask, collisionMask, (flags & ChainFlagRootSwing) != 0, (flags & ChainFlagLocalBend) != 0));
        }
        var joints = new List<(string Name, int Slot, int LimitKind, float Angle, float Angle2, Vector3 Axis,
            float StiffnessScale, float DampingScale, Vector3 Head, byte Mode, Vector3 Direction, Vector4 Frame)>();
        for (var i = 0; i < jointCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = reader.Name();
            var hash = reader.U64();
            var slot = reader.I32();
            var radius = reader.F32();
            var kind = reader.U32();
            var angle = reader.F32();
            var angle2 = reader.F32();
            var axis = reader.Vec3();
            var stiffnessScale = reader.F32();
            var dampingScale = reader.F32();
            var head = reader.Vec3();
            Require(reader.U32() == 0, "HD2PHY1 joint reserved field must be zero.");
            var mode = reader.U8();
            Require(reader.U8() == 0, "HD2PHY1 reference flags must be zero.");
            Require(reader.U16() == 0, "HD2PHY1 reference reserved field must be zero.");
            var direction = reader.Vec3();
            var frame = reader.Quat();
            Require(reader.U32() == 0, "HD2PHY1 joint extension reserved field must be zero.");
            Require(hash == Fnv1A64(StrictUtf8.GetBytes(name)), "HD2PHY1 bone name hash mismatch.");
            Require(slot >= 0 && slot < paletteSlots, "HD2PHY1 joint palette slot is out of range.");
            Require(kind <= 3 && Finite(angle, 0, WirePi) && Finite(angle2, 0, WirePi), "HD2PHY1 joint limits are invalid.");
            Require(mode <= 3, "HD2PHY1 reference mode is invalid.");
            Require(Finite(head) && Finite(axis) && Finite(direction) && Finite(frame), "HD2PHY1 joint has non-finite data.");
            Require(!float.IsNaN(radius) && radius is >= 0 and <= 10, "HD2PHY1 joint radius is out of range.");
            Require(Finite(stiffnessScale, 0, 100) && Finite(dampingScale, 0, 100), "HD2PHY1 joint scales are out of range.");
            joints.Add((name, slot, (int)kind, angle, angle2, axis, stiffnessScale, dampingScale, head, mode, direction, frame));
            if (!bones.ContainsKey(slot))
                bones.Add(slot, new Hd2PhysBone(name, slot, -1, head));
        }
        var offset = HeaderSize + chainCount * ChainSize + jointCount * JointSize;
        _ = offset; // Record blocks are consumed below by fixed-size skips.
        for (var i = 0; i < colliderCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.Take(ColliderSize);
        }
        for (var i = 0; i < linkCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.Take(ChainLinkSize);
        }
        for (var i = 0; i < linkCollisionCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reader.Take(LinkCollisionSize);
        }
        if ((capabilities & CapCollisionFilters) != 0)
            reader.Take(checked((int)(jointCount * JointFilterSize)));
        Require(reader.Remaining == 0, "Unexpected trailing HD2PHY1 data.");

        return new Hd2PhysInspection(unitId, fingerprint, paletteSlots, (int)boneCount, (int)chainCount,
            (int)jointCount, (int)colliderCount, (int)linkCount, (int)linkCollisionCount, fixedDt,
            capabilities, crc, (int)fileSize,
            joints.Select(joint => joint.Name).ToArray().AsReadOnly(),
            chains.Select(chain => chain.Name).ToArray().AsReadOnly(),
            bones.Values.OrderBy(bone => bone.PaletteSlot).ToArray().AsReadOnly());
    }

    public static ulong Fnv1A64(ReadOnlySpan<byte> data, ulong seed = FnvOffset)
    {
        var value = seed;
        foreach (var item in data)
        {
            unchecked
            {
                value ^= item;
                value *= FnvPrime;
            }
        }
        return value;
    }

    public static ulong SkeletonFingerprint(uint paletteSlots, IReadOnlyList<Hd2PhysBone> bones)
    {
        var value = Fnv1A64("HD2SKEL1\0"u8);
        Span<byte> counts = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(counts, paletteSlots);
        BinaryPrimitives.WriteUInt32LittleEndian(counts[4..], (uint)bones.Count);
        value = Fnv1A64(counts, value);
        Span<byte> slots = stackalloc byte[8];
        foreach (var bone in bones.OrderBy(bone => bone.PaletteSlot).ThenBy(bone => bone.Name, StringComparer.Ordinal))
        {
            slots.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(slots, (uint)bone.PaletteSlot);
            BinaryPrimitives.WriteInt32LittleEndian(slots[4..], bone.ParentSlot);
            value = Fnv1A64(slots, value);
            var name = new byte[StrictUtf8.GetByteCount(bone.Name) + 1];
            StrictUtf8.GetBytes(bone.Name, name);
            value = Fnv1A64(name, value);
        }
        return value;
    }

    private static byte[] Name64(string value, string where, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(value) && !allowEmpty) throw new Hd2PhysFormatException($"{where}: name must not be empty.");
        if (value.Contains('\0')) throw new Hd2PhysFormatException($"{where}: name must not contain NUL.");
        byte[] encoded;
        try { encoded = StrictUtf8.GetBytes(value); }
        catch (EncoderFallbackException) { throw new Hd2PhysFormatException($"{where}: name is not valid UTF-16."); }
        Require(encoded.Length <= 63, $"{where}: name exceeds 63 UTF-8 bytes.");
        var result = new byte[64];
        encoded.CopyTo(result, 0);
        return result;
    }

    private static Vector3 Normalize(Vector3 value, string where)
    {
        Require(Finite(value), $"{where} contains non-finite values.");
        var length = value.Length();
        Require(length >= 1e-6f, $"{where} is degenerate.");
        return value / length;
    }

    private static Vector4 NormalizeQuaternion(Vector4 value, string where)
    {
        Require(Finite(value), $"{where} contains non-finite values.");
        var length = MathF.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W);
        Require(length >= 1e-6f, $"{where} is degenerate.");
        var normalized = value / length;
        // q and -q denote the same frame; fix the first significant component positive.
        foreach (var component in new[] { normalized.X, normalized.Y, normalized.Z, normalized.W })
        {
            if (MathF.Abs(component) < 1e-6f) continue;
            return component < 0 ? -normalized : normalized;
        }
        return normalized;
    }

    private static bool Finite(float value, float minimum = float.MinValue, float maximum = float.MaxValue) =>
        float.IsFinite(value) && value >= minimum && value <= maximum;

    private static float FiniteValue(float value, float minimum, float maximum, string where)
    {
        Require(Finite(value, minimum, maximum), $"{where} must be finite within {minimum}..{maximum}.");
        return value == 0 ? 0 : value;
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Hd2PhysFormatException(message);
    }

    private sealed record JointRecord(Hd2PhysBone Bone, float Radius, uint LimitKind, float LimitAngle,
        float LimitAngle2, Vector3 LimitAxis, float StiffnessScale, float DampingScale,
        Vector3 Direction, Vector4 Frame, byte Mode, uint GroupMask, uint CollisionMask);

    private sealed class Utf8ByteComparer : IComparer<string>
    {
        public static readonly Utf8ByteComparer Instance = new();
        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var left = StrictUtf8.GetBytes(x);
            var right = StrictUtf8.GetBytes(y);
            return left.AsSpan().SequenceCompareTo(right);
        }
    }

    private sealed class Writer(Stream stream)
    {
        public void U8(byte value) => stream.WriteByte(value);
        public void U16(ushort value) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, value); stream.Write(s); }
        public void U32(uint value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, value); stream.Write(s); }
        public void I32(int value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, value); stream.Write(s); }
        public void U64(ulong value) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(s, value); stream.Write(s); }
        public void F32(float value) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, value); stream.Write(s); }
        public void Bytes(ReadOnlySpan<byte> value) => stream.Write(value);
        public void Name(string value) => Bytes(Name64(value, "name", allowEmpty: true));
        public void Vec3(Vector3 value) { F32(value.X); F32(value.Y); F32(value.Z); }
        public void Quat(Vector4 value) { F32(value.X); F32(value.Y); F32(value.Z); F32(value.W); }
    }

    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;
        public int Remaining => _remaining.Length;

        public ReadOnlySpan<byte> Take(int length)
        {
            Require(length >= 0 && length <= _remaining.Length, "Truncated HD2PHY1 record.");
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }

        public byte U8() => Take(1)[0];
        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public Vector3 Vec3() => new(F32(), F32(), F32());
        public Vector4 Quat() => new(F32(), F32(), F32(), F32());

        public string Name()
        {
            var field = Take(64);
            var end = field.IndexOf((byte)0);
            Require(end >= 0 && field[(end + 1)..].IndexOfAnyExcept((byte)0) < 0, "HD2PHY1 name must be zero-padded.");
            try { return StrictUtf8.GetString(field[..end]); }
            catch (DecoderFallbackException) { throw new Hd2PhysFormatException("HD2PHY1 name is not UTF-8."); }
        }
    }
}

/// <summary>HD2PHY1 线格式的只读检查视图。作者层名称与未进入 wire 的字段不还原。</summary>
public sealed record Hd2PhysInspection(
    ulong UnitId,
    ulong SkeletonFingerprint,
    uint PaletteSlots,
    int BoneCount,
    int ChainCount,
    int JointCount,
    int ColliderCount,
    int ChainLinkCount,
    int LinkCollisionCount,
    float FixedDt,
    uint Capabilities,
    uint PayloadCrc32,
    int Bytes,
    IReadOnlyList<string> JointNames,
    IReadOnlyList<string> ChainNames,
    IReadOnlyList<Hd2PhysBone> JointBones);
