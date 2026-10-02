using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Infrastructure.Workflows;

/// <summary>
/// Workflow for Integrated Armor Mods.
/// </summary>
public class IntegratedArmorWorkflow : IWorkflow
{
    private const string ManifestFileName = "armor_manifest.json";

    /// <inheritdoc/>
    public string Name => "IntegratedArmor";

    /// <inheritdoc/>
    public string Description => "Integrated Armor Workflow for Helldivers 2";

    /// <inheritdoc/>
    public async Task<bool> CanHandleAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
        {
            return false;
        }

        var manifestPath = Path.Combine(modPath, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            return true;
        }

        // Structure-based detection: check for armor-related directories
        var armorDirs = new[] { "armors", "appearances", "variants" };
        return armorDirs.Any(dir => Directory.Exists(Path.Combine(modPath, dir)));
    }

    /// <inheritdoc/>
    public async Task<ModManifest?> ParseManifestAsync(string modPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
        {
            return null;
        }

        var manifestPath = Path.Combine(modPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        // IntegratedArmor uses its own ArmorManifest format, not the standard ModManifest
        // Return a basic ModManifest with type information
        var armorManifest = await ParseArmorManifestAsync(manifestPath, cancellationToken);

        return new ModManifest
        {
            Name = armorManifest.Name,
            Description = armorManifest.Description,
            Author = armorManifest.Author,
            Version = armorManifest.Version
        };
    }

    /// <inheritdoc/>
    public async Task<WorkflowValidationResult> ValidateAsync(string modPath, CancellationToken cancellationToken = default)
    {
        var result = new WorkflowValidationResult
        {
            IsValid = true,
            Errors = new List<string>(),
            Warnings = new List<string>()
        };

        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
        {
            result.IsValid = false;
            result.Errors.Add("Mod directory not found.");
            return result;
        }

        var manifestPath = Path.Combine(modPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            result.Warnings.Add($"{ManifestFileName} not found. Using structure-based detection.");
            return result;
        }

        try
        {
            var manifest = await ParseArmorManifestAsync(manifestPath, cancellationToken);

            // Validate: at least one armor option
            if (!manifest.GameplayArmors.Any() && !manifest.AppearanceArmors.Any())
            {
                result.IsValid = false;
                result.Errors.Add("At least one gameplay or appearance armor is required.");
            }

            // Validate: all referenced files exist
            var allFiles = manifest.GameplayArmors.SelectMany(a => a.Files)
                .Concat(manifest.AppearanceArmors.SelectMany(a => a.Files))
                .Concat(manifest.BodyVariants.SelectMany(v => v.Files))
                .Concat(manifest.HeadVariants.SelectMany(v => v.Files))
                .ToList();

            foreach (var file in allFiles)
            {
                var fullPath = Path.Combine(modPath, file);
                if (!File.Exists(fullPath))
                {
                    result.Errors.Add($"Referenced file not found: {file}");
                    result.IsValid = false;
                }

                // Check V1 file pairing
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".patch_0")
                {
                    var streamFile = Path.ChangeExtension(file, ".stream");
                    var streamPath = Path.Combine(modPath, streamFile);
                    if (!File.Exists(streamPath))
                    {
                        result.Errors.Add($"Missing paired .stream file for: {file}");
                        result.IsValid = false;
                    }
                }
            }

            // Validate: variant IDs are unique
            var bodyIds = manifest.BodyVariants.Select(v => v.Id).ToList();
            if (bodyIds.Count != bodyIds.Distinct().Count())
            {
                result.Errors.Add("Body variant IDs must be unique.");
                result.IsValid = false;
            }

            var headIds = manifest.HeadVariants.Select(v => v.Id).ToList();
            if (headIds.Count != headIds.Distinct().Count())
            {
                result.Errors.Add("Head variant IDs must be unique.");
                result.IsValid = false;
            }
        }
        catch (JsonException ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Invalid JSON format: {ex.Message}");
        }
        catch (Exception ex)
        {
            result.IsValid = false;
            result.Errors.Add($"Validation error: {ex.Message}");
        }

        return result;
    }

    /// <inheritdoc/>
    public async Task<List<WorkflowDeploymentFile>> GetDeploymentFilesAsync(
        string modPath,
        ModManifest? manifest = null,
        CancellationToken cancellationToken = default)
    {
        var deploymentFiles = new List<WorkflowDeploymentFile>();

        if (string.IsNullOrWhiteSpace(modPath) || !Directory.Exists(modPath))
        {
            return deploymentFiles;
        }

        var manifestPath = Path.Combine(modPath, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            // Fallback: deploy all files in armor-related directories
            var armorDirs = new[] { "armors", "appearances", "variants" };
            foreach (var dir in armorDirs)
            {
                var dirPath = Path.Combine(modPath, dir);
                if (Directory.Exists(dirPath))
                {
                    var files = Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories);
                    foreach (var file in files)
                    {
                        var relativePath = Path.GetRelativePath(modPath, file);
                        deploymentFiles.Add(new WorkflowDeploymentFile
                        {
                            SourcePath = file,
                            TargetRelativePath = relativePath,
                            Priority = 10
                        });
                    }
                }
            }
            return deploymentFiles;
        }

        var armorManifest = await ParseArmorManifestAsync(manifestPath, cancellationToken);

        // Add enabled gameplay armors
        foreach (var armor in armorManifest.GameplayArmors.Where(a => a.Enabled))
        {
            foreach (var file in armor.Files)
            {
                var sourcePath = Path.Combine(modPath, file);
                if (File.Exists(sourcePath))
                {
                    deploymentFiles.Add(new WorkflowDeploymentFile
                    {
                        SourcePath = sourcePath,
                        TargetRelativePath = file,
                        Priority = 10,
                        Metadata = new Dictionary<string, object>
                        {
                            ["ArmorType"] = "Gameplay",
                            ["ArmorId"] = armor.Id
                        }
                    });
                }
            }
        }

        // Add enabled appearance armors
        foreach (var armor in armorManifest.AppearanceArmors.Where(a => a.Enabled))
        {
            foreach (var file in armor.Files)
            {
                var sourcePath = Path.Combine(modPath, file);
                if (File.Exists(sourcePath))
                {
                    deploymentFiles.Add(new WorkflowDeploymentFile
                    {
                        SourcePath = sourcePath,
                        TargetRelativePath = file,
                        Priority = 10,
                        Metadata = new Dictionary<string, object>
                        {
                            ["ArmorType"] = "Appearance",
                            ["ArmorId"] = armor.Id
                        }
                    });
                }
            }
        }

        // Add enabled body variants
        foreach (var variant in armorManifest.BodyVariants.Where(v => v.Enabled))
        {
            foreach (var file in variant.Files)
            {
                var sourcePath = Path.Combine(modPath, file);
                if (File.Exists(sourcePath))
                {
                    deploymentFiles.Add(new WorkflowDeploymentFile
                    {
                        SourcePath = sourcePath,
                        TargetRelativePath = file,
                        Priority = 10,
                        Metadata = new Dictionary<string, object>
                        {
                            ["VariantType"] = "Body",
                            ["VariantId"] = variant.Id
                        }
                    });
                }
            }
        }

        // Add enabled head variants
        foreach (var variant in armorManifest.HeadVariants.Where(v => v.Enabled))
        {
            foreach (var file in variant.Files)
            {
                var sourcePath = Path.Combine(modPath, file);
                if (File.Exists(sourcePath))
                {
                    deploymentFiles.Add(new WorkflowDeploymentFile
                    {
                        SourcePath = sourcePath,
                        TargetRelativePath = file,
                        Priority = 10,
                        Metadata = new Dictionary<string, object>
                        {
                            ["VariantType"] = "Head",
                            ["VariantId"] = variant.Id
                        }
                    });
                }
            }
        }

        return deploymentFiles;
    }

    /// <inheritdoc/>
    public async Task<List<string>> GetCleanupFilesAsync(
        string modPath,
        ModManifest? manifest = null,
        CancellationToken cancellationToken = default)
    {
        // Return all deployment file target paths for cleanup
        var deploymentFiles = await GetDeploymentFilesAsync(modPath, manifest, cancellationToken);
        return deploymentFiles.Select(f => f.TargetRelativePath).ToList();
    }

    private async Task<ArmorManifest> ParseArmorManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var jsonContent = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var manifest = JsonSerializer.Deserialize<ArmorManifest>(jsonContent, options);
        if (manifest == null)
        {
            throw new InvalidOperationException("Failed to parse armor manifest.");
        }

        return manifest;
    }
}
