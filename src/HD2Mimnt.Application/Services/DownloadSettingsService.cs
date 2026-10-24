using System.Text.Json;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;

namespace HD2Mimnt.Application.Services;

/// <summary>Persisted defaults read once for each new transfer.</summary>
public sealed class DownloadSettingsService(SettingsService settingsService) : IDownloadOptionsProvider
{
    public async Task<DownloadOptions> GetOptionsAsync()
    {
        var values = await settingsService.GetAllSettingsAsync();
        T Read<T>(string key, T fallback)
        {
            if (!values.TryGetValue(key, out var value)) return fallback;
            try { return value is JsonElement json ? json.Deserialize<T>()! : (T)value; }
            catch (Exception ex) when (ex is JsonException or InvalidCastException) { return fallback; }
        }
        var defaults = new DownloadOptions();
        var options = new DownloadOptions
        {
            MaxParallelSegments = Math.Clamp(Read(SettingKeys.DownloadParallelSegments, defaults.MaxParallelSegments), 1, 16),
            MinimumSegmentSize = Math.Clamp(Read(SettingKeys.DownloadSegmentSizeMiB, 4), 1, 256) * 1024L * 1024,
            MaxRetries = Math.Clamp(Read(SettingKeys.DownloadMaxRetries, defaults.MaxRetries), 0, 10),
            RetryDelay = TimeSpan.FromMilliseconds(Math.Clamp(Read(SettingKeys.DownloadRetryDelayMs, 500), 0, 60000)),
            EnableResume = Read(SettingKeys.DownloadEnableResume, defaults.EnableResume)
        };
        return options;
    }

    public Task SaveAsync(DownloadOptions options)
    {
        options.Validate();
        if (options.MinimumSegmentSize % (1024 * 1024) != 0 || options.MinimumSegmentSize is < 1024 * 1024 or > 256 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumSegmentSize), "分段大小必须为 1–256 MiB 的整数。");
        return settingsService.SetSettingsAsync(new Dictionary<string, object>
        {
            [SettingKeys.DownloadParallelSegments] = options.MaxParallelSegments,
            [SettingKeys.DownloadSegmentSizeMiB] = (int)(options.MinimumSegmentSize / (1024 * 1024)),
            [SettingKeys.DownloadMaxRetries] = options.MaxRetries,
            [SettingKeys.DownloadRetryDelayMs] = (int)options.RetryDelay.TotalMilliseconds,
            [SettingKeys.DownloadEnableResume] = options.EnableResume
        });
    }
}
