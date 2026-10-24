using System.Security.Cryptography;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.SoundFix;
using Moq;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;
using static HD2Mimnt.Tests.Services.SoundFixFixture;

namespace HD2Mimnt.Tests.Services;

public sealed class SoundFixTests : IDisposable
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-SoundFix-" + Guid.NewGuid().ToString("N"));
    private string BackupRoot => Path.Combine(_root, "backups");
    private string GameRoot => Path.Combine(_root, "game");
    private Mod CreateMod() => new() { Id = Guid.NewGuid(), Name = "Sound mod", Path = Path.Combine(_root, "library", Guid.NewGuid().ToString("N")), Hash = "old" };
    private static byte[] Official => Bank((1, new byte[] { 1, 2, 3 }), (2, new byte[] { 4, 5 }));
    private static byte[] Custom => Bank((1, new byte[] { 9, 8, 7, 6, 5, 4, 3 }));

    private (SoundFixService Service, ModService Mods, Mock<IModRepository> Repository) CreateService(IGameSoundBankReader reader, params Mod[] library)
    {
        Directory.CreateDirectory(_root);
        var settingsRepository = new Mock<ISettingsRepository>();
        settingsRepository.Setup(r => r.ReadAll()).Returns(new Dictionary<string, string> { ["Game.Path"] = System.Text.Json.JsonSerializer.Serialize(GameRoot) });
        settingsRepository.Setup(r => r.IsLegacyMigrationComplete()).Returns(true);
        var settings = new SettingsService(settingsRepository.Object, _root);
        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(library);
        foreach (var mod in library) repository.Setup(r => r.GetByIdAsync(mod.Id, It.IsAny<CancellationToken>())).ReturnsAsync(mod);
        var mods = new ModService(repository.Object, Path.Combine(_root, "library"));
        return (new(repository.Object, mods, new(reader), settings, BackupRoot), mods, repository);
    }

    [Fact]
    public async Task LibraryScan_SyntheticGameArchive_MultipleBanksRepairPreservesMediaOtherResourcesAndIdentityThenRestores()
    {
        var mod = CreateMod();
        var path = WritePatch(mod.Path, resources: [Sound(10, Custom), Sound(11, Custom), Name(10, "content/audio/weapon_one"),
            Name(11, "content/audio/weapon_two"), new(99, UnitType, Unit(), Gpu: [11, 12], Stream: [13, 14])]);
        var original = File.ReadAllBytes(path);
        mod.Size = original.Length; var id = mod.Id;
        var gpu = File.ReadAllBytes(path + ".gpu_resources"); var stream = File.ReadAllBytes(path + ".stream");
        WriteGame(GameRoot, Sound(10, Official), Sound(11, Official));
        var gameHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(GameRoot, "data", "bundles.00.nxa")));
        var (service, mods, _) = CreateService(new GameUnitReferenceReader(), mod);
        var events = 0; mods.ModsChanged += (_, _) => events++;
        var report = Assert.Single(await service.ScanLibraryAsync());
        Assert.Equal(2, report.Banks.Count(b => b.State == SoundFixBankState.Repairable));
        Assert.Equal(original, File.ReadAllBytes(path));
        var repaired = await service.RepairAsync(report);
        Assert.True(repaired.Changed); Assert.True(await service.HasBackupAsync(id)); Assert.Equal(id, mod.Id); Assert.Empty(mod.Hash);
        var bytes = File.ReadAllBytes(path);
        foreach (var bankId in new ulong[] { 10, 11 })
        {
            var chunks = Chunks(ResourceData(bytes, bankId)[16..]);
            Assert.Equal(24, chunks["DIDX"].Length);
            Assert.Equal(1u, BitConverter.ToUInt32(chunks["DIDX"])); Assert.Equal(16u, BitConverter.ToUInt32(chunks["DIDX"], 16));
            Assert.Equal(new byte[] { 9, 8, 7, 6, 5, 4, 3 }, chunks["DATA"][..7]);
            Assert.Equal(7u, BitConverter.ToUInt32(chunks["HIRC"], 26));
        }
        Assert.Equal(ResourceData(original, 99, UnitType), ResourceData(bytes, 99, UnitType));
        Assert.Equal(gpu, File.ReadAllBytes(path + ".gpu_resources")); Assert.Equal(stream, File.ReadAllBytes(path + ".stream"));
        Assert.Equal(gameHash, SHA256.HashData(File.ReadAllBytes(Path.Combine(GameRoot, "data", "bundles.00.nxa"))));
        Assert.All(Assert.Single(await service.ScanLibraryAsync()).Banks, bank => Assert.Equal(SoundFixBankState.Current, bank.State));
        await service.RestoreAsync(id);
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal(original.Length, mod.Size); Assert.Equal(2, events);
        Assert.False(await service.HasBackupAsync(id));
        Assert.Empty(Directory.GetFiles(mod.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Scan_SkipsCustomVoiceAndStreamedBanksAndReportsMissingReference()
    {
        var mod = CreateMod();
        WritePatch(mod.Path, resources: [Sound(1, Bank((1, new byte[] { 9 }), (99, new byte[] { 8 }))),
            Name(1, "content/audio/voice"), Sound(2, Custom, [1]), Sound(3, Custom)]);
        var reader = new Reader(); reader.Banks[1] = new(Official); reader.Banks[2] = new(Official);
        var (service, _, _) = CreateService(reader, mod);
        var report = Assert.Single(await service.ScanLibraryAsync());
        Assert.Equal(new[] { SoundFixBankState.Skipped, SoundFixBankState.Skipped, SoundFixBankState.Unknown }, report.Banks.Select(b => b.State));
        Assert.False(report.CanRepair);
    }

    [Fact]
    public async Task Repair_HardLinkedDeploymentStaysUnchangedAndBackupRestoresAfterLibraryMove()
    {
        if (!OperatingSystem.IsWindows()) return;
        var mod = CreateMod(); var path = WritePatch(mod.Path, resources: [Sound(1, Custom)]);
        var original = File.ReadAllBytes(path);
        var deployed = Path.Combine(_root, "deployed.patch_0");
        Assert.True(CreateHardLink(deployed, path, IntPtr.Zero), "测试硬链接创建失败。");
        var reader = new Reader(); reader.Banks[1] = new(Official);
        var (service, _, _) = CreateService(reader, mod);
        await service.RepairAsync(Assert.Single(await service.ScanLibraryAsync()));
        Assert.Equal(original, File.ReadAllBytes(deployed));
        Assert.NotEqual(original, File.ReadAllBytes(path));
        var moved = mod.Path + "-moved";
        Directory.Move(mod.Path, moved); mod.Path = moved;
        await service.RestoreAsync(mod.Id);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(moved, Path.GetFileName(path))));
        Assert.Equal(original, File.ReadAllBytes(deployed));
    }

    [Fact]
    public async Task Repair_StaleModOrReferenceIsRejectedAndDatabaseFailureRollsBackFilesAndCaches()
    {
        var mod = CreateMod(); var path = WritePatch(mod.Path, resources: [Sound(1, Custom)]);
        var original = File.ReadAllBytes(path); mod.Size = original.Length;
        var reader = new Reader(); reader.Banks[1] = new(Official);
        var (service, mods, repository) = CreateService(reader, mod);
        var events = 0; mods.ModsChanged += (_, _) => events++;
        var report = Assert.Single(await service.ScanLibraryAsync());
        reader.Banks[1] = new(Bank((1, new byte[] { 1 }), (2, new byte[] { 2 }), (3, new byte[] { 3 })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync(report));
        reader.Banks[1] = new(Official);
        repository.Setup(r => r.UpdateAsync(It.IsAny<Mod>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("database unavailable"));
        await Assert.ThrowsAsync<IOException>(() => service.RepairAsync(report));
        Assert.Equal(original, File.ReadAllBytes(path)); Assert.Equal("old", mod.Hash); Assert.Equal(original.Length, mod.Size);
        Assert.Equal(0, events); Assert.False(await service.HasBackupAsync(mod.Id));
        Assert.Empty(Directory.GetFiles(mod.Path, "*.tmp", SearchOption.AllDirectories));
        var changed = original.ToArray(); changed[^1] ^= 1; File.WriteAllBytes(path, changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RepairAsync(report));
    }

    [Fact]
    public async Task Scan_CancellationAndMalformedBankLeaveLibraryUnchangedAndNoTemporaryFiles()
    {
        var mod = CreateMod(); var path = WritePatch(mod.Path, resources: [Sound(1, Custom)]);
        var original = File.ReadAllBytes(path);
        var reader = new Reader { WaitForCancellation = true }; reader.Banks[1] = new(Official);
        var (service, _, _) = CreateService(reader, mod);
        using var cancellation = new CancellationTokenSource();
        var pending = service.ScanLibraryAsync(token: cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(original, File.ReadAllBytes(path));
        reader.WaitForCancellation = false;
        WritePatch(mod.Path, resources: [Sound(1, [1, 2, 3])]);
        var report = Assert.Single(await service.ScanLibraryAsync());
        Assert.Equal(SoundFixBankState.Error, Assert.Single(report.Banks).State); Assert.False(report.CanRepair);
    }

    [Fact]
    public async Task GameSoundReferences_DifferentOfficialBanksWithSameIdStayAmbiguous()
    {
        WriteGame(GameRoot, Sound(1, Official), Sound(1, Custom));
        var result = await new GameUnitReferenceReader().ReadSoundBanksAsync(GameRoot, [1, 99]);
        Assert.Null(result[1].Data); Assert.Contains("不同版本", result[1].Error);
        Assert.Null(result[99].Data); Assert.Contains("未找到", result[99].Error);
    }

    [Fact]
    public async Task Transaction_CancelBeforeCommitLeavesFilesAndRestoreRefusesNewerModContent()
    {
        var mod = CreateMod(); var path = WritePatch(mod.Path, resources: [Sound(1, Custom)]);
        var reader = new Reader(); reader.Banks[1] = new(Official);
        var (service, _, _) = CreateService(reader, mod);
        var report = Assert.Single(await service.ScanLibraryAsync());
        await service.RepairAsync(report);
        var changed = File.ReadAllBytes(path); changed[^1] ^= 1; File.WriteAllBytes(path, changed);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.RestoreAsync(mod.Id));
        Assert.Equal(changed, File.ReadAllBytes(path));
        using var transaction = new SoundFixTransaction(mod.Id, mod.Path, BackupRoot);
        await transaction.StageAsync(path, new MemoryStream([1, 2, 3]), 3, Convert.ToHexString(SHA256.HashData(changed)), default);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.ApplyAsync(cancellation.Token));
        await transaction.RollbackAsync(); Assert.Equal(changed, File.ReadAllBytes(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
