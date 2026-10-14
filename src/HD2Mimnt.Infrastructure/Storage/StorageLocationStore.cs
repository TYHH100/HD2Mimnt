using System.Text.Json;
using HD2Mimnt.Core;
using HD2Mimnt.Core.Entities;

namespace HD2Mimnt.Infrastructure.Storage;

/// <summary>启动定位文件始终留在固定目录，不能随 settings.json 一起搬走。</summary>
public sealed class StorageLocationStore
{
    private readonly string _file;
    public string BootstrapPath { get; }
    public StorageLocations Defaults { get; }
    public StorageState State { get; private set; }

    public StorageLocationStore(string? bootstrapPath = null)
    {
        var environmentPath = Environment.GetEnvironmentVariable("HD2MIMNT_DATA_PATH");
        BootstrapPath = Normalize(bootstrapPath ?? (string.IsNullOrWhiteSpace(environmentPath)
            ? RuntimeDataPaths.GetDefaultApplicationDataPath()
            : environmentPath));
        _file = Path.Combine(BootstrapPath, "storage-locations.json");
        Defaults = new(BootstrapPath, Path.Combine(BootstrapPath, "mods"), Path.Combine(BootstrapPath, "Cache"));
        // 损坏/不可读的定位文件必须明确报错，不能悄悄启动一个空库。
        State = File.Exists(_file)
            ? JsonSerializer.Deserialize<StorageState>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("存储位置配置无效。")
            : new(Defaults);
    }

    public async Task SaveAsync(StorageState state, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(BootstrapPath);
        var temporary = _file + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state,
                new JsonSerializerOptions { WriteIndented = true }), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _file, true);
            State = state;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public bool IsBootstrapFile(string path) => Same(path, _file) || Same(path, _file + ".tmp") ||
        Contains(Path.Combine(BootstrapPath, "interop"), path);
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Environment.ExpandEnvironmentVariables(path.Trim());
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择完整的绝对目录路径。");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (Same(full, Path.GetPathRoot(full)!)) throw new ArgumentException("请使用专门的子目录，不能直接使用磁盘根目录。");
        return full;
    }
    public static bool Same(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);
    public static bool Contains(string root, string path) => Same(root, path) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}

public sealed record StorageState(StorageLocations Current)
{
    public StorageLocations? Pending { get; init; }
    public string? MigrationId { get; init; }
    public bool ReadyToCommit { get; init; }
    public List<StorageMove> Moves { get; init; } = [];
    public List<string> CleanupFiles { get; init; } = [];
}

public sealed record StorageMove(string Source, string Target);
