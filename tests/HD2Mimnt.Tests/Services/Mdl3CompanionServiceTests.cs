using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Deployment;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.NativeCompanion;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

/// <summary>MDL3 原生伴生组件安装：哈希/签名钉扎、config 未知键保留、官方 state 互操作与幂等删除语义。</summary>
public sealed class Mdl3CompanionServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-Mdl3-{Guid.NewGuid():N}");
    private readonly HD2MimntDbContext _db;
    private readonly ModRepository _mods;
    private readonly ProfileService _profileService;
    private readonly DeploymentService _deployment;
    private readonly StubVerifier _verifier = new(new AuthenticodeVerifyResult(true, "AA".PadRight(40, '0'), null));

    public Mdl3CompanionServiceTests()
    {
        Directory.CreateDirectory(_root);
        _db = new HD2MimntDbContext(Path.Combine(_root, "test.db"));
        _db.InitializeDatabaseAsync().GetAwaiter().GetResult();
        _mods = new ModRepository(_db);
        var profiles = new ProfileRepository(_db);
        _profileService = new ProfileService(profiles, _mods);
        _deployment = new DeploymentService(profiles, _mods, new DeploymentRepository(_db),
            new[] { new CopyDeploymentMethod() }, companionService: CreateService());
    }

    private Mdl3CompanionService CreateService()
        => new(_verifier, localApplicationData: LibraryPath);

    private string LibraryPath => Path.Combine(_root, "localappdata");
    private string DataRoot => Path.Combine(LibraryPath, "MDL3");

    [Fact]
    public void PackageWithoutBuildJson_ShouldReportNoPayload()
    {
        var mod = AddMod("普通模组");
        var service = CreateService();
        Assert.False(service.HasCompanionPayload(mod));
        Assert.Equal(CompanionInstallState.NoPayload, service.GetStatus(mod).State);
    }

    [Fact]
    public void PlainNativePath_ShouldResolveNativeSubdirectorySource()
    {
        // 官方包的 BUILD.json 只写文件名，Native/ 前缀由 install.ps1 拼接；管理器须自行回退到 Native/ 子目录。
        var mod = AddMod("MDL3 官方形态");
        WritePackage(mod.Path, nativePath: "mdl_native_bridge.dll", thumbprint: _verifier.Result.SignerThumbprint!);
        var service = CreateService();
        Assert.True(service.HasCompanionPayload(mod));
        var result = service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Message);
        Assert.True(File.Exists(Path.Combine(DataRoot, "mdl_native_bridge.dll")));
    }

    [Fact]
    public void Install_ShouldRejectSourceHashMismatch()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path, buildSha256: new string('0', 64));
        var service = CreateService();
        Assert.Throws<InvalidOperationException>(() => service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult());
        Assert.False(Directory.Exists(DataRoot));
    }

    [Fact]
    public void Install_ShouldRejectUntrustedSignature()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path);
        _verifier.Result = new AuthenticodeVerifyResult(false, null, "信任链失败");
        var service = CreateService();
        Assert.Throws<InvalidOperationException>(() => service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult());
        Assert.False(Directory.Exists(DataRoot));
    }

    [Fact]
    public void Install_ShouldRejectThumbprintMismatch()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path, thumbprint: new string('B', 40));
        var service = CreateService();
        Assert.Throws<InvalidOperationException>(() => service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult());
        Assert.False(Directory.Exists(DataRoot));
    }

    [Fact]
    public void Install_ShouldCopyDllWriteConfigAndStateAndPreserveUnknownKeys()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path, thumbprint: _verifier.Result.SignerThumbprint!);
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(Path.Combine(DataRoot, "config.json"),
            """{"future_option":"keep-me","schema":9}""");

        var service = CreateService();
        var result = service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult();

        Assert.True(result.Success);
        var dll = Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll");
        Assert.True(File.Exists(dll));
        Assert.True(Directory.Exists(Path.Combine(DataRoot, "Logs")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "config.json")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "config.json.backup")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "install-state.json")));
        // 游戏 VM 无法自建的用户目录须提前创建。
        Assert.True(Directory.Exists(Path.Combine(LibraryPath, "MDL", "Helldivers2", "Mods")));
        Assert.True(Directory.Exists(Path.Combine(LibraryPath, "MDL", "Helldivers2", "Languages", "Options")));
        Assert.True(Directory.Exists(Path.Combine(LibraryPath, "CowboyBingus", "Helldivers2", "Logs")));
        Assert.True(Directory.Exists(Path.Combine(LibraryPath, "HD2Runtime", "mod_data")));

        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataRoot, "config.json")));
        Assert.Equal(1, config.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(2048, config.RootElement.GetProperty("native_memory_mb").GetInt32());
        Assert.Equal("on", config.RootElement.GetProperty("task_jit").GetString());
        Assert.Equal(_root, config.RootElement.GetProperty("game_dir").GetString());
        Assert.Equal("keep-me", config.RootElement.GetProperty("future_option").GetString());

        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(DataRoot, "install-state.json")));
        Assert.Equal("MDL3", state.RootElement.GetProperty("product").GetString());
        Assert.Equal(1, state.RootElement.GetProperty("files").GetArrayLength());
        Assert.Equal(dll, state.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal(CompanionInstallState.Installed, service.GetStatus(mod).State);
    }

    [Fact]
    public void Install_ShouldBeIdempotentAndBackupModifiedTarget()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path, thumbprint: _verifier.Result.SignerThumbprint!);
        var service = CreateService();
        service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult();
        // 目标被外部改动后重装：旧文件进 Backups/<version>，新内容覆盖。
        File.WriteAllText(Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll"), "tampered");

        var result = service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(DataRoot, "Backups", "3.0.0-dev.21", "mdl_native_bridge.dll")));
        Assert.Equal(CompanionInstallState.Installed, service.GetStatus(mod).State);
        // 幂等重跑仍成功。
        Assert.True(service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult().Success);
    }

    [Fact]
    public void Remove_ShouldDeleteDllAndStateButKeepConfigAndUserFolders()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path, thumbprint: _verifier.Result.SignerThumbprint!);
        var service = CreateService();
        service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult();

        var result = service.RemoveAsync(mod).GetAwaiter().GetResult();

        Assert.True(result.Success);
        Assert.False(File.Exists(Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll")));
        Assert.False(File.Exists(Path.Combine(DataRoot, "install-state.json")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "config.json")));
        Assert.True(Directory.Exists(Path.Combine(LibraryPath, "MDL", "Helldivers2", "Mods")));
        Assert.Equal(CompanionInstallState.NotInstalled, service.GetStatus(mod).State);
    }

    [Fact]
    public void Remove_ShouldRetainExternallyModifiedDll()
    {
        var mod = AddMod("MDL3");
        WritePackage(mod.Path, thumbprint: _verifier.Result.SignerThumbprint!);
        var service = CreateService();
        service.EnsureInstalledAsync(mod, _root).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll"), "tampered");

        var result = service.RemoveAsync(mod).GetAwaiter().GetResult();

        Assert.False(result.Success);
        Assert.True(File.Exists(Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "install-state.json")));
    }

    [Fact]
    public async Task DeployProfile_ShouldInstallCompanionAndDeleteShouldRemoveIt()
    {
        var (mod, profileId) = await AddDeployableMdl3ModAsync();
        var game = Path.Combine(_root, "game", "data");
        Directory.CreateDirectory(game);

        var result = await _deployment.DeployProfileAsync(profileId, DeploymentMethod.Copy,
            DeploymentOrder.TopToBottom, Path.Combine(_root, "game"));
        Assert.Equal(DeploymentStatus.Completed, result.Status);
        Assert.True(File.Exists(Path.Combine(game, "9ba626afa44a3aa3.patch_0")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll")));
        Assert.True(File.Exists(Path.Combine(DataRoot, "install-state.json")));

        await _deployment.DeleteModDeploymentRecordsAsync(mod.Id);
        Assert.False(File.Exists(Path.Combine(DataRoot, "Native", "mdl_native_bridge.dll")));
        Assert.False(File.Exists(Path.Combine(DataRoot, "install-state.json")));
        // 用户配置按官方 Remove 语义保留。
        Assert.True(File.Exists(Path.Combine(DataRoot, "config.json")));
    }

    private Mod AddMod(string name, BslModRole? role = null)
    {
        var id = Guid.NewGuid();
        var mod = new Mod
        {
            Id = id, Name = name, Path = Path.Combine(_root, "mods", id.ToString()),
            BslRole = role, BslDetectionVersion = role != null ? BslModDetector.DetectionVersion : 0
        };
        Directory.CreateDirectory(mod.Path);
        _mods.AddAsync(mod).GetAwaiter().GetResult();
        return mod;
    }

    private async Task<(Mod Mod, Guid ProfileId)> AddDeployableMdl3ModAsync()
    {
        var id = Guid.NewGuid();
        var mod = new Mod
        {
            Id = id, Name = "MDL3 预览版", Path = Path.Combine(_root, "mods", id.ToString()),
            Manifest = new ModManifest { Guid = new Guid("b0ae9d35-4a69-5918-b43d-51d4d10ff001") }
        };
        Directory.CreateDirectory(mod.Path);
        // 普通可部署补丁 + 伴生载荷（BUILD.json/Native），FullDirectory 只收集补丁，顶层其余文件不进游戏目录。
        File.WriteAllBytes(Path.Combine(mod.Path, "9ba626afa44a3aa3.patch_0"), [1, 2, 3, 4]);
        WritePackage(mod.Path, thumbprint: _verifier.Result.SignerThumbprint!);
        await _mods.AddAsync(mod);

        var profileId = await _profileService.CreateProfileAsync("配置");
        await _profileService.AddModToProfileAsync(profileId, mod.Id);
        return (mod, profileId);
    }

    /// <summary>写出 BUILD.json 与 Native 源 DLL；哈希默认与内容一致，可指定错误值测试拒绝路径。</summary>
    private static void WritePackage(string modDirectory, string? buildSha256 = null, string? thumbprint = null,
        string nativePath = "Native/mdl_native_bridge.dll")
    {
        Directory.CreateDirectory(Path.Combine(modDirectory, "Native"));
        var dllPath = Path.Combine(modDirectory, "Native", "mdl_native_bridge.dll");
        File.WriteAllBytes(dllPath, "signed-native-bytes"u8.ToArray());
        var sha = buildSha256 ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dllPath))).ToLowerInvariant();
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["native_path"] = nativePath,
            ["native_sha256"] = sha,
            ["native_thumbprint"] = thumbprint ?? new string('A', 40),
            ["native_version"] = "0.4.5",
            ["patch_sha256"] = "unused",
            ["requires_signature"] = false,
            ["version"] = "3.0.0-dev.21",
        });
        File.WriteAllText(Path.Combine(modDirectory, "BUILD.json"), json);
    }

    private sealed class StubVerifier(AuthenticodeVerifyResult result) : IAuthenticodeVerifier
    {
        public AuthenticodeVerifyResult Result { get; set; } = result;

        public AuthenticodeVerifyResult Verify(string filePath) => Result;
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
