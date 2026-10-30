# HD2Mimnt

**中文** | [English](README.en.md)

Helldivers 2（绝地潜兵 2）模组管理器，基于 .NET 10 与 Avalonia 开发，主要面向 Windows 10/11。

## 主要功能

- **模组库管理**：导入、整理和搜索模组，支持 HD2IR 嵌套压缩包格式
- **配置系统**：多套 Profile 管理不同场景下启用的模组，一键切换
- **多种部署方式**：Copy、符号链接、硬链接
- **后台任务**：扫描、导入、部署异步执行，支持进度查看与取消
- **插件系统**：提供 SDK 与独立宿主进程，插件隔离运行

## 技术栈

.NET 10 · Avalonia · SQLite · CommunityToolkit.MVVM · SharpCompress

## 构建与运行

需要 .NET 10 SDK。

```powershell
dotnet build HD2Mimnt.slnx
dotnet run --project src/HD2Mimnt.UI/HD2Mimnt.UI.csproj

# 单元测试与 UI 测试
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build
```

## 项目结构

| 项目 | 职责 |
| --- | --- |
| `HD2Mimnt.Core` | 领域实体与接口 |
| `HD2Mimnt.Infrastructure` | 数据访问、部署实现、工作流 |
| `HD2Mimnt.Application` | 业务服务与流程编排 |
| `HD2Mimnt.UI` | Avalonia 界面（MVVM） |
| `HD2Mimnt.PluginSDK` / `PluginHost` | 插件 SDK 与隔离宿主进程 |
| `HD2Mimnt.Worker` | 后台工作进程 |

`tests/` 下分为单元测试（Tests）、UI 测试（UITests）、稳定性（StabilityTests）与性能（PerformanceTests）四个测试项目。

## 文档

- [文档导航](docs/README.md)
- [发布流程](docs/RELEASE_PROCESS.md)
- [协作规则](AGENTS.md)

## 许可证

[MIT](LICENSE)
