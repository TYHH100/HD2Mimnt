using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.Services;

public class DeploymentCleanupProgressTests
{
    [Fact]
    public async Task Cleanup_ShouldReportSessionFileProgressAcrossModsWithoutFloodingCallbacks()
    {
        var session = Guid.NewGuid();
        var records = Enumerable.Range(0, 2).Select(_ => new DeploymentRecord
        {
            Id = Guid.NewGuid(), SessionId = session, DeploymentMethod = "Copy",
            Files = Enumerable.Range(0, 500).Select(_ => new DeploymentFile { Method = "Copy" }).ToList()
        }).ToList();
        var repository = new Mock<IDeploymentRepository>();
        repository.Setup(repo => repo.GetLatestSessionIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(session);
        repository.Setup(repo => repo.GetBySessionIdAsync(session, It.IsAny<CancellationToken>())).ReturnsAsync(records);
        foreach (var record in records)
        {
            repository.Setup(repo => repo.GetByIdAsync(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record);
            repository.Setup(repo => repo.GetDeploymentFilesAsync(record.Id, It.IsAny<CancellationToken>())).ReturnsAsync(record.Files);
        }
        var method = new Mock<IDeploymentMethod>();
        method.SetupGet(value => value.Name).Returns("Copy");
        method.Setup(value => value.CleanupFileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = new DeploymentService(Mock.Of<IProfileRepository>(), Mock.Of<IModRepository>(), repository.Object, [method.Object]);
        var updates = new List<DeploymentCleanupProgress>();
        var messages = new List<string>();

        var result = await service.PreDeploymentCleanupAsync(new InlineProgress<string>(messages.Add),
            fileProgress: new InlineProgress<DeploymentCleanupProgress>(updates.Add));

        Assert.Equal((1000, 2), result);
        Assert.Equal(new DeploymentCleanupProgress(0, 1000), updates.First());
        Assert.Equal(new DeploymentCleanupProgress(1000, 1000), updates.Last());
        Assert.All(updates, update => Assert.Equal(1000, update.TotalFiles));
        Assert.True(updates.Count < 100, $"Unexpected progress callback count: {updates.Count}");
        Assert.Equal(updates.OrderBy(update => update.ProcessedFiles), updates);
        Assert.Contains(session.ToString(), messages.Last());
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
