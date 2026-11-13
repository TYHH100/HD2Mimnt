using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

/// <summary>外观页字体设置：系统字体与字体文件两种来源各自显示对应输入，并把选择写入应用级字体资源。</summary>
public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task AppearanceFont_CanSwitchBetweenSystemFamilyAndFontFile()
    {
        await using var scope = await UiScope.CreateAsync();
        var vm = scope.Services.GetRequiredService<SettingsViewModel>();
        await vm.LoadAsync();
        vm.SelectedCategory = "外观";
        // 系统字体在后台枚举；下拉必须有真实候选，否则聚焦展开只会得到空列表。
        for (var attempt = 0; attempt < 200 && vm.SystemFontFamilies.Count == 0; attempt++)
            await System.Threading.Tasks.Task.Delay(20);
        Assert.NotEmpty(vm.SystemFontFamilies);
        var view = new SettingsView { DataContext = vm };
        var window = new Window { Width = 1040, Height = 800, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(view.FindControl<ComboBox>("FontSourceInput")!.IsEffectivelyVisible);
            var family = view.FindControl<ComboBox>("FontFamilyInput")!;
            var fileRow = view.FindControl<Grid>("FontFileRow")!;
            Assert.False(family.IsEffectivelyVisible);
            Assert.False(fileRow.IsEffectivelyVisible);

            vm.FontSource = AppFontService.SourceInstalled;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(family.IsEffectivelyVisible);
            Assert.False(fileRow.IsEffectivelyVisible);

            // 打开/关闭下拉再选择一项：曾因下拉关闭路径在 Avalonia 内部抛索引越界而崩溃。
            family.IsDropDownOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            family.IsDropDownOpen = false;
            Dispatcher.UIThread.RunJobs();
            var picked = vm.SystemFontFamilies[0];
            family.SelectedItem = picked;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(picked, vm.FontFamilyName);
            Assert.Contains(picked, vm.FontPreviewFamily.ToString());

            vm.SetFontFilePath(@"C:\Fonts\demo.ttf");
            Assert.Equal(AppFontService.SourceFile, vm.FontSource);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.True(fileRow.IsEffectivelyVisible);
            Assert.False(family.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AppearanceFont_ApplyOverridesUiAndDisplayFontAndDefaultRestores()
    {
        var app = global::Avalonia.Application.Current!;
        try
        {
            AppFontService.Apply(AppFontService.SourceInstalled, "Consolas", string.Empty);
            Assert.Contains("Consolas", Resource(app, AppFontService.FontResourceKey).ToString());
            Assert.Contains("Consolas", Resource(app, DisplayFontService.DisplayFontKey).ToString());
            Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(
                new Avalonia.Media.Typeface((Avalonia.Media.FontFamily)Resource(app, AppFontService.FontResourceKey)),
                out var systemGlyph));
            Assert.Equal("Consolas", systemGlyph.FamilyName);

            AppFontService.Apply(AppFontService.SourceDefault, string.Empty, string.Empty);
            Assert.DoesNotContain("Consolas", Resource(app, AppFontService.FontResourceKey).ToString());

            var fontPath = Path.Combine(System.AppContext.BaseDirectory, "Assets", "Fonts", "Monocraft.ttf");
            AppFontService.Apply(AppFontService.SourceFile, string.Empty, fontPath);
            Assert.Contains("Monocraft", Resource(app, AppFontService.FontResourceKey).ToString());
            // 字体文件注册后必须真的能解析出字形，否则界面会静默回退系统字体。
            Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(
                new Avalonia.Media.Typeface((Avalonia.Media.FontFamily)Resource(app, AppFontService.FontResourceKey)),
                out var fileGlyph));
            Assert.Equal("Monocraft", fileGlyph.FamilyName);
        }
        finally
        {
            AppFontService.Apply(AppFontService.SourceDefault, string.Empty, string.Empty);
        }
    }

    private static object Resource(global::Avalonia.Application app, string key)
    {
        Assert.True(app.Resources.TryGetResource(key, ThemeVariant.Default, out var value));
        return value!;
    }
}
