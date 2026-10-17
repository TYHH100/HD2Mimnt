using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Archive;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace HD2Mimnt.Tests.Archive;

public class Hd2IrDetectorTests
{
    private readonly Hd2IrDetector _detector;
    private readonly Mock<ILogger<Hd2IrDetector>> _loggerMock;

    public Hd2IrDetectorTests()
    {
        _loggerMock = new Mock<ILogger<Hd2IrDetector>>();
        _detector = new Hd2IrDetector(_loggerMock.Object);
    }

    [Fact]
    public async Task DetectFromPathsAsync_WithHd2IrPackZip_ShouldDetectWithAbsoluteConfidence()
    {
        // Arrange
        var paths = new List<string>
        {
            "VRChat_AIRI.hd2irpack.zip"
        };

        // Act
        var result = await _detector.DetectFromPathsAsync(paths);

        // Assert
        Assert.True(result.IsHd2Ir);
        Assert.Equal(ComponentType.HD2IAS, result.ComponentType);
        Assert.Equal(DetectionConfidence.Absolute, result.Confidence);
        Assert.Contains(".hd2irpack.zip", result.Reason);
    }

    [Fact]
    public async Task DetectFromPathsAsync_WithHD2IndependentRigPath_ShouldDetectWithHighConfidence()
    {
        // Arrange
        var paths = new List<string>
        {
            "bin/HD2IndependentRig/character.hd2phys",
            "bin/HD2IndependentRig/character.rigbin",
            "bin/HD2IndependentRig/character.hd2pose"
        };

        // Act
        var result = await _detector.DetectFromPathsAsync(paths);

        // Assert
        Assert.True(result.IsHd2Ir);
        Assert.Equal(ComponentType.HD2IAS, result.ComponentType);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
        Assert.Contains("HD2IndependentRig", result.Reason);
    }

    [Fact]
    public async Task DetectFromPathsAsync_WithHd2IrExtensions_ShouldDetectWithHighConfidence()
    {
        // Arrange
        var paths = new List<string>
        {
            "character.hd2phys",
            "character.rigbin",
            "character.hd2pose",
            "character.hd2preview"
        };

        // Act
        var result = await _detector.DetectFromPathsAsync(paths);

        // Assert
        Assert.True(result.IsHd2Ir);
        Assert.Equal(ComponentType.HD2IAS, result.ComponentType);
        Assert.Equal(DetectionConfidence.High, result.Confidence);
    }

    [Fact]
    public async Task DetectFromPathsAsync_WithConfigFiles_ShouldDetectWithMediumConfidence()
    {
        // Arrange
        var paths = new List<string>
        {
            "difference.ini",
            "isolation.ini"
        };

        // Act
        var result = await _detector.DetectFromPathsAsync(paths);

        // Assert
        Assert.True(result.IsHd2Ir);
        Assert.Equal(ComponentType.HD2IAS, result.ComponentType);
        Assert.Equal(DetectionConfidence.Medium, result.Confidence);
    }

    [Fact]
    public async Task DetectFromPathsAsync_WithNoHd2IrContent_ShouldNotDetect()
    {
        // Arrange
        var paths = new List<string>
        {
            "model.fbx",
            "texture.png",
            "material.mat"
        };

        // Act
        var result = await _detector.DetectFromPathsAsync(paths);

        // Assert
        Assert.False(result.IsHd2Ir);
        Assert.Equal(ComponentType.Unknown, result.ComponentType);
        Assert.Equal(DetectionConfidence.None, result.Confidence);
    }

    [Fact]
    public void GetHd2IrPatterns_ShouldReturnExpectedPatterns()
    {
        // Act
        var patterns = _detector.GetHd2IrPatterns().ToList();

        // Assert
        Assert.NotEmpty(patterns);
        Assert.Contains(patterns, p => p.Contains(".hd2irpack.zip"));
        Assert.Contains(patterns, p => p.Contains(".hd2phys"));
        Assert.Contains(patterns, p => p.Contains(".rigbin"));
        Assert.Contains(patterns, p => p.Contains("HD2IndependentRig"));
    }
}
