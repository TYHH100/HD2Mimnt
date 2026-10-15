using System.Globalization;
using System.Threading.Channels;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interop;
using HD2Mimnt.Infrastructure.Interop;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>事件合并后在后台发布离线快照；不需要网络或执行第三方脚本。</summary>
public sealed class OfflineInteropService : IAsyncDisposable
{
    private readonly IOfflineInteropSource _source;
    private readonly OfflineInteropPublisher _publisher;
    private readonly StorageSettingsService _storage;
    private readonly SettingsService _settings;
    private readonly ModService _mods;
    private readonly ProfileService _profiles;
    private readonly DeploymentService _deployments;
    private readonly BackupService _backups;
    private readonly ILogger<OfflineInteropService>? _logger;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly Dictionary<Guid, CachedFiles> _fileCache = new();
    private Task? _worker;
    private bool _started;
    public string IndexPath => _publisher.IndexPath;
    public string RuntimePath => Path.Combine(_publisher.RootPath, "v1", "runtime.json");
    public string? LastError { get; private set; }

    public OfflineInteropService(IOfflineInteropSource source, OfflineInteropPublisher publisher,
        StorageSettingsService storage, SettingsService settings, ModService mods, ProfileService profiles,
        DeploymentService deployments, BackupService backups, ILogger<OfflineInteropService>? logger = null)
    {
        _source = source; _publisher = publisher; _storage = storage; _settings = settings;
        _mods = mods; _profiles = profiles; _deployments = deployments; _backups = backups; _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        _started = true;
        _mods.ModsChanged += Changed;
        _profiles.ProfilesChanged += ProfilesChanged;
        _deployments.DeploymentsChanged += Changed;
        _backups.Restored += Changed;
        _settings.SettingChanged += SettingChanged;
        if (!await Task.Run(() => TryRefreshAsync(cancellationToken), cancellationToken).ConfigureAwait(false))
            _updates.Writer.TryWrite(true);
        _worker = Task.Run(RunAsync);
    }

    private void Changed(object? sender, EventArgs args) => _updates.Writer.TryWrite(true);
    private void ProfilesChanged(object? sender, ProfilesChangedEventArgs args) => Changed(sender, args);
    private void SettingChanged(object? sender, SettingChangedEventArgs args) => Changed(sender, args);

    private async Task RunAsync()
    {
        try
        {
            while (await _updates.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                await Task.Delay(150, _stop.Token).ConfigureAwait(false);
                while (_updates.Reader.TryRead(out _)) { }
                // 写入失败保留旧快照，短暂占用解除后自动重试。
                while (!await TryRefreshAsync(_stop.Token).ConfigureAwait(false))
                    await Task.Delay(TimeSpan.FromSeconds(2), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    private async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        try { await RefreshAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            LastError = error.Message;
            _logger?.LogWarning(error, "发布离线模组接口失败，将保留旧快照并重试: {Path}", RuntimePath);
            return false;
        }
    }

    /// <summary>显式刷新完成后即可读取文件；关闭时也使用同一发布闸门。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var data = await _source.ReadAsync(cancellationToken).ConfigureAwait(false);
            var byId = data.Mods.ToDictionary(mod => mod.Id);
            var library = new List<OfflineModV1>();
            foreach (var mod in data.Mods)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var inventory = GetFiles(mod, cancellationToken);
                library.Add(new(mod.Id, mod.Manifest?.Guid is { } guid && guid != Guid.Empty ? guid : null,
                    mod.Name, mod.Version, mod.Author, mod.Description, mod.Path,
                    string.IsNullOrEmpty(mod.ThumbnailPath) ? null : mod.ThumbnailPath, mod.Size, mod.Hash,
                    BslProfilePolicy.Role(mod).ToString(), mod.Category.ToString(), mod.WorkflowType,
                    mod.Status.ToString(), mod.Tags, mod.ContainsScripts,
                    mod.ResourceTypeIds?.Select(id => id.ToString("x16", CultureInfo.InvariantCulture)).ToArray(),
                    Utc(mod.ModifiedAt), mod.Manifest?.LegacyOptions ?? [],
                    mod.Manifest?.Options?.Select(option => new OfflineOptionV1(option.Name, option.Description,
                        option.DefaultEnabled ?? true, option.Include, option.Image,
                        option.SubOptions?.Select(sub => new OfflineSubOptionV1(sub.Name, sub.Description, sub.Include, sub.Image)).ToArray() ?? [])).ToArray() ?? [],
                    State(ProfileModOptionResolver.ResolveEffective(mod.Manifest, null)),
                    inventory.Complete, inventory.Error, inventory.Files));
            }
            foreach (var removed in _fileCache.Keys.Where(id => !byId.ContainsKey(id)).ToArray()) _fileCache.Remove(removed);
            Enum.TryParse<DeploymentOrder>(_settings.GetSetting(SettingKeys.DefaultDeploymentOrder,
                DeploymentOrder.TopToBottom.ToString()), out var direction);
            var profiles = data.Profiles.Select(profile => new OfflineProfileV1(profile.Id, profile.Name,
                profile.IsActive, profile.Mods.OrderBy(item => item.Order).Select(item => new OfflineProfileModV1(
                    item.ModId, item.Enabled, item.Order, State(ProfileModOptionResolver.ResolveEffective(
                        byId.GetValueOrDefault(item.ModId)?.Manifest, item.OptionsJson)))).ToArray(),
                BslProfilePolicy.DeploymentSequence(profile, byId, direction,
                    _settings.GetSetting(SettingKeys.ProfilesLockLoaders, true)).Select(item => item.ModId).ToArray())).ToArray();
            var deployments = data.Deployments.Select(ProjectDeployment).ToArray();
            var snapshot = new OfflineRuntimeV1("hd2mimnt-runtime", 1, 0, Guid.NewGuid().ToString("D"),
                DateTimeOffset.UtcNow, typeof(OfflineInteropService).Assembly.GetName().Version?.ToString() ?? "UNKNOWN",
                new(_storage.Current.AppDataPath, _storage.Current.ModsPath),
                data.Profiles.FirstOrDefault(profile => profile.IsActive)?.Id, direction.ToString(), library, profiles, deployments);
            await _publisher.PublishAsync(snapshot, cancellationToken).ConfigureAwait(false);
            LastError = null;
        }
        finally { _publishGate.Release(); }
    }

    private CachedFiles GetFiles(Mod mod, CancellationToken cancellationToken)
    {
        var stamp = (mod.Path, mod.ModifiedAt, mod.Hash);
        if (_fileCache.TryGetValue(mod.Id, out var cached) && cached.Stamp == stamp && cached.Complete) return cached;
        var files = new List<OfflineLibraryFileV1>();
        string? error = null;
        try
        {
            // 仅枚举实际补丁，不读取内容，不跟随库内目录链接，不执行 Lua。
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var path in Directory.EnumerateFiles(mod.Path, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PatchDeploymentPlanner.TryParseTarget(path, out _, out _, out _))
                    files.Add(new(Path.GetRelativePath(mod.Path, path).Replace('\\', '/'), new FileInfo(path).Length));
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { error = failure.Message; }
        cached = new(stamp, error == null, error,
            files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray());
        _fileCache[mod.Id] = cached;
        return cached;
    }

    private static OfflineDeploymentV1 ProjectDeployment(DeploymentRecord record)
    {
        var present = new List<DeploymentFile>();
        var missing = new List<string>();
        foreach (var file in record.Files)
            if (File.Exists(file.TargetPath)) present.Add(file); else missing.Add(file.TargetPath);
        return new(record.Id, record.SessionId, record.ProfileId, record.ModId, record.Status,
            record.DeploymentMethod, Utc(record.StartTime), record.EndTime is { } ended ? Utc(ended) : null,
            record.DeploymentOrderIndex, record.ModVersion,
            record.OptionsSnapshotJson is { } json ? State(ProfileModOptionsState.FromJson(json)) : null,
            record.Status == DeploymentStatus.Completed.ToString() && missing.Count == 0,
            present.Select(file => new OfflineDeployedFileV1(string.IsNullOrEmpty(file.SourcePath) ? null : file.SourcePath,
                file.TargetPath, string.IsNullOrEmpty(file.Method) ? record.DeploymentMethod : file.Method,
                string.IsNullOrEmpty(file.SourcePath))).ToArray(), missing);
    }

    private static OfflineOptionStateV1 State(ProfileModOptionsState state)
        => new(state.LegacySelectedIndex, state.OptionToggles, state.OptionSelections);
    private static DateTimeOffset Utc(DateTime value) => new(value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime());
    private sealed record CachedFiles((string Path, DateTime ModifiedAt, string Hash) Stamp, bool Complete,
        string? Error, IReadOnlyList<OfflineLibraryFileV1> Files);

    public async ValueTask DisposeAsync()
    {
        if (!_started) return;
        _started = false;
        _mods.ModsChanged -= Changed;
        _profiles.ProfilesChanged -= ProfilesChanged;
        _deployments.DeploymentsChanged -= Changed;
        _backups.Restored -= Changed;
        _settings.SettingChanged -= SettingChanged;
        _stop.Cancel();
        if (_worker != null) await _worker.ConfigureAwait(false);
        // 完成已授权的状态更新，进程关闭后文件继续可用。
        await Task.Run(() => TryRefreshAsync(CancellationToken.None)).ConfigureAwait(false);
        _stop.Dispose();
    }
}
