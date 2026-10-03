using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.ViewModels;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

public class PluginManagementViewModelTests
{
    private readonly Mock<IPluginService> _mockPluginService;
    private readonly PluginManagementViewModel _viewModel;

    public PluginManagementViewModelTests()
    {
        _mockPluginService = new Mock<IPluginService>();
        _viewModel = new PluginManagementViewModel(_mockPluginService.Object);
    }

    [Fact]
    public async Task InitializeAsync_LoadsPlugins()
    {
        // Arrange
        var plugins = new List<Plugin>
        {
            new Plugin { Id = "plugin1", Name = "Plugin 1", Version = "1.0.0", Enabled = true },
            new Plugin { Id = "plugin2", Name = "Plugin 2", Version = "2.0.0", Enabled = false }
        };

        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(plugins);
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        // Act
        await _viewModel.InitializeAsync();

        // Assert
        Assert.Equal(2, _viewModel.InstalledPlugins.Count);
        Assert.Equal("Plugin 1", _viewModel.InstalledPlugins[0].Name);
        Assert.Equal("Plugin 2", _viewModel.InstalledPlugins[1].Name);
    }

    [Fact]
    public void InstallPluginCommand_RaisesManifestPickerRequest()
    {
        var requested = false;
        _viewModel.InstallManifestRequested += (_, _) => requested = true;

        _viewModel.InstallPluginCommand.Execute(null);

        Assert.True(requested);
    }

    [Fact]
    public async Task InstallPluginAsync_Success_RefreshesPlugins()
    {
        _mockPluginService
            .Setup(s => s.InstallPluginAsync("manifest.json", It.IsAny<CancellationToken>()))
            .ReturnsAsync(PluginOperationResult.SuccessResult("Installed"));
        _mockPluginService
            .Setup(s => s.GetAllPluginsAsync(default))
            .ReturnsAsync(new List<Plugin>
            {
                new Plugin { Id = "plugin1", Name = "Installed Plugin", Version = "1.0.0" }
            });
        _mockPluginService
            .Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        await _viewModel.InstallPluginAsync("manifest.json");

        Assert.Single(_viewModel.InstalledPlugins);
        Assert.Equal("Installed Plugin", _viewModel.InstalledPlugins[0].Name);
        Assert.Equal("插件安装成功", _viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadPluginsAsync_UpdatesStatistics()
    {
        // Arrange
        var plugins = new List<Plugin>
        {
            new Plugin { Id = "plugin1", Name = "Plugin 1", Enabled = true },
            new Plugin { Id = "plugin2", Name = "Plugin 2", Enabled = true },
            new Plugin { Id = "plugin3", Name = "Plugin 3", Enabled = false }
        };

        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(plugins);
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        // Act
        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(3, _viewModel.TotalPlugins);
        Assert.Equal(2, _viewModel.EnabledPlugins);
        Assert.Equal(1, _viewModel.DisabledPlugins);
    }

    [Fact]
    public async Task LoadPluginsAsync_SetsStatusMessage()
    {
        // Arrange
        var plugins = new List<Plugin>
        {
            new Plugin { Id = "plugin1", Name = "Plugin 1" }
        };

        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(plugins);
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        // Act
        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Loaded 1 plugins", _viewModel.StatusMessage);
    }

    [Fact]
    public async Task LoadPluginsAsync_HandlesError()
    {
        // Arrange
        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default))
            .ThrowsAsync(new Exception("Database error"));

        // Act
        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("Failed to load plugins", _viewModel.StatusMessage);
        Assert.Contains("Database error", _viewModel.StatusMessage);
    }

    [Fact]
    public async Task EnablePluginAsync_Success_UpdatesPlugin()
    {
        // Arrange
        var plugin = new Plugin { Id = "plugin1", Name = "Test Plugin", Enabled = false };
        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(new List<Plugin> { plugin });
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);
        var pluginItem = _viewModel.InstalledPlugins[0];

        _mockPluginService.Setup(s => s.EnablePluginAsync(plugin.Id, default))
            .ReturnsAsync(PluginOperationResult.SuccessResult("Enabled"));

        // Act
        await _viewModel.EnablePluginCommand.ExecuteAsync(pluginItem);

        // Assert
        Assert.True(pluginItem.IsEnabled);
        Assert.Contains("Enabled", _viewModel.StatusMessage);
        Assert.Equal(1, _viewModel.EnabledPlugins);
    }

    [Fact]
    public async Task EnablePluginAsync_Failure_ShowsError()
    {
        // Arrange
        var plugin = new Plugin { Id = "plugin1", Name = "Test Plugin", Enabled = false };
        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(new List<Plugin> { plugin });
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);
        var pluginItem = _viewModel.InstalledPlugins[0];

        _mockPluginService.Setup(s => s.EnablePluginAsync(plugin.Id, default))
            .ReturnsAsync(PluginOperationResult.ErrorResult("Plugin not found"));

        // Act
        await _viewModel.EnablePluginCommand.ExecuteAsync(pluginItem);

        // Assert
        Assert.False(pluginItem.IsEnabled);
        Assert.Contains("Failed to enable", _viewModel.StatusMessage);
    }

    [Fact]
    public async Task DisablePluginAsync_Success_UpdatesPlugin()
    {
        // Arrange
        var plugin = new Plugin { Id = "plugin1", Name = "Test Plugin", Enabled = true };
        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(new List<Plugin> { plugin });
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);
        var pluginItem = _viewModel.InstalledPlugins[0];

        _mockPluginService.Setup(s => s.DisablePluginAsync(plugin.Id, default))
            .ReturnsAsync(PluginOperationResult.SuccessResult("Disabled"));

        // Act
        await _viewModel.DisablePluginCommand.ExecuteAsync(pluginItem);

        // Assert
        Assert.False(pluginItem.IsEnabled);
        Assert.Contains("Disabled", _viewModel.StatusMessage);
        Assert.Equal(0, _viewModel.EnabledPlugins);
    }

    [Fact]
    public async Task UninstallPluginAsync_Success_RemovesPlugin()
    {
        // Arrange
        var plugin = new Plugin { Id = "plugin1", Name = "Test Plugin" };
        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(new List<Plugin> { plugin });
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);
        var pluginItem = _viewModel.InstalledPlugins[0];

        _mockPluginService.Setup(s => s.UninstallPluginAsync(plugin.Id, default))
            .ReturnsAsync(PluginOperationResult.SuccessResult("Uninstalled"));

        // Act
        await _viewModel.UninstallPluginCommand.ExecuteAsync(pluginItem);

        // Assert
        Assert.Empty(_viewModel.InstalledPlugins);
        Assert.Contains("Uninstalled", _viewModel.StatusMessage);
        Assert.Equal(0, _viewModel.TotalPlugins);
    }

    [Fact]
    public async Task ViewExecutionHistoryAsync_LoadsHistory()
    {
        // Arrange
        var plugin = new Plugin { Id = "plugin1", Name = "Test Plugin" };
        var history = new List<PluginExecutionHistory>
        {
            new PluginExecutionHistory { Id = "h1", PluginId = "plugin1", Command = "Test" }
        };

        _mockPluginService.Setup(s => s.GetAllPluginsAsync(default)).ReturnsAsync(new List<Plugin> { plugin });
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());
        _mockPluginService.Setup(s => s.GetExecutionHistoryAsync(plugin.Id, 50, default))
            .ReturnsAsync(history);

        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);
        var pluginItem = _viewModel.InstalledPlugins[0];

        // Act
        await _viewModel.ViewExecutionHistoryCommand.ExecuteAsync(pluginItem);

        // Assert
        Assert.Contains("Found 1 execution records", _viewModel.StatusMessage);
    }

    [Fact]
    public async Task RefreshAsync_ReloadsPlugins()
    {
        // Arrange
        var plugins1 = new List<Plugin> { new Plugin { Id = "plugin1", Name = "Plugin 1" } };
        var plugins2 = new List<Plugin>
        {
            new Plugin { Id = "plugin1", Name = "Plugin 1" },
            new Plugin { Id = "plugin2", Name = "Plugin 2" }
        };

        _mockPluginService.SetupSequence(s => s.GetAllPluginsAsync(default))
            .ReturnsAsync(plugins1)
            .ReturnsAsync(plugins2);
        _mockPluginService.Setup(s => s.GetPluginPermissionsAsync(It.IsAny<string>(), default))
            .ReturnsAsync(new List<PluginPermission>());

        await _viewModel.LoadPluginsCommand.ExecuteAsync(null);
        Assert.Single(_viewModel.InstalledPlugins);

        // Act
        await _viewModel.RefreshCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal(2, _viewModel.InstalledPlugins.Count);
    }

    [Fact]
    public async Task EnablePluginAsync_WithNullPlugin_DoesNothing()
    {
        // Act
        await _viewModel.EnablePluginCommand.ExecuteAsync(null);

        // Assert
        _mockPluginService.Verify(s => s.EnablePluginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisablePluginAsync_WithNullPlugin_DoesNothing()
    {
        // Act
        await _viewModel.DisablePluginCommand.ExecuteAsync(null);

        // Assert
        _mockPluginService.Verify(s => s.DisablePluginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
