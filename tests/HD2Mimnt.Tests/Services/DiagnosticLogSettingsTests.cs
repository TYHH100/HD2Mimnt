using HD2Mimnt.Application.Services;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Tests.Services;

public sealed class DiagnosticLogSettingsTests
{
    [Fact]
    public async Task Level_ShouldDefaultToWarningPersistApplyAndReset()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-LogSettings-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService(root);
            var options = new DiagnosticLogSettings();
            options.Attach(settings);
            using var factory = LoggerFactory.Create(builder => builder.AddFilter((_, level) => options.IsEnabled(level)));
            var logger = factory.CreateLogger("test");
            Assert.False(logger.IsEnabled(LogLevel.Debug));
            Assert.False(options.IsEnabled(LogLevel.Information));
            Assert.True(options.IsEnabled(LogLevel.Warning));
            await settings.SetSettingAsync(DiagnosticLogSettings.LevelKey, "Debug");
            Assert.True(options.IsEnabled(LogLevel.Debug));
            var reopened = new DiagnosticLogSettings();
            reopened.Attach(new SettingsService(root));
            Assert.Equal(LogLevel.Debug, reopened.MinimumLevel);
            await settings.SetSettingAsync(DiagnosticLogSettings.LevelKey, "None");
            Assert.False(options.IsEnabled(LogLevel.Critical));
            await settings.ResetCategoryAsync("Diagnostics");
            Assert.Equal(LogLevel.Warning, options.MinimumLevel);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
