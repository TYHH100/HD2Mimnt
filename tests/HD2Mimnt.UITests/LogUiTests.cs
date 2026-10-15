using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Tests.UI;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task Logs_ShouldRenderDetailsAndSaveRecordingLevelInSettings()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var settings = main.SettingsViewModel;
        var options = scope.Services.GetRequiredService<DiagnosticLogSettings>();
        var logger = scope.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HD2Mimnt.Infrastructure.Platforms.NexusModsPlatformAdapter");
        Assert.Equal(LogLevel.Warning, options.MinimumLevel);
        Assert.False(logger.IsEnabled(LogLevel.Debug));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
        main.NavigateToSettingsCommand.Execute(null);
        settings.SelectedCategory = "日志";
        settings.GamePath = "invalid-game-path";
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!;
        var theme = app.RequestedThemeVariant;
        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
        try
        {
            window.Show(); Layout();
            main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<SettingsView>().Single();
            var input = view.FindControl<ComboBox>("LogRecordingLevelInput")!;
            Assert.True(input.IsEffectivelyVisible);
            Assert.Equal("Warning", input.SelectedItem);
            input.SelectedItem = "Debug";
            await settings.SaveCommand.ExecuteAsync(null);
            Assert.True(logger.IsEnabled(LogLevel.Debug));
            Assert.Equal("Debug", new SettingsService(scope.Root).GetSetting(DiagnosticLogSettings.LevelKey, "Warning"));
            await settings.ResetCommand.ExecuteAsync(null);
            Assert.Equal("Warning", settings.LogRecordingLevel);
            Assert.False(logger.IsEnabled(LogLevel.Debug));

            var logService = scope.Services.GetRequiredService<IFileLogService>();
            await File.WriteAllLinesAsync(logService.LogFilePath, new[]
            {
                "2026-10-15 13:00:00.000 +08:00 [Warning] HD2Mimnt.Application.Services.SettingsService: 示例警告",
                "2026-10-15 13:01:00.000 +08:00 [Debug] HD2Mimnt.Infrastructure.Platforms.NexusModsPlatformAdapter: Nexus API response: 获取网页文件列表; GET /v1/games/helldivers2/mods/17373/files.json; HTTP 403 Forbidden; Version=1.1; Headers=[{\"Name\":\"userid\",\"Value\":\"[REDACTED]\"},{\"Name\":\"Content-Type\",\"Value\":\"application/json\"}]; Body={\"code\":403,\"message\":\"Mod not available: 17373\"}"
            });
            main.SelectedTabIndex = 3;
            main.MoreViewModel.SelectedCategory = "日志";
            var logs = main.LogViewModel;
            await logs.RefreshCommand.ExecuteAsync(null);
            var localization = scope.Services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "en-US", "zh-TW" })
            foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                await localization.SetLanguageAsync(language);
                app.RequestedThemeVariant = variant;
                Layout();
                var logView = window.GetVisualDescendants().OfType<LogView>().Single();
                Assert.True(logView.IsEffectivelyVisible);
                var detail = logView.FindControl<TextBox>("LogDetailText")!;
                Assert.True(detail.Bounds.Height >= 70);
                Assert.Contains("\"code\": 403", detail.Text);
                var point = detail.TranslatePoint(new Avalonia.Point(0, detail.Bounds.Height), window)!.Value;
                Assert.True(point.Y <= window.ClientSize.Height, $"Detail bottom: {point.Y}; window: {window.ClientSize.Height}");
                if (Environment.GetEnvironmentVariable("HD2MIMNT_LOG_UI_CAPTURE") is { Length: > 0 } directory)
                {
                    using var frame = window.CaptureRenderedFrame();
                    frame!.Save(Path.Combine(directory, $"logs-{language}-{variant.Key}.png"));
                }
            }
            var search = window.GetVisualDescendants().OfType<LogView>().Single().FindControl<TextBox>("LogSearchInput")!;
            search.Text = "17373"; Layout();
            Assert.Single(logs.EntryItems);
            Assert.Contains("17373", logs.GetRecentText());
        }
        finally { app.RequestedThemeVariant = theme; window.Close(); }
    }
}
