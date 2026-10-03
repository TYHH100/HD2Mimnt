using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>
/// Component type detector implementation
/// </summary>
public class ComponentDetector : IComponentDetector
{
    private readonly ILogger<ComponentDetector>? _logger;
    private readonly IHd2IrDetector _hd2IrDetector;

    // Component detection patterns
    private static readonly Dictionary<ComponentType, string[]> ComponentPatterns = new()
    {
        [ComponentType.Model] = new[] { "model", "models", ".fbx", ".obj", ".dae", ".blend" },
        [ComponentType.Texture] = new[] { "texture", "textures", "tex", ".dds", ".png", ".jpg", ".tga" },
        [ComponentType.Material] = new[] { "material", "materials", "mat", ".mat", ".mtl" },
        [ComponentType.Animation] = new[] { "animation", "animations", "anim", ".anim" },
        [ComponentType.Lua] = new[] { "lua", ".lua", "script", "scripts" },
        [ComponentType.Addon] = new[] { "addon", "addons", "add-on" },
        [ComponentType.Appearance] = new[] { "appearance", "幻化", "transmog" }
    };

    public ComponentDetector(IHd2IrDetector hd2IrDetector, ILogger<ComponentDetector>? logger = null)
    {
        _hd2IrDetector = hd2IrDetector;
        _logger = logger;
    }

    public async Task<DetectionResult> DetectComponentAsync(ArchiveNode node, CancellationToken cancellationToken = default)
    {
        // First check if it's HD2IR
        var hd2IrResult = await _hd2IrDetector.DetectAsync(node, cancellationToken);
        if (hd2IrResult.IsHd2Ir)
        {
            return hd2IrResult;
        }

        // Check other component types
        var allFiles = GetAllFiles(node);
        var result = new DetectionResult
        {
            ComponentType = ComponentType.Unknown,
            Confidence = DetectionConfidence.None
        };

        foreach (var (componentType, patterns) in ComponentPatterns)
        {
            var matchCount = 0;
            var matchedFiles = new List<string>();

            foreach (var file in allFiles)
            {
                foreach (var pattern in patterns)
                {
                    if (file.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        matchCount++;
                        matchedFiles.Add(file);
                        break;
                    }
                }
            }

            if (matchCount > 0)
            {
                var confidence = matchCount switch
                {
                    >= 10 => DetectionConfidence.High,
                    >= 5 => DetectionConfidence.Medium,
                    _ => DetectionConfidence.Low
                };

                if (confidence > result.Confidence)
                {
                    result.ComponentType = componentType;
                    result.Confidence = confidence;
                    result.DetectedFiles = matchedFiles.Take(10).ToList();
                    result.Reason = $"检测到 {matchCount} 个匹配 {componentType} 特征的文件";
                }
            }
        }

        return result;
    }

    public async Task<List<ComponentCandidate>> ExtractCandidatesAsync(ArchiveTree tree, CancellationToken cancellationToken = default)
    {
        var candidates = new List<ComponentCandidate>();

        await ExtractCandidatesFromNodeAsync(tree.Root, candidates, cancellationToken);

        _logger?.LogInformation("从归档树提取了 {Count} 个组件候选", candidates.Count);

        return candidates;
    }

    private async Task ExtractCandidatesFromNodeAsync(ArchiveNode node, List<ComponentCandidate> candidates, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Detect component for this node
        var detection = await DetectComponentAsync(node, cancellationToken);

        if (detection.ComponentType != ComponentType.Unknown && detection.Confidence >= DetectionConfidence.Low)
        {
            var candidate = new ComponentCandidate
            {
                ComponentType = detection.ComponentType,
                Name = node.Name,
                SourceNode = node,
                DetectionResult = detection,
                RelatedFiles = detection.DetectedFiles,
                Size = node.Size
            };

            candidates.Add(candidate);
        }

        // Recursively process children
        foreach (var child in node.Children)
        {
            await ExtractCandidatesFromNodeAsync(child, candidates, cancellationToken);
        }
    }

    public IEnumerable<string> GetPatternsForType(ComponentType componentType)
    {
        if (componentType == ComponentType.HD2IR)
        {
            return _hd2IrDetector.GetHd2IrPatterns();
        }

        if (ComponentPatterns.TryGetValue(componentType, out var patterns))
        {
            return patterns;
        }

        return Enumerable.Empty<string>();
    }

    private List<string> GetAllFiles(ArchiveNode node)
    {
        var files = new List<string>();

        if (!node.IsDirectory && !node.IsArchive)
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
