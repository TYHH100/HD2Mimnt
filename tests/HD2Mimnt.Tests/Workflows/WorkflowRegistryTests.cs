using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Workflows;
using Xunit;

namespace HD2Mimnt.Tests.Workflows;

/// <summary>
/// WorkflowRegistry 测试
/// </summary>
public class WorkflowRegistryTests
{
    [Fact]
    public void RegisterWorkflow_Should_Add_Workflow()
    {
        // Arrange
        var registry = new WorkflowRegistry();
        var workflow = new LegacyWorkflow();

        // Act
        registry.RegisterWorkflow(workflow);

        // Assert
        var retrieved = registry.GetWorkflow("Legacy");
        Assert.NotNull(retrieved);
        Assert.Equal("Legacy", retrieved.Name);
    }

    [Fact]
    public void RegisterWorkflow_Should_Throw_When_Workflow_Is_Null()
    {
        // Arrange
        var registry = new WorkflowRegistry();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => registry.RegisterWorkflow(null!));
    }

    [Fact]
    public void UnregisterWorkflow_Should_Remove_Workflow()
    {
        // Arrange
        var registry = new WorkflowRegistry();
        var workflow = new LegacyWorkflow();
        registry.RegisterWorkflow(workflow);

        // Act
        registry.UnregisterWorkflow("Legacy");

        // Assert
        var retrieved = registry.GetWorkflow("Legacy");
        Assert.Null(retrieved);
    }

    [Fact]
    public void GetWorkflow_Should_Return_Null_When_Not_Found()
    {
        // Arrange
        var registry = new WorkflowRegistry();

        // Act
        var workflow = registry.GetWorkflow("NonExistent");

        // Assert
        Assert.Null(workflow);
    }

    [Fact]
    public void GetAllWorkflows_Should_Return_All_Registered_Workflows()
    {
        // Arrange
        var registry = new WorkflowRegistry();
        var legacyWorkflow = new LegacyWorkflow();
        var v1Workflow = new V1Workflow();

        registry.RegisterWorkflow(legacyWorkflow);
        registry.RegisterWorkflow(v1Workflow);

        // Act
        var workflows = registry.GetAllWorkflows().ToList();

        // Assert
        Assert.Equal(2, workflows.Count);
        Assert.Contains(workflows, w => w.Name == "Legacy");
        Assert.Contains(workflows, w => w.Name == "V1");
    }

    [Fact]
    public void RegisterWorkflow_Should_Replace_Existing_Workflow()
    {
        // Arrange
        var registry = new WorkflowRegistry();
        var workflow1 = new LegacyWorkflow();
        var workflow2 = new LegacyWorkflow();

        // Act
        registry.RegisterWorkflow(workflow1);
        registry.RegisterWorkflow(workflow2);

        // Assert
        var workflows = registry.GetAllWorkflows().ToList();
        Assert.Single(workflows);
    }

    [Fact]
    public async Task DetectWorkflowAsync_Should_Return_Null_When_Path_Is_Empty()
    {
        // Arrange
        var registry = new WorkflowRegistry();

        // Act
        var workflow = await registry.DetectWorkflowAsync(string.Empty);

        // Assert
        Assert.Null(workflow);
    }

    [Fact]
    public async Task DetectWorkflowAsync_Should_Return_Null_When_No_Workflow_Matches()
    {
        // Arrange
        var registry = new WorkflowRegistry();
        var legacyWorkflow = new LegacyWorkflow();
        registry.RegisterWorkflow(legacyWorkflow);

        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            // Act
            var workflow = await registry.DetectWorkflowAsync(tempDir);

            // Assert
            Assert.Null(workflow);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DetectAllWorkflowsAsync_Should_Return_Empty_List_When_Path_Is_Empty()
    {
        // Arrange
        var registry = new WorkflowRegistry();

        // Act
        var workflows = await registry.DetectAllWorkflowsAsync(string.Empty);

        // Assert
        Assert.Empty(workflows);
    }
}
