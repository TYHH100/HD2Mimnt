namespace HD2Mimnt.Core.Interfaces;

/// <summary>设置值以 JSON 文本存储；启动读取保持同步，写入按键事务提交。</summary>
public interface ISettingsRepository
{
    Dictionary<string, string> ReadAll();
    bool IsLegacyMigrationComplete();
    void ImportLegacy(IReadOnlyDictionary<string, string> values);
    Task ApplyAsync(IReadOnlyDictionary<string, string> values, IReadOnlyCollection<string> removedKeys);
    Task ResetAsync(string? category);
    Task ResetCategoriesAsync(IReadOnlyCollection<string> categories);
}
