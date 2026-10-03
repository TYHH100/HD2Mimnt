using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using System.Collections.Concurrent;

namespace HD2Mimnt.Application.Services;

/// <summary>
/// Service for managing mod downloads from platforms.
/// </summary>
public class DownloadManagerService
{
    private readonly IPlatformRegistry _platformRegistry;
    private readonly ConcurrentDictionary<Guid, DownloadTask> _downloadTasks = new();

    public DownloadManagerService(IPlatformRegistry platformRegistry)
    {
        _platformRegistry = platformRegistry ?? throw new ArgumentNullException(nameof(platformRegistry));
    }

    /// <summary>
    /// Starts a download task for a mod.
    /// </summary>
    public Guid EnqueueDownload(string platformId, string modId, string targetPath)
    {
        var platform = _platformRegistry.GetPlatform(platformId);
        if (platform == null)
        {
            throw new InvalidOperationException($"Platform not found: {platformId}");
        }

        var taskId = Guid.NewGuid();
        var downloadTask = new DownloadTask
        {
            Id = taskId,
            PlatformId = platformId,
            ModId = modId,
            TargetPath = targetPath,
            Status = DownloadStatus.Queued,
            EnqueuedAt = DateTime.UtcNow
        };

        _downloadTasks[taskId] = downloadTask;

        // Start download in background
        _ = Task.Run(async () => await ExecuteDownloadAsync(taskId, platform));

        return taskId;
    }

    /// <summary>
    /// Gets the status of a download task.
    /// </summary>
    public DownloadTask? GetDownloadTask(Guid taskId)
    {
        return _downloadTasks.TryGetValue(taskId, out var task) ? task : null;
    }

    /// <summary>
    /// Gets all download tasks.
    /// </summary>
    public List<DownloadTask> GetAllDownloads()
    {
        return _downloadTasks.Values.OrderByDescending(t => t.EnqueuedAt).ToList();
    }

    /// <summary>
    /// Gets active download tasks.
    /// </summary>
    public List<DownloadTask> GetActiveDownloads()
    {
        return _downloadTasks.Values
            .Where(t => t.Status == DownloadStatus.Downloading || t.Status == DownloadStatus.Queued)
            .OrderByDescending(t => t.EnqueuedAt)
            .ToList();
    }

    /// <summary>
    /// Cancels a download task.
    /// </summary>
    public bool CancelDownload(Guid taskId)
    {
        if (_downloadTasks.TryGetValue(taskId, out var task))
        {
            lock (task)
            {
                if (task.Status == DownloadStatus.Queued || task.Status == DownloadStatus.Downloading)
                {
                    task.CancellationTokenSource?.Cancel();
                    task.Status = DownloadStatus.Cancelled;
                    task.CompletedAt = DateTime.UtcNow;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Clears completed downloads from history.
    /// </summary>
    public void ClearCompleted()
    {
        var completedKeys = _downloadTasks
            .Where(kvp => kvp.Value.Status == DownloadStatus.Completed ||
                         kvp.Value.Status == DownloadStatus.Failed ||
                         kvp.Value.Status == DownloadStatus.Cancelled)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in completedKeys)
        {
            _downloadTasks.TryRemove(key, out _);
        }
    }

    private async Task ExecuteDownloadAsync(Guid taskId, IModPlatform platform)
    {
        if (!_downloadTasks.TryGetValue(taskId, out var task))
        {
            return;
        }

        try
        {
            lock (task)
            {
                // A queued task may be cancelled before Task.Run starts.
                // Do not resurrect it as an active download in that case.
                if (task.Status != DownloadStatus.Queued)
                {
                    return;
                }

                task.Status = DownloadStatus.Downloading;
                task.StartedAt = DateTime.UtcNow;
                task.CancellationTokenSource = new CancellationTokenSource();
            }

            // Get mod info first
            var modInfo = await platform.GetModInfoAsync(task.ModId, task.CancellationTokenSource.Token);
            task.ModName = modInfo.Name;

            // Create progress reporter
            var progress = new Progress<DownloadProgress>(p =>
            {
                task.Progress = p.Percentage;
                task.DownloadedBytes = p.DownloadedBytes;
                task.TotalBytes = p.TotalBytes;
                task.StatusMessage = p.StatusMessage;
            });

            // Download the mod
            using var stream = await platform.DownloadModAsync(task.ModId, progress, task.CancellationTokenSource.Token);

            // Save to file
            var directory = Path.GetDirectoryName(task.TargetPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var fileStream = File.Create(task.TargetPath);
            await stream.CopyToAsync(fileStream, task.CancellationTokenSource.Token);

            task.Status = DownloadStatus.Completed;
            task.Progress = 100;
            task.CompletedAt = DateTime.UtcNow;
            task.StatusMessage = "Download completed";
        }
        catch (OperationCanceledException)
        {
            task.Status = DownloadStatus.Cancelled;
            task.CompletedAt = DateTime.UtcNow;
            task.StatusMessage = "Download cancelled";
        }
        catch (Exception ex)
        {
            task.Status = DownloadStatus.Failed;
            task.CompletedAt = DateTime.UtcNow;
            task.ErrorMessage = ex.Message;
            task.StatusMessage = "Download failed";
        }
        finally
        {
            task.CancellationTokenSource?.Dispose();
            task.CancellationTokenSource = null;
        }
    }
}

/// <summary>
/// Represents a download task.
/// </summary>
public class DownloadTask
{
    public Guid Id { get; set; }
    public string PlatformId { get; set; } = string.Empty;
    public string ModId { get; set; } = string.Empty;
    public string ModName { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public DownloadStatus Status { get; set; }
    public double Progress { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public DateTime EnqueuedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    internal CancellationTokenSource? CancellationTokenSource { get; set; }
}

/// <summary>
/// Download task status.
/// </summary>
public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    Cancelled
}
