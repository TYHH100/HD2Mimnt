# ForceManifestV1 设置实现指南

## 概述

本文档说明如何在 UI 层和业务层集成 `ForceManifestV1` 设置功能。

## 设置定义

设置键已添加到 `SettingKeys` 类中：

```csharp
public const string ForceManestV1 = "Manifest.ForceV1";
```

## 在 UI 中添加设置界面

### 步骤 1：在资源文件中添加标签

编辑 `src/HD2Mimnt.Application/Localization/Resources/Strings.resx`：

```xml
<data name="Setting_ForceManifestV1" xml:space="preserve">
  <value>强制新导入模组为 V1 清单格式</value>
</data>
<data name="Setting_ForceManifestV1_Description" xml:space="preserve">
  <value>导入模组时，若无清单文件，自动生成 V1 清单格式而非旧版 Legacy 格式。已启用此选项时，库中没有清单的模组也可自动升级为 V1</value>
</data>
```

### 步骤 2：在设置视图模型中添加绑定属性

编辑 `src/HD2Mimnt.UI/ViewModels/SettingsViewModel.cs`（或相应的设置视图模型）：

```csharp
private bool _forceManifestV1;

public bool ForceManifestV1
{
    get => _forceManifestV1;
    set
    {
        if (SetProperty(ref _forceManifestV1, value))
        {
            _settingsService.SetSettingAsync(SettingKeys.ForceManifestV1, value).FireAndForget();
        }
    }
}

// 在初始化方法中加载设置
public async Task LoadSettingsAsync()
{
    ForceManifestV1 = _settingsService.GetSetting(SettingKeys.ForceManifestV1, true);
}
```

### 步骤 3：在设置视图中添加 UI 控件

编辑 `src/HD2Mimnt.UI/Views/SettingsView.axaml`：

```xml
<ToggleSwitch 
    Content="{DynamicResource Setting_ForceManifestV1}"
    IsChecked="{Binding ForceManifestV1}" />
```

## 在导入流程中的使用

导入过程已自动集成此功能。当导入无 manifest 文件的模组时：

1. `ModImportService.PrepareModAsync()` 调用 `InferManifestFromDirectory()`
2. 该方法根据 `ForceManifestV1` 设置选择清单格式
3. V1 格式的清单被保存到模组目录

**代码位置**：`src/HD2Mimnt.Application/Services/ModImportService.cs:225`

```csharp
manifest ??= InferManifestFromDirectory(modRootPath, fallbackName);
```

## 批量升级现有模组

### 场景：用户启用了 ForceManifestV1，想升级库中所有现有模组

### 实现方式：添加菜单项或按钮

在 UI 中添加一个按钮，调用升级方法：

```csharp
private async Task UpgradeAllModsAsync()
{
    var progress = new Progress<(int current, int total)>(p =>
    {
        ProgressMessage = $"升级进度：{p.current}/{p.total}";
        ProgressPercentage = (double)p.current / p.total * 100;
    });

    var (upgraded, skipped, failed) = await _modImportService.UpgradeAllModsManifestToV1Async(
        cancellationToken: _cancellationToken,
        progress: progress
    );

    await _dialogService.ShowInfo(
        $"清单升级完成\n" +
        $"升级：{upgraded} 个\n" +
        $"跳过：{skipped} 个\n" +
        $"失败：{failed} 个"
    );
}
```

### 单个模组升级

如果需要为某个模组升级清单：

```csharp
private async Task UpgradeModManifestAsync(Guid modId)
{
    var mod = await _modRepository.GetByIdAsync(modId);
    if (mod == null) return;

    var success = await _modImportService.UpgradeModManifestToV1Async(mod);
    if (success)
    {
        await _dialogService.ShowSuccess("模组清单已升级为 V1 格式");
        RaiseModsChanged();
    }
    else
    {
        await _dialogService.ShowWarning("无法升级该模组的清单");
    }
}
```

## 测试

单元测试已添加到 `tests/HD2Mimnt.Tests/Services/ModImportServiceManifestV1Tests.cs`

### 运行特定测试

```bash
# 运行所有 ManifestV1 相关测试
dotnet test --filter "FullyQualifiedName~ModImportServiceManifestV1Tests"

# 运行单个测试
dotnet test --filter "FullyQualifiedName~ModImportServiceManifestV1Tests.InferManifestFromDirectory_WithForceV1Enabled_ReturnsV1Manifest"
```

## 数据库迁移

设置存储在数据库中，无需数据库迁移。首次读取时会使用默认值 `true`。

## 配置示例

### 启用 ForceManifestV1（默认）

```csharp
// 应用启动时
var settings = new SettingsService(appDataPath);
var forceV1 = settings.GetSetting(SettingKeys.ForceManifestV1, true); // 返回 true
```

### 禁用 ForceManifestV1

```csharp
await settings.SetSettingAsync(SettingKeys.ForceManifestV1, false);
// 之后的导入将使用 Legacy 格式
```

### 检查设置状态

```csharp
public void CheckManifestSettings()
{
    var forceV1 = _settingsService.GetSetting(SettingKeys.ForceManifestV1, true);
    _logger.LogInformation("ForceManifestV1 设置: {Enabled}", forceV1 ? "启用" : "禁用");
}
```

## 清单版本检测

系统能自动检测现有模组的清单版本：

```csharp
// 检测单个模组
if (mod.Manifest?.ManifestVersion == ManifestVersion.V1)
{
    // 模组使用 V1 格式
}
else if (mod.Manifest?.ManifestVersion == ManifestVersion.Legacy)
{
    // 模组使用 Legacy 格式
}
else
{
    // 模组无清单
}
```

## 注意事项

1. **向后兼容性**：禁用此设置后，仍可导入 V1 格式的模组，现有 V1 模组也能正常使用
2. **自动升级**：调用 `UpgradeAllModsManifestToV1Async()` 前，务必确保 ForceManifestV1 已启用
3. **无子目录的模组**：如果模组无子目录结构，无法自动生成清单，需要手动创建 manifest.json
4. **持久化**：设置值自动保存到数据库，应用重启后仍然有效

## 常见问题

### Q：如何回滚到 Legacy 格式？

A：禁用 `ForceManifestV1` 设置。新导入的模组将使用 Legacy 格式，但现有的 V1 模组不会自动转换。

### Q：升级后如何恢复 Legacy 清单？

A：目前无自动回滚机制。如需恢复，需手动删除新清单或编辑清单文件。

### Q：子选项支持什么格式？

A：子选项结构存储在 manifest.json 中，支持名称、描述、预览图、文件映射等。详见 ModManifest 类定义。

### Q：升级过程中遇到错误怎么办？

A：升级方法会捕获异常并记录到日志，不会影响其他模组。检查日志文件了解具体错误信息。
