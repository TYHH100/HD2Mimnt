using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Application.Services;

/// <summary>Cached logging preferences: the logging filter must never read SQLite or take the settings lock.</summary>
public sealed class DiagnosticLogSettings
{
    public const string LevelKey = "Diagnostics.MinimumLogLevel";
    private const LogLevel DefaultLevel = LogLevel.Warning;
    private int _minimumLevel = (int)DefaultLevel;
    public LogLevel MinimumLevel => (LogLevel)Volatile.Read(ref _minimumLevel);

    public void Attach(SettingsService settings)
    {
        void Reload()
        {
            var value = settings.GetSetting(LevelKey, DefaultLevel.ToString());
            var level = Enum.TryParse<LogLevel>(value, out var parsed) && parsed is >= LogLevel.Trace and <= LogLevel.None
                ? parsed : DefaultLevel;
            Volatile.Write(ref _minimumLevel, (int)level);
        }
        Reload();
        settings.SettingChanged += (_, _) => Reload();
    }

    public bool IsEnabled(LogLevel level) => level != LogLevel.None && level >= MinimumLevel;
}
