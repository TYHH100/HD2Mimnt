using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using HD2Mimnt.Infrastructure.Hd2IAS.Companion;

namespace HD2Mimnt.Tests.Services;

public sealed class IasCompanionApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hd2mimnt-ias-applier-{Guid.NewGuid():N}");
    private string GamePath => Path.Combine(_root, "game");
    private string RuntimePath => Path.Combine(_root, "runtime", "abc123");
    private string ArmorPath => Path.Combine(RuntimePath, "armor");
    private string BackupRoot => Path.Combine(_root, "backups");

    public IasCompanionApplierTests() => Directory.CreateDirectory(GamePath);

    [Fact]
    public async Task Apply_WritesNewFiles_DeletesRemoved_UpdatesReceipt()
    {
        var receipt = WriteReceipt(("old.rigbin", "old", preserve: false), ("kept.dll", "kept", preserve: false));
        var package = WritePackage("2.0.0", ("new.rigbin", "new"), ("kept.dll", "kept"));

        var (written, deleted, _, transactionPath) = await ApplyAsync(package, ["kept.dll"]);

        Assert.Equal(1, written);
        Assert.Equal(1, deleted);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(ArmorPath, "new.rigbin")));
        Assert.Equal("kept", await File.ReadAllTextAsync(Path.Combine(ArmorPath, "kept.dll")));
        Assert.False(File.Exists(Path.Combine(ArmorPath, "old.rigbin")));

        var updated = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RuntimePath, "deployment.json")))!.AsObject();
        var armor = IasCompanionApplier.FindArmorComponent(updated);
        Assert.NotNull(armor);
        var paths = armor!["files"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()).ToList();
        Assert.Equal(["kept.dll", "new.rigbin"], paths);
        Assert.Equal("2.0.0", armor["version"]!.GetValue<string>());

        // 事务记录与上游 HD2ExternalTransaction1 兼容且已标记 installed。
        var transaction = JsonNode.Parse(await File.ReadAllTextAsync(transactionPath))!.AsObject();
        Assert.Equal("HD2ExternalTransaction1", transaction["schema"]!.GetValue<string>());
        Assert.Equal("installed", transaction["status"]!.GetValue<string>());
        Assert.True(transaction["runtime_only"]!.GetValue<bool>());
        Assert.Equal(3, transaction["files"]!.AsArray().Count);
        // 被覆盖/删除的旧文件都有 .original 备份（新增文件的 before 为空，不备份）。
        var backupDir = Path.GetDirectoryName(transactionPath)!;
        Assert.True(File.Exists(Path.Combine(backupDir, "0.original")));
        Assert.True(File.Exists(Path.Combine(backupDir, "2.original")));
        Assert.False(File.Exists(Path.Combine(backupDir, "1.original")));
    }

    [Fact]
    public async Task Apply_PreservesPersonalAndRetainedFiles()
    {
        WriteReceipt(("physics.json", "personal", preserve: true), ("bridge.dll", "retained", preserve: false),
            ("stale.rigbin", "stale", preserve: false));
        var package = WritePackage("2.0.0", ("fresh.rigbin", "fresh"));

        var result = await ApplyAsync(package, ["bridge.dll", "bootstrap/boot.patch_0"]);

        Assert.Equal("personal", await File.ReadAllTextAsync(Path.Combine(ArmorPath, "physics.json")));
        Assert.Equal("retained", await File.ReadAllTextAsync(Path.Combine(ArmorPath, "bridge.dll")));
        Assert.False(File.Exists(Path.Combine(ArmorPath, "stale.rigbin")));

        var updated = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RuntimePath, "deployment.json")))!.AsObject();
        var armor = IasCompanionApplier.FindArmorComponent(updated)!;
        var paths = armor["files"]!.AsArray().Select(node => node!["path"]!.GetValue<string>()).ToList();
        Assert.Equal(["bridge.dll", "fresh.rigbin"], paths);
        Assert.True(result.Preserved >= 1);
    }

    [Fact]
    public async Task Apply_RollsBackOnFailure()
    {
        WriteReceipt(("old.rigbin", "old", preserve: false));
        // 目标 b.rigbin 被目录占用 → 写入阶段失败并自动回滚。
        var package = WritePackage("2.0.0", ("a.rigbin", "aaa"), ("b.rigbin", "bbb"));
        Directory.CreateDirectory(Path.Combine(ArmorPath, "b.rigbin"));

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => ApplyAsync(package, []));
        // 回滚后旧内容与 receipt 全部恢复。
        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(ArmorPath, "old.rigbin")));
        Assert.False(File.Exists(Path.Combine(ArmorPath, "a.rigbin")));
        var updated = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RuntimePath, "deployment.json")))!.AsObject();
        var armor = IasCompanionApplier.FindArmorComponent(updated)!;
        Assert.Equal(["old.rigbin"], armor["files"]!.AsArray()
            .Select(node => node!["path"]!.GetValue<string>()).ToList());
        var transaction = Directory.GetFiles(BackupRoot, "external-runtime-transaction.json", SearchOption.AllDirectories)
            .Select(path => JsonNode.Parse(File.ReadAllText(path))!.AsObject()).Single();
        Assert.Equal("restored", transaction["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Apply_DeletesObsoleteDirectWriteFiles_NotManagedByUpstream()
    {
        WriteReceipt();
        var rigFile = Path.Combine(ArmorPath, "rig", "leftover.rigbin");
        Directory.CreateDirectory(Path.GetDirectoryName(rigFile)!);
        await File.WriteAllTextAsync(rigFile, "leftover");
        var package = WritePackage("2.0.0", ("fresh.rigbin", "fresh"));

        await ApplyAsync(package, [], obsoleteFiles: [rigFile]);

        Assert.False(File.Exists(rigFile));
        var armor = IasCompanionApplier.FindArmorComponent(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(RuntimePath, "deployment.json")))!.AsObject())!;
        Assert.DoesNotContain(armor["files"]!.AsArray()
            .Select(node => node!["path"]!.GetValue<string>()), path => path.Contains("leftover"));
    }

    [Fact]
    public async Task Apply_SkipsEverythingUpToDate_WithoutTransaction()
    {
        WriteReceipt(("fresh.rigbin", "fresh", preserve: false));
        var package = WritePackage("1.0.0", ("fresh.rigbin", "fresh"));

        var (written, deleted, preserved, transactionPath) = await ApplyAsync(package, []);

        Assert.Equal(0, written);
        Assert.Equal(0, deleted);
        Assert.Equal(0, preserved);
        Assert.Equal(string.Empty, transactionPath);
    }

    private Task<(int Written, int Deleted, int Preserved, string TransactionPath)> ApplyAsync(
        string packageDir, IEnumerable<string> retained, IReadOnlyList<string>? obsoleteFiles = null)
        => IasCompanionApplier.ApplyAsync(GamePath, RuntimePath, BackupRoot, packageDir,
            new HashSet<string>(retained, StringComparer.OrdinalIgnoreCase),
            obsoleteFiles ?? [], null, CancellationToken.None);

    /// <summary>写一个最小 receipt（含 armor 组件），返回 receipt 路径。</summary>
    private string WriteReceipt(params (string Path, string Content, bool Preserve)[] files)
    {
        Directory.CreateDirectory(ArmorPath);
        var rows = new JsonArray();
        foreach (var (path, content, preserve) in files)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(ArmorPath, path.Replace('/', '\\')))!);
            File.WriteAllBytes(Path.Combine(ArmorPath, path.Replace('/', '\\')), bytes);
            var row = new JsonObject
            {
                ["path"] = path,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                ["bytes"] = bytes.Length,
            };
            if (preserve) row["preserve_existing"] = true;
            rows.Add(row);
        }
        var receipt = new JsonObject
        {
            ["schema"] = "HD2ExternalDeployment1",
            ["game"] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(GamePath)),
            ["runtime_root"] = Path.TrimEndingDirectorySeparator(Path.GetFullPath(RuntimePath)),
            ["components"] = new JsonArray(new JsonObject
            {
                ["component"] = "armor",
                ["version"] = "1.0.0",
                ["files"] = rows,
                ["bootstrap"] = new JsonArray(),
            }),
            ["boot_files"] = new JsonArray(),
        };
        var receiptPath = Path.Combine(RuntimePath, "deployment.json");
        Directory.CreateDirectory(RuntimePath);
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt));
        return receiptPath;
    }

    /// <summary>写一个最小生成包（external-runtime.json + files/）。</summary>
    private string WritePackage(string version, params (string Path, string Content)[] files)
    {
        var packageDir = Path.Combine(_root, $"package-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(packageDir, "files"));
        var rows = new JsonArray();
        foreach (var (path, content) in files)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(packageDir, "files", path.Replace('/', '\\')))!);
            File.WriteAllBytes(Path.Combine(packageDir, "files", path.Replace('/', '\\')), bytes);
            rows.Add(Row(path, bytes));
        }
        var manifest = new JsonObject
        {
            ["schema"] = "HD2ExternalRuntime1",
            ["component"] = "armor",
            ["metadata_only"] = true,
            ["version"] = version,
            ["host"] = new JsonObject(),
            ["files"] = rows,
            ["bootstrap"] = new JsonArray(),
        };
        File.WriteAllText(Path.Combine(packageDir, "external-runtime.json"), JsonSerializer.Serialize(manifest));
        return packageDir;
    }

    private static JsonObject Row(string path, byte[] bytes) => new()
    {
        ["path"] = path,
        ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        ["bytes"] = bytes.Length,
    };

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }
}
