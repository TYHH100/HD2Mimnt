# 强制 V1 清单功能

## 功能描述

添加了一个新的设置项 `ForceManifestV1`，用于控制在导入模组或自动生成清单时是否强制使用 V1 清单格式而非旧版 Legacy 格式。

### 特性

1. **新设置项**：`SettingKeys.ForceManifestV1`
   - 位置：`src/HD2Mimnt.Application/Services/SettingsService.cs`
   - 默认值：`true`（默认开启）
   - 类型：布尔值

2. **导入行为**：
   - 当导入无 manifest 文件的模组时，如果模组目录有子目录结构
   - 若启用 `ForceManifestV1`：生成 V1 清单，子目录作为互斥子选项
   - 若禁用 `ForceManifestV1`：生成 Legacy 清单，子目录作为互斥下拉选项

3. **清单升级功能**：
   - `ModImportService.UpgradeModManifestToV1Async()`：升级单个模组清单
   - `ModImportService.UpgradeAllModsManifestToV1Async()`：批量升级所有模组清单
   - 仅在 `ForceManifestV1` 启用时执行

## 文件更改

### 1. `src/HD2Mimnt.Application/Services/SettingsService.cs`
- 添加新的设置键：`public const string ForceManifestV1 = "Manifest.ForceV1";`

### 2. `src/HD2Mimnt.Application/Services/ModImportService.cs`

#### 修改方法：`InferManifestFromDirectory`
- 从静态方法改为实例方法（需要访问 `_settingsService`）
- 读取 `ForceManifestV1` 设置：
  ```csharp
  var forceV1 = _settingsService?.GetSetting(SettingKeys.ForceManifestV1, true) ?? true;
  ```
- 根据设置返回不同格式的清单

#### 新增方法：`UpgradeModManifestToV1Async`
- 将单个模组的清单升级为 V1 格式
- 转换 Legacy 选项为 V1 选项结构
- 保留所有基本元数据（GUID、名称、作者等）
- 返回升级是否成功

#### 新增方法：`UpgradeAllModsManifestToV1Async`
- 批量升级库中所有模组的清单
- 支持进度报告：`IProgress<(int current, int total)>`
- 返回升级/跳过/失败的统计数据

### 3. `src/HD2Mimnt.Application/Services/ModService.cs`
- 修改第 336 行：`InferManifestFromDirectory` 调用改为实例方法
  ```csharp
  ?? _importService.InferManifestFromDirectory(mod.Path, mod.Name);
  ```

## 使用示例

### 查询和设置

```csharp
// 查询当前设置
var forceV1 = settingsService.GetSetting(SettingKeys.ForceManifestV1, true);

// 更改设置
await settingsService.SetSettingAsync(SettingKeys.ForceManifestV1, false);
```

### 升级单个模组

```csharp
var mod = await modRepository.GetByIdAsync(modId);
var upgraded = await importService.UpgradeModManifestToV1Async(mod);
if (upgraded)
{
    _logger?.LogInformation("模组已升级为 V1 清单");
}
```

### 批量升级所有模组

```csharp
var progress = new Progress<(int current, int total)>(p =>
{
    Console.WriteLine($"进度: {p.current}/{p.total}");
});

var (upgraded, skipped, failed) = await importService.UpgradeAllModsManifestToV1Async(
    cancellationToken: token,
    progress: progress
);

Console.WriteLine($"升级完成：升级 {upgraded} 个，跳过 {skipped} 个，失败 {failed} 个");
```

## 清单版本对比

### Legacy 格式（V0）
- 子目录作为互斥下拉选项
- 通过 `LegacyOptions: string[]` 定义
- 部署时只能选择一个子目录

### V1 格式
- 子目录作为独立启用的选项
- 通过 `Options: ModOption[]` 定义
- 每个选项可以独立启用/禁用
- 支持子选项（互斥选择）

### 升级后的转换
```
Legacy: { "LegacyOptions": ["Option1", "Option2"] }
  ↓
V1: {
  "Options": [
    { "Name": "Option1", "DefaultEnabled": false, "SubOptions": [] },
    { "Name": "Option2", "DefaultEnabled": false, "SubOptions": [] }
  ]
}
```

## 默认行为

- 新安装时，`ForceManifestV1` 默认为 `true`
- 导入无清单的模组时会自动生成 V1 清单
- 现有库中的 Legacy 清单不会自动转换，除非调用升级方法

## 向后兼容性

- 现有的 Legacy 清单继续工作，不受此设置影响
- V1 和 Legacy 清单格式都能被正确解析和部署
- 升级是可选操作，可通过禁用设置来维持 Legacy 行为
