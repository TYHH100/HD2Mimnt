using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Entities;
using HD2Mimnt.Core.Enums;
using HD2Mimnt.Core.Resources;
using HD2Mimnt.Infrastructure.Analysis;
using HD2Mimnt.Infrastructure.Data;
using HD2Mimnt.Infrastructure.Repositories;

namespace HD2Mimnt.Tests.Services;

public sealed class ResourceTypeClassificationTests : IDisposable
{
    private const ulong Texture = 0xcd4238c6a0c69e32;
    private const ulong Mesh = 0xe0a48d0be9a7453f;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"HD2Mimnt-ResourceTypes-{Guid.NewGuid():N}");

    public ResourceTypeClassificationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Catalog_ShouldKeepCommunityIdsAndNames()
    {
        Assert.Equal(47, ResourceTypeCatalog.All.Count);
        Assert.Equal(47, ResourceTypeCatalog.All.Select(type => type.Id).Distinct().Count());
        Assert.Equal("texture", ResourceTypeCatalog.All.Single(type => type.Id == Texture).Name);
        Assert.Equal("mesh", ResourceTypeCatalog.All.Single(type => type.Id == Mesh).Name);
        Assert.Equal("未知资源 0xFFFFFFFFFFFFFFFF", ResourceTypeCatalog.GetDisplayName(ulong.MaxValue));
    }

    [Fact]
    public void ReadTypeIds_ShouldReadEntriesAndPreserveUnknownUnsignedIds()
    {
        var path = Path.Combine(_root, "body.patch_0");
        WritePatch(path, Texture, Mesh, Texture, ulong.MaxValue);
        Assert.Equal(new[] { Texture, Mesh, ulong.MaxValue }, ResourceTypeScanner.ReadTypeIds(path));
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(uint.MaxValue, uint.MaxValue)]
    public void ReadTypeIds_ShouldRejectTruncatedTables(uint typeCount, uint fileCount)
    {
        var path = Path.Combine(_root, "broken.patch_0");
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(0xF0000011u);
            writer.Write(typeCount);
            writer.Write(fileCount);
            writer.Write(new byte[60]);
        }
        Assert.Throws<InvalidDataException>(() => ResourceTypeScanner.ReadTypeIds(path));
    }

    [Fact]
    public async Task Scan_ShouldIgnorePreviewImagesAndReadNestedPatchVariants()
    {
        var nested = Path.Combine(_root, "variant");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(_root, "preview.png"), "preview image");
        WritePatch(Path.Combine(nested, "body.patch_3"), Texture);
        WritePatch(Path.Combine(nested, "9ba626afa44a3aa3"), Mesh);
        WritePatch(Path.Combine(nested, "body.patch_3.gpu_resources"), ulong.MaxValue);
        Assert.Equal(new[] { Texture, Mesh }, await ResourceTypeScanner.ScanAsync(_root));
    }

    [Fact]
    public async Task Scan_ShouldHonorCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ResourceTypeScanner.ScanAsync(_root, cts.Token));
    }

    [Fact]
    public async Task Import_ShouldPersistHexIdsAndBackfillExistingModsOnce()
    {
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        WritePatch(Path.Combine(source, "body.patch_0"), Texture, Mesh, ulong.MaxValue);
        using var db = new HD2MimntDbContext(Path.Combine(_root, "mods.db"));
        await db.InitializeDatabaseAsync();
        var repository = new ModRepository(db);
        var import = new ModImportService(repository, Path.Combine(_root, "library"));
        var id = await import.ImportModAsync(source);
        var imported = (await repository.GetByIdAsync(id))!;
        Assert.Equal(new[] { Texture, Mesh, ulong.MaxValue }, imported.ResourceTypeIds);
        using (var sql = (await db.GetConnectionAsync()).CreateCommand())
        {
            sql.CommandText = "SELECT ResourceTypeIds FROM Mods;";
            Assert.Equal("[\"CD4238C6A0C69E32\",\"E0A48D0BE9A7453F\",\"FFFFFFFFFFFFFFFF\"]", await sql.ExecuteScalarAsync());
        }
        imported.ResourceTypeIds = null;
        imported.Tags.Add("user-tag");
        await repository.UpdateAsync(imported);
        var modifiedAt = imported.ModifiedAt;
        var service = new ModService(repository, Path.Combine(_root, "library"));
        var backfilled = (await service.GetModByIdAsync(id))!;
        Assert.Equal(new[] { Texture, Mesh, ulong.MaxValue }, backfilled.ResourceTypeIds);
        Assert.Equal(modifiedAt, backfilled.ModifiedAt);
        Assert.Contains("user-tag", backfilled.Tags);
        File.Delete(Path.Combine(imported.Path, "body.patch_0"));
        Assert.Equal(backfilled.ResourceTypeIds, (await service.GetModByIdAsync(id))!.ResourceTypeIds);
    }

    [Fact]
    public async Task Migration_ShouldKeepLegacyCategoryAndTagsAndAddNullableIds()
    {
        using var db = new HD2MimntDbContext(Path.Combine(_root, "legacy.db"));
        await db.InitializeDatabaseAsync();
        var repository = new ModRepository(db);
        var mod = new Mod { Id = Guid.NewGuid(), Name = "Legacy", Category = ModCategory.Armor, Tags = new() { "armor" } };
        await repository.AddAsync(mod);
        using (var sql = (await db.GetConnectionAsync()).CreateCommand())
        {
            sql.CommandText = "ALTER TABLE Mods DROP COLUMN ResourceTypeIds;";
            await sql.ExecuteNonQueryAsync();
        }
        await db.InitializeDatabaseAsync();
        await db.InitializeDatabaseAsync();
        var restored = (await repository.GetByIdAsync(mod.Id))!;
        Assert.Null(restored.ResourceTypeIds);
        Assert.Equal(ModCategory.Armor, restored.Category);
        Assert.Equal(mod.Tags, restored.Tags);
    }

    private static void WritePatch(string path, params ulong[] ids)
    {
        using var writer = new BinaryWriter(File.Create(path));
        var types = ids.Distinct().ToArray();
        writer.Write(0xF0000011u);
        writer.Write((uint)types.Length);
        writer.Write((uint)ids.Length);
        writer.Write(new byte[60]);
        foreach (var type in types)
        {
            writer.Write(0UL);
            writer.Write(type);
            writer.Write((ulong)ids.Count(id => id == type));
            writer.Write(16u);
            writer.Write(64u);
        }
        for (var index = 0; index < ids.Length; index++)
        {
            writer.Write((ulong)index + 1);
            writer.Write(ids[index]);
            writer.Write(new byte[64]);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
