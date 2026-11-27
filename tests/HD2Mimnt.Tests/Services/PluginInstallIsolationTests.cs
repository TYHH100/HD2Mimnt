using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;
using System.Text.Json;
using Xunit;
using PluginManifest = HD2Mimnt.PluginSDK.PluginManifest;

namespace HD2Mimnt.Tests.Services;

public class PluginInstallIsolationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hd2-plugin-isolation-" + Guid.NewGuid().ToString("N"));
    private readonly string _installedRoot;
    private readonly Mock<IPluginRepository> _repository = new();
    private readonly Mock<IPluginPermissionRepository> _permissions = new();
    private readonly Mock<IPluginExecutionRepository> _executions = new();
    private readonly Mock<IPluginHostManager> _hosts = new();
    private readonly PluginService _service;

    public PluginInstallIsolationTests()
    {
        _installedRoot = Path.Combine(_root, "AppData", "InstalledPlugins");
        _service = new PluginService(_repository.Object, _permissions.Object, _executions.Object,
            _hosts.Object, installedPluginsPath: _installedRoot);
    }

    [Fact]
    public async Task Install_CopiesWholePackageIntoIdDirectory_AndSourceCanBeDeleted()
    {
        var source = Path.Combine(_root, "download", "sample");
        var manifestPath = await WriteDotNetPluginAsync(source);
        Directory.CreateDirectory(Path.Combine(source, "assets"));
        await File.WriteAllTextAsync(Path.Combine(source, "assets", "page.html"), "<h1>isolated</h1>");
        Plugin? installed = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
            .Callback<Plugin, CancellationToken>((plugin, _) => installed = plugin)
            .Returns(Task.CompletedTask);

        var result = await _service.InstallPluginAsync(manifestPath);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(installed);
        var target = Path.Combine(_installedRoot, "sample.plugin");
        Assert.Equal(Path.Combine(target, "plugin.json"), installed!.ManifestPath);
        Assert.Equal(Path.Combine(target, "Sample.dll"), installed.AssemblyPath);
        Assert.Equal("<h1>isolated</h1>", await File.ReadAllTextAsync(Path.Combine(target, "assets", "page.html")));
        Assert.Empty(Directory.GetDirectories(_installedRoot, ".installing-*"));

        Directory.Delete(source, recursive: true);
        _repository.Setup(r => r.GetByIdAsync(installed.Id, It.IsAny<CancellationToken>())).ReturnsAsync(installed);
        _hosts.Setup(h => h.StartPluginAsync(installed.Id, installed.AssemblyPath, installed.ManifestPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("started"));

        var start = await _service.StartPluginAsync(installed.Id);

        Assert.True(start.Success, start.Error);
        _hosts.Verify(h => h.StartPluginAsync(installed.Id, installed.AssemblyPath, installed.ManifestPath,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Install_PermissionPersistenceFails_RemovesCopyAndDatabaseRecords()
    {
        var source = Path.Combine(_root, "download");
        var manifestPath = await WriteDotNetPluginAsync(source, permissions: ["mods.read"]);
        _permissions.Setup(p => p.AddAsync(It.IsAny<PluginPermission>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("database full"));

        var result = await _service.InstallPluginAsync(manifestPath);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(Path.Combine(_installedRoot, "sample.plugin")));
        Assert.Empty(Directory.GetFileSystemEntries(_installedRoot));
        _permissions.Verify(p => p.DeleteByPluginIdAsync("sample.plugin", It.IsAny<CancellationToken>()), Times.Once);
        _repository.Verify(r => r.DeleteAsync("sample.plugin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Install_DuplicateId_DoesNotOverwriteExistingCopy()
    {
        var manifestPath = await WriteDotNetPluginAsync(Path.Combine(_root, "download"));
        _repository.Setup(r => r.ExistsAsync("sample.plugin", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var target = Path.Combine(_installedRoot, "sample.plugin");
        Directory.CreateDirectory(target);
        var marker = Path.Combine(target, "keep.txt");
        await File.WriteAllTextAsync(marker, "keep");

        var result = await _service.InstallPluginAsync(manifestPath);

        Assert.False(result.Success);
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
    }

    [Fact]
    public async Task Uninstall_ManagedPlugin_DeletesOnlyItsInstallDirectory()
    {
        var source = Path.Combine(_root, "download");
        var manifestPath = await WriteDotNetPluginAsync(source);
        Plugin? installed = null;
        _repository.Setup(r => r.AddAsync(It.IsAny<Plugin>(), It.IsAny<CancellationToken>()))
            .Callback<Plugin, CancellationToken>((plugin, _) => installed = plugin)
            .Returns(Task.CompletedTask);
        Assert.True((await _service.InstallPluginAsync(manifestPath)).Success);
        _repository.Setup(r => r.GetByIdAsync(installed!.Id, It.IsAny<CancellationToken>())).ReturnsAsync(installed);
        var sibling = Path.Combine(_installedRoot, "other.plugin", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sibling)!);
        await File.WriteAllTextAsync(sibling, "keep");

        var result = await _service.UninstallPluginAsync(installed!.Id);

        Assert.True(result.Success, result.Error);
        Assert.False(Directory.Exists(Path.Combine(_installedRoot, installed.Id)));
        Assert.Equal("keep", await File.ReadAllTextAsync(sibling));
        Assert.True(File.Exists(manifestPath));
    }

    [Fact]
    public async Task Uninstall_LegacyExternalPlugin_LeavesSourceFilesUntouched()
    {
        var source = Path.Combine(_root, "legacy");
        var manifestPath = await WriteDotNetPluginAsync(source);
        var plugin = new Plugin { Id = "sample.plugin", ManifestPath = manifestPath, AssemblyPath = Path.Combine(source, "Sample.dll") };
        _repository.Setup(r => r.GetByIdAsync(plugin.Id, It.IsAny<CancellationToken>())).ReturnsAsync(plugin);

        var result = await _service.UninstallPluginAsync(plugin.Id);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(manifestPath));
        Assert.True(File.Exists(plugin.AssemblyPath));
    }

    private static async Task<string> WriteDotNetPluginAsync(string directory, string[]? permissions = null)
    {
        Directory.CreateDirectory(directory);
        var manifest = new PluginManifest
        {
            Id = "sample.plugin",
            Name = "Sample",
            Version = "1.0.0",
            Assembly = "Sample.dll",
            EntryType = "Sample.Entry",
            RequestedPermissions = permissions?.ToList() ?? []
        };
        var manifestPath = Path.Combine(directory, "plugin.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(Path.Combine(directory, "Sample.dll"), "assembly");
        return manifestPath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
