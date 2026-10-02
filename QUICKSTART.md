# HD2Mimnt 快速开始指南

## 构建和运行

```bash
# 构建项目
dotnet build

# 运行测试
dotnet test

# 运行应用程序
dotnet run --project src/HD2Mimnt.UI
```

## 项目状态

✅ **Phase 1 完成** - 架构基础搭建完成  
✅ **Phase 2 完成** - Mod 管理核心功能实现  
🔄 **Phase 3 待开始** - 部署工作流引擎  

当前构建状态: ✅ 成功  
当前测试状态: ✅ 全部通过 (16/16)

## 核心功能

### 已实现

1. **Mod 管理**
   - Mod 导入（文件夹、ZIP）
   - Manifest 解析（Legacy + V1 格式）
   - 缩略图生成
   - 标签和分类
   - 搜索和过滤

2. **配置文件管理**
   - 创建、读取、更新、删除配置文件
   - 切换活动配置文件

3. **数据库**
   - SQLite 持久化存储
   - 仓储模式实现
   - 迁移支持

4. **UI 基础**
   - MainWindow（主窗口）
   - LibraryViewModel（Mod 库）
   - ProfileViewModel（配置文件）
   - MVVM 数据绑定

### 待实现

1. **部署工作流** (Phase 3)
   - 工作流定义和执行
   - PAK 文件处理
   - 配置文件修改

2. **冲突检测** (Phase 4)
   - 文件冲突检测
   - PAK 内容冲突
   - 自动解决策略

3. **UI 完善** (Phase 5)
   - 完整的 Mod 列表视图
   - Mod 详情面板
   - 部署历史记录

## 架构概览

```
┌─────────────────────────────────────────┐
│          HD2Mimnt.UI (Jalium.UI)        │
│   ViewModels, Views, MVVM Bindings      │
└─────────────────┬───────────────────────┘
                  │
┌─────────────────▼───────────────────────┐
│       HD2Mimnt.Application              │
│   Services, Commands, Business Logic    │
└─────────────────┬───────────────────────┘
                  │
        ┌─────────┴─────────┐
        │                   │
┌───────▼─────────┐  ┌──────▼──────────────┐
│ HD2Mimnt.Core   │  │ HD2Mimnt.Infrastructure │
│ Entities,       │  │ Repositories,        │
│ Interfaces      │  │ DbContext            │
└─────────────────┘  └─────────────────────┘
```

## 核心服务

### ModService
Mod 的核心管理服务，提供:
- `GetAllAsync()` - 获取所有 Mod
- `GetByIdAsync(id)` - 根据 ID 获取 Mod
- `CreateAsync(mod)` - 创建新 Mod
- `UpdateAsync(mod)` - 更新 Mod
- `DeleteModAsync(id)` - 删除 Mod
- `SearchAsync(query)` - 搜索 Mod
- `GetByTagsAsync(tags)` - 按标签筛选
- `GetByCategoryAsync(category)` - 按分类筛选
- `GetStatisticsAsync()` - 获取统计信息

### ModImportService
Mod 导入服务，提供:
- `ImportFromFolderAsync(path)` - 从文件夹导入
- `ImportFromZipAsync(zipPath)` - 从 ZIP 导入
- `ParseManifestAsync(path)` - 解析 Manifest

### ProfileService
配置文件管理服务，提供:
- `GetAllProfilesAsync()` - 获取所有配置文件
- `GetActiveProfileAsync()` - 获取活动配置文件
- `CreateProfileAsync(name)` - 创建新配置文件
- `SwitchProfileAsync(id)` - 切换配置文件
- `DeleteProfileAsync(id)` - 删除配置文件

### ThumbnailService
缩略图生成服务，提供:
- `GenerateThumbnailAsync(imagePath, size)` - 生成缩略图
- `GetThumbnailPathAsync(modId)` - 获取缩略图路径

## 数据模型

### Mod 实体
```csharp
- Id: Guid
- Name: string
- Version: string
- Author: string
- Description: string
- InstallPath: string
- Status: ModStatus (Ready, Installed, Conflict, Error)
- Source: ModSource (Local, NexusMods, GitHub, ModIO)
- Category: ModCategory
- Tags: List<string>
- Manifest: ModManifest
- ThumbnailPath: string
- Size: long
- ContainsScripts: bool
- CreatedAt: DateTime
- UpdatedAt: DateTime
```

### ModManifest
```csharp
- Name: string
- Author: string
- Description: string
- Version: string
- Category: ModCategory
- Tags: List<string>
- IsLegacy: bool
- LegacyOptions: List<LegacyOption>  // 互斥下拉选项
- Options: List<ManifestOption>       // 独立启用选项
```

### Profile 实体
```csharp
- Id: Guid
- Name: string
- IsActive: bool
- CreatedAt: DateTime
- UpdatedAt: DateTime
```

## 开发规范

### 代码风格
- 遵循 Clean Architecture 原则
- 使用依赖注入
- Repository 模式访问数据
- MVVM 模式开发 UI
- 异步编程优先（async/await）

### 命名规范
- 实体类: PascalCase（Mod, Profile）
- 接口: I + PascalCase（IModRepository）
- 服务: PascalCase + Service（ModService）
- 私有字段: _camelCase
- 公共属性: PascalCase

### 测试规范
- 单元测试命名: MethodName_Scenario_ExpectedResult
- 使用 xUnit 测试框架
- 使用 In-Memory SQLite 进行数据库测试
- 每个服务都应有对应的测试类

## 参考项目

原项目代码参考: `D:\TYHH10-git\Helldivers2ModManager`

可以从原项目中参考:
- Mod 部署逻辑
- PAK 文件处理
- 冲突检测算法
- UI 交互设计

## 技术文档

- [CLAUDE.md](./CLAUDE.md) - 详细的开发文档
- [PROGRESS.md](./PROGRESS.md) - 开发进度跟踪
- [HD2Mimnt v2 总体开发计划.md](./HD2Mimnt%20v2%20总体开发计划.md) - 总体规划

## 常见问题

### 如何添加新的 Mod 源？
1. 在 `Core/Enums/CoreEnums.cs` 中添加新的 `ModSource` 枚举值
2. 在 `Application/Services` 中创建对应的导入服务
3. 在 UI 中添加导入界面

### 如何扩展 Manifest 格式？
1. 修改 `Core/Entities/ModManifest.cs`
2. 更新 `ModImportService` 中的解析逻辑
3. 添加相应的测试用例

### 如何添加新的工作流步骤？
（Phase 3 完成后补充）

## 技术债务

1. **平台兼容性**: ThumbnailService 使用 System.Drawing.Common，仅支持 Windows。考虑迁移到 SkiaSharp 或 ImageSharp。

2. **错误处理**: 需要添加统一的异常处理机制和日志记录。

3. **UI 确认对话框**: LibraryViewModel 中的删除确认对话框待实现。

4. **性能优化**: 大量 Mod 时的列表加载性能优化。

## 下一步行动

按照开发计划，接下来应该实现 **Phase 3: 部署工作流引擎**

1. 定义 WorkflowDefinition 和 WorkflowStep 实体
2. 实现 WorkflowEngine 服务
3. 创建内置工作流（标准部署、PAK 处理等）
4. 添加工作流测试

详细计划参见 [HD2Mimnt v2 总体开发计划.md](./HD2Mimnt%20v2%20总体开发计划.md)
