using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.Services;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.UI;
using HD2Mimnt.UI.Controls;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-ModelPreviewProbe-" + Guid.NewGuid().ToString("N"));
    private static readonly string Sample = @"C:\Users\29845\AppData\Local\HD2Mimnt.dev\mods\31ffe377-a0e8-4a3f-b0d8-17b158ffb271";
    private static int _exit;

    [STAThread]
    public static int Main()
    {
        Environment.SetEnvironmentVariable("HD2MIMNT_DATA_PATH", Root);
        using var stop = new CancellationTokenSource();
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithoutStarting();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await RunAsync(); }
            catch (Exception error) { Console.Error.WriteLine(error); _exit = 1; }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        if (Directory.Exists(Root)) Directory.Delete(Root, true);
        return _exit;
    }

    private static async Task RunAsync()
    {
        var collection = new ServiceCollection();
        typeof(App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(Avalonia.Application.Current, [collection]);
        await using var services = collection.BuildServiceProvider();
        await services.GetRequiredService<HD2MimntDbContext>().InitializeDatabaseAsync();
        await services.GetRequiredService<SettingsService>().SetSettingAsync(SettingKeys.GameAutoDetect, false);
        var manifest = await new ManifestParser().ParseFromFileAsync(Path.Combine(Sample, "manifest.json"));
        var mod = new Mod { Id = Guid.NewGuid(), Name = "HD2Mimnt Test Model Preview", Path = Sample, Manifest = manifest };
        await services.GetRequiredService<IModRepository>().AddAsync(mod);
        var main = services.GetRequiredService<MainWindowViewModel>();
        await main.LibraryViewModel.InitializeAsync();
        main.SelectedTabIndex = 1;
        var window = new MainWindow { DataContext = main, Width = 1200, Height = 850, Position = new PixelPoint(80, 40), Topmost = true };
        var vm = main.ModelPreview!;
        try
        {
            window.Show(); window.Activate();
            var watch = Stopwatch.StartNew();
            await vm.OpenAsync(mod.Id);
            Console.WriteLine($"LOAD {watch.ElapsedMilliseconds}ms {vm.Summary} ERROR={vm.Error}");
            if (vm.Scene?.Meshes.Count is not > 0) throw new InvalidOperationException("No sample geometry.");
            foreach (var mesh in vm.Scene.Meshes)
                Console.WriteLine($"MESH {mesh.UnitId:x16} {mesh.PatchFile} v={mesh.VertexCount} t={mesh.TriangleCount} texture={mesh.ColorTextureId:x16}");
            await Frames(window);
            var viewport = window.GetVisualDescendants().OfType<ModelPreviewViewport>().Single();
            var program = (int)Field(viewport, "_program")!;
            var gpuMeshes = (ICollection)Field(viewport, "_meshes")!;
            Console.WriteLine($"GPU program={program} meshes={gpuMeshes.Count} failed={Field(viewport, "_failed")} size={viewport.Bounds} error={vm.Error}");
            if (program == 0 || gpuMeshes.Count == 0 || (bool)Field(viewport, "_failed")!) throw new InvalidOperationException("GPU did not render the scene: " + vm.Error);
            Capture(window, "textured.png");
            var handle = window.TryGetPlatformHandle()!.Handle;
            var point = viewport.TranslatePoint(new Point(viewport.Bounds.Width / 2, viewport.Bounds.Height / 2), window)!.Value;
            Console.WriteLine($"HIT {window.InputHitTest(point)?.GetType().FullName} at={point}");
            window.AddHandler(InputElement.PointerPressedEvent, (_, e) => Console.WriteLine($"DOWN source={e.Source?.GetType().Name} pos={e.GetPosition(window)}"), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
            var screen = window.PointToScreen(point);
            var yaw = (float)Field(viewport, "_yaw")!;
            GetCursorPos(out var originalCursor);
            try
            {
                SetCursorPos(screen.X, screen.Y); await Frames(window);
                mouse_event(0x0002, 0, 0, 0, 0); await Frames(window);
                SetCursorPos(screen.X + 65, screen.Y + 20); await Frames(window);
                mouse_event(0x0004, 0, 0, 0, 0); await Frames(window);
            }
            finally { SetCursorPos(originalCursor.X, originalCursor.Y); }
            await Frames(window);
            Console.WriteLine($"ORBIT {yaw} -> {Field(viewport, "_yaw")}");
            if (yaw == (float)Field(viewport, "_yaw")!) throw new InvalidOperationException("Native orbit input did not move camera.");
            vm.Wireframe = true;
            await Frames(window); Capture(window, "wireframe.png");
            vm.Wireframe = false; vm.UseTextures = false;
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            viewport.ResetCamera();
            await Frames(window); Capture(window, "gray-light.png");
            vm.UseTextures = true; vm.UseAlpha = true;
            await Frames(window);
            if (vm.HasError) throw new InvalidOperationException(vm.Error);
            var cache = (IDictionary)Field(viewport, "_meshCache")!;
            var firstMesh = vm.Scene!.Meshes[0];
            var cached = cache[firstMesh];
            vm.Meshes.First(row => ReferenceEquals(row.Mesh, firstMesh)).IsVisible = false;
            await Frames(window);
            vm.Meshes.First(row => ReferenceEquals(row.Mesh, firstMesh)).IsVisible = true;
            await Frames(window);
            if (!ReferenceEquals(cached, cache[firstMesh])) throw new InvalidOperationException("Visibility toggle reuploaded shared geometry.");
            Console.WriteLine("GPU CACHE reused geometry and textures across visibility toggles");
            vm.ShowStockyBody = true; await Frames(window);
            Console.WriteLine("STOCKY " + vm.Summary);
            foreach (var option in vm.Options) option.IsEnabled = false;
            await vm.ReloadCommand.ExecuteAsync(null);
            await Frames(window);
            if (vm.Scene?.Meshes.Count != 0) throw new InvalidOperationException("All-off selection retained meshes.");
            Console.WriteLine("ALL-OFF empty scene confirmed");
            await vm.StopAsync(); await Frames(window);
            if (window.GetVisualDescendants().OfType<ModelPreviewViewport>().Any()) throw new InvalidOperationException("Closed viewport still attached.");
            await vm.OpenAsync(mod.Id); await Frames(window);
            viewport = window.GetVisualDescendants().OfType<ModelPreviewViewport>().Single();
            if ((int)Field(viewport, "_program")! == 0 || vm.HasError) throw new InvalidOperationException("Reopen failed: " + vm.Error);
            Console.WriteLine($"REOPEN {vm.Summary}; privateMiB={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576}");
        }
        finally
        {
            await vm.StopAsync();
            await main.DisposeAsync();
            window.DataContext = null; window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static object? Field(object value, string name) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);
    private static async Task Frames(Window window)
    {
        for (var i = 0; i < 8; i++)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.RequestAnimationFrame(_ => completion.SetResult());
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }
    private static void Capture(Window window, string name)
    {
        var point = window.PointToScreen(default);
        var size = window.ClientSize * window.RenderScaling;
        using var image = new System.Drawing.Bitmap((int)size.Width, (int)size.Height);
        using var graphics = System.Drawing.Graphics.FromImage(image);
        graphics.CopyFromScreen(point.X, point.Y, 0, 0, image.Size);
        var path = Path.Combine(@"G:\Temp\Githud\HD2Mimnt\tools\ModelPreviewProbe", name);
        image.Save(path); Console.WriteLine("CAPTURE " + path);
    }
    private static nint Coords(int x, int y) => (nint)((y << 16) | (x & 0xFFFF));
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extraInfo);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
}
