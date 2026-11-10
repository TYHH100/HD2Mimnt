using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Core.IPC;
using HD2Mimnt.UI;
using HD2Mimnt.UI.ViewModels;
using HD2Mimnt.UI.Views;
using HD2Mimnt.Worker;
using Microsoft.Extensions.DependencyInjection;

namespace HD2Mimnt.UITests;

public sealed partial class FluentAvaloniaUiTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupDeployment_ShouldReportProgressAndKeepPagesResponsive(bool closeWhileCleaning)
    {
        using var method = new PausedCleanupMethod();
        await using var scope = await UiScope.CreateAsync(seedMods: true,
            confirmation: new RecordingConfirmation(), deploymentMethod: method,
            workerHostManagerFactory: services => new LoopbackWorkerHost(services));
        var main = scope.Services.GetRequiredService<MainWindowViewModel>();
        await main.InitializeAsync();
        var mod = (await scope.Services.GetRequiredService<ModService>().GetAllModsAsync()).First();
        var record = new DeploymentRecord
        {
            Id = Guid.NewGuid(), SessionId = Guid.NewGuid(), ModId = mod.Id,
            ProfileId = main.ProfileViewModel.SelectedProfile!.Id,
            DeploymentMethod = "Copy", StartTime = DateTime.UtcNow, Status = "Completed"
        };
        var targets = Path.Combine(scope.Root, "cleanup-targets");
        Directory.CreateDirectory(targets);
        for (var index = 0; index < 1024; index++)
        {
            var path = Path.Combine(targets, $"file-{index}");
            File.WriteAllText(path, "test");
            record.Files.Add(new DeploymentFile
            {
                Id = Guid.NewGuid(), DeploymentId = record.Id, ModId = mod.Id,
                SourcePath = path, TargetPath = path, Method = "Copy"
            });
        }
        var repository = scope.Services.GetRequiredService<IDeploymentRepository>();
        await repository.AddAsync(record);
        var window = new MainWindow { DataContext = main };
        try
        {
            window.Show();
            void Layout()
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                using var frame = window.CaptureRenderedFrame();
            }
            void Click(Control control)
            {
                Layout();
                var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
                Assert.True(control.IsVisualAncestorOf((Visual)window.InputHitTest(point)!) || ReferenceEquals(control, window.InputHitTest(point)));
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }
            Layout();
            Click(window.GetVisualDescendants().OfType<ProfileView>().Single().FindControl<Button>("CleanupDeploymentButton")!);
            await method.FirstFile.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 进度经 TaskService 的观察者异步回传，等首条文件进度到达 UI 再断言。
            for (var attempt = 0; attempt < 200 && main.LibraryViewModel.IsDeploymentIndeterminate; attempt++)
            {
                Layout();
                await Task.Delay(10);
            }
            Layout();
            Assert.False(method.RanOnUiThread);
            var surface = window.FindControl<Border>("DeploymentOperationSurface")!;
            var progress = window.FindControl<ProgressBar>("DeploymentProgressBar")!;
            Assert.True(surface.IsEffectivelyVisible);
            Assert.False(progress.IsIndeterminate);
            Assert.Equal(560, surface.Bounds.Width);
            var center = surface.TranslatePoint(new Point(surface.Bounds.Width / 2, surface.Bounds.Height / 2), window)!.Value;
            Assert.InRange(Math.Abs(center.X - window.Bounds.Width / 2), 0, 1);
            Assert.InRange(Math.Abs(center.Y - window.Bounds.Height / 2), 0, 1);
            // 收起与导入同样的居中浮窗，后台清理仍保持运行。
            Click(window.FindControl<Button>("CloseDeploymentProgressButton")!); Layout();
            Assert.False(surface.IsEffectivelyVisible);
            Assert.True(main.LibraryViewModel.IsDeploying);
            Assert.True(main.LibraryViewModel.CleanupCommand.IsRunning);
            // 清理阻塞在真实同步文件操作的位置时，页面切换和搜索输入仍然可用。
            main.NavigateToLibraryCommand.Execute(null); Layout();
            var search = window.GetVisualDescendants().OfType<TextBox>().First(box => box.IsEffectivelyVisible);
            Click(search); window.KeyTextInput("clean");
            Assert.Contains("clean", search.Text);
            Assert.False(surface.IsEffectivelyVisible);
            main.NavigateToSettingsCommand.Execute(null); Layout();
            Assert.Equal(4, main.SelectedTabIndex);
            Click(window.FindControl<Button>("OpenDeploymentProgressButton")!); Layout();
            Assert.True(surface.IsEffectivelyVisible);
            Assert.True(main.LibraryViewModel.CleanupCommand.IsRunning);
            await Task.Delay(150);
            method.ContinueFirst.Set();
            await method.SecondBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var attempt = 0; attempt < 100 && progress.Value == 0; attempt++) await Task.Delay(10);
            Layout();
            Assert.InRange(progress.Value, 0.01, 99.99);
            Assert.Contains("1,024", window.FindControl<TextBlock>("DeploymentFileCount")!.Text);
            Task completion;
            if (closeWhileCleaning)
                completion = main.CanCloseAsync();
            else
            {
                Click(window.FindControl<Button>("CancelDeploymentButton")!);
                completion = main.LibraryViewModel.CleanupCommand.ExecutionTask!;
            }
            Assert.False(completion.IsCompleted);
            method.ContinueSecond.Set();
            await completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(main.LibraryViewModel.IsDeploying);
            Layout();
            Assert.False(surface.IsEffectivelyVisible);
            Assert.NotNull(await repository.GetByIdAsync(record.Id));
            Assert.NotEmpty(Directory.GetFiles(targets));
            Assert.Contains(scope.Services.GetRequiredService<NotificationService>().GetRecentNotifications(),
                notification => notification.Message.Contains("清理部署已取消"));
            // 取消后沿用同一追踪记录重试，剩余目标能够全部清理。
            main.LibraryViewModel.SearchText = string.Empty;
            await main.LibraryViewModel.CleanupCommand.ExecuteAsync(null);
            Assert.Empty(Directory.GetFiles(targets));
            Assert.Null(await repository.GetByIdAsync(record.Id));
        }
        finally
        {
            method.ContinueFirst.Set(); method.ContinueSecond.Set();
            await main.LibraryViewModel.CancelDeploymentAndWaitAsync();
            window.DataContext = null; window.Close();
        }
    }

    private sealed class PausedCleanupMethod : IDeploymentMethod, IDisposable
    {
        public string Name => "Copy";
        public bool RanOnUiThread { get; private set; }
        private int _files;
        public TaskCompletionSource FirstFile { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ContinueFirst { get; } = new();
        public ManualResetEventSlim ContinueSecond { get; } = new();
        public Task<(bool CanDeploy, string? ErrorMessage)> CheckCompatibilityAsync(string sourceDirectory,
            string targetDirectory, CancellationToken cancellationToken = default) => Task.FromResult((true, (string?)null));
        public Task<bool> DeployFileAsync(string sourcePath, string targetPath, IProgress<string>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsUpToDateAsync(string sourcePath, string targetPath,
            CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> CleanupFileAsync(string targetPath, CancellationToken cancellationToken = default)
        {
            RanOnUiThread |= Dispatcher.UIThread.CheckAccess();
            _files++;
            if (_files == 1)
            {
                FirstFile.SetResult();
                if (!ContinueFirst.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("UI did not release cleanup");
            }
            if (_files == 129)
            {
                SecondBatch.SetResult();
                if (!ContinueSecond.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("UI did not cancel cleanup");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(targetPath);
            return Task.FromResult(true);
        }
        public void Dispose() { ContinueFirst.Dispose(); ContinueSecond.Dispose(); }
    }

    /// <summary>
    /// 回环宿主：不起子进程，在本进程直接执行 Worker 任务实现。
    /// 让 UI 测试能注入阻塞式部署方法来验证浮层与取消，同时仍经过
    /// TaskService → Worker 任务 → DeploymentService 的真实链路（只有管道传输被替换）。
    /// <paramref name="importModServiceFactory"/> 用于导入任务注入独立服务图，
    /// 模拟子进程自己的 ModService/ProfileService：主进程收不到它们的内存事件。
    /// </summary>
    private sealed class LoopbackWorkerHost(IServiceProvider services,
        Func<IServiceProvider, ModService>? importModServiceFactory = null) : IWorkerHostManager
    {
        private sealed record Run(CancellationTokenSource Cancellation, Task<WorkerResult> Completion);

        private readonly ConcurrentDictionary<Guid, Run> _runs = new();

        public Task<Guid> StartWorkerAsync(string workerType, Dictionary<string, string> arguments,
            IProgress<WorkerTaskProgress>? progress = null, CancellationToken cancellationToken = default,
            Guid? taskId = null, WorkerPromptHandler? promptHandler = null)
        {
            var id = taskId ?? Guid.NewGuid();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var host = new LoopbackTaskHost(id, cts, progress);
            _ = Task.Run(async () =>
            {
                try
                {
                    IWorkerTask task = workerType switch
                    {
                        "Deployment" => new DeploymentWorkerTask(services.GetRequiredService<DeploymentService>()),
                        "Cleanup" => new CleanupWorkerTask(services.GetRequiredService<DeploymentService>()),
                        "Import" => new ImportWorkerTask(importModServiceFactory?.Invoke(services)
                            ?? services.GetRequiredService<ModService>()),
                        _ => throw new NotSupportedException(workerType)
                    };
                    await task.ExecuteAsync(host,
                        new ExecuteWorkerCommand { WorkerType = workerType, Arguments = arguments }, cts.Token);
                }
                catch (OperationCanceledException) { host.MarkCancelled(); }
                catch (Exception ex) { host.MarkError(ex); }
            }, CancellationToken.None);
            _runs[id] = new Run(cts, host.Completion);
            return Task.FromResult(id);
        }

        public Task<bool> IsWorkerRunningAsync(Guid taskId)
            => Task.FromResult(_runs.TryGetValue(taskId, out var run) && !run.Completion.IsCompleted);

        public async Task CancelWorkerAsync(Guid taskId)
        {
            if (!_runs.TryGetValue(taskId, out var run)) return;
            run.Cancellation.Cancel();
            try { await run.Completion.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { }
        }

        public Task ForceTerminateWorkerAsync(Guid taskId)
        {
            if (_runs.TryGetValue(taskId, out var run)) run.Cancellation.Cancel();
            return Task.CompletedTask;
        }

        public async Task<WorkerResult> WaitForCompletionAsync(Guid taskId, CancellationToken cancellationToken = default)
            => _runs.TryGetValue(taskId, out var run)
                ? await run.Completion.WaitAsync(cancellationToken)
                : throw new InvalidOperationException($"Worker {taskId} not found");
    }

    private sealed class LoopbackTaskHost(Guid taskId, CancellationTokenSource cancellation,
        IProgress<WorkerTaskProgress>? progress) : IWorkerTaskHost
    {
        private readonly TaskCompletionSource<WorkerResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<WorkerResult> Completion => _completion.Task;
        public Guid TaskId { get; } = taskId;
        public CancellationToken CancellationRequested => cancellation.Token;

        public Task SendProgressAsync(double progressValue, string stage, string? currentItem = null,
            string? message = null, CancellationToken cancellationToken = default)
        {
            progress?.Report(new WorkerTaskProgress
            {
                Progress = progressValue,
                Stage = stage,
                CurrentItem = currentItem ?? string.Empty,
                Message = message ?? string.Empty
            });
            return Task.CompletedTask;
        }

        public Task SendCompletedAsync(bool success, string? result = null, List<string>? errors = null,
            bool partiallyFailed = false, CancellationToken cancellationToken = default, string? data = null)
        {
            _completion.TrySetResult(new WorkerResult
            {
                Success = success,
                PartiallyFailed = partiallyFailed,
                Result = result,
                Data = data,
                Errors = errors ?? new List<string>()
            });
            return Task.CompletedTask;
        }

        public Task SendCancelledAsync(CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult(new WorkerResult { Cancelled = true });
            return Task.CompletedTask;
        }

        public Task SendErrorAsync(Exception exception, CancellationToken cancellationToken = default)
        {
            _completion.TrySetResult(new WorkerResult { Errors = new List<string> { exception.Message } });
            return Task.CompletedTask;
        }

        public Task<WorkerPromptResponse> PromptAsync(string kind, string? title = null, string? payload = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkerPromptResponse
            {
                RequestId = Guid.NewGuid().ToString("N"),
                Accepted = false
            });

        public void MarkCancelled() => _completion.TrySetResult(new WorkerResult { Cancelled = true });

        public void MarkError(Exception exception)
            => _completion.TrySetResult(new WorkerResult { Errors = new List<string> { exception.Message } });
    }
}
