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

- 冲突检测比较实际选中补丁内的 `(Type ID, Resource ID)`，不能按原补丁名判断（部署会重编号）。文件集与补丁数值顺序复用 `PatchDeploymentPlanner`，模组顺序复用 `BslProfilePolicy`；同模组多次提供资源不算自冲突，预期生效者取实际部署末项。版本/主数据/Stream/GPU 大小相同只能说明潜在覆盖，不能证明内容相同或游戏兼容；损坏补丁单独列出并标记检测不完整。测试应生成真实 TOC，覆盖异名同资源、同名异资源、相同 ID 不同类型、正反部署方向及选项过滤。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition

- 断点续传不能把预分配文件长度当成已下载进度，按分段偏移持久化；先刷新数据到磁盘，再原子替换断点 JSON。恢复必须校验下载身份、远端长度与强 ETag / Last-Modified，签名 URL 更新按稳定文件版本键关联，不能明文保存 URL 凭据。同一断点禁止并发写入，回退前等待全部分段退出；下载设置按每次启动读取快照，独立下载 Tab 保存不依赖游戏路径校验。开启续传时保留中断数据，测试使用隔离目录并结束后清理。来源：https://www.rfc-editor.org/rfc/rfc9110.html#section-14

- HTTP 分段下载使用独立 `Downloads/HttpDownloadService`：先确认已知长度、Range 支持及强 ETag / Last-Modified，再带 If-Range 请求并严格校验 206 范围和长度；HTTP 200 回退前必须等待所有分段写入者退出。重试进度不能重复计数，取消/失败不能替换已有目标，临时文件流必须释放；下载客户端默认头禁止携带平台 API 凭据。测试未知长度时不能使用可定位 MemoryStream 的 StreamContent（.NET 会推导 Content-Length），应使用 TryComputeLength 返回 false 的 HttpContent。来源：https://www.rfc-editor.org/rfc/rfc9110.html#section-14

- BSL 扫描的正常取消使用协作式退出并将公开任务完成为 Canceled，不能返回成功的 None/null 或缓存识别版本；已知 GUID 快速路径及写入缓存前也必须检查取消。调试器的“抛出时中断”可能停在已处理的取消异常上，须区分调试中断与未处理崩溃，不能用吞掉取消异常掩盖问题。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 加载器 GUID 不是唯一识别依据：GUID 改变/缺失时以合法 TOC + Lua envelope 的独有资源入口兜底 MDL/SmoothBoot；BSL 必须同时匹配 Wwise 启动资源、`CowboyBingusModLoader`、`BingusSharedLoader.log`、`mods/cowboybingus/`，不能把 MDL 兼容 API 或其它 Wwise 替换认作 BSL。识别结果保存规则版本，规则升级后按需重扫旧缓存；失败/取消不标记识别成功。不执行 Lua、不按包名识别。来源：https://github.com/CowboyBingus/BingusSharedLoader/blob/main/src/shared_loader.lua

- Lua 分隔符是配置页固定的系统分组，不是伪造 Mod 或可拖动成员。规范优先级为普通模组 → Lua 依赖 → BSL / MDL（二选一）→ SmoothBoot；反向部署时固定区域反转置顶，SmoothBoot 始终最后部署。实际部署及冲突顺序共用 `BslProfilePolicy`。BSL GUID `612eaf70-d682-43c7-9efd-16dcc695f977`、MDL GUID `761188b2-c45b-4607-9241-f89ca221c47b`、SmoothBoot GUID `7c3e9a52-1b84-4f6a-9c2d-8e5f0a1b2c3d` 的特殊身份优先于通用依赖描述；MDL 与 SmoothBoot 的 Lua 入口为 `mods/mdl/runtime`、`mods/codex/smoothboot`。启用一个加载器时在同一次配置写入中停用其它加载器；批量同时选择多个加载器拒绝写入，部署预检再次拦截旧配置中的互斥状态。只依据 provenance `requires`、明确声明及资源契约识别依赖，不能按名称、作者或补丁前缀猜测；清单 `Version: 1` 是格式版本。依据为用户提供的 MDL 2.0.5 / SmoothBoot 3.0.22 包清单与资源及明确排序要求；通用 BSL 契约来源：https://github.com/CowboyBingus/BingusSharedLoader/blob/main/docs/AUTHORING.md

- 外部平台按独立模块目录组织，例如 `Infrastructure/Platforms/NexusMods/`；公开适配器实现 `IModPlatform` 并通过 `PlatformRegistry` 调用，HTTP 客户端、平台模型、映射和文件流程按职责拆分，内部类型保持 `internal`。新增 A 网等平台使用自己的目录、认证状态和 API 模型，不向 N 网文件追加其它网站逻辑，也不把平台 DTO 泄漏到 Application/UI。来源：https://learn.microsoft.com/dotnet/csharp/fundamentals/types/interfaces

- Nexus Mods v3 以 `docs/openapi.yaml` 为契约：网站 `game_scoped_id`、模组全局 `id`、文件 `id` 和文件版本 `id` 不可混用。读取接口用 `data` 包装，但 `download-repacked` 的 POST 响应直接返回 `download_url`；下载优先活动主版本，API 凭据不能放入共享默认头并随签名下载地址发送。热门接口仅前 5 项，不能称为全站搜索；详情不含版本号，更新检查须查询文件版本，空版本不能制造更新提示。来源：https://api-docs.nexusmods.com/

- SharpCompress `ReaderFactory` 的流式格式不包含 7z；先按内容签名检测 7z 并重置流位置，再用 `ArchiveFactory.OpenArchive` + `ExtractAllEntries`，其它格式保留 ReaderFactory 以支持 tar.gz 等复合流。格式支持测试必须实际生成/读取该格式，不能用 tar.gz 通过来代表 7z 已支持，也不能只凭扩展名选择读取器。来源：https://github.com/adamhathcock/sharpcompress/blob/master/docs/FORMATS.md

- 外部文件导入拖放统一在 MainWindow 冒泡路由处理并启用 handledEventsToo（DragDrop 事件不走隧道路由），先识别本地文件再标记 Handled，避免页面子控件吞掉事件或内部排序覆盖 Copy；根容器使用透明背景覆盖空白区域。提示层不参与命中测试，DragLeave 只有窗口坐标越界才隐藏，不能因子控件之间移动反复显隐；无文件数据继续交给内部排序。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events

- 弹出通知的计时器随视图挂载/卸载启动和停止，横幅收起不能删除通知历史；后台通知事件先派发到 UI 线程。Avalonia 位移动画使用已创建的 `TranslateTransform` 和 `TranslateTransform.X`，不要直接对 `RenderTransform` 写关键帧，否则可能编译通过但运行时报无 Animator；必须用 Headless 实际显示通知验证。来源：https://docs.avaloniaui.net/docs/guides/graphics-and-animation/keyframe-animations

- 调整顶部导航顺序时必须同步 FluentAvalonia `FANavigationView.MenuItems`、`MainWindow` 的选择事件映射、`MainWindowViewModel.SelectedTabIndex`、快捷导航命令和隐藏 `TabControl` 的页面顺序；只修改 XAML 标签会导致按钮与页面错位。
- UI 只通过 Application 服务访问模组、配置文件和部署数据；不要在 View 或 ViewModel 中直接访问 SQLite、部署实现或复制文件。
- 启动与备份恢复先执行 `ProfileService.InitializeAsync`，默认配置使用数据库 `IsDefault` 标记，服务层禁止删除、排序固定首位；旧库沿用最早配置并保留内容。`IsActive` 在切换时立即落库，启动不能无条件激活默认配置，否则覆盖用户上次使用的配置。
- 冲突检查跟随配置文件页选中的配置并显示名称；模组、配置或选项变动后取消旧扫描并使结果失效。扫描必须复用部署的 `ProfileModOptionResolver` 与 Include 文件收集逻辑，应用 Enabled、OptionsJson、选项目录拍平目标路径和部署顺序，不能直接遍历所有变体制造误报。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation
- Avalonia 的 `ObservableCollection` 和控件状态只能在 UI 上下文更新。后台筛选/排序先计算普通 `List`，回到 UI 上下文后再替换集合。
- 导入、部署、清理等耗时操作必须可取消并保持 UI 可响应；临时解压目录和测试目录必须在流程结束后清理。
- 模组启用状态与部署状态是两个独立状态。删除模组时同步清理对应部署记录，不能只删除数据库中的模组行。
- 删除模组前必须先按 DeploymentFiles.TargetPath 清理游戏文件，成功后才删除部署记录和模组；文件清理失败或取消保留记录以供重试，不能直接调用 DeleteByModIdAsync 丢失追踪。清理使用每个文件的 Method（旧空值才回退会话方式），链接部署的普通占位文件用 Copy；符号链接源文件缺失时仍要删除链接本身，不能仅用 File.Exists 判断。来源：https://learn.microsoft.com/dotnet/api/system.io.fileinfo.linktarget
- 删除已部署模组后按现有游戏目标编号保留实际部署顺序，为每个资源前缀独立从 patch_0 补齐连续编号；主文件、stream、gpu_resources 和占位文件一起移动，库内原文件不改名。改名完成后在同一数据库事务中更新剩余 TargetPath 并删除被移除模组的部署记录；移动或提交失败逆序回滚改名，不能覆盖未知目标文件或留下追踪旧路径的记录。来源：https://learn.microsoft.com/dotnet/api/system.io.file.move
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
- 运行中的 UI 或 Visual Studio 锁住常规输出时，可用 `dotnet build HD2Mimnt.slnx --no-restore -m:1 -p:OutputPath=<独立临时绝对目录>/` 保留现有 obj 并隔离输出；随后 `dotnet test <测试项目> --no-build --no-restore -p:OutputPath=<同一目录>/ --filter <相关用例>` 顺序运行。确认输出中的测试程序集路径与实际通过数量，不能将无测试执行的 exit 0 当作通过；完成后删除该临时目录。来源：https://learn.microsoft.com/dotnet/core/tools/dotnet-test
- Avalonia `CheckBox` 如果绑定了“切换状态”的命令，`IsChecked` 应使用 `Mode=OneWay`，由命令完成持久化并更新 ViewModel；双向绑定会先写一次状态、命令再反转一次，造成 UI 与 Profile 数据库相反。
- 设置页保存的运行时前置条件必须同步到对应 Application 服务；例如游戏路径不能只写 `SettingsService`，还要通过 `GameDetectionService` 校验并更新当前状态，否则部署页仍会读取旧检测结果。
- 任务中心或其它覆盖层的自动刷新必须有明确的 Start/Stop 生命周期；关闭面板和应用退出时取消刷新令牌，并串行化加载，不能留下后台轮询或让旧快照覆盖新状态。
- 任务完成事件必须在最终状态写入数据库并从运行任务集合移除后发布；UI 通知处理器只负责呈现结果和刷新视图，不能在事件回调中重新执行任务状态持久化。
- 强制终止任务必须与后台执行体的最终写入使用同一任务级闸门；先标记并持久化 `ForceTerminated`，后台取消回调不能覆盖终止状态，也不能重复发布完成事件。
- 后台服务事件（游戏检测、通知、任务完成等）不能直接修改 Avalonia 绑定属性；事件处理器必须检查 UI 线程并通过 Dispatcher 派发，测试无 UI 上下文时才允许同步回退。
- 设置页的“自动识别游戏”必须同时约束启动流程和保存后的检测流程；关闭时只能应用用户配置路径，不能仍然调用会扫描常见目录的 `AutoDetectAsync`。
- 开发工具生成测试模组必须通过 Application 层服务完成导入和删除；ViewModel 不得直接创建 manifest、复制模组或删除模组目录，临时源目录必须按每项完成/失败清理。
- 开发工具的批量生成、启用状态和清理命令必须传递 CancellationToken，并提供可见取消入口；Application 服务在每项操作前检查取消并保证临时源目录清理。
- UI 统一使用 FluentAvaloniaTheme，不能再叠加 Avalonia FluentTheme。Avalonia 12.0.3 对应 FluentAvaloniaUI 3.0.2；3.1.0 要求 Avalonia 12.1.0，升级时必须同步 UI 和 Headless 版本。来源：https://www.nuget.org/packages/FluentAvaloniaUI/3.0.2
- 页面色彩、圆角和间距集中到 `Styles/AppResources.axaml`、`Styles/AppStyles.axaml`；主要按钮使用组件库的 `accent` 类，图标使用 `UtilityIcon`（FluentAvalonia FASymbolIcon）。不要复制页面调色板、文字/emoji 图标或基础控件状态模板。来源：https://github.com/amwx/FluentAvalonia
- 运行时背景透明度只更新 `CardSurfaceOpacity`、`ChromeSurfaceOpacity`、`ContentSurfaceOpacity`，不能用新画刷覆盖动态资源，否则跟随系统切换主题时颜色会失联。
- 模态弹窗即使复用 `card` 样式，也必须显式使用不透明的主题背景（如 `{DynamicResource Surface2Brush}`），避免受卡片透明度设置影响而透出底层文字；验证低透明度及深浅主题切换。来源：https://docs.avaloniaui.net/docs/guides/styles-and-resources/resources
- 图片占位图标的可见性使用 `ObjectConverters.IsNull` 判断图片对象；不要将 `!Icon` 与 `NotNullConverter` 组合，布尔结果仍然非空，会导致占位图标覆盖已加载图片。来源：https://docs.avaloniaui.net/docs/reference/built-in-data-binding-converters
- 拖放排序预览必须与 Drop 共用插入位置计算，覆盖层设置 IsHitTestVisible=False 且不参与列表布局；DragLeave 可能从子控件冒泡，只有指针真正离开列表视口才隐藏预览，避免鼠标跨过文字/图片时闪烁。取消、完成、DataContext 变化和视图卸载都要恢复拖动源透明度。来源：https://docs.avaloniaui.net/docs/concepts/input/routed-events
- Application 导入服务创建 Mod 时必须显式赋值 `ImportedAt`，初始创建/修改/导入时间使用同一 UTC 时间；实体默认日期不是有效导入时间。详情页转成本地时间显示，旧记录缺失时显示“未记录”，不能用修改时间冒充导入时间。来源：https://learn.microsoft.com/dotnet/api/system.datetime.tolocaltime
- 枚举类型的 RelayCommand 必须用 `{x:Static vm:枚举类型.成员}` 传递 CommandParameter，不能传同名字符串，否则 CanExecute 会禁用按钮；命令驱动的筛选 RadioButton 选中状态使用 OneWay，避免转换器反写枚举属性。来源：https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand
- 模组库分类按 HD2SDK 的资源 Type ID 匹配，使用 `ResourceTypeCatalog` 与 `Mod.ResourceTypeIds`，不要按旧 `ModCategory`、中文名称或预览图扩展名猜测。一个模组可包含多种类型；未知 ID 保留原值。Type ID 使用 `ulong`，SQLite 持久化为固定 16 位十六进制字符串 JSON，不能转成有符号整数或浮点数。旧库只增加列，保留原分类和用户标签。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/blob/main/utils/constants.py
- 读取 Stingray TOC 分类只需索引：magic `0xF0000011`，72 字节头部、32 字节类型记录、80 字节资源记录，资源 Type ID 位于资源记录偏移 8；先校验索引长度，再读取，保持只读并检查取消。未扫描用 null，已扫描无类型用空列表，损坏索引失败不能缓存成成功。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition
- Avalonia 12 的 `Avalonia.Headless.XUnit` 依赖 xUnit v3，与现有 xUnit v2 单元测试分项目运行；UI 测试放在 `HD2Mimnt.UITests`，通过 `AvaloniaTestApplication` 复用宿主 UI 线程，不能给旧测试程序集引入第二套 xUnit。来源：https://www.nuget.org/packages/Avalonia.Headless.XUnit/12.0.3
- Headless 多用例合并运行若出现 `Cannot get KeyValueStorage on the idle test context`，先逐用例分进程、顺序运行以区分宿主上下文错误与业务断言失败；不得将隔离通过表述为合并运行已修复，也不要通过再创建 AppBuilder 绕过宿主。当前 Avalonia.Headless.XUnit 12.0.3 / xUnit v3 3.2.2 组合已复现此问题。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless.XUnit/AvaloniaTestRunner.cs
- 模组添加按钮和外部拖放共用导入浮窗及批次入口。阶段进度由 Application 服务报告，未知耗时步骤显示不确定进度；剩余数量包含当前未完成项。排队的 UI 进度回调须校验批次/文件是否仍有效，不能覆盖完成或取消结果；取消不得被通用异常处理吞掉，未落库副本须清理，已落库模组不能因缩略图取消被算作失败。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation
- 模组库普通单击只聚焦是明确的产品设计，不要改成单击进入批量选择；但必须记录范围锚点。Shift 替换范围且连续操作保留锚点，Ctrl+Shift 追加范围，Ctrl 增减选择并更新锚点；Ctrl+A 始终全选，与工具栏的全选切换按钮区分。交互按钮和文本输入不能触发卡片选择，验证须覆盖实际鼠标/键盘事件。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox
- 模组详情 Hash 由 Application 的 `ModHashService` 计算：规范化相对路径按 Ordinal 排序，以路径长度前缀和完整文件 SHA-256 摘要生成目录指纹；不能用文件名/大小或首尾快速指纹冒充内容哈希。新导入在落库前计算并支持取消；旧记录只在打开详情时补算空值并保存，不能在加载列表时扫描整个库。来源：https://learn.microsoft.com/dotnet/api/system.security.cryptography.incrementalhash
- 配置页部署必须显式传入正在查看的配置 ID，并读取已保存的 `Deployment.DefaultMethod`；新增 ViewModel 依赖时同步修改 `App.ConfigureServices` 中的手动构造工厂，不能只改构造函数。回归测试要通过实际 DI 和按钮绑定验证，避免手动构造掩盖漏接依赖。
- 配置页模组卡片的顺序数字使用 1-based 显示；右键“移动到指定位置”应保留当前多选集，输入位置后复用 `CanMoveMods`/`MoveModsAsync`，继续校验 Lua 依赖与加载器的固定区域，不能直接改集合绕过持久化。
- 数字定位转换落点时，正向列表普通模组区域末尾要映射为 `Guid.Empty` 分隔符锚点，不能用第一项 Lua 模组 ID，否则合法的区域内移动被误拒绝；反向列表末尾保留 null 锚点。回归覆盖含 Lua 分组的单选、多选、正反向和越界拒绝。
- Lua 分隔符在两种方向下都位于普通模组与固定区域之间：正向为普通 → 分隔符 → Lua 依赖 / 加载器 / SmoothBoot，反向为 SmoothBoot / 加载器 / Lua 依赖 → 分隔符 → 普通，不能将反向分隔符索引写死为 0。显示、拖放校验、实际移动和数字定位共用分组边界；反向依赖区域末尾使用 `Guid.Empty`，普通区域末尾使用 null。搜索后按可见成员计算分隔符位置。来源：https://docs.avaloniaui.net/docs/concepts/input/drag-and-drop
- 配置页普通模组和 Lua 依赖模组支持常规多选；按用户要求，BSL、MDL、SmoothBoot 和分隔符不可选中。点击、范围选择、Ctrl+A 共用 `CanSelect`，范围选择和全选必须跳过固定卡片；交互控件保留自身输入行为，跨分组的多选集仍禁止排序。来源：https://docs.avaloniaui.net/docs/reference/controls/listbox
- 当前 FluentAvaloniaTheme 下右键菜单使用 `FAMenuFlyout` / `FAMenuFlyoutItem`；原生 `ContextMenu` 的 IsOpen 为 true 不代表菜单可见，必须实际验证条目布局和鼠标点击。模组列表统一捕获右键冒泡事件并启用 handledEventsToo，兼容子控件吞掉事件。顺序号使用版本号前的独立 chip，不能依赖缩略图占位文字。来源：https://github.com/amwx/FluentAvalonia
- 游戏路径是安装根目录，补丁部署目标固定为其 `data` 子目录。只读取根目录或选中 Include 目录直属的合法补丁，不能递归部署未选变体或复制 manifest/预览图。三元组按资源前缀和配置顺序从 0 重编号，保留目录原编号并集的空文件占位语义；占位文件必须先解除旧链接再创建，且所有目标在写入前登记，以支持取消后的清理。旧版 HD2PhysBone 兼容骨骼功能已废弃。来源：https://learn.microsoft.com/windows/win32/fileio/hard-links-and-junctions

- 教程等跨页面聚焦覆盖层应通过真实控件坐标定位，等待页面布局后再高亮，并在缩放时重新定位；覆盖层阻止底层鼠标、快捷导航和外部文件导入，退出后恢复页面与分类。模糊效果的视觉验收使用 Headless 的 CaptureRenderedFrame（实际合成渲染）；RenderTargetBitmap.Render 的静态截图不能代替 Effect 的实际显示验证。来源：https://github.com/AvaloniaUI/Avalonia/blob/12.0.3/src/Headless/Avalonia.Headless/HeadlessWindowExtensions.cs

- 所有ui基本上都要采用这个FluentAvalonia组件库保证视觉统一性

- 配置页多选快捷键需接入 MainWindowViewModel.CloseOverlays 的 Escape 入口：窗口 KeyBinding 会先处理按键，不能只在卡片列表挂 Escape 事件。覆盖层优先关闭，普通配置页才清空选择；Ctrl+A 避开文本框。普通点击已选卡片在松开且未启动拖动时收起为单选，拖动仍保留整组选中；全选切换与反选只作用于可见 CanSelect 成员，清空操作同时重置所有成员和范围锚点。来源：https://docs.avaloniaui.net/docs/concepts/input/hotkeys

- ViewModel 自行管理批量选择时，不能把 FocusedMod 绑定到原生 ListBox.SelectedItem，也不能让原生列表在筛选时清空持久的选中集合。模组库与配置页使用 ItemsControl + 共享 ViewModelSelectableListBoxItemTheme（继承 FluentAvalonia 原生 ListBoxItem），IsSelected 由 ViewModel 单向驱动；鼠标/键盘在原生输入前处理并保留可聚焦的列表。回归必须同时检查选中集合和实际容器 IsSelected，并覆盖搜索后容器重建；只断言 SelectedCount 无法发现视觉失效。ControlTheme 放入 ResourceDictionary，通用 IsSelected 使用 ReflectionBinding 避免绑定到单一 ViewModel 类型。来源：https://docs.avaloniaui.net/docs/basics/user-interface/styling/control-themes
