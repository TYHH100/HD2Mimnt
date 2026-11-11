using System.IO.Compression;
using HD2Mimnt.Core.Darktide;
using HD2Mimnt.Core.IO;

namespace HD2Mimnt.Infrastructure.Darktide;

/// <summary>
/// 文件夹形式的模组包：分类规则与压缩包一致（StandardMod / ModBundle / CustomInstallRequired），
/// 复制候选目录到暂存时拒绝符号链接/目录联接，并限制条目数与总体积。
/// </summary>
public sealed class DarktideFolderInspector
{
    private const int MaxEntries = 100_000;
    private const long MaxTotalBytes = 16L * 1024 * 1024 * 1024;

    public DarktideArchiveInspection Inspect(string folderPath)
    {
        try
        {
            if (!Directory.Exists(folderPath))
                return new DarktideArchiveInspection(folderPath, Array.Empty<DarktideArchiveCandidate>(), "文件夹不存在。");
            var root = Path.GetFullPath(folderPath);
            if (DarktidePaths.IsReparsePoint(root))
                return new DarktideArchiveInspection(folderPath, Array.Empty<DarktideArchiveCandidate>(),
                    "文件夹是符号链接或目录联接，已停止检查。");
            var (candidates, error, kind, extra) = DarktidePackageClassifier.Classify(EnumerateFiles(root));
            return new DarktideArchiveInspection(folderPath, candidates, error, kind, extra);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new DarktideArchiveInspection(folderPath, Array.Empty<DarktideArchiveCandidate>(),
                $"无法读取文件夹：{ex.Message}");
        }
    }

    public async Task CopyCandidateAsync(string folderPath, DarktideArchiveCandidate candidate,
        string destinationRoot, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        var folderRoot = Path.GetFullPath(folderPath);
        var sourceRoot = candidate.RelativeRoot.Length == 0
            ? folderRoot
            : Path.GetFullPath(Path.Combine(folderRoot, candidate.RelativeRoot.Replace('/', Path.DirectorySeparatorChar)));
        if (candidate.RelativeRoot.Length > 0)
            PathGuard.ResolveInsideRoot(folderRoot, sourceRoot, "候选模组目录越过了所选文件夹。");
        if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("候选模组目录不存在。");
        if (DarktidePaths.IsReparsePoint(sourceRoot))
            throw new InvalidDataException("候选模组目录是符号链接或目录联接，已停止复制。");
        var files = EnumerateFiles(sourceRoot);
        if (files.Count == 0) throw new InvalidDataException("候选模组目录为空。");
        var total = Math.Max(1, files.Sum(file => file.Length));
        Directory.CreateDirectory(destinationRoot);
        long written = 0;

        foreach (var (path, _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = path.Replace('/', Path.DirectorySeparatorChar);
            var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            PathGuard.ResolveInsideRoot(destinationRoot, target, "模组文件越过了暂存目录。");
            var directory = Path.GetDirectoryName(target);
            if (directory != null) Directory.CreateDirectory(directory);

            await using var source = new FileStream(Path.Combine(sourceRoot, relative), FileMode.Open,
                FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
            await using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
                progress?.Report(Math.Min(100d, written * 100d / total));
            }
        }

        var modFile = Path.Combine(destinationRoot, candidate.InternalName + ".mod");
        if (!File.Exists(modFile) || new FileInfo(modFile).Length == 0)
            throw new InvalidDataException($"暂存目录缺少有效的 {candidate.InternalName}.mod。");
        progress?.Report(100d);
    }

    /// <summary>
    /// 把模组目录打包成 “&lt;entryRootName&gt;/...” 结构的 ZIP，供模组库归档与再次安装。
    /// 条目时间保留源文件修改时间（不早于 1980），内容不变的目录生成相同的归档字节。
    /// </summary>
    public async Task CreateArchiveAsync(string sourceFolder, string entryRootName, string destinationZip,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("要打包的模组目录不存在。");
        if (DarktidePaths.IsReparsePoint(root))
            throw new InvalidDataException("模组目录是符号链接或目录联接，已停止打包。");
        var files = EnumerateFiles(root);
        await using var stream = new FileStream(destinationZip, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (path, _) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(entryRootName + "/" + path, CompressionLevel.Optimal);
            var sourceFile = new FileInfo(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
            var timestamp = sourceFile.LastWriteTimeUtc;
            entry.LastWriteTime = timestamp.Year < 1980
                ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero)
                : new DateTimeOffset(timestamp, TimeSpan.Zero);
            await using var entryStream = entry.Open();
            await using var source = new FileStream(sourceFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            await source.CopyToAsync(entryStream, cancellationToken);
        }
    }

    /// <summary>手动递归枚举：不跟随符号链接/目录联接，发现即报错，避免只复制一半。</summary>
    private static List<(string Path, long Length)> EnumerateFiles(string root)
    {
        var files = new List<(string, long)>();
        var pending = new Stack<string>();
        pending.Push(root);
        long total = 0;
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"文件夹包含符号链接或目录联接：{entry.FullName}");
                if (entry is DirectoryInfo subdirectory)
                {
                    pending.Push(subdirectory.FullName);
                    continue;
                }
                if (entry is not FileInfo file) continue;
                if (files.Count >= MaxEntries) throw new InvalidDataException("文件夹条目过多，已停止检查。");
                total += file.Length;
                if (total > MaxTotalBytes) throw new InvalidDataException("文件夹内容体积超过限制。");
                files.Add((Path.GetRelativePath(root, file.FullName).Replace('\\', '/'), file.Length));
            }
        }
        return files;
    }
}