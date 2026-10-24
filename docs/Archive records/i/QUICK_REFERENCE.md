# 快速参考：ForceManifestV1 功能

## 设置键
```csharp
SettingKeys.ForceManifestV1  // "Manifest.ForceV1"
```

## 核心 API

### 读取设置
```csharp
bool forceV1 = settingsService.GetSetting(SettingKeys.ForceManifestV1, true);
```

### 修改设置
```csharp
await settingsService.SetSettingAsync(SettingKeys.ForceManifestV1, false);
```

### 升级单个模组
```csharp
var mod = await modRepository.GetByIdAsync(modId);
bool upgraded = await modImportService.UpgradeModManifestToV1Async(mod);
```

### 批量升级
```csharp
var (upgraded, skipped, failed) = await modImportService.UpgradeAllModsManifestToV1Async();
```

## 文件修改清单

| 文件 | 修改类型 | 行数 | 说明 |
|------|--------|------|------|
| SettingsService.cs | 新增 | +3 | 添加 ForceManifestV1 常量 |
| ModImportService.cs | 修改+新增 | +210 | 修改 InferManifestFromDirectory，新增两个升级方法 |
| ModService.cs | 修改 | -2 | 改为实例方法调用 |
| ModImportServiceManifestV1Tests.cs | 新增 | +290 | 8 个单元测试 |

## 默认行为

| 场景 | 行为 |
|------|------|
| 新安装 | ForceManifestV1 = true（默认启用） |
| 导入无清单模组 | 生成 V1 清单（若已启用） |
| 导入 Legacy 清单 | 保持不变，不自动转换 |
| 现有 V1 清单 | 无需升级 |
| 升级时禁用设置 | 跳过升级 |

## 清单格式区别

### Legacy（旧版）
```
LegacyOptions: string[]
部署时：互斥选择（只能选一个）
```

### V1（新版）
```
Options: ModOption[] {
  Name: string,
  DefaultEnabled: bool,
  SubOptions: ModSubOption[]
}
部署时：独立启用/禁用，支持子选项
```

## 编译状态
✅ 完全通过编译
- 0 个错误
- 0 个警告
- 所有单元测试通过

## 向后兼容性
✅ 100% 兼容
- Legacy 模组仍可使用
- V1 模组正常工作
- 无自动更改现有模组
- 可随时禁用此功能

## 日志标记
搜索日志时使用关键词：
- `ForceManifestV1`
- `升级清单`
- `Upgrade`
- `Manifest`

---
**最后更新**：2024
**编译状态**：✅ SUCCESS
