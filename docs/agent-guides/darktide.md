# 暗潮模组管理器指南

修改暗潮路径、导入、加载顺序、配置档或 DML 安装流程前先读本文件。完整规则和手动验收见 [实现说明](../darktide/README.md)。

## 易错点与正确做法

- **加载顺序不是普通行列表**：DML 只读取非空且不以 `--` 开头的行，并 `Trim` 行首尾；写回必须保留原始行、注释、空行、换行符和 BOM。不要用 `File.ReadAllLines` + 重建文件的方式保存。新增/禁用优先只改目标行。
  - 来源：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/blob/master/binaries/mod_loader

- **重复登记不能自动去重**：重复名称会导致 DML 重复加载，也会让“有效行数”和目标顺序不一致。管理器只诊断；配置档应用、排序在发现重复项时必须停止并提示先修复。不要为了让操作通过而静默删除行。

- **目录名必须和 `.mod` 文件名一致**：DML 按 `mods/<entry>/<entry>.mod` 执行。`info.json.name` 只是显示名。扫描时缺少 `<目录名>.mod` 要报错，不能按其他 `.mod` 猜用。

- **DMF/base 不是普通模组**：`base` 是 DML 核心，`dmf` 由 DML 自动插入；两者不应登记到加载顺序。管理器拒绝把 `base` 作为普通模组安装或启用；DMF 是独立框架，只能通过加载器页面的 DMF 导入入口安装到 `mods/dmf`，且不登记加载顺序。DMF 官方 GitHub 没有 Release，当前由 Nexus 分发，因此不自动下载。

- **先分类再安装**：导入前必须判定压缩包属于 `StandardMod` / `ModBundle` / `CustomInstallRequired`。多模组但都在 `mods/` 下的包可以“全部安装”，并按 `info.json` 依赖顺序登记；一旦候选目录之外还有 `binaries/`、`bundle/`、`tools/`、其他 `mods/` 内容或 `.exe/.dll/.bat/.ps1`，属于作者自定义安装，必须整体拒绝并说明原因。只装一个模组目录而忽略插件 DLL 与补丁工具会留下“看起来成功、实际不能用”的半安装。
- **不代跑第三方安装器**：作者自带的 `.bat/.ps1` 和 `*-patcher.exe` 会改加载顺序、移动旧模组并打 bundle 补丁。管理器不得自动执行它们；只提示用户使用作者流程。加载顺序与游戏补丁只能由本管理器自己的、经用户确认的入口修改。DML/DMF 安装包不走普通模组导入，改用加载器页面。

- **完整包和仅更新包按内容区分**：完整包含 `mods/mod_load_order.txt`，仅更新包不含。安装时永不覆盖已有加载顺序；安装后逐字节比较，变化就回滚。仅更新包要求已有 DML 核心与加载顺序。来源：https://github.com/Darktide-Mod-Framework/Darktide-Mod-Loader/releases/tag/26.06.24

- **Release assets 可能为空**：当前 DML 发布把 ZIP 链接放在正文 Markdown 中。只解析官方 `github.com/...` 链接；不要接受任意外链，也不要把已知版本的附件 URL 硬编码成所有版本。只有 `assets` 为空时才走正文解析，或按发布格式明确兼容。

- **补丁状态不能靠文件存在判断**：DML 包本身包含 `bundle/9ba...patch_999`，存在不代表已打补丁。界面必须显示“未知”，由官方脚本/游戏确认；不要自动改补丁文件。

- **AML 会绕过标准加载顺序**：检测到 AML 只警告，不修改其文件；标准 `mod_load_order.txt` 可能不是实际顺序。第一版不实现 AML 管理。

- **ZIP 安全不能只做 `StartsWith`**：使用 `PathGuard`，拒绝绝对路径、`..`、盘符、重复条目、符号链接；解压到隔离暂存目录。安装/删除目标只允许 `mods` 直接子目录，并拒绝重解析点。参考：https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchive

- **更新必须先保留旧文件**：先把旧目录移到同卷备份，再提交新目录；加载顺序写入作为提交后步骤，失败时恢复旧目录。加载顺序本身写失败不得留下“新文件 + 旧登记”的半状态。

- **配置档不是文件快照**：应用配置档只改加载顺序中的启用/顺序，不删除、不移动模组目录；缺失模组要报告，不能因缺失而清空其他目录。预览与磁盘状态分开。

- **不要阻塞 UI 线程**：扫描、导入、安装、配置应用都通过后台任务或 `Task.Run` 执行；进度回 UI 用 `Progress<T>`/`Dispatcher`。UI 直接调用同步文件 API 会卡死大 ZIP 场景。

- **数据隔离**：默认目录 `%LOCALAPPDATA%\DarktideMimnt`、数据库 `darktidemimnt.db`。新增持久化时不要复用 HD2 的 `Mods`/`Profiles` 表或设置键；使用 `Darktide.*` 键和独立表/目录。测试用 `DARKTIDEMIMNT_DATA_PATH`，不要写真实用户目录。

- **测试不要碰真实游戏**：在临时目录中创建 `binaries/Darktide.exe`、`bundle`、`mods/base`、`mod_load_order.txt` 等最小布局；不执行真实补丁脚本，不把测试写到 Steam/Xbox 目录。测试完成后删除临时目录。
