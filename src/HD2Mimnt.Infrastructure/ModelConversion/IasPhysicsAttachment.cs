namespace HD2Mimnt.Infrastructure.ModelConversion;

/// <summary>物理记录实际绑定的骨骼调色板槽位，用于核对 Unit/Rig 世界 Rest。</summary>
public static class IasPhysicsAttachment
{
    public static IReadOnlySet<int> Slots(IasRigProfile profile, byte[] physics)
    {
        var nodes = profile.TargetBones;
        var rig = IasCompanionReader.PreviewRig(profile);
        IasNativeModelData.ValidateBinary(new IasPhysicsDomain(profile.UnitId, nodes.Count,
            nodes.Select(node => node.Parent).ToArray(), rig, physics), IasHash.Sha256Hex(rig.Concat(physics).ToArray()));
        var names = nodes.ToDictionary(node => node.PaletteSlot, node => node.Name);
        var used = new HashSet<int>();
        void Attached(int record)
        {
            IasBinary.Require(record + 76 <= physics.Length, "物理骨骼绑定截断");
            var name = IasBinary.Name64(physics.AsSpan(record, 64));
            var digest = IasBinary.ReadUInt64(physics, record + 64);
            var slot = IasBinary.ReadInt32(physics, record + 72);
            IasBinary.Require(names.TryGetValue(slot, out var expected) && expected == name && IasHash.Fnv1a64(name) == digest,
                "unverified physics bone attachment");
            used.Add(slot);
        }
        var joints = IasBinary.ReadUInt32(physics, 48);
        var attachments = IasBinary.ReadUInt32(physics, 52);
        var colliders = IasBinary.ReadUInt32(physics, 56);
        var capabilities = IasBinary.ReadUInt32(physics, 72);
        var start = IasCompanionReader.PhysicsHeaderSize + 120 * (int)joints;
        for (var index = 0; index < attachments; index++) Attached(start + 164 * index);
        start += 164 * (int)attachments;
        for (var index = 0; index < colliders; index++)
        {
            var record = start + 188 * index;
            IasBinary.Require(record + 188 <= physics.Length, "物理碰撞截断");
            var flags = IasBinary.ReadUInt32(physics, record + 152);
            var end = IasBinary.ReadUInt32(physics, record + 184);
            IasBinary.Require((flags & ~7U) == 0, "unverified physics collider flags");
            if (physics[record + 64] != 0)
            {
                Attached(record + 64);
            }
            else
            {
                IasBinary.Require(physics.AsSpan(record + 64, 72).IndexOfAnyExcept((byte)0) < 0
                                  && IasBinary.ReadInt32(physics, record + 136) == -1 && (flags & 6U) == 0,
                    "unverified root collider attachment");
                used.UnionWith(nodes.Where(node => node.Parent < 0).Select(node => node.PaletteSlot));
            }
            if ((flags & 4) != 0)
            {
                var kind = IasBinary.ReadUInt32(physics, record + 140);
                IasBinary.Require((capabilities & 512) != 0 && kind == 1 && end > 0 && names.ContainsKey((int)end - 1),
                    "unverified second collider endpoint");
                used.Add((int)end - 1);
            }
            else
            {
                IasBinary.Require(end == 0, "unverified collider endpoint encoding");
            }
        }
        return used;
    }
}
