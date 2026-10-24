using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Archive;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Archive;

public class ComponentDetectorTests
{
    private readonly ComponentDetector _detector;
    private readonly Mock<ILogger<ComponentDetector>> _loggerMock;
    private readonly Hd2IrDetector _hd2IrDetector;

    public ComponentDetectorTests()
    {
        _loggerMock = new Mock<ILogger<ComponentDetector>>();
        _hd2IrDetector = new Hd2IrDetector();
        _detector = new ComponentDetector(_hd2IrDetector, _loggerMock.Object);
    }

    [Fact]
    public async Task DetectComponentAsync_WithModelFiles_ShouldDetectModel()
    {
        // Arrange
        var node = new ArchiveNode
        {
            Name = "Model.zip",
            Children = new List<ArchiveNode>
            {
                new() { Name = "character.fbx", FullPath = "models/character.fbx" },
                new() { Name = "weapon.obj", FullPath = "models/weapon.obj" }
            }
        };

        // Act
        var result = await _detector.DetectComponentAsync(node);

        // Assert
        Assert.Equal(ComponentType.Model, result.ComponentType);
        Assert.True(result.Confidence >= DetectionConfidence.Low);
    }

    [Fact]
    public async Task DetectComponentAsync_WithTextureFiles_ShouldDetectTexture()
    {
        // Arrange
        var node = new ArchiveNode
        {
            Name = "Texture.zip",
            Children = new List<ArchiveNode>
            {
                new() { Name = "diffuse.dds", FullPath = "textures/diffuse.dds" },
                new() { Name = "normal.png", FullPath = "textures/normal.png" },
                new() { Name = "specular.tga", FullPath = "textures/specular.tga" }
            }
        };

        // Act
        var result = await _detector.DetectComponentAsync(node);

        // Assert
        Assert.Equal(ComponentType.Texture, result.ComponentType);
        Assert.True(result.Confidence >= DetectionConfidence.Low);
    }

    [Fact]
    public async Task DetectComponentAsync_WithHd2IrContent_ShouldDetectHd2Ir()
    {
        // Arrange
        var node = new ArchiveNode
        {
            Name = "AIRI.hd2irpack.zip",
            Children = new List<ArchiveNode>
            {
                new() { Name = "character.hd2phys", FullPath = "bin/HD2IndependentRig/character.hd2phys" },
                new() { Name = "character.rigbin", FullPath = "bin/HD2IndependentRig/character.rigbin" }
            }
        };

        // Act
        var result = await _detector.DetectComponentAsync(node);

        // Assert
        Assert.True(result.IsHd2Ir);
        Assert.Equal(ComponentType.HD2IAS, result.ComponentType);
        Assert.True(result.Confidence >= DetectionConfidence.High);
    }

    [Fact]
    public void GetPatternsForType_Model_ShouldReturnModelPatterns()
    {
        // Act
        var patterns = _detector.GetPatternsForType(ComponentType.Model).ToList();

        // Assert
        Assert.NotEmpty(patterns);
        Assert.Contains(patterns, p => p.Contains("model", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetPatternsForType_HD2IR_ShouldReturnHd2IrPatterns()
    {
        // Act
        var patterns = _detector.GetPatternsForType(ComponentType.HD2IAS).ToList();

        // Assert
        Assert.NotEmpty(patterns);
        Assert.Contains(patterns, p => p.Contains(".hd2irpack.zip", StringComparison.OrdinalIgnoreCase));
    }
}
