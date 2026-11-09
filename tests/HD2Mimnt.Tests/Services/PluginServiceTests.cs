using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;
using System.Text.Json;
using Xunit;
using PluginManifest = HD2Mimnt.PluginSDK.PluginManifest;
using PluginPermissionMode = HD2Mimnt.PluginSDK.PluginPermissionMode;
using PluginCommandDescriptor = HD2Mimnt.PluginSDK.PluginCommandDescriptor;

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
    public async Task InstallPluginAsync_RecordsAssemblySha256Anchor()
    {
        var manifest = new PluginManifest
        {
            Id = "hash.plugin", Name = "Hash", Version = "1.0.0", Assembly = "Hash.dll",
            EntryType = "Hash.Entry", PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = new List<string>()
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));
        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Hash.dll");
        await File.WriteAllTextAsync(assemblyPath, "assembly-bytes");
        _mockPluginRepo.Setup(r => r.ExistsAsync(manifest.Id, default)).ReturnsAsync(false);
        _mockPluginRepo.Setup(r => r.AddAsync(It.IsAny<Plugin>(), default)).Returns(Task.CompletedTask);
        _mockPermissionRepo.Setup(r => r.AddAsync(It.IsAny<PluginPermission>(), default)).Returns(Task.CompletedTask);

        var expectedSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData("assembly-bytes"u8.ToArray())).ToLowerInvariant();

        var result = await _service.InstallPluginAsync(_tempManifestPath);

        Assert.True(result.Success);
        _mockPluginRepo.Verify(r => r.AddAsync(It.Is<Plugin>(p => p.AssemblySha256 == expectedSha), default), Times.Once);
    }

    [Fact]
    public async Task StartPluginAsync_TamperedAssembly_IsRejectedBeforeHostStart()
    {
        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Tampered.dll");
        await File.WriteAllTextAsync(assemblyPath, "modified-content");
        var plugin = new Plugin
        {
            Id = "tampered.plugin", Name = "T", Version = "1.0.0",
            ManifestPath = _tempManifestPath, AssemblyPath = assemblyPath,
            AssemblySha256 = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData("original-content"u8.ToArray())).ToLowerInvariant()
        };
        _mockPluginRepo.Setup(r => r.GetByIdAsync(plugin.Id, default)).ReturnsAsync(plugin);

        var result = await _service.StartPluginAsync(plugin.Id);

        Assert.False(result.Success);
        Assert.Contains("拒绝启动", result.Error);
        _mockHostManager.Verify(h => h.StartPluginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StartPluginAsync_IntactAssembly_StartsHost()
    {
        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Intact.dll");
        await File.WriteAllTextAsync(assemblyPath, "stable-content");
        var sha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData("stable-content"u8.ToArray())).ToLowerInvariant();
        var plugin = new Plugin
        {
            Id = "intact.plugin", Name = "I", Version = "1.0.0",
            ManifestPath = _tempManifestPath, AssemblyPath = assemblyPath, AssemblySha256 = sha
        };
        _mockPluginRepo.Setup(r => r.GetByIdAsync(plugin.Id, default)).ReturnsAsync(plugin);
        _mockHostManager.Setup(h => h.StartPluginAsync(plugin.Id, plugin.AssemblyPath, plugin.ManifestPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("started"));

        var result = await _service.StartPluginAsync(plugin.Id);

        Assert.True(result.Success);
        _mockHostManager.Verify(h => h.StartPluginAsync(plugin.Id, plugin.AssemblyPath, plugin.ManifestPath,
            It.IsAny<CancellationToken>()), Times.Once);
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
        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = "Test Plugin",
            Version = "1.0.0",
            Assembly = "TestPlugin.dll",
            EntryType = "TestPlugin.Entry",
            Commands = new List<PluginCommandDescriptor>
            {
                new() { Name = command, DisplayName = "Do something" }
            }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));

        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Test Plugin",
            Enabled = true,
            ManifestPath = _tempManifestPath
        };

        var hostResult = PluginOperationResult.SuccessResult("Command executed", null);

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockExecutionRepo.Setup(r => r.AddAsync(It.IsAny<PluginExecutionHistory>(), default)).Returns(Task.CompletedTask);
        _mockExecutionRepo.Setup(r => r.UpdateAsync(It.IsAny<PluginExecutionHistory>(), default)).Returns(Task.CompletedTask);
        _mockHostManager.Setup(h => h.GetRunningPlugins()).Returns(new List<PluginHostInfo>
        {
            new() { PluginId = pluginId, IsRunning = true }
        });
        _mockHostManager.Setup(h => h.ExecutePluginCommandAsync(pluginId, command, null, default)).ReturnsAsync(hostResult);

        // Act
        var result = await _service.ExecutePluginCommandAsync(pluginId, command);

        // Assert
        Assert.True(result.Success);
        _mockExecutionRepo.Verify(r => r.AddAsync(It.IsAny<PluginExecutionHistory>(), default), Times.Once);
        _mockExecutionRepo.Verify(r => r.UpdateAsync(It.IsAny<PluginExecutionHistory>(), default), Times.Once);
    }

    [Fact]
    public async Task ExecutePluginCommandAsync_CommandNotDeclared_IsRejected()
    {
        var pluginId = "undeclared.plugin";
        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = "Undeclared",
            Version = "1.0.0",
            Assembly = "Undeclared.dll",
            EntryType = "Undeclared.Entry"
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));

        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Undeclared",
            Enabled = true,
            ManifestPath = _tempManifestPath
        };
        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);

        var result = await _service.ExecutePluginCommandAsync(pluginId, "Unlisted");

        Assert.False(result.Success);
        Assert.Contains("未声明", result.Error);
        _mockExecutionRepo.Verify(r => r.AddAsync(It.IsAny<PluginExecutionHistory>(), default), Times.Never);
        _mockHostManager.Verify(h => h.ExecutePluginCommandAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecutePluginCommandAsync_MissingRequiredPermission_IsRejected()
    {
        var pluginId = "restricted.plugin";
        var command = "Restricted";
        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = "Restricted",
            Version = "1.0.0",
            Assembly = "Restricted.dll",
            EntryType = "Restricted.Entry",
            Commands = new List<PluginCommandDescriptor>
            {
                new() { Name = command, RequiredPermission = "mods.read" }
            }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));

        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "Restricted",
            Enabled = true,
            ManifestPath = _tempManifestPath
        };
        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPermissionRepo.Setup(r => r.GetByPluginIdAsync(pluginId, default))
            .ReturnsAsync(new List<PluginPermission>());

        var result = await _service.ExecutePluginCommandAsync(pluginId, command);

        Assert.False(result.Success);
        Assert.Contains("缺少权限", result.Error);
        _mockHostManager.Verify(h => h.ExecutePluginCommandAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecutePluginCommandAsync_PluginNotRunning_StartsHostFirst()
    {
        var pluginId = "autostart.plugin";
        var command = "Run";
        var manifest = new PluginManifest
        {
            Id = pluginId,
            Name = "AutoStart",
            Version = "1.0.0",
            Assembly = "AutoStart.dll",
            EntryType = "AutoStart.Entry",
            Commands = new List<PluginCommandDescriptor>
            {
                new() { Name = command }
            }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));

        var assemblyPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "AutoStart.dll");
        await File.WriteAllTextAsync(assemblyPath, "assembly-bytes");

        var plugin = new Plugin
        {
            Id = pluginId,
            Name = "AutoStart",
            Enabled = true,
            ManifestPath = _tempManifestPath,
            AssemblyPath = assemblyPath
        };

        _mockPluginRepo.Setup(r => r.GetByIdAsync(pluginId, default)).ReturnsAsync(plugin);
        _mockPluginRepo.Setup(r => r.UpdateAsync(It.IsAny<Plugin>(), default)).Returns(Task.CompletedTask);
        _mockExecutionRepo.Setup(r => r.AddAsync(It.IsAny<PluginExecutionHistory>(), default)).Returns(Task.CompletedTask);
        _mockExecutionRepo.Setup(r => r.UpdateAsync(It.IsAny<PluginExecutionHistory>(), default)).Returns(Task.CompletedTask);
        _mockHostManager.Setup(h => h.GetRunningPlugins()).Returns(new List<PluginHostInfo>());
        _mockHostManager.Setup(h => h.StartPluginAsync(pluginId, assemblyPath, _tempManifestPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("started"));
        _mockHostManager.Setup(h => h.ExecutePluginCommandAsync(pluginId, command, null, default))
            .ReturnsAsync(PluginOperationResult.SuccessResult("ok"));

        var result = await _service.ExecutePluginCommandAsync(pluginId, command);

        Assert.True(result.Success);
        _mockHostManager.Verify(
            h => h.StartPluginAsync(pluginId, assemblyPath, _tempManifestPath, It.IsAny<CancellationToken>()),
            Times.Once);
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

    [Fact]
    public async Task InstallPluginAsync_AiPermission_IsNotAutoGranted()
    {
        var manifest = new PluginManifest
        {
            Id = "ai.plugin", Name = "AI", Version = "1.0.0", Assembly = "Ai.dll",
            EntryType = "Ai.Entry", PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = new List<string> { "ai.use" }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Ai.dll"), "assembly");

        _mockPluginRepo.Setup(r => r.ExistsAsync(manifest.Id, default)).ReturnsAsync(false);
        PluginPermission? saved = null;
        _mockPermissionRepo
            .Setup(r => r.AddAsync(It.IsAny<PluginPermission>(), default))
            .Callback<PluginPermission, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        var result = await _service.InstallPluginAsync(_tempManifestPath);

        Assert.True(result.Success);
        Assert.NotNull(saved);
        Assert.False(saved!.Granted);
        Assert.Null(saved.GrantedAt);
    }

    [Fact]
    public async Task InstallPluginAsync_IncompatibleApiVersion_IsRejected()
    {
        var manifest = new PluginManifest
        {
            Id = "future.plugin", Name = "Future", Version = "1.0.0", Assembly = "Future.dll",
            EntryType = "Future.Entry", ApiVersion = "2.0.0",
            PermissionMode = PluginPermissionMode.Sandbox
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Future.dll"), "assembly");

        var result = await _service.InstallPluginAsync(_tempManifestPath);

        Assert.False(result.Success);
        Assert.Contains("SDK", result.Error ?? result.Message);
        _mockPluginRepo.Verify(r => r.AddAsync(It.IsAny<Plugin>(), default), Times.Never);
    }

    [Fact]
    public async Task InstallPluginAsync_WithConsent_GrantsOnlySelectedPermissions()
    {
        var manifest = new PluginManifest
        {
            Id = "consent.plugin", Name = "Consent", Version = "1.0.0", Assembly = "Consent.dll",
            EntryType = "Consent.Entry", PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = new List<string> { "mods.read", "ai.use" }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Consent.dll"), "assembly");

        _mockPluginRepo.Setup(r => r.ExistsAsync(manifest.Id, default)).ReturnsAsync(false);
        var saved = new List<PluginPermission>();
        _mockPermissionRepo
            .Setup(r => r.AddAsync(It.IsAny<PluginPermission>(), default))
            .Callback<PluginPermission, CancellationToken>((p, _) => saved.Add(p))
            .Returns(Task.CompletedTask);

        var result = await _service.InstallPluginAsync(_tempManifestPath, new PluginInstallConsent
        {
            PersistGrants = true,
            GrantedPermissions = new[] { "ai.use" }
        });

        Assert.True(result.Success);
        Assert.Equal(2, saved.Count);
        Assert.True(saved.Single(p => p.Permission == "ai.use").Granted);
        Assert.False(saved.Single(p => p.Permission == "mods.read").Granted);
    }

    [Fact]
    public async Task InstallPluginAsync_SessionConsent_KeepsGrantOutOfDatabase()
    {
        var manifest = new PluginManifest
        {
            Id = "session.plugin", Name = "Session", Version = "1.0.0", Assembly = "Session.dll",
            EntryType = "Session.Entry", PermissionMode = PluginPermissionMode.Sandbox,
            RequestedPermissions = new List<string> { "ai.use" }
        };
        await File.WriteAllTextAsync(_tempManifestPath, JsonSerializer.Serialize(manifest));
        await File.WriteAllTextAsync(
            Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "Session.dll"), "assembly");

        _mockPluginRepo.Setup(r => r.ExistsAsync(manifest.Id, default)).ReturnsAsync(false);
        _mockPluginRepo.Setup(r => r.GetByIdAsync(manifest.Id, default))
            .ReturnsAsync(new Plugin { Id = manifest.Id, Name = "Session", Enabled = true, PermissionMode = "Sandbox" });
        PluginPermission? saved = null;
        _mockPermissionRepo
            .Setup(r => r.AddAsync(It.IsAny<PluginPermission>(), default))
            .Callback<PluginPermission, CancellationToken>((p, _) => saved = p)
            .Returns(Task.CompletedTask);

        var result = await _service.InstallPluginAsync(_tempManifestPath, new PluginInstallConsent
        {
            PersistGrants = false,
            GrantedPermissions = new[] { "ai.use" }
        });

        Assert.True(result.Success);
        Assert.NotNull(saved);
        Assert.False(saved!.Granted);
        // “仅本次允许”只在本进程内生效
        Assert.True(await _service.CheckPermissionAsync(manifest.Id, "ai.use"));
    }

    [Fact]
    public async Task GetContributedCommandsAsync_ReturnsOnlyRequestedPlacement()
    {
        var manifest = new PluginManifest
        {
            Id = "menu.plugin", Name = "Menu", Version = "1.0.0", Assembly = "Menu.dll",
            EntryType = "Menu.Entry", PermissionMode = PluginPermissionMode.Sandbox,
            Commands = new List<HD2Mimnt.PluginSDK.PluginCommandDescriptor>
            {
                new() { Name = "summarize", DisplayName = "AI 摘要", Placement = "mod", RequiredPermission = "ai.use" },
                new() { Name = "ping", DisplayName = "Ping" }
            }
        };

        var manifestPath = Path.Combine(Path.GetDirectoryName(_tempManifestPath)!, "menu.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));

        _mockPluginRepo.Setup(r => r.GetEnabledAsync(default))
            .ReturnsAsync(new List<Plugin>
            {
                new() { Id = manifest.Id, Name = "Menu", Enabled = true, ManifestPath = manifestPath }
            });
        _mockPluginRepo.Setup(r => r.GetByIdAsync(manifest.Id, default))
            .ReturnsAsync(new Plugin { Id = manifest.Id, Name = "Menu", Enabled = true, PermissionMode = "Sandbox" });
        _mockPermissionRepo.Setup(r => r.GetByPluginIdAsync(manifest.Id, default))
            .ReturnsAsync(new List<PluginPermission>());

        var contributions = await _service.GetContributedCommandsAsync(HD2Mimnt.PluginSDK.PluginCommandPlacements.ModContextMenu);

        var command = Assert.Single(contributions);
        Assert.Equal("summarize", command.Name);
        Assert.False(command.IsPermissionGranted);
        Assert.Equal("Menu", command.PluginName);
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
