using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;
using System.Text.Json;
using Xunit;
using PluginManifest = HD2Mimnt.PluginSDK.PluginManifest;
using PluginPermissionMode = HD2Mimnt.PluginSDK.PluginPermissionMode;

namespace HD2Mimnt.Tests.Services;

public class PluginServiceTests
{
    private readonly Mock<IPluginRepository> _mockPluginRepo;
    private readonly Mock<IPluginPermissionRepository> _mockPermissionRepo;
    private readonly Mock<IPluginExecutionRepository> _mockExecutionRepo;
    private readonly Mock<IPluginHostManager> _mockHostManager;
    private readonly PluginService _service;
    private readonly string _tempManifestPath;

    public PluginServiceTests()
    {
        _mockPluginRepo = new Mock<IPluginRepository>();
        _mockPermissionRepo = new Mock<IPluginPermissionRepository>();
        _mockExecutionRepo = new Mock<IPluginExecutionRepository>();
        _mockHostManager = new Mock<IPluginHostManager>();

        _service = new PluginService(
            _mockPluginRepo.Object,
            _mockPermissionRepo.Object,
            _mockExecutionRepo.Object,
            _mockHostManager.Object);

        // Create a temporary manifest for testing
        _tempManifestPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "plugin.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_tempManifestPath)!);
    }

    [Fact]
    public async Task InstallPluginAsync_ValidManifest_Success()
    {
        // Arrange
        var manifest = new PluginManifest
        {
            Id = "test.plugin",
            Name = "Test Plugin",
            Version = "1.0.0",
            Author = "Test Author",
            Description = "Test Description",
            Assembly = "TestPlugin.dll",
            EntryType = "TestPlugin.Entry",
            PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = new List<string> { "mods.read" }
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(_tempManifestPath, manifestJson);

        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "TestPlugin.dll");
        await File.WriteAllTextAsync(assemblyPath, "dummy assembly");

        _mockPluginRepo.Setup(r => r.ExistsAsync(manifest.Id, default)).ReturnsAsync(false);
        _mockPluginRepo.Setup(r => r.AddAsync(It.IsAny<Plugin>(), default)).Returns(Task.CompletedTask);
        _mockPermissionRepo.Setup(r => r.AddAsync(It.IsAny<PluginPermission>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.InstallPluginAsync(_tempManifestPath);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Plugin installed successfully", result.Message);
        _mockPluginRepo.Verify(r => r.AddAsync(It.IsAny<Plugin>(), default), Times.Once);
        _mockPermissionRepo.Verify(r => r.AddAsync(It.IsAny<PluginPermission>(), default), Times.Once);
    }

    [Fact]
    public async Task InstallPluginAsync_ManifestNotFound_Failure()
    {
        // Arrange
        var nonExistentPath = Path.Combine(Path.GetTempPath(), "nonexistent.json");

        // Act
        var result = await _service.InstallPluginAsync(nonExistentPath);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Manifest file not found", result.Error);
    }

    [Fact]
    public async Task InstallPluginAsync_PluginAlreadyInstalled_Failure()
    {
        // Arrange
        var manifest = new PluginManifest
        {
            Id = "test.plugin",
            Name = "Test Plugin",
            Version = "1.0.0",
            Assembly = "TestPlugin.dll",
            EntryType = "TestPlugin.Entry"
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(_tempManifestPath, manifestJson);

        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "TestPlugin.dll");
        await File.WriteAllTextAsync(assemblyPath, "dummy assembly");

        _mockPluginRepo.Setup(r => r.ExistsAsync(manifest.Id, default)).ReturnsAsync(true);

        // Act
        var result = await _service.InstallPluginAsync(_tempManifestPath);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Plugin already installed", result.Error);
    }

    [Fact]
    public async Task UninstallPluginAsync_PluginExists_Success()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            Enabled = true
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockHostManager.Setup(h => h.StopPluginAsync(pluginId, default)).Returns(Task.CompletedTask);
        _mockPermissionRepo.Setup(r => r.DeleteByPluginIdAsync(pluginId, default)).Returns(Task.CompletedTask);
        _mockPluginRepo.Setup(r => r.DeleteAsync(pluginId, default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.UninstallPluginAsync(pluginId);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Plugin uninstalled successfully", result.Message);
        _mockHostManager.Verify(h => h.StopPluginAsync(pluginId, default), Times.Once);
        _mockPermissionRepo.Verify(r => r.DeleteByPluginIdAsync(pluginId, default), Times.Once);
        _mockPluginRepo.Verify(r => r.DeleteAsync(pluginId, default), Times.Once);
    }

    [Fact]
    public async Task UninstallPluginAsync_PluginNotFound_Failure()
    {
        // Arrange
        var pluginId = "nonexistent.plugin";
        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync((Plugin?)null);

        // Act
        var result = await _service.UninstallPluginAsync(pluginId);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Plugin not found", result.Error);
    }

    [Fact]
    public async Task EnablePluginAsync_PluginDisabled_Success()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            Enabled = false
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPluginRepo.Setup(r => r.UpdateAsync(It.IsAny<Plugin>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.EnablePluginAsync(pluginId);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Plugin enabled successfully", result.Message);
        Assert.True(plugin.Enabled);
        _mockPluginRepo.Verify(r => r.UpdateAsync(It.IsAny<Plugin>(), default), Times.Once);
    }

    [Fact]
    public async Task DisablePluginAsync_PluginEnabled_Success()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            Enabled = true
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockHostManager.Setup(h => h.StopPluginAsync(pluginId, default)).Returns(Task.CompletedTask);
        _mockPluginRepo.Setup(r => r.UpdateAsync(It.IsAny<Plugin>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.DisablePluginAsync(pluginId);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("Plugin disabled successfully", result.Message);
        Assert.False(plugin.Enabled);
        _mockHostManager.Verify(h => h.StopPluginAsync(pluginId, default), Times.Once);
        _mockPluginRepo.Verify(r => r.UpdateAsync(It.IsAny<Plugin>(), default), Times.Once);
    }

    [Fact]
    public async Task CheckPermissionAsync_FullAccessMode_ReturnsTrue()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            PermissionMode = "FullAccess"
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);

        // Act
        var result = await _service.CheckPermissionAsync(pluginId, "any.permission");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CheckPermissionAsync_SandboxModeWithGrantedPermission_ReturnsTrue()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            PermissionMode = "Sandbox"
        };

        var permission = new PluginPermission
        {
            Id = Guid.NewGuid().ToString(),
            PluginId = pluginId,
            Permission = "mods.read",
            Granted = true
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPermissionRepo.Setup(r => r.GetByPluginIdAsync(pluginId, default)).ReturnsAsync(new List<PluginPermission> { permission });

        // Act
        var result = await _service.CheckPermissionAsync(pluginId, "mods.read");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task CheckPermissionAsync_SandboxModeWithDeniedPermission_ReturnsFalse()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            PermissionMode = "Sandbox"
        };

        var permission = new PluginPermission
        {
            Id = Guid.NewGuid().ToString(),
            PluginId = pluginId,
            Permission = "mods.read",
            Granted = false
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPermissionRepo.Setup(r => r.GetByPluginIdAsync(pluginId, default)).ReturnsAsync(new List<PluginPermission> { permission });

        // Act
        var result = await _service.CheckPermissionAsync(pluginId, "mods.read");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GrantPermissionAsync_Success()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin { Id = pluginId, Name = "Test Plugin" };
        var permission = new PluginPermission
        {
            Id = Guid.NewGuid().ToString(),
            PluginId = pluginId,
            Permission = "mods.write",
            Granted = false
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPermissionRepo.Setup(r => r.GetByPluginIdAsync(pluginId, default)).ReturnsAsync(new List<PluginPermission> { permission });
        _mockPermissionRepo.Setup(r => r.UpdateAsync(It.IsAny<PluginPermission>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.GrantPermissionAsync(pluginId, "mods.write");

        // Assert
        Assert.True(result.Success);
        Assert.True(permission.Granted);
        Assert.NotNull(permission.GrantedAt);
        _mockPermissionRepo.Verify(r => r.UpdateAsync(It.IsAny<PluginPermission>(), default), Times.Once);
    }

    [Fact]
    public async Task RevokePermissionAsync_Success()
    {
        // Arrange
        var pluginId = "test.plugin";
        var plugin = new Plugin { Id = pluginId, Name = "Test Plugin" };
        var permission = new PluginPermission
        {
            Id = Guid.NewGuid().ToString(),
            PluginId = pluginId,
            Permission = "mods.write",
            Granted = true,
            GrantedAt = DateTime.UtcNow
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPermissionRepo.Setup(r => r.GetByPluginIdAsync(pluginId, default)).ReturnsAsync(new List<PluginPermission> { permission });
        _mockPermissionRepo.Setup(r => r.UpdateAsync(It.IsAny<PluginPermission>(), default)).Returns(Task.CompletedTask);

        // Act
        var result = await _service.RevokePermissionAsync(pluginId, "mods.write");

        // Assert
        Assert.True(result.Success);
        Assert.False(permission.Granted);
        Assert.Null(permission.GrantedAt);
        _mockPermissionRepo.Verify(r => r.UpdateAsync(It.IsAny<PluginPermission>(), default), Times.Once);
    }

    [Fact]
    public async Task ValidateManifestAsync_ValidManifest_Success()
    {
        // Arrange
        var manifest = new PluginManifest
        {
            Id = "test.plugin",
            Name = "Test Plugin",
            Version = "1.0.0",
            Assembly = "TestPlugin.dll",
            EntryType = "TestPlugin.Entry"
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(_tempManifestPath, manifestJson);

        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "TestPlugin.dll");
        await File.WriteAllTextAsync(assemblyPath, "dummy assembly");

        // Act
        var result = await _service.ValidateManifestAsync(_tempManifestPath);

        // Assert
        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task GetManifestPreviewAsync_ReturnsPermissionMetadata()
    {
        var manifest = new PluginManifest
        {
            Id = "preview.plugin",
            Name = "Preview Plugin",
            Version = "1.2.3",
            Author = "Author",
            Description = "Description",
            PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = new List<string> { "mods.read", "storage.write" }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));

        var preview = await _service.GetManifestPreviewAsync(_tempManifestPath);

        Assert.NotNull(preview);
        Assert.Equal("Preview Plugin", preview!.Name);
        Assert.Equal("Sandbox", preview.PermissionMode);
        Assert.Equal(manifest.RequestedPermissions, preview.RequestedPermissions);
    }

    [Fact]
    public async Task ValidateManifestAsync_MissingFields_Failure()
    {
        // Arrange
        var manifest = new PluginManifest
        {
            Id = "",  // Missing required field
            Name = "Test Plugin",
            Version = "1.0.0"
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(_tempManifestPath, manifestJson);

        // Act
        var result = await _service.ValidateManifestAsync(_tempManifestPath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Id is required"));
    }

    [Fact]
    public async Task ValidateManifestAsync_InvalidVersion_Failure()
    {
        // Arrange
        var manifest = new PluginManifest
        {
            Id = "test.plugin",
            Name = "Test Plugin",
            Version = "invalid.version.format",
            Assembly = "TestPlugin.dll",
            EntryType = "TestPlugin.Entry"
        };

        var manifestJson = JsonSerializer.Serialize(manifest);
        await File.WriteAllTextAsync(_tempManifestPath, manifestJson);

        // Act
        var result = await _service.ValidateManifestAsync(_tempManifestPath);

        // Assert
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Invalid version format"));
    }

    [Fact]
    public async Task ExecutePluginCommandAsync_PluginEnabled_Success()
    {
        // Arrange
        var pluginId = "test.plugin";
        var command = "DoSomething";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            Enabled = true
        };

        var hostResult = PluginOperationResult.SuccessResult("Command executed", null);

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockExecutionRepo.Setup(r => r.AddAsync(It.IsAny<PluginExecutionHistory>(), default)).Returns(Task.CompletedTask);
        _mockExecutionRepo.Setup(r => r.UpdateAsync(It.IsAny<PluginExecutionHistory>(), default)).Returns(Task.CompletedTask);
        _mockHostManager.Setup(h => h.ExecutePluginCommandAsync(pluginId, command, null, default)).ReturnsAsync(hostResult);

        // Act
        var result = await _service.ExecutePluginCommandAsync(pluginId, command);

        // Assert
        Assert.True(result.Success);
        _mockExecutionRepo.Verify(r => r.AddAsync(It.IsAny<PluginExecutionHistory>(), default), Times.Once);
        _mockExecutionRepo.Verify(r => r.UpdateAsync(It.IsAny<PluginExecutionHistory>(), default), Times.Once);
    }

    [Fact]
    public async Task ExecutePluginCommandAsync_PluginDisabled_Failure()
    {
        // Arrange
        var pluginId = "test.plugin";
        var command = "DoSomething";
        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            Enabled = false
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);

        // Act
        var result = await _service.ExecutePluginCommandAsync(pluginId, command);

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Plugin is disabled", result.Error);
    }

    [Fact]
    public async Task GetAllPluginsAsync_ReturnsAllPlugins()
    {
        // Arrange
        var plugins = new List<Plugin>
        {
            new Plugin { Id = "plugin1", Name = "Plugin 1" },
            new Plugin { Id = "plugin2", Name = "Plugin 2" }
        };

        _mockPluginRepo.Setup(r => r.GetAllAsync(default)).ReturnsAsync(plugins);

        // Act
        var result = await _service.GetAllPluginsAsync();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, p => p.Id == "plugin1");
        Assert.Contains(result, p => p.Id == "plugin2");
    }

    [Fact]
    public async Task GetEnabledPluginsAsync_ReturnsOnlyEnabledPlugins()
    {
        // Arrange
        var enabledPlugins = new List<Plugin>
        {
            new Plugin { Id = "plugin1", Name = "Plugin 1", Enabled = true }
        };

        _mockPluginRepo.Setup(r => r.GetEnabledAsync(default)).ReturnsAsync(enabledPlugins);

        // Act
        var result = await _service.GetEnabledPluginsAsync();

        // Assert
        Assert.Single(result);
        Assert.True(result[0].Enabled);
    }

    public void Dispose()
    {
        // Cleanup temp files
        var tempDir = Path.GetDirectoryName(_tempManifestPath);
        if (tempDir != null && Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, true);
        }
    }
}
