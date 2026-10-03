using HD2Mimnt.Core.Interfaces;
using System.IO.Compression;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// LogicalMod 的部署适配器
/// 将 LogicalMod 的组件系统适配为统一的部署源
/// </summary>
public class LogicalModDeployableSource : IDeployableModSource
{
    private readonly LogicalMod _logicalMod;

    public LogicalModDeployableSource(LogicalMod logicalMod)
    {
        _logicalMod = logicalMod ?? throw new ArgumentNullException(nameof(logicalMod));
    }

    public Guid Id => _logicalMod.Id;
    public string Name => _logicalMod.Name;
    public string Version => _logicalMod.Version ?? string.Empty;
    public string Author => _logicalMod.Author ?? string.Empty;
    public string ModType => "LogicalMod";

    public Task<IEnumerable<DeployableFileMapping>> GetDeployableFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<DeployableFileMapping>();

        // 遍历所有启用的组件
        foreach (var component in _logicalMod.Components.Where(c => c.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 获取组件的源包
            var sourcePackage = _logicalMod.Packages.FirstOrDefault(p => p.Id == component.SourcePackageId);
            if (sourcePackage == null)
            {
                continue;
            }

            // 遍历组件的所有可部署文件
            foreach (var deployableFile in component.DeployableFiles)
            {
                var mapping = new DeployableFileMapping
                {
                    SourcePath = deployableFile.SourcePath,
                    TargetPath = deployableFile.TargetPath,
                    Size = deployableFile.Size,
                    Hash = deployableFile.Hash,
                    IsShared = deployableFile.IsShared,
                    ComponentId = component.Id,
                    PackageId = sourcePackage.Id,
                    RequiresExtraction = IsArchivePath(sourcePackage.PhysicalPath),
                    ArchiveInternalPath = deployableFile.SourcePath
                };

                files.Add(mapping);
            }
        }

        return Task.FromResult<IEnumerable<DeployableFileMapping>>(files);
    }

    public Task<IEnumerable<Guid>> GetDependenciesAsync(CancellationToken cancellationToken = default)
    {
        var dependencies = new HashSet<Guid>();

        // 收集所有组件的依赖
        foreach (var component in _logicalMod.Components.Where(c => c.Enabled))
        {
            foreach (var depId in component.Dependencies)
            {
                dependencies.Add(depId);
            }
        }

        return Task.FromResult<IEnumerable<Guid>>(dependencies);
    }

    private static bool IsArchivePath(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".zip" or ".7z" or ".rar" or ".tar" or ".gz";
    }
}
