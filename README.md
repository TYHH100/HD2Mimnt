# DarktideMimnt

《Warhammer 40,000: Darktide》（战锤 40K：暗潮）本地模组管理器，基于原 HD2Mimnt 的 .NET 10 + Avalonia + SQLite 基础设施改造。当前位于 `feature/darktide-mod-manager` 分支，启动入口是暗潮主窗口；HD2 页面与旧服务保留用于构建，不在暗潮导航中暴露。

## 主要功能

- **游戏与加载器检测**：手动选择、Steam 库扫描、DML/DMF 状态检查；AML 等替代加载器只检测并警告。
- **DMF 导入**：DMF 是独立框架，需手动下载官方 ZIP 后从加载器页面导入；安装到 `mods/dmf`，不登记为普通模组。加载器页提供 [Nexus DMF 页面](https://www.nexusmods.com/warhammer40kdarktide/mods/8) 一键跳转。
- **模组库**：ZIP 导入、候选根目录选择、安装、更新、卸载，独立托管归档。多模组打包的标准包可一次“全部安装”并按依赖顺序登记；带 `binaries/`、`tools/` 或自带安装脚本的特殊包会被识别并拒绝部分安装，提示改用作者流程。
- **加载顺序**：`mod_load_order.txt` 可视化编辑，保留注释/空行/换行符/BOM，修改前备份，原子写入。
- **依赖诊断**：读取 `info.json` 的 `dependencies.required`、`self_after`、`self_before`，检测缺失、禁用、顺序错误与循环。
- **配置档**：保存、预览、应用模组组合与顺序；不删除模组目录。
- **DML 安装**：从官方 GitHub Release 检查版本，按内容区分完整包/仅更新包；备份覆盖文件，保护现有加载顺序，失败回滚。
- **官方脚本入口**：启动 `toggle_darktide_mods.bat`，保留可见控制台并记录退出码。

## 构建与运行

需要 .NET 10 SDK，Windows 10/11。

```powershell
dotnet build HD2Mimnt.slnx --no-restore
dotnet run --project src/HD2Mimnt.UI/HD2Mimnt.UI.csproj

dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --no-restore
```

暗潮测试可用过滤条件单独运行：

```powershell
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~Darktide"
```

## 数据隔离

- 默认数据目录：`%LOCALAPPDATA%\DarktideMimnt`（Debug：`DarktideMimnt.dev`）。
- 数据库：`darktidemimnt.db`。
- 托管归档、加载顺序备份与加载器备份位于该数据目录的 `Darktide` 子目录。
- 测试/开发可用 `DARKTIDEMIMNT_DATA_PATH` 指向隔离目录。

## 安全约束

- 不自动修改游戏补丁；补丁状态无法仅凭文件确认时显示“未知”。
- 不覆盖现有 `mod_load_order.txt`；安装 DML 后逐字节校验。
- ZIP 解压拒绝路径穿越、符号链接、重复条目；安装/删除目标限制在 `mods` 直接子目录。
- 自动化测试只使用临时模拟游戏目录，不写入真实游戏目录，不执行真实补丁脚本。

## 文档

- [暗潮实现说明与手动验收](docs/darktide/README.md)
- [暗潮开发指南与易错点](docs/agent-guides/darktide.md)

## 外部来源

- DMF 安装说明：https://dmf-docs.darkti.de/#/installing-mods
- DML 仓库：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader
- DMF 仓库：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Framework
- 扩展元数据：https://github-wiki-see.page/m/Darktide-Mod-Framework/Darktide-Mod-Framework/wiki/expanded-metadata
- AML：https://www.nexusmods.com/warhammer40kdarktide/mods/246

本项目沿用仓库根目录的 `LICENSE`。第三方工具（7zip、minidump-stackwalk 等）的许可证文件保留在原位置；本分支未新增第三方运行时依赖。
