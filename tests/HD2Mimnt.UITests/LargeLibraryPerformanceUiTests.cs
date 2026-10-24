using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Application.Services;
using HD2Mimnt.PerformanceTests;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    [Trait("Category", "Performance")]
    public async Task LargeLibrary_TenThousandImagesWithSqlFlood_MeasureResponsiveness()
        => await RunLargeLibraryWorkloadAsync(withConflictPatches: false, automaticChecking: false);

    [AvaloniaFact]
    [Trait("Category", "Performance")]
    public async Task LargeLibrary_ConflictPatches_AutomaticCheckingDisabled()
        => await RunLargeLibraryWorkloadAsync(withConflictPatches: true, automaticChecking: false);

    [AvaloniaFact]
    [Trait("Category", "Performance")]
    public async Task LargeLibrary_ConflictPatches_AutomaticCheckingEnabled()
        => await RunLargeLibraryWorkloadAsync(withConflictPatches: true, automaticChecking: true);

    private async Task RunLargeLibraryWorkloadAsync(bool withConflictPatches, bool automaticChecking)
    {
        // 显式启用，避免普通 UI 回归顺带生成 1 万张图片与数 GiB 的解码数据。
        if (Environment.GetEnvironmentVariable("HD2MIMNT_RUN_LARGE_PERF") != "1")
        {
            Assert.Skip("设置 HD2MIMNT_RUN_LARGE_PERF=1 后运行大数据性能测试。");
            return;
        }
        await using var scope = await UiScope.CreateAsync();
        var template = Path.Combine(scope.Root, "template.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(256, 256), new Vector(96, 96)))
        {
            using (var buffer = bitmap.Lock())
            {
                var pixels = new byte[buffer.RowBytes * buffer.Size.Height];
                new Random(42).NextBytes(pixels);
                for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
                Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
            }
            bitmap.Save(template);
        }
        var db = scope.Services.GetRequiredService<HD2MimntDbContext>();
        var ids = await Task.Run(() => LargeLibraryWorkload.SeedAsync(db, scope.Root, template,
            resourcesPerMod: withConflictPatches ? 100 : 0));
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.ProfilesAutoCheckConflicts, automaticChecking);
        Assert.Equal(10000, Directory.GetFiles(Path.Combine(scope.Root, "images")).Length);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var actions = new List<object>();
        var memory = new ConcurrentQueue<object>();
        var delays = new ConcurrentQueue<double>();
        var phases = new ConcurrentDictionary<string, ConcurrentQueue<double>>();
        var scanDelays = new ConcurrentQueue<double>();
        var conflicts = main.ConflictsViewModel;
        var scans = new List<object>();
        var scanStarted = 0;
        var scanCompleted = 0;
        var wasScanningActive = false;
        Stopwatch? scanTimer = null;
        var scanStartPhase = "";
        var phase = "initialization";
        conflicts.ScanCompleted += (_, _) => scanCompleted++;
        conflicts.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ConflictsViewModel.IsScanning)) return;
            // 状态刷新会重复通知 IsScanning；只记录实际的 false/true 边沿。
            if (wasScanningActive == conflicts.IsScanning) return;
            wasScanningActive = conflicts.IsScanning;
            if (conflicts.IsScanning)
            {
                scanStarted++;
                scanTimer = Stopwatch.StartNew();
                scanStartPhase = phase;
            }
            else if (scanTimer != null)
            {
                scans.Add(new { Index = scanStarted, PhaseAtStart = scanStartPhase,
                    PipelineMs = scanTimer.Elapsed.TotalMilliseconds, Completed = conflicts.HasScanned && !conflicts.HasError,
                    Groups = conflicts.Groups.Count, Warnings = conflicts.Warnings.Count, Summary = conflicts.SummaryText });
                scanTimer = null;
            }
        };
        using var stop = new CancellationTokenSource();
        using var stopSql = new CancellationTokenSource();
        var heartbeat = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var start = Stopwatch.GetTimestamp();
                var wasScanning = conflicts.IsScanning;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    delays.Enqueue(elapsed);
                    phases.GetOrAdd(phase, _ => new()).Enqueue(elapsed);
                    if (wasScanning || conflicts.IsScanning) scanDelays.Enqueue(elapsed);
                }, DispatcherPriority.Input);
                await Task.Delay(16);
            }
        });
        var memorySampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var process = Process.GetCurrentProcess();
                memory.Enqueue(new { Phase = phase, IsScanning = conflicts.IsScanning, WorkingMiB = process.WorkingSet64 / 1048576d,
                    PrivateMiB = process.PrivateMemorySize64 / 1048576d, ManagedMiB = GC.GetTotalMemory(false) / 1048576d });
                await Task.Delay(250);
            }
        });
        var flood = LargeLibraryWorkload.FloodAsync(db.DatabasePath, ids, perWorker: 25_000, continueUntil: stopSql.Token);
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 830 };
        void Layout()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        async Task Measure(string name, Func<Task> action)
        {
            phase = name;
            await Task.Delay(25); // 让输入探针先入队，能观察同步命令造成的停顿。
            var timer = Stopwatch.StartNew();
            var before = GC.GetTotalAllocatedBytes(false);
            await action();
            Layout();
            actions.Add(new { Name = name, ElapsedMs = timer.Elapsed.TotalMilliseconds,
                AllocatedMiB = (GC.GetTotalAllocatedBytes(false) - before) / 1048576d });
            await Task.Delay(25);
        }
        async Task WaitForAutomaticScan(int count, bool starting = false)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            while (starting ? !conflicts.IsScanning : scanCompleted < count || conflicts.IsScanning)
            {
                Assert.False(conflicts.HasError, conflicts.ErrorMessage);
                await Task.Delay(20, timeout.Token);
            }
            if (!starting)
            {
                Assert.True(conflicts.HasScanned);
                Assert.Equal(50_000, conflicts.Groups.Count);
                Assert.Empty(conflicts.Warnings);
                Assert.Contains("1000000", conflicts.SummaryText);
                Assert.All(main.ProfileViewModel.ProfileMods, mod => Assert.True(mod.HasConflict));
            }
        }
        async Task<int> TriggerRecheck(string name)
        {
            var before = scanCompleted;
            await Measure(name, async () =>
            {
                var mod = main.ProfileViewModel.ProfileMods[0];
                Assert.True(mod.Enabled);
                await main.ProfileViewModel.ToggleModEnabledCommand.ExecuteAsync(mod);
                Assert.False(mod.Enabled);
                await main.ProfileViewModel.ToggleModEnabledCommand.ExecuteAsync(mod);
                Assert.True(mod.Enabled);
            });
            if (automaticChecking) await WaitForAutomaticScan(before + 1, starting: true);
            return before + 1;
        }
        try
        {
            window.Show();
            await Measure("main_initialize", () => main.InitializeAsync());
            Assert.Equal(automaticChecking, main.SettingsViewModel.AutoCheckConflicts);
            Assert.False(main.LibraryViewModel.HasError, main.LibraryViewModel.ErrorMessage);
            Assert.False(main.ProfileViewModel.HasProfileModsError, main.ProfileViewModel.ProfileModsErrorMessage);
            Assert.Equal(10000, main.LibraryViewModel.FilteredMods.Count);
            Assert.Equal(10000, main.ProfileViewModel.ProfileMods.Count);
            main.NotificationPanelViewModel.DismissAllToasts();
            await Measure("all_20000_icons_ready", async () =>
            {
                var timeout = Stopwatch.StartNew();
                while (main.LibraryViewModel.FilteredMods.Any(mod => mod.Icon == null)
                    || main.ProfileViewModel.ProfileMods.Any(mod => mod.Icon == null))
                {
                    Assert.True(timeout.Elapsed < TimeSpan.FromMinutes(4), "图片加载超过 4 分钟");
                    await Task.Delay(50);
                }
            });
            if (automaticChecking)
                await Measure("initial_automatic_scan_ready", () => WaitForAutomaticScan(1));
            var frames = new Dictionary<string, object>();
            async Task Scroll(string name, int tab)
            {
                main.SelectedTabIndex = tab;
                Layout();
                await Task.Delay(100);
                var view = tab == 1 ? (Control)window.GetVisualDescendants().OfType<LibraryView>().Single()
                    : window.GetVisualDescendants().OfType<ProfileView>().Single();
                var items = view.FindControl<ItemsControl>(tab == 1 ? "ModList" : "ModItemsControl")!;
                var scroll = items.GetVisualAncestors().OfType<ScrollViewer>().First();
                var times = new List<double>();
                var scanningTimes = new List<double>();
                var maxContainers = 0;
                phase = name;
                for (var i = 0; i < 180; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    var scanning = conflicts.IsScanning;
                    var point = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
                    window.MouseWheel(point, new Vector(0, -3));
                    Layout();
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    maxContainers = Math.Max(maxContainers, items.GetRealizedContainers().Count());
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    times.Add(elapsed);
                    if (scanning || conflicts.IsScanning) scanningTimes.Add(elapsed);
                    await Task.Delay(16);
                }
                Assert.True(scroll.Offset.Y > 0);
                Assert.InRange(maxContainers, 1, 40);
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                Layout();
                using var finalFrame = window.CaptureRenderedFrame();
                Assert.NotNull(items.ContainerFromIndex(items.ItemCount - 1));
                frames[name] = new { FrameWorkMs = LargeLibraryWorkload.Distribution(times),
                    FrameWorkWhileScanningMs = LargeLibraryWorkload.Distribution(scanningTimes), MaxRealizedContainers = maxContainers,
                    LastIndex = items.ItemCount - 1, SqlFloodStillRunningAtEnd = !flood.IsCompleted };
                if (automaticChecking) Assert.NotEmpty(scanningTimes);
            }
            var libraryRecheck = withConflictPatches ? await TriggerRecheck("trigger_library_recheck") : 0;
            await Scroll("library_scroll", 1);
            if (automaticChecking) await Measure("library_recheck_ready", () => WaitForAutomaticScan(libraryRecheck));
            var library = main.LibraryViewModel;
            foreach (var query in new[] { "Armor", "Perf 09999", "no_match_here", "" })
                await Measure("library_search_" + query, async () => { library.SearchText = query; await library.ExecuteSearchCommand.ExecuteAsync(null); });
            Assert.Equal(10000, library.FilteredMods.Count);
            await Measure("library_sort_descending", () => library.SortCommand.ExecuteAsync(ModSortMode.NameDescending));
            await Measure("library_select_all", () => { library.SelectAllCommand.Execute(null); Assert.Equal(10000, library.SelectedCount); return Task.CompletedTask; });
            await Measure("library_clear_selection", () => { library.ClearSelectionCommand.Execute(null); return Task.CompletedTask; });
            var profileRecheck = withConflictPatches ? await TriggerRecheck("trigger_profile_recheck") : 0;
            await Scroll("profile_scroll", 0);
            if (automaticChecking) await Measure("profile_recheck_ready", () => WaitForAutomaticScan(profileRecheck));
            var profile = main.ProfileViewModel;
            foreach (var query in new[] { "Armor", "Perf 09999", "no_match_here", "" })
                await Measure("profile_search_" + query, () => { profile.ModSearchText = query; return Task.CompletedTask; });
            Assert.Equal(10000, profile.VisibleProfileMods.Count);
            await Measure("profile_select_all", () => { profile.ToggleSelectAllModsCommand.Execute(null); Assert.Equal(10000, profile.SelectedModCount); return Task.CompletedTask; });
            await Measure("profile_clear_selection", () => { profile.ClearModSelectionCommand.Execute(null); return Task.CompletedTask; });
            await Measure("profile_refresh", () => profile.RefreshAfterProfilesChangedAsync());
            await Measure("profile_refreshed_icons_ready", async () =>
            {
                var timeout = Stopwatch.StartNew();
                while (profile.ProfileMods.Any(mod => mod.Icon == null))
                {
                    Assert.True(timeout.Elapsed < TimeSpan.FromMinutes(4));
                    await Task.Delay(50);
                }
            });
            phase = "steady_state";
            var completedBeforeIdle = scanCompleted;
            await Task.Delay(5000);
            Assert.Equal(completedBeforeIdle, scanCompleted);
            Assert.False(conflicts.IsScanning);
            Assert.False(conflicts.HasError, conflicts.ErrorMessage);
            if (automaticChecking) Assert.True(scanCompleted >= 3);
            else Assert.Equal(0, scanStarted);
            stopSql.Cancel();
            var sql = await flood;
            stop.Cancel();
            await heartbeat;
            await memorySampler;
            var reportName = withConflictPatches ? "large-library-ui-auto-conflicts-" + (automaticChecking ? "on" : "off") : "large-library-ui";
            LargeLibraryWorkload.Save(reportName, new { DateUtc = DateTime.UtcNow, Runtime = Environment.Version.ToString(),
                Mods = 10000, IndependentImagePaths = 10000, ImageWidth = 256, ImageHeight = 256,
                AutomaticChecking = automaticChecking, Patches = withConflictPatches ? 10000 : 0,
                Resources = withConflictPatches ? 1000000 : 0, AutomaticScansStarted = scanStarted, AutomaticScansCompleted = scanCompleted,
                Scans = scans, DispatcherWhileScanningMs = LargeLibraryWorkload.Distribution(scanDelays),
                // 内容相同但路径独立，当前路径缓存不能合并；所有实际 PNG 均经 Skia 解码。
                ImageContent = "seeded noise PNG, identical bytes at 10000 separate paths", Actions = actions,
                Frames = frames, DispatcherLatencyMs = LargeLibraryWorkload.Distribution(delays),
                PhaseLatencyMs = phases.ToDictionary(x => x.Key, x => LargeLibraryWorkload.Distribution(x.Value)),
                MemorySamples = memory.ToArray(), SqlFlood = sql, Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2) });
        }
        finally
        {
            stop.Cancel();
            stopSql.Cancel();
            await heartbeat;
            await memorySampler;
            await flood;
            await main.CanCloseAsync();
            window.DataContext = null;
            window.Close();
        }
    }
}
