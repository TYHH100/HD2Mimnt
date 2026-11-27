using Avalonia.Controls;
using Avalonia.Headless;
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
    public async Task ModelToIas_CategoryBindsStatusWithoutRigGenderSelector()
    {
        await using var scope = await UiScope.CreateAsync();
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var language = scope.Services.GetRequiredService<ILocalizationService>();
        await language.SetLanguageAsync("zh-CN");
        main.SelectedTabIndex = 3;
        var window = new MainWindow { DataContext = main, Width = 960, Height = 720 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame()?.Dispose();
            var more = window.GetVisualDescendants().OfType<MoreView>().Single();
            main.MoreViewModel.SelectedCategory = "普通模型转 IAS";
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame()?.Dispose();

            Assert.True(main.MoreViewModel.IsModelToIasVisible);
            Assert.NotNull(main.ModelToIasViewModel);
            Assert.Equal("普通模型转 IAS", more.FindControl<TextBlock>("ModelToIasTitle")!.Text);
            await main.ModelToIasViewModel.RefreshCommand.ExecutionTask!;
            Assert.False(main.ModelToIasViewModel.IsLoading);

            Assert.DoesNotContain(more.GetVisualDescendants().OfType<RadioButton>(),
                button => button.GroupName == "ModelToIasRigGender");

            await language.SetLanguageAsync("en-US");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Convert ordinary models to IAS", more.FindControl<TextBlock>("ModelToIasTitle")!.Text);
            await language.SetLanguageAsync("zh-CN");
        }
        finally
        {
            window.DataContext = null;
            window.Close();
        }
    }
}
