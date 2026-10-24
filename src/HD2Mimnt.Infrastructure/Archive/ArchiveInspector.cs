using System.IO.Compression;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Infrastructure.Archive;

/// <summary>
/// Implementation of archive inspection without full extraction
/// </summary>
public class ArchiveInspector : IArchiveInspector
{
    private readonly ILogger<ArchiveInspector>? _logger;
    private readonly IHd2IrDetector _hd2IrDetector;
    private readonly IComponentDetector _componentDetector;

    public ArchiveInspector(
        IHd2IrDetector hd2IrDetector,
        IComponentDetector componentDetector,
        ILogger<ArchiveInspector>? logger = null)
    {
        _hd2IrDetector = hd2IrDetector;
        _componentDetector = componentDetector;
        _logger = logger;
    }

    public async Task<ArchiveTree> InspectAsync(string archivePath, int maxDepth = 5, CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("开始分析归档: {ArchivePath}, 最大深度: {MaxDepth}", archivePath, maxDepth);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException($"归档文件不存在: {archivePath}");
        }

        var tree = new ArchiveTree();
        var rootNode = new ArchiveNode
        {
            Name = Path.GetFileName(archivePath),
            PhysicalPath = archivePath,
            FullPath = archivePath,
            IsArchive = true,
            Depth = 0,
            Size = new FileInfo(archivePath).Length,
            LastModified = File.GetLastWriteTimeUtc(archivePath),
            ArchiveType = await GetArchiveTypeAsync(archivePath, cancellationToken)
        };

        tree.Root = rootNode;

        // Build the tree recursively
        await BuildTreeAsync(rootNode, maxDepth, cancellationToken);

        // Calculate statistics
        tree.MaxDepth = CalculateMaxDepth(rootNode);
        tree.TotalNodes = CountNodes(rootNode);
        tree.TotalArchives = CountArchives(rootNode);
        tree.TotalSize = rootNode.Size;

        // Extract component candidates
        tree.ComponentCandidates = await _componentDetector.ExtractCandidatesAsync(tree, cancellationToken);

        _logger?.LogInformation("归档分析完成: {TotalNodes} 个节点, {TotalArchives} 个归档, {ComponentCount} 个组件候选",
            tree.TotalNodes, tree.TotalArchives, tree.ComponentCandidates.Count);

        return tree;
    }

    private async Task BuildTreeAsync(ArchiveNode node, int maxDepth, CancellationToken cancellationToken)
    {
        if (node.Depth >= maxDepth)
        {
            _logger?.LogWarning("达到最大深度 {MaxDepth}, 停止继续递归", maxDepth);
            return;
        }

        if (!node.IsArchive)
        {
            return;
        }

        try
        {
            var entries = await ListEntriesAsync(node.PhysicalPath ?? node.FullPath, cancellationToken);
            node.EntryCount = entries.Count;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var childNode = new ArchiveNode
                {
                    Name = Path.GetFileName(entry),
                    FullPath = entry,
                    Parent = node,
                    Depth = node.Depth + 1,
                    IsDirectory = entry.EndsWith("/") || entry.EndsWith("\\")
                };

                // Check if this entry is itself an archive
                if (!childNode.IsDirectory && IsArchive(entry))
                {
                    childNode.IsArchive = true;
                    childNode.ArchiveType = await GetArchiveTypeAsync(entry, cancellationToken);

                    // Recursively build tree for nested archives
                    // Note: We don't extract yet, just mark for potential future extraction
                }

                node.Children.Add(childNode);
            }

            // Detect components in this node
            node.DetectionResult = await _componentDetector.DetectComponentAsync(node, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "分析归档节点失败: {NodePath}", node.FullPath);
        }
    }

    public bool IsArchive(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".zip" => true,
            ".7z" => true,
            ".rar" => true,
            ".tar" => true,
            ".gz" => true,
            ".bz2" => true,
            _ => false
        };
    }

    public async Task<ArchiveType> GetArchiveTypeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        return await Task.FromResult(extension switch
        {
            ".zip" => ArchiveType.Zip,
            ".7z" => ArchiveType.SevenZip,
            ".rar" => ArchiveType.Rar,
            ".tar" => ArchiveType.Tar,
            ".gz" or ".gzip" => ArchiveType.GZip,
            _ => ArchiveType.Unknown
        });
    }

    public async Task<List<string>> ListEntriesAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var entries = new List<string>();

        try
        {
            var archiveType = await GetArchiveTypeAsync(archivePath, cancellationToken);

            switch (archiveType)
            {
                case ArchiveType.Zip:
                    using (var archive = ZipFile.OpenRead(archivePath))
                    {
                        foreach (var entry in archive.Entries)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            entries.Add(entry.FullName);
                        }
                    }
                    break;

                case ArchiveType.SevenZip:
                case ArchiveType.Rar:
                    // TODO: Implement 7z/RAR support when needed
                    _logger?.LogWarning("暂不支持 {ArchiveType} 格式", archiveType);
                    break;

                default:
                    _logger?.LogWarning("未知归档类型: {ArchivePath}", archivePath);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "列出归档条目失败: {ArchivePath}", archivePath);
        }

        return entries;
    }

    private int CalculateMaxDepth(ArchiveNode node)
    {
        if (node.Children.Count == 0)
        {
            return node.Depth;
        }

        return node.Children.Max(child => CalculateMaxDepth(child));
    }

    private int CountNodes(ArchiveNode node)
    {
        return 1 + node.Children.Sum(child => CountNodes(child));
    }

    private int CountArchives(ArchiveNode node)
    {
        var count = node.IsArchive ? 1 : 0;
        return count + node.Children.Sum(child => CountArchives(child));
    }
}
