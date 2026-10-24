using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Service for exploring and managing Mod resources.
/// </summary>
public class ResourceExplorerService : IResourceExplorer
{
    private readonly IResourceAnalyzer _resourceAnalyzer;
    private readonly IModRepository _modRepository;
    private readonly Dictionary<string, ResourceNode> _treeCache = new();
    private readonly object _cacheLock = new();

    public ResourceExplorerService(IResourceAnalyzer resourceAnalyzer, IModRepository modRepository)
    {
        _resourceAnalyzer = resourceAnalyzer ?? throw new ArgumentNullException(nameof(resourceAnalyzer));
        _modRepository = modRepository ?? throw new ArgumentNullException(nameof(modRepository));
    }

    /// <inheritdoc/>
    public async Task<ResourceNode> GetResourceTreeAsync(string modId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new ArgumentException("Mod ID cannot be null or empty.", nameof(modId));
        }

        // Check cache first
        if (!forceRefresh)
        {
            lock (_cacheLock)
            {
                if (_treeCache.TryGetValue(modId, out var cachedTree))
                {
                    return cachedTree;
                }
            }
        }

        // Get Mod from repository
        var mod = await _modRepository.GetByIdAsync(Guid.Parse(modId), cancellationToken);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        if (string.IsNullOrWhiteSpace(mod.Path))
        {
            throw new InvalidOperationException($"Mod has no local path: {modId}");
        }

        // Analyze and cache
        var tree = await _resourceAnalyzer.AnalyzeAsync(mod.Path, cancellationToken);

        lock (_cacheLock)
        {
            _treeCache[modId] = tree;
        }

        return tree;
    }

    /// <inheritdoc/>
    public async Task<List<ResourceNode>> SearchResourcesAsync(string modId, string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new ArgumentException("Mod ID cannot be null or empty.", nameof(modId));
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<ResourceNode>();
        }

        var tree = await GetResourceTreeAsync(modId, false, cancellationToken);
        var results = new List<ResourceNode>();

        SearchRecursive(tree, query.ToLowerInvariant(), results);

        return results;
    }

    /// <inheritdoc/>
    public async Task<ResourceDependency> GetDependenciesAsync(string modId, string resourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new ArgumentException("Mod ID cannot be null or empty.", nameof(modId));
        }

        if (string.IsNullOrWhiteSpace(resourcePath))
        {
            throw new ArgumentException("Resource path cannot be null or empty.", nameof(resourcePath));
        }

        var mod = await _modRepository.GetByIdAsync(Guid.Parse(modId), cancellationToken);
        if (mod == null)
        {
            throw new InvalidOperationException($"Mod not found: {modId}");
        }

        var fullPath = System.IO.Path.Combine(mod.Path, resourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Resource not found: {resourcePath}");
        }

        var dependency = new ResourceDependency
        {
            ModId = modId,
            ResourcePath = resourcePath,
            DependsOn = new List<string>(),
            RequiredBy = new List<string>()
        };

        // Get references from this file
        var references = await _resourceAnalyzer.DetectReferencesAsync(fullPath, cancellationToken);
        dependency.DependsOn.AddRange(references.Select(r => r.TargetFile));

        // Find files that reference this resource
        var tree = await GetResourceTreeAsync(modId, false, cancellationToken);
        var allFiles = GetAllFiles(tree);

        foreach (var fileNode in allFiles)
        {
            if (fileNode.Path == fullPath)
                continue;

            var fileRefs = await _resourceAnalyzer.DetectReferencesAsync(fileNode.Path, cancellationToken);
            if (fileRefs.Any(r => r.TargetFile == resourcePath || System.IO.Path.Combine(mod.Path, r.TargetFile) == fullPath))
            {
                dependency.RequiredBy.Add(fileNode.RelativePath);
            }
        }

        // Check for V1 file pairing
        var extension = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
        if (extension == ".patch_0")
        {
            var streamFile = System.IO.Path.ChangeExtension(resourcePath, ".stream");
            if (File.Exists(System.IO.Path.Combine(mod.Path, streamFile)))
            {
                dependency.DependsOn.Add(streamFile);
            }
        }
        else if (extension == ".stream")
        {
            var patchFile = System.IO.Path.ChangeExtension(resourcePath, ".patch_0");
            if (File.Exists(System.IO.Path.Combine(mod.Path, patchFile)))
            {
                dependency.RequiredBy.Add(patchFile);
            }
        }

        return dependency;
    }

    /// <inheritdoc/>
    public async Task<List<string>> ValidateIntegrityAsync(string modId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modId))
        {
            throw new ArgumentException("Mod ID cannot be null or empty.", nameof(modId));
        }

        var errors = new List<string>();

        var mod = await _modRepository.GetByIdAsync(Guid.Parse(modId), cancellationToken);
        if (mod == null)
        {
            errors.Add($"Mod not found: {modId}");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(mod.Path) || !Directory.Exists(mod.Path))
        {
            errors.Add($"Mod directory not found: {mod.Path}");
            return errors;
        }

        var tree = await GetResourceTreeAsync(modId, false, cancellationToken);
        var allFiles = GetAllFiles(tree);

        // Check for broken references
        foreach (var fileNode in allFiles)
        {
            var references = await _resourceAnalyzer.DetectReferencesAsync(fileNode.Path, cancellationToken);
            foreach (var reference in references.Where(r => !r.TargetExists))
            {
                errors.Add($"Missing reference: {fileNode.RelativePath} → {reference.TargetFile}");
            }
        }

        // Check for V1 file pairing
        var patchFiles = allFiles.Where(f => f.Extension == ".patch_0").ToList();
        foreach (var patchFile in patchFiles)
        {
            var streamPath = Path.ChangeExtension(patchFile.Path, ".stream");
            if (!File.Exists(streamPath))
            {
                errors.Add($"Missing paired .stream file for: {patchFile.RelativePath}");
            }
        }

        var streamFiles = allFiles.Where(f => f.Extension == ".stream").ToList();
        foreach (var streamFile in streamFiles)
        {
            var patchPath = Path.ChangeExtension(streamFile.Path, ".patch_0");
            if (!File.Exists(patchPath))
            {
                errors.Add($"Missing paired .patch_0 file for: {streamFile.RelativePath}");
            }
        }

        return errors;
    }

    private void SearchRecursive(ResourceNode node, string query, List<ResourceNode> results)
    {
        if (node.Name.ToLowerInvariant().Contains(query) || node.RelativePath.ToLowerInvariant().Contains(query))
        {
            results.Add(node);
        }

        foreach (var child in node.Children)
        {
            SearchRecursive(child, query, results);
        }
    }

    private List<ResourceNode> GetAllFiles(ResourceNode root)
    {
        var files = new List<ResourceNode>();

        if (root.IsFile)
        {
            files.Add(root);
        }

        foreach (var child in root.Children)
        {
            files.AddRange(GetAllFiles(child));
        }

        return files;
    }
}
