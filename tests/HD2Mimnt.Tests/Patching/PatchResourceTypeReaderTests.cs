using HD2Mimnt.Core.IO;
using HD2Mimnt.Core.ModelPreview;

namespace HD2Mimnt.Tests.Patching;

/// <summary>
/// TOC 布局回归：头部 72 字节 + 类型表 32 字节/项 + 文件条目 80 字节/项。
/// 少算头部或漏跳类型表都会读到偏移错位的数据，把纯外观补丁判成非外观。
/// </summary>
public sealed class PatchResourceTypeReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2mimnt-toc-" + Guid.NewGuid().ToString("N"));

    public PatchResourceTypeReaderTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TryReadTypeIds_SkipsTypeTable_ReturnsOnlyFileEntryTypes()
    {
        var path = WriteToc(typeCount: 3, fileTypes:
            [PatchResourceTypeIds.Unit, PatchResourceTypeIds.Texture, PatchResourceTypeIds.Material, PatchResourceTypeIds.Unit]);

        Assert.True(PatchResourceTypeReader.TryReadTypeIds(path, out var types));
        Assert.Equal(3, types.Count);
        Assert.Contains(PatchResourceTypeIds.Unit, types);
        Assert.Contains(PatchResourceTypeIds.Texture, types);
        Assert.Contains(PatchResourceTypeIds.Material, types);
    }

    [Fact]
    public void TryReadTypeIds_TypeTableOverrunsFile_ReturnsFalse()
    {
        // 头部声明 64 个类型，但类型表根本没写：条目表起点已越过文件末尾，
        // 只能判为“无法确认”，绝不能把后面的字节当条目读。
        var path = Path.Combine(_root, "overrun.patch_0");
        using (var stream = File.Create(path))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0xF0000011u);
            writer.Write(64u);
            writer.Write(1u);
            for (var index = 0; index < (72 - 12) / 4; index++) writer.Write(0u);
            writer.Write((ulong)0);
            writer.Write(PatchResourceTypeIds.Unit);
            for (var index = 0; index < 8; index++) writer.Write(0UL);
        }

        Assert.False(PatchResourceTypeReader.TryReadTypeIds(path, out _));
    }

    [Fact]
    public void TryReadTypeIds_NoFiles_ReturnsFalse()
    {
        var path = WriteToc(typeCount: 0, fileTypes: []);

        Assert.False(PatchResourceTypeReader.TryReadTypeIds(path, out _));
    }

    [Fact]
    public void TryReadTypeIds_TruncatedHeader_ReturnsFalse()
    {
        var path = Path.Combine(_root, "short.patch_0");
        File.WriteAllBytes(path, BitConverter.GetBytes(0xF0000011u));

        Assert.False(PatchResourceTypeReader.TryReadTypeIds(path, out _));
    }

    private string WriteToc(int typeCount, ulong[] fileTypes)
    {
        var path = Path.Combine(_root, $"9ba626afa44a3aa3.patch_{Guid.NewGuid():N}");
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(0xF0000011u);
        writer.Write((uint)typeCount);
        writer.Write((uint)fileTypes.Length);
        for (var index = 0; index < (72 - 12) / 4; index++) writer.Write(0u);
        for (var index = 0; index < typeCount * 8; index++) writer.Write(0u);
        foreach (var typeId in fileTypes)
        {
            writer.Write((ulong)0);
            writer.Write(typeId);
            for (var index = 0; index < 8; index++) writer.Write(0UL);
        }
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
