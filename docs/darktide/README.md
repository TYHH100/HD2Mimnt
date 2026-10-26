# 暗潮模组管理器（DarktideMimnt）实现说明

本分支 `feature/darktide-mod-manager` 将原 HD2Mimnt 的 UI、任务中心、设置与本地化基础设施复用于《Warhammer 40,000: Darktide》，只替换与游戏强绑定的领域逻辑。HD2 分支的代码保留用于构建，当前启动入口是暗潮主窗口。

## 1. 当前功能

- 暗潮游戏目录检测、手动选择与 Steam 库扫描。
- DML/DMF 状态检查；AML 等替代加载器只检测并警告，不修改其文件。
- DMF ZIP 导入：DMF 是独立框架，需手动从 Nexus/官方渠道下载；管理器安装到 `mods/dmf`，不登记为普通模组，并在缺失时提示。加载器页提供 Nexus 页面一键跳转：https://www.nexusmods.com/warhammer40kdarktide/mods/8
- 扫描 `mods/<ModName>/<ModName>.mod`，读取可选 `info.json`。
- ZIP 导入、候选模组根目录选择、安装、更新、卸载与模组库归档。
- `mod_load_order.txt` 编辑：启用/禁用、上下移动、拖动排序、备份与原子替换。
- 基于 `info.json` 的依赖/顺序诊断与循环检测。
- 配置档保存、预览、应用；只改加载顺序，不删除模组目录。
- DML 官方 GitHub Release 检查、完整包/仅更新包下载与安装；保护现有加载顺序。
- 官方 `toggle_darktide_mods.bat` 启动入口，保留可见控制台与退出码记录。
- 导航栏“启动游戏”直接启动 `<游戏目录>/binaries/Darktide.exe`；Steam 版需先启动 Steam。

不在当前范围：Nexus 自动下载、AML 配置管理、自动重排依赖、自动安装 DMF、自行重写游戏补丁。

## 2. 游戏与加载器布局

已验证的 DML/DMF 布局（来源见文末）：

```
<GameRoot>/
  binaries/Darktide.exe
  binaries/mod_loader
  bundle/
  bundle/9ba626afa44a3aa3.patch_999
  mods/
    base/mod_manager.lua
    dmf/dmf.mod
    <ModName>/<ModName>.mod
    <ModName>/scripts/mods/<ModName>/...
    <ModName>/info.json          # 可选
    mod_load_order.txt
  toggle_darktide_mods.bat
  tools/dtkit-patch.exe
```

关键规则：

- `mods/mod_load_order.txt` 中**只有非空、且不以 `--` 开头的行**会被 DML 当作模组名；行首尾空白会被去掉。
- 启用模组 = 保留/添加有效登记；禁用 = 移除或注释登记。管理器优先还原已有的 `-- <Name>` 行。
- `base` 与 `dmf` 不需要登记到加载顺序；登记了会给出警告，可显式移除。
- 普通模组目录名必须和根目录 `.mod` 文件名一致。`info.json` 的 `name` 只用于显示。
- `info.json` 的 `dependencies.required`、`self_after`、`self_before` 是依赖诊断来源；字段缺失时不猜测依赖。
- Game Pass 版本可能位于 `...\content`，选择上级包目录时会解析到实际根目录。

## 3. 替代加载器（AML）

Auto Mod Loading and Ordering（AML）会自动排序并加载有效模组，不要求也不以 `mod_load_order.txt` 为权威顺序。管理器只检测 `mods/AML` 等目录名并显示警告；不覆盖、不卸载、不移动 AML 文件，也不实现 AML 配置管理。

## 3.1 压缩包分类与特殊安装

导入前先把压缩包分类（`DarktideArchivePackageKind`），避免“只装一半却报告成功”：

| 分类 | 判定条件 | 管理器行为 |
| --- | --- | --- |
| `StandardMod` | 只有一个 `<Name>/<Name>.mod`，其余是文档/图片等 | 正常导入，可选登记加载顺序 |
| `ModBundle` | 多个候选模组目录，且候选之外没有需要安装的文件 | 可选择“全部安装”，按 `info.json` 依赖顺序登记 |
| `CustomInstallRequired` | 候选目录之外还有 `binaries/`、`bundle/`、`tools/`、其他 `mods/` 内容，或 `.exe/.dll/.bat/.ps1` 等可执行/脚本文件 | 拒绝导入并列出这些文件；不运行作者的脚本或补丁工具 |
| `NotAMod` | 找不到 `<Name>/<Name>.mod` 结构 | 报错，提示可能是加载器安装包 |

实测样例：`PlayerModelHub_1.1.0.zip` 同时包含 `mods/CustomAssets`、`mods/ModelReplacementRegistry`、`mods/PlayerModelHub`（PlayerModelHub 的 `info.json` 声明 `required` + `self_after`），并附带 `binaries/plugins/custom_assets_pluginw64_release.dll`、`tools/custom-assets-patcher.exe`、`install_player_models.bat/.ps1`。作者脚本会把三个 ID 放到加载顺序最前（无 BOM）、把旧版独立角色模组目录移入备份，并调用 patcher 打 bundle 补丁。这类包属于 `CustomInstallRequired`：管理器只提示按作者说明安装，不会只安装其中一部分，也不会代为执行第三方补丁程序。

多模组但结构标准的包（前置模组和主模组都在 `mods/` 下）可以选择“全部安装”：管理器先按依赖拓扑排序，再依次登记；出现循环依赖时退回原顺序并由诊断面板提示，不会静默重排其他模组。

## 4. DML 官方安装包审计

对 GitHub Release `26.06.24` 的两个附件做了实际清单检查：

- 完整包 `Darktide-Mod-Loader.zip`：包含 `binaries/mod_loader`、`mods/base/`、`mods/mod_load_order.txt`、`toggle_darktide_mods.bat`、`tools/dtkit-patch.exe`、`bundle/9ba626afa44a3aa3.patch_999`、`README.md`。
- 仅更新包 `Darktide-Mod-Loader-UpdateOnly.zip`：与完整包相同，但**不包含** `mods/mod_load_order.txt`。

实现按内容分类，而不是只看文件名：

- 有 `mods/mod_load_order.txt` → 完整包；没有 → 仅更新包。
- 文件名暗示仅更新但内容含加载顺序时拒绝安装。
- 仅更新包要求已存在 DML 核心文件和 `mods/mod_load_order.txt`。
- 安装包只允许已知的加载器路径；出现其他 `mods/**` 或未知文件时拒绝。
- 安装前备份所有将被覆盖的文件；写入失败按文件回滚。
- 永不覆盖已有 `mod_load_order.txt`；安装后再次读取并逐字节比较，变化则回滚并报错。缺失时完整包可初始化该文件。
- 补丁是否已启用无法仅凭文件可靠判断，界面显示“未知”，由用户通过官方脚本/游戏确认。

Release 获取优先使用 GitHub API 的 assets；当前版本 assets 为空，因此从发布正文的 Markdown 官方链接解析。只接受 `github.com/user-attachments/files/...` 与官方仓库 `releases/download/...` 的 HTTPS 地址；同一类型出现多个不同链接时停止自动安装并保留 Release 页面入口。

## 5. 数据隔离

- 默认数据目录：`%LOCALAPPDATA%\DarktideMimnt`（Debug 为 `DarktideMimnt.dev`）。
- 数据库：`darktidemimnt.db`；设置键：`Darktide.GamePath` 等。
- 托管归档：`<AppData>\Darktide\Archives`；加载顺序备份：`<AppData>\Darktide\LoadOrderBackups`；加载器备份：`<AppData>\Darktide\LoaderBackups`。
- DML 安装包下载和解压临时目录位于当前存储设置中的 Temp 路径。
- 测试/开发可用 `DARKTIDEMIMNT_DATA_PATH` 覆盖默认根目录；旧的 `HD2MIMNT_DATA_PATH` 仅作为兼容回退。

## 6. 安全边界

- ZIP 条目拒绝绝对路径、`..`、盘符、重复条目和符号链接；限制条目数与解压体积。
- 安装/删除只针对 `mods` 直接子目录；拒绝 `base`、`dmf` 和符号链接/目录联接。
- 更新先移走旧目录，再提交新目录；提交或加载顺序写入失败时恢复旧目录。
- 加载顺序写入使用同目录临时文件 + 原子替换；修改前创建带时间戳的备份。
- 文件操作通过后台任务/异步执行，UI 线程不执行大型解压或扫描。
- 自动化测试只使用临时模拟目录，不写入真实游戏目录，不执行真实补丁脚本。

## 7. 手动验收步骤

在真实游戏上执行以下步骤（前六步可先用临时模拟目录）：

1. 设置 → 选择包含 `binaries/Darktide.exe` 与 `bundle` 的游戏目录。
2. 模组库 → 刷新；确认已安装模组、缺失登记和依赖诊断。
3. 导入一个 DMF 模组 ZIP；若包含多个候选目录，选择正确根目录；确认复制到 `mods` 且可启用。
4. 加载器 → 若提示 DMF 缺失，导入从 Nexus/官方渠道下载的 DMF ZIP；确认安装到 `mods/dmf` 且未登记到加载顺序。
5. 加载顺序 → 拖动/上下移动，禁用再启用；检查 `mod_load_order.txt` 的注释、空行与未知条目仍在。
6. 配置档 → 保存当前状态、修改启用状态、预览并应用；确认没有删除模组目录。
7. 加载器 → 检查最新版本；在模拟目录安装仅更新包；确认现有加载顺序字节不变。真实游戏安装前先关闭游戏。
8. 加载器 → 运行 `toggle_darktide_mods.bat`；确认控制台可见、可交互，结束后应用记录退出码并刷新状态。
9. 从导航栏“启动游戏”启动游戏，确认模组加载；游戏更新后重新运行官方切换脚本。

## 8. 已知限制

- 不自动从 Nexus 下载 DMF；DMF 是独立框架，需手动下载后通过加载器页面导入。DMF 缺失时只提示，不会静默安装。
- 不解析 `.mod` Lua 内部字段作为依赖来源；只使用 `info.json` 的已文档化字段。
- 重复登记不会被自动去重；含重复项时配置应用/排序会要求先修复。
- 旧 HD2 的配置档、模组库与数据库不会迁移到暗潮版。
- 第一版未实现对 AML/Mod Relay 的配置或部署。
- Game Pass 版本直接启动 exe 可能受商店激活限制；若无法启动，请从 Xbox 应用进入游戏。

## 9. 来源

- DMF 安装说明：https://dmf-docs.darkti.de/#/installing-mods
- DMF 创建模组与 `.mod`/`info.json`：https://dmf-docs.darkti.de/#/creating-mods
- 扩展元数据（依赖字段）：https://github-wiki-see.page/m/Darktide-Mod-Framework/Darktide-Mod-Framework/wiki/expanded-metadata
- DML 仓库与 README：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader
- DML 加载顺序读取实现：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/blob/master/binaries/mod_loader
- DML Release 26.06.24：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/tag/26.06.24
- AML（Nexus）：https://www.nexusmods.com/warhammer40kdarktide/mods/246
- .NET ZIP 安全说明：https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchive
- SQLite 在线备份：https://learn.microsoft.com/dotnet/standard/data/sqlite/backup
