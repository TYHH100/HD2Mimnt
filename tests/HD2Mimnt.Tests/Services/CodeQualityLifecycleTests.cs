using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class CodeQualityLifecycleTests
{
    [Fact]
    public async Task Connections_ShouldBeIndependentAndDisposingOneContextShouldNotCloseAnother()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mimnt-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var first = new HD2MimntDbContext(Path.Combine(root, "first.db"));
            using var other = new HD2MimntDbContext(Path.Combine(root, "other.db"));
            await first.InitializeDatabaseAsync();
            await other.InitializeDatabaseAsync();
            using var a = await first.OpenConnectionAsync();
            using var b = await first.OpenConnectionAsync();
            using var c = await other.OpenConnectionAsync();
            Assert.NotSame(a, b);
            a.Dispose();
            first.Dispose();
            using var query = c.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM Mods";
            Assert.Equal(0L, await query.ExecuteScalarAsync());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => first.OpenConnectionAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Repositories_ShouldSupportConcurrentWritesWithoutSharingConnections()
    {
        var path = Path.Combine(Path.GetTempPath(), "hd2mimnt-concurrent-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = new HD2MimntDbContext(path);
            await db.InitializeDatabaseAsync();
            var repository = new ModRepository(db);
            await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => repository.AddAsync(
                new Mod { Id = Guid.NewGuid(), Name = $"Mod {i}", Path = $"mod-{i}" }))));
            Assert.Equal(24, (await repository.GetAllAsync()).Count());
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Fact]
    public async Task ProfileQuery_ShouldReturnSnapshotWithoutReadingModsOrWritingNormalization()
    {
        var snapshot = new Profile { Id = Guid.NewGuid(), Name = "Unnormalized" };
        var profiles = new Mock<IProfileRepository>(MockBehavior.Strict);
        var mods = new Mock<IModRepository>(MockBehavior.Strict);
        profiles.Setup(p => p.GetByIdAsync(snapshot.Id, It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);
        var service = new ProfileService(profiles.Object, mods.Object);
        Assert.Same(snapshot, await service.GetProfileByIdAsync(snapshot.Id));
        profiles.Verify(p => p.UpdateAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>()), Times.Never);
        mods.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ProfileRename_ShouldWaitForOptionsWriteAndPreserveSavedOptions()
    {
        var id = Guid.NewGuid();
        var modId = Guid.NewGuid();
        var saved = new Profile { Id = id, Name = "Before", Mods = [new ProfileMod { ProfileId = id, ModId = modId }] };
        var profiles = new Mock<IProfileRepository>();
        profiles.Setup(p => p.GetByIdAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(() => new Profile
        {
            Id = id, Name = saved.Name,
            Mods = saved.Mods.Select(m => new ProfileMod { ProfileId = id, ModId = m.ModId, OptionsJson = m.OptionsJson }).ToList()
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profiles.Setup(p => p.UpdateAsync(It.IsAny<Profile>(), It.IsAny<CancellationToken>())).Returns(async (Profile p, CancellationToken _) =>
        {
            if (p.Name == "Before") { entered.TrySetResult(); await release.Task; }
            saved = p;
        });
        var service = new ProfileService(profiles.Object, Mock.Of<IModRepository>());
        var options = new ProfileModOptionsState { LegacySelectedIndex = 1 };
        var writing = service.SetModOptionsAsync(id, modId, options);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var renaming = service.RenameProfileAsync(id, "After");
        try { Assert.False(renaming.IsCompleted); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(writing, renaming);
        Assert.Equal("After", saved.Name);
        Assert.Equal(options.ToJson(), saved.Mods.Single().OptionsJson);
    }

    [Fact]
    public async Task CancelTask_ShouldWaitForWorkerCleanupAndFinalPersistence()
    {
        var worker = new CleanupWorker();
        var task = new ModTask { Id = Guid.NewGuid(), Type = worker.Type };
        var repository = new Mock<ITaskRepository>();
        repository.Setup(p => p.GetByIdAsync(task.Id, It.IsAny<CancellationToken>())).ReturnsAsync(task);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var service = new TaskService(repository.Object, provider);
        service.RegisterWorker(worker);
        await service.StartTaskAsync(task.Id);
        var cancel = service.CancelTaskAsync(task.Id);
        await worker.Cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.False(cancel.IsCompleted); }
        finally { worker.Release.TrySetResult(); }
        await cancel.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HD2Mimnt.Core.Entities.TaskStatus.Cancelled, task.Status);
        repository.Verify(p => p.UpdateAsync(It.Is<ModTask>(t => t.Status == HD2Mimnt.Core.Entities.TaskStatus.Cancelled), CancellationToken.None), Times.AtLeastOnce);
    }

    [Fact]
    public async Task RefreshQueue_ShouldSerializeOperationsAndDrainReentrantWork()
    {
        var queue = new AsyncOperationQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        _ = queue.Enqueue(async () => { order.Add(1); await release.Task; order.Add(2); _ = queue.Enqueue(() => { order.Add(4); return Task.CompletedTask; }); });
        _ = queue.Enqueue(() => { order.Add(3); return Task.CompletedTask; });
        var draining = queue.DrainAsync();
        try { Assert.False(draining.IsCompleted); Assert.Equal(new[] { 1 }, order); }
        finally { release.TrySetResult(); }
        await draining.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { 1, 2, 3, 4 }, order);
    }

    [Fact]
    public async Task BackupCommand_ShouldAwaitPageRefreshAndReportItsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), "hd2mimnt-restore-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var db = new HD2MimntDbContext(Path.Combine(root, "hd2mimnt.db"));
            await db.InitializeDatabaseAsync();
            var backups = new BackupService(root, db, new SettingsService(root));
            var backup = await backups.CreateBackupAsync();
            var confirmation = new Mock<IConfirmationService>();
            confirmation.Setup(c => c.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
            var vm = new BackupViewModel(backups, confirmation.Object, new NotificationService());
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.RefreshAfterRestoreAsync = async () => { entered.TrySetResult(); await release.Task; throw new InvalidOperationException("refresh failed"); };
            var restoring = vm.RestoreCommand.ExecuteAsync(backup);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try { Assert.False(restoring.IsCompleted); Assert.True(vm.IsLoading); }
            finally { release.TrySetResult(); }
            await restoring;
            Assert.Contains("备份已恢复，但页面刷新失败", vm.StatusMessage);
            Assert.Contains("refresh failed", vm.StatusMessage);
            Assert.False(vm.IsLoading);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class CleanupWorker : IWorker
    {
        public string Type => "CleanupControlled";
        public TaskCompletionSource Cleaning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(ModTask task, IProgress<TaskProgress> progress, CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { Cleaning.TrySetResult(); await Release.Task; }
        }
    }
}
