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
        Assert.Equal("Lock", viewModel.PermissionModeIcon);
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
        Assert.Equal("Warning", viewModel.PermissionModeIcon);
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
        Assert.Contains("完全访问模式", viewModel.WarningMessage);
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
        Assert.Contains("潜在危险权限", viewModel.WarningMessage);
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
        Assert.Equal("读取模组文件", permission.DisplayName);
        Assert.Equal("Logs", permission.Icon);
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
        Assert.Equal("网络访问", permission.DisplayName);
        Assert.Equal("向外部服务发起网络请求", permission.Description);
        Assert.Equal("Globe", permission.Icon);
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
        Assert.Equal("自定义权限", permission.Description);
        Assert.Equal("Lock", permission.Icon);
        Assert.False(permission.IsHighRisk);
    }

    [Theory]
    [InlineData("ReadModFiles", "Logs", false)]
    [InlineData("WriteModFiles", "Edit", true)]
    [InlineData("AccessNetwork", "Globe", false)]
    [InlineData("ExecuteProcesses", "Code", true)]
    [InlineData("ReadDatabase", "Database", false)]
    [InlineData("WriteConfiguration", "Wrench", false)]
    [InlineData("FileSystemAccess", "Folder", true)]
    [InlineData("RegistryAccess", "Settings", true)]
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
