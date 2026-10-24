using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class ModCompatibilityAnalyzer
{
    private static async Task AddExtendedMetadataRepairsAsync(string path, PatchMetadata raw,
        CompatibilityRepairPlan plan, CancellationToken token, string? companionSource,
        IReadOnlyDictionary<string, string>? companionOverrides)
    {
        var length = new FileInfo(path).Length;
        var tableEnd = (ulong)(72 + raw.TypeCount * 32L + raw.ResourceCount * 80L);
        void Action(CompatibilityRepairKind kind, long offset, int width, ulong oldValue, ulong newValue)
        {
            if (oldValue != newValue) plan.Actions.Add(new CompatibilityRepairAction
                { Kind = kind, PatchFilePath = path, Offset = offset, Width = width, OldValue = oldValue, NewValue = newValue });
        }
        void Block(string reason) => plan.BlockingReasons.Add($"{Path.GetFileName(path)}: {reason}");
        foreach (var gpu in new[] { false, true })
        {
            uint Value(PatchType type) => gpu ? type.GpuAlignment : type.MainAlignment;
            var candidates = raw.Types.Select(Value).Where(v => v > 0 && v <= 4096 && (v & (v - 1)) == 0).Distinct().ToArray();
            foreach (var type in raw.Types.Where(t => Value(t) == 0))
            {
                // 旧包全零 alignment 没有唯一证据，保留为未声明；只补有唯一同表参考的槽位。
                if (candidates.Length == 1) Action(CompatibilityRepairKind.TypeAlignment, type.TableOffset + (gpu ? 28 : 24), 4, 0, candidates[0]);
                else if (candidates.Length > 1) Block("类型 alignment 存在多个候选值。");
            }
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        async Task RepairSizes()
        {
            for (var i = 0; i < raw.Entries.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var entry = raw.Entries[i];
                if (entry.TypeId != UnitTypeId || entry.DataOffset < tableEnd || !InBounds(entry.DataOffset, 0x68, length)) continue;
                var header = await ReadAtAsync(stream, entry.DataOffset, 0x68, token);
                var end = U32(header, 0x60);
                if (end < 0x60 || end > uint.MaxValue - 8 || end + 8 == entry.DataSize) continue;
                var expected = end + 8;
                var next = raw.Entries.Where(e => e.DataSize > 0 && e.DataOffset > entry.DataOffset && e.DataOffset <= (ulong)length)
                    .Select(e => e.DataOffset).DefaultIfEmpty((ulong)length).Min();
                var lod = U32(header, 0x30);
                var lodEnd = U32(header, 0x34);
                if (!InBounds(entry.DataOffset, expected, length) || entry.DataOffset + expected != next
                    || !(lod == 0 && lodEnd == 0 || lod >= 0x68 && lodEnd > lod && lodEnd <= expected))
                { Block($"Unit {entry.ResourceId:x16} 的内部尺寸缺少物理边界证明。"); continue; }
                Action(CompatibilityRepairKind.UnitDataSize, entry.TableOffset + 56, 4, entry.DataSize, expected);
                raw.Entries[i] = entry with { DataSize = expected };
            }
        }
        await RepairSizes();
        var invalid = raw.Entries.Where(e => !InBounds(e.DataOffset, e.DataSize, length)
            || e.DataSize > 0 && e.DataOffset < tableEnd).ToArray();
        var ranges = raw.Entries.Except(invalid).Where(e => e.DataSize > 0).OrderBy(e => e.DataOffset).ToArray();
        var cursor = tableEnd;
        var gaps = new List<(ulong Start, ulong Size)>();
        foreach (var range in ranges)
        {
            if (range.DataOffset < cursor) { Block("主载荷重叠，无法安全推断偏移。"); return; }
            if (range.DataOffset > cursor) gaps.Add((cursor, range.DataOffset - cursor));
            cursor = range.DataOffset + range.DataSize;
        }
        if (cursor < (ulong)length) gaps.Add((cursor, (ulong)length - cursor));
        var used = new HashSet<ulong>();
        foreach (var entry in invalid)
        {
            var matching = gaps.Where(g => g.Size == entry.DataSize && entry.DataSize > 0).ToArray();
            if (matching.Length != 1 || !used.Add(matching[0].Start)) { Block("无效主偏移没有唯一等长物理空隙。"); continue; }
            Action(CompatibilityRepairKind.MainDataOffset, entry.TableOffset + 16, 8, entry.DataOffset, matching[0].Start);
            raw.Entries[entry.Position] = entry with { DataOffset = matching[0].Start };
        }
        await RepairSizes();
        foreach (var entry in raw.Entries)
        {
            if (!InBounds(entry.DataOffset, entry.DataSize, length) || entry.DataSize > 0 && entry.DataOffset < tableEnd)
                Block("主资源范围仍无效。");
            foreach (var gpu in new[] { false, true })
            {
                var size = gpu ? entry.GpuSize : entry.StreamSize;
                if (size == 0) continue;
                var suffix = gpu ? ".gpu_resources" : ".stream";
                var companion = companionOverrides?.GetValueOrDefault(suffix) ?? (companionSource ?? path) + suffix;
                if (!File.Exists(companion) || !InBounds(gpu ? entry.GpuOffset : entry.StreamOffset, size, new FileInfo(companion).Length))
                    Block("必需的伴随数据缺失或范围无效。");
            }
        }
    }
}
