using System.Security.Cryptography;
using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Analysis;

/// <summary>
/// Implementation of IResourceAnalyzer for analyzing Mod resources.
/// </summary>
public class ResourceAnalyzer : IResourceAnalyzer
{
    private static readonly string[] V1Extensions = { ".patch_0", ".gpu_resources", ".stream" };

    private static readonly Dictionary<string, string> FileTypeMap = new()
    {
        { ".json", "JSON Configuration" },
        { ".patch_0", "V1 Patch File" },
        { ".gpu_resources", "V1 GPU Resources" },
        { ".stream", "V1 Stream Data" },
        { ".png", "Image" },
        { ".jpg", "Image" },
        { ".jpeg", "Image" },
        { ".bmp", "Image" },
        { ".txt", "Text Document" },
        { ".md", "Markdown Document" },
        { ".dll", "Library" },
        { ".exe", "Executable" },
        { ".lua", "Lua Script" },
        { ".xml", "XML Configuration" },
        { ".ini", "INI Configuration" },
        { ".cfg", "Configuration File" }
    };

    /// <inheritdoc/>
    public async Task<ResourceNode> AnalyzeAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath))
        {
            throw new ArgumentException("Mod path cannot be null or empty.", nameof(modPath));
        }

        if (!Directory.Exists(modPath))
        {
            throw new DirectoryNotFoundException($"Mod directory not found: {modPath}");
        }

        var rootInfo = new DirectoryInfo(modPath);
        var rootNode = await BuildTreeAsync(rootInfo, modPath, cancellationToken);
        return rootNode;
    }

    /// <inheritdoc/>
    public async Task<ResourceMetadata> GetMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        var fileInfo = new FileInfo(filePath);
        var extension = fileInfo.Extension.ToLowerInvariant();

        var metadata = new ResourceMetadata
        {
            FilePath = filePath,
            Size = fileInfo.Length,
            Created = fileInfo.CreationTime,
            Modified = fileInfo.LastWriteTime,
            FileType = GetFileType(extension),
            IsV1File = V1Extensions.Contains(extension)
        };

        // Calculate hash
        metadata.Hash = await CalculateHashAsync(filePath, cancellationToken);

        // Detect references for JSON files
        if (extension == ".json")
        {
            metadata.References = await DetectReferencesAsync(filePath, cancellationToken);
        }

        return metadata;
    }

    /// <inheritdoc/>
    public async Task<string> CalculateHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();

        var hashBytes = await sha256.ComputeHashAsync(stream, cancellationToken);
        return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
    }

    /// <inheritdoc/>
    public async Task<List<ResourceReference>> DetectReferencesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var references = new List<ResourceReference>();

        if (string.IsNullOrWhiteSpace(filePath))
        {
            return references;
        }

        if (!File.Exists(filePath))
        {
            return references;
        }

        var extension = Path.GetExtension(filePath).ToLowerInvariant();

        // Only process JSON files for now
        if (extension != ".json")
        {
            return references;
        }

        try
        {
            var jsonContent = await File.ReadAllTextAsync(filePath, cancellationToken);
            var doc = JsonDocument.Parse(jsonContent);

            var modDir = Path.GetDirectoryName(filePath) ?? string.Empty;

            // Recursively search for potential file path references
            SearchJsonForReferences(doc.RootElement, references, filePath, modDir);
        }
        catch (JsonException)
        {
            // Invalid JSON, skip reference detection
        }
        catch (Exception)
        {
            // Other errors, skip
        }

        return references;
    }

    private async Task<ResourceNode> BuildTreeAsync(DirectoryInfo dirInfo, string rootPath, CancellationToken cancellationToken)
    {
        var node = new ResourceNode
        {
            Name = dirInfo.Name,
            Path = dirInfo.FullName,
            RelativePath = Path.GetRelativePath(rootPath, dirInfo.FullName),
            Type = ResourceNodeType.Directory,
            LastModified = dirInfo.LastWriteTime,
            Children = new List<ResourceNode>()
        };

        // Add files
        foreach (var file in dirInfo.GetFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileNode = new ResourceNode
            {
                Name = file.Name,
                Path = file.FullName,
                RelativePath = Path.GetRelativePath(rootPath, file.FullName),
                Type = ResourceNodeType.File,
                Size = file.Length,
                LastModified = file.LastWriteTime,
                Extension = file.Extension.ToLowerInvariant()
            };

            node.Children.Add(fileNode);
        }

        // Add subdirectories
        foreach (var subDir in dirInfo.GetDirectories())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var subNode = await BuildTreeAsync(subDir, rootPath, cancellationToken);
            node.Children.Add(subNode);
        }

        return node;
    }

    private void SearchJsonForReferences(JsonElement element, List<ResourceReference> references, string sourceFile, string modDir)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (value != null && LooksLikeFilePath(value))
                {
                    var targetPath = Path.Combine(modDir, value);
                    var targetExists = File.Exists(targetPath);

                    references.Add(new ResourceReference
                    {
                        SourceFile = sourceFile,
                        TargetFile = value,
                        Type = ReferenceType.Direct,
                        Context = "JSON value",
                        TargetExists = targetExists
                    });
                }
                break;

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    SearchJsonForReferences(property.Value, references, sourceFile, modDir);
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    SearchJsonForReferences(item, references, sourceFile, modDir);
                }
                break;
        }
    }

    private bool LooksLikeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Check if it has an extension (with or without path separators)
        return Path.HasExtension(value);
    }

    private string GetFileType(string extension)
    {
        return FileTypeMap.TryGetValue(extension, out var fileType) ? fileType : "Unknown";
    }
}
