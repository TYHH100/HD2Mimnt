# AGENTS.md

This file provides guidance to Codex (Codex.ai/code) when working with code in this repository.

## Project Overview

HD2Mimnt is a Helldivers 2 Mod Manager built with .NET 10, Avalonia UI, and SQLite. It provides mod library management, profile-based configurations, multiple deployment methods (Copy, Symbolic Link, Hard Link), workflow system for different mod formats, and plugin architecture for extensibility.

**Architecture**: Clean Architecture with 4 main layers:
- **Core** (`HD2Mimnt.Core`): Domain entities, interfaces, enums
- **Infrastructure** (`HD2Mimnt.Infrastructure`): Data access (SQLite), deployment methods, workflows, platform adapters
- **Application** (`HD2Mimnt.Application`): Business logic services (ModService, ProfileService, DeploymentService, TaskService, etc.)
- **UI** (`HD2Mimnt.UI`): Avalonia-based MVVM presentation layer

**Additional Projects**:
- `HD2Mimnt.PluginSDK`: Plugin development SDK (interfaces: IPlugin, IModPlugin, IDeploymentPlugin, IAnalysisPlugin)
- `HD2Mimnt.PluginHost`: Isolated plugin execution host (separate process)
- `HD2Mimnt.Worker`: Background worker process for long-running tasks

## Build and Test Commands

```bash
# Build the entire solution
dotnet build

# Build specific project
dotnet build src/HD2Mimnt.UI/HD2Mimnt.UI.csproj

# Run the application
cd src/HD2Mimnt.UI
dotnet run

# Run all tests
dotnet test

# Run tests with detailed output
dotnet test --verbosity normal

# Run specific test class
dotnet test --filter "FullyQualifiedName~ProfileServiceTests"

# Run single test method
dotnet test --filter "FullyQualifiedName~ProfileServiceTests.CreateProfile_ShouldSucceed"

# Run benchmarks
cd benchmarks/HD2Mimnt.Benchmarks
dotnet run -c Release

# Run stability tests
cd tests/HD2Mimnt.StabilityTests
dotnet test

# Publish for Windows x64
dotnet publish src/HD2Mimnt.UI/HD2Mimnt.UI.csproj -c Release -r win-x64 --self-contained
```

## Architecture Highlights

### Deployment System
The deployment system supports three methods with complete file tracking:
- **Copy**: Traditional file copying
- **SymbolicLink**: Windows symbolic links (requires admin)
- **HardLink**: NTFS hard links (same volume only)

Each deployment creates a `SessionId` for batch operations. The `DeploymentService` handles:
- Pre-deployment cleanup (removes previous session files)
- Profile deployment (deploys all enabled mods in order)
- File-level tracking via `DeploymentRecord` and `DeploymentFile` entities
- Cleanup operations using the appropriate method for each file

### Workflow System
Workflows define how different mod formats are parsed, validated, and deployed:
- **LegacyWorkflow**: Original format with dropdown options (manifest v0)
- **V1Workflow**: Modern format with independent toggles (manifest v1)
- **IntegratedArmorWorkflow**: Specialized armor mod format with variants

Workflows implement `IWorkflow` and are managed by `WorkflowRegistry`. The `ManifestParser` service handles JSON parsing.

### Plugin Architecture
Plugin system uses isolated processes for safety:
- Plugins implement `IPlugin` (base), `IModPlugin`, `IDeploymentPlugin`, or `IAnalysisPlugin`
- PluginHost runs in separate process, communicates via IPC
- Plugins have manifest-based permissions (filesystem, network, database)
- Sandbox isolation prevents plugins from affecting main application

### Task System
All long-running operations are tracked through the `TaskService`:
- Import, Scan, Validation, Deployment, Cleanup tasks
- Progress tracking with stages and percentages
- Cancellation support via CancellationToken
- Workers (`ScanWorker`, `ValidationWorker`, `ImportWorker`) execute background operations

### Platform Integration
External mod platforms (Nexus Mods, etc.) are abstracted via `IModPlatform`:
- Search, download, version checking
- Platform-specific metadata
- `PlatformRegistry` manages available adapters
- Currently includes `MockPlatformAdapter` for testing

## Key Design Patterns

### Repository Pattern
All data access goes through repository interfaces:
- `IModRepository`, `IProfileRepository`, `ITaskRepository`, `IDeploymentRepository`
- Concrete implementations in Infrastructure layer use SQLite
- Enables testing with in-memory implementations

### Service Layer
Business logic lives in Application services:
- `ModService`: Mod CRUD, import, search
- `ProfileService`: Profile management, mod assignment
- `DeploymentService`: Deployment orchestration and cleanup
- `TaskService`: Task lifecycle management
- `SearchService`: Full-text search across mods
- `ResourceExplorerService`: Mod resource dependency analysis

### Dependency Injection
The application uses Microsoft.Extensions.DependencyInjection. Services are registered in the UI project's Program.cs or App.axaml.cs.

### Image Handling
- `ImageLoaderService`: Loads images from disk/resources
- `ImageCacheService`: LRU cache for thumbnail optimization
- `ThumbnailService`: Generates thumbnails from mod icons

## Database Schema

SQLite database with core tables:
- `Mods`: Mod library (Id, Name, Path, Version, Author, etc.)
- `Profiles`: User configurations
- `ProfileMods`: Many-to-many relationship (ProfileId, ModId, Enabled, Order)
- `Tasks`: Task tracking
- `DeploymentRecords`: Deployment sessions (SessionId, ProfileId, ModId, Method, Status)
- `DeploymentFiles`: Individual file tracking (DeploymentId, SourcePath, TargetPath, Method)

## Testing

Test framework: xUnit with Moq for mocking
- Unit tests: `tests/HD2Mimnt.Tests/`
- Benchmarks: `benchmarks/HD2Mimnt.Benchmarks/` (BenchmarkDotNet)
- Stability tests: `tests/HD2Mimnt.StabilityTests/` (long-running scenarios)

When writing tests:
- Use InMemory database for repository tests
- Mock external dependencies (IModRepository, ILogger, etc.)
- Follow existing test patterns (arrange-act-assert)
- Test files use descriptive method names: `MethodName_Scenario_ExpectedResult`

## Code Conventions

- **Nullable reference types** enabled project-wide
- **ImplicitUsings** enabled
- Target framework: `net10.0`
- Use async/await consistently; append `Async` suffix to async methods
- CancellationToken parameters default to `default`
- Use `ILogger<T>?` with null-conditional for logging (logger is optional)
- Entity IDs are `Guid`, timestamps are `DateTime` (UTC)
- Enums are in `HD2Mimnt.Core.Enums` namespace

## Important Paths and Entry Points

- Main entry: `src/HD2Mimnt.UI/Program.cs`
- Main window: `src/HD2Mimnt.UI/MainWindow.axaml` + `.cs`
- ViewModels: `src/HD2Mimnt.UI/ViewModels/`
- Core entities: `src/HD2Mimnt.Core/Entities/`
- Service implementations: `src/HD2Mimnt.Application/Services/`

## Common Patterns

### Deployment Workflow
1. Call `PreDeploymentCleanupAsync()` to remove previous session
2. Call `DeployProfileAsync()` with method (Copy/SymbolicLink/HardLink) and order
3. Service creates new SessionId, deploys enabled mods in order
4. Each file deployment creates `DeploymentFile` record
5. Cleanup uses tracked records to reverse deployment

### Task Creation
1. Create `ModTask` entity with Name, Type, Status
2. Add to database via `TaskService.CreateTaskAsync()`
3. Update progress via `UpdateTaskProgressAsync()`
4. Update status via `UpdateTaskStatusAsync()` when complete

### Adding a New Workflow
1. Create class implementing `IWorkflow` in `Infrastructure/Workflows/`
2. Implement `CanHandleAsync()` detection logic
3. Implement `ParseManifestAsync()` for manifest parsing
4. Implement `ValidateAsync()` for structure validation
5. Implement `GetDeploymentFilesAsync()` for file mapping
6. Register in `WorkflowRegistry`

## Current Status

Version: 0.1.0-alpha
Recent phases completed: Phase 22 (Release preparation), Phase 21 (Stability testing), Phase 20 (Performance testing), Phase 19 (AI Integration), Phase 18 (External platforms), Phase 17 (Plugin system)

All core systems are functional. The application is in early alpha with ongoing UI and feature development.

## Codex 工作注意事项

- UI 只通过 Application 服务访问模组、配置文件和部署数据；不要在 View 或 ViewModel 中直接访问 SQLite、部署实现或复制文件。
- Avalonia 的 `ObservableCollection` 和控件状态只能在 UI 上下文更新。后台筛选/排序先计算普通 `List`，回到 UI 上下文后再替换集合。
- 导入、部署、清理等耗时操作必须可取消并保持 UI 可响应；临时解压目录和测试目录必须在流程结束后清理。
- 模组启用状态与部署状态是两个独立状态。删除模组时同步清理对应部署记录，不能只删除数据库中的模组行。
- 修改后先顺序执行 `dotnet build HD2Mimnt.slnx --no-restore` 和相关测试；共享 SQLite 输出的测试程序集已禁用类级并行，避免全局池清理互相干扰。
- 仅在实际需要时添加文档和测试，不用占位页面或“Coming Soon”文案冒充完成。
- 测试异步进度回调时不要用默认 `Progress<T>` 后立即断言，使用同步测试进度实现或显式等待回调完成。
- 可取消的排队任务要在后台执行体开始时再次检查状态，避免取消后被异步启动逻辑恢复为运行中。
- TaskService 的进度回调不能用并发异步写库；同一任务的进度、完成和取消状态必须串行持久化，避免 SQLite 行状态被旧进度覆盖。
- 配置文件批量启用/禁用必须在 ProfileService 内一次读取、一次更新；启用不在 ProfileMods 中的模组要先加入并分配稳定顺序，不能在 UI 中逐条提交。
- 开发工具批量生成启用状态也必须调用批量接口，不能通过 AddModToProfileAsync 与 SetModEnabledAsync 的逐条组合模拟批量操作。
- 部署清理只有在所有目标文件成功清理后才能删除 DeploymentRecord；任一文件失败必须保留 DeploymentFiles 和记录供重试，不能用会话级删除覆盖失败状态。
- 部署清理的最终进度消息要带 SessionId，便于任务中心把结果与失败恢复记录对应起来；测试断言应同时覆盖前缀和会话标识。
- BenchmarkDotNet 会生成额外产物；基准完成后清理临时目录，并在报告中记录实际数据集规模和测量边界。
- UI 大数据验收使用 `HD2MIMNT_DATA_PATH` 隔离目录；开发工具测试模组必须有稳定的 `HD2Mimnt Test ` 名称前缀，清理时按前缀匹配，不能按用户模组路径或模糊目录名删除；导入源目录必须在每项完成或失败后清理。
- 性能采样脚本默认输出到 `%TEMP%\HD2MimntPerformance`；短时启动样本只能验证采样链，长时验收必须记录持续时间、采样间隔、数据集和 UI 操作范围，完成后删除临时输出。
- 托管堆采样只在显式设置 `HD2MIMNT_METRICS_PATH` 时启用，使用 `GC.GetTotalMemory(false)` 记录；不要在普通运行路径中无条件启动诊断定时器。
- 备份必须通过 `BackupService` 生成临时 SQLite 快照后再压缩，恢复前必须校验路径位于备份目录，并在覆盖当前数据库前要求确认。
- 日志页面只能读取有限的尾部记录，文件日志读取要允许共享写入；不要一次性把整个日志文件加载进 UI 内存。
- 插件 manifest 的解析和校验属于 `IPluginService`，View 只负责文件选择和权限确认，不要在 code-behind 中复制安装逻辑。
- SQLite 上下文的连接初始化和释放必须受生命周期闸门保护；不要在生产 `DbContext.Dispose` 中调用全局 `ClearAllPools` 或强制 GC，否则会干扰并行测试和其他上下文的连接。
- Avalonia Headless 测试在同一进程只能进行一次 `AppBuilder.SetupWithoutStarting()`；新增窗口布局测试必须复用现有测试应用或拆分到独立测试程序集，不能再创建第二个 AppBuilder，否则会出现初始化冲突、资源缺失或测试宿主不退出。
- Avalonia Headless 测试的 `AppBuilder.SetupWithoutStarting()` 必须在测试宿主拥有的 UI 线程上初始化；若测试程序集并行发现或从错误线程触发静态初始化，可能出现 `The calling thread cannot access this object because a different thread owns it`，应先隔离该测试程序集/关闭并行后再判断业务代码是否回归。
- 参考旧版 WPF 前端时只迁移可验证的业务行为语义（例如筛选列表中的范围选择），必须继续通过当前 UI 的 Application 服务和 ViewModel 对接；不要直接复制旧 UI 或绕过当前 Infrastructure/数据库边界。
- 共享 `bin/obj` 输出的构建和测试必须顺序执行；并行启动会锁定 Core 或 UI 程序集，产生误导性的 CS2012 文件占用错误。
- Avalonia `CheckBox` 如果绑定了“切换状态”的命令，`IsChecked` 应使用 `Mode=OneWay`，由命令完成持久化并更新 ViewModel；双向绑定会先写一次状态、命令再反转一次，造成 UI 与 Profile 数据库相反。
- 设置页保存的运行时前置条件必须同步到对应 Application 服务；例如游戏路径不能只写 `SettingsService`，还要通过 `GameDetectionService` 校验并更新当前状态，否则部署页仍会读取旧检测结果。
- 任务中心或其它覆盖层的自动刷新必须有明确的 Start/Stop 生命周期；关闭面板和应用退出时取消刷新令牌，并串行化加载，不能留下后台轮询或让旧快照覆盖新状态。
- 任务完成事件必须在最终状态写入数据库并从运行任务集合移除后发布；UI 通知处理器只负责呈现结果和刷新视图，不能在事件回调中重新执行任务状态持久化。
- 强制终止任务必须与后台执行体的最终写入使用同一任务级闸门；先标记并持久化 `ForceTerminated`，后台取消回调不能覆盖终止状态，也不能重复发布完成事件。
- 后台服务事件（游戏检测、通知、任务完成等）不能直接修改 Avalonia 绑定属性；事件处理器必须检查 UI 线程并通过 Dispatcher 派发，测试无 UI 上下文时才允许同步回退。
- 设置页的“自动识别游戏”必须同时约束启动流程和保存后的检测流程；关闭时只能应用用户配置路径，不能仍然调用会扫描常见目录的 `AutoDetectAsync`。
- 开发工具生成测试模组必须通过 Application 层服务完成导入和删除；ViewModel 不得直接创建 manifest、复制模组或删除模组目录，临时源目录必须按每项完成/失败清理。
- 开发工具的批量生成、启用状态和清理命令必须传递 CancellationToken，并提供可见取消入口；Application 服务在每项操作前检查取消并保证临时源目录清理。
