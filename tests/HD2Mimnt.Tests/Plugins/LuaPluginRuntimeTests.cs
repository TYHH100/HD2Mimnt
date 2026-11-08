using HD2Mimnt.PluginLua;
using HD2Mimnt.PluginSDK;

namespace HD2Mimnt.Tests.Plugins;

/// <summary>
/// Lua 插件运行时：加载、命令分发、声明式界面转换、沙箱边界与混合调用。
/// 这些用例直接跑真实解释器，不做 mock——脚本能否工作只能靠真跑一遍来验证。
/// </summary>
public class LuaPluginRuntimeTests
{
    private const string BasicManifest = """
        {
          "id": "test.lua.plugin",
          "name": "Lua 测试插件",
          "version": "1.0.0",
          "author": "tests",
          "apiVersion": "1.3.0",
          "kind": "lua",
          "script": "main.lua",
          "commands": [
            { "name": "report", "displayName": "报告", "placement": "modContextMenu" }
          ]
        }
        """;

    [Fact]
    public async Task Load_RegistersCommandsDeclaredInManifest()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              return hd2.result.ok("ok")
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        Assert.True(runtime.Commands.ContainsKey("report"));
        Assert.Equal("report", runtime.Commands["report"].Descriptor.Name);
        Assert.Empty(runtime.LoadErrors);
    }

    [Fact]
    public async Task Execute_ConvertsScriptTableToUiUpdate()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              return hd2.result.ok(nil, hd2.ui.update("replace", {
                hd2.ui.markdown("## 标题\n\n正文"),
                hd2.ui.divider(),
                hd2.ui.facts({ { label = "键", value = "值" } }),
              }))
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration(), "{}"));

        Assert.True(result.Success);
        Assert.NotNull(result.Ui);
        Assert.Equal(PluginUiUpdateModes.Replace, result.Ui!.Mode);
        Assert.Equal(3, result.Ui.Blocks.Count);
        Assert.Equal(PluginUiBlockTypes.Markdown, result.Ui.Blocks[0].Type);
        Assert.Equal("## 标题\n\n正文", result.Ui.Blocks[0].Text);
        Assert.Equal(PluginUiBlockTypes.Facts, result.Ui.Blocks[2].Type);
        Assert.Equal("键", result.Ui.Blocks[2].Facts[0].Label);
    }

    [Fact]
    public async Task Execute_PassesArgumentsAsLuaTable()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              local mod = args.mod or {}
              return hd2.result.ok("name=" .. tostring(mod.name) .. ";count=" .. tostring(#args.list))
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(),
            new MemoryStorage(directory),
            new MemoryConfiguration(),
            """{ "mod": { "name": "测试模组" }, "list": [1, 2, 3] }"""));

        Assert.True(result.Success);
        Assert.Equal("name=测试模组;count=3", result.Message);
    }

    [Fact]
    public async Task Execute_ScriptFailureBecomesFailedResult()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              error("脚本内部出错")
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.False(result.Success);
        Assert.Contains("脚本内部出错", result.Error);
    }

    [Fact]
    public async Task Sandbox_HidesIoOsAndLoaders()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              local forbidden = { "io", "os", "debug", "dofile", "loadfile", "require", "package" }
              local leaked = {}
              for _, name in ipairs(forbidden) do
                if _G[name] ~= nil then table.insert(leaked, name) end
              end
              if #leaked > 0 then
                return hd2.result.fail("沙箱泄露：" .. table.concat(leaked, ","))
              end
              return hd2.result.ok("沙箱干净")
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.True(result.Success, result.Error);
        Assert.Equal("沙箱干净", result.Message);
    }

    [Fact]
    public async Task Require_RejectsPathEscape()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              return hd2.require("../outside")
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.False(result.Success);
        Assert.Contains("模块名非法", result.Error);
    }

    [Fact]
    public async Task Require_LoadsModuleInsidePluginDirectory()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            local util = hd2.require("util")
            function on_command(name, args)
              return hd2.result.ok(util.greet("世界"))
            end
            """);
        await File.WriteAllTextAsync(Path.Combine(directory, "util.lua"), """
            local M = {}
            function M.greet(who) return "你好，" .. who end
            return M
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.True(result.Success, result.Error);
        Assert.Equal("你好，世界", result.Message);
    }

    [Fact]
    public async Task Execute_UsesStorageAcrossCalls()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              local count = (tonumber(hd2.storage.read("hits")) or 0) + 1
              hd2.storage.write("hits", tostring(count))
              return hd2.result.ok(tostring(count))
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin");
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var storage = new MemoryStorage(directory);
        var context = new PluginCommandContext(new CapturingLogger(), storage, new MemoryConfiguration());

        Assert.Equal("1", (await runtime.ExecuteCommandAsync("report", context)).Message);
        Assert.Equal("2", (await runtime.ExecuteCommandAsync("report", context)).Message);
        Assert.Equal("2", storage.Files["hits"]);
    }

    [Fact]
    public async Task Hybrid_CallsDllCommandAndMergesUi()
    {
        var manifest = BasicManifest.Replace(
            "\"script\": \"main.lua\",",
            "\"script\": \"main.lua\",\n  \"assembly\": \"Tools.dll\",");

        var directory = CreatePluginDirectory(manifest, """
            function on_command(name, args)
              local result, err = hd2.dll.call("digest", { text = "hello" })
              if result == nil then return hd2.result.fail(tostring(err)) end

              local blocks = { hd2.ui.markdown("脚本块") }
              for _, block in ipairs(result.ui.blocks) do table.insert(blocks, block) end
              return hd2.result.ok(result.message, hd2.ui.update("replace", blocks))
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin", new StubDllBridge());
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        Assert.True(runtime.Commands.ContainsKey("report"));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.True(result.Success, result.Error);
        Assert.Equal("来自 DLL", result.Message);
        Assert.NotNull(result.Ui);
        Assert.Equal(2, result.Ui!.Blocks.Count);
        Assert.Equal("脚本块", result.Ui.Blocks[0].Text);
        Assert.Equal(PluginUiBlockTypes.Facts, result.Ui.Blocks[1].Type);
    }

    [Fact]
    public async Task Hybrid_FallsBackToDllWhenScriptHasNoHandler()
    {
        var manifest = BasicManifest.Replace(
            "\"script\": \"main.lua\",",
            "\"script\": \"main.lua\",\n  \"assembly\": \"Tools.dll\",");

        // 脚本完全没有 on_command：命令应回落到 DLL
        var directory = CreatePluginDirectory(manifest, """
            hd2.log.info("纯 DLL 处理")
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin", new StubDllBridge());
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.True(result.Success, result.Error);
        Assert.Equal("来自 DLL", result.Message);
    }

    [Fact]
    public async Task Execute_TimesOutAndFlagsRunaway()
    {
        var directory = CreatePluginDirectory(BasicManifest, """
            function on_command(name, args)
              local total = 0
              for i = 1, 20000000 do total = total + i end
              return hd2.result.ok(tostring(total))
            end
            """);

        using var runtime = new LuaPluginRuntime("test.lua.plugin")
        {
            CommandTimeout = TimeSpan.FromMilliseconds(5)
        };
        await runtime.InitializeAsync(
            Path.Combine(directory, "main.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory));

        var result = await runtime.ExecuteCommandAsync("report", new PluginCommandContext(
            new CapturingLogger(), new MemoryStorage(directory), new MemoryConfiguration()));

        Assert.False(result.Success);
        Assert.Contains("超过", result.Error);
        Assert.True(runtime.HasRunawayExecution);
    }

    [Fact]
    public async Task Load_RejectsMissingScript()
    {
        var directory = CreatePluginDirectory(BasicManifest, "return 1");

        using var runtime = new LuaPluginRuntime("test.lua.plugin");

        await Assert.ThrowsAsync<FileNotFoundException>(() => runtime.InitializeAsync(
            Path.Combine(directory, "missing.lua"),
            Path.Combine(directory, "manifest.json"),
            new FakePluginContext(directory)));
    }

    private static string CreatePluginDirectory(string manifestJson, string luaSource)
    {
        var directory = Path.Combine(Path.GetTempPath(), "hd2-lua-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(directory, "main.lua"), luaSource);
        return directory;
    }

    // ---- 测试替身 ----

    private sealed class FakePluginContext : IPluginContext
    {
        public FakePluginContext(string dataDirectory)
        {
            Storage = new MemoryStorage(dataDirectory);
        }

        public IPluginLogger Logger { get; } = new CapturingLogger();

        public IPluginStorage Storage { get; }

        public IPluginModAccess ModAccess { get; } = new EmptyModAccess();

        public IPluginConfiguration Configuration { get; } = new MemoryConfiguration();

        public IPluginAiClient AI { get; } = new EchoAi();
    }

    private sealed class CapturingLogger : IPluginLogger
    {
        public List<string> Messages { get; } = new();

        public void Info(string message) => Messages.Add("info:" + message);

        public void Warning(string message) => Messages.Add("warn:" + message);

        public void Error(string message, Exception? exception = null) => Messages.Add("error:" + message);

        public void Debug(string message) => Messages.Add("debug:" + message);
    }

    private sealed class MemoryStorage : IPluginStorage
    {
        public MemoryStorage(string directory) => Directory = directory;

        public string Directory { get; }

        public Dictionary<string, string> Files { get; } = new();

        public string GetDataDirectory() => Directory;

        public Task<string> ReadFileAsync(string fileName, CancellationToken cancellationToken = default)
            => Task.FromResult(Files.TryGetValue(fileName, out var content) ? content : string.Empty);

        public Task WriteFileAsync(string fileName, string content, CancellationToken cancellationToken = default)
        {
            Files[fileName] = content;
            return Task.CompletedTask;
        }

        public bool FileExists(string fileName) => Files.ContainsKey(fileName);

        public Task DeleteFileAsync(string fileName, CancellationToken cancellationToken = default)
        {
            Files.Remove(fileName);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryConfiguration : IPluginConfiguration
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

    private sealed class EchoAi : IPluginAiClient
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<PluginAiResult> CompleteAsync(
            string prompt,
            PluginAiOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(PluginAiResult.Ok("回声：" + prompt));
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

    /// <summary>模拟混合插件的 DLL 那一半。</summary>
    private sealed class StubDllBridge : ILuaDllBridge
    {
        private readonly IPluginCommand _command = new StubDigestCommand();

        public bool IsAvailable => true;

        public string? LoadError => null;

        public IReadOnlyCollection<string> CommandNames => new[] { "digest" };

        public Task<bool> InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public bool TryGetCommand(string name, out IPluginCommand command)
        {
            // 同时响应脚本显式调用的 digest 与命令回落用的 report
            if (string.Equals(name, "digest", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "report", StringComparison.OrdinalIgnoreCase))
            {
                command = _command;
                return true;
            }

            command = null!;
            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubDigestCommand : IPluginCommand
    {
        public PluginCommandDescriptor Descriptor => new() { Name = "digest", DisplayName = "指纹" };

        public Task<PluginResult> ExecuteAsync(PluginCommandContext context, CancellationToken cancellationToken = default)
        {
            var ui = PluginUiUpdate.Replace(new PluginUiBlock
            {
                Type = PluginUiBlockTypes.Facts,
                Facts = new List<PluginUiFact> { new() { Label = "长度", Value = (context.Arguments?.Length ?? 0).ToString() } }
            });
            return Task.FromResult(PluginResult.Ok("来自 DLL", ui));
        }
    }
}
