using HD2Mimnt.UI.ViewModels;
using Xunit;

namespace HD2Mimnt.Tests.ViewModels;

public class PluginPermissionDialogViewModelTests
{
    [Fact]
    public void Initialize_SetsBasicProperties()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize(
            pluginName: "Test Plugin",
            version: "1.0.0",
            author: "Test Author",
            description: "Test Description",
            permissionMode: "Sandbox",
            requestedPermissions: new[] { "ReadModFiles" }
        );

        // Assert
        Assert.Equal("Test Plugin", viewModel.PluginName);
        Assert.Equal("1.0.0", viewModel.PluginVersion);
        Assert.Equal("Test Author", viewModel.PluginAuthor);
        Assert.Equal("Test Description", viewModel.PluginDescription);
        Assert.Equal("Sandbox", viewModel.PermissionMode);
    }

    [Fact]
    public void Initialize_SandboxMode_SetsCorrectIcon()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", Array.Empty<string>());

        // Assert
        Assert.False(viewModel.IsFullAccess);
        Assert.Equal("🔒", viewModel.PermissionModeIcon);
    }

    [Fact]
    public void Initialize_FullAccessMode_SetsCorrectIcon()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "FullAccess", Array.Empty<string>());

        // Assert
        Assert.True(viewModel.IsFullAccess);
        Assert.Equal("🔓", viewModel.PermissionModeIcon);
    }

    [Fact]
    public void Initialize_FullAccessMode_ShowsWarning()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "FullAccess", Array.Empty<string>());

        // Assert
        Assert.True(viewModel.ShowWarning);
        Assert.Contains("Full Access", viewModel.WarningMessage);
    }

    [Fact]
    public void Initialize_HighRiskPermission_ShowsWarning()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", new[] { "ExecuteProcesses" });

        // Assert
        Assert.True(viewModel.ShowWarning);
        Assert.Contains("dangerous permissions", viewModel.WarningMessage);
    }

    [Fact]
    public void Initialize_LowRiskPermissions_NoWarning()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", new[] { "ReadModFiles", "AccessNetwork" });

        // Assert
        Assert.False(viewModel.ShowWarning);
    }

    [Fact]
    public void Initialize_PopulatesRequestedPermissions()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox",
            new[] { "ReadModFiles", "WriteModFiles", "AccessNetwork" });

        // Assert
        Assert.Equal(3, viewModel.RequestedPermissions.Count);
        Assert.Contains(viewModel.RequestedPermissions, p => p.Permission == "ReadModFiles");
        Assert.Contains(viewModel.RequestedPermissions, p => p.Permission == "WriteModFiles");
        Assert.Contains(viewModel.RequestedPermissions, p => p.Permission == "AccessNetwork");
    }

    [Fact]
    public void Initialize_SetsPermissionDisplayNames()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", new[] { "ReadModFiles" });

        // Assert
        var permission = viewModel.RequestedPermissions.First();
        Assert.Equal("Read Mod Files", permission.DisplayName);
        Assert.Equal("📖", permission.Icon);
        Assert.False(permission.IsHighRisk);
    }

    [Fact]
    public void Initialize_MarksHighRiskPermissions()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox",
            new[] { "ExecuteProcesses", "WriteModFiles", "FileSystemAccess", "RegistryAccess" });

        // Assert
        Assert.All(viewModel.RequestedPermissions, p => Assert.True(p.IsHighRisk));
    }

    [Fact]
    public void Initialize_SetsPermissionDescriptions()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", new[] { "AccessNetwork" });

        // Assert
        var permission = viewModel.RequestedPermissions.First();
        Assert.Equal("Network Access", permission.DisplayName);
        Assert.Equal("Make network requests to external services", permission.Description);
        Assert.Equal("🌐", permission.Icon);
    }

    [Fact]
    public void AllowOnceCommand_SetsCorrectResult()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.AllowOnceCommand.Execute(null);

        // Assert
        Assert.Equal(PluginPermissionDialogResult.AllowOnce, viewModel.Result);
    }

    [Fact]
    public void AllowAlwaysCommand_SetsCorrectResult()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.AllowAlwaysCommand.Execute(null);

        // Assert
        Assert.Equal(PluginPermissionDialogResult.AllowAlways, viewModel.Result);
    }

    [Fact]
    public void DenyCommand_SetsCorrectResult()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.DenyCommand.Execute(null);

        // Assert
        Assert.Equal(PluginPermissionDialogResult.Deny, viewModel.Result);
    }

    [Fact]
    public void DefaultResult_IsDeny()
    {
        // Arrange & Act
        var viewModel = new PluginPermissionDialogViewModel();

        // Assert
        Assert.Equal(PluginPermissionDialogResult.Deny, viewModel.Result);
    }

    [Fact]
    public void Initialize_CustomPermission_UsesDefaultDisplayName()
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", new[] { "CustomPermission" });

        // Assert
        var permission = viewModel.RequestedPermissions.First();
        Assert.Equal("CustomPermission", permission.DisplayName);
        Assert.Equal("Custom permission", permission.Description);
        Assert.Equal("🔒", permission.Icon);
        Assert.False(permission.IsHighRisk);
    }

    [Theory]
    [InlineData("ReadModFiles", "📖", false)]
    [InlineData("WriteModFiles", "✏️", true)]
    [InlineData("AccessNetwork", "🌐", false)]
    [InlineData("ExecuteProcesses", "⚙️", true)]
    [InlineData("ReadDatabase", "💾", false)]
    [InlineData("WriteConfiguration", "🔧", false)]
    [InlineData("FileSystemAccess", "📁", true)]
    [InlineData("RegistryAccess", "🔑", true)]
    public void Initialize_KnownPermissions_CorrectIconAndRisk(string permission, string expectedIcon, bool expectedHighRisk)
    {
        // Arrange
        var viewModel = new PluginPermissionDialogViewModel();

        // Act
        viewModel.Initialize("Test", "1.0", "Author", "Desc", "Sandbox", new[] { permission });

        // Assert
        var permissionItem = viewModel.RequestedPermissions.First();
        Assert.Equal(expectedIcon, permissionItem.Icon);
        Assert.Equal(expectedHighRisk, permissionItem.IsHighRisk);
    }
}
