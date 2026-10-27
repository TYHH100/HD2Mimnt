using System.Buffers.Binary;
using System.Text;

namespace HD2Mimnt.Infrastructure.Analysis;

internal enum CompatibilityBodyShape
{
    Unknown,
    Any,
    Slim,
    Stocky
}

internal enum CompatibilityCustomizationSlot
{
    Unknown,
    Torso,
    Hip,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
    LeftShoulder,
    RightShoulder
}

internal readonly record struct CompatibilityUnitCustomizationInfo(
    CompatibilityBodyShape BodyShape,
    CompatibilityCustomizationSlot Slot);

/// <summary>
/// 读取 Unit 的可选自定义元数据（体型字符串与 HelldiverCustomizationSlot_* 槽位）。
/// 解析保持保守：畸形或未知值一律返回 Unknown；结果只用于自动 LOD 策略的同槽位传播，
/// 不参与检查结论。（移植自旧管理器 PatchResourceInspectionService。）
/// </summary>
internal static class CompatibilityUnitInspection
{
    private const int CustomizationInfoOffset = 0x4C;
    private const int FirstStringOffset = 24;
    private const int MaximumStringSize = 1024;
    private const string BodyTypePrefix = "HelldiverCustomizationBodyType_";
    private const string SlotPrefix = "HelldiverCustomizationSlot_";
    private const int MaximumSlotScanSize = 4096;

    internal static CompatibilityUnitCustomizationInfo TryReadCustomizationInfo(byte[] data)
    {
        if (data.Length < CustomizationInfoOffset + sizeof(uint))
            return new(CompatibilityBodyShape.Unknown, CompatibilityCustomizationSlot.Unknown);

        var customizationOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(CustomizationInfoOffset, 4));
        var maximumCustomizationOffset = data.Length - FirstStringOffset - sizeof(uint);
        if (customizationOffset == 0 || customizationOffset > int.MaxValue || maximumCustomizationOffset < 0 ||
            customizationOffset > (uint)maximumCustomizationOffset)
            return new(CompatibilityBodyShape.Unknown, CompatibilityCustomizationSlot.Unknown);

        var cursor = checked((int)customizationOffset + FirstStringOffset);
        if (cursor + sizeof(uint) > data.Length)
            return new(CompatibilityBodyShape.Unknown, CompatibilityCustomizationSlot.Unknown);

        var stringLength = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor, 4));
        cursor += sizeof(uint);
        if (stringLength > MaximumStringSize || cursor + stringLength > data.Length)
            return new(CompatibilityBodyShape.Unknown, CompatibilityCustomizationSlot.Unknown);

        var bodyType = Encoding.UTF8.GetString(data, cursor, (int)stringLength).TrimEnd('\0');
        return new(ParseBodyShape(bodyType), TryReadSlot(data, cursor + (int)stringLength));
    }

    internal static CompatibilityBodyShape ParseBodyShape(string? bodyType)
    {
        if (string.IsNullOrWhiteSpace(bodyType))
            return CompatibilityBodyShape.Unknown;

        var value = bodyType.Trim();
        if (value.StartsWith(BodyTypePrefix, StringComparison.OrdinalIgnoreCase))
            value = value[BodyTypePrefix.Length..];

        return value switch
        {
            "Any" => CompatibilityBodyShape.Any,
            "Slim" => CompatibilityBodyShape.Slim,
            "Stocky" => CompatibilityBodyShape.Stocky,
            _ => CompatibilityBodyShape.Unknown
        };
    }

    private static CompatibilityCustomizationSlot TryReadSlot(byte[] data, int startOffset)
    {
        if (startOffset < 0 || startOffset >= data.Length)
            return CompatibilityCustomizationSlot.Unknown;

        var scanSize = Math.Min(MaximumSlotScanSize, data.Length - startOffset);
        var text = Encoding.UTF8.GetString(data, startOffset, scanSize);
        var prefixIndex = text.IndexOf(SlotPrefix, StringComparison.OrdinalIgnoreCase);
        if (prefixIndex < 0)
            return CompatibilityCustomizationSlot.Unknown;

        var endIndex = text.IndexOf('\0', prefixIndex);
        return ParseSlot(endIndex >= 0 ? text[prefixIndex..endIndex] : text[prefixIndex..]);
    }

    internal static CompatibilityCustomizationSlot ParseSlot(string? slot)
    {
        if (string.IsNullOrWhiteSpace(slot))
            return CompatibilityCustomizationSlot.Unknown;

        var value = slot.Trim();
        if (value.StartsWith(SlotPrefix, StringComparison.OrdinalIgnoreCase))
            value = value[SlotPrefix.Length..];

        return value switch
        {
            "Torso" => CompatibilityCustomizationSlot.Torso,
            "Hip" => CompatibilityCustomizationSlot.Hip,
            "LeftArm" => CompatibilityCustomizationSlot.LeftArm,
            "RightArm" => CompatibilityCustomizationSlot.RightArm,
            "LeftLeg" => CompatibilityCustomizationSlot.LeftLeg,
            "RightLeg" => CompatibilityCustomizationSlot.RightLeg,
            "LeftShoulder" => CompatibilityCustomizationSlot.LeftShoulder,
            "RightShoulder" => CompatibilityCustomizationSlot.RightShoulder,
            _ => CompatibilityCustomizationSlot.Unknown
        };
    }
}
