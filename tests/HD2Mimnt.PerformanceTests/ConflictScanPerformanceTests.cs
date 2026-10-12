using System.Diagnostics;
using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.UI.ViewModels;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace HD2Mimnt.PerformanceTests;

public sealed class ConflictScanPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(5000, 200)]
    public async Task ScanLargeProfile_FirstAndRepeatedPass(int modCount, int resourcesPerMod)
    {
        var root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-ConflictPerf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var profile = new Profile { Id = Guid.NewGuid(), Name = "性能样本" };
            var mods = new List<Mod>();
            for (var index = 0; index < modCount; index++)
            {
                var mod = new Mod { Id = Guid.NewGuid(), Name = $"HD2Mimnt Test Perf {index}", Path = Path.Combine(root, index.ToString()),
                    BslDetectionVersion = BslModDetector.DetectionVersion };
                Directory.CreateDirectory(mod.Path);
                using var writer = new BinaryWriter(File.Create(Path.Combine(mod.Path, "aaaaaaaaaaaaaaaa.patch_0")));
                writer.Write(0xF0000011u); writer.Write(1u); writer.Write((uint)resourcesPerMod);
                writer.Write(new byte[60 + 32]);
                var entry = new byte[80];
                for (var resource = 0; resource < resourcesPerMod; resource++)
                {
                    // 每对模组共享 10% 的索引，其余唯一；数据正文为空，只测 TOC 和聚合。
                    var id = resource < resourcesPerMod / 10
                        ? (ulong)(index / 2 * resourcesPerMod + resource)
                        : (1UL << 63) + (ulong)(index * resourcesPerMod + resource);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry, id);
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(entry.AsSpan(8), 0xcd4238c6a0c69e32UL);
                    writer.Write(entry);
                }
                mods.Add(mod);
                profile.Mods.Add(new ProfileMod { ModId = mod.Id, Enabled = true, Order = index });
            }
            var profiles = new Mock<IProfileRepository>();
            profiles.Setup(repo => repo.GetByIdAsync(profile.Id, It.IsAny<CancellationToken>())).ReturnsAsync(profile);
            var repository = new Mock<IModRepository>();
            repository.Setup(repo => repo.GetByIdsAsync(It.IsAny<IEnumerable<Guid>>(), It.IsAny<CancellationToken>())).ReturnsAsync(mods);
            var scanner = new ConflictDetectionService(profiles.Object, repository.Object);
            // 预热 JIT；大样本的第一次扫描仍包含每个文件的首次解析（不代表物理磁盘冷缓存）。
            await new ConflictDetectionService(profiles.Object, repository.Object).ScanProfileAsync(profile.Id);
            for (var pass = 0; pass < 2; pass++)
            {
                GC.Collect();
                var allocated = GC.GetTotalAllocatedBytes(true);
                var timer = Stopwatch.StartNew();
                var result = await scanner.ScanProfileAsync(profile.Id);
                timer.Stop();
                var megabytes = (GC.GetTotalAllocatedBytes(true) - allocated) / 1048576d;
                var presentation = Stopwatch.StartNew();
                var views = result.Groups.Select(group => new ConflictGroupViewModel(group)).ToArray();
                var cards = ConflictCardIndex.Create(views);
                presentation.Stop();
                Assert.Equal(modCount * resourcesPerMod, result.ScannedResourceCount);
                Assert.Equal(modCount / 2 * (resourcesPerMod / 10), views.Length);
                Assert.Empty(result.Warnings);
                Assert.True(cards.Contains(mods[0].Id));
                output.WriteLine($"mods={modCount}, resources={result.ScannedResourceCount}, pass={pass + 1}, cached={result.CachedPatchCount}, scan_ms={timer.Elapsed.TotalMilliseconds:F1}, allocated_MiB={megabytes:F1}, presentation_ms={presentation.Elapsed.TotalMilliseconds:F1}");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
