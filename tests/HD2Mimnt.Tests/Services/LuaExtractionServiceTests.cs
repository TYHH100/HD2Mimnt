using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Analysis;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class LuaExtractionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-LuaSvc-{Guid.NewGuid():N}");
    private readonly Mock<IModRepository> _mods = new();
    private readonly Mock<ILuaExtractionStore> _store = new();
    private readonly SettingsService _settings;
    private readonly List<LuaExtractionResult> _records = [];

    public LuaExtractionServiceTests()
    {
        Directory.CreateDirectory(_root);
        var repository = new Mock<ISettingsRepository>();
        repository.Setup(repo => repo.IsLegacyMigrationComplete()).Returns(true);
        repository.Setup(repo => repo.ReadAll()).Returns(new Dictionary<string, string>());
        _settings = new SettingsService(repository.Object, Path.Combine(_root, "settings"));
        _store.Setup(store => store.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _records.ToList());
        _store.Setup(store => store.UpsertAsync(It.IsAny<LuaExtractionResult>(), It.IsAny<CancellationToken>()))
            .Callback((LuaExtractionResult result, CancellationToken _) =>
            {
                _records.RemoveAll(existing => existing.ModId == result.ModId);
                _records.Add(result);
            })
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private LuaExtractionService CreateService() => new(_mods.Object, _store.Object, _settings);

    /// <summary>写一个只含一个源码 lua 的最小 patch_0（与 LuaExtractorTests 的布局约定一致）。</summary>
    private static void WritePatchWithLua(string modDirectory)
    {
        var body = Encoding.UTF8.GetBytes("-- HD2-Addon: mods/test/script\nreturn 1");
        var payload = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 2);
        body.CopyTo(payload, 8);
        const int dataStart = 104;
        var bytes = new byte[dataStart + 80 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xF0000011);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(dataStart), BslModDetector.ResourceHash("mods/test/script"));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(dataStart + 8), 0xa14e8dfa2cd117e2UL);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(dataStart + 16), (ulong)(dataStart + 80));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(dataStart + 56), (uint)payload.Length);
        payload.CopyTo(bytes, dataStart + 80);
        File.WriteAllBytes(Path.Combine(modDirectory, "9ba626afa44a3aa3.patch_0"), bytes);
    }

    private Mod AddMod(string name)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        WritePatchWithLua(path);
        var mod = new Mod { Id = Guid.NewGuid(), Name = name, Path = path };
        _mods.Setup(repo => repo.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        return mod;
    }

    private static string OutputOf(string modDirectory) => Path.Combine(modDirectory, ModDerivedContent.LuaFolderName);

    [Fact]
    public async Task Disabled_ShouldNotExtractAnything()
    {
        var mod = AddMod("未开启");
        var summary = await CreateService().ExtractForModsAsync([mod.Id]);
        Assert.Equal(0, summary.ProcessedMods);
        Assert.False(Directory.Exists(OutputOf(mod.Path)));
        Assert.Empty(_records);
    }

    [Fact]
    public async Task Enabled_ShouldExtractAndRecord()
    {
        var mod = AddMod("新模组");
        await _settings.SetSettingAsync(SettingKeys.LuaAutoExtract, true);
        var summary = await CreateService().ExtractForModsAsync([mod.Id]);
        Assert.Equal(1, summary.ProcessedMods);
        Assert.Equal(1, summary.SourceCount);
        var record = Assert.Single(_records);
        Assert.Equal(mod.Id, record.ModId);
        Assert.Equal(1, record.SourceCount);
        Assert.Equal(ModContentStamp.Calculate(mod.Path), record.ContentStamp);
        Assert.True(File.Exists(Path.Combine(OutputOf(mod.Path), "mods", "test", "script.lua")));
    }

    [Fact]
    public async Task RepeatRun_ShouldSkipAlreadyExtractedMod()
    {
        var mod = AddMod("重复开启");
        await _settings.SetSettingAsync(SettingKeys.LuaAutoExtract, true);
        var service = CreateService();
        Assert.Equal(1, (await service.ExtractForModsAsync([mod.Id])).ProcessedMods);
        var output = Path.Combine(OutputOf(mod.Path), "mods", "test", "script.lua");
        var before = File.GetLastWriteTimeUtc(output);
        // 模拟功能关闭再开启：记录与内容戳都未变，不重复提取，输出文件保持原样。
        Assert.Equal(0, (await service.ExtractForModsAsync([mod.Id])).ProcessedMods);
        Assert.Equal(before, File.GetLastWriteTimeUtc(output));
        Assert.Equal(1, _records.Count);
        _store.Verify(store => store.UpsertAsync(It.IsAny<LuaExtractionResult>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ChangedContent_ShouldReextractAndReplaceRecord()
    {
        var mod = AddMod("内容变化");
        await _settings.SetSettingAsync(SettingKeys.LuaAutoExtract, true);
        var service = CreateService();
        await service.ExtractForModsAsync([mod.Id]);
        var originalRecord = _records.Single();
        // 模组目录更新：新增补丁文件使内容戳变化，应重拆并覆盖记录。
        await File.WriteAllTextAsync(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_1"), "更新内容");
        Assert.Equal(1, (await service.ExtractForModsAsync([mod.Id])).ProcessedMods);
        Assert.Single(_records);
        Assert.NotEqual(originalRecord.ContentStamp, _records[0].ContentStamp);
        Assert.Equal(ModContentStamp.Calculate(mod.Path), _records[0].ContentStamp);
        Assert.True(File.Exists(Path.Combine(OutputOf(mod.Path), "mods", "test", "script.lua")));
    }

    [Fact]
    public async Task Failure_ShouldNotWriteRecordSoNextRunRetries()
    {
        var mod = AddMod("失败重试");
        await _settings.SetSettingAsync(SettingKeys.LuaAutoExtract, true);
        _store.SetupSequence(store => store.UpsertAsync(It.IsAny<LuaExtractionResult>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("写入失败"));
        // 单模组失败不中断整批（返回值不计成功），且不写记录，下次运行重试。
        var summary = await CreateService().ExtractForModsAsync([mod.Id]);
        Assert.Equal(0, summary.ProcessedMods);
        Assert.Empty(_records);
    }
}
