using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.PluginLua;
using HD2Mimnt.PluginSDK;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Plugins;

/// <summary>
/// 沙箱红队探针：直接跑 <c>samples/plugins/SandboxProbe</c> 里的真实脚本，
/// 用恶意插件的姿势去撞每一条边界。
///
/// 这里刻意用**真实的** <see cref="PluginStorage"/> 而不是内存替身——
/// 存储路径校验只有在真实实现里才存在，用替身测等于自欺欺人。
/// </summary>
public class SandboxProbeTests
{
    private const string ProbePluginId = "com.hd2mimnt.samples.sandbox-probe";

    [Fact]
    public async Task Probe_ContainmentMatrix()
    {
        var sampleDirectory = CopySampleToTemp("SandboxProbe");
        var storageRoot = CreateTempDirectory();

        try
        {
            var storage = CreateGrantedStorage(ProbePluginId, storageRoot);
            var manifestPath = Path.Combine(sampleDirectory, "manifest.json");
            var scriptPath = Path.Combine(sampleDirectory, "main.lua");

            using var runtime = new LuaPluginRuntime(ProbePluginId);
            await runtime.InitializeAsync(scriptPath, manifestPath, new ProbeContext(storage));

            // 七个命令都要在清单里，否则界面上的按钮点了没反应
            foreach (var command in ProbeCommands)
            {
                Assert.True(runtime.Commands.ContainsKey(command), $"清单缺少命令 {command}");
            }

            async Task<PluginResult> Execute(string command) => await runtime.ExecuteCommandAsync(
                command,
                new PluginCommandContext(
                    new CapturingLogger(),
                    storage,
                    new EmptyConfiguration(),
                    "{}",
                    new UnavailableAi()));

            // 1. 环境面清查：七项危险全局全部不可见
            var surface = await Execute("sandbox-surface");
            Assert.True(surface.Success, surface.Error);
            Assert.Equal("probe:surface:7/7", surface.Message);
            Assert.NotNull(surface.Ui);
            Assert.True(surface.Ui!.Blocks.Count >= 2);

            // 2. 越界尝试：宿主必须抛错。沙箱里没有 pcall，脚本自己拦不住任何错误，
            //    所以“命令失败”就是拦截生效的直接证据。
            foreach (var command in new[]
                     {
                         "probe-require-escape",
                         "probe-require-absolute",
                         "probe-storage-traversal",
                         "probe-storage-absolute",
                         "probe-storage-invalid"
                     })
            {
                var result = await Execute(command);
                Assert.False(result.Success, $"{command} 竟然执行成功了，边界失守");
                Assert.False(string.IsNullOrWhiteSpace(result.Error), $"{command} 失败但没有给出原因");
            }

            // 3. 已知缺陷：路径校验是裸前缀匹配，相邻插件目录被放行。
            //    修复后这里应改为 Assert.False(result.Success)。
            var sibling = await Execute("probe-storage-sibling");
            Assert.True(sibling.Success, sibling.Error);
            Assert.Equal("probe:breach", sibling.Message);
        }
        finally
        {
            DeleteDirectory(sampleDirectory);
            DeleteDirectory(storageRoot);
        }
    }

    private static readonly string[] ProbeCommands =
    {
        "sandbox-surface",
        "probe-require-escape",
        "probe-require-absolute",
        "probe-storage-traversal",
        "probe-storage-absolute",
        "probe-storage-invalid",
        "probe-storage-sibling"
    };

    /// <summary>
    /// 已知缺陷（当前行为的特征化用例，修复后应改为断言“拒绝访问”）：
    /// <see cref="PluginStorage"/> 用不带分隔符的裸前缀判断是否越界，
    /// 于是 id 为 <c>com.example</c> 的插件可以通过 <c>../com.example.ai/x</c>
    /// 读写 id 为 <c>com.example.ai</c> 的另一个插件的私有目录。
    /// </summary>
    [Fact]
    public async Task KnownGap_StoragePrefixCollisionReachesSiblingPluginDirectory()
    {
        var storageRoot = CreateTempDirectory();

        try
        {
            var attacker = CreateGrantedStorage("com.example", storageRoot);
            var victimDirectory = Path.Combine(storageRoot, "PluginData", "com.example.ai");
            Directory.CreateDirectory(victimDirectory);
            var victimFile = Path.Combine(victimDirectory, "settings.json");
            await File.WriteAllTextAsync(victimFile, """{"secret":"victim"}""");

            // 攻击者 id 是受害者 id 的前缀，跨目录读写不会被拒绝
            await attacker.WriteFileAsync("../com.example.ai/settings.json", """{"secret":"overwritten"}""");

            Assert.Equal("""{"secret":"overwritten"}""", await File.ReadAllTextAsync(victimFile));
        }
        finally
        {
            DeleteDirectory(storageRoot);
        }
    }

    /// <summary>
    /// 已知缺陷（特征化用例）：<see cref="PluginStorage.FileExists"/> 不做权限检查，
    /// 没有 <c>storage.read</c> 的插件仍可把私有目录当存在性探针用。
    /// </summary>
    [Fact]
    public async Task KnownGap_FileExistsAnswersWithoutStoragePermission()
    {
        var storageRoot = CreateTempDirectory();

        try
        {
            var granted = CreateGrantedStorage("com.example", storageRoot);
            await granted.WriteFileAsync("settings.json", "{}");

            var denied = CreateDeniedStorage("com.example", storageRoot);

            // 读拒绝、但存在性照答：构成了文件枚举的旁路信号
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => denied.ReadFileAsync("settings.json"));
            Assert.True(denied.FileExists("settings.json"));
        }
        finally
        {
            DeleteDirectory(storageRoot);
        }
    }

    // ---- 构造 ----

    private static PluginStorage CreateGrantedStorage(string pluginId, string baseDirectory)
    {
        var service = new Mock<IPluginService>();
        service
            .Setup(s => s.CheckPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new PluginStorage(pluginId, baseDirectory, service.Object);
    }

    private static PluginStorage CreateDeniedStorage(string pluginId, string baseDirectory)
    {
        var service = new Mock<IPluginService>();
        service
            .Setup(s => s.CheckPermissionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return new PluginStorage(pluginId, baseDirectory, service.Object);
    }

    private static string CopySampleToTemp(string sampleName)
    {
        var source = Path.Combine(FindRepositoryRoot(), "samples", "plugins", sampleName);
        Assert.True(Directory.Exists(source), $"找不到示例插件目录：{source}");

        var target = CreateTempDirectory();
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        return target;
    }

    /// <summary>从测试输出目录向上找解决方案文件，避免写死仓库绝对路径。</summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HD2Mimnt.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"从 {AppContext.BaseDirectory} 向上未找到 HD2Mimnt.slnx");
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hd2-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
            // 清理失败不影响断言结果
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- 测试替身 ----

    private sealed class ProbeContext : IPluginContext
    {
        public ProbeContext(IPluginStorage storage) => Storage = storage;

        public IPluginLogger Logger { get; } = new CapturingLogger();

        public IPluginStorage Storage { get; }

        public IPluginModAccess ModAccess { get; } = new EmptyModAccess();

        public IPluginConfiguration Configuration { get; } = new EmptyConfiguration();

        public IPluginAiClient AI { get; } = new UnavailableAi();
    }

    private sealed class CapturingLogger : IPluginLogger
    {
        public List<string> Messages { get; } = new();

        public void Info(string message) => Messages.Add("info:" + message);

        public void Warning(string message) => Messages.Add("warn:" + message);

        public void Error(string message, Exception? exception = null) => Messages.Add("error:" + message);

        public void Debug(string message) => Messages.Add("debug:" + message);
    }

    private sealed class EmptyConfiguration : IPluginConfiguration
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public IEnumerable<string> GetKeys() => _values.Keys;

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class UnavailableAi : IPluginAiClient
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<PluginAiResult> CompleteAsync(
            string prompt,
            PluginAiOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PluginAiResult.Fail("探针不发起 AI 请求"));
    }

    private sealed class EmptyModAccess : IPluginModAccess
    {
        public Task<PluginModInfo?> GetModAsync(Guid modId, CancellationToken cancellationToken = default)
            => Task.FromResult<PluginModInfo?>(null);

        public Task<List<PluginModInfo>> GetAllModsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<PluginModInfo>());

        public Task<List<PluginModInfo>> GetModsByCategoryAsync(string category, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<PluginModInfo>());

        public Task<List<PluginModInfo>> GetModsByTagAsync(string tag, CancellationToken cancellationToken = default)
            => Task.FromResult(new List<PluginModInfo>());
    }
}
