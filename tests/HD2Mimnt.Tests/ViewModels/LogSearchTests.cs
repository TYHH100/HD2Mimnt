using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using HD2Mimnt.UI.Services;
using HD2Mimnt.UI.ViewModels;

namespace HD2Mimnt.Tests.ViewModels;

public class LogSearchTests
{
    [Fact]
    public async Task Search_AppliesPinyinAndFuzzySettings()
    {
        var repository = new MemorySettingsRepository();
        var settings = new SettingsService(repository,
            Path.Combine(Path.GetTempPath(), "hd2mimnt-log-search-" + Guid.NewGuid().ToString("N")));
        var viewModel = new LogViewModel(new FakeLogService(
            "2026-01-01 00:00:00.000 +08:00 [Information] Helldivers: 中文日志"), settings);
        await viewModel.InitializeAsync();

        viewModel.SearchText = "zw";
        Assert.Empty(viewModel.Entries);

        await settings.SetSettingAsync(SettingKeys.SearchPinyin, true);
        Assert.Single(viewModel.Entries);

        viewModel.SearchText = "hldv";
        Assert.Empty(viewModel.Entries);

        await settings.SetSettingAsync(SettingKeys.SearchFuzzy, true);
        Assert.Single(viewModel.Entries);
    }

    private sealed class FakeLogService(params string[] lines) : IFileLogService
    {
        public string LogFilePath => "test.log";

        public Task<IReadOnlyList<string>> ReadRecentAsync(int maxLines, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(lines);

        public Task ExportAsync(string destinationPath, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class MemorySettingsRepository : ISettingsRepository
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Dictionary<string, string> ReadAll() => new(_values, StringComparer.Ordinal);

        public bool IsLegacyMigrationComplete() => true;

        public void ImportLegacy(IReadOnlyDictionary<string, string> values)
        {
            foreach (var (key, value) in values) _values[key] = value;
        }

        public Task ApplyAsync(IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> removedKeys)
        {
            foreach (var key in removedKeys) _values.Remove(key);
            foreach (var (key, value) in values) _values[key] = value;
            return Task.CompletedTask;
        }

        public Task ResetAsync(string? category)
        {
            if (string.IsNullOrEmpty(category))
            {
                _values.Clear();
                return Task.CompletedTask;
            }
            var prefix = category + ".";
            foreach (var key in _values.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
