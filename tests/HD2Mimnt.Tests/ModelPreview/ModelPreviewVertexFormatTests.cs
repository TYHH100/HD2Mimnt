using System.Buffers.Binary;
using HD2Mimnt.Infrastructure.ModelPreview;
using HD2Mimnt.Tests.Services;

namespace HD2Mimnt.Tests.ModelPreview;

public sealed class ModelPreviewVertexFormatTests
{
    [Fact]
    public async Task Reader_Float4UvComponentDecodesFirstTwoChannels()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2Mimnt-Preview-Float4Uv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const int list = 0x88;
            const int info = list + 16;
            var unit = new byte[info + 0x1b0 + 8];
            CompatibilityFixture.Put32(unit, 0x2c, CompatibilityFixture.CurrentVersion);
            CompatibilityFixture.Put32(unit, 0x30, 0x78);
            CompatibilityFixture.Put32(unit, 0x34, (uint)list);
            CompatibilityFixture.Put32(unit, 0x5c, list);
            CompatibilityFixture.Put32(unit, 0x60, (uint)(unit.Length - 8));
            CompatibilityFixture.Put32(unit, list, 1);
            CompatibilityFixture.Put32(unit, list + 4, 16);
            CompatibilityFixture.Put64(unit, info + 0x148, 2);
            CompatibilityFixture.Put32(unit, info + 8, 0);
            CompatibilityFixture.Put32(unit, info + 12, 2);
            CompatibilityFixture.Put32(unit, info + 28, 4);
            CompatibilityFixture.Put32(unit, info + 32, 3);
            CompatibilityFixture.Put32(unit, info + 0x160, 1);
            CompatibilityFixture.Put32(unit, info + 0x164, 28);
            CompatibilityFixture.Put32(unit, info + 0x188, 3);
            CompatibilityFixture.Put32(unit, info + 0x1a4, 28);
            CompatibilityFixture.Put32(unit, info + 0x1a8, 28);
            CompatibilityFixture.Put32(unit, info + 0x1ac, 6);
            var gpu = new byte[34];
            BinaryPrimitives.WriteSingleLittleEndian(gpu.AsSpan(12, 4), 0.25f);
            BinaryPrimitives.WriteSingleLittleEndian(gpu.AsSpan(16, 4), 0.5f);
            var patch = CompatibilityFixture.WritePatch(root, resources: [new(1, CompatibilityFixture.UnitType, unit, gpu)]);

            var result = await new PatchResourceInspectionService().PreviewModelAsync(new DirectoryInfo(root), [new FileInfo(patch)]);

            Assert.Null(result.Error);
            var mesh = Assert.Single(result.Meshes);
            Assert.NotNull(mesh.TextureCoordinates);
            Assert.Equal([0.25f, 0.5f], mesh.TextureCoordinates);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
