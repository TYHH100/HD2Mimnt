using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using TaskStatus = HD2Mimnt.Core.Entities.TaskStatus;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaFact]
    public async Task TaskCenter_ClearFinished_ShouldDeleteFinishedRecordsAndKeepRunningTask()
    {
        var confirmation = new RecordingConfirmation();
        await using var scope = await UiScope.CreateAsync(confirmation: confirmation);
        var repository = scope.Services.GetRequiredService<ITaskRepository>();
        var finished = new ModTask
        {
            Id = Guid.NewGuid(), Name = "已完成的历史任务", Type = "Scan", Status = TaskStatus.Completed,
            Progress = 100, Stage = "Completed", StartTime = DateTime.UtcNow.AddMinutes(-2), EndTime = DateTime.UtcNow
        };
        var running = new ModTask
        {
            Id = Guid.NewGuid(), Name = "运行中的任务", Type = "Scan", Status = TaskStatus.Running,
            Progress = 42, CurrentItem = "mod-heavy-package.zip", StartTime = DateTime.UtcNow
        };
        var failed = new ModTask
        {
            Id = Guid.NewGuid(), Name = "部署任务", Type = "Deployment", Status = TaskStatus.Failed,
            Progress = 78, Stage = "Failed", StartTime = DateTime.UtcNow.AddMinutes(-5), EndTime = DateTime.UtcNow.AddMinutes(-4),
            Message = "复制 BSL 文件时目标路径被游戏占用，已回滚本次部署并保留上一次成功记录。"
        };
        await repository.AddAsync(finished);
        await repository.AddAsync(running);
        await repository.AddAsync(failed);
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        var viewModel = main.TaskCenterViewModel;
        var window = new MainWindow { DataContext = main, Width = 1100, Height = 760 };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }

            main.OpenTaskCenterCommand.Execute(null);
            Layout();
            var view = window.GetVisualDescendants().OfType<TaskCenterView>().Single();
            if (Environment.GetEnvironmentVariable("HD2MIMNT_UI_SCREENSHOT") is { } screenshotPath)
                window.CaptureRenderedFrame()?.Save(screenshotPath);
            var clearButton = view.FindControl<Button>("ClearFinishedButton")!;
            Assert.True(clearButton.IsEffectivelyVisible);
            Assert.NotEqual("UI.ClearCompletedTasks", clearButton.Content?.ToString());
            Assert.Contains(finished.Id, viewModel.Tasks.Select(task => task.Id));

            // 已结束的任务才有删除按钮，运行中的任务只能取消。
            var deleteButtons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, viewModel.DeleteCommand)).ToList();
            var finishedDelete = Assert.Single(deleteButtons.Where(button => IdOf(button) == finished.Id));
            Assert.True(finishedDelete.IsEffectivelyVisible);
            var runningDelete = Assert.Single(deleteButtons.Where(button => IdOf(button) == running.Id));
            Assert.False(runningDelete.IsEffectivelyVisible);
            // 取消按钮只属于运行中的任务。
            var cancelButtons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, viewModel.CancelCommand)).ToList();
            Assert.Contains(cancelButtons, button => button.IsEffectivelyVisible && IdOf(button) == running.Id);
            Assert.DoesNotContain(cancelButtons, button => button.IsEffectivelyVisible &&
                (IdOf(button) == finished.Id || IdOf(button) == failed.Id));

            Click(clearButton);
            for (var attempt = 0; attempt < 200 && string.IsNullOrWhiteSpace(confirmation.Message); attempt++)
            {
                Layout();
                await Task.Delay(10);
            }
            Assert.False(string.IsNullOrWhiteSpace(confirmation.Message));
            for (var attempt = 0; attempt < 200 && viewModel.Tasks.Any(task => task.Id == finished.Id); attempt++)
                await Task.Delay(10);

            Assert.Null(await repository.GetByIdAsync(finished.Id, CancellationToken.None));
            Assert.NotNull(await repository.GetByIdAsync(running.Id, CancellationToken.None));
            Layout();
            Assert.False(clearButton.IsEffectivelyVisible);
            Assert.Contains(running.Id, viewModel.Tasks.Select(task => task.Id));
        }
        finally { window.DataContext = null; window.Close(); }

        static Guid? IdOf(Control control) => (control.DataContext as ModTask)?.Id;
    }
}
