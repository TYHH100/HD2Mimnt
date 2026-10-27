using System.Buffers.Binary;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Analysis;

public sealed partial class ModCompatibilityAnalyzer
{
    private const uint ModernUnitVersion = 0xA4CD36; // 十进制 10800438，两种写法是同一版本。
    private const int MaxUnitBytes = 64 * 1024 * 1024;

    // 自动 LOD 策略分类阈值，与旧管理器 VersionCheckAssistedRepairService 保持一致。
    private const double AutomaticMeshGpuExpansionRatio = 6.0;
    private const uint AutomaticLargeCustomGpuBytes = 5U * 1024 * 1024;
    private const double AutomaticLargeCustomGpuExpansionRatio = 8.0;
    private const double AutomaticWholePatchCustomDensity = 0.5;

    private static async Task<byte[]> ReadAtAsync(FileStream stream, ulong offset, int size, CancellationToken token)
    {
        if (size < 0 || offset > (ulong)stream.Length || (ulong)size > (ulong)stream.Length - offset)
            throw new InvalidDataException("资源读取超出文件边界。");
        var data = new byte[size];
        stream.Position = checked((long)offset);
        await stream.ReadExactlyAsync(data, token);
        return data;
    }

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));

    private static async Task<CompatibilityUnitAnalysis> InspectUnitAsync(FileStream stream, PatchEntry entry,
        string gpuPath, CancellationToken token)
    {
        var result = new CompatibilityUnitAnalysis();
        try
        {
            if (entry.DataSize < 0x68) throw new InvalidDataException("Unit 头部不足 0x68 字节。");
            var header = await ReadAtAsync(stream, entry.DataOffset, 0x68, token);
            result.HeaderValid = true;
            var ending = U32(header, 0x60);
            if (ending >= 0x60 && ending <= uint.MaxValue - 8) result.ExpectedDataSize = ending + 8;
            result.SizeMatches = result.ExpectedDataSize == entry.DataSize;
            if (!result.SizeMatches) result.Errors.Add("Unit TOC 尺寸与内部结束位置不一致。");
            result.LodOffset = U32(header, 0x30);
            result.LodEnd = U32(header, 0x34);
            result.LodValid = result.LodOffset == 0 && result.LodEnd == 0 || result.LodOffset >= 0x68
                && result.LodEnd > result.LodOffset && result.LodEnd <= entry.DataSize;
            if (!result.LodValid) result.Errors.Add("Unit LOD 范围无效。");
            var version = U32(header, 0x2c);
            var list = U32(header, 0x5c);
            if (version < ModernUnitVersion)
            {
                result.LayoutChecked = true;
                if (list == 0 && entry.GpuSize == 0) return result;
                var offsets = await ReadRelativeTableAsync(stream, entry, list, 4, token);
                foreach (var relative in offsets)
                {
                    var layout = await ReadUnitRangeAsync(stream, entry, (ulong)list + relative, 328, token);
                    for (var i = 0; i < 16; i++)
                        if (U32(layout, 8 + i * 20 + 4) > 16)
                            result.Warnings.Add("旧版 Layout 格式需要随 Unit 版本迁移。");
                }
            }
            else if (version == ModernUnitVersion)
            {
                result.GpuChecked = true;
                if (list == 0 && entry.GpuSize == 0) return result;
                var offsets = await ReadRelativeTableAsync(stream, entry, list, 8, token);
                await using var gpu = File.Exists(gpuPath)
                    ? new FileStream(gpuPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        4096, FileOptions.Asynchronous | FileOptions.RandomAccess) : null;
                foreach (var relative in offsets)
                {
                    var info = await ReadUnitRangeAsync(stream, entry, (ulong)list + relative, 0x1b0, token);
                    var components = U64(info, 0x148);
                    if (components > 16) { result.Errors.Add("GPU 分量数量无效。"); continue; }
                    var strideSum = 0;
                    var unknown = false;
                    var position = -1;
                    var weights = -1;
                    for (var i = 0; i < (int)components; i++)
                    {
                        var type = U32(info, 8 + i * 20);
                        var format = U32(info, 12 + i * 20);
                        var size = format switch { 1 => 8, 2 => 12, 4 or 28 or 30 or 33 => 4, 35 => 8, _ => 0 };
                        if (size == 0 || type > 7) { result.UnknownGpuComponents++; unknown = true; continue; }
                        if (type == 0 && format == 2) position = strideSum;
                        if (type == 7 && format == 35) weights = strideSum;
                        strideSum += size;
                    }
                    var vertexCount = U32(info, 0x160);
                    var stride = U32(info, 0x164);
                    var indexCount = U32(info, 0x188);
                    var indexType = U32(info, 0x18c);
                    var vertexOffset = U32(info, 0x1a0);
                    var vertexSize = U32(info, 0x1a4);
                    var indexOffset = U32(info, 0x1a8);
                    var indexSize = U32(info, 0x1ac);
                    var vertexBytes = (ulong)vertexCount * stride;
                    var indexElementSize = indexType == 0 ? 2u : indexType == 1 ? 4u : 0u;
                    if (!unknown && (stride == 0 || strideSum != stride)) result.Errors.Add("GPU 顶点步长与分量不一致。");
                    if (vertexBytes > vertexSize || vertexSize - vertexBytes > 15
                        || !InBounds(vertexOffset, vertexSize, entry.GpuSize)
                        || indexElementSize == 0 || (ulong)indexCount * indexElementSize != indexSize
                        || !InBounds(indexOffset, indexSize, entry.GpuSize))
                    { result.Errors.Add("GPU 顶点或索引缓冲范围无效。"); continue; }
                    if (unknown) { result.Warnings.Add("GPU 包含未识别的分量格式。"); continue; }
                    if (gpu is null) { result.Errors.Add("缺少 GPU 载荷。"); continue; }
                    if (vertexCount > 0 && stride is > 0 and <= 4096)
                    {
                        var vertex = await ReadAtAsync(gpu, checked(entry.GpuOffset + vertexOffset), (int)stride, token);
                        if (position >= 0 && Enumerable.Range(0, 3).Any(i => !float.IsFinite(BitConverter.ToSingle(vertex, position + i * 4))))
                            result.Errors.Add("GPU 采样顶点位置不是有限数值。");
                        if (weights >= 0)
                        {
                            var sum = 0f;
                            for (var i = 0; i < 4; i++)
                            {
                                var weight = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(vertex.AsSpan(weights + i * 2)));
                                if (!float.IsFinite(weight) || weight < -0.001f) result.Errors.Add("GPU 采样蒙皮权重无效。");
                                sum += weight;
                            }
                            if (sum > 0.001f && MathF.Abs(sum - 1) > 0.03f) result.Errors.Add("GPU 采样权重总和无效。");
                        }
                    }
                    if (indexCount > 0)
                        foreach (var index in new HashSet<uint> { 0, indexCount / 2, indexCount - 1 })
                        {
                            var sample = await ReadAtAsync(gpu, checked(entry.GpuOffset + indexOffset + index * (ulong)indexElementSize), (int)indexElementSize, token);
                            var value = indexElementSize == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(sample) : U32(sample, 0);
                            if (value >= vertexCount) result.Errors.Add("GPU 采样索引超出顶点数量。");
                        }
                }
            }
            else result.Warnings.Add("尚未识别此 Unit 版本的 GPU 结构。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException)
        { result.Errors.Add(ex.Message); }
        return result;
    }

    private static async Task<uint[]> ReadRelativeTableAsync(FileStream stream, PatchEntry entry, uint list,
        int bytesPerEntry, CancellationToken token)
    {
        if (list < 0x68) throw new InvalidDataException("Unit Layout/StreamInfo 表偏移无效。");
        var count = U32(await ReadUnitRangeAsync(stream, entry, list, 4, token), 0);
        if (count > 100) throw new InvalidDataException("Unit Layout/StreamInfo 数量无效。");
        var table = await ReadUnitRangeAsync(stream, entry, list, checked(4 + (int)count * bytesPerEntry + (bytesPerEntry == 8 ? 4 : 0)), token);
        return Enumerable.Range(0, (int)count).Select(i => U32(table, 4 + i * 4)).ToArray();
    }

    private static Task<byte[]> ReadUnitRangeAsync(FileStream stream, PatchEntry entry, ulong offset, int size, CancellationToken token)
    {
        if (offset > entry.DataSize || (ulong)size > entry.DataSize - offset)
            throw new InvalidDataException("Unit 内部范围越界。");
        return ReadAtAsync(stream, checked(entry.DataOffset + offset), size, token);
    }

    internal static uint[] ReadMeshIds(byte[] data)
    {
        if (data.Length < 0x68) return [];
        var offset = U32(data, 0x64);
        if (offset < 0x68 || offset > data.Length - 4) return [];
        var count = U32(data, (int)offset);
        if (count > 4096 || offset + 4UL + count * 8UL > (ulong)data.Length) return [];
        return Enumerable.Range(0, (int)count).Select(i => U32(data, checked((int)offset + 4 + (int)count * 4 + i * 4))).ToArray();
    }
}
