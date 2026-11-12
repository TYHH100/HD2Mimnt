# HellForge 私服集成（交接文档）

本文面向接手 HellForge 适配工作的智能体/开发者：记录原定目标、已完成部分、剩余路线、官方 CLI 接口要点、测试方法与易错点。修改相关代码前先读本文与 [`ui-and-avalonia.md`](../agent-guides/ui-and-avalonia.md)、[`deployment-and-loaders.md`](../agent-guides/deployment-and-loaders.md)。

## 原定目标与现状

用户要求 HD2Mimnt **原生适配 HellForge 私服**（本地后端 HD2 游戏副本）：模组配置、启动流程、档案切换。分阶段路线：

| 阶段 | 内容 | 状态 |
| --- | --- | --- |
| ① | 只读状态 + 玩家档案管理专页，设置切换游戏源后自动显示/导航 | 已完成（2026-11-11） |
| ③ | 会话管理：`play --wait yes` 事件流进度、停止、库页启动接线 | 已完成（2026-11-11） |
| ② | 模组同步：HellForge 模式下部署改走 `mods\` + CLI 模组命令 | 已完成（2026-11-12） |
| ④ | 更多设置（war/multiplayer/sandbox 等）、日志页 | 已完成（2026-11-12，设置留在专页、日志为专页内卡片） |

阶段编号保留最初规划顺序；实际先做了 ③，因为用户反馈“点启动没反应”实为约 3 分钟准备期无进度显示。

真实验证环境：`G:\App\Game\HellForge`（0.4.0-beta，`ready.gameCopy=true`）；参考源码仓库 `G:\Temp\Githud\HellForge`（只读参考，不复制代码）。

## GPL 边界（必须遵守）

- HellForge 上游 <https://github.com/SkyeShade/HellForge> 为 **GPL-3.0**；HD2Mimnt 为 MIT。
- 只允许**独立进程调用官方 CLI**（`app\cli\HellForge.Cli.exe`）并解析其 JSON 输出；不得复制、链接 HellForge 源码或其 DLL。
- 不直接触碰 `game\`、`app\`、本地后端、网络阻断；会话检查/清理全部由 HellForge 自己的会话运行器完成。
- 官方外部管理器接口文档：HellForge 仓库 `docs/external-launchers.md`（接口只增不改，camelCase JSON）。本文的接口摘要以该文档为准，版本差异用 `status` 输出的版本号判断。

## 架构与文件地图

UI 只经 Application 服务访问业务（仓库常驻规则）。

### Application 层

| 文件 | 职责 |
| --- | --- |
| `src/HD2Mimnt.Application/Services/HellForgeService.cs` | CLI 封装：`TryGetInstallationRoot` / `TryGetCliPath` / `CreateCliStartInfo`（自动附 `--install`）、`GetStatusAsync`、`GetProfilesAsync`、`CreateProfileAsync`、`SelectProfileAsync`（`settings --profile`）、`StopSessionAsync`（`stop --wait yes`，90 秒超时）；模组命令 `GetModsAsync` / `AddModAsync`（`mod-add --file`，10 分钟超时）/ `RemoveModAsync` / `SwitchModAsync` / `OrderModsAsync` / `SetPackOptionAsync`；设置与日志 `GetSettingsAsync` / `UpdateSettingsAsync`（键不带 `--`，自动拼 `settings --key value`）/ `GetLogsAsync` / `ExportLogsAsync`（导出前删同路径旧文件，CLI 拒绝覆盖；5 分钟）/ `CreateCrashReportAsync`（同上；`--dump yes` 可选；10 分钟）。常规命令 30 秒超时，超时杀进程树；stderr 作为错误信息抛出。 |
| `src/HD2Mimnt.Application/Services/HellForgeModExporter.cs` | 库模组→HellForge 形态导出（直接写入调用方给的目标目录，不写 `game\data`）。`TryQualifyAsPack`：干净 V1（完整 Include 映射、无根补丁、Include 目录齐全）透传为 cosmetic pack（**按 HD2Mimnt 清单合成** `manifest.json`（`Guid`/`Name`/`Options`/`SubOptions`/`Include`/`Image`，只写导出目录里真实存在的图片）+ 全部 Include 目录 + 预览图，剔除 LuaExtracted/备份）；其余扁平化为 `hellforge-mod.json`（id=`hd2mimnt.<modId N>`、name≤80、version≤32、gameplay=true）+ `data/`，用 `CollectSelectedFiles`+`BuildFiles` 按 bundle 从 0 重编号保住有效顺序。文件写入经可插拔的 `HellForgeFilePlace`（默认流式复制，同步传入按部署方式的写入器）；`ComputeFingerprint`（stat 级：pack 与选项无关、flat 含选项快照）供跳过未变化；`ResolveModId` 复现 HellForge 的 id 规则（`hd2mimnt.<N>` / `pack.<Guid>` / `pack.<slug>`）用于目录命名。 |
| `src/HD2Mimnt.Application/Services/HellForgeModFilePlacer.cs` | 按设置中的部署方式把库文件放进 `mods\`：Copy=流式复制（只复制一次），HardLink/SymbolicLink 调 `IDeploymentMethod`；写入前 `CheckCompatibilityAsync` 预检，不可用（跨卷、无建链权限）回退复制并给出警告；单个文件建链失败同样回退并只提示一次。 |
| `src/HD2Mimnt.Application/Services/HellForgeModSyncService.cs` | 同步编排：`SyncProfileAsync`（会话运行拦截；加载器/IAS 跳过并记录原因；仅启用集；指纹跳过未变化；**按部署方式直接写 `<安装根>\mods\<id>\`，不调 `mod-add`**：点前缀临时目录导出→整体改名（目录名=模组 id，目录内 `.hd2mimnt-mod.json` 记录归属；同 id 旧目录与历史 `id-N` 遗留副本先清；开局清点前缀临时目录；同 Guid 的两个外观包后一个降级扁平导出，避免 HellForge 判重复）；**开关/选项/顺序本地读 `mods\hellforge-mods.json` 比对**，启用与 pack 选项一次原子写入该文件，仅 `order` 走 `mod-order`；只有真的导入了才跑一次 `mods` 校验识别并按目录校正 id）、`RemoveSyncedAsync`（清理=删本管理器目录 + 清状态条目，不跑 `mod-remove`）、`GetStatusAsync`。归属与指纹存 AppData `hellforge-sync.json`（按安装根，原子写），顺带清理旧版导出缓存 `Cache\HellForgeSync`。 |
| `src/HD2Mimnt.Application/Services/HellForgeModsFile.cs` | `mods\hellforge-mods.json` 的本地读写（上游 `docs/external-launchers.md` 明确允许外部启动器写，临时文件+改名）：`disabled`/`order`/`packs.off`/`packs.sub`，大小写不敏感读取、未知字段原样保留、内容没变不落盘；`HellForgeLocalMods.Scan` 不启 CLI 就从 `mods\` 读 id（`hellforge-mod.json` 的 `id` 或 `manifest.json` 的 `Guid`/slug）。 |
| `src/HD2Mimnt.Application/Services/HellForgeSessionService.cs` | 单例会话观察：`StartAsync` 启动 `play --wait yes`，等到 `started` 事件返回，后台逐行解析事件流；`State`（锁保护快照）、`StateChanged`、`SessionStarted` 事件；`StopAsync` = stop + 校准；`RefreshAsync` 用 `status` 校准外部启动的会话（本管理器观察中的会话以事件流为准）。`Dispose` 只杀 play 观察进程，**不停止游戏**。 |
| `src/HD2Mimnt.Application/Services/HellForgeModels.cs` | CLI DTO（`HellForgeStatus`、`HellForgeModsList`/`HellForgeModInfo`/`HellForgeModOption` 及 mod-add/remove/switch/order/pack 结果、启动设置 `HellForgeLauncherSettings`、日志 `HellForgeLogsResult`/`HellForgeLogRun`/`HellForgeLogFile`/`HellForgeLogExportResult`/`HellForgeCrashReportResult`）+ 进度快照 `HellForgeSessionProgress` / 枚举 `HellForgeSessionPhase`（Idle/Preparing/AwaitingNetworkBlock/AwaitingSteamBroker/Patching/Running/Ended/Failed）。注意 DTO `HellForgeSessionState`（status.session）与进度快照同名不同义，勿混淆。 |
| `src/HD2Mimnt.Application/Services/GameDetectionService.cs` | `LaunchGameAsync`：HF 模式先 `--help` 探测 CLI 是否支持 `play`（输出含以 `play ` 开头的行）；支持则 `_sessionService.StartAsync`，否则回退打开 `HellForge.exe` 启动器。官方模式仍走 `steam://rungameid/553850`。 |
| `src/HD2Mimnt.Application/Services/GameInstallationPaths.cs` | `TryResolveHellForgeRoot`（识别 `hellforge-install.json` 标记）、`ResolveHellForgeGameDirectory`（`<root>\game`）。 |
| `src/HD2Mimnt.Application/Services/SettingsService.cs` | `GetGameInstallationMode()`（键 `SettingKeys.GameMode`，缺省 Official）、`GetEffectiveGamePath()`：HF 模式解析为 `<root>\game`。HF 模式下部署不再走此路径写 `game\data`，而由 `LibraryViewModel` 改路由到 `HellForgeModSyncService`（见下）。 |
| `src/HD2Mimnt.Worker/HellForgeSyncWorkerTask.cs` | Worker 任务 `HellForgeSync`：`Mode=Sync`（ProfileId/Order）调 `SyncProfileAsync`，`Mode=Remove` 调 `RemoveSyncedAsync`；结果状态映射到任务中心。`WorkerFactory`/`Worker/Program.cs` 已注册（含 `HellForgeService`/`HellForgeModSyncService` DI，`PrepareStorageState` 含 HellForgeSync 只做 ProfileService 初始化、不做部署迁移）。 |

### UI 层

| 文件 | 职责 |
| --- | --- |
| `src/HD2Mimnt.UI/ViewModels/HellForgeViewModel.cs` | 专页 VM：状态卡片、档案列表/切换/创建（名称规则 `^[a-z0-9-]{1,32}$`）、会话进度（`SessionSteps` 5 步、`StartSessionCommand` / `StopSessionCommand`、问题与提示文本）、模组同步只读状态（`HasSync`/`SyncedModCount`/`SyncStatusText`/`SyncSourceText`，注入 `HellForgeModSyncService`/`ProfileService`，随 `LoadAsync` 刷新）、启动设置编辑（`WarOptions`/`StrangersOptions` 本地化选项、各开关、`ForgeCreditChance`、`GameOptions`；`IsSettingsDirty` 与快照比较，`SaveSettingsCommand` 只发差异键，成功后用返回的 settings 重建快照）、日志（`LogRuns`/`HasLogs`/`SelectedLogRun`，`ExportLogsRequested`/`CrashReportRequested` 事件交给视图选路径，`ExportLogsAsync`/`CreateCrashReportAsync` 完成后弹通知）。`OperateAsync` + `Completion` TCS 支持取消与关闭等待；`StateChanged` 经 Dispatcher marshalling 更新步骤。 |
| `src/HD2Mimnt.UI/Views/HellForgeView.axaml(.cs)` | 状态卡、会话卡（`SessionStepBar` 步骤条，`step-circle` 样式类 done/active/failed）、档案列表、创建对话框覆盖层、启动设置卡（war ComboBox、启动参数、6 个 ToggleSwitch、高级 Expander：陌生人联机/会话中改模组/Forge Credits）、日志卡（`LogRunList`、保存日志/崩溃报告按钮、包含转储勾选）。代码后置订阅导出事件，用 `StorageProvider.SaveFilePickerAsync` 选 zip 路径后回调 VM（与 `LogView` 同一模式）。 |
| `src/HD2Mimnt.UI/ViewModels/MainWindowViewModel.cs` | `HellForgeTabIndex=7`、`PluginPageOffset=8`（插件页 Tag 从 100 起）；`NavigateToHellForgeAsync`；订阅 `SettingsViewModel.GameSourceChanged`（HF→自动导航，官方→离开 tab 7）与 `HellForgeSessionService.SessionStarted`（库页启动后自动跳专页）；`CanCloseAsync` 等待 HF 页 `CancelAndWaitAsync`。 |
| `src/HD2Mimnt.UI/ViewModels/SettingsViewModel.cs` | `ShowHellForgeEntry`（按已保存模式）、`GameSourceChanged` 事件（仅在保存成功且模式实际变化后触发）。 |
| `src/HD2Mimnt.UI/ViewModels/LibraryViewModel.cs` | `IsHellForgeMode` 时 `DeployAsync` 改走 `RunHellForgeSyncTaskAsync`（`HellForgeSync` Worker，Mode=Sync，确认弹窗/任务中心进度复用），`CleanupAsync` 改走 Mode=Remove。文案沿用部署流程的硬编码中文约定（非 resx）。官方模式分支不变。 |
| `src/HD2Mimnt.UI/MainWindow.axaml(.cs)` | 导航项 Tag 7（`IsVisible` 绑 `ShowHellForgeEntry`）、Ctrl+D8、TabItem 索引 7（`IsVisible="False"`，隐藏页必须本地设 False）。 |
| `src/HD2Mimnt.UI/App.axaml.cs` | DI：`HellForgeService`、`HellForgeSessionService` 单例，`HellForgeViewModel` 瞬态，`GameDetectionService` 注入会话服务。 |

本地化：`src/HD2Mimnt.Application/Localization/Resources/Strings*.resx` 三份的 `HellForge.*` 键（含 `HellForge.Phase.*`、`HellForge.Step.*`、`HellForge.Sync.*`、`HellForge.Settings.*`、`HellForge.War.*`、`HellForge.Strangers.*`、`HellForge.Logs.*`、`HellForge.LogStatus.*`）。`TranslationCatalogTests` 校验三语键集合与格式参数一致（值可重复）。

## CLI 接口要点（摘要，全文见上游文档）

调用形式：`<root>\app\cli\HellForge.Cli.exe <command> [options] --install <root>`。成功时 stdout 输出 JSON；失败时 stderr 输出 `HellForge: <原因>` 并退出码 1。

| 命令 | 用途 |
| --- | --- |
| `status` | 版本（hellforge/engine/modSdk/gameBuild）、`ready.gameCopy`/`ready.sessionRunner`、`networkBlock`（wfp/firewall/linux-netns）、`settings`、`session`（running/lastRun/lastStatus/lastFailure）。 |
| `profiles` | 档案列表（name/template/folder/used/selected）+ 模板；`--create <name> --template <t>` 创建。 |
| `settings [--profile <name>] [--war ...] ...` | 保存并打印启动设置；`play` 默认读它，`play` 自身不改它。可控项：`--profile`、`--war live\|frozen\|open`、`--local-playfab`、`--multiplayer`、`--engine-log`、`--crash-dump`、`--sandbox`、`--steam-udp`、`--game-options`、`--strangers yes\|no\|default`、`--live-changes`、`--forge-credits 0-100\|default`。 |
| `mods` | Mods 页列表（id/name/version/enabled/group/tags/folder/archive/pack 选项）+ modlist code。 |
| `mod-add --file <path>` | 添加 zip/rar/7z、文件夹模组的 `hellforge-mod.json` 或外观包 `manifest.json`；同 id 视为更新替换。 |
| `mod-remove --id` / `mod-switch --id --on yes\|no` / `mod-order --ids a,b,c` / `mod-pack --id --option ...` | 删除（bundled 会自愈，应改用关闭）、开关、置顶排序、外观包选项。 |
| `play [--wait yes] [...]` | 启动会话；`--wait yes` 逐行输出事件直到 `ended`，仅正常结束（observed）退出码 0。 |
| `stop [--wait yes] [--now yes]` | 请游戏自行关闭（30 秒后才强杀，自行关闭才保存 shader cache）；`--wait yes` 等清理完成。 |
| `logs [--run <id>] [--export <zip>] [--crash-report <zip>] [--dump yes]` | 会话日志列表/导出/崩溃报告（阶段④已用）。`--export`/`--crash-report` 拒绝覆盖已存在文件；`--dump yes` 才把崩溃转储放进报告；报告文本会脱敏（Steam id/IP/玩家名）。 |

**会话事件流**（`play --wait yes`，每行一个 JSON）：

- `started`（runId/run/pid/profile/war）→ `status`（多次）→ 可选 `mod-problems`（被剔除模组及原因）、`steam-input`（手柄提示 note）→ `ended`（status/result/logs）。
- status 取值与 UI 步骤映射（`HellForgeSessionProgress.PhaseFromStatus` + VM `StepOfPhase`）：

| CLI status | 阶段 | 步骤条（共 5 格） |
| --- | --- | --- |
| `preparing` | Preparing | 1 构建模组 |
| `awaiting_network_block_install` / `awaiting_windows_firewall_consent` | AwaitingNetworkBlock | 3 网络阻断 |
| `awaiting_steam_broker` | AwaitingSteamBroker | 3 |
| `patching` | Patching | 4 启动中 |
| `observing` | Running | 5 运行中 |
| `observed` | Ended | 全部完成 |
| `failed` / `cleanup_failed` | Failed | 停留在最后活动格 |

（步骤 2“校验文件”在 preparing 内，无独立事件；`awaiting_windows_firewall_consent`/`cleanup_failed` 来自旧版运行器，仍需处理。）

**关键约束**：

- 一台 PC 同时只有一个会话；`play` 在已有会话时拒绝（任何安装）。
- **会话运行中拒绝一切模组变更**（与 Mods 页一致）。
- 模组必须进 `mods\`（`mod-add` 或直接放 zip/文件夹），**绝不直写 `game\data`**：会话按加载顺序把启用模组的补丁暂存进副本并清理上次暂存；手放 `game\data` 的补丁虽被容忍，但脱离列表、排序与联机 modlist code。
- 首次安装/修复（`ready.gameCopy=false`）必须走 `HellForge.exe` 向导；之后一切可在其关闭时经 CLI 完成。
- 关闭 play 观察进程不会停止会话；停止必须用 `stop`。

## 阶段②：模组同步（已完成，2026-11-12）

**原缺口**：HF 模式部署经 `SettingsService.GetEffectiveGamePath()` 解析为 `<root>\game` 直写 `data\`，绕过 HellForge 的 `mods\` 管理。现已改为：HF 模式下配置页“部署”=同步模组进 `mods\`，“清理部署”=移除本管理器同步的模组；`game\data` 的暂存与清理完全交给 HellForge 会话运行器。

**用户裁定的交互（2026-11-12）**：① 混合包装——干净 V1 透传 cosmetic pack（HellForge Mods 页保留选项面板），其余扁平化；② 复用配置页部署按钮（不在专页另设入口）；③ 仅启用集（禁用/移出即从 `mods\` 删目录并清状态条目）；④ Lua 加载器/依赖与 HD2IAS 模组跳过并提示。

**实现要点**：

1. **包装判定**（`HellForgeModExporter.TryQualifyAsPack`）：V1 + 有选项 + 每个选项/子选项都有 Include 映射 + 模组根无补丁 + 所有 Include 目录存在 → 透传 pack；否则扁平化。透传保留作者 `manifest.json` verbatim、复制全部 Include 目录（含未启用选项，供 HellForge 侧切换）与预览图，剔除 LuaExtracted/备份等垃圾。
2. **扁平化**：`CollectSelectedFiles`（HD2Mimnt 选项解析）+ `BuildFiles`（空编号表，按 bundle 从 0 重编号）→ `data/`，写 `hellforge-mod.json`（id=`hd2mimnt.<modId N>`、name≤80、version≤32 缺省 1.0.0、gameplay=true 让 HellForge 按内容自动判 per-player/shared）。**重编号是必须的**：HellForge 按 (bundle, 原编号) 排序暂存，若不重编号，跨 Include 目录的同 bundle 补丁会顺序倒置。HellForge 会话会再次接在游戏已有补丁之后重编号，导出只需保住模组内部相对顺序。
3. **顺序映射**：`BslProfilePolicy.DeploymentSequence` 是加载顺序（先部署=底部），HellForge 顶部最后加载，故 `mod-order --ids` 传**取反**列表。gameplay 组恒在 cosmetic 组之上（HellForge 分组语义，跨组顺序不由我们控制）。
4. **落盘方式**（2026-11-12 用户裁定）：按设置中的部署方式（`Deployment.DefaultMethod`）把模组直接写进 `<安装根>\mods\<id>\`，**不调 `mod-add`**——上游 `ModInstaller.Add` 对文件夹模组恒为 `File.Copy`，无法通过 CLI 建链接，且旧路径会复制两遍（库→AppData 缓存→`mods\`）。做法：点前缀临时目录（`.hd2mimnt-xxxxxxxx`，HellForge 不加载点目录）导出 → 同 id 旧目录先删 → `Directory.Move` 改名；随后 `mods` 重查按目录路径校正 id（pack 的 id 由上游从清单推导，不写在我们导出的文件里）并校验识别，未识别即报错并附 CLI `problems`。部署方式不可用（硬链接跨卷、Windows 符号链接需管理员）时自动回退文件复制并把警告写进结果文案。
5. **状态 diff（2026-11-12 优化）**：同步前 `status.session.running` 拦截；写盘后**本地**读 `mods\hellforge-mods.json` + 扫 `mods\` 判断启用/顺序/选项，只有真导入了才跑一次 `mods` 校验识别。所有开关与 pack 选项**一次原子写入** `hellforge-mods.json`（官方允许外部写），`order` 仍用 `mod-order`（只写数组，很快）。不再逐个 `mod-switch`/`mod-pack`：这两条命令每次都要重扫整个 `mods\`，60 个外观包时单次约 5 秒，首次同步 50 个选项差就是 4 分钟——实测首次 278s→13s、再次同步 16s→0.3s。指纹（stat 级）未变且目录仍在 → 跳过重新导出；`hellforge-mods.json` 内容没变则完全不落盘。
6. **归属与清理**：AppData `hellforge-sync.json` 按安装根记录 modId→{hellforgeId, kind, fingerprint}；清理只移除归属内的 id，绝不触碰他人模组/bundled/`game\data`。
7. **跳过项**：`BslProfilePolicy.Role(mod) != None`（加载器/依赖/SmoothBoot）与 `WorkflowType==HD2IAS` 跳过并在结果列明原因。上游只支持带 HellForge bridge 的 BSL+HD2Runtime，Lua 由用户在 HellForge 侧自行管理。

**残留风险/后续**：① 重复 GUID 的两个模组经 `mod-add` 互相替换，`mod-order` 已去重避免崩溃，但内容取最后写入者；② 指纹用 长度+mtime（与 HellForge `ModStaging.Same` 一致），极端同尺寸同时间戳的内容替换可能漏判；③ 透传 pack 未校验 Include 目录内孤儿 sidecar（缺主文件的 .stream/.gpu_resources），HellForge 会照暂存——真实库罕见，扁平化路径有主文件校验。

**HF 专页同步状态（2026-11-12 已接线）**：`HellForgeViewModel` 注入 `HellForgeModSyncService`/`ProfileService`，`LoadAsync` 读 `GetStatusAsync` 并解析来源配置名，专页档案列表之后显示只读状态卡（`SyncStatusText`/`SyncSourceText`，三语 `HellForge.Sync.*` 键）。同步入口仍只在配置页部署按钮，专页不提供竞争入口。状态卡置于档案列表**之后**，避免下推交互内容导致无头点击命中错位。

## 阶段④：启动设置与会话日志（已完成，2026-11-12）

**用户裁定（2026-11-12）**：设置范围 = 核心项 + 高级项（陌生人联机/会话中改模组/Forge Credits 收进 Expander）；日志呈现 = HF 专页内卡片，不新增独立页签。

**实现要点**：

1. **设置数据流**：`LoadAsync` 每次调用 CLI `settings`（与 `status.settings` 同源，但 `settings` 是保存后的权威输出）填充编辑字段并保存快照。保存时逐项与快照比较，只把差异拼进 `settings --key value ...`（yes/no 用 `yes|no`）；成功后直接用 CLI 返回的完整设置重建快照，不再额外 `status`。会话运行中 CLI 会拒绝会话相关修改，错误经 `OperateAsync` 进入状态文字。
2. **枚举映射**：war = `live|frozen|open`；陌生人 = `default|yes|no`（`PlayWithStrangersChosen` 为 null 即 default；显示的 `PlayWithStrangers` 是沙箱下的有效值，不能当作已选项回写）。Forge Credits 为 0-100（NumericUpDown，缺省 30）。
3. **日志列表**：`logs` 运行记录（id/folder/startedUtc/status/failure/gameExitCode/completedUtc/crashed）映射为 `HellForgeLogRunItem`（本地化状态、崩溃/失败/退出码摘要）；默认选中最新一次，刷新后按 id 保留选择。状态卡与日志卡都追加在页面底部，不位移档案/会话交互。
4. **导出与崩溃报告**：VM 仅发 `ExportLogsRequested`/`CrashReportRequested`，视图用保存对话框选 zip 后调 `ExportLogsAsync`/`CreateCrashReportAsync`；服务在调用 CLI 前删除同路径旧文件（CLI 拒绝覆盖）。崩溃报告是否含转储由 `IncludeCrashDump` 决定（`--dump yes`，体量大）。全程不上传任何内容。
5. **测试替身**：`settings` 真实维护 `launcher-settings.json`（首次按 `cli-profiles.json` 的 selected 播种），校验 profile/war/yes-no/strangers/forge-credits；`logs` 扫描 `logs\sessions\<id>\`（`run.json` 可给 status/failure/gameExitCode/crashed/startedUtc），`--export`/`--crash-report` 真的生成 zip 并遵守“目标已存在即拒绝”。

**残留风险/后续**：① 设置保存后不重新拉取 `profiles`/`status`，极端情况下 CLI 侧被其他工具同时改动，界面要等下次刷新才同步；② 日志列表按目录名倒序仅为替身行为，真实 CLI 以 startedUtc 排序，UI 不做二次排序（信任 CLI）；③ 保存日志/崩溃报告的文件对话框在无头测试中不可用，测试直接调用 VM 方法（与 `LogView` 相同取舍）；④ `LoadAsync` 把 `settings`/`logs` 作为硬依赖：低于 0.4.0、没有 `logs` 命令的旧 CLI 会让专页刷新失败（阶段②/③ 已要求模组与 play 命令，版本要求本就较高；如要兼容旧版，需按 `status.hellforge` 版本降级隐藏日志卡）。

## 测试与验证

### 测试替身与夹具

- `tests/HD2Mimnt.HellForgeCliStub/`：程序集名 `HellForge.Cli` 的控制台替身。`--help` 分支**必须在 `--install` 校验之前**（探测不带 `--install`）；按命令输出安装根目录预置的 `cli-<command>.json`；参数记录到 `cli-args.txt`；`cli-fail.txt` 模拟失败；`play` 逐行输出 `cli-play.jsonl`（20ms 间隔），`cli-play-hold.txt` 存在时等待 `stop` 写入的 `cli-stop-signal.txt` 再输出 `ended observed`（模拟运行中→停止）；退出码可用 `cli-play-exit.txt` 指定。**模组命令（`mods`/`mod-add`/`mod-remove`/`mod-switch`/`mod-order`/`mod-pack`）真实维护 `mods\` 文件夹与 `hellforge-mods.json`**（disabled/order/packs），`mods` 列表反映增删/开关/排序/选项，供同步与 UI 测试断言实际落位。**`settings` 真实维护 `launcher-settings.json`**（首次按 profiles 的 selected 播种，支持 profile/war/yes-no/strangers/forge-credits 校验，strangers 与 forge-credits 落在 `mods\hellforge-mods.json`）；**`logs` 真实扫描 `logs\sessions\<id>\`**（`run.json` 提供 status/failure/gameExitCode/crashed/startedUtc），`--export`/`--crash-report` 生成 zip（目标已存在则拒绝，`--dump yes` 才收转储）。
- `tests/HD2Mimnt.Tests/Services/HellForgeCliFixture.cs`（链接进 UITests）：搭建假安装（`hellforge-install.json`、`game\{data,tools,bin}`、复制替身到 `app\cli`）、预置 status/profiles 响应；`SetPlayStream(jsonl, hold, exitCode)`、`SetFailure()`、`OverwriteResponse()`、`ReadArgs()`。

### 现有测试

- 单元：`HellForgeServiceTests`（13，状态/档案/失败与超时；settings 解析与保存参数、logs 列表与单次文件、导出 zip 覆盖旧文件、崩溃报告 `--dump yes`、无会话导出报错）、`HellForgeSessionServiceTests`（6，事件流/失败/停止/外部校准）、`HellForgeModExporterTests`（15，pack 判定/扁平化重编号/legacy 选择/多 bundle/透传内容与垃圾剔除/合成根清单（含根目录无 manifest 的复合包）/重名选项与变体改名/禁用选项目录仍导出/指纹/空补丁/名称版本截断）、`HellForgeModSyncServiceTests`（21，未变化同步只跑 status 且不重写状态文件/状态文件保留他人条目与未知字段/复合包重名选项按索引各自开关/导入与取反排序/禁用跳过/加载器与 IAS 跳过/会话拦截/未变化跳过（按落盘文件时间戳断言未重写）/陈旧移除/pack 选项与变体/清理/状态/game\data 零写入/复制方式落真实文件/硬链接与库文件同一份（`FileIdentity.IsSameFile`）/建链失败回退复制并警告/内容变化替换目录不留重复/同 id pack 后一个降级扁平/复合包（根目录无 manifest）被识别/遗留 `id-N` 副本清理/旧导出缓存清理）。
- UI：`HellForgeUiTests`（2：档案管理与自动导航；库页启动→进度→停止全流程）、`HellForgeGameSourceUiTests`（设置单选切换）、`HellForgeModSyncUiTests`（1：配置页部署→按部署方式直接写 `mods\hd2mimnt.<N>\`（断言不含 `mod-add`）→未变化不重写→专页同步状态卡显示来源配置→清理移除→状态卡回到未同步，经真实 DI + LoopbackWorkerHost + CLI 替身）、`HellForgeSettingsLogsUiTests`（2：设置卡显示 CLI 值→改动两项→只发差异参数→按钮状态回正；日志列表最新在前→导出 zip→勾选转储后崩溃报告含 dump，断言通知文案）。

### 运行方式（重要）

```bash
dotnet build HD2Mimnt.slnx --no-restore
# UI 测试必须一次一个进程（合并运行会挂起，见下）
dotnet test tests/HD2Mimnt.UITests/HD2Mimnt.UITests.csproj --no-build --no-restore \
  --filter "FullyQualifiedName~HellForge_ShouldStreamSessionProgressFromLibraryLaunchAndStop"
# 单元测试可批量
dotnet test tests/HD2Mimnt.Tests/HD2Mimnt.Tests.csproj --no-build --no-restore \
  --filter "FullyQualifiedName~HellForge|FullyQualifiedName~GameDetection|FullyQualifiedName~SettingsViewModel|FullyQualifiedName~TranslationCatalog"
```

## 易错点与正确做法

1. **通知吐司遮挡按钮**：无头 UI 测试中点击前必须 `main.NotificationPanelViewModel.DismissAllToasts()` + Layout，否则命中测试打到吐司 Border（启动成功吐司正好盖住右上角停止按钮）。
2. **`--help` 探测不带 `--install`**：`GameDetectionService.SupportsPlayCommandAsync` 直接 `cli --help`；替身/真实 CLI 都必须在参数校验前应答 help。
3. **无头 UI 测试合并运行挂起**：多个 UI 测试同进程会因空闲上下文污染挂死；一次进程只跑一个测试方法。过滤器匹配类名/方法名而非文件名（所有 UI 测试都是 `partial class FluentAvaloniaUiTests`）。
4. **`TranslatedPages_ShouldRenderAtMinimumSizeAndKeepUserGroupNames` 在 HEAD 上预先存在失败**（已用基线 worktree 验证，非 HF 改动引入）；回归时跳过或单独确认。
5. **XAML 注释不能含 `--`**（AVLN1001）：写 `play --wait` 之类要改措辞。
6. **隐藏的 TabItem 必须本地 `IsVisible="False"`**；导航项显隐绑 `ShowHellForgeEntry`，测试遍历导航时跳过隐藏项。
7. **布局验证必须含渲染帧**：`Layout()` = `Dispatcher.UIThread.RunJobs()` + `UpdateLayout()` + `CaptureRenderedFrame()`。
8. **三语 resx 键/参数必须一致**（TranslationCatalogTests）；新增 `HellForge.*` 键要同时改 en/zh-CN/zh-TW。
9. **会话服务 Dispose 只杀观察进程**，不要顺手 `stop`——关闭管理器不应终止玩家的游戏。
10. 共享工作区：其他会话可能并行构建/提交，跑测试前确认无冲突进程；保留他人未提交修改。
11. **`Process.Kill(entireProcessTree: true)` 抛 `AggregateException`**：子进程已退出时它把 `Win32Exception`（句柄无效）包在 `AggregateException` 里，只 catch `Win32Exception`/`InvalidOperationException` 会漏接，导致会话服务 Dispose/CLI 超时杀进程偶发崩溃。两处 catch 均已补 `AggregateException`（`HellForgeSessionService.Dispose`、`HellForgeService.RunJsonAsync` 超时路径）。
12. **无头 UI 断言控件状态前要等绑定传播**：VM 标志（如 `IsSessionRunning`）翻转与按钮 `IsEnabled` 绑定更新之间有调度间隙；`WaitForAsync` 只等 VM 标志会偶发断言失败。应等实际断言的控件状态 settled（如 `StopSessionButton.IsEffectivelyEnabled`）再断言。
13. **专页新增卡片会下推交互内容致无头点击错位**：HF 页同步状态卡最初放在档案列表前，把档案行“切换”按钮推到视口边缘，`Click` 命中 TextBlock 而非按钮。只读状态卡应置于档案列表**之后**，不位移主要交互内容；新增页面区块后须重跑既有点击类 UI 测试。
14. **`--strangers` 只收 `yes|no|default`**：不是 `on/off`；`on/off` 是 `hellforge-mods.json` 里的存储值。UI 侧 `StrangersValue` 已做 true→yes、false→no、null→default 映射，改保存逻辑时勿把存储值直接发给 CLI。
15. **导出日志/崩溃报告前必须先删目标**：CLI 对已存在的 zip 直接报错；`HellForgeService.PrepareExportTarget` 先删再调用，保存对话框的“覆盖”确认因此可用。导出 5 分钟、崩溃报告 10 分钟超时（含转储时文件很大）。
16. **全量单元测试有与 HF 无关的既有失败**：2026-11-12 本机全量 `HD2Mimnt.Tests` 失败 15 个（`LibraryViewModelSelectionTests` 7、`Hd2IASIntegrationTests` 5、`PluginContributionRefreshTests` 1、`SameGuidImportUpdateTests` 2），单独复跑仍失败且堆栈都在对应模块；HF 回归只认 `--filter "FullyQualifiedName~HellForge"`（单元 86，UI 6）。
17. **`mod-add` 恒为文件复制，落盘必须自己写**：上游 `ModInstaller.Add` → `CopyFolder` 全是 `File.Copy`，CLI 没有“链接导入”选项；同时 `ModInstaller.Delete` 走 `LocalInstall.NoLinks`，路径上任何一级是链接都会拒绝删除。因此部署方式只能在 HD2Mimnt 侧实现，且**链接必须是文件级**——把整个模组目录做成目录链接会让 `mod-remove`/清理失败。
18. **pack 的 id 由清单 `Guid` 推导**：上游 `CosmeticPacks.Parse` 从 `manifest.json` 的 `Guid` 推导 `pack.<Guid>`（无 Guid 时 `pack.<名称 slug>`），清单里没有 `id` 字段。我们合成的清单写的就是 `mod.Manifest.Guid`，所以 `ResolveModId` 的预计算值与实际 id 一致，可直接用于目录命名；写完后仍重查 `mods` **按目录路径校正 id** 并以实际 id 做启用/排序/选项与状态记录，避免上游规则变化时错位。
19. **写入必须经点前缀临时目录**：`ModCatalog.Load` 跳过 `.` 开头的目录，导出先写 `mods\.hd2mimnt-xxxxxxxx` 再 `Directory.Move` 整体改名，会话与 Mods 页就不会读到半个模组；临时目录与最终目录同卷，硬链接与改名都成立。同步失败时删掉临时目录，不留垃圾。
20. **部署方式不可用要显式回退**：硬链接要求库与安装根同卷，Windows 符号链接要求管理员（`SymbolicLinkDeploymentMethod.CheckCompatibilityAsync`），预检失败或单文件建链失败都回退文件复制，并把“已回退”写进结果文案与 `Warnings`；`Method` 字段按实际方式报告，不要仍显示硬链接。
21. **pack 必须自带 HellForge 认得的根清单**：`ModCatalog.Load` 只收「有 `hellforge-mod.json`／`bts-mod.json`」或「`CosmeticPacks.IsPack` 为真」的目录；后者要求根目录有补丁文件，或 `manifest.json` 含**精确键名** `Options`/`Guid`（大小写敏感）。复合包（角色包/HD2IAS 把多个子包合并成一个模组）根目录本来就没有 `manifest.json`，只搬 Include 目录会让整个模组被**静默忽略**——`mods` 不列出，`problems` 也不报，同步只好报“未识别”。所以导出时**总是按 HD2Mimnt 清单合成**根 `manifest.json`；也不能先硬链接作者 manifest 再覆写（会写穿库文件），所以根清单压根不进复制列表。
22. **模组目录名固定为 id 才能收敛**：早期实现用“空闲名 + 序号”，一旦目录未被 HellForge 识别就永远列不出来，下次同步又新开 `id-2/id-3…`（用户机上同一外观包叠了 7 份）。现在固定 `mods\<id>`，写入前清掉同名与 `id-<数字>` 的遗留目录，但只删「带本管理器标记 `.hd2mimnt-mod.json`」或「HellForge 认不出（无任何清单）」的目录，别人的模组不动；同步开局再清一遍点前缀临时目录。
24. **重名选项会互相覆盖**：HellForge 的 `CosmeticPacks.Choice` 用**选项名**做键（`Off` 是 HashSet、`Sub` 是 Dictionary），而复合包合并后各子包的选项平铺到同一层，`本体`/`上衣` 这类重名极常见。直接把库清单原样写成根 `manifest.json` 会让 `mod-pack --option 本体 --on no` 一次关掉两个选项，我们的 diff 代码也会在建名字字典时抛 `An item with the same key has already been added. Key: 本体`。所以 `PackOptionNames`/`PackSubOptionNames` 在导出时给重复项追加序号（`本体`、`本体 (2)`，变体同理），同步侧**按索引**对齐 `mods` 列表与清单（数量一致时最可靠，否则按改名后的名字查），CLI 传参也用改名后的值。改名只影响显示与 CLI 键，`Include` 原样保留，部署内容不变。
25. **改导出内容要同时递增 `ExportFormat`**：指纹只算文件 stat（pack 不算清单内容），清单写法变了指纹却不变，已同步的模组会被判为“未变化”跳过，旧目录（含坏清单）永远留着。`ComputeFingerprint` 里带 `fmt<ExportFormat>`，布局一改就 +1，下次同步自动重导一次。
26. **`mods`/`mod-switch`/`mod-pack` 都慢，别在循环里调**：`mods` 会给联机 modlist 码做内容摘要，`mod-switch`/`mod-pack` 每次都会重新扫描并解析整个 `mods\`——60 个模组时单条 5 秒上下，而 `status`/`mod-order` 只要 0.3 秒（`mod-order` 顺序没变会早退）。所以：状态一律本地读 `hellforge-mods.json` + 扫目录，开关与 pack 选项按官方文档**一次写入**该文件（`disabled`/`packs`），绝不逐项调 CLI；只有真的写入了新目录才跑一次 `mods` 做校验。
27. **写 `hellforge-mods.json` 要“像是在改别人的配置”**：读的时候大小写不敏感（历史文件可能 PascalCase），写的时候只动自己的 id（`disabled` 移除自己、`packs` 只改自己的键）、其余字段与别人的条目原样保留；`sub` 即使为空也要写（HellForge 自己的保存也带这一项，省略会让每次比对都判成有变化而反复落盘）；临时文件 + `File.Move(overwrite)` 原子替换，内容没变就不动文件（HellForge 的 Mods 页靠文件时间判断外部改动）。

## 外部参考

- HellForge 上游仓库：<https://github.com/SkyeShade/HellForge>（GPL-3.0，许可证见仓库 `LICENSE`；本地参考副本 `G:\Temp\Githud\HellForge`）。
- 外部管理器接口全文：HellForge 仓库 `docs/external-launchers.md`（命令、JSON、事件、日志、大厅浏览器 API）。
- 模组形态与补丁规则：HellForge 仓库 `docs/modding/data.md`、`docs/first-mod.md`、`docs/mods.md`。
- 本项目相关指南：[`ui-and-avalonia.md`](../agent-guides/ui-and-avalonia.md)（导航/无头测试）、[`deployment-and-loaders.md`](../agent-guides/deployment-and-loaders.md)（部署语义）、[`testing-and-lifecycle.md`](../agent-guides/testing-and-lifecycle.md)。