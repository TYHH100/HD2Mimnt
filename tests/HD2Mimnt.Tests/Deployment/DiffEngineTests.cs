using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Deployment;

public class DiffEngineTests
{
    private readonly Mock<IDeploymentRepository> _deploymentRepoMock;
    private readonly Mock<IFileOwnershipRepository> _ownershipRepoMock;
    private readonly Mock<ILogger<DiffEngine>> _loggerMock;
    private readonly DiffEngine _diffEngine;

    public DiffEngineTests()
    {
        _deploymentRepoMock = new Mock<IDeploymentRepository>();
        _ownershipRepoMock = new Mock<IFileOwnershipRepository>();
        _loggerMock = new Mock<ILogger<DiffEngine>>();
        _diffEngine = new DiffEngine(
            _deploymentRepoMock.Object,
            _ownershipRepoMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task CalculateDiffAsync_WithNoCurrentFiles_ShouldAddAll()
    {
        // Arrange
        var desiredFiles = new List<DeployableFileMapping>
        {
            new() { TargetPath = "bin/file1.dll", Hash = "hash1" },
            new() { TargetPath = "bin/file2.dll", Hash = "hash2" }
        };

        _ownershipRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<FileOwnership>());

        // Act
        var diff = await _diffEngine.CalculateDiffAsync(desiredFiles, Guid.NewGuid());

        // Assert
        Assert.Equal(2, diff.FilesToAdd.Count);
        Assert.Empty(diff.FilesToUpdate);
        Assert.Empty(diff.FilesToRemove);
        Assert.Empty(diff.FilesUnchanged);
    }

    [Fact]
    public async Task CalculateDiffAsync_WithIdenticalFiles_ShouldBeUnchanged()
    {
        // Arrange
        var desiredFiles = new List<DeployableFileMapping>
        {
            new() { TargetPath = "bin/file1.dll", Hash = "hash1" }
        };

        var currentOwnerships = new List<FileOwnership>
        {
            new()
            {
                DeployPath = "bin/file1.dll",
                ContentHash = "hash1",
                IsShared = false,
                ReferenceCount = 1
            }
        };

        _ownershipRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentOwnerships);

        // Act
        var diff = await _diffEngine.CalculateDiffAsync(desiredFiles, Guid.NewGuid());

        // Assert
        Assert.Empty(diff.FilesToAdd);
        Assert.Empty(diff.FilesToUpdate);
        Assert.Empty(diff.FilesToRemove);
        Assert.Single(diff.FilesUnchanged);
    }

    [Fact]
    public async Task CalculateDiffAsync_WithDifferentHash_ShouldUpdate()
    {
        // Arrange
        var desiredFiles = new List<DeployableFileMapping>
        {
            new() { TargetPath = "bin/file1.dll", Hash = "hash_new" }
        };

        var currentOwnerships = new List<FileOwnership>
        {
            new()
            {
                DeployPath = "bin/file1.dll",
                ContentHash = "hash_old",
                IsShared = false,
                ReferenceCount = 1
            }
        };

        _ownershipRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentOwnerships);

        // Act
        var diff = await _diffEngine.CalculateDiffAsync(desiredFiles, Guid.NewGuid());

        // Assert
        Assert.Empty(diff.FilesToAdd);
        Assert.Single(diff.FilesToUpdate);
        Assert.Empty(diff.FilesToRemove);
        Assert.Empty(diff.FilesUnchanged);
    }

    [Fact]
    public async Task CalculateDiffAsync_WithRemovedFiles_ShouldRemove()
    {
        // Arrange
        var desiredFiles = new List<DeployableFileMapping>();

        var currentOwnerships = new List<FileOwnership>
        {
            new()
            {
                DeployPath = "bin/file1.dll",
                ContentHash = "hash1",
                IsShared = false,
                ReferenceCount = 1
            }
        };

        _ownershipRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentOwnerships);

        // Act
        var diff = await _diffEngine.CalculateDiffAsync(desiredFiles, Guid.NewGuid());

        // Assert
        Assert.Empty(diff.FilesToAdd);
        Assert.Empty(diff.FilesToUpdate);
        Assert.Single(diff.FilesToRemove);
        Assert.Empty(diff.FilesUnchanged);
    }

    [Fact]
    public async Task CalculateDiffAsync_WithSharedResource_ShouldNotRemove()
    {
        // Arrange
        var desiredFiles = new List<DeployableFileMapping>();

        var currentOwnerships = new List<FileOwnership>
        {
            new()
            {
                DeployPath = "bin/shared.dll",
                ContentHash = "hash1",
                IsShared = true,
                ReferenceCount = 2  // 被多个模组引用
            }
        };

        _ownershipRepoMock
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(currentOwnerships);

        // Act
        var diff = await _diffEngine.CalculateDiffAsync(desiredFiles, Guid.NewGuid());

        // Assert
        // 共享资源且引用计数 > 1，不应该删除
        Assert.Empty(diff.FilesToRemove);
    }

    [Fact]
    public void IsEmptyDiff_WithNoChanges_ShouldReturnTrue()
    {
        // Arrange
        var diff = new DeploymentDiff
        {
            FilesToAdd = new List<DeployableFileMapping>(),
            FilesToUpdate = new List<DeployableFileMapping>(),
            FilesToRemove = new List<DeployableFileMapping>()
        };

        // Act
        var isEmpty = _diffEngine.IsEmptyDiff(diff);

        // Assert
        Assert.True(isEmpty);
    }

    [Fact]
    public void IsEmptyDiff_WithChanges_ShouldReturnFalse()
    {
        // Arrange
        var diff = new DeploymentDiff
        {
            FilesToAdd = new List<DeployableFileMapping>
            {
                new() { TargetPath = "bin/file1.dll" }
            },
            FilesToUpdate = new List<DeployableFileMapping>(),
            FilesToRemove = new List<DeployableFileMapping>()
        };

        // Act
        var isEmpty = _diffEngine.IsEmptyDiff(diff);

        // Assert
        Assert.False(isEmpty);
    }
}
