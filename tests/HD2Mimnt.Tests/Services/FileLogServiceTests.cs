using HD2Mimnt.UI.Services;
using HD2Mimnt.Application.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HD2Mimnt.Tests.Services;

public sealed class FileLogServiceTests
{
    [Fact]
    public async Task Log_ReadOnlyFile_ShouldKeepDatabaseFailureAndRecoverAfterAccessReturns()
    {
        var root = Path.Combine(Path.GetTempPath(), "HD2MimntLogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var logPath = Path.Combine(root, "application.log");
        var databasePath = Path.Combine(root, "hd2mimnt.db");
        try
        {
            using var factory = LoggerFactory.Create(builder => builder.AddProvider(new FileLogProvider(logPath)));
            var settings = new SettingsService(root, factory.CreateLogger<SettingsService>());
            await settings.SetSettingAsync(SettingKeys.Theme, "Dark");
            var originalLog = await File.ReadAllTextAsync(logPath);
            var notifications = 0;
            settings.SettingChanged += (_, _) => notifications++;
            File.SetAttributes(logPath, File.GetAttributes(logPath) | FileAttributes.ReadOnly);
            File.SetAttributes(databasePath, File.GetAttributes(databasePath) | FileAttributes.ReadOnly);

            var error = await Assert.ThrowsAsync<SqliteException>(() => settings.SetSettingAsync(SettingKeys.Theme, "Light"));

            Assert.Equal(8, error.SqliteErrorCode);
            Assert.Equal("Dark", settings.GetSetting(SettingKeys.Theme, ""));
            Assert.Equal(0, notifications);
            Assert.Equal(originalLog, await File.ReadAllTextAsync(logPath));
            File.SetAttributes(databasePath, File.GetAttributes(databasePath) & ~FileAttributes.ReadOnly);
            File.SetAttributes(logPath, File.GetAttributes(logPath) & ~FileAttributes.ReadOnly);
            await settings.SetSettingAsync(SettingKeys.Theme, "Light");
            Assert.Equal("Light", new SettingsService(root).GetSetting(SettingKeys.Theme, ""));
            Assert.Equal(1, notifications);
            Assert.True(new FileInfo(logPath).Length > System.Text.Encoding.UTF8.GetByteCount(originalLog));
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
        var path = Path.Combine(root, "application.log");
        try
        {
            using var provider = new FileLogProvider(path);
            var logger = provider.CreateLogger("test");
            using (var locked = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
                logger.LogError("locked message");
            logger.LogError("recovered message");
            Assert.Contains("recovered message", File.ReadAllText(path));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
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
