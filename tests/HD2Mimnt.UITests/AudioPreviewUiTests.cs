using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.AudioPreview;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    private const string AudioPackageName = "9ba626afa44a3aa3";
    private const string AudioPatchRelative = AudioPackageName + ".patch_0";

    [AvaloniaFact]
    public async Task AudioPreview_LibraryEntriesPlaybackAndEscape()
    {
        var reader = new StubAudioPreviewReader();
        var playback = new FakeAudioPlayback();
        await using var scope = await UiScope.CreateAsync(audioPreviewReader: reader, audioPlayback: playback);
        var id = await ImportAudioMod(scope, "HD2Mimnt Test audio entries");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var mod = library.FilteredMods.Single(item => item.Id == id);
        var preview = Assert.IsType<AudioPreviewViewModel>(main.AudioPreview);
        Assert.Same(reader, scope.Services.GetRequiredService<IAudioPreviewReader>());
        Assert.Same(playback, scope.Services.GetRequiredService<IAudioPlaybackEngine>());
        Assert.Same(preview, library.AudioPreview);
        Assert.Same(preview, main.ProfileViewModel.AudioPreview);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 780 };
        try
        {
            window.Show();
            RenderAudioWindow(window);

            // 右键菜单入口与命令接线。
            var libraryView = window.GetVisualDescendants().OfType<LibraryView>().Single();
            ClickAudioMenu(window, libraryView, mod, library.PreviewAudioCommand);
            await library.PreviewAudioCommand.ExecutionTask!;
            await preview.Completion;
            var overlay = window.FindControl<AudioPreviewOverlay>("AudioPreviewOverlay")!;
            Assert.True(preview.IsOpen);
            Assert.False(preview.IsBusy);
            Assert.Equal(mod.Name, preview.Title);
            Assert.True(preview.HasEntries);
            Assert.Equal(2, preview.TotalCount);
            Assert.Equal(1, preview.BankCount);
            Assert.True(preview.HasOriginalComparison); // 两条目：一条已替换、一条原版
            Assert.False(main.NavigateToProfilesCommand.CanExecute(null)); // 打开时禁止切页
            Assert.True(overlay.IsEffectivelyVisible);
            var list = overlay.FindControl<ListBox>("AudioEntriesList")!;
            Assert.Equal(3, preview.Rows.Count); // 分组头 + 两条目

            // 行内播放按钮的命令经绑定接到 VM 命令，播放请求进入引擎。
            var rowButtons = list.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, preview.ToggleEntryPlaybackCommand)).ToList();
            Assert.Equal(2, rowButtons.Count);
            var firstRow = Assert.IsType<AudioEntryRow>(rowButtons[0].CommandParameter);
            Assert.Equal(1001UL, firstRow.Model.SourceId);
            ClickAudioControl(window, rowButtons[0]);
            Assert.Equal([1001UL], playback.PlayRequests);
            Assert.Same(firstRow.Model, playback.CurrentEntry);
            Assert.True(preview.IsAudioPlaying);
            Assert.True(firstRow.IsPlaying);

            // Escape 优先关闭音频预览而非底层页面。
            PressAudioEscape(window);
            await preview.CloseCommand.ExecutionTask!;
            Assert.False(preview.IsOpen);
            Assert.False(overlay.IsEffectivelyVisible);
            Assert.True(main.NavigateToProfilesCommand.CanExecute(null));

            // 详情面板按钮入口（真实控件祖先回溯，不经命令代替）。
            library.ShowModDetailCommand.Execute(mod);
            RenderAudioWindow(window);
            var panel = libraryView.GetVisualDescendants().OfType<ModDetailPanel>().Single(panel => panel.IsEffectivelyVisible);
            var detailButton = panel.FindControl<Button>("PreviewDetailAudioButton")!;
            detailButton.BringIntoView();
            ClickAudioControl(window, detailButton);
            await preview.Completion;
            Assert.True(preview.IsOpen);
            Assert.Equal(3, preview.Rows.Count);
        }
        finally
        {
            await preview.StopAsync();
            library.CloseDetailPanelCommand.Execute(null);
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AudioPreview_EmptyModShowsNoComparisonFilter()
    {
        var reader = new StubAudioPreviewReader(empty: true);
        var playback = new FakeAudioPlayback();
        await using var scope = await UiScope.CreateAsync(audioPreviewReader: reader, audioPlayback: playback);
        var id = await ImportAudioMod(scope, "HD2Mimnt Test audio empty");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var mod = library.FilteredMods.Single(item => item.Id == id);
        var preview = main.AudioPreview!;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 780 };
        try
        {
            window.Show();
            RenderAudioWindow(window);
            library.PreviewAudioCommand.Execute(mod);
            await preview.Completion;
            var overlay = window.FindControl<AudioPreviewOverlay>("AudioPreviewOverlay")!;
            Assert.True(preview.IsOpen);
            Assert.False(preview.HasEntries);
            Assert.False(preview.HasOriginalComparison);
            Assert.False(overlay.FindControl<CheckBox>("OnlyShowModifiedCheck")!.IsEffectivelyVisible);
            var list = overlay.FindControl<ListBox>("AudioEntriesList")!;
            Assert.Empty(preview.Rows);
            Assert.Empty(list.Items);
        }
        finally
        {
            await preview.StopAsync();
            window.DataContext = null;
            window.Close();
        }
    }

    private static async Task<Guid> ImportAudioMod(UiScope scope, string name)
    {
        var source = Path.Combine(scope.Root, "audio-source-" + Guid.NewGuid().ToString("N"));
        WritePatch(Path.Combine(source, "audio"), $"{AudioPackageName}.patch_0", resources: []);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"),
            $$"""{"Version":1,"Name":"{{name}}","Options":[{"Name":"Audio","Include":["audio"]}]}""");
        var mods = scope.Services.GetRequiredService<ModService>();
        var id = await mods.ImportModAsync(source);
        await mods.EnsureModHashAsync(id);
        return id;
    }

    private static void RenderAudioWindow(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame();
    }

    private static void ClickAudioControl(Window window, Control control)
    {
        control.BringIntoView();
        RenderAudioWindow(window);
        var origin = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(origin);
        window.MouseDown(origin, MouseButton.Left);
        window.MouseUp(origin, MouseButton.Left);
        RenderAudioWindow(window);
    }

    private static void ClickAudioMenu(Window window, Control view, object mod, ICommand command)
    {
        RenderAudioWindow(window);
        var card = view.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
        var title = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text ==
            (mod is ModItemViewModel libraryMod ? libraryMod.Name : ((ProfileModViewModel)mod).ModName));
        title.BringIntoView();
        RenderAudioWindow(window);
        var point = title.TranslatePoint(new Point(12, title.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        RenderAudioWindow(window);
        var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
        Assert.True(menu.IsOpen);
        var entry = menu.Items.OfType<FAMenuFlyoutItem>().Single(item => ReferenceEquals(item.Command, command));
        Assert.Same(mod, entry.CommandParameter);
        var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(entry));
        popup.UpdateLayout();
        using var frame = popup.CaptureRenderedFrame();
        Assert.True(entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled);
        var menuPoint = entry.TranslatePoint(new Point(20, entry.Bounds.Height / 2), popup)!.Value;
        popup.MouseMove(menuPoint);
        popup.MouseDown(menuPoint, MouseButton.Left);
        popup.MouseUp(menuPoint, MouseButton.Left);
        RenderAudioWindow(window);
        Assert.False(menu.IsOpen);
    }

    private static void PressAudioEscape(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        RenderAudioWindow(window);
    }

    /// <summary>返回两条目清单：#1001 已替换、#1002 原版；empty 时无条目。</summary>
    private sealed class StubAudioPreviewReader(bool empty = false) : IAudioPreviewReader
    {
        public Task<AudioInventoryResult> InspectAsync(DirectoryInfo modDirectory, IReadOnlyList<FileInfo> patchFiles,
            Func<string, IGameAudioBaseline?>? baselineResolver, CancellationToken cancellationToken = default)
        {
            var backing = patchFiles.Count > 0 ? patchFiles[0].FullName : Path.Combine(modDirectory.FullName, AudioPatchRelative);
            if (empty)
                return Task.FromResult(new AudioInventoryResult([], patchFiles.Count, null));
            AudioInventoryResult result = new(
            [
                new AudioBankGroup(AudioPatchRelative, "test_bank", 1,
                [
                    new AudioEntry(1001, AudioEntryOrigin.BankMedia, AudioPatchRelative, "test_bank", 1, backing, 16, 300, 2, 48000,
                        AudioEntryIssue.None, MatchesOriginal: false),
                    new AudioEntry(1002, AudioEntryOrigin.BankMedia, AudioPatchRelative, "test_bank", 1, backing, 316, 300, 2, 48000,
                        AudioEntryIssue.None, MatchesOriginal: true),
                ]),
            ], patchFiles.Count, null);
            return Task.FromResult(result);
        }
    }

    private sealed class FakeAudioPlayback : IAudioPlaybackEngine
    {
        public List<ulong> PlayRequests { get; } = [];
        public event Action<AudioEntry, string?>? PlaybackEnded;

        /// <summary>测试辅助：模拟音频线程的自然播完回调。</summary>
        public void RaiseEnded(AudioEntry entry, string? error) => PlaybackEnded?.Invoke(entry, error);

        public AudioEntry? CurrentEntry { get; private set; }
        public AudioPlaybackState State => CurrentEntry is null ? AudioPlaybackState.Idle : AudioPlaybackState.Playing;
        public TimeSpan Position => CurrentEntry is null ? TimeSpan.Zero : TimeSpan.FromSeconds(0.5);
        public TimeSpan Duration => CurrentEntry is null ? TimeSpan.Zero : TimeSpan.FromSeconds(2);

        public void SetVolume(float volume) { }

        public Task<(bool Success, string? Error)> PlayAsync(AudioEntry entry, CancellationToken cancellationToken)
        {
            CurrentEntry = entry;
            PlayRequests.Add(entry.SourceId);
            return Task.FromResult((true, (string?)null));
        }

        public void Pause() { }
        public void Resume() { }
        public double Seek(double seconds) => seconds;
        public void Stop() => CurrentEntry = null;
        public void Dispose() { }
    }
}
