using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using HD2Mimnt.UI;

[assembly: AvaloniaTestApplication(typeof(HD2Mimnt.Tests.UI.TestAppBuilder))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace HD2Mimnt.Tests.UI;

// 整个测试程序集共用由 Headless 测试宿主拥有的 UI 线程和应用实例。
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
