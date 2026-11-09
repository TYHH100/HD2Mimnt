using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>Cached logging preferences: the logging filter must never read SQLite or take the settings lock.</summary>
public sealed class DiagnosticLogSettings
{
    public const string LevelKey = "Diagnostics.MinimumLogLevel";
    public const string RetentionKey = "Diagnostics.LogRetentionDays";
    public const int DefaultRetentionDays = 30;
    public const int MinRetentionDays = 1;
    public const int MaxRetentionDays = 365;

    private const LogLevel DefaultLevel = LogLevel.Warning;
    private int _minimumLevel = (int)DefaultLevel;
    private int _retentionDays = DefaultRetentionDays;

    public LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

    /// <summary>日志分片保留天数；读取方（写入器/日志页）按快照使用，不需要加锁。</summary>
    public int RetentionDays => Volatile.Read(ref _retentionDays);

    public void Attach(SettingsService settings)
    {
        void Reload()
        {
            var value = settings.GetSetting(LevelKey, DefaultLevel.ToString());
            var level = Enum.TryParse<LogLevel>(value, out var parsed) && parsed is >= LogLevel.Trace and <= LogLevel.None
                ? parsed : DefaultLevel;
            Volatile.Write(ref _minimumLevel, (int)level);

            var retention = settings.GetSetting(RetentionKey, DefaultRetentionDays);
            Volatile.Write(ref _retentionDays, Math.Clamp(retention, MinRetentionDays, MaxRetentionDays));
        }
        Reload();
        settings.SettingChanged += (_, _) => Reload();
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;

    /// <summary>把任意来源的保留天数收敛到允许区间，供设置页与清理逻辑共用。</summary>
    public static int ClampRetention(int days) => Math.Clamp(days, MinRetentionDays, MaxRetentionDays);
}
