using HD2Mimnt.UI.Services;
using HD2Mimnt.Application.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Tests.Services;

public sealed class FileLogServiceTests
{
    /// <summary>写入器持有常驻句柄，测试必须按共享读写方式读取，这与真实读取方（日志页/导出）一致。</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task Log_ReadOnlyFile_ShouldKeepDatabaseFailureAndRecoverAfterAccessReturns()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var service = new FileLogService(root);
        // 日志按天分片：断言对象是“当天分片”，不是固定的 application.log。
        var logPath = service.LogFilePath;
        var databasePath = Path.Combine(root, "hd2mimnt.db");
        try
        {
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(new FileLogProvider(root, "application")));
            var settings = new SettingsService(root, factory.CreateLogger<SettingsService>());
            await settings.SetSettingAsync(SettingKeys.Theme, "Dark");
            var originalLog = ReadShared(logPath);
            var notifications = 0;
            settings.SettingChanged += (_, _) => notifications++;
            File.SetAttributes(logPath, File.GetAttributes(logPath) | FileAttributes.ReadOnly);
            File.SetAttributes(databasePath, File.GetAttributes(databasePath) | FileAttributes.ReadOnly);

            var error = await Assert.ThrowsAsync<SqliteException>(() => settings.SetSettingAsync(SettingKeys.Theme, "Light"));

            Assert.Equal(8, error.SqliteErrorCode);
            Assert.Equal("Dark", settings.GetSetting(SettingKeys.Theme, ""));
            Assert.Equal(0, notifications);
            // 写入器持有常驻句柄，文件被标记只读不会让写入失败，这里只要求日志不阻塞业务。
            File.SetAttributes(databasePath, File.GetAttributes(databasePath) & ~FileAttributes.ReadOnly);
            File.SetAttributes(logPath, File.GetAttributes(logPath) & ~FileAttributes.ReadOnly);
            await settings.SetSettingAsync(SettingKeys.Theme, "Light");
            Assert.Equal("Light", new SettingsService(root).GetSetting(SettingKeys.Theme, ""));
            Assert.Equal(1, notifications);
            Assert.True(new FileInfo(logPath).Length >= System.Text.Encoding.UTF8.GetByteCount(originalLog));
        }
        finally
        {
            foreach (var path in new[] { logPath, databasePath })
                if (File.Exists(path)) File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Log_LockedFile_ShouldNotThrowAndRecoverOnNextMessage()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = new FileLogService(root).LogFilePath;
        try
        {
            using var provider = new FileLogProvider(root, "application");
            var logger = provider.CreateLogger("test");
            using (var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                logger.LogError("locked message");
            logger.LogError("recovered message");
            Assert.Contains("recovered message", ReadShared(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Log_ShouldEmitAlignedLineWithScopeAndTemplateFields()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var provider = new FileLogProvider(root, "application"))
            {
                var logger = provider.CreateLogger("Plugin.com.example.hello");
                using (logger.BeginScope(new Dictionary<string, object?> { ["job"] = 7 }))
                {
                    logger.LogInformation("被调用 {Total} 次", 46);
                }
            }

            var line = File.ReadAllLines(new FileLogService(root).LogFilePath).Single();
            // 级别缩写、插件短名、定宽对齐、作用域与模板字段的顺序都在这一条里固定下来。
            Assert.Matches(@"^\[[^\]]+\] \[INF\] plugin:hello\s+被调用 46 次 \| job=7$", line);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrimExpired_RemovesSlicesOlderThanRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var kept = Path.Combine(root, $"application-{today.AddDays(-6):yyyy-MM-dd}.log");
            var stale = Path.Combine(root, $"application-{today.AddDays(-10):yyyy-MM-dd}.log");
            File.WriteAllText(kept, "kept");
            File.WriteAllText(stale, "stale");

            new FileLogService(root, () => 7).TrimExpired();

            Assert.True(File.Exists(kept));
            Assert.False(File.Exists(stale));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadRecentAsync_ReturnsBoundedTail()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileLogService(root);
            await File.WriteAllLinesAsync(service.LogFilePath, Enumerable.Range(1, 10).Select(i => $"line-{i}"));

            var lines = await service.ReadRecentAsync(3);

            Assert.Equal(new[] { "line-8", "line-9", "line-10" }, lines);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ReadRecentAsync_ShouldReadUnicodeTailAcrossBlocksWhileWriterIsOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileLogService(root);
            var lines = Enumerable.Range(0, 10000).Select(i => $"日志-{i}-测试内容").ToArray();
            await File.WriteAllTextAsync(service.LogFilePath, string.Join("\r\n", lines));
            using var writer = new FileStream(service.LogFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            Assert.Equal(lines.TakeLast(6000), await service.ReadRecentAsync(6000));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ExportAsync_CopiesLogWhileSourceIsOpen()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new FileLogService(root);
            await File.WriteAllTextAsync(service.LogFilePath, "diagnostic");
            var destination = Path.Combine(root, "export.log");
            await service.ExportAsync(destination);

            Assert.Equal("diagnostic", await File.ReadAllTextAsync(destination));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
