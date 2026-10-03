# HD2Mimnt 快速参考

## 🚀 快速开始

### 构建项目
```bash
cd "G:\Temp\Githud\HD2Mimnt"
dotnet build
```

### 运行应用
```bash
cd "G:\Temp\Githud\HD2Mimnt\src\HD2Mimnt.UI"
dotnet run
```

### 运行测试
```bash
cd "G:\Temp\Githud\HD2Mimnt"
dotnet test
```

---

## 📁 项目结构

```
HD2Mimnt/
├── src/
│   ├── HD2Mimnt.Core/              # 核心层
│   │   ├── Entities/               # 实体
│   │   ├── Enums/                  # 枚举
│   │   └── Interfaces/             # 接口
│   ├── HD2Mimnt.Infrastructure/    # 基础设施层
│   │   ├── Data/                   # 数据库
│   │   └── Repositories/           # 仓储
│   ├── HD2Mimnt.Application/       # 应用层
│   │   └── Services/               # 服务
│   └── HD2Mimnt.UI/                # UI层
│       ├── ViewModels/             # 视图模型
│       ├── Views/                  # 视图
│       └── App.axaml               # 应用入口
├── tests/
│   └── HD2Mimnt.Tests/             # 测试
├── docs/                            # 文档
└── HD2Mimnt v2 总体开发计划.md     # 开发计划
```

---

## 🎯 核心概念

### Mod
- **路径**: `G:\Temp\Githud\HD2Mimnt\src\HD2Mimnt.Core\Entities\Mod.cs`
- **职责**: 表示一个 Mod 的元数据
- **状态**: Importing, Ready, Error, Disabled

### Profile
- **路径**: `G:\Temp\Githud\HD2Mimnt\src\HD2Mimnt.Core\Entities\Profile.cs`
- **职责**: 管理 Mod 配置集合
- **特性**: 只能有一个激活的 Profile

### ProfileMod
- **路径**: `G:\Temp\Githud\HD2Mimnt\src\HD2Mimnt.Core\Entities\ProfileMod.cs`
- **职责**: Profile 和 Mod 的多对多关联
- **属性**: Enabled, Order

---

## 🔧 关键服务

### ModLibraryService
**位置**: `src\HD2Mimnt.Application\Services\ModLibraryService.cs`

**功能**:
```csharp
// 导入 Mod
await modLibraryService.ImportModAsync(modPath);

// 获取所有 Mod
var mods = await modLibraryService.GetAllModsAsync();

// 搜索 Mod
var results = await modLibraryService.SearchModsAsync("weapon");

// 删除 Mod
await modLibraryService.DeleteModAsync(modId);
```

### ProfileService
**位置**: `src\HD2Mimnt.Application\Services\ProfileService.cs`

**功能**:
```csharp
// 创建 Profile
var profileId = await profileService.CreateProfileAsync("My Profile");

// 激活 Profile
await profileService.SetActiveProfileAsync(profileId);

// 添加 Mod 到 Profile
await profileService.AddModToProfileAsync(profileId, modId);

// 重排序 Mod
await profileService.ReorderModsAsync(profileId, modIds);
```

---

## 🗄️ 数据库

### 位置
- **开发**: `%LOCALAPPDATA%\HD2Mimnt\hd2mimnt.db`
- **测试**: `%TEMP%\test_hd2mimnt_*.db` (临时)

### 表结构

| 表名 | 主键 | 说明 |
|------|------|------|
| Mods | Id (GUID) | Mod 元数据 |
| Profiles | Id (GUID) | Profile 配置 |
| ProfileMods | ProfileId + ModId | Profile-Mod 关联 |
| Tasks | Id (GUID) | 任务记录 |
| DeploymentRecords | Id (GUID) | 部署记录 |
| DeploymentFiles | Id (GUID) | 部署文件追踪 |
| Settings | Key (TEXT) | 应用设置 |

### 查询示例

```sql
-- 获取所有 Ready 状态的 Mod
SELECT * FROM Mods WHERE Status = 'Ready';

-- 获取激活的 Profile 及其 Mod
SELECT p.*, pm.*, m.*
FROM Profiles p
LEFT JOIN ProfileMods pm ON p.Id = pm.ProfileId
LEFT JOIN Mods m ON pm.ModId = m.Id
WHERE p.IsActive = 1
ORDER BY pm.Order;
```

---

## 🎨 UI 样式

### 颜色变量

```xml
<!-- 背景层级 -->
<SolidColorBrush x:Key="BackgroundLevel0">#05070C</SolidColorBrush>
<SolidColorBrush x:Key="BackgroundLevel1">#0A0D12</SolidColorBrush>
<SolidColorBrush x:Key="BackgroundLevel2">#0F131C</SolidColorBrush>
<SolidColorBrush x:Key="BackgroundLevel3">#161D2B</SolidColorBrush>
<SolidColorBrush x:Key="BackgroundLevel4">#1E2636</SolidColorBrush>

<!-- 强调色 -->
<SolidColorBrush x:Key="AccentColor">#38BDF8</SolidColorBrush>

<!-- 文本 -->
<SolidColorBrush x:Key="TextPrimary">#E5E7EB</SolidColorBrush>
<SolidColorBrush x:Key="TextSecondary">#9CA3AF</SolidColorBrush>
```

### 按钮样式

```xml
<!-- 导航按钮 -->
<Button Classes="nav-button" Content="Library" />

<!-- 主按钮 -->
<Button Classes="primary" Content="Import" />

<!-- 次按钮 -->
<Button Classes="secondary" Content="Cancel" />
```

---

## 🧪 测试

### 运行特定测试
```bash
# 只运行 ModLibraryService 测试
dotnet test --filter "FullyQualifiedName~ModLibraryServiceTests"

# 只运行 ProfileService 测试
dotnet test --filter "FullyQualifiedName~ProfileServiceTests"
```

### 测试覆盖
```bash
# 生成代码覆盖率报告
dotnet test --collect:"XPlat Code Coverage"
```

---

## 📦 依赖包

| 包名 | 版本 | 用途 |
|------|------|------|
| Avalonia | 12.0.3 | UI 框架 |
| Avalonia.Desktop | 12.0.3 | 桌面支持 |
| Microsoft.Data.Sqlite | 最新 | SQLite 驱动 |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM 工具包 |
| Microsoft.Extensions.DependencyInjection | 最新 | 依赖注入 |
| xUnit | 最新 | 测试框架 |

---

## 🔍 常用命令

### 清理构建
```bash
dotnet clean
dotnet build
```

### 发布 Release 版本
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

### 查看项目依赖
```bash
dotnet list package
```

### 添加 NuGet 包
```bash
dotnet add package <PackageName>
```

---

## 📝 开发约定

### 命名规范
- **实体**: PascalCase (Mod, Profile)
- **接口**: IPascalCase (IModRepository)
- **服务**: PascalCaseService (ModLibraryService)
- **ViewModel**: PascalCaseViewModel (LibraryViewModel)
- **私有字段**: _camelCase (_modRepository)

### 异步方法
- 所有数据访问方法都以 `Async` 结尾
- 返回 `Task` 或 `Task<T>`
- 接受 `CancellationToken` 参数

### 错误处理
- 使用 try-catch 捕获异常
- 记录错误日志（未来实现）
- 返回友好的错误消息

---

## 🛠️ 故障排查

### 构建失败
```bash
# 清理并重新构建
dotnet clean
dotnet restore
dotnet build
```

### 测试失败
```bash
# 查看详细输出
dotnet test --verbosity normal

# 查看具体错误
dotnet test --logger "console;verbosity=detailed"
```

### 数据库问题
```bash
# 删除数据库重新初始化
# Windows
del "%LOCALAPPDATA%\HD2Mimnt\hd2mimnt.db"
```

---

## 📚 重要文档

| 文档 | 说明 |
|------|------|
| `HD2Mimnt v2 总体开发计划.md` | 完整开发计划 |
| `CLAUDE.md` | 开发规范和架构说明 |
| `README.md` | 项目介绍 |
| `PROJECT_STATUS.md` | 当前进度 |
| `PHASE1_SUMMARY.md` | Phase 1 总结 |
| `docs/PHASE1_COMPLETION.md` | Phase 1 详细报告 |

---

## 🔗 相关链接

- **Avalonia 文档**: https://docs.avaloniaui.net/
- **SQLite 文档**: https://www.sqlite.org/docs.html
- **MVVM Toolkit**: https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/

---

## 💡 快速提示

1. **数据库位置**: 默认在 `%LOCALAPPDATA%\HD2Mimnt\`
2. **日志位置**: 暂未实现，计划在 Phase 4
3. **配置文件**: 暂未实现，计划在 Phase 2
4. **插件目录**: 暂未实现，计划在 Phase 7

---

*最后更新: 2026-10-02*  
*Phase: 1 完成*
