using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>
/// Mod merger implementation for combining component candidates into logical mods
/// </summary>
public class ModMerger : IModMerger
{
    private readonly ILogger<ModMerger>? _logger;

    public ModMerger(ILogger<ModMerger>? logger = null)
    {
        _logger = logger;
    }

    public async Task<MergeResult> MergeAsync(List<ComponentCandidate> candidates, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("开始合并 {Count} 个组件候选", candidates.Count);

        var result = new MergeResult
        {
            MergedComponents = candidates
        };

        if (candidates.Count == 0)
        {
            result.Decision = MergeDecision.Ignore;
            result.Reason = "没有组件候选";
            return result;
        }

        // Calculate merge confidence
        result.Confidence = CalculateMergeConfidence(candidates);
        result.Decision = DetermineMergeDecision(result.Confidence);

        if (ShouldMerge(candidates))
        {
            // Create a single logical mod from all candidates
            var logicalMod = await CreateLogicalModFromCandidatesAsync(candidates, cancellationToken);
            result.LogicalMods.Add(logicalMod);
            result.Reason = BuildMergeReason(candidates, result.Confidence);
        }
        else
        {
            // Create separate logical mods for each candidate
            foreach (var candidate in candidates)
            {
                var logicalMod = await CreateLogicalModFromCandidatesAsync(new List<ComponentCandidate> { candidate }, cancellationToken);
                result.LogicalMods.Add(logicalMod);
            }
            result.Decision = MergeDecision.SeparateImport;
            result.Reason = "组件候选不满足合并条件，分别导入";
        }

        _logger?.LogInformation("合并完成: {Decision}, 生成 {Count} 个逻辑模组", result.Decision, result.LogicalMods.Count);
        result.RequiredConfirmation = result.Decision == MergeDecision.ManualConfirmation;

        return result;
    }

    public bool ShouldMerge(List<ComponentCandidate> candidates)
    {
        if (candidates.Count <= 1)
        {
            return true;
        }

        // Check if all candidates are from the same parent container
        var parentNodes = candidates.Select(c => c.SourceNode.Parent?.Id).Distinct().ToList();
        if (parentNodes.Count == 1 && parentNodes[0] != null)
        {
            _logger?.LogDebug("所有组件来自同一个父容器，建议合并");
            return true;
        }

        // Check name similarity
        var names = candidates.Select(c => c.Name).ToList();
        if (HasNameSimilarity(names))
        {
            _logger?.LogDebug("组件名称具有相似性，建议合并");
            return true;
        }

        // Check if there's HD2IAS + Model combination (common pattern)
        var hasHd2Ir = candidates.Any(c => c.ComponentType == ComponentType.HD2IAS);
        var hasModel = candidates.Any(c => c.ComponentType == ComponentType.Model);
        if (hasHd2Ir && hasModel)
        {
            _logger?.LogDebug("检测到 HD2IAS + 模型组合，建议合并");
            return true;
        }

        return false;
    }

    public DetectionConfidence CalculateMergeConfidence(List<ComponentCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return DetectionConfidence.None;
        }

        if (candidates.Count == 1)
        {
            return DetectionConfidence.High;
        }

        var score = 0;

        // Same parent container: +3
        var parentNodes = candidates.Select(c => c.SourceNode.Parent?.Id).Distinct().ToList();
        if (parentNodes.Count == 1 && parentNodes[0] != null)
        {
            score += 3;
        }

        // Name similarity: +2
        var names = candidates.Select(c => c.Name).ToList();
        if (HasNameSimilarity(names))
        {
            score += 2;
        }

        // HD2IAS + Model pattern: +2
        var hasHd2Ir = candidates.Any(c => c.ComponentType == ComponentType.HD2IAS);
        var hasModel = candidates.Any(c => c.ComponentType == ComponentType.Model);
        if (hasHd2Ir && hasModel)
        {
            score += 2;
        }

        // All candidates have high detection confidence: +1
        if (candidates.All(c => c.DetectionResult.Confidence >= DetectionConfidence.High))
        {
            score += 1;
        }

        return score switch
        {
            >= 6 => DetectionConfidence.High,
            >= 4 => DetectionConfidence.Medium,
            >= 2 => DetectionConfidence.Low,
            _ => DetectionConfidence.None
        };
    }

    private MergeDecision DetermineMergeDecision(DetectionConfidence confidence)
    {
        return confidence switch
        {
            DetectionConfidence.High or DetectionConfidence.Absolute => MergeDecision.AutomaticMerge,
            DetectionConfidence.Medium => MergeDecision.ManualConfirmation,
            DetectionConfidence.Low => MergeDecision.ManualConfirmation,
            _ => MergeDecision.SeparateImport
        };
    }

    private async Task<LogicalMod> CreateLogicalModFromCandidatesAsync(List<ComponentCandidate> candidates, CancellationToken cancellationToken)
    {
        var firstCandidate = candidates.First();
        var modName = ExtractModName(candidates);

        var logicalMod = new LogicalMod
        {
            Name = modName,
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow
        };

        foreach (var candidate in candidates)
        {
            var component = new LogicalModComponent
            {
                ComponentId = candidate.ComponentType.ToString().ToLowerInvariant(),
                Type = candidate.ComponentType,
                Name = candidate.Name,
                Size = candidate.Size,
                Metadata = candidate.Metadata
            };

            logicalMod.Components.Add(component);
            logicalMod.TotalSize += candidate.Size;
        }

        _logger?.LogDebug("创建逻辑模组: {Name}, {ComponentCount} 个组件", logicalMod.Name, logicalMod.Components.Count);

        return await Task.FromResult(logicalMod);
    }

    private string ExtractModName(List<ComponentCandidate> candidates)
    {
        if (candidates.Count == 1)
        {
            return Path.GetFileNameWithoutExtension(candidates[0].Name);
        }

        // Try to find common name prefix
        var names = candidates.Select(c => Path.GetFileNameWithoutExtension(c.Name)).ToList();
        var commonPrefix = FindCommonPrefix(names);

        if (!string.IsNullOrWhiteSpace(commonPrefix) && commonPrefix.Length > 3)
        {
            return commonPrefix.Trim('_', '-', ' ');
        }

        // Use the parent container name if available
        var parentName = candidates.FirstOrDefault()?.SourceNode.Parent?.Name;
        if (!string.IsNullOrWhiteSpace(parentName))
        {
            return Path.GetFileNameWithoutExtension(parentName);
        }

        // Fallback to first candidate name
        return Path.GetFileNameWithoutExtension(candidates[0].Name);
    }

    private bool HasNameSimilarity(List<string> names)
    {
        if (names.Count <= 1)
        {
            return true;
        }

        var cleanNames = names.Select(n => Path.GetFileNameWithoutExtension(n)).ToList();
        var commonPrefix = FindCommonPrefix(cleanNames);

        return !string.IsNullOrWhiteSpace(commonPrefix) && commonPrefix.Length > 3;
    }

    private string FindCommonPrefix(List<string> strings)
    {
        if (strings.Count == 0)
        {
            return string.Empty;
        }

        var first = strings[0];
        var prefix = string.Empty;

        for (int i = 0; i < first.Length; i++)
        {
            var c = first[i];
            if (strings.All(s => s.Length > i && s[i] == c))
            {
                prefix += c;
            }
            else
            {
                break;
            }
        }

        return prefix;
    }

    private string BuildMergeReason(List<ComponentCandidate> candidates, DetectionConfidence confidence)
    {
        var reasons = new List<string>();

        reasons.Add($"{candidates.Count} 个组件候选");

        var parentNodes = candidates.Select(c => c.SourceNode.Parent?.Id).Distinct().ToList();
        if (parentNodes.Count == 1 && parentNodes[0] != null)
        {
            reasons.Add("来自同一容器");
        }

        var names = candidates.Select(c => c.Name).ToList();
        if (HasNameSimilarity(names))
        {
            reasons.Add("名称相似");
        }

        var hasHd2Ir = candidates.Any(c => c.ComponentType == ComponentType.HD2IAS);
        var hasModel = candidates.Any(c => c.ComponentType == ComponentType.Model);
        if (hasHd2Ir && hasModel)
        {
            reasons.Add("HD2IAS+模型组合");
        }

        reasons.Add($"置信度: {confidence}");

        return string.Join(", ", reasons);
    }
}
