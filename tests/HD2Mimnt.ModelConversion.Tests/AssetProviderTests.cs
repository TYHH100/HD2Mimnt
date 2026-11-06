using System.Text;
using HD2Mimnt.ModelConversion.Source;
using HD2Mimnt.ModelConversion.Source.Gma;

namespace HD2Mimnt.ModelConversion.Tests;

public sealed class AssetProviderTests
{
    public static TheoryData<string> UnsafePaths => new()
    {
        "", " ", "/", "/materials/test.vmt", @"\materials\test.vmt",
        "C:/materials/test.vmt", @"C:\materials\test.vmt", "C:materials/test.vmt",
        @"\\server\share\test.vmt", @"\\?\C:\test.vmt",
        "..", "../addon-neighbor/secret.vmt", "materials/../secret.vmt",
        @"materials\..\secret.vmt", ".", "./test.vmt", "materials/./test.vmt",
        "materials//test.vmt", @"materials\\test.vmt", "materials/",
        "materials/ /test.vmt", "materials/test.vmt.", "materials/test.vmt ",
        "materials/test:stream", "materials/te*st.vmt", "materials/te?st.vmt",
        "materials/te\"st.vmt", "materials/te<st.vmt", "materials/te>st.vmt",
        "materials/te|st.vmt", "materials/te\u0001st.vmt"
    };

    [Theory]
    [MemberData(nameof(UnsafePaths))]
    public void AddonPaths_UnsafePaths_AreRejectedByAllReaders(string unsafePath)
    {
        Assert.Throws<SourceFormatException>(() => GmaArchive.NormalizePath(unsafePath));
        WithTemporaryDirectory(root =>
        {
            using var directory = new DirectoryAssetProvider(root);
            Assert.Throws<SourceFormatException>(() => directory.TryRead(unsafePath, out _));

            var validPath = WriteArchive(root, "valid.gma", new Member("materials/test.vmt", [1]));
            using (var archive = new GmaArchive(validPath))
            {
                Assert.Throws<SourceFormatException>(() => archive.TryGetEntry(unsafePath, out _));
                Assert.Throws<SourceFormatException>(() => archive.Read(unsafePath));
            }

            using (var provider = new GmaAssetProvider(validPath))
            {
                Assert.Throws<SourceFormatException>(() => provider.TryRead(unsafePath, out _));
            }

            var invalidPath = WriteArchive(root, "invalid.gma", new Member(unsafePath, [1]));
            Assert.Throws<SourceFormatException>(() =>
            {
                using var archive = new GmaArchive(invalidPath);
            });
        });
    }

    [Fact]
    public void NormalizePath_NulCharacter_IsRejected()
    {
        Assert.Throws<SourceFormatException>(() => GmaArchive.NormalizePath("materials/a\0b.vmt"));
    }

    [Theory]
    [InlineData("materials/a..b.vmt", "materials/a..b.vmt")]
    [InlineData(@"materials\a..b.vmt", "materials/a..b.vmt")]
    [InlineData("materials/..name.vmt", "materials/..name.vmt")]
    public void NormalizePath_LegalDotNamesAndBackslashes_ArePreserved(string path, string expected)
    {
        Assert.Equal(expected, GmaArchive.NormalizePath(path));
    }

    [Fact]
    public void GmaProvider_ValidCrc_ReadsCaseInsensitivePathWithoutChangingInput()
    {
        WithTemporaryDirectory(root =>
        {
            var payload = "123456789"u8.ToArray();
            var path = WriteArchive(root, "addon.gma", new Member(@"materials\a..b.vmt", payload, 0xcbf43926));
            var original = File.ReadAllBytes(path);
            using var provider = new GmaAssetProvider(path);

            Assert.Equal((byte)3, provider.Archive.FormatVersion);
            Assert.Equal("materials/a..b.vmt", Assert.Single(provider.EnumeratePaths()));
            Assert.True(provider.TryRead(@"MATERIALS\A..B.VMT", out var result));
            Assert.Equal(payload, result);
            Assert.False(provider.TryRead("materials/missing.vmt", out var missing));
            Assert.Empty(missing);
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(Directory.EnumerateFiles(root));
            Assert.Empty(Directory.EnumerateDirectories(root));
        });
    }

    [Fact]
    public void GmaArchive_BadCrc_IsRejectedWhenReadingPayload()
    {
        WithTemporaryDirectory(root =>
        {
            var path = WriteArchive(root, "addon.gma", new Member("test.vmt", "123456789"u8.ToArray(), 1));
            using var archive = new GmaArchive(path);
            Assert.Contains("CRC mismatch", Assert.Throws<SourceFormatException>(() => archive.Read("test.vmt")).Message);
        });
    }

    [Fact]
    public void GmaArchive_ZeroCrcAndChunkedMembers_ReadExactPayloadsAndOffsets()
    {
        WithTemporaryDirectory(root =>
        {
            var payload = Enumerable.Range(0, 128 * 1024 + 7).Select(index => (byte)(index % 251)).ToArray();
            var path = WriteArchive(root, "addon.gma",
                new Member("large.bin", payload),
                new Member("empty.bin", []),
                new Member("last.bin", "123456789"u8.ToArray(), 0xcbf43926));
            using var archive = new GmaArchive(path);

            Assert.Equal(payload, archive.Read("large.bin"));
            Assert.Empty(archive.Read("empty.bin"));
            Assert.Equal("123456789"u8.ToArray(), archive.Read("last.bin"));
            Assert.Equal(payload, archive.Read(archive.Entries[0]));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(-1)]
    [InlineData(-4)]
    [InlineData(-8)]
    [InlineData(-12)]
    public void GmaArchive_TruncatedHeaderTableOrPayload_IsRejected(int length)
    {
        WithTemporaryDirectory(root =>
        {
            var bytes = CreateArchive(3, new Member("test.vmt", [1, 2, 3]));
            var path = Path.Combine(root, "truncated.gma");
            File.WriteAllBytes(path, bytes[..(length < 0 ? bytes.Length + length : length)]);
            Assert.Throws<SourceFormatException>(() =>
            {
                using var archive = new GmaArchive(path);
            });
        });
    }

    [Theory]
    [InlineData("materials/test.vmt", "MATERIALS/TEST.VMT")]
    [InlineData(@"materials\test.vmt", "materials/test.vmt")]
    public void GmaArchive_DuplicateNormalizedPaths_AreRejected(string first, string second)
    {
        WithTemporaryDirectory(root =>
        {
            var path = WriteArchive(root, "duplicate.gma", new Member(first, []), new Member(second, []));
            Assert.Contains("duplicate", Assert.Throws<SourceFormatException>(() =>
            {
                using var archive = new GmaArchive(path);
            }).Message);
        });
    }

    [Theory]
    [InlineData((byte)0)]
    [InlineData((byte)4)]
    [InlineData(byte.MaxValue)]
    public void GmaArchive_UnsupportedVersion_IsRejected(byte version)
    {
        WithTemporaryDirectory(root =>
        {
            var path = Path.Combine(root, "version.gma");
            File.WriteAllBytes(path, CreateArchive(version));
            Assert.Contains("version", Assert.Throws<SourceFormatException>(() =>
            {
                using var archive = new GmaArchive(path);
            }).Message);
        });
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MaxValue - 1)]
    [InlineData((long)int.MaxValue + 1)]
    public void GmaArchive_InvalidMemberSizeOrOffsetOverflow_IsRejectedBeforeAllocation(long size)
    {
        WithTemporaryDirectory(root =>
        {
            var path = WriteArchive(root, "oversized.gma", new Member("test.vmt", [1], DeclaredSize: size));
            Assert.Throws<SourceFormatException>(() =>
            {
                using var archive = new GmaArchive(path);
            });
        });
    }

    [Fact]
    public void GmaArchive_ForeignClonedOrForgedEntries_AreRejected()
    {
        WithTemporaryDirectory(root =>
        {
            var path = WriteArchive(root, "addon.gma", new Member("test.vmt", [1, 2, 3]));
            using var archive = new GmaArchive(path);
            using var other = new GmaArchive(path);
            var ownEntry = Assert.Single(archive.Entries);
            var foreignEntry = Assert.Single(other.Entries);

            Assert.Equal(ownEntry, foreignEntry);
            Assert.Throws<SourceFormatException>(() => archive.Read(foreignEntry));
            Assert.Throws<SourceFormatException>(() => archive.Read(ownEntry with { }));
            Assert.Throws<SourceFormatException>(() => archive.Read(ownEntry with { DataOffset = long.MaxValue, Size = long.MaxValue }));
            Assert.Throws<SourceFormatException>(() => archive.Read(ownEntry with { DataOffset = -1, Size = -1 }));
            Assert.Equal(new byte[] { 1, 2, 3 }, archive.Read(ownEntry));
        });
    }

    [Fact]
    public void DirectoryProvider_NeighborAndAbsolutePaths_AreRejectedWhileLegalNamesRead()
    {
        WithTemporaryDirectory(root =>
        {
            var addon = Directory.CreateDirectory(Path.Combine(root, "addon")).FullName;
            var neighbor = Directory.CreateDirectory(Path.Combine(root, "addon-neighbor")).FullName;
            var materials = Directory.CreateDirectory(Path.Combine(addon, "materials")).FullName;
            var payload = "material"u8.ToArray();
            File.WriteAllBytes(Path.Combine(materials, "a..b.vmt"), payload);
            var outside = Path.Combine(neighbor, "secret.vmt");
            File.WriteAllText(outside, "outside");
            using var provider = new DirectoryAssetProvider(addon);

            Assert.Throws<SourceFormatException>(() => provider.TryRead("../addon-neighbor/secret.vmt", out _));
            Assert.Throws<SourceFormatException>(() => provider.TryRead(outside, out _));
            Assert.Throws<SourceFormatException>(() => provider.TryRead(Path.Combine(materials, "a..b.vmt"), out _));
            Assert.True(provider.TryRead(@"materials\a..b.vmt", out var result));
            Assert.Equal(payload, result);
            if (OperatingSystem.IsWindows())
            {
                Assert.True(provider.TryRead("MATERIALS/A..B.VMT", out result));
                Assert.Equal(payload, result);
            }

            Assert.Equal("materials/a..b.vmt", Assert.Single(provider.EnumeratePaths()));
            Assert.False(provider.TryRead("materials", out _));
            Assert.False(provider.TryRead("missing/test.vmt", out var missing));
            Assert.Empty(missing);
        });
    }

    [Fact]
    public void Providers_PreCancelledOperations_ThrowCancellation()
    {
        WithTemporaryDirectory(root =>
        {
            var path = WriteArchive(root, "addon.gma", new Member("test.vmt", [1]));
            using var archive = new GmaArchive(path);
            using ISourceAssetProvider gma = new GmaAssetProvider(path);
            using ISourceAssetProvider directory = new DirectoryAssetProvider(root);
            using var source = new CancellationTokenSource();
            source.Cancel();
            var token = source.Token;

            Assert.Throws<OperationCanceledException>(() =>
            {
                using var cancelled = new GmaArchive(path, token);
            });
            Assert.Throws<OperationCanceledException>(() =>
            {
                using var cancelled = new GmaAssetProvider(path, token);
            });
            Assert.Throws<OperationCanceledException>(() =>
            {
                using var cancelled = new DirectoryAssetProvider(root, token);
            });
            Assert.Throws<OperationCanceledException>(() => archive.Read("test.vmt", token));
            Assert.Throws<OperationCanceledException>(() => archive.Read(archive.Entries[0], token));
            Assert.Throws<OperationCanceledException>(() => gma.TryRead("test.vmt", out _, token));
            Assert.Throws<OperationCanceledException>(() => directory.TryRead("addon.gma", out _, token));
            Assert.Throws<OperationCanceledException>(() => gma.EnumeratePaths(token).ToArray());
            Assert.Throws<OperationCanceledException>(() => directory.EnumeratePaths(token).ToArray());
            Assert.Throws<OperationCanceledException>(() => Crc32.Compute(new byte[128 * 1024], token));
            Assert.Throws<OperationCanceledException>(() => Crc32.Compute(Array.Empty<byte>(), token));
        });
    }

    [Fact]
    public void Providers_CancelDuringEnumeration_StopBeforeNextMember()
    {
        WithTemporaryDirectory(root =>
        {
            var path = WriteArchive(root, "addon.gma", new Member("one.vmt", []), new Member("two.vmt", []));
            File.WriteAllText(Path.Combine(root, "another.vmt"), "material");
            using var gma = new GmaAssetProvider(path);
            using var directory = new DirectoryAssetProvider(root);
            AssertEnumerationCancellation(gma);
            AssertEnumerationCancellation(directory);
        });
    }

    [Fact]
    public void Crc32_KnownVectorAndEmptyPayload_MatchIeeeValues()
    {
        Assert.Equal(0xcbf43926u, Crc32.Compute("123456789"u8));
        Assert.Equal(0u, Crc32.Compute([]));
    }

    private static void AssertEnumerationCancellation(ISourceAssetProvider provider)
    {
        using var source = new CancellationTokenSource();
        using var enumerator = provider.EnumeratePaths(source.Token).GetEnumerator();
        Assert.True(enumerator.MoveNext());
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => enumerator.MoveNext());
    }

    private static string WriteArchive(string root, string name, params Member[] members)
    {
        var path = Path.Combine(root, name);
        File.WriteAllBytes(path, CreateArchive(3, members));
        return path;
    }

    private static byte[] CreateArchive(byte version, params Member[] members)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("GMAD"u8);
        writer.Write(version);
        writer.Write(0UL);
        writer.Write(0UL);
        if (version > 1)
        {
            writer.Write((byte)0);
        }

        WriteCString(writer, "synthetic addon");
        WriteCString(writer, "{}");
        WriteCString(writer, "test author");
        writer.Write(1);
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            writer.Write(index + 1);
            WriteCString(writer, member.Path);
            writer.Write(member.DeclaredSize ?? member.Payload.LongLength);
            writer.Write(member.Crc);
        }

        writer.Write(0);
        foreach (var member in members)
        {
            writer.Write(member.Payload);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteCString(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    private static void WithTemporaryDirectory(Action<string> action)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"HD2Mimnt-AssetProviderTests-{Guid.NewGuid():N}")).FullName;
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Member(string Path, byte[] Payload, uint Crc = 0, long? DeclaredSize = null);
}
