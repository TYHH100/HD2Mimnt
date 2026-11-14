# 项目基础与常用命令

首次定位项目结构、增加服务/工作流或查找入口时读取本文件。业务易错点按根目录 [AGENTS.md](../../AGENTS.md) 的索引读取；版本号以根目录 `Directory.Build.props` 的 `<Version>` 为唯一来源，不把旧阶段总结当成实时状态。

文档统一从 [文档导航](../README.md) 查找。模块说明、契约、计划、实测报告与历史归档按用途存放；移动文档时更新相对链接并检查目标存在，不能只移动文件留下旧入口。维护分类见导航中的“文档维护”。来源：[GitHub 相对链接](https://docs.github.com/en/get-started/writing-on-github/getting-started-with-writing-and-formatting-on-github/basic-writing-and-formatting-syntax#relative-links)。

## 架构与入口

HD2Mimnt 是基于 .NET 10、Avalonia 和 SQLite 的 Helldivers 2 模组管理器，采用 MVVM 和分层架构。

| 项目 | 职责 |
| --- | --- |
| `HD2Mimnt.Core` | 领域实体、接口、枚举 |
| `HD2Mimnt.Infrastructure` | SQLite Repository、部署实现、工作流、平台适配器 |
| `HD2Mimnt.Application` | 业务服务与流程编排 |
| `HD2Mimnt.UI` | Avalonia View、ViewModel、DI 注册 |
| `HD2Mimnt.Cli` | 命令行入口：转发到运行中的 UI，或在 UI 未运行时无头执行 |
| `HD2Mimnt.PluginSDK` / `PluginHost` / `PluginLua` | 插件接口、独立进程宿主与 IPC、Lua 脚本运行时（MoonSharp，只在宿主进程引用） |
| `HD2Mimnt.Worker` | 后台工作进程 |

- UI 入口：`src/HD2Mimnt.UI/Program.cs`；DI 注册检查 `App.axaml.cs` 中的 `ConfigureServices`，尤其手动构造工厂。
- 主窗口：`src/HD2Mimnt.UI/MainWindow.axaml` 及 `.cs`；页面模型在 `ViewModels/`。
- 当前导入入口是 Application 的 `ModImportService`，部署为 `DeploymentService`，配置激活为 `ProfileService.SetActiveProfileAsync`。未接入的 Core 旧导入器、`UnifiedDeploymentService` 和 `ProfileSwitchService` 已移除；归档阶段报告不代表当前调用链。
- 业务服务：`src/HD2Mimnt.Application/Services/`；实体在 `src/HD2Mimnt.Core/Entities/`，枚举在 `HD2Mimnt.Core.Enums`。
- 数据访问通过 `IModRepository`、`IProfileRepository`、`ITaskRepository`、`IDeploymentRepository` 等接口完成。
- `ModService` 管理模组，`ProfileService` 管理配置及成员，`DeploymentService` 编排部署/清理，`TaskService` 管理任务，搜索与资源分析由各自服务负责。
- 图片加载、缓存、缩略图分别由 `ImageLoaderService`、`ImageCacheService`、`ThumbnailService` 负责。

## 命令与测试项目

在仓库根目录执行；构建和测试顺序运行，输出锁定与 Headless 限制见 [测试指南](testing-and-lifecycle.md)。

```powershell
dotnet build HD2Mimnt.slnx --no-restore
dotnet build src/HD2Mimnt.UI/HD2Mimnt.UI.csproj --no-restore
dotnet run --project src/HD2Mimnt.UI/HD2Mimnt.UI.csproj
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~ProfileServiceTests"
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build --no-restore --filter "FullyQualifiedName~目标用例"
dotnet publish src/HD2Mimnt.UI/HD2Mimnt.UI.csproj -c Release -r win-x64 --self-contained
```

- 单元测试在 `tests/HD2Mimnt.Tests/`，使用 xUnit v2 与 Moq；UI 测试在 `tests/HD2Mimnt.UITests/`，使用 Avalonia Headless 与 xUnit v3，分项目运行。
- 测试沿用 arrange-act-assert；名称使用 `MethodName_Scenario_ExpectedResult`；外部依赖使用 Mock，Repository 测试使用隔离数据库并沿用现有测试模式。
- 全量、稳定性和 BenchmarkDotNet 测试按任务需要运行，先确认对应项目实际存在，不照搬历史文档中已失效的路径。

## 业务结构速查

- 部署支持 Copy、SymbolicLink、HardLink；符号链接需满足 Windows 权限要求，硬链接要求同卷。每批部署有 `SessionId`，`DeploymentRecord` / `DeploymentFile` 保存文件与方法追踪，清理按文件记录执行。详细规则见 [部署指南](deployment-and-loaders.md)。
- 工作流定义检测、清单解析、校验与部署文件映射；包括 Legacy、V1、IntegratedArmor，由 `WorkflowRegistry` 管理，JSON 由 `ManifestParser` 解析。
- 新工作流在 `Infrastructure/Workflows/` 实现 `IWorkflow` 的 `CanHandleAsync`、`ParseManifestAsync`、`ValidateAsync`、`GetDeploymentFilesAsync`，再注册到 `WorkflowRegistry`。
- 插件用 `IPlugin` 提供生命周期，用 `IPluginCommand` 向管理器贡献可执行命令：manifest 的 `Commands` 声明元数据（`Placement` 决定入口：`pluginPage` 出现在插件管理页，`modContextMenu` 出现在模组卡片右键菜单并在调用时附带模组名称/作者/说明，`mainMenu` 进入导航栏“插件功能”菜单，`libraryToolbar` 进入模组库工具栏），宿主扫描程序集内的实现并按 `Descriptor.Name` 分发；`IPluginService.ExecutePluginCommandAsync` 校验声明与命令权限、按需拉起宿主并写执行历史。`IModPlugin`、`IDeploymentPlugin`、`IAnalysisPlugin` 目前只有定义，导入/部署主流程尚未调用。插件界面扩展必须保持声明式（主程序渲染），不得把插件程序集加载进 UI 进程。
- 声明式界面在 manifest 的 `ui` 节点：`pages` 生成导航项（`PluginPageView`），`dialogs` 生成浮窗（`PluginUiDialog`），`menus` 按 placement 进入功能菜单 / 模组库工具栏 / 模组右键菜单，`themes` 提供设计令牌覆盖（`PluginThemeService`，白名单见 `Tokens`）。渲染统一走 `PluginUiPresenter`，区块类型见 `PluginUiBlockTypes`；交互由 `PluginUiSurfaceViewModel.ExecuteAsync` 发命令，参数形如 `{ "ui": { surfaceId, actionId, values } }`，插件用 `PluginResult.Ok(null, PluginUiUpdate...)` 回传增量区块。`PluginHostManager` 对 `UiJson` 同样做密钥扫描，命中真实密钥立即终止宿主。
- 插件结果文本经常是 Markdown（尤其 AI 输出），展示必须用 `MarkdownView` / `MarkdownRenderer`，不要退化为原始文本。安装校验会拒绝 id 重复、菜单无 command/dialog、引用不存在浮窗的界面声明。
- 插件有三种形态，都由 manifest 的 `kind` 区分并跑同一套隔离/权限/界面模型：`dotnet`（默认，`assembly` + `entryType`）、`lua`（`script`，宿主用 `PluginLua` 里的 MoonSharp 沙箱执行）、混合（`kind=lua` 且填 `assembly`，脚本用 `hd2.dll.call` 调用程序集里的 `IPluginCommand`，DLL 按“命令库”加载，不实例化 `IPlugin`）。入口文件统一由 `PluginManifest.EntryFile` 解析，安装时把**入口文件**的 SHA-256 记为完整性锚点，所以脚本插件被改脚本也会拒绝启动。
- `PluginHost` 用 `IPluginRuntime` 抽象两种运行时（`DotNetPluginRuntime` / `LuaPluginHostRuntime`），新增插件形态时实现该接口并在 `PluginRuntimeFactory` 分派，不要在 `Program` 里写 if 分支。Lua 侧 `hd2` 表是脚本触达宿主的唯一入口：没有 io/os/debug/loadfile/package，模块加载限定在插件目录内（`hd2.require` 拒绝 `..` 与绝对路径），`hd2.ai` 仍走 IPC 由主进程代发。
- Lua 命令默认 30 秒超时。MoonSharp 无法安全中断脚本，超时后 `LuaPluginRuntime.HasRunawayExecution` 置位，宿主把超时错误回执发出后 `Environment` 退出回收进程；不要试图在宿主里"取消"一个死循环脚本。
- 插件宿主是**懒启动**（用户确认保留该设计）：启动应用、安装、启用都不拉起插件进程，UI 贡献来自 manifest 静态解析所以立即可见；`InitializeAsync` 只在首次执行命令（权限已授予）时运行。示例插件的初始化日志必须写明"首次调用命令，宿主按需启动"，不要写成让人误以为随应用启动执行的"已初始化"；语义详见 [插件开发指南](../features/PLUGIN_DEVELOPMENT.md) 第 2 节。
- **文本行不能用横向 `StackPanel` 排版**：横向容器以无限宽度测量子元素，`TextWrapping.Wrap` 不生效，长文本会被撑成超宽单行、溢出右边并被裁切（列表「符号 + 文字」曾因此实测出 1143px 的行宽）。一律用 `Grid` + `ColumnDefinitions="Auto,*"`，符号放 Auto、文本放 `*`。行内元素（`InlineUIContainer`）的高度必须小于 `LineHeight`，否则会把该行基线顶下去，看起来像行距不匀。
- 视图 code-behind **不要手写 `private void InitializeComponent()`**。Avalonia 的名称生成器会生成带默认参数的同名方法并负责给 `x:Name` 字段赋值；手写一个无参版本会因精确匹配胜出而被调用，导致所有 `x:Name` 字段保持 `null`（表现为构造期 `NullReferenceException` 或控件永远不响应）。只保留 `InitializeComponent();` 调用即可，`x:Name` 也避免与基类成员重名（如 `Presenter`）。
- 插件贡献的界面入口必须随启用/停用/安装/卸载/授权变更立即刷新：`PluginManagementViewModel.NotifyContributionsChanged()` 是唯一通知点，漏调就会残留导航项与菜单项。所有「清空集合再逐条追加」的刷新逻辑都要先把结果拼进局部集合、最后一次替换，或合并并发调用，否则右键菜单反复弹出时会在 `await` 处交错产生重复条目。
- 插件 AI 只能经 `PluginAiBroker` 代理：密钥留在主进程，提示词/日志/结果统一过 `PluginSecretGuard`，命中真实密钥时 `PluginHostManager` 立即强杀宿主、`PluginSecurityReporter` 停用插件并写入 `Blocked` 历史。`ai.use` 不随安装自动授予。新增任何插件出口文本都必须走 `Sanitize`，不能直传。
- 插件 manifest 的 `Id` 是任意稳定字符串（反向域名或 GUID），IPC 全程按字符串传递；读取 manifest 必须用 `PluginManifestJson.Options`（大小写不敏感、字符串枚举）。安装对话框按逐项勾选授权，`PluginInstallConsent.PersistGrants=false`（仅本次允许）写入 `IPluginGrantSession` 内存授权，`CheckPermissionAsync` 与 `PluginAiBroker` 都必须先查它。IPC 消息类型在 `Application/Services/PluginHostManager.cs` 与 `PluginHost/IPC/PluginHostMessage.cs` 各定义一份、靠 JSON 字段名隐式匹配，改字段必须两处同步。
- 长任务通过 `TaskService` 创建、报告阶段/进度并结束。批量校验、导入、部署与清理都在 `HD2Mimnt.Worker` 独立进程执行（`TaskService.RegisterProcessWorker` + `WorkerHostManager`，注册在 DI 工厂里），Worker 崩溃/被强杀不会拖死 UI。导入的密码与 HD2IR 确认经 IPC 交互请求回到主进程弹窗；部署/清理靠 `DeploymentResult.Status` 判定终态。进程隔离、取消、交互请求与终态约束见 [测试与生命周期指南](testing-and-lifecycle.md)。
- 模型/音频预览的解析、贴图解码、音频巡检与播放走 `HD2Mimnt.Worker` 的 Preview 长连接会话（`PreviewWorkerSession`，不是一次性任务）：预览打开时懒启动，关闭预览即终止进程，解析缓冲/解码像素/音频缓存随进程归还；大块结果由 Worker 写临时文件、管道只传路径，UI 只保留渲染所需场景。贴图上传 GPU 后 `ModelPreviewViewport` 会通知 ViewModel 释放 CPU 像素，Worker 每传完一批大载荷也会压实 LOH 并收缩工作集，因此反复切换预览分辨率不会让主进程和 Worker 同时叠高；关闭后 `PreviewMemoryReclaimer` 再在后台压实 LOH 并收缩工作集（[LOH 压实](https://learn.microsoft.com/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode)、[SetProcessWorkingSetSize](https://learn.microsoft.com/windows/win32/api/memoryapi/nf-memoryapi-setprocessworkingsetsize)）。GPU 上下文丢失时 CPU 像素已释放，视口会请求 ViewModel 重新加载；远程读取器/播放代理在 `App.axaml.cs` 注册，UI 测试必须注入本地 reader/播放器，否则每次预览都会冷启动真实子进程。
- 外部平台实现 `IModPlatform`，通过 `PlatformRegistry` 使用；平台模型、认证、下载流程隔离在独立模块，见 [平台指南](platforms-and-downloads.md)。
- 核心表包括 `Mods`、`Profiles`、`ProfileMods`、`Tasks`、`DeploymentRecords`、`DeploymentFiles`；字段变动须检查实体、补列、SQL 写入/读取和参数绑定，不能只增加实体属性。

## 存储位置

- “设置 → 存储位置”分别编辑应用数据（数据库、配置、日志、备份和插件数据）、模组库（含缩略图）、临时与缓存（图片缓存、下载断点、导入解压和在线更新临时文件）。保存计划后重启迁移；恢复默认位置只回填编辑内容，需要再次保存。
- 只有设置页确认保存了与当前目录不同的位置，才生成待迁移计划并在启动时显示迁移窗口。首次启动、普通启动、保存相同路径（含规范化后相同）之后的启动均跳过搬迁，不自动收拢旧缓存或重写定位文件；未完成的已确认计划仍按提交状态恢复，已提交后的残留文件清理独立执行、不显示迁移窗口。
- 启动定位文件 `storage-locations.json` 留在默认 `%LOCALAPPDATA%/HD2Mimnt`；测试通过 `HD2MIMNT_DATA_PATH` 改变该固定定位目录，不能把隔离测试写到真实用户目录。定位文件必须保持独立于可搬迁数据库，否则下次启动无法找到数据。
- 路径由 `StorageSettingsService` 统一提供并在业务服务创建前迁移。新增临时文件或内部子服务时必须传入当前 `TempPath`，不能继续硬编码系统临时目录；插件子进程通过 `HD2MIMNT_RUNTIME_DATA_PATH` 接收已生效的应用数据目录。
- 搬迁使用 SQLite 快照，并映射库路径、缩略图、部署来源和 SQLite 设置中的受管理路径。模组库/缓存不随仅应用数据根目录的移动而隐式更改；最长根目录匹配要包含未移动根，防止重写仍然有效的路径。游戏部署目标和外部目录保持原引用。符号链接部署先清理，避免搬库后出现断链。
- 复制期间失败/取消保留源文件，目标只清理带本次搬迁标记的副本；复制完成先持久化提交状态，发布定位后才删除旧文件及 WAL/SHM。中断后的启动按提交状态恢复，不能在提交失败后让业务服务使用半迁移数据库。迁移在后台运行，关闭窗口须等清理完成。

参考：[Microsoft.Data.Sqlite 备份](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup)、[SQLite WAL](https://www.sqlite.org/wal.html)、[Avalonia 文件夹选择器](https://docs.avaloniaui.net/docs/concepts/services/storage-provider)。

## 代码约定

- 启用 nullable reference types 与 ImplicitUsings，目标框架 `net10.0`。
- 异步方法以 `Async` 结尾，`CancellationToken` 默认值为 `default`。
- 可选日志使用 `ILogger<T>?` 及空条件调用。
- 实体 ID 使用 `Guid`；日期时间保存 UTC，展示时按需求转本地时间。

参考：[.NET CLI](https://learn.microsoft.com/dotnet/core/tools/)、[MVVM Toolkit](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)、[Avalonia 文档](https://docs.avaloniaui.net/)。
