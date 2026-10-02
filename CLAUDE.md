# HD2Mimnt - Helldivers 2 Mod Manager

## 项目概述

HD2Mimnt 是一个专门为 Helldivers 2 设计的 Mod 管理器，采用 Clean Architecture 架构。

## 技术栈

- **UI 框架**: Jalium.UI (基于 Avalonia 的跨平台桌面应用框架)
- **运行时**: .NET 10
- **数据库**: SQLite (WAL 模式)
- **架构模式**: Clean Architecture + MVVM
- **依赖注入**: Microsoft.Extensions.DependencyInjection
- **MVVM**: CommunityToolkit.Mvvm

## 项目结构

```
HD2Mimnt/
├── src/
│   ├── HD2Mimnt.Core/              # 核心层 - 实体和接口
│   │   ├── Entities/               # 领域实体
│   │   │   ├── Mod.cs
│   │   │   ├── Profile.cs
│   │   │   ├── ModTask.cs
│   │   │   └── DeploymentRecord.cs
│   │   ├── Interfaces/             # 仓储接口
│   │   │   ├── IModRepository.cs
│   │   │   ├── IProfileRepository.cs
│   │   │   └── ITaskRepository.cs
│   │   └── Enums/                  # 枚举类型
│   │       └── CoreEnums.cs
│   │
│   ├── HD2Mimnt.Infrastructure/    # 基础设施层 - 数据访问
│   │   ├── Data/
│   │   │   └── HD2MimntDbContext.cs
│   │   ├── Repositories/
│   │   │   ├── ModRepository.cs
│   │   │   └── ProfileRepository.cs
│   │   └── Services/
│   │
│   ├── HD2Mimnt.Application/       # 应用层 - 业务逻辑
│   │   ├── Services/
│   │   │   ├── ModLibraryService.cs
│   │   │   └── ProfileService.cs
│   │   ├── Commands/
│   │   └── DTOs/
│   │
│   └── HD2Mimnt.UI/                # 表示层 - Jalium.UI
│       ├── ViewModels/
│       │   ├── MainWindowViewModel.cs
│       │   ├── LibraryViewModel.cs
│       │   └── ProfileViewModel.cs
│       ├── Views/
│       ├── Services/
│       ├── App.axaml
│       ├── MainWindow.axaml
│       └── Program.cs
```

## 依赖关系

```
UI -> Application -> Core
        ↓
   Infrastructure -> Core
```

- **Core**: 不依赖任何项目，只包含实体、接口和枚举
- **Infrastructure**: 依赖 Core，实现数据访问
- **Application**: 依赖 Core 和 Infrastructure，实现业务逻辑
- **UI**: 依赖 Application，实现用户界面

## 核心概念

### 1. Mod Library
- Mod 是用户导入并管理的 Mod 资产
- 每个 Mod 有唯一的 Guid
- Mod 存储在 Library 中，由 ModRepository 管理

### 2. Profile
- Profile 是 Mod 使用配置
- 每个 Profile 包含多个 Mod 引用
- Profile 中的 Mod 可以启用/禁用，并有加载顺序
- 同一时刻只能有一个 Active Profile

### 3. Task Center
- 所有后台操作（Import、Scan、Deploy、Cleanup）都是 Task
- Task 有状态、进度、错误信息
- Task 历史记录用于追踪和诊断

### 4. Deployment
- 部署有三种方式：Copy、Symbolic Link、Hard Link
- DeploymentRecord 记录每次部署的详细信息
- DeploymentFile 记录每个文件的部署位置，用于精准清理

## 数据库设计

### 表结构

- **Mods**: 存储 Mod 元数据
- **Profiles**: 存储 Profile 配置
- **ProfileMods**: Profile 和 Mod 的多对多关系
- **Tasks**: 任务记录
- **DeploymentRecords**: 部署会话记录
- **DeploymentFiles**: 具体文件部署记录
- **Settings**: 应用设置

### 索引策略

- Mods: Name, Category
- ProfileMods: Order
- Tasks: Status, StartTime
- DeploymentRecords: SessionId, ModId
- DeploymentFiles: DeploymentId, ModId, TargetPath

## UI 设计原则

### 色彩系统
- **背景层级**: 
  - `#05070C` - 最深背景
  - `#0A0D12` - 主背景
  - `#0F131C` - 卡片背景
  - `#161D2B` - Hover 状态
  - `#1E2636` - 边框/分隔
- **强调色**: `#38BDF8` (青色)
- **文字颜色**:
  - `#E0E0E0` - 主要文字
  - `#9CA3AF` - 次要文字
  - `#6B7280` - 辅助文字

### 组件样式
- **按钮**: 完全圆角 (`CornerRadius="999"`)
- **卡片**: 圆角 6px
- **间距**: 8px、12px、16px、24px 的倍数
- **字体**: Inter (Avalonia 默认)

## 开发约定

### 命名规范
- **文件**: PascalCase (MyClass.cs)
- **类**: PascalCase
- **接口**: I 前缀 + PascalCase (IModRepository)
- **私有字段**: _camelCase
- **公共属性**: PascalCase
- **方法**: PascalCase + Async 后缀（异步方法）

### 异步编程
- 所有数据访问方法都是异步的
- 使用 CancellationToken 参数（默认值 default）
- 返回 Task 或 Task<T>

### MVVM 模式
- 使用 CommunityToolkit.Mvvm 的特性
  - `[ObservableProperty]` 自动生成属性
  - `[RelayCommand]` 自动生成命令
- ViewModel 不直接访问 Repository，通过 Service 层

### 错误处理
- Repository 层：抛出异常
- Service 层：捕获并处理异常，返回结果或 null
- UI 层：显示用户友好的错误消息

## 下一步开发计划

参见 `HD2Mimnt v2 总体开发计划.md` 中的 Phase 规划。

当前已完成：
- ✅ Phase 0: 项目初始化
- ✅ Phase 1: 项目骨架（基础架构）

待实现：
- Phase 2: Mod Library 完整功能
- Phase 3: Profile 管理
- Phase 4: Task Center
- Phase 5: Deployment & Cleanup
- ...

## 构建和运行

### 构建
```bash
dotnet build
```

### 运行
```bash
cd src/HD2Mimnt.UI
dotnet run
```

### 发布
```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## 数据库位置

Windows: `%LOCALAPPDATA%\HD2Mimnt\hd2mimnt.db`

## 参考项目

部分代码参考和学习自：`D:\TYHH10-git\Helldivers2ModManager`
