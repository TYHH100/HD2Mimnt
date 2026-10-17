using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Application.Services;

public partial class ModService
{
    public async Task<IReadOnlyList<LocalModVersion>> GetLocalVersionsAsync(Guid id, CancellationToken token = default)
    {
        await _mutationGate.WaitAsync(token);
        try { return await new ModVersionStore(_storageDirectory).ReadAsync(id, token); }
        finally { _mutationGate.Release(); }
    }

    public async Task<ModReplacementResult> SwitchLocalVersionAsync(Guid id, Guid versionId, CancellationToken token = default)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, token) ?? throw new KeyNotFoundException("模组已删除。");
            await LoadSourceAsync(mod, token);
            var store = new ModVersionStore(_storageDirectory);
            var previous = await store.ReadAsync(id, token);
            var selected = previous.SingleOrDefault(v => v.Id == versionId) ?? throw new KeyNotFoundException("本地版本已不存在。");
            if (!Directory.Exists(selected.Content.Path)) throw new DirectoryNotFoundException("本地版本文件已缺失。");
            var hash = await ModHashService.CalculateAsync(selected.Content.Path, token);
            if (!string.IsNullOrWhiteSpace(selected.Content.Hash) && hash != selected.Content.Hash)
                throw new InvalidDataException("本地版本文件已变更，无法切换。");
            var next = previous.Where(v => v.Id != versionId).Append(new LocalModVersion(Guid.NewGuid(), DateTime.UtcNow,
                ModVersionStore.Clone(mod))).ToList();
            var wasDeployed = _deploymentService != null && (await _deploymentService.GetModDeploymentHistoryAsync(id, token)).Any();
            if (_deploymentService != null) await _deploymentService.DeleteModDeploymentRecordsAsync(id, token);
            token.ThrowIfCancellationRequested();
            var content = selected.Content;
            mod.Path = content.Path;
            mod.Version = content.Version;
            mod.Manifest = content.Manifest;
            mod.LogicalMod = content.LogicalMod;
            mod.WorkflowType = content.WorkflowType;
            mod.Hash = hash;
            mod.Size = content.Size;
            mod.Category = content.Category;
            mod.ResourceTypeIds = content.ResourceTypeIds;
            mod.ContainsScripts = content.ContainsScripts;
            mod.BslRole = content.BslRole;
            mod.BslDetectionVersion = content.BslDetectionVersion;
            mod.ThumbnailPath = content.ThumbnailPath;
            mod.Status = ModStatus.Ready;
            mod.ModifiedAt = DateTime.UtcNow;
            if (mod.PlatformLink is { } source)
            {
                var sameSource = content.PlatformLink?.PlatformId == source.PlatformId && content.PlatformLink.ModId == source.ModId;
                mod.PlatformLink = source with
                {
                    InstalledVersion = content.Version,
                    InstalledFileVersionId = sameSource ? content.PlatformLink!.InstalledFileVersionId : null,
                    InstalledWebsiteFileId = sameSource ? content.PlatformLink!.InstalledWebsiteFileId : null
                };
            }
            await store.WriteAsync(id, next, CancellationToken.None);
            try { await SaveLinkedModAsync(mod, CancellationToken.None); }
            catch { await store.WriteAsync(id, previous, CancellationToken.None); throw; }
            RaiseModsChanged();
            return new(mod, wasDeployed, null);
        }
        finally { _mutationGate.Release(); }
    }

    public async Task DeleteLocalVersionAsync(Guid id, Guid versionId, CancellationToken token = default)
    {
        await _mutationGate.WaitAsync(token);
        try
        {
            var mod = await _modRepository.GetByIdAsync(id, token) ?? throw new KeyNotFoundException("模组已删除。");
            var store = new ModVersionStore(_storageDirectory);
            var versions = await store.ReadAsync(id, token);
            var version = versions.SingleOrDefault(v => v.Id == versionId) ?? throw new KeyNotFoundException("本地版本已不存在。");
            if (version.Content.Path == mod.Path) throw new InvalidOperationException("不能删除当前版本。");
            if (Directory.Exists(version.Content.Path)) Directory.Delete(version.Content.Path, true);
            versions.Remove(version);
            if (version.Content.ThumbnailPath != mod.ThumbnailPath && !versions.Any(v => v.Content.ThumbnailPath == version.Content.ThumbnailPath))
                await _thumbnailService.DeleteThumbnailFileAsync(version.Content.ThumbnailPath, CancellationToken.None);
            await store.WriteAsync(id, versions, CancellationToken.None);
        }
        finally { _mutationGate.Release(); }
    }

    private async Task DeleteSavedVersionsCoreAsync(Guid id, CancellationToken token)
    {
        var store = new ModVersionStore(_storageDirectory);
        foreach (var version in await store.ReadAsync(id, token))
        {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(version.Content.Path)) Directory.Delete(version.Content.Path, true);
            await _thumbnailService.DeleteThumbnailFileAsync(version.Content.ThumbnailPath, CancellationToken.None);
        }
        await store.WriteAsync(id, Array.Empty<LocalModVersion>(), CancellationToken.None);
    }
}
