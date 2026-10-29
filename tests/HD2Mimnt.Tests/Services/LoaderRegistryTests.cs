using System.Buffers.Binary;
using System.Text;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Analysis;

namespace HD2Mimnt.Tests.Services;

/// <summary>加载器识别配置：随应用分发的默认配置、外部覆盖的新加载器零代码适配与坏配置回退。
/// 测试程序集全局禁用并行，静态快照的覆盖与恢复安全。</summary>
public sealed class LoaderRegistryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-LoaderReg-{Guid.NewGuid():N}");

    public LoaderRegistryTests() => LoaderRegistry.ResetToDefault();

    [Fact]
    public void EmbeddedCatalog_ShouldDefineDefaultLoadersPrioritiesAndMutex()
    {
        var snapshot = LoaderRegistry.Current;
        Assert.Null(LoaderRegistry.InitializationError);
        Assert.Equal(6, snapshot.RuleVersion);
        Assert.Equal(5, snapshot.Loaders.Length);
        Assert.Equal(BslModRole.Loader, snapshot.DefinitionOf(BslModDetector.LoaderGuid)?.Role);
        Assert.Equal(BslModRole.MdlLoader, snapshot.DefinitionOf(BslModDetector.MdlGuid)?.Role);
        Assert.Equal(BslModRole.BmlLoader, snapshot.DefinitionOf(BslModDetector.BootModLoaderGuid)?.Role);
        Assert.Equal(BslModRole.CslLoader, snapshot.DefinitionOf(BslModDetector.CslGuid)?.Role);
        Assert.Equal(BslModRole.SmoothBoot, snapshot.DefinitionOf(BslModDetector.SmoothBootGuid)?.Role);
        Assert.Equal("BSL / MDL / Boot Mod Loader", snapshot.LoaderNames);
        Assert.Equal("Chef Shared Loader", snapshot.GuestLoaderNames);
        Assert.Equal("Boot Mod Loader", snapshot.HostLoaderNames);
        Assert.Equal(0, snapshot.PriorityRank(BslModRole.None));
        Assert.Equal(1, snapshot.PriorityRank(BslModRole.Dependency));
        Assert.Equal(2, snapshot.PriorityRank(BslModRole.CslLoader));
        Assert.Equal(3, snapshot.PriorityRank(BslModRole.Loader));
        Assert.Equal(3, snapshot.PriorityRank(BslModRole.MdlLoader));
        Assert.Equal(3, snapshot.PriorityRank(BslModRole.BmlLoader));
        Assert.Equal(4, snapshot.PriorityRank(BslModRole.SmoothBoot));
        Assert.True(snapshot.IsPrimary(BslModRole.Loader));
        Assert.True(snapshot.IsPrimary(BslModRole.BmlLoader));
        Assert.False(snapshot.IsPrimary(BslModRole.CslLoader));
        Assert.True(snapshot.IsGuest(BslModRole.CslLoader));
        Assert.False(snapshot.IsGuest(BslModRole.Loader));
        Assert.True(snapshot.CanHost(BslModRole.BmlLoader, BslModRole.CslLoader));
        Assert.False(snapshot.CanHost(BslModRole.Loader, BslModRole.CslLoader));
        Assert.False(snapshot.IsPrimary(BslModRole.SmoothBoot));
        Assert.False(snapshot.IsLoader(BslModRole.SmoothBoot));
        // 识别特征与配置一致：MDL 入口、BML/BSL 启动标记、BSL 注册模块与依赖声明。
        Assert.Contains(BslModDetector.ResourceHash("mods/mdl/runtime"), snapshot.ByEntryHash.Keys);
        Assert.Contains(BslModDetector.ResourceHash("mods/codex/smoothboot"), snapshot.ByEntryHash.Keys);
        Assert.Contains(BslModDetector.ResourceHash("mods/chef_shared_loader/core_1"), snapshot.ByEntryHash.Keys);
        Assert.Contains(BslModDetector.ResourceHash("boot"), snapshot.ByStartupHash.Keys);
        Assert.Contains(BslModDetector.ResourceHash("core/wwise/lua/wwise_flow_callbacks"), snapshot.ByStartupHash.Keys);
        Assert.Contains(BslModDetector.ResourceHash("mods/cowboybingus/vanilla_plus_megapack"), snapshot.ByRegisteredModuleHash.Keys);
        Assert.Contains("BSL", snapshot.DependencyNames);
        Assert.Contains("Chef Shared Loader", snapshot.DependencyNames);
        Assert.Contains("shared_loader_api", snapshot.DependencyKeys);
    }

    [Fact]
    public async Task ExternalOverride_ShouldAdaptNewLoaderWithoutCodeChanges()
    {
        Directory.CreateDirectory(_root);
        const string guid = "11111111-2222-3333-4444-555555555555";
        File.WriteAllText(Path.Combine(_root, "loaders.json"), $$"""
            {
              "ruleVersion": 6,
              "loaders": [ {
                "id": "future",
                "displayName": "FutureLoader",
                "role": "loader",
                "priority": 2,
                "mutexGroup": "primary",
                "guids": [ "{{guid}}" ],
                "entryResources": [ "mods/future/runtime" ]
              } ]
            }
            """);
        LoaderRegistry.Initialize(_root);
        try
        {
            Assert.Null(LoaderRegistry.InitializationError);
            Assert.Equal(6, LoaderRegistry.Current.RuleVersion);
            Assert.Equal(BslModRole.Loader, LoaderRegistry.Current.DefinitionOf(Guid.Parse(guid))?.Role);
            Assert.Equal("FutureLoader", LoaderRegistry.Current.LoaderNames);
            // GUID 缺失时新入口资源仍可识别；规则版本提升驱动旧缓存重扫。
            var mod = new Mod { Name = "未来加载器", Path = WriteEntryPackage("mods/future/runtime") };
            Assert.Equal(BslModRole.Loader, await BslModDetector.DetectAsync(mod));
            Assert.Equal(6, BslModDetector.DetectionVersion);
        }
        finally
        {
            LoaderRegistry.ResetToDefault();
        }
    }

    [Fact]
    public async Task ExternalOverride_ShouldReconfigureStartupMarkersForExistingSlot()
    {
        Directory.CreateDirectory(_root);
        // 同一 loader 槽位的新加载器：无 GUID，仅凭 boot 资源正文标记识别。
        File.WriteAllText(Path.Combine(_root, "loaders.json"), """
            {
              "ruleVersion": 7,
              "loaders": [ {
                "id": "nextboot",
                "displayName": "NextBoot",
                "role": "loader",
                "priority": 2,
                "mutexGroup": "primary",
                "startupChecks": [ { "resource": "boot", "markers": [ "next-boot-loader", "nextboot.log" ] } ]
              } ]
            }
            """);
        LoaderRegistry.Initialize(_root);
        try
        {
            var mod = new Mod { Name = "下一世代加载器", Path = WriteEntryPackage("boot", "next-boot-loader __next_boot nextboot.log") };
            Assert.Equal(BslModRole.Loader, await BslModDetector.DetectAsync(mod));
            // 标记不全的普通 boot 替换不能命中，落回普通模组。
            var plain = new Mod { Name = "普通 boot 替换", Path = WriteEntryPackage("boot", "自己的启动脚本") };
            Assert.Equal(BslModRole.None, await BslModDetector.DetectAsync(plain));
        }
        finally
        {
            LoaderRegistry.ResetToDefault();
        }
    }

    [Fact]
    public void InvalidOverride_ShouldFallBackToEmbeddedDefaultsAndRecordReason()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "loaders.json"), "{ \"ruleVersion\": 7 }");
        LoaderRegistry.Initialize(_root);
        try
        {
            Assert.NotNull(LoaderRegistry.InitializationError);
            // 解析失败回退分发默认，识别与策略不中断。
            Assert.Equal(6, LoaderRegistry.Current.RuleVersion);
            Assert.Equal(BslModRole.Loader, LoaderRegistry.Current.DefinitionOf(BslModDetector.LoaderGuid)?.Role);
        }
        finally
        {
            LoaderRegistry.ResetToDefault();
        }
    }

    [Fact]
    public async Task ChefSharedLoader_ShouldBeIdentifiedByGuidOrCoreEntryWithoutFlaggingStubCarriers()
    {
        const string cslGuid = "2d6f0b8e-91c4-4a37-b5e2-7c0a94f13d58";
        const string core = "-- Chef Shared Loader - its core (mods/chef_shared_loader/core_1) --@@LOADER@@";
        // 官方包：清单 GUID 快速路径。
        var official = new Mod { Name = "Chef Shared Loader 1.2.0", Path = WriteEntryPackage("mods/chef_shared_loader/core_1", core) };
        official.Manifest = new ModManifest { Guid = Guid.Parse(cslGuid) };
        Assert.Equal(BslModRole.CslLoader, await BslModDetector.DetectAsync(official));
        // 重打包丢失清单时，独有核心资源仍可识别。
        var repacked = new Mod { Name = "Chef Shared Loader", Path = WriteEntryPackage("mods/chef_shared_loader/core_1", core) };
        Assert.Equal(BslModRole.CslLoader, await BslModDetector.DetectAsync(repacked));
        // 官方允许任意模组原样携带 stub 作为自己的 boot：携带者不是加载器本体。
        var stubCarrier = new Mod
        {
            Name = "携带 stub 的模组",
            Path = WriteEntryPackage("boot", "-- Chef Shared Loader start-up stub, version 1 --@@LOADER@@\nlocal core = 'mods/chef_shared_loader/core_1'")
        };
        Assert.Equal(BslModRole.None, await BslModDetector.DetectAsync(stubCarrier));
        // 描述声明依赖 Chef Shared Loader 的模组落依赖槽位。
        var dependent = new Mod { Name = "CSL 模组", Path = WriteEntryPackage("mods/example/hello", "print('hello')") };
        dependent.Manifest = new ModManifest { Description = "Needs Chef Shared Loader 1.0.0 or newer." };
        Assert.Equal(BslModRole.Dependency, await BslModDetector.DetectAsync(dependent));
    }

    [Fact]
    public void MissingOverride_ShouldKeepEmbeddedDefaultsWithoutError()
    {
        Directory.CreateDirectory(_root);
        LoaderRegistry.Initialize(_root);
        try
        {
            Assert.Null(LoaderRegistry.InitializationError);
            Assert.Equal(6, LoaderRegistry.Current.RuleVersion);
        }
        finally
        {
            LoaderRegistry.ResetToDefault();
        }
    }

    /// <summary>构造含单个 Lua 资源的补丁包，资源正文为 <paramref name="script"/>。</summary>
    private static string WriteEntryPackage(string resourceName, string script = "")
    {
        var path = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-LoaderPkg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var body = Encoding.UTF8.GetBytes(script);
        var bytes = new byte[184 + 8 + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0xF0000011);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(104), BslModDetector.ResourceHash(resourceName));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), 0xa14e8dfa2cd117e2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), 184);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(160), (uint)(body.Length + 8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(184), (uint)body.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(188), 2);
        body.CopyTo(bytes, 192);
        File.WriteAllBytes(Path.Combine(path, "9ba626afa44a3aa3.patch_0"), bytes);
        return path;
    }

    public void Dispose()
    {
        LoaderRegistry.ResetToDefault();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        // 包目录以模组库模式命名，逐个清理。
        foreach (var directory in Directory.GetDirectories(Path.GetTempPath(), "HD2Mimnt-LoaderPkg-*"))
            try { Directory.Delete(directory, true); } catch (IOException) { }
    }
}
