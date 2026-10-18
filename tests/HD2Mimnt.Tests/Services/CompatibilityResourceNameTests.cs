using System.Net;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using Moq;

namespace HD2Mimnt.Tests.Services;

public sealed class CompatibilityResourceNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HD2MimntNameTests", Guid.NewGuid().ToString("N"));
    // 与在线表一致：资源 ID 是十进制，归档 ID 是十六进制；有些 ID 超出 Int64。
    private const string Friendly = "15418518113916889238 Helmet A-9 Helljumper\n1 Unit One\n";
    private const string Archives = """{"Armor":{"1d6dc4216e7ce52d":"A-9 Helljumper","0000000000000001":"Archive One"},"Cape":{"476ad64c8b719fe":"Thundering Hooves of Loyalty"}}""";

    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public bool Fail;
        public bool Malformed;
        public bool Cancel;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Cancel) throw new OperationCanceledException(token);
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent(Malformed ? "<html>login</html>" : request.RequestUri!.AbsolutePath.EndsWith(".txt") ? Friendly : Archives) });
        }
    }

    [Fact]
    public async Task GetAsync_DownloadsOnceAndReloadsOffline_SeparatesResourceAndArchiveHashes()
    {
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var provider = new CompatibilityResourceNameProvider(client, _root);
        var first = await provider.GetAsync();
        Assert.Equal("Helmet A-9 Helljumper", first.Resources[15418518113916889238UL]);
        Assert.Equal("A-9 Helljumper", first.Archives[0x1d6dc4216e7ce52d]);
        Assert.Equal("Unit One", first.Resources[1]);
        Assert.Equal("Archive One", first.Archives[1]);
        Assert.Equal("Thundering Hooves of Loyalty", first.Archives[0x0476ad64c8b719fe]);
        Assert.Same(first, await provider.GetAsync());
        Assert.Equal(2, handler.Calls);
        handler.Fail = true;
        var reloaded = await new CompatibilityResourceNameProvider(client, _root).GetAsync();
        Assert.Equal(first.UpdatedAtUtc, reloaded.UpdatedAtUtc);
        Assert.Equal(2, handler.Calls);
        var fallback = await provider.GetAsync(true);
        Assert.NotNull(fallback.UpdateError);
        Assert.Equal(first.Resources, fallback.Resources);
        Assert.Equal(first.UpdatedAtUtc, fallback.UpdatedAtUtc);
        Assert.Equal(3, handler.Calls);
        await provider.GetAsync();
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task GetAsync_MalformedRefresh_PreservesPersistedSnapshot()
    {
        var handler = new Handler();
        using var client = new HttpClient(handler);
        var provider = new CompatibilityResourceNameProvider(client, _root);
        var first = await provider.GetAsync();
        handler.Malformed = true;
        var failed = await provider.GetAsync(true);
        Assert.NotNull(failed.UpdateError);
        Assert.Equal(first.UpdatedAtUtc, failed.UpdatedAtUtc);
        Assert.Equal("Unit One", (await new CompatibilityResourceNameProvider(client, _root).GetAsync()).Resources[1]);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task GetAsync_FirstOfflineAndPreCanceled_ReturnsErrorOrPropagatesCancellation()
    {
        var handler = new Handler { Fail = true };
        using var client = new HttpClient(handler);
        var provider = new CompatibilityResourceNameProvider(client, _root);
        var empty = await provider.GetAsync();
        Assert.NotNull(empty.UpdateError);
        Assert.Empty(empty.Resources);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetAsync(true, canceled.Token));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task GetAsync_Timeout_KeepsOfflineLookupAvailable()
    {
        var handler = new Handler { Cancel = true };
        using var client = new HttpClient(handler);
        var result = await new CompatibilityResourceNameProvider(client, _root).GetAsync();
        Assert.NotNull(result.UpdateError);
        Assert.Empty(result.Resources);
    }

    [Fact]
    public async Task CheckModsAsync_AppliesNamesOncePerBatch_LeavesCompatibilityUnchanged()
    {
        var path = CompatibilityFixture.WritePatch(_root, "1d6dc4216e7ce52d.patch_0", resources:
            [new(15418518113916889238UL, CompatibilityFixture.UnitType, CompatibilityFixture.Unit())]);
        var mod = new Mod { Id = Guid.NewGuid(), Path = _root };
        var repository = new Mock<IModRepository>();
        repository.Setup(r => r.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync([mod]);
        var names = new Mock<ICompatibilityResourceNames>();
        names.Setup(n => n.GetAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(new CompatibilityResourceNames
            { Resources = new Dictionary<ulong, string> { [15418518113916889238UL] = "Helmet A-9 Helljumper" },
                Archives = new Dictionary<ulong, string> { [0x1d6dc4216e7ce52d] = "A-9 Helljumper" } });
        var service = new ModCompatibilityService(repository.Object, new ModCompatibilityAnalyzer(),
            new ModService(repository.Object, Path.Combine(_root, "storage")), names: names.Object);
        var report = Assert.Single(await service.CheckModsAsync([mod.Id, mod.Id], CompatibilityFixture.CurrentVersion));
        Assert.Equal(ModCompatibilityStatus.Compatible, report.Status);
        Assert.Equal("A-9 Helljumper", report.Patches[0].ArchiveName);
        Assert.Equal("Helmet A-9 Helljumper", report.Units[0].ResourceName);
        names.Verify(n => n.GetAsync(false, It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(File.Exists(path));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
