using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.Services;

/// <summary>
/// 脚本型插件在安装与校验阶段的差异：入口是 .lua 而不是 .dll，
/// EntryType 不再必需，混合插件声明的程序集必须真实存在。
/// </summary>
public class PluginScriptInstallTests
{
    private readonly Mock<IPluginRepository> _pluginRepository = new();
    private readonly Mock<IPluginPermissionRepository> _permissionRepository = new();
    private readonly Mock<IPluginExecutionRepository> _executionRepository = new();
    private readonly Mock<IPluginHostManager> _hostManager = new();
    private readonly PluginService _service;
    private readonly string _directory;

    public PluginScriptInstallTests()
    {
        _service = new PluginService(
            _pluginRepository.Object,
            _permissionRepository.Object,
            _executionRepository.Object,
            _hostManager.Object);

        _directory = Path.Combine(Path.GetTempPath(), "hd2-lua-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Validate_LuaWithoutScript_ReportsError()
    {
        var manifestPath = WriteManifest("""
            {
              "id": "test.lua.no-script",
              "name": "缺脚本",
              "version": "1.0.0",
              "kind": "lua",
              "apiVersion": "1.3.0"
            }
            """);

        var result = await _service.ValidateManifestAsync(manifestPath);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Script"));
    }

    [Fact]
    public async Task Validate_LuaWithScriptAndNoAssembly_IsValid()
    {
        var manifestPath = WriteManifest("""
            {
              "id": "test.lua.ok",
              "name": "纯脚本",
              "version": "1.0.0",
              "kind": "lua",
              "script": "main.lua",
              "apiVersion": "1.3.0"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(_directory, "main.lua"), "return 1");

        var result = await _service.ValidateManifestAsync(manifestPath);

        Assert.True(result.IsValid, string.Join(";", result.Errors));
        // 编译型字段不再是必填项
        Assert.DoesNotContain(result.Errors, e => e.Contains("Assembly is required"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("EntryType"));
    }

    [Fact]
    public async Task Validate_LuaScriptMissingFile_ReportsError()
    {
        var manifestPath = WriteManifest("""
            {
              "id": "test.lua.missing",
              "name": "脚本不存在",
              "version": "1.0.0",
              "kind": "lua",
              "script": "main.lua",
              "apiVersion": "1.3.0"
            }
            """);

        var result = await _service.ValidateManifestAsync(manifestPath);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("入口脚本不存在"));
    }

    [Fact]
    public async Task Validate_HybridWithMissingAssembly_ReportsError()
    {
        var manifestPath = WriteManifest("""
            {
              "id": "test.lua.hybrid",
              "name": "混合",
              "version": "1.0.0",
              "kind": "lua",
              "script": "main.lua",
              "assembly": "Missing.dll",
              "apiVersion": "1.3.0"
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(_directory, "main.lua"), "return 1");

        var result = await _service.ValidateManifestAsync(manifestPath);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Missing.dll"));
    }

    [Fact]
    public async Task Install_LuaPlugin_RecordsScriptAsEntryFile()
    {
        var manifestPath = WriteManifest("""
            {
              "id": "test.lua.install",
              "name": "可安装脚本插件",
              "version": "1.0.0",
              "kind": "lua",
              "script": "main.lua",
              "apiVersion": "1.3.0"
            }
            """);
        var scriptPath = Path.Combine(_directory, "main.lua");
        await File.WriteAllTextAsync(scriptPath, "return 1");

        Plugin? installed = null;
        _pluginRepository.Setup(r => r.ExistsAsync("test.lua.install", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _pluginRepository
            .Setup(r => r.AddAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
            .Callback<Plugin, CancellationToken>((plugin, _) => installed = plugin)
            .Returns(Task.CompletedTask);

        var result = await _service.InstallPluginAsync(manifestPath);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(installed);
        Assert.Equal(scriptPath, installed!.AssemblyPath);
        // 完整性锚点落在脚本上，脚本被改过就拒绝启动
        Assert.False(string.IsNullOrWhiteSpace(installed.AssemblySha256));
    }

    [Fact]
    public async Task Install_DotNetPluginWithoutAssembly_StillRejected()
    {
        var manifestPath = WriteManifest("""
            {
              "id": "test.dll.no-assembly",
              "name": "缺程序集",
              "version": "1.0.0",
              "apiVersion": "1.3.0"
            }
            """);

        var result = await _service.ValidateManifestAsync(manifestPath);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Assembly is required"));
    }

    private string WriteManifest(string json)
    {
        var path = Path.Combine(_directory, "manifest.json");
        File.WriteAllText(path, json);
        return path;
    }
}
