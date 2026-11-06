using System.Buffers.Binary;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task GmodIasConversion_ToolsPageRendersTranslatesAndWritesBundle()
    {
        await using var scope = await UiScope.CreateAsync(confirmation: new RecordingConfirmation());
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var vm = main.GmodIasConversionViewModel;
        Assert.NotNull(vm);

        var addon = Directory.CreateDirectory(Path.Combine(scope.Root, "gmod-addon")).FullName;
        Directory.CreateDirectory(Path.Combine(addon, "materials", "fixture"));
        File.WriteAllBytes(Path.Combine(addon, "fixture.mdl"), MdlFixture());
        File.WriteAllBytes(Path.Combine(addon, "fixture.vvd"), VvdFixture());
        File.WriteAllBytes(Path.Combine(addon, "fixture.dx90.vtx"), VtxFixture());
        const string material = "\"VertexLitGeneric\"\n{\n\"$basetexture\" \"textures/base\"\n}\n";
        File.WriteAllText(Path.Combine(addon, "materials", "fixture", "base.vmt"), material);
        File.WriteAllText(Path.Combine(addon, "materials", "fixture", "variant.vmt"), material);
        var output = Path.Combine(scope.Root, "gmod-output");

        var window = new MainWindow { DataContext = main, Width = 960, Height = 600 };
        var localization = scope.Services.GetRequiredService<ILocalizationService>();
        try
        {
            await localization.SetLanguageAsync("zh-CN");
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
            Layout();
            main.SelectedTabIndex = 3;
            main.MoreViewModel.SelectedCategory = "GMOD 模型转换";
            Layout();

            var view = Assert.Single(window.GetVisualDescendants().OfType<GmodIasConversionView>());
            Assert.True(view.IsEffectivelyVisible);
            Assert.True(main.MoreViewModel.IsGmodIasVisible);
            var title = view.FindControl<TextBlock>("GmodIasTitle")!;
            Assert.Equal("GMOD 模型转 HD2 IAS", title.Text);

            // 语言切换后已显示的静态文案必须实时刷新；工具页分类也要在英文下反查成功。
            await localization.SetLanguageAsync("en-US");
            Layout();
            Assert.Equal("GMOD model to HD2 IAS conversion", title.Text);
            Assert.Contains("Fill in", vm!.StatusMessage);
            var localizer = scope.Services.GetRequiredService<HD2Mimnt.UI.Localization.UiLocalization>();
            Assert.NotEqual("GMOD 模型转换", localizer.Translate("GMOD 模型转换"));

            // 选定 addon 后必须自动分析内容，并把可转换模型喂给模型下拉框。
            vm!.AcceptAddonPath(addon);
            await vm.AnalysisTask;
            Layout();
            var option = Assert.Single(vm.ModelOptions);
            Assert.Equal("fixture.mdl", option.Path);
            Assert.Equal("fixture.mdl", vm.SelectedModel?.Path);
            Assert.Equal("fixture.mdl", vm.ModelPath);
            Assert.Contains("1", vm.StatusMessage);
            var combo = view.FindControl<ComboBox>("GmodIasModelCombo")!;
            Assert.Single(combo.ItemsSource!.Cast<object>());
            Assert.Same(vm.SelectedModel, combo.SelectedItem);

            vm.AcceptModelPath("fixture.mdl");
            vm.AcceptOutputDirectory(output);
            vm.ProjectId = "ui-fixture";
            vm.ArchiveId = "0xabcdef";
            vm.NativeUnitIds = "0x101";
            // 默认按身体/头盔拆分发布；fixture 没有头骨，应只产出 body/ 包。
            Assert.NotNull(view.FindControl<ComboBox>("GmodIasPartSplitCombo"));
            Assert.NotNull(view.FindControl<TextBox>("GmodIasHelmetArchiveId"));
            Assert.NotNull(view.FindControl<TextBox>("GmodIasSwitchSelection"));
            Assert.Equal(0, vm.PartSplitIndex);
            vm.SwitchSelection = "skin";
            Assert.True(vm.CanStart);
            await vm.ConvertCommand.ExecuteAsync(null);
            Assert.False(vm.IsBusy);
            Assert.True(vm.HasResult, vm.StatusMessage);
            Assert.True(File.Exists(Path.Combine(output, "manifest.json")), vm.StatusMessage);
            Assert.True(File.Exists(Path.Combine(output, "body/geometry/model.hd2geom")));
            Assert.False(Directory.Exists(Path.Combine(output, "helmet")));
            Assert.Contains("body", vm.ResultText);
            Assert.Contains(output, vm.ResultText);
            // 现在直接产出游戏原生资源：每个已发布 Unit 一份 .unit，并给出可导入的 data 模组包。
            Assert.True(File.Exists(Path.Combine(output, "body/package/manifest.json")), vm.StatusMessage);
            Assert.True(File.Exists(Path.Combine(output, "body/package/data/9ba626afa44a3aa3.patch_0")),
                vm.StatusMessage);
            Assert.True(File.Exists(Path.Combine(output, "body/runnable.json")), vm.StatusMessage);
            Assert.Contains("RuntimeNotVerified", vm.IssuesText);
            await localization.SetLanguageAsync("auto");
        }
        finally { window.Close(); }
    }

    private static void Int(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);
    private static void Short(byte[] data, int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    private static void Float(byte[] data, int offset, float value) => BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
    private static void Text(byte[] data, int offset, string value) => Encoding.UTF8.GetBytes(value).CopyTo(data, offset);

    private static byte[] MdlFixture()
    {
        var data = new byte[1500];
        Text(data, 0, "IDST"); Int(data, 4, 48); Int(data, 8, 123); Text(data, 12, "fixture"); Int(data, 76, data.Length);
        Int(data, 156, 1); Int(data, 160, 408);
        Int(data, 408, 216); Int(data, 412, -1); Text(data, 624, "ValveBiped.Bip01_Pelvis");
        Float(data, 408 + 56, 1); Float(data, 408 + 156, 1);
        Float(data, 408 + 96, 1); Float(data, 408 + 116, 1); Float(data, 408 + 136, 1);
        Int(data, 204, 2); Int(data, 208, 656);
        Int(data, 656, 128); Text(data, 784, "base");
        Int(data, 720, 72); Text(data, 792, "variant");
        Int(data, 212, 1); Int(data, 216, 808); Int(data, 808, 812); Text(data, 812, "fixture/");
        Int(data, 220, 1); Int(data, 224, 2); Int(data, 228, 824);
        Short(data, 824, 0); Short(data, 826, 1);
        Int(data, 232, 1); Int(data, 236, 832);
        Int(data, 832, 16); Int(data, 836, 2); Int(data, 840, 1); Int(data, 844, 32); Text(data, 848, "hat");
        Text(data, 864, "visible"); Int(data, 864 + 72, 1); Int(data, 864 + 76, 296); Int(data, 864 + 80, 3);
        Text(data, 1012, "blank");
        Int(data, 1160 + 8, 3); Int(data, 1160 + 52, 3);
        return data;
    }

    private static byte[] VvdFixture()
    {
        var data = new byte[256];
        Text(data, 0, "IDSV"); Int(data, 4, 4); Int(data, 8, 123); Int(data, 12, 1); Int(data, 16, 3);
        Int(data, 56, 64); Int(data, 60, 208);
        for (var i = 0; i < 3; i++)
        {
            var offset = 64 + i * 48;
            Float(data, offset, 1); data[offset + 15] = 1;
            Float(data, offset + 16, i == 1 ? 1 : 0); Float(data, offset + 20, i == 2 ? 1 : 0);
            Float(data, offset + 36, 1);
            Float(data, 208 + i * 16, 1); Float(data, 220 + i * 16, 1);
        }
        return data;
    }

    private static byte[] VtxFixture()
    {
        var data = new byte[178];
        Int(data, 0, 7); Int(data, 16, 123); Int(data, 20, 1); Int(data, 28, 1); Int(data, 32, 36);
        Int(data, 36, 2); Int(data, 40, 8);
        Int(data, 44, 1); Int(data, 48, 16);
        Int(data, 52, 1); Int(data, 56, 20);
        Int(data, 60, 1); Int(data, 64, 24);
        Int(data, 84, 1); Int(data, 88, 9);
        Int(data, 93, 3); Int(data, 97, 25);
        Int(data, 101, 3); Int(data, 105, 52);
        Int(data, 109, 1); Int(data, 113, 58);
        for (var i = 0; i < 3; i++)
        {
            data[118 + i * 9 + 3] = 1;
            Short(data, 118 + i * 9 + 4, (ushort)i);
            Short(data, 145 + i * 2, (ushort)i);
        }
        Int(data, 151, 3); Int(data, 159, 3); Short(data, 167, 1); data[169] = 1;
        return data;
    }
}
