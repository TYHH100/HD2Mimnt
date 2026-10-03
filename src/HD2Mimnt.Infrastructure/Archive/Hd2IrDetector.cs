using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>
/// HD2IR content detector implementation
/// </summary>
public class Hd2IrDetector : IHd2IrDetector
{
    private readonly ILogger<Hd2IrDetector>? _logger;

    // HD2IR 特征文件扩展名
    private static readonly string[] Hd2IrExtensions = new[]
    {
        ".hd2preview",
        ".hd2phys",
        ".hd2pose",
        ".rigbin",
        ".hd2shared"
    };

    // HD2IR 特征路径
    private static readonly string[] Hd2IrPaths = new[]
    {
        "HD2IndependentRig",
        "bin/HD2IndependentRig"
    };

    // HD2IR 特征文件名
    private static readonly string[] Hd2IrFileNames = new[]
    {
        "difference.ini",
        "isolation.ini"
    };

    public Hd2IrDetector(ILogger<Hd2IrDetector>? logger = null)
    {
        _logger = logger;
    }

    public async Task<DetectionResult> DetectAsync(ArchiveNode node, CancellationToken cancellationToken = default)
    {
        var result = new DetectionResult
        {
            ComponentType = ComponentType.Unknown,
            Confidence = DetectionConfidence.None
        };

        var allFiles = GetAllFiles(node);
        return await DetectFromPathsAsync(allFiles, cancellationToken);
    }

    public async Task<DetectionResult> DetectFromPathsAsync(IEnumerable<string> filePaths, CancellationToken cancellationToken = default)
    {
        var result = new DetectionResult
        {
            ComponentType = ComponentType.Unknown,
            Confidence = DetectionConfidence.None
        };

        var paths = filePaths.ToList();
        var reasons = new List<string>();

        // Priority 1: .hd2irpack.zip extension
        if (paths.Any(p => p.EndsWith(".hd2irpack.zip", StringComparison.OrdinalIgnoreCase)))
        {
            result.IsHd2Ir = true;
            result.ComponentType = ComponentType.HD2IR;
            result.Confidence = DetectionConfidence.Absolute;
            reasons.Add("文件扩展名匹配 .hd2irpack.zip");
            result.DetectedFiles.AddRange(paths.Where(p => p.EndsWith(".hd2irpack.zip", StringComparison.OrdinalIgnoreCase)));
        }

        // Priority 2: HD2IndependentRig directory structure
        var hd2IrPathMatches = paths.Where(p => Hd2IrPaths.Any(pattern =>
            p.Contains(pattern, StringComparison.OrdinalIgnoreCase))).ToList();

        if (hd2IrPathMatches.Any())
        {
            result.IsHd2Ir = true;
            result.ComponentType = ComponentType.HD2IR;
            if (result.Confidence < DetectionConfidence.High)
                result.Confidence = DetectionConfidence.High;
            reasons.Add($"包含 HD2IndependentRig 路径结构 ({hd2IrPathMatches.Count} 个文件)");
            result.DetectedStructure.AddRange(hd2IrPathMatches.Take(5));
        }

        // Priority 3: HD2IR characteristic file extensions
        var extensionMatches = paths.Where(p => Hd2IrExtensions.Any(ext =>
            p.EndsWith(ext, StringComparison.OrdinalIgnoreCase))).ToList();

        if (extensionMatches.Any())
        {
            result.IsHd2Ir = true;
            result.ComponentType = ComponentType.HD2IR;
            if (result.Confidence < DetectionConfidence.High)
                result.Confidence = DetectionConfidence.High;
            reasons.Add($"包含 HD2IR 特征文件扩展名: {string.Join(", ", extensionMatches.Select(Path.GetExtension).Distinct().Take(3))}");
            result.DetectedFiles.AddRange(extensionMatches.Take(10));
        }

        // Priority 4: HD2IR configuration files
        var configMatches = paths.Where(p => Hd2IrFileNames.Any(name =>
            Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase))).ToList();

        if (configMatches.Any())
        {
            result.IsHd2Ir = true;
            result.ComponentType = ComponentType.HD2IR;
            if (result.Confidence < DetectionConfidence.Medium)
                result.Confidence = DetectionConfidence.Medium;
            reasons.Add($"包含 HD2IR 配置文件: {string.Join(", ", configMatches.Select(Path.GetFileName))}");
            result.DetectedFiles.AddRange(configMatches);
        }

        result.Reason = string.Join("; ", reasons);

        if (result.IsHd2Ir)
        {
            _logger?.LogInformation("检测到 HD2IR 内容, 置信度: {Confidence}, 原因: {Reason}",
                result.Confidence, result.Reason);
        }

        return await Task.FromResult(result);
    }

    public IEnumerable<string> GetHd2IrPatterns()
    {
        var patterns = new List<string>();
        patterns.Add("*.hd2irpack.zip");
        patterns.AddRange(Hd2IrExtensions.Select(ext => $"*{ext}"));
        patterns.AddRange(Hd2IrPaths.Select(path => $"*{path}*"));
        patterns.AddRange(Hd2IrFileNames);
        return patterns;
    }

    private List<string> GetAllFiles(ArchiveNode node)
    {
        var files = new List<string>();

        if (!node.IsDirectory)
        {
            files.Add(node.FullPath);
        }

        foreach (var child in node.Children)
        {
            files.AddRange(GetAllFiles(child));
        }

        return files;
    }
}
