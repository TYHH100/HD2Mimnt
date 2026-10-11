# 项目基础与常用命令

首次定位项目结构、增加服务/工作流或查找入口时读取本文件。业务易错点按根目录 [AGENTS.md](../../AGENTS.md) 的索引读取；版本和阶段状态查看当前程序集与项目状态文档，不把旧阶段总结当成实时状态。

## 架构与入口

HD2Mimnt 是基于 .NET 10、Avalonia 和 SQLite 的 Helldivers 2 模组管理器，采用 MVVM 和分层架构。

| 项目 | 职责 |
| --- | --- |
| `HD2Mimnt.Core` | 领域实体、接口、枚举 |
| `HD2Mimnt.Infrastructure` | SQLite Repository、部署实现、工作流、平台适配器 |
| `HD2Mimnt.Application` | 业务服务与流程编排 |
| `HD2Mimnt.UI` | Avalonia View、ViewModel、DI 注册 |
| `HD2Mimnt.PluginSDK` / `PluginHost` | 插件接口与独立进程宿主、IPC、权限声明 |
| `HD2Mimnt.Worker` | 后台工作进程 |

- UI 入口：`src/HD2Mimnt.UI/Program.cs`；DI 注册检查 `App.axaml.cs` 中的 `ConfigureServices`，尤其手动构造工厂。
- 主窗口：`src/HD2Mimnt.UI/MainWindow.axaml` 及 `.cs`；页面模型在 `ViewModels/`。
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
- 插件使用 `IPlugin`、`IModPlugin`、`IDeploymentPlugin`、`IAnalysisPlugin` 等 SDK 接口；manifest 描述权限，宿主与主程序分进程通信。解析与安装流程归 `IPluginService`。
- 长任务通过 `TaskService` 创建、报告阶段/进度并结束；扫描、校验、导入由各 Worker 执行。状态持久化与取消约束见 [测试与生命周期指南](testing-and-lifecycle.md)。
- 外部平台实现 `IModPlatform`，通过 `PlatformRegistry` 使用；平台模型、认证、下载流程隔离在独立模块，见 [平台指南](platforms-and-downloads.md)。
- 核心表包括 `Mods`、`Profiles`、`ProfileMods`、`Tasks`、`DeploymentRecords`、`DeploymentFiles`；字段变动须检查实体、补列、SQL 写入/读取和参数绑定，不能只增加实体属性。

## 代码约定

- 启用 nullable reference types 与 ImplicitUsings，目标框架 `net10.0`。
- 异步方法以 `Async` 结尾，`CancellationToken` 默认值为 `default`。
- 可选日志使用 `ILogger<T>?` 及空条件调用。
- 实体 ID 使用 `Guid`；日期时间保存 UTC，展示时按需求转本地时间。

参考：[.NET CLI](https://learn.microsoft.com/dotnet/core/tools/)、[MVVM Toolkit](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/)、[Avalonia 文档](https://docs.avaloniaui.net/)。
