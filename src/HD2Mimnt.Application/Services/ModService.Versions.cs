using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Infrastructure.Storage;

namespace HD2Mimnt.Application.Services;

/// <summary>所选版本相对当前版本的文件变化类型。</summary>
public enum LocalVersionChangeKind { Added, Modified, Removed }

/// <summary>单文件变化；大小分别是当前版本与所选版本中的字节数，不存在的一侧为 0。</summary>
public sealed record LocalVersionFileChange(string Path, LocalVersionChangeKind Kind, long CurrentSize, long VersionSize);

/// <summary>以当前版本为基准，切换到所选版本后的文件变化。</summary>
public sealed record LocalVersionDiff(IReadOnlyList<LocalVersionFileChange> Changes)
{
    public int AddedCount => Changes.Count(change => change.Kind == LocalVersionChangeKind.Added);
    public int ModifiedCount => Changes.Count(change => change.Kind == LocalVersionChangeKind.Modified);
    public int RemovedCount => Changes.Count(change => change.Kind == LocalVersionChangeKind.Removed);
}

/// <summary>
/// 单个文件在两个版本中的内容。文本按 UTF-8 严格解码；任一侧不是文本时只提供字节差异摘要，
/// 超过大小上限的文件不读取内容（<see cref="TooLarge"/>）。
/// </summary>
public sealed record LocalVersionFileContent(
    string Path, bool IsText, bool TooLarge, string? CurrentText, string? SelectedText,
    long CurrentSize, long SelectedSize, long FirstDifferenceOffset, long DifferingByteCount);

public partial class ModService
{
    public async Task<IReadOnlyList<LocalModVersion>> GetLocalVersionsAsync(Guid id, CancellationToken token = default)
    {
        await _mutationGate.WaitAsync(token);
        try { return await new ModVersionStore(_storageDirectory).ReadAsync(id, token); }
        finally { _mutationGate.Release(); }
    }

    /// <summary>只读对比，不改动任何版本；以当前版本为基准列出切换到所选版本后的新增、修改与删除。</summary>
    public async Task<LocalVersionDiff> CompareLocalVersionAsync(Guid id, Guid versionId, CancellationToken token = default)
    {
        var mod = await _modRepository.GetByIdAsync(id, token) ?? throw new KeyNotFoundException("模组已删除。");
        var versions = await new ModVersionStore(_storageDirectory).ReadAsync(id, token);
        var selected = versions.SingleOrDefault(v => v.Id == versionId) ?? throw new KeyNotFoundException("本地版本已不存在。");
        if (!Directory.Exists(mod.Path)) throw new DirectoryNotFoundException("当前版本文件已缺失。");
        if (!Directory.Exists(selected.Content.Path)) throw new DirectoryNotFoundException("所选版本文件已缺失。");
        return await Task.Run(() => DiffVersionDirectories(mod.Path, selected.Content.Path, token), token);
    }

    /// <summary>读取单个文件在两个版本中的内容，供对比窗口展示；只读，不加载其它文件。</summary>
    public async Task<LocalVersionFileContent> ReadLocalVersionFileAsync(Guid id, Guid versionId, string relativePath,
        CancellationToken token = default)
    {
        const long maxContentBytes = 4 * 1024 * 1024;
        var mod = await _modRepository.GetByIdAsync(id, token) ?? throw new KeyNotFoundException("模组已删除。");
        var versions = await new ModVersionStore(_storageDirectory).ReadAsync(id, token);
        var selected = versions.SingleOrDefault(v => v.Id == versionId) ?? throw new KeyNotFoundException("本地版本已不存在。");
        if (!Directory.Exists(mod.Path)) throw new DirectoryNotFoundException("当前版本文件已缺失。");
        if (!Directory.Exists(selected.Content.Path)) throw new DirectoryNotFoundException("所选版本文件已缺失。");

        var relative = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var currentPath = HD2Mimnt.Core.IO.PathGuard.ResolveInsideRoot(mod.Path,
            Path.Combine(mod.Path, relative), "对比文件不在当前版本目录中。");
        var selectedPath = HD2Mimnt.Core.IO.PathGuard.ResolveInsideRoot(selected.Content.Path,
            Path.Combine(selected.Content.Path, relative), "对比文件不在所选版本目录中。");
        var (currentBytes, currentTooLarge) = await ReadCappedAsync(currentPath, maxContentBytes, token);
        var (selectedBytes, selectedTooLarge) = await ReadCappedAsync(selectedPath, maxContentBytes, token);
        var (offset, differing) = CompareBytes(currentBytes, selectedBytes);
        var result = new LocalVersionFileContent(relativePath, false, currentTooLarge || selectedTooLarge,
            null, null, currentBytes?.LongLength ?? 0, selectedBytes?.LongLength ?? 0, offset, differing);
        if (result.TooLarge) return result;

        var currentIsText = TryDecodeText(currentBytes, out var currentText);
        var selectedIsText = TryDecodeText(selectedBytes, out var selectedText);
        return result with
        {
            IsText = currentIsText && selectedIsText,
            CurrentText = currentIsText ? currentText : null,
            SelectedText = selectedIsText ? selectedText : null
        };
    }

    private static async Task<(byte[]? Bytes, bool TooLarge)> ReadCappedAsync(string path, long limit, CancellationToken token)
    {
        if (!File.Exists(path)) return (null, false);
        if (new FileInfo(path).Length > limit) return (null, true);
        return (await File.ReadAllBytesAsync(path, token), false);
    }

    /// <summary>任一侧含 NUL 字节或不是合法 UTF-8 时按二进制处理。</summary>
    private static bool TryDecodeText(byte[]? bytes, out string? text)
    {
        text = null;
        if (bytes == null) return true;
        if (bytes.AsSpan().IndexOf((byte)0) >= 0) return false;
        try
        {
            text = new System.Text.UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            return true;
        }
        catch (System.Text.DecoderFallbackException) { return false; }
    }

    private static (long FirstDifferenceOffset, long DifferingByteCount) CompareBytes(byte[]? left, byte[]? right)
    {
        var first = left ?? Array.Empty<byte>();
        var second = right ?? Array.Empty<byte>();
        var common = Math.Min(first.Length, second.Length);
        long firstDifference = -1, differing = Math.Abs(first.Length - second.Length);
        for (var index = 0; index < common; index++)
        {
            if (first[index] == second[index]) continue;
            if (firstDifference < 0) firstDifference = index;
            differing++;
        }
        if (firstDifference < 0 && differing > 0) firstDifference = common;
        return (firstDifference, differing);
    }

    private static LocalVersionDiff DiffVersionDirectories(string currentRoot, string versionRoot, CancellationToken token)
    {
        var current = IndexVersionFiles(currentRoot, token);
        var selected = IndexVersionFiles(versionRoot, token);
        var changes = new List<LocalVersionFileChange>();
        foreach (var (path, file) in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!current.TryGetValue(path, out var existing))
                changes.Add(new(path, LocalVersionChangeKind.Added, 0, file.Size));
            else if (existing.Size != file.Size || !FilesMatch(existing.FullPath, file.FullPath, token))
                changes.Add(new(path, LocalVersionChangeKind.Modified, existing.Size, file.Size));
        }
        foreach (var (path, file) in current)
        {
            token.ThrowIfCancellationRequested();
            if (!selected.ContainsKey(path)) changes.Add(new(path, LocalVersionChangeKind.Removed, file.Size, 0));
        }
        return new(changes
            .OrderBy(change => change.Kind)
            .ThenBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static Dictionary<string, (string FullPath, long Size)> IndexVersionFiles(string root, CancellationToken token)
    {
        var files = new Dictionary<string, (string FullPath, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            token.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            files[relative] = (file, new FileInfo(file).Length);
        }
        return files;
    }

    /// <summary>逐块比对；大小相同但内容不同的文件也算修改。</summary>
    private static bool FilesMatch(string left, string right, CancellationToken token)
    {
        const int bufferSize = 81920;
        using var first = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        using var second = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, FileOptions.SequentialScan);
        var firstBuffer = new byte[bufferSize];
        var secondBuffer = new byte[bufferSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var readFirst = first.Read(firstBuffer, 0, bufferSize);
            var readSecond = second.Read(secondBuffer, 0, bufferSize);
            if (readFirst != readSecond) return false;
            if (readFirst == 0) return true;
            if (!firstBuffer.AsSpan(0, readFirst).SequenceEqual(secondBuffer.AsSpan(0, readSecond))) return false;
        }
    }

    public async Task<ModReplacementResult> SwitchLocalVersionAsync(Guid id, Guid versionId,
        CancellationToken token = default, IProgress<DeploymentProgress>? deploymentProgress = null)
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
            var (hash, fingerprint) = await ModHashService.CalculateWithFingerprintAsync(selected.Content.Path, token);
            if (!string.IsNullOrWhiteSpace(selected.Content.Hash) && hash != selected.Content.Hash)
                throw new InvalidDataException("本地版本文件已变更，无法切换。");
            var next = previous.Where(v => v.Id != versionId).Append(new LocalModVersion(Guid.NewGuid(), DateTime.UtcNow,
                ModVersionStore.Clone(mod))).ToList();
            var wasDeployed = _deploymentService != null && (await _deploymentService.GetModDeploymentHistoryAsync(id, token)).Any();
            token.ThrowIfCancellationRequested();
            var content = selected.Content;
            mod.Path = content.Path;
            mod.Version = content.Version;
            mod.Manifest = content.Manifest;
            mod.LogicalMod = content.LogicalMod;
            mod.WorkflowType = content.WorkflowType;
            mod.Hash = hash;
            mod.ContentFingerprint = string.IsNullOrWhiteSpace(content.ContentFingerprint) ? fingerprint : content.ContentFingerprint;
            mod.Size = content.Size;
            mod.Category = content.Category;
            mod.ResourceTypeIds = content.ResourceTypeIds;
            mod.ContainsScripts = content.ContainsScripts;
            mod.BslRole = content.BslRole;
            mod.BslDetectionVersion = content.BslDetectionVersion;
            mod.ThumbnailPath = content.ThumbnailPath;
            mod.Status = ModStatus.Ready;
            // 切换不改变内容本身的日期；沿用该版本安装时间，再次切走时版本列表仍显示原始日期。
            mod.ModifiedAt = content.ModifiedAt == default ? DateTime.UtcNow : content.ModifiedAt;
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
            var (deployment, deploymentWarning) = await ReconcileDeploymentAfterUpdateAsync(
                id, wasDeployed, deploymentProgress, token);
            RaiseModsChanged();
            return new(mod, wasDeployed, deployment, deploymentWarning);
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
