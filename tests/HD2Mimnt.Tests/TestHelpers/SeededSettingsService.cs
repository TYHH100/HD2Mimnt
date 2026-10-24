using HD2Mimnt.Application.Services;
using HD2Mimnt.Core.Interfaces;
using Moq;

namespace HD2Mimnt.Tests.TestHelpers;

/// <summary>
/// 构造带种子设置的真实 SettingsService（内存假仓储）。
/// SettingsService 的构造与 GetSetting 均非虚方法，无法用 Moq 拦截，
/// 测试需要控制 ForceManifestV1 等开关时用真实实例加种子值。
/// </summary>
public static class SeededSettingsService
{
    public static SettingsService Create(bool? forceManifestV1 = null)
    {
        var values = new Dictionary<string, string>();
        if (forceManifestV1 is { } value)
        {
            values[SettingKeys.ForceManifestV1] = value ? "true" : "false";
        }

        var repository = new Mock<ISettingsRepository>();
        repository.Setup(r => r.IsLegacyMigrationComplete()).Returns(true);
        repository.Setup(r => r.ReadAll()).Returns(values);
        return new SettingsService(repository.Object, appDataPath: "unused");
    }
}
