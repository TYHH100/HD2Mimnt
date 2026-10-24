using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Core.Entities;

/// <summary>
/// 传统 Mod 的部署适配器
/// 将传统基于文件夹的 Mod 适配为统一的部署源
/// </summary>
public class TraditionalModDeployableSource : IDeployableModSource
{
    private readonly Mod _mod;

    public TraditionalModDeployableSource(Mod mod)
    {
        _mod = mod ?? throw new ArgumentNullException(nameof(mod));
    }

    public Guid Id => _mod.Id;
    public string Name => _mod.Name;
    public string Version => _mod.Version;
    public string Author => _mod.Author;
    public string ModType => "Traditional";

    public Task<IEnumerable<DeployableFileMapping>> GetDeployableFilesAsync(CancellationToken cancellationToken = default)
    {
        var files = new List<DeployableFileMapping>();

        if (!Directory.Exists(_mod.Path))
        {
            return Task.FromResult<IEnumerable<DeployableFileMapping>>(files);
        }

        // 递归获取 Mod 目录下的所有文件
        var allFiles = Directory.GetFiles(_mod.Path, "*", SearchOption.AllDirectories);

        foreach (var file in allFiles)
        {
            var relativePath = Path.GetRelativePath(_mod.Path, file);
            var fileInfo = new FileInfo(file);

            files.Add(new DeployableFileMapping
            {
                SourcePath = file,
                TargetPath = relativePath,
                Size = fileInfo.Length,
                Hash = string.Empty, // 按需计算
                IsShared = false,
                RequiresExtraction = false
            });
        }

        return Task.FromResult<IEnumerable<DeployableFileMapping>>(files);
    }

    public Task<IEnumerable<Guid>> GetDependenciesAsync(CancellationToken cancellationToken = default)
    {
        // 传统 Mod 暂不支持依赖
        return Task.FromResult<IEnumerable<Guid>>(Array.Empty<Guid>());
    }
}
