using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
    public async Task HellForge_ModsCategory_ShouldListAddToggleAndOrderModsInHellForgeFolder()
    {
        await using var scope = await UiScope.CreateAsync();
        using var fixture = HellForgeCliFixture.Create();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); using var frame = window.CaptureRenderedFrame(); }
            void Click(Button button)
            {
                Layout();
                // 成功通知吐司悬浮在右上角，可能盖住“添加模组…”按钮；点击前先收起。
                main.NotificationPanelViewModel.DismissAllToasts();
                Layout();
                Assert.True(button.IsEffectivelyEnabled);
                var point = button.TranslatePoint(new Point(10, 8), window)!.Value;
                var hit = window.InputHitTest(point) as Visual;
                Assert.True(button.IsVisualAncestorOf(hit) || ReferenceEquals(hit, button),
                    $"点击 {button.Name} 命中了 {hit?.GetType().Name ?? "null"}（点 {point}）");
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Layout();
            }
            Layout();
            main.SettingsViewModel.HellForgePath = fixture.Root;
            main.SettingsViewModel.IsOfficialGameSource = false;
            main.SettingsViewModel.SelectedCategory = "游戏";
            await main.SettingsViewModel.SaveCommand.ExecuteAsync(null);
            var hf = main.HellForgeViewModel!;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            // 文件夹模组：把清单所在文件夹交给 mod-add，直接写进 HellForge 的 mods\。
            var first = CreateSourceMod(fixture.Root, "hf-alpha", "custom.alpha", "Alpha Mod");
            var second = CreateSourceMod(fixture.Root, "hf-beta", "custom.beta", "Beta Mod");

            // 切到模组分区才按需读取 mods\（不拖慢页面初始化）。
            hf.SelectedCategory = hf.Categories.Single(category => category.Key == "Mods");
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            Assert.False(hf.HasMods);

            await hf.AddModAsync(first);
            await hf.AddModFromFolderAsync(Path.GetDirectoryName(second)!);
            Layout();
            Assert.Equal(2, hf.HellForgeMods.Count);
            Assert.Contains("mod-add --file", fixture.ReadArgs());
            Assert.True(File.Exists(Path.Combine(fixture.Root, "mods", "hf-alpha", "hellforge-mod.json")));

            // 启停：行内开关经 mod-switch 直接改 HellForge 的开关。
            var alpha = hf.HellForgeMods.Single(mod => mod.Id == "custom.alpha");
            alpha.Enabled = false;
            await hf.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            Layout();
            Assert.False(alpha.Enabled);
            Assert.Contains("mod-switch --id custom.alpha --on no", fixture.ReadArgs());

            // 排序：同组内下移/上移走 mod-order，列表本地同步顺序。
            var alphaIndex = hf.HellForgeMods.IndexOf(alpha);
            await hf.MoveModDownCommand.ExecuteAsync(alpha);
            Layout();
            Assert.Equal(alphaIndex + 1, hf.HellForgeMods.IndexOf(alpha));
            Assert.Contains("mod-order --ids", fixture.ReadArgs());

            // “添加模组…”按钮必须真正触发视图事件（回归：事件曾在 Attached 时机漏订阅）。
            var addRequested = false;
            hf.AddModRequested += (_, _) => addRequested = true;
            var view = window.GetVisualDescendants().OfType<HellForgeView>().Single();
            Click(view.FindControl<Button>("AddHellForgeModButton")!);
            Assert.True(addRequested);

            Assert.True(view.FindControl<ItemsControl>("HellForgeModList")!.IsEffectivelyVisible);
            Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(),
                notification => notification.Message.Contains("Alpha Mod"));
        }
        finally { window.DataContext = null; window.Close(); }
    }

    private static string CreateSourceMod(string root, string folder, string id, string name)
    {
        var source = Path.Combine(root, "incoming", folder);
        Directory.CreateDirectory(source);
        var manifest = Path.Combine(source, "hellforge-mod.json");
        File.WriteAllText(manifest, $$"""{"id":"{{id}}","name":"{{name}}","version":"1.0.0","gameplay":true}""");
        return manifest;
    }
}
