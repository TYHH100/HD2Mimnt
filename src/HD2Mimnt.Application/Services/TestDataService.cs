using HD2Mimnt.Core.Entities;
using System.Text.Json;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// 开发工具使用的测试数据服务。
/// 文件和数据库操作留在 Application 层，避免 UI 直接操作模组目录。
/// </summary>
public sealed class TestDataService
{
    private readonly ModService _modService;

    public TestDataService(ModService modService) => _modService = modService;

    public Task<IEnumerable<Mod>> GetAllModsAsync(CancellationToken cancellationToken = default)
        => _modService.GetAllModsAsync(cancellationToken);

    public async Task<int> ImportGeneratedModsAsync(
        IEnumerable<Mod> mods,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var modList = mods.ToList();
        var completed = 0;

        foreach (var mod in modList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(mod.Path);
            try
            {
                var manifest = new
                {
                    name = mod.Name,
                    version = mod.Version,
                    author = mod.Author,
                    description = mod.Description,
                    category = mod.Category.ToString(),
                    tags = mod.Tags
                };
                await File.WriteAllTextAsync(
                    Path.Combine(mod.Path, "manifest.json"),
                    JsonSerializer.Serialize(manifest),
                    cancellationToken);

                await _modService.ImportModAsync(mod.Path, cancellationToken);
                completed++;
                progress?.Report(completed);
            }
            finally
            {
                if (Directory.Exists(mod.Path))
                {
                    Directory.Delete(mod.Path, true);
                }
            }
        }

        return completed;
    }

    public async Task<int> ClearGeneratedModsAsync(
        string namePrefix = "HD2Mimnt Test ",
        CancellationToken cancellationToken = default)
    {
        var mods = await _modService.GetAllModsAsync(cancellationToken);
        var deleted = 0;
        foreach (var mod in mods.Where(item => item.Name.StartsWith(namePrefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _modService.DeleteModAsync(mod.Id, cancellationToken))
            {
                deleted++;
            }
        }

        return deleted;
    }
}
