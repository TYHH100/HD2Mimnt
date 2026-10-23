using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Diagnostics;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task CrashDiagnostics_ShouldUseActualDiCompareMarkExportAndCancelThroughView()
    {
        var analyzer = new TestMinidumpAnalyzer();
        await using var scope = await UiScope.CreateAsync(minidumpAnalyzer: analyzer);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var vm = main.CrashDiagnosticsViewModel!;
        var history = scope.Services.GetRequiredService<CrashDeploymentHistoryService>();
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        var gamePath = Path.Combine(scope.Root, "Helldivers 2"); Directory.CreateDirectory(gamePath);
        await scope.Services.GetRequiredService<GameDetectionService>().ApplyConfiguredPathAsync(gamePath);
        // 测试只需要应用已配置路径，不要求安装真实游戏。
        var before = DateTime.UtcNow.AddHours(-2);
        var old = new CrashModSnapshot(Guid.NewGuid(), "已有模组", "1", "a", before, "a", "", 0);
        var added = new CrashModSnapshot(Guid.NewGuid(), "新增的很长很长很长的测试模组名称", "1", "b", before.AddMinutes(10), "b", "", 1);
        var baseline = new CrashDeploymentSnapshot(Guid.NewGuid(), Guid.NewGuid(), "基准", gamePath, before, before.AddSeconds(1), "Completed", false, [old]);
        var current = baseline with { SessionId = Guid.NewGuid(), StartedUtc = before.AddMinutes(10), EndedUtc = before.AddMinutes(11), Mods = [old, added] };
        await history.SaveAsync(baseline); await history.SaveAsync(current);
        analyzer.Result = new("EXCEPTION_ACCESS_VIOLATION_READ", "0x12", "helldivers2.exe",
            [new(0, "helldivers2.exe", "0x626296", "", "context")], [new("helldivers2.exe", "1", "0x10", "0x20")], before.AddMinutes(20));
        main.SelectedTabIndex = 3; main.MoreViewModel.SelectedCategory = "游戏崩溃排查";
        await vm.Completion;
        vm.DumpPath = Path.Combine(scope.Root, "test.dmp");
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var app = Avalonia.Application.Current!; var previousTheme = app.RequestedThemeVariant;
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); window.CaptureRenderedFrame()?.Dispose(); }
            Layout(); main.NotificationPanelViewModel.DismissAllToasts(); Layout();
            var view = window.GetVisualDescendants().OfType<CrashDiagnosticsView>().Single();
            Assert.Same(vm, view.DataContext);
            var analyze = view.FindControl<Button>("AnalyzeCrashButton")!;
            var point = analyze.TranslatePoint(new Avalonia.Point(analyze.Bounds.Width / 2, analyze.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            await vm.AnalyzeCommand.ExecutionTask!; Layout();
            Assert.True(vm.HasReport); Assert.Contains("证据不足", vm.Summary);
            Assert.Contains(vm.ChangeLines, line => line.Contains(added.Name));
            Assert.Contains("尚未确认正常", vm.ContextWarning);
            foreach (var code in new[] { "zh-CN", "zh-TW", "en-US" })
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                await language.SetLanguageAsync(code); app.RequestedThemeVariant = theme;
                Layout(); Assert.True(analyze.Bounds.Width > 50);
                Assert.True(view.FindControl<TextBox>("CrashDumpPathInput")!.Bounds.Width >= 180);
                Assert.DoesNotContain("Crash.", vm.Summary);
                Capture(window, theme.ToString(), "crash-diagnostics-" + code);
                var scroll = view.GetVisualAncestors().OfType<ScrollViewer>().First();
                scroll.Offset = new Vector(0, 500); Layout();
                Assert.True(scroll.Offset.Y > 0);
                Capture(window, theme.ToString(), "crash-diagnostics-result-" + code);
                scroll.Offset = default; Layout();
            }
            await language.SetLanguageAsync("zh-CN");
            vm.SelectedHistory = vm.History.Single(snapshot => snapshot.SessionId == baseline.SessionId);
            var mark = view.FindControl<Button>("MarkCrashBaselineButton")!;
            mark.Command!.Execute(null); await vm.MarkKnownGoodCommand.ExecutionTask!;
            Assert.False(vm.HasReport);
            Assert.True((await history.GetRecentAsync()).Single(snapshot => snapshot.SessionId == baseline.SessionId).KnownGood);
            await vm.AnalyzeCommand.ExecuteAsync(null);
            Assert.Contains("已确认正常", vm.ComparisonDetails);
            var export = Path.Combine(scope.Root, "report.json"); await vm.ExportAsync(export);
            var json = await File.ReadAllTextAsync(export); Assert.Contains("Added", json); Assert.Contains("Inconclusive", json);
            analyzer.WaitForCancellation = true;
            var pending = vm.AnalyzeCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            await main.CanCloseAsync(); await pending;
            Assert.False(vm.IsBusy); Assert.True(analyzer.Cancelled); Assert.True(vm.Completion.IsCompleted);
        }
        finally { window.DataContext = null; window.Close(); app.RequestedThemeVariant = previousTheme; }
    }

    private sealed class TestMinidumpAnalyzer : IMinidumpAnalyzer
    {
        public bool IsAvailable => true;
        public bool WaitForCancellation { get; set; }
        public bool Cancelled { get; private set; }
        public CrashDumpAnalysis Result { get; set; } = new("", "", "", [], []);
        public async Task<CrashDumpAnalysis> AnalyzeAsync(string dumpPath, CancellationToken cancellationToken = default)
        {
            if (WaitForCancellation)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return Result;
        }
    }
}
