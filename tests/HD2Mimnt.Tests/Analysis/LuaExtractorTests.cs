using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Tests.Analysis;

public sealed class LuaExtractorTests : IDisposable
{
    private const ulong LuaType = 0xa14e8dfa2cd117e2;
    private const ulong UnitType = 0xe0a48d0be9a7453f;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-LuaExtract-{Guid.NewGuid():N}");

    public LuaExtractorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string ModDirectory()
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] SourcePayload(string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var payload = new byte[8 + bodyBytes.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)bodyBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
        bodyBytes.CopyTo(payload, 8);
        return payload;
    }

    private static byte[] BytecodePayload()
    {
        // Wwise 回调字节码：同为 lua 类型，但 envelope 版本不是 2。
        var payload = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 5);
        Encoding.ASCII.GetBytes("JITCODE!").CopyTo(payload, 8);
        return payload;
    }

    /// <summary>写一个最小 patch_0：72 字节头 + 一个占位类型记录 + 每资源 80 字节 TOC 记录与载荷。</summary>
    private static void WritePatch(string modDirectory, string fileName, params (ulong Id, ulong Type, byte[] Payload)[] entries)
    {
        const int headerLength = 72;
        const int typeLength = 32;
        const int entryLength = 80;
        var dataStart = headerLength + typeLength;
        var bytes = new byte[dataStart + entries.Length * (entryLength + 4096)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xF0000011);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)entries.Length);
        var offset = dataStart;
        var payloadOffset = (long)dataStart + entries.Length * entryLength;
        for (var index = 0; index < entries.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), entries[index].Id);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 8), entries[index].Type);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset + 16), (ulong)payloadOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 56), (uint)entries[index].Payload.Length);
            entries[index].Payload.CopyTo(bytes, (int)payloadOffset);
            payloadOffset += entries[index].Payload.Length;
            offset += entryLength;
        }
        Array.Resize(ref bytes, (int)payloadOffset);
        File.WriteAllBytes(Path.Combine(modDirectory, fileName), bytes);
    }

    private static string OutputOf(string modDirectory) => Path.Combine(modDirectory, ModDerivedContent.LuaFolderName);

    [Fact]
    public void Extract_ShouldNameSourceByAddonHeaderAndFallbackToResourceId()
    {
        var mod = ModDirectory();
        var named = BslModDetector.ResourceHash("mods/test/armor_hud");
        WritePatch(mod, "a.patch_0",
            (named, LuaType, SourcePayload("-- HD2-Addon: mods/test/armor_hud\nreturn true")),
            (0x123456789abcdef0UL, LuaType, SourcePayload("return false")));
        var summary = LuaExtractor.Extract(mod, OutputOf(mod));
        Assert.Equal(2, summary.SourceCount);
        Assert.Equal(0, summary.BytecodeCount);
        Assert.Equal("-- HD2-Addon: mods/test/armor_hud\nreturn true", File.ReadAllText(Path.Combine(OutputOf(mod), "mods", "test", "armor_hud.lua")));
        Assert.Equal("return false", File.ReadAllText(Path.Combine(OutputOf(mod), "123456789ABCDEF0.lua")));
    }

    [Fact]
    public void Extract_ShouldFallbackToResourceIdWhenHeaderHashMismatches()
    {
        var mod = ModDirectory();
        // 注释路径与资源身份不一致：命名不可信，必须回退资源 ID，避免误导审查。
        var resourceId = BslModDetector.ResourceHash("mods/test/real");
        WritePatch(mod, "a.patch_0", (resourceId, LuaType, SourcePayload("-- HD2-Addon: mods/other/fake\nreturn 1")));
        var summary = LuaExtractor.Extract(mod, OutputOf(mod));
        Assert.Equal(1, summary.SourceCount);
        Assert.False(Directory.Exists(Path.Combine(OutputOf(mod), "mods")));
        Assert.Equal("-- HD2-Addon: mods/other/fake\nreturn 1", File.ReadAllText(Path.Combine(OutputOf(mod), $"{resourceId:X16}.lua")));
    }

    [Fact]
    public void Extract_ShouldExportBytecodeAsBytesAndSkipNonLua()
    {
        var mod = ModDirectory();
        WritePatch(mod, "a.patch_0",
            (0xaabbccdd11223344UL, LuaType, BytecodePayload()),
            (0x5555UL, UnitType, new byte[] { 1, 2, 3 }));
        var summary = LuaExtractor.Extract(mod, OutputOf(mod));
        Assert.Equal(0, summary.SourceCount);
        Assert.Equal(1, summary.BytecodeCount);
        var output = File.ReadAllBytes(Path.Combine(OutputOf(mod), "AABBCCDD11223344.bytes"));
        Assert.Equal(BytecodePayload(), output);
        Assert.Equal(1, Directory.GetFiles(OutputOf(mod), "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Extract_ShouldOverwriteSameResourceFromLaterPatchAndCountOnce()
    {
        var mod = ModDirectory();
        var id = BslModDetector.ResourceHash("mods/test/thing");
        WritePatch(mod, "z.patch_0", (id, LuaType, SourcePayload("-- HD2-Addon: mods/test/thing\nreturn 'old'")));
        WritePatch(mod, "z.patch_1", (id, LuaType, SourcePayload("-- HD2-Addon: mods/test/thing\nreturn 'new'")));
        var summary = LuaExtractor.Extract(mod, OutputOf(mod));
        Assert.Equal(1, summary.SourceCount);
        Assert.Equal("-- HD2-Addon: mods/test/thing\nreturn 'new'", File.ReadAllText(Path.Combine(OutputOf(mod), "mods", "test", "thing.lua")));
    }

    [Fact]
    public void Extract_ShouldNotLoseDifferentResourcesSharingACommentPath()
    {
        var mod = ModDirectory();
        // 注释路径须与资源哈希一致才用于命名；两个不同资源声明同一路径时哈希都不匹配，
        // 各自回退到自己的资源 ID 命名，互不覆盖、都不丢失。
        var first = BslModDetector.ResourceHash("mods/test/collide_a");
        var second = BslModDetector.ResourceHash("mods/test/collide_b");
        WritePatch(mod, "a.patch_0",
            (first, LuaType, SourcePayload("-- HD2-Addon: mods/test/shared\nreturn 'first'")),
            (second, LuaType, SourcePayload("-- HD2-Addon: mods/test/shared\nreturn 'second'")));
        var summary = LuaExtractor.Extract(mod, OutputOf(mod));
        Assert.Equal(2, summary.SourceCount);
        Assert.False(Directory.Exists(Path.Combine(OutputOf(mod), "mods")));
        Assert.Equal("-- HD2-Addon: mods/test/shared\nreturn 'first'", File.ReadAllText(Path.Combine(OutputOf(mod), $"{first:X16}.lua")));
        Assert.Equal("-- HD2-Addon: mods/test/shared\nreturn 'second'", File.ReadAllText(Path.Combine(OutputOf(mod), $"{second:X16}.lua")));
    }

    [Fact]
    public void Extract_ShouldIgnoreInvalidMagicAndIncompleteIndex()
    {
        var mod = ModDirectory();
        File.WriteAllBytes(Path.Combine(mod, "bad.patch_0"), new byte[] { 17, 0, 0, 240 });
        // 索引声明超出文件长度的损坏补丁：跳过而不抛异常。
        using (var stream = File.Create(Path.Combine(mod, "truncated.patch_0")))
        {
            var header = new byte[72];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0xF0000011);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), 100);
            stream.Write(header);
            stream.SetLength(72 + 32);
        }
        Assert.Equal(0, LuaExtractor.Extract(mod, OutputOf(mod)).SourceCount);
    }

    [Fact]
    public void Extract_ShouldRespectCancellation()
    {
        var mod = ModDirectory();
        WritePatch(mod, "a.patch_0", (1UL, LuaType, SourcePayload("return 1")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => LuaExtractor.Extract(mod, OutputOf(mod), cts.Token));
    }
}
