using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Tests.Services;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task HellForge_Settings_ShouldShowCliValuesAndSaveOnlyChanges()
    {
        await using var scope = await UiScope.CreateAsync();
        using var fixture = HellForgeCliFixture.Create();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();
            main.SettingsViewModel.HellForgePath = fixture.Root;
            main.SettingsViewModel.IsOfficialGameSource = false;
            main.SettingsViewModel.SelectedCategory = "游戏";
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            var hf = main.HellForgeViewModel!;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            var view = window.GetVisualDescendants().OfType<HellForgeView>().Single();

            // 设置卡显示 CLI settings 的值；无改动时“保存设置”不可用。
            Assert.True(hf.HasSettings);
            Assert.Equal("live", hf.SelectedWar!.Value);
            Assert.True(hf.SettingSandbox);
            Assert.False(hf.IsSettingsDirty);
            var saveButton = view.FindControl<Button>("SaveHellForgeSettingsButton")!;
            Assert.True(saveButton.IsEffectivelyVisible);
            Assert.False(saveButton.IsEffectivelyEnabled);
            Assert.Equal(hf.SelectedWar, view.FindControl<ComboBox>("WarSelector")!.SelectedItem);

            // 改动两项后保存：只发送改动项（settings --key value）。
            hf.SelectedWar = hf.WarOptions.Single(o => o.Value == "frozen");
            hf.SettingSandbox = false;
            Layout();
            Assert.True(hf.IsSettingsDirty);
            Assert.True(saveButton.IsEffectivelyEnabled);
            await hf.SaveSettingsCommand.ExecuteAsync(null);
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            Assert.Contains("settings --war frozen --sandbox no", fixture.ReadArgs());
            Assert.Equal("frozen", hf.SelectedWar!.Value);
            Assert.False(hf.SettingSandbox);
            Assert.False(hf.IsSettingsDirty);
            Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(),
                n => n.Message.Contains("设置已保存"));
        }
        finally { window.DataContext = null; window.Close(); }
    }

    [AvaloniaFact]
    public async Task HellForge_Logs_ShouldListRunsAndExportZipWithOptionalDump()
    {
        await using var scope = await UiScope.CreateAsync();
        using var fixture = HellForgeCliFixture.Create();
        var runA = Path.Combine(fixture.Root, "logs", "sessions", "runA");
        Directory.CreateDirectory(runA);
        File.WriteAllText(Path.Combine(runA, "session.log"), "first");
        File.WriteAllText(Path.Combine(runA, "run.json"),
            """{"status":"completed","gameExitCode":"0x00000000"}""");
        var runB = Path.Combine(fixture.Root, "logs", "sessions", "runB");
        Directory.CreateDirectory(runB);
        File.WriteAllText(Path.Combine(runB, "session.log"), "second");
        File.WriteAllText(Path.Combine(runB, "crash.dmp"), "dump");
        File.WriteAllText(Path.Combine(runB, "run.json"),
            """{"status":"crashed","crashed":true,"failure":"boom"}""");

        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            Layout();
            main.SettingsViewModel.HellForgePath = fixture.Root;
            main.SettingsViewModel.IsOfficialGameSource = false;
            main.SettingsViewModel.SelectedCategory = "游戏";
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            var hf = main.HellForgeViewModel!;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();

            // 日志列表最新在前（替身按运行目录名倒序），默认选中最新一次。
            Assert.True(hf.HasLogs);
            Assert.Equal(new[] { "runB", "runA" }, hf.LogRuns.Select(r => r.Id));
            Assert.Equal("runB", hf.SelectedLogRun!.Id);
            Assert.True(hf.SelectedLogRun.Crashed);
            Assert.Contains("崩溃", hf.SelectedLogRun.DetailText);
            var view = window.GetVisualDescendants().OfType<HellForgeView>().Single();
            Assert.True(view.FindControl<ListBox>("LogRunList")!.IsEffectivelyVisible);
            Assert.True(view.FindControl<Button>("ExportLogsButton")!.IsEffectivelyEnabled);
            Assert.True(view.FindControl<Button>("SaveCrashReportButton")!.IsEffectivelyEnabled);

            // 导出所选会话的日志 zip；勾选“包含崩溃转储”后崩溃报告带上 dump\。
            var zip = Path.Combine(fixture.Root, "out", "logs.zip");
            await hf.ExportLogsAsync(zip);
            using (var archive = ZipFile.OpenRead(zip))
                Assert.Equal(new[] { "crash.dmp", "run.json", "session.log" },
                    archive.Entries.Select(e => e.FullName).Order());
            Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(),
                n => n.Message.Contains("已保存 3 个日志文件"));

            hf.IncludeCrashDump = true;
            var report = Path.Combine(fixture.Root, "out", "crash.zip");
            await hf.CreateCrashReportAsync(report);
            using (var archive = ZipFile.OpenRead(report))
            {
                var names = archive.Entries.Select(e => e.FullName).Order().ToArray();
                Assert.Contains("summary.txt", names);
                Assert.Contains("logs/session.log", names);
                Assert.Contains("dump/crash-1.dmp", names);
            }
            Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(),
                n => n.Message.Contains("崩溃报告已保存"));
        }
        finally { window.DataContext = null; window.Close(); }
    }
}
