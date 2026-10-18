using System.Buffers.Binary;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

// Adapted from Helldivers2ModManager (Apache-2.0); see ThirdParty/Helldivers2ModManager/NOTICE.md.
internal static class CompatibilityResourceTransforms
{
    private const uint VersionThresholdForLayoutCheck = 0xA4CD36;
    private const long MaterialTypeId = unchecked((long)0xEAC0B497876ADEDFUL);
    private const int MaterialParentIdOffset = 0x18;
    private const int MaterialTextureCountOffset = 0x40;
    private const int MaterialVariableCountOffset = 0x68;
    private const int MaterialVariableDataSizeOffset = 0x78;
    private const int MaterialTextureTableOffset = 0x88;
    private const int MaterialVariableDescriptorSize = 20;
    private const ulong LegacyCharacterMaterialParentId = 0x54AE9CE1A8FAFE8BUL;
    private const ulong CurrentCharacterMaterialParentId = 0x8F669F365F24594EUL;
    private const int LegacyEmissiveMaterialSize = 512;
    private const int CurrentEmissiveMaterialSize = 480;
    private const uint CurrentEmissiveMaterialVersion = 0x11F;
    private const uint CurrentEmissiveMaterialEndOffset = 0x1C8;
    private const uint CurrentEmissiveOpacityThresholdVariableId = 0x529A4AAF;
    private const uint CurrentEmissiveRangeVariableId = 0x32C02400;
    private const ulong LegacyEmissiveMaterialParentId = 0xD3701FC725106C09UL;
    private const ulong CurrentEmissiveMaterialParentId = 0xC6042E3403385D40UL;
    private const ulong CurrentEmissiveOpacityTextureId = 0x12D4692531C1FD35UL;
    private static readonly uint[] s_legacyCharacterMaterialTextureSemantics =
    [
        0x7CA0D044, 0xC985395A, 0xA72CB013, 0x479FB1EF, 0xDF3EE984,
        0xCAED6CD6, 0xD2F99D38, 0xE7BD9019, 0xD47DB28B, 0xFF2C91CC,
        0x736A0029, 0xF8E31D7B, 0xA59F5E11
    ];
    private static readonly uint[] s_legacyEmissiveMaterialTextureSemantics =
    [
        0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B
    ];
    private static readonly uint[] s_currentEmissiveMaterialTextureSemantics =
    [
        0x1D57DCF3, 0xCA6F2CF1, 0x848BA63B, 0xCBDE381B
    ];
    private static readonly MaterialScalarVariable[] s_legacyEmissiveMaterialVariables =
    [
        new(0xA3351311, 0), new(0x43695F7B, 4), new(0x64AAB07B, 8),
        new(0x6FD0B9E7, 12), new(0x60E7D2A1, 16), new(0x4A7CD0EF, 20),
        new(0x4A6796C6, 24), new(0xBD16A396, 28), new(0x32C02400, 56),
        new(0xC012EFE1, 36), new(0xA83F44CD, 40), new(0x6DDBAE8F, 44),
        new(0x4B564F57, 48), new(0x9ED04DA2, 52)
    ];
    private static readonly MaterialScalarVariable[] s_currentEmissiveMaterialVariables =
    [
        new(CurrentEmissiveOpacityThresholdVariableId, 48), new(0xA3351311, 4),
        new(0x43695F7B, 8), new(0x64AAB07B, 12), new(0x6FD0B9E7, 16),
        new(0x60E7D2A1, 20), new(0x4A7CD0EF, 24), new(0xBD16A396, 28),
        new(CurrentEmissiveRangeVariableId, 52), new(0xA83F44CD, 36),
        new(0x6DDBAE8F, 40), new(0x9ED04DA2, 44)
    ];

    private readonly record struct MaterialScalarVariable(uint Id, uint Offset);
    internal static bool TryGetLegacyMaterialParentMigration(
        ReadOnlySpan<byte> materialData,
        out ulong oldParentMaterialId,
        out ulong newParentMaterialId)
    {
        oldParentMaterialId = 0;
        newParentMaterialId = 0;
        if (materialData.Length < MaterialTextureTableOffset ||
            materialData.Length < MaterialParentIdOffset + sizeof(ulong))
        {
            return false;
        }

        var parentMaterialId = BinaryPrimitives.ReadUInt64LittleEndian(
            materialData.Slice(MaterialParentIdOffset, sizeof(ulong)));
        var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(
            materialData.Slice(MaterialTextureCountOffset, sizeof(uint)));
        var semanticsLength = checked((long)textureCount * sizeof(uint));
        if (textureCount > 4096 ||
            MaterialTextureTableOffset + semanticsLength > materialData.Length)
        {
            return false;
        }

        if (parentMaterialId == LegacyCharacterMaterialParentId &&
            HasExactTextureSemantics(materialData, s_legacyCharacterMaterialTextureSemantics))
        {
            oldParentMaterialId = parentMaterialId;
            newParentMaterialId = CurrentCharacterMaterialParentId;
            return true;
        }

        // The current replacement for the legacy three-input emissive template adds
        // an opacity-clip input, so it cannot be migrated as a fixed-width parent edit.
        return false;
    }

    internal static bool TryBuildLegacyEmissiveMaterialMigration(
        ReadOnlySpan<byte> materialData,
        out byte[] updatedData)
    {
        updatedData = [];
        if (materialData.Length != LegacyEmissiveMaterialSize ||
            BinaryPrimitives.ReadUInt64LittleEndian(
                materialData.Slice(MaterialParentIdOffset, sizeof(ulong))) !=
            LegacyEmissiveMaterialParentId ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                materialData.Slice(MaterialTextureCountOffset, sizeof(uint))) !=
            s_legacyEmissiveMaterialTextureSemantics.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                materialData.Slice(MaterialVariableCountOffset, sizeof(uint))) !=
            s_legacyEmissiveMaterialVariables.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(
                materialData.Slice(MaterialVariableDataSizeOffset, sizeof(uint))) != 60 ||
            !HasExactTextureSemantics(materialData, s_legacyEmissiveMaterialTextureSemantics))
        {
            return false;
        }

        var sourceTextureIdOffset = GetMaterialTextureIdOffset(
            s_legacyEmissiveMaterialTextureSemantics.Length);
        var sourceVariableDescriptorOffset = sourceTextureIdOffset +
            s_legacyEmissiveMaterialTextureSemantics.Length * sizeof(ulong);
        var sourceVariableDataOffset = sourceVariableDescriptorOffset +
            s_legacyEmissiveMaterialVariables.Length * MaterialVariableDescriptorSize;
        if (sourceVariableDataOffset + 60 != materialData.Length ||
            !HasExactScalarVariableLayout(
                materialData,
                sourceVariableDescriptorOffset,
                s_legacyEmissiveMaterialVariables,
                60))
        {
            return false;
        }

        updatedData = new byte[CurrentEmissiveMaterialSize];
        materialData.Slice(0, MaterialTextureTableOffset).CopyTo(updatedData);
        BinaryPrimitives.WriteUInt32LittleEndian(updatedData.AsSpan(0, sizeof(uint)),
            CurrentEmissiveMaterialVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(12, sizeof(uint)),
            CurrentEmissiveMaterialEndOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(
            updatedData.AsSpan(MaterialParentIdOffset, sizeof(ulong)),
            CurrentEmissiveMaterialParentId);
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(MaterialTextureCountOffset, sizeof(uint)),
            checked((uint)s_currentEmissiveMaterialTextureSemantics.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(MaterialVariableCountOffset, sizeof(uint)),
            checked((uint)s_currentEmissiveMaterialVariables.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(MaterialVariableDataSizeOffset, sizeof(uint)), 56);

        var targetTextureIdOffset = GetMaterialTextureIdOffset(
            s_currentEmissiveMaterialTextureSemantics.Length);
        for (var index = 0; index < s_currentEmissiveMaterialTextureSemantics.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(MaterialTextureTableOffset + index * sizeof(uint), sizeof(uint)),
                s_currentEmissiveMaterialTextureSemantics[index]);
        }

        for (var index = 0; index < s_legacyEmissiveMaterialTextureSemantics.Length; index++)
        {
            var textureId = BinaryPrimitives.ReadUInt64LittleEndian(materialData.Slice(
                sourceTextureIdOffset + index * sizeof(ulong),
                sizeof(ulong)));
            BinaryPrimitives.WriteUInt64LittleEndian(updatedData.AsSpan(
                targetTextureIdOffset + index * sizeof(ulong),
                sizeof(ulong)), textureId);
        }
        BinaryPrimitives.WriteUInt64LittleEndian(updatedData.AsSpan(
            targetTextureIdOffset + s_legacyEmissiveMaterialTextureSemantics.Length * sizeof(ulong),
            sizeof(ulong)), CurrentEmissiveOpacityTextureId);

        var sourceValuesById = new Dictionary<uint, byte[]>();
        foreach (var variable in s_legacyEmissiveMaterialVariables)
        {
            sourceValuesById[variable.Id] = materialData.Slice(
                sourceVariableDataOffset + checked((int)variable.Offset),
                sizeof(uint)).ToArray();
        }
        var targetVariableDescriptorOffset = targetTextureIdOffset +
            s_currentEmissiveMaterialTextureSemantics.Length * sizeof(ulong);
        var targetVariableDataOffset = targetVariableDescriptorOffset +
            s_currentEmissiveMaterialVariables.Length * MaterialVariableDescriptorSize;
        for (var index = 0; index < s_currentEmissiveMaterialVariables.Length; index++)
        {
            var variable = s_currentEmissiveMaterialVariables[index];
            var descriptorOffset = targetVariableDescriptorOffset + index * MaterialVariableDescriptorSize;
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(descriptorOffset + 8, sizeof(uint)), variable.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(descriptorOffset + 12, sizeof(uint)), variable.Offset);

            if (variable.Id != CurrentEmissiveRangeVariableId &&
                sourceValuesById.TryGetValue(variable.Id, out var sourceValue))
            {
                sourceValue.CopyTo(updatedData, targetVariableDataOffset + checked((int)variable.Offset));
                continue;
            }

            var defaultValue = variable.Id switch
            {
                CurrentEmissiveOpacityThresholdVariableId => 0.144f,
                CurrentEmissiveRangeVariableId => 1.0f,
                _ => throw new InvalidDataException("The current emissive material layout has an unmapped variable.")
            };
            BinaryPrimitives.WriteInt32LittleEndian(
                updatedData.AsSpan(targetVariableDataOffset + checked((int)variable.Offset), sizeof(uint)),
                BitConverter.SingleToInt32Bits(defaultValue));
        }

        return true;
    }

    private static bool HasExactTextureSemantics(
        ReadOnlySpan<byte> materialData,
        ReadOnlySpan<uint> expectedSemantics)
    {
        var textureCount = BinaryPrimitives.ReadUInt32LittleEndian(
            materialData.Slice(MaterialTextureCountOffset, sizeof(uint)));
        if (textureCount != expectedSemantics.Length)
            return false;

        for (var index = 0; index < expectedSemantics.Length; index++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(
                    materialData.Slice(
                        MaterialTextureTableOffset + index * sizeof(uint),
                        sizeof(uint))) != expectedSemantics[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int GetMaterialTextureIdOffset(int textureCount) =>
        checked(MaterialTextureTableOffset + textureCount * sizeof(uint));

    private static bool HasExactScalarVariableLayout(
        ReadOnlySpan<byte> materialData,
        int descriptorOffset,
        IReadOnlyList<MaterialScalarVariable> expectedVariables,
        int variableDataSize)
    {
        var variableDataOffset = checked(
            descriptorOffset + expectedVariables.Count * MaterialVariableDescriptorSize);
        if (descriptorOffset < 0 || variableDataOffset + variableDataSize > materialData.Length)
            return false;

        for (var index = 0; index < expectedVariables.Count; index++)
        {
            var offset = checked(descriptorOffset + index * MaterialVariableDescriptorSize);
            var expected = expectedVariables[index];
            if (BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset, sizeof(uint))) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 4, sizeof(uint))) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 8, sizeof(uint))) != expected.Id ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 12, sizeof(uint))) != expected.Offset ||
                BinaryPrimitives.ReadUInt32LittleEndian(materialData.Slice(offset + 16, sizeof(uint))) != 0 ||
                expected.Offset > variableDataSize - sizeof(uint))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryBuildUpdatedUnitData(
        byte[] originalData,
        GameUnitReference reference,
        CompatibilityLodStrategy lodStrategy,
        out byte[] updatedData,
        out string error)
    {
        updatedData = [];
        error = string.Empty;
        if (originalData.Length < 0x78)
        {
            error = "header is too small";
            return false;
        }

        var originalVersion = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x2C, 4));
        var lodGroupOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x30, 4));
        var nextSectionOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x34, 4));
        var endingOffset = BinaryPrimitives.ReadUInt32LittleEndian(originalData.AsSpan(0x60, 4));
        if (lodGroupOffset < 0x68 ||
            nextSectionOffset <= lodGroupOffset ||
            nextSectionOffset > originalData.Length ||
            (ulong)endingOffset + 8 != (ulong)originalData.Length)
        {
            error = "has invalid LOD or ending offsets";
            return false;
        }

        var lodStart = checked((int)lodGroupOffset);
        var lodSize = checked((int)(nextSectionOffset - lodGroupOffset));
        var originalLodData = originalData.AsSpan(lodStart, lodSize).ToArray();
        var workingData = originalData.ToArray();
        if (originalVersion < VersionThresholdForLayoutCheck &&
            !TryUpgradeLegacyLayoutFormats(workingData, out error))
        {
            return false;
        }

        return lodStrategy switch
        {
            CompatibilityLodStrategy.UseGameReference => TryUseGameReferenceLod(
                workingData,
                reference,
                lodGroupOffset,
                nextSectionOffset,
                out updatedData,
                out error),
            CompatibilityLodStrategy.PreserveMod => TryPreserveModLod(
                originalData,
                workingData,
                reference,
                lodGroupOffset,
                nextSectionOffset,
                endingOffset,
                originalLodData,
                out updatedData,
                out error),
            _ => throw new ArgumentOutOfRangeException(nameof(lodStrategy))
        };
    }

    private static bool TryPreserveModLod(
        byte[] originalData,
        byte[] workingData,
        GameUnitReference reference,
        uint lodGroupOffset,
        uint nextSectionOffset,
        uint endingOffset,
        byte[] originalLodData,
        out byte[] updatedData,
        out string error)
    {
        var lodStart = checked((int)lodGroupOffset);
        var lodSize = checked((int)(nextSectionOffset - lodGroupOffset));
        if (!workingData.AsSpan(lodStart, lodSize).SequenceEqual(originalLodData))
        {
            error = "legacy Layout data overlaps the custom LOD group";
            updatedData = [];
            return false;
        }

        // Some custom models depend on their own unreversed LOD group list.
        // For this explicit strategy, the game reference supplies only the target Unit version.
        updatedData = workingData;
        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(0x2C, 4),
            reference.Version!.Value);

        var updatedLodStart = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x30, 4));
        var updatedLodEnd = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x34, 4));
        var updatedEnding = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x60, 4));
        if (updatedData.Length != originalData.Length ||
            updatedLodStart != lodGroupOffset ||
            updatedLodEnd != nextSectionOffset ||
            updatedEnding != endingOffset ||
            !updatedData.AsSpan(lodStart, lodSize).SequenceEqual(originalLodData))
        {
            error = "failed to preserve custom LOD and Unit offsets";
            updatedData = [];
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryUseGameReferenceLod(
        byte[] workingData,
        GameUnitReference reference,
        uint lodGroupOffset,
        uint nextSectionOffset,
        out byte[] updatedData,
        out string error)
    {
        var oldLodSize = checked((int)(nextSectionOffset - lodGroupOffset));
        var sizeDifference = reference.LodGroupData.Length - oldLodSize;
        if (sizeDifference % 16 != 0)
        {
            error = "game LOD size change would break 16-byte alignment";
            updatedData = [];
            return false;
        }

        var newLength = checked(workingData.Length + sizeDifference);
        if (newLength < 0x78)
        {
            error = "game LOD would make the Unit too small";
            updatedData = [];
            return false;
        }

        updatedData = new byte[newLength];
        var lodStart = checked((int)lodGroupOffset);
        Buffer.BlockCopy(workingData, 0, updatedData, 0, lodStart);
        Buffer.BlockCopy(
            reference.LodGroupData,
            0,
            updatedData,
            lodStart,
            reference.LodGroupData.Length);
        Buffer.BlockCopy(
            workingData,
            checked((int)nextSectionOffset),
            updatedData,
            checked(lodStart + reference.LodGroupData.Length),
            workingData.Length - checked((int)nextSectionOffset));

        BinaryPrimitives.WriteUInt32LittleEndian(
            updatedData.AsSpan(0x2C, 4),
            reference.Version!.Value);
        for (var offset = 0x34; offset <= 0x70; offset += 4)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(offset, 4));
            if (value == 0 || value <= lodGroupOffset)
                continue;

            if (value < nextSectionOffset || value >= workingData.Length)
            {
                updatedData = [];
                error = "Unit header contains an unsupported interior or out-of-range section pointer";
                return false;
            }

            var adjusted = checked((long)value + sizeDifference);
            if (adjusted < 0 || adjusted > uint.MaxValue)
            {
                error = "contains an offset that cannot be adjusted safely";
                updatedData = [];
                return false;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(
                updatedData.AsSpan(offset, 4),
                (uint)adjusted);
        }

        var updatedLodEnd = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x34, 4));
        var updatedEnding = BinaryPrimitives.ReadUInt32LittleEndian(updatedData.AsSpan(0x60, 4));
        if (updatedLodEnd - lodGroupOffset != reference.LodGroupData.Length ||
            updatedEnding + 8 != updatedData.Length ||
            !updatedData.AsSpan(lodStart, reference.LodGroupData.Length)
                .SequenceEqual(reference.LodGroupData))
        {
            error = "failed its game LOD post-transform checks";
            updatedData = [];
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryUpgradeLegacyLayoutFormats(
        byte[] unitData,
        out string error)
    {
        error = string.Empty;
        var layoutListOffset = BinaryPrimitives.ReadUInt32LittleEndian(unitData.AsSpan(0x5C, 4));
        if (layoutListOffset > unitData.Length - 4)
        {
            error = "legacy Layout list is out of bounds";
            return false;
        }

        var layoutBase = checked((int)layoutListOffset);
        var layoutCount = BinaryPrimitives.ReadInt32LittleEndian(unitData.AsSpan(layoutBase, 4));
        if (layoutCount < 0 ||
            layoutCount > 100 ||
            (long)layoutBase + 4L + layoutCount * 4L > unitData.Length)
        {
            error = "legacy Layout table is invalid";
            return false;
        }

        for (var i = 0; i < layoutCount; i++)
        {
            var relativeOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                unitData.AsSpan(layoutBase + 4 + i * 4, 4));
            var layoutStart = (long)layoutBase + relativeOffset;
            if (layoutStart < 0 ||
                layoutStart + 8 + 16 * 20 > unitData.Length)
            {
                error = "legacy Layout entry is out of bounds";
                return false;
            }

            for (var itemIndex = 0; itemIndex < 16; itemIndex++)
            {
                var formatOffset = checked((int)layoutStart + 8 + itemIndex * 20 + 4);
                var itemFormat = BinaryPrimitives.ReadUInt32LittleEndian(
                    unitData.AsSpan(formatOffset, 4));
                if (itemFormat > 16)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        unitData.AsSpan(formatOffset, 4),
                        checked(itemFormat + 4));
                }
            }
        }

        return true;
    }

}
