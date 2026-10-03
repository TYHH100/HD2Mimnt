using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Archive;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Archive;

public class ModMergerTests
{
    private readonly ModMerger _merger;
    private readonly Mock<ILogger<ModMerger>> _loggerMock;

    public ModMergerTests()
    {
        _loggerMock = new Mock<ILogger<ModMerger>>();
        _merger = new ModMerger(_loggerMock.Object);
    }

    [Fact]
    public async Task MergeAsync_WithEmptyCandidates_ShouldReturnIgnoreDecision()
    {
        // Arrange
        var candidates = new List<ComponentCandidate>();

        // Act
        var result = await _merger.MergeAsync(candidates);

        // Assert
        Assert.Equal(MergeDecision.Ignore, result.Decision);
        Assert.Empty(result.LogicalMods);
    }

    [Fact]
    public async Task MergeAsync_WithSingleCandidate_ShouldCreateSingleLogicalMod()
    {
        // Arrange
        var candidates = new List<ComponentCandidate>
        {
            new()
            {
                ComponentType = ComponentType.HD2IR,
                Name = "AIRI.hd2irpack.zip",
                SourceNode = new ArchiveNode { Name = "AIRI.hd2irpack.zip" },
                DetectionResult = new DetectionResult { ComponentType = ComponentType.HD2IR, Confidence = DetectionConfidence.High },
                Size = 1000000
            }
        };

        // Act
        var result = await _merger.MergeAsync(candidates);

        // Assert
        Assert.Single(result.LogicalMods);
        Assert.Equal("AIRI.hd2irpack", result.LogicalMods[0].Name);
        Assert.Single(result.LogicalMods[0].Components);
    }

    [Fact]
    public async Task MergeAsync_WithHd2IrAndModel_ShouldMergeIntoOne()
    {
        // Arrange
        var parentNode = new ArchiveNode { Id = Guid.NewGuid(), Name = "VRChat_AIRI.zip" };
        var candidates = new List<ComponentCandidate>
        {
            new()
            {
                ComponentType = ComponentType.HD2IR,
                Name = "AIRI.hd2irpack.zip",
                SourceNode = new ArchiveNode { Name = "AIRI.hd2irpack.zip", Parent = parentNode },
                DetectionResult = new DetectionResult { ComponentType = ComponentType.HD2IR, Confidence = DetectionConfidence.High },
                Size = 1000000
            },
            new()
            {
                ComponentType = ComponentType.Model,
                Name = "AIRI_Model.zip",
                SourceNode = new ArchiveNode { Name = "AIRI_Model.zip", Parent = parentNode },
                DetectionResult = new DetectionResult { ComponentType = ComponentType.Model, Confidence = DetectionConfidence.Medium },
                Size = 5000000
            }
        };

        // Act
        var result = await _merger.MergeAsync(candidates);

        // Assert
        Assert.Single(result.LogicalMods);
        Assert.Equal(2, result.LogicalMods[0].Components.Count);
        Assert.Contains(result.LogicalMods[0].Components, c => c.Type == ComponentType.HD2IR);
        Assert.Contains(result.LogicalMods[0].Components, c => c.Type == ComponentType.Model);
    }

    [Fact]
    public void ShouldMerge_WithSameParent_ShouldReturnTrue()
    {
        // Arrange
        var parentNode = new ArchiveNode { Id = Guid.NewGuid(), Name = "VRChat_AIRI.zip" };
        var candidates = new List<ComponentCandidate>
        {
            new()
            {
                ComponentType = ComponentType.HD2IR,
                Name = "AIRI.hd2irpack.zip",
                SourceNode = new ArchiveNode { Name = "AIRI.hd2irpack.zip", Parent = parentNode },
                DetectionResult = new DetectionResult()
            },
            new()
            {
                ComponentType = ComponentType.Model,
                Name = "AIRI_Model.zip",
                SourceNode = new ArchiveNode { Name = "AIRI_Model.zip", Parent = parentNode },
                DetectionResult = new DetectionResult()
            }
        };

        // Act
        var result = _merger.ShouldMerge(candidates);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CalculateMergeConfidence_WithHighScoreFactors_ShouldReturnHighConfidence()
    {
        // Arrange
        var parentNode = new ArchiveNode { Id = Guid.NewGuid(), Name = "VRChat_AIRI.zip" };
        var candidates = new List<ComponentCandidate>
        {
            new()
            {
                ComponentType = ComponentType.HD2IR,
                Name = "AIRI.hd2irpack.zip",
                SourceNode = new ArchiveNode { Name = "AIRI.hd2irpack.zip", Parent = parentNode },
                DetectionResult = new DetectionResult { Confidence = DetectionConfidence.High }
            },
            new()
            {
                ComponentType = ComponentType.Model,
                Name = "AIRI_Model.zip",
                SourceNode = new ArchiveNode { Name = "AIRI_Model.zip", Parent = parentNode },
                DetectionResult = new DetectionResult { Confidence = DetectionConfidence.High }
            }
        };

        // Act
        var confidence = _merger.CalculateMergeConfidence(candidates);

        // Assert
        Assert.True(confidence >= DetectionConfidence.Medium);
    }
}
