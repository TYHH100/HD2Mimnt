# HD2Mimnt

> **Helldivers 2 Mod Manager** - 专业的 Helldivers 2 Mod 管理工具

## 功能特性

- 🗂️ **Mod Library**: 统一管理所有 Mod 资产
- 👤 **Profile 系统**: 多配置快速切换
- 🚀 **智能部署**: 支持 Copy、Symbolic Link、Hard Link 三种部署方式
- 🧹 **精准清理**: 完整追踪部署文件，安全清理
- 📋 **Task Center**: 统一的任务管理和进度追踪
- 🔍 **Mod 分析**: 自动识别 Mod 类型和冲突
- 🎨 **现代化 UI**: 基于 Avalonia 的深色主题界面

## 技术栈

- **UI**: Avalonia.UI 12.0+
- **Runtime**: .NET 10
- **Database**: SQLite
- **Architecture**: Clean Architecture + MVVM

## 开发状态

🚧 **当前版本**: v0.1.0-alpha

项目处于早期开发阶段，核心架构已完成，功能正在逐步实现中。

## 项目结构

```
HD2Mimnt/
├── src/
│   ├── HD2Mimnt.Core/              # 核心层 - 实体和接口
│   ├── HD2Mimnt.Infrastructure/    # 基础设施层 - 数据访问
│   ├── HD2Mimnt.Application/       # 应用层 - 业务逻辑
│   └── HD2Mimnt.UI/                # 表示层 - Avalonia UI
└── CLAUDE.md                        # 开发文档
```

## 快速开始

### 前置要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Windows 10/11

### 构建

```bash
# 克隆仓库
git clone https://github.com/yourusername/HD2Mimnt.git
cd HD2Mimnt

# 构建项目
dotnet build

# 运行
cd src/HD2Mimnt.UI
dotnet run
```

### 测试

```bash
# 运行所有测试
dotnet test

# 运行测试并查看详细输出
dotnet test --verbosity normal
```

**当前测试状态**: ✅ 11/11 通过

### 发布

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## 开发计划

详细的开发计划请参考：`HD2Mimnt v2 总体开发计划.md`

### 已完成 ✅

- Phase 0: 项目初始化
- Phase 1: 项目骨架
  - Clean Architecture 分层
  - Avalonia UI 基础框架
  - SQLite 数据库设计
  - 基础实体和仓储

### 进行中 🚧

- Phase 2: Mod Library 功能
  - Mod 导入
  - Mod 扫描和分析
  - Mod 搜索和过滤

### 计划中 📋

- Phase 3: Profile 管理
- Phase 4: Task Center
- Phase 5: Deployment & Cleanup
- Phase 6: Workflow 系统
- Phase 7: Plugin 扩展
- Phase 8: AI 辅助功能

## 贡献

欢迎提交 Issue 和 Pull Request！

## 许可证

MIT License

## 致谢

- [Helldivers 2](https://www.helldivers.com/) - 游戏本体
- [Avalonia](https://avaloniaui.net/) - UI 框架
- 所有 Mod 作者和社区贡献者
