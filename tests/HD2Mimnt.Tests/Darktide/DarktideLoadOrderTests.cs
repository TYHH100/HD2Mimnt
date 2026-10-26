using System.Text;
using HD2Mimnt.Infrastructure.Darktide;

namespace HD2Mimnt.Tests.Darktide;

public sealed class DarktideLoadOrderTests
{
    [Fact]
    public void Load_NoChanges_SavePreservesBytesAndDoesNotCreateBackup()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        var content = "-- header\r\n\r\nAlpha\r\n-- comment\r\nBeta\r\n";
        File.WriteAllText(path, content, new UTF8Encoding(false));
        var before = File.ReadAllBytes(path);

        var document = DarktideLoadOrderDocument.Load(path);
        var result = document.Save(Path.Combine(env.Root, "backups"));

        Assert.False(result.Changed);
        Assert.Null(result.BackupPath);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Path.Combine(env.Root, "backups")));
    }

    [Fact]
    public void Enable_UncommentsExistingLine_AndPreservesOtherContent()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "-- header\r\n\r\n-- Alpha\r\n-- user note\r\n", new UTF8Encoding(false));
        var document = DarktideLoadOrderDocument.Load(path);

        Assert.True(document.Enable("Alpha"));
        document.Save(Path.Combine(env.Root, "backups"));

        var lines = File.ReadAllLines(path);
        Assert.Contains("Alpha", lines);
        Assert.DoesNotContain("-- Alpha", lines);
        Assert.Contains("-- header", lines);
        Assert.Contains("-- user note", lines);
    }

    [Fact]
    public void Disable_CommentsAllDuplicateRegistrations_AndKeepsComments()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "-- header\r\nAlpha\r\nBeta\r\nAlpha\r\n-- footer\r\n", new UTF8Encoding(false));
        var document = DarktideLoadOrderDocument.Load(path);

        Assert.Equal(2, document.Disable("Alpha"));
        document.Save();

        var document2 = DarktideLoadOrderDocument.Load(path);
        Assert.False(document2.IsEnabled("Alpha"));
        Assert.True(document2.IsEnabled("Beta"));
        Assert.Equal(4, document2.Lines.Count(line => line.IsComment));
    }

    [Fact]
    public void ReorderActive_OnlyRewritesActiveEntryText()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "-- header\r\nAlpha\r\n-- between\r\nBeta\r\n\r\n", new UTF8Encoding(false));
        var document = DarktideLoadOrderDocument.Load(path);

        document.ReorderActive(new[] { "Beta", "Alpha" });
        document.Save();

        var lines = File.ReadAllLines(path);
        Assert.Equal("Beta", lines[1]);
        Assert.Equal("-- between", lines[2]);
        Assert.Equal("Alpha", lines[3]);
        Assert.Equal("-- header", lines[0]);
    }

    [Fact]
    public void Save_PreservesUtf8BomAndCrLf()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "-- header\r\nAlpha\r\n", new UTF8Encoding(true));
        var document = DarktideLoadOrderDocument.Load(path);
        document.Enable("Beta");
        document.Save();

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.Contains("Alpha\r\n", text);
        Assert.EndsWith("Beta\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    [Fact]
    public void Save_PreservesUtf16Encoding()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "-- header\r\nAlpha\r\n", Encoding.Unicode);
        var document = DarktideLoadOrderDocument.Load(path);
        document.Enable("Beta");
        document.Save();

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
        var text = Encoding.Unicode.GetString(bytes[2..]);
        Assert.Contains("Alpha", text);
        Assert.Contains("Beta", text);
    }

    [Fact]
    public void Load_InvalidUtf8_ThrowsWithoutWriting()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllBytes(path, new byte[] { 0x80, 0x81, 0x82 });
        var before = File.ReadAllBytes(path);

        Assert.Throws<InvalidDataException>(() => DarktideLoadOrderDocument.Load(path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void Save_ReadOnlyFile_ThrowsAndKeepsOriginalContent()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "Alpha\r\n");
        var document = DarktideLoadOrderDocument.Load(path);
        document.Enable("Beta");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var before = File.ReadAllBytes(path);
            Assert.Throws<UnauthorizedAccessException>(() => document.Save());
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Save_CreatesBackupInConfiguredDirectory()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        var backupDir = Path.Combine(env.Root, "backups");
        File.WriteAllText(path, "Alpha\r\n");
        var document = DarktideLoadOrderDocument.Load(path);
        document.Enable("Beta");
        var result = document.Save(backupDir);

        Assert.True(result.Changed);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.StartsWith(backupDir, Path.GetFullPath(result.BackupPath!));
    }

    [Fact]
    public void SetActiveOrder_DisablesUnlistedAndKeepsTheirText()
    {
        using var env = new DarktideTestEnvironment();
        var path = Path.Combine(env.Root, "mod_load_order.txt");
        File.WriteAllText(path, "-- header\r\nAlpha\r\nBeta\r\nUnknown\r\n-- footer\r\n");
        var document = DarktideLoadOrderDocument.Load(path);

        document.SetActiveOrder(new[] { "Beta", "Alpha" });
        document.Save();

        var loaded = DarktideLoadOrderDocument.Load(path);
        Assert.Equal(new[] { "Beta", "Alpha" }, loaded.ActiveNames);
        Assert.Contains("-- Unknown", File.ReadAllLines(path));
        Assert.Contains("-- footer", File.ReadAllLines(path));
    }
}
