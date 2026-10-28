using System.Collections.Concurrent;
using System.Threading.Channels;
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
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.ModelPreview;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using static HD2Mimnt.Tests.Services.CompatibilityFixture;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task ModelPreview_LibraryEntriesAndClose()
    {
        var reader = new ControlledModelPreviewReader();
        await using var scope = await UiScope.CreateAsync(modelPreviewReader: reader);
        var id = await ImportModelPreviewMod(scope, "HD2Mimnt Test model entries");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var mod = library.FilteredMods.Single(item => item.Id == id);
        var preview = Assert.IsType<ModelPreviewViewModel>(main.ModelPreview);
        Assert.Same(reader, scope.Services.GetRequiredService<IModelPreviewReader>());
        Assert.Same(preview, library.ModelPreview);
        Assert.Same(preview, main.ProfileViewModel.ModelPreview);
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 780 };
        try
        {
            window.Show();
            RenderModelPreviewWindow(window);
            var libraryView = window.GetVisualDescendants().OfType<LibraryView>().Single();
            ClickModelPreviewMenu(window, libraryView, mod, library.PreviewModelCommand);
            await library.PreviewModelCommand.ExecutionTask!;
            var firstRead = await reader.NextAsync();
            Assert.Equal(new[] { "body/1234567890abcdef.patch_0", "hat/abcdef1234567890.patch_0" }, firstRead.Patches);
            Assert.All(preview.Options, option => Assert.True(option.IsEnabled));
            var viewport = AssertModelPreviewOpen(window, main, mod.Name);
            Assert.Single(preview.Scene!.Meshes);
            Assert.True(preview.Scene.Meshes[0].VertexCount > 24);

            var overlay = window.FindControl<ModelPreviewOverlay>("ModelPreviewOverlay")!;
            ClickVersionControl(window, overlay.FindControl<Button>("CloseModelPreviewButton")!);
            await preview.CloseCommand.ExecutionTask!;
            AssertModelPreviewClosed(window, main, viewport);

            // 详情入口必须经过实际控件祖先找到 LibraryViewModel，不能直接调用预览命令代替。
            library.ShowModDetailCommand.Execute(mod);
            RenderModelPreviewWindow(window);
            var panel = libraryView.GetVisualDescendants().OfType<ModDetailPanel>().Single(panel => panel.IsEffectivelyVisible);
            var detailButton = panel.FindControl<Button>("PreviewDetailModelButton")!;
            detailButton.BringIntoView();
            ClickVersionControl(window, detailButton);
            await preview.Completion;
            await reader.NextAsync();
            var secondViewport = AssertModelPreviewOpen(window, main, mod.Name);
            Assert.NotSame(viewport, secondViewport);
            Assert.True(library.IsDetailPanelOpen);

            PressModelPreviewEscape(window);
            await preview.CloseCommand.ExecutionTask!;
            AssertModelPreviewClosed(window, main, secondViewport);
            Assert.True(library.IsDetailPanelOpen); // Escape 优先关闭预览，而非底层详情。
        }
        finally
        {
            reader.ReleaseAll();
            await preview.StopAsync();
            library.CloseDetailPanelCommand.Execute(null);
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ModelPreview_ProfileOptionsStaySaved()
    {
        var reader = new ControlledModelPreviewReader();
        await using var scope = await UiScope.CreateAsync(modelPreviewReader: reader);
        var id = await ImportModelPreviewMod(scope, "HD2Mimnt Test profile model");
        await scope.Services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.ProfilesAutoSwitchOnSelect, false);
        var profiles = scope.Services.GetRequiredService<ProfileService>();
        var activeId = await profiles.CreateProfileAsync("Active model profile");
        await profiles.AddModToProfileAsync(activeId, id);
        await profiles.SetActiveProfileAsync(activeId);
        var viewedId = await profiles.CreateProfileAsync("Viewed model profile");
        await profiles.AddModToProfileAsync(viewedId, id);
        await profiles.SetModOptionsAsync(viewedId, id, new ProfileModOptionsState
        {
            OptionToggles = [true, false], OptionSelections = [0, 0]
        });
        var savedProfile = (await profiles.GetProfileByIdAsync(viewedId))!;
        var savedOptions = Assert.Single(savedProfile.Mods).OptionsJson;
        var activeOptions = Assert.Single((await profiles.GetProfileByIdAsync(activeId))!.Mods).OptionsJson;
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        var profile = main.ProfileViewModel;
        await profile.LoadProfilesAsync();
        profile.SelectedProfile = profile.Profiles.Single(item => item.Id == viewedId);
        await profile.RefreshAfterProfilesChangedAsync();
        main.SelectedTabIndex = 0;
        var row = profile.ProfileMods.Single(item => item.ModId == id);
        var preview = main.ModelPreview!;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        try
        {
            window.Show();
            RenderModelPreviewWindow(window);
            var profileView = window.GetVisualDescendants().OfType<ProfileView>().Single();
            ClickModelPreviewMenu(window, profileView, row, profile.PreviewModelCommand);
            await profile.PreviewModelCommand.ExecutionTask!;
            Assert.Equal(new[] { "body/1234567890abcdef.patch_0" }, (await reader.NextAsync()).Patches);
            Assert.Equal(new[] { true, false }, preview.Options.Select(option => option.IsEnabled));
            var viewport = AssertModelPreviewOpen(window, main, row.ModName);
            var overlay = window.FindControl<ModelPreviewOverlay>("ModelPreviewOverlay")!;
            var hatToggle = overlay.GetVisualDescendants().OfType<CheckBox>()
                .Single(toggle => ReferenceEquals(toggle.DataContext, preview.Options[1]));
            ClickVersionControl(window, hatToggle);
            Assert.True(preview.Options[1].IsEnabled);
            ClickVersionControl(window, overlay.FindControl<Button>("ReloadModelPreviewButton")!);
            await preview.ReloadCommand.ExecutionTask!;
            Assert.Equal(new[] { "body/1234567890abcdef.patch_0", "hat/abcdef1234567890.patch_0" },
                (await reader.NextAsync()).Patches);
            Assert.Equal(savedOptions, Assert.Single((await profiles.GetProfileByIdAsync(viewedId))!.Mods).OptionsJson);
            Assert.Equal(activeOptions, Assert.Single((await profiles.GetProfileByIdAsync(activeId))!.Mods).OptionsJson);
            Assert.Equal(savedProfile.Mods[0].Enabled, Assert.Single((await profiles.GetProfileByIdAsync(viewedId))!.Mods).Enabled);
            Assert.Equal(activeId, (await profiles.GetActiveProfileAsync())!.Id);

            ClickVersionControl(window, overlay.FindControl<Button>("CloseModelPreviewButton")!);
            await preview.CloseCommand.ExecutionTask!;
            AssertModelPreviewClosed(window, main, viewport);
            profile.ShowModDetailCommand.Execute(row);
            RenderModelPreviewWindow(window);
            var panel = profileView.GetVisualDescendants().OfType<ModDetailPanel>().Single(panel => panel.IsEffectivelyVisible);
            var detailButton = panel.FindControl<Button>("PreviewDetailModelButton")!;
            detailButton.BringIntoView();
            ClickVersionControl(window, detailButton);
            await preview.Completion;
            Assert.Equal(new[] { "body/1234567890abcdef.patch_0" }, (await reader.NextAsync()).Patches);
            Assert.Equal(new[] { true, false }, preview.Options.Select(option => option.IsEnabled));
            var reopenedViewport = AssertModelPreviewOpen(window, main, row.ModName);
            PressModelPreviewEscape(window);
            await preview.CloseCommand.ExecutionTask!;
            AssertModelPreviewClosed(window, main, reopenedViewport);
            Assert.True(profile.IsDetailPanelOpen);
        }
        finally
        {
            reader.ReleaseAll();
            await preview.StopAsync();
            await profile.CloseModDetailAsync();
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ModelPreview_CancelReopenRejectsStaleResults()
    {
        var reader = new ControlledModelPreviewReader(holdReads: true);
        await using var scope = await UiScope.CreateAsync(modelPreviewReader: reader);
        var firstId = await ImportModelPreviewMod(scope, "HD2Mimnt Test old model");
        var secondId = await ImportModelPreviewMod(scope, "HD2Mimnt Test new model");
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var library = main.LibraryViewModel;
        await library.InitializeAsync();
        main.SelectedTabIndex = 1;
        var firstMod = library.FilteredMods.Single(item => item.Id == firstId);
        var secondMod = library.FilteredMods.Single(item => item.Id == secondId);
        var preview = main.ModelPreview!;
        var observedUnits = new List<ulong>();
        void RecordScene(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ModelPreviewViewModel.Scene) && preview.Scene is { } scene)
                observedUnits.AddRange(scene.Meshes.Select(mesh => mesh.UnitId));
        }
        preview.PropertyChanged += RecordScene;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 780 };
        Task closing = Task.CompletedTask;
        try
        {
            window.Show();
            RenderModelPreviewWindow(window);
            var libraryView = window.GetVisualDescendants().OfType<LibraryView>().Single();
            ClickModelPreviewMenu(window, libraryView, firstMod, library.PreviewModelCommand);
            var firstOpening = library.PreviewModelCommand.ExecutionTask!;
            var oldRead = await reader.NextAsync();
            Assert.True(preview.IsBusy);
            Assert.Null(preview.Scene);
            var overlay = window.FindControl<ModelPreviewOverlay>("ModelPreviewOverlay")!;
            var viewport = Assert.IsType<ModelPreviewViewport>(overlay.FindControl<ContentControl>("ViewportHost")!.Content);
            ClickVersionControl(window, overlay.FindControl<Button>("CancelModelPreviewButton")!);
            await oldRead.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(firstOpening.IsCompleted); // 假读取器仍在清理，UI 不应等待它才能响应。
            Assert.True(preview.IsOpen);
            Assert.Null(preview.Scene);

            PressModelPreviewEscape(window);
            closing = preview.CloseCommand.ExecutionTask!;
            Assert.False(closing.IsCompleted);
            AssertModelPreviewClosed(window, main, viewport);

            // 旧命令尚未返回时，通过另一模组的详情按钮再次打开，验证跨代结果隔离。
            library.ShowModDetailCommand.Execute(secondMod);
            RenderModelPreviewWindow(window);
            var panel = libraryView.GetVisualDescendants().OfType<ModDetailPanel>().Single(panel => panel.IsEffectivelyVisible);
            var detailButton = panel.FindControl<Button>("PreviewDetailModelButton")!;
            detailButton.BringIntoView();
            ClickVersionControl(window, detailButton);
            var reopening = preview.Completion;
            Assert.True(preview.IsOpen);
            Assert.False(main.NavigateToProfilesCommand.CanExecute(null));
            Assert.False(reopening.IsCompleted);
            oldRead.Result.TrySetResult(CreateModelPreviewResult(11));
            await firstOpening;
            await closing;
            var newRead = await reader.NextAsync();
            Assert.True(preview.IsBusy); // 旧关闭任务完成也不能清掉新一代的加载状态。
            Assert.NotEqual(oldRead.Directory, newRead.Directory);
            Assert.Equal(secondMod.Name, preview.Title);
            Assert.Null(preview.Scene);
            Assert.Empty(preview.Meshes);
            Assert.Empty(observedUnits);
            newRead.Result.TrySetResult(CreateModelPreviewResult(22));
            await reopening;
            var newViewport = AssertModelPreviewOpen(window, main, secondMod.Name);
            Assert.NotSame(viewport, newViewport);
            Assert.Equal(22UL, Assert.Single(preview.Scene!.Meshes).UnitId);
            Assert.Equal(22UL, Assert.Single(preview.Meshes).Mesh.UnitId);
            Assert.DoesNotContain(11UL, observedUnits);
            Assert.Contains(22UL, observedUnits);
            Assert.False(preview.IsBusy);
        }
        finally
        {
            reader.ReleaseAll();
            await closing;
            await preview.StopAsync();
            preview.PropertyChanged -= RecordScene;
            library.CloseDetailPanelCommand.Execute(null);
            window.DataContext = null;
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ModelPreview_ViewportAcceptsNativeCameraGestures()
    {
        var viewport = new ModelPreviewViewport();
        var window = new Window { Content = viewport, Width = 640, Height = 480 };
        try
        {
            window.Show();
            RenderModelPreviewWindow(window);
            var point = new Point(200, 180);
            Assert.Same(viewport, window.InputHitTest(point));
            var yaw = CameraField<float>("_yaw");
            window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(40, 10), RawInputModifiers.LeftMouseButton);
            window.MouseUp(point + new Vector(40, 10), MouseButton.Left);
            Assert.NotEqual(yaw, CameraField<float>("_yaw"));
            var target = CameraField<System.Numerics.Vector3>("_target");
            window.MouseDown(point, MouseButton.Right);
            window.MouseMove(point + new Vector(20, 30), RawInputModifiers.RightMouseButton);
            window.MouseUp(point + new Vector(20, 30), MouseButton.Right);
            Assert.NotEqual(target, CameraField<System.Numerics.Vector3>("_target"));
            var distance = CameraField<float>("_distance");
            window.MouseWheel(point, new Vector(0, 1));
            Assert.True(CameraField<float>("_distance") < distance);
            viewport.ResetCamera();
            Assert.Equal(yaw, CameraField<float>("_yaw"));
        }
        finally { window.Close(); }
        T CameraField<T>(string name) => (T)typeof(ModelPreviewViewport)
            .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewport)!;
    }

    [AvaloniaFact]
    public void ModelPreview_PngNormalizationPreservesStraightAlphaAndBgra()
    {
        using var bitmap = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(2, 1), new Vector(96, 96),
            Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
        byte[] pixels = [10, 30, 240, 128, 210, 20, 5, 255];
        using (var buffer = bitmap.Lock()) System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        var source = new Dictionary<ulong, TexturePreviewData> { [1] = new()
            { Width = 2, Height = 1, Description = "PNG", EncodedImageBytes = stream.ToArray() } };
        var normalize = typeof(ModelPreviewViewModel).GetMethod("NormalizeTextures",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (IReadOnlyDictionary<ulong, TexturePreviewData>)normalize.Invoke(null, [source, 512, CancellationToken.None])!;
        var actual = Assert.Single(result).Value.BgraPixels!;
        for (var index = 0; index < pixels.Length; index++) Assert.InRange(Math.Abs(actual[index] - pixels[index]), 0, 1);
    }

    private static async Task<Guid> ImportModelPreviewMod(UiScope scope, string name)
    {
        var source = Path.Combine(scope.Root, "model-source-" + Guid.NewGuid().ToString("N"));
        WritePatch(Path.Combine(source, "body"), "1234567890abcdef.patch_0", resources: [new(1, UnitType, Unit())]);
        WritePatch(Path.Combine(source, "hat"), "abcdef1234567890.patch_0", resources: [new(2, UnitType, Unit())]);
        await File.WriteAllTextAsync(Path.Combine(source, "manifest.json"), $$"""
            {"Version":1,"Name":"{{name}}","Options":[
              {"Name":"Body","Include":["body"]},
              {"Name":"Hat","Include":["hat"]}]}
            """);
        var mods = scope.Services.GetRequiredService<ModService>();
        var id = await mods.ImportModAsync(source);
        await mods.EnsureModHashAsync(id);
        return id;
    }

    private static void RenderModelPreviewWindow(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame(); // 仅刷新布局与命中场景，不验证 Headless 的 GPU 图像。
    }

    private static void ClickModelPreviewMenu(Window window, Control view, object mod, ICommand command)
    {
        RenderModelPreviewWindow(window);
        var card = view.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("mod-card") && ReferenceEquals(border.DataContext, mod));
        var title = card.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text ==
            (mod is ModItemViewModel libraryMod ? libraryMod.Name : ((ProfileModViewModel)mod).ModName));
        title.BringIntoView();
        RenderModelPreviewWindow(window);
        var point = title.TranslatePoint(new Point(12, title.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        RenderModelPreviewWindow(window);
        var menu = Assert.IsType<FAMenuFlyout>(card.ContextFlyout);
        Assert.True(menu.IsOpen);
        var entry = menu.Items.OfType<FAMenuFlyoutItem>().Single(item => ReferenceEquals(item.Command, command));
        Assert.Same(mod, entry.CommandParameter);
        var popup = Assert.IsAssignableFrom<TopLevel>(TopLevel.GetTopLevel(entry));
        popup.UpdateLayout();
        using var frame = popup.CaptureRenderedFrame();
        Assert.True(entry.IsEffectivelyVisible && entry.IsEffectivelyEnabled);
        Assert.True(entry.Bounds.Width > 0 && entry.Bounds.Height > 0);
        var menuPoint = entry.TranslatePoint(new Point(20, entry.Bounds.Height / 2), popup)!.Value;
        popup.MouseMove(menuPoint);
        popup.MouseDown(menuPoint, MouseButton.Left);
        popup.MouseUp(menuPoint, MouseButton.Left);
        RenderModelPreviewWindow(window);
        Assert.False(menu.IsOpen);
    }

    private static ModelPreviewViewport AssertModelPreviewOpen(Window window, MainWindowViewModel main, string title)
    {
        RenderModelPreviewWindow(window);
        var preview = main.ModelPreview!;
        Assert.True(preview.IsOpen);
        Assert.False(preview.IsBusy);
        Assert.Equal(title, preview.Title);
        Assert.NotNull(preview.Scene);
        Assert.False(main.NavigateToProfilesCommand.CanExecute(null));
        Assert.False(main.NavigateToLibraryCommand.CanExecute(null));
        var overlay = window.FindControl<ModelPreviewOverlay>("ModelPreviewOverlay")!;
        Assert.True(overlay.IsEffectivelyVisible);
        var host = overlay.FindControl<ContentControl>("ViewportHost")!;
        var viewport = Assert.IsType<ModelPreviewViewport>(host.Content);
        Assert.Same(viewport, Assert.Single(overlay.GetVisualDescendants().OfType<ModelPreviewViewport>()));
        Assert.Same(preview.Scene, viewport.Scene);
        foreach (var control in new Control[] { host, overlay.FindControl<Button>("CloseModelPreviewButton")!,
                     overlay.FindControl<Button>("ReloadModelPreviewButton")! })
        {
            Assert.True(control.Bounds.Width > 0 && control.Bounds.Height > 0);
            var origin = control.TranslatePoint(default, window)!.Value;
            Assert.InRange(origin.X, 0, window.Bounds.Width - control.Bounds.Width);
            Assert.InRange(origin.Y, 0, window.Bounds.Height - control.Bounds.Height);
        }
        return viewport;
    }

    private static void AssertModelPreviewClosed(Window window, MainWindowViewModel main, ModelPreviewViewport oldViewport)
    {
        RenderModelPreviewWindow(window);
        var preview = main.ModelPreview!;
        Assert.False(preview.IsOpen);
        Assert.Null(preview.Scene);
        Assert.Empty(preview.Meshes);
        Assert.Empty(preview.Options);
        var overlay = window.FindControl<ModelPreviewOverlay>("ModelPreviewOverlay")!;
        Assert.False(overlay.IsEffectivelyVisible);
        Assert.Null(overlay.FindControl<ContentControl>("ViewportHost")!.Content);
        Assert.Empty(window.GetVisualDescendants().OfType<ModelPreviewViewport>());
        Assert.Null(oldViewport.Scene);
        Assert.Null(oldViewport.DataContext);
        Assert.True(main.NavigateToProfilesCommand.CanExecute(null));
        Assert.True(main.NavigateToLibraryCommand.CanExecute(null));
    }

    private static void PressModelPreviewEscape(Window window)
    {
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        RenderModelPreviewWindow(window);
    }

    private static ModelPreviewResult CreateModelPreviewResult(ulong unitId)
    {
        // 5×5 网格的 25 个顶点与 32 个三角形，避免被代理几何过滤器隐藏。
        var positions = new float[25 * 3];
        var indices = new List<int>();
        for (var y = 0; y < 5; y++)
        for (var x = 0; x < 5; x++)
        {
            var index = y * 5 + x;
            positions[index * 3] = x;
            positions[index * 3 + 1] = y;
            if (x < 4 && y < 4)
                indices.AddRange([index, index + 1, index + 6, index, index + 6, index + 5]);
        }
        var result = new ModelPreviewResult();
        result.Meshes.Add(new ModelPreviewMesh
        {
            PatchFile = "1234567890abcdef.patch_0", UnitId = unitId, StreamIndex = 0, MeshInfoIndex = 0,
            Positions = positions, TriangleIndices = indices.ToArray()
        });
        return result;
    }

    private sealed class ModelPreviewRead(DirectoryInfo directory, IReadOnlyList<FileInfo> patches)
    {
        public string Directory { get; } = directory.FullName;
        public string[] Patches { get; } = patches.Select(file => Path.GetRelativePath(directory.FullName, file.FullName).Replace('\\', '/')).ToArray();
        public TaskCompletionSource<ModelPreviewResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ControlledModelPreviewReader(bool holdReads = false) : IModelPreviewReader
    {
        private readonly Channel<ModelPreviewRead> _started = Channel.CreateUnbounded<ModelPreviewRead>();
        private readonly ConcurrentQueue<ModelPreviewRead> _reads = new();
        private bool _holdReads = holdReads;

        public Task<ModelPreviewRead> NextAsync() => _started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public async Task<ModelPreviewResult> PreviewModelAsync(DirectoryInfo modDirectory, IReadOnlyList<FileInfo> patchFiles,
            bool forceDecodeOversizedStreams = false, CancellationToken cancellationToken = default)
        {
            var read = new ModelPreviewRead(modDirectory, patchFiles);
            using var registration = cancellationToken.Register(() => read.Cancelled.TrySetResult());
            _reads.Enqueue(read);
            _started.Writer.TryWrite(read);
            if (!Volatile.Read(ref _holdReads)) read.Result.TrySetResult(CreateModelPreviewResult(1));
            // 故意允许取消后返回结果；由真实 Application/ViewModel 阻止过期结果回填。
            return await read.Result.Task;
        }

        public Task<TexturePreviewData?> PreviewTextureAsync(DirectoryInfo modDirectory, TextureInspectionItem texture,
            int maxPreviewPixels = 1048576, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The fixture mesh has no texture references.");

        public void ReleaseAll()
        {
            Volatile.Write(ref _holdReads, false);
            foreach (var read in _reads) read.Result.TrySetResult(CreateModelPreviewResult(1));
        }
    }
}
