# 部署、配置与加载器

涉及 DeploymentService、ProfileService、补丁规划/清理、冲突检测或任一 Lua 加载器（BSL/MDL/BML/CSL/SmoothBoot）时先读本文件；配置页排序交互另读 [UI 指南](ui-and-avalonia.md)。

## 易错点与正确做法

- HD2IAS 卸载不能只保留 ReShade 注入器而删除仍被配置引用的字体。删除运行文件前，定点恢复/清空 ReShade.ini 中指向当前 `HD2IndependentRig/fonts/` 的字体设置，保留用户自定义字体和其它配置；配置修改也须参加同一备份与失败/取消回滚。见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md)，依据为用户卸载后的字体缺失反馈；来源：[ReShade 字体选择实现](https://github.com/crosire/reshade/blob/v6.6.2/source/runtime_gui.cpp)、[许可证](https://github.com/crosire/reshade/blob/v6.6.2/LICENSE.md)。

- HD2IAS 卸载与安装复用同一备份/事务恢复流程，只移除管理器登记且实际哈希仍匹配的系统文件。`dxgi.dll` 始终保留，不能因它曾列入 HD2IAS 清单就删除 ReShade；退役清理也不能删除它。用户 INI、未知或外部修改文件及普通 data 模组保留。卸载提交须留下可识别的完成记录，恢复不能把已成功卸载的文件装回来。库中存在真实 HD2IAS data、运行时缺失或不完整时给本地安装提示，不能只看模组名称或 build.json，更不能因此自动联网安装。该管理页与自动检查自 2026-10-28 起暂时停用（上游大改内测），恢复接线后才重新生效。见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md)，依据为用户明确要求；来源：[官方运行包](https://github.com/Estecsky/HD2-Integrated-Armor-System)。

- HD2IAS 降级不能仅依赖旧包的 `previous_sha256`，旧包不含未来版本哈希。覆盖校验须接受活动安装记录中同路径且实际哈希完全一致的系统文件；外部修改后仍拦截，不能按版本声明放行。`HD2IndependentRig.build.json` 是系统元数据，须同步部署并参与备份回滚，不能作为用户配置永久保留。仅对这个已知源文件补 `bin/HD2IndependentRig.build.json` 目标，其它 `target=null` 仍不部署。旧未分类记录不提供所有权，必要时用当前活动版本的已校验缓存包补足构建记录的认可哈希。见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md)，依据为用户提供的 1.4.2 / 1.4.5 包及降级报错；来源：[官方更新目录](https://github.com/Estecsky/HD2-Integrated-Armor-System/tree/main/updates)。

- HD2IAS 本机版本读取 `bin/HD2IndependentRig.build.json`（`HD2IndependentRigBuild1`），JSON 的版本与路线声明不能代替实际载荷哈希校验。`HD2IndependentRig` 下现有 `runtime.ini`、`free_parts_selections.ini`、`physics_control.ini` 保存运行选项或用户选择，未知 INI 也保留；`helmet_manifest.ini`、`transmog_manifest.ini` 属于系统目录清单，仍校验哈希。文件处理计划中哈希相同标记跳过，认可旧哈希可规划更新，来源未知不能自动覆盖。覆盖式安装须先校验并暂存完整载荷、备份真实旧文件、持久化变更日志，再写目标并发布独立活动记录；失败或取消恢复真实旧状态，不删除整个 bin。复用 CopyDeploymentMethod 时须在返回后检查 CancellationToken，其取消路径可能返回 false，不能把取消误报为复制失败。运行时独立管理，生成的纯 data 则按用户最新要求通过 ModService 加入模组库；已有模块更新原 ID 并保留配置引用。data 不直接写游戏、不预留特殊 patch_0，复用普通配置部署和重编号。当前覆盖式流程待用户真机测试，先卸后装暂不启用。见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md)，来源：[官方更新记录](https://raw.githubusercontent.com/Estecsky/HD2-Integrated-Armor-System/refs/heads/main/UPDATE_HISTORY.txt)及用户提供的 1.4.4 包和本机配置样本。

- “设置 → 配置文件 → 锁定加载器”保存为 `Profiles.LockLoaders`，缺省为 true。开启时沿用固定分组、不可选加载器和优先级规则；关闭时 Lua 依赖、主加载器互斥组成员、SmoothBoot 按普通模组选择、拖动和数字定位，用户分隔符也按全部成员计数。ProfileService 不能在读取或保存时偷偷恢复固定排序；部署、冲突和离线接口共用带开关参数的 `BslProfilePolicy.DeploymentSequence`，关闭时对完整启用列表应用部署方向。主加载器互斥和部署依赖校验继续生效，批量切换加载器后回填全部启用状态。重新开启后按原规则归组，切换开关需刷新配置卡片并使旧冲突结果失效。新增服务依赖同步修改 App 的手动 DI 工厂。来源：https://learn.microsoft.com/dotnet/core/extensions/dependency-injection 、https://github.com/CowboyBingus/BingusSharedLoader/blob/main/docs/AUTHORING.md

- 离线接口固定留在 BootstrapPath 的 `interop` 目录，不能跟可迁移数据库一起搬走；旧主版本由保留的适配器持续生成。库、配置与部署必须从独立只读事务取得，后台不共享 UI 的 SQLite 连接；部署选项/版本读取当时快照，旧 null 不得用当前配置补造。预登记目标不等于已部署文件，导出需核对实际存在并列出缺失目标。契约见[离线模组接口](../features/OFFLINE_MOD_INTERFACE.md)。来源：https://www.sqlite.org/isolation.html 、https://semver.org/

- 部署记录表示**当前安装状态**：`DeploymentRecords` 每个 `(GamePath, ModId)` 一行，`DeploymentFiles` 记录当前目标；`SessionId` 只表示最近一次对账。部署先写临时文件、再单事务登记当前状态、最后落位；不能重新引入“清理上一次会话再全量重建记录”。临时写入阶段可取消且不产生最终目标；移动/落位阶段不响应取消。登记失败不得落位；落位失败时数据库已指向期望状态，下次对账按 `SourceKey` 和目标内容自愈。文件复制仍按模组批量提交，不能逐文件提交 SQLite。来源：https://learn.microsoft.com/dotnet/standard/data/sqlite/bulk-insert 、https://learn.microsoft.com/dotnet/api/system.io.file.move

- 大补丁复制使用池化 1MiB 缓冲区、顺序读取和目标空间预分配，关闭 FileStream 的额外缓冲；仍逐块检查取消，并在流关闭后删除取消产生的副本。预取消不能删除现有目标。优化用同一数据集交替测量并校验内容；热缓存复制及数据库登记的局部提速不能当作完整游戏部署提速。来源：https://learn.microsoft.com/dotnet/api/system.io.filestreamoptions

- 崩溃排查使用独立 `CrashDeploymentSnapshots` 保留最近部署及正常基准，不能依赖清理后会删除的部署记录，也不能用当前库/配置补造历史。时间匹配读取 minidump 文件头的 UTC 时间，排除崩溃后部署；比较共同成员的相对顺序，避免新增项引起索引平移后误报所有模组顺序改变。版本、库哈希与文件元数据变化只是候选线索，不能直接定为崩溃原因。功能边界见[崩溃排查](../features/CRASH_DIAGNOSTICS.md)。来源：https://github.com/rust-minidump/rust-minidump/blob/v0.27.0/minidump-processor/json-schema.md 、https://learn.microsoft.com/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_header

- 冲突检测比较实际选中补丁内的 `(Type ID, Resource ID)`，不能按原补丁名判断（部署会重编号）。文件集与补丁数值顺序复用 `PatchDeploymentPlanner`，模组顺序复用 `BslProfilePolicy`；同模组多次提供资源不算自冲突，预期生效者取实际部署末项。版本/主数据/Stream/GPU 大小相同只能说明潜在覆盖，不能证明内容相同或游戏兼容；损坏补丁单独列出并标记检测不完整。测试应生成真实 TOC，覆盖异名同资源、同名异资源、相同 ID 不同类型、正反部署方向及选项过滤。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition

- BSL 扫描的正常取消使用协作式退出并将公开任务完成为 Canceled，不能返回成功的 None/null 或缓存识别版本；已知 GUID 快速路径及写入缓存前也必须检查取消。调试器的“抛出时中断”可能停在已处理的取消异常上，须区分调试中断与未处理崩溃，不能用吞掉取消异常掩盖问题。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 加载器 GUID 不是唯一识别依据：GUID 改变/缺失时以合法 TOC + Lua envelope 的独有资源入口兜底；启动类资源只在 Lua 正文同时包含配置的全部标记才认定，不能把普通 boot/Wwise 替换当作加载器。识别特征（GUID、入口资源、启动正文标记、注册模块、依赖声明模式）与排序/互斥策略全部来自 Infrastructure 随应用分发的 `Config/LoaderCatalog.json`，可用 `<AppData>/loaders.json` 覆盖（解析失败回退分发默认并记录原因），新增加载器只改配置并提升 `ruleVersion`——它同时是 `DetectionVersion`，提升后旧缓存按需重扫；快照不可变，扫描中途不读混合配置。识别结果保存规则版本；失败/取消不标记识别成功。不执行 Lua、不按包名识别。具体加载器的特征值以配置文件为事实来源，不在指南中重复罗列。启动标记不得用于认定加载器本体：Chef Shared Loader 官方允许任意模组把启动 stub 原样携带为自己的 `boot`，其识别锚点只用清单 GUID 与独有入口资源 `mods/chef_shared_loader/core_1`。来源：https://github.com/CowboyBingus/BingusSharedLoader/blob/main/src/shared_loader.lua 、https://github.com/pat-pat-patrick/boot-mod-loader/blob/main/src/boot_mod_loader.lua 、https://github.com/Th3chef/Chef-Shared-Loader

- Lua 分隔符是配置页固定的系统分组，不是伪造 Mod 或可拖动成员。规范优先级为普通模组 → Lua 依赖 → 主加载器（互斥组，成员见 `LoaderCatalog.json`）→ SmoothBoot；部署序列按 LoaderRegistry 配置的优先级数值通用分段，同段保持配置页顺序，新加载器在配置中声明 `role` 槽位与 `priority` 即自然并入，反向部署时固定区域反转置顶，SmoothBoot 类末位槽位始终最后部署。实际部署及冲突顺序共用 `BslProfilePolicy`。各加载器 GUID、Lua 入口与互斥分组以 `LoaderCatalog.json` 为准；`role` 映射固定枚举槽位（loader/mdl/bml/smoothboot），配置移除的历史角色按主加载器段兜底排序。MDL 与 SmoothBoot 的 Lua 入口、BML 的唯一 `boot` 入口及 BSL 的 `-- HD2-Addon: mods/<author>/<name>` addon 契约见配置与 `BslModDetector`。BML 与 BSL 都会启动 addon，并存会重复加载；BSL 自 2026-10-20 更新后已无 Wwise 启动入口，BML 是该更新后的替代实现。启用一个互斥组加载器时在同一次配置写入中停用其它成员；批量同时选择多个拒绝写入，部署预检再次拦截旧配置中的互斥状态。只依据 provenance `requires`、明确声明及资源契约识别依赖，不能按名称、作者或补丁前缀猜测；清单 `Version: 1` 是格式版本。UI 加载器显示名由配置聚合拼接，翻译走 `{0}` 占位模板键。依据为用户提供的 MDL 2.0.5 / SmoothBoot 3.0.22 包清单与资源及明确排序要求；BML 依据为 v0.1.0–v0.7.0 发行包清单、`boot` 资源与官方 README；通用 BSL 契约来源：https://github.com/CowboyBingus/BingusSharedLoader/blob/main/docs/AUTHORING.md 、https://github.com/CowboyBingus/BingusSharedLoader/blob/main/docs/TECHNICAL.md 、https://github.com/pat-pat-patrick/boot-mod-loader/blob/main/README.md 、https://github.com/pat-pat-patrick/boot-mod-loader/blob/main/LICENSE

- 启动与备份恢复先执行 `ProfileService.InitializeAsync`，默认配置使用数据库 `IsDefault` 标记，服务层禁止删除、排序固定首位；旧库沿用最早配置并保留内容。`IsActive` 在切换时立即落库，启动不能无条件激活默认配置，否则覆盖用户上次使用的配置。

- 冲突检查跟随配置文件页选中的配置并显示名称；模组、配置或选项变动后取消旧扫描并使结果失效。扫描必须复用部署的 `ProfileModOptionResolver` 与 Include 文件收集逻辑，应用 Enabled、OptionsJson、选项目录拍平目标路径和部署顺序，不能直接遍历所有变体制造误报。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 模组启用状态与部署状态是两个独立状态。删除模组时同步清理对应部署记录，不能只删除数据库中的模组行。

- 删除模组前必须先按 DeploymentFiles.TargetPath 清理游戏文件，成功后才删除部署记录和模组；文件清理失败或取消保留记录以供重试，不能直接丢失追踪。删除提交必须显式删除 `DeploymentFiles` 行，不能只依赖外键级联；按模组所在的每个 `GamePath` 分别压缩剩余编号。清理使用每个文件的 Method（旧空值才回退记录方式），链接部署的普通占位文件用 Copy；符号链接源文件缺失时仍要删除链接本身，不能仅用 File.Exists 判断。来源：https://learn.microsoft.com/dotnet/api/system.io.fileinfo.linktarget

- 删除已部署模组后按现有游戏目标编号保留实际部署顺序，为每个资源前缀独立从 patch_0 补齐连续编号；主文件、stream、gpu_resources 和占位文件一起移动，库内原文件不改名。改名完成后在同一数据库事务中更新剩余 TargetPath 并删除被移除模组的部署记录；移动或提交失败逆序回滚改名，不能覆盖未知目标文件或留下追踪旧路径的记录。压缩编号前先判断目标是否仍存在（悬空符号链接算存在，与部署对账判断一致）：追踪文件被外部删除时跳过物理移动但仍同步记录编号，否则删除模组会因 `FileNotFoundException` 整体失败；缺失内容由下次部署对账按内容不匹配重写。依据：2026-10-25 用户手动删除游戏 data 目录后删除模组报 Could not find file。来源：https://learn.microsoft.com/dotnet/api/system.io.file.move

- 配置文件批量启用/禁用必须在 ProfileService 内一次读取、一次更新；启用不在 ProfileMods 中的模组要先加入并分配稳定顺序，不能在 UI 中逐条提交。

- 部署清理只有在所有目标文件成功清理后才能删除 DeploymentRecord；任一文件失败必须保留 DeploymentFiles 和记录供重试，不能用会话级删除覆盖失败状态。清理只处理当前生效 `GamePath` 的当前状态；没有当前记录时才允许回退清理未归属旧记录，绝不能按会话误删其它安装的部署。

- 部署清理的最终进度消息要带 SessionId 或当前安装路径，便于任务中心把结果与失败恢复记录对应起来；测试断言应覆盖前缀和会话/安装标识。

- 游戏来源分官方安装与 HellForge 离线服，两者互斥。`Game.Path` 只保存官方目录，`Game.Mode` 与 `Game.HellForgePath` 决定当前来源；所有业务服务必须通过 `SettingsService.GetEffectiveGamePath()` 读取，HellForge 解析为 `<安装根>\game`，不能直接读 `Game.Path` 或按目录名猜来源。设置页只校验当前来源，另一个目录仅保留供切回。HellForge 部署仍写入 `game\data`；启动优先执行 `app\cli\HellForge.Cli.exe play --install <安装根>`，旧版 CLI 没有 `play` 时必须探测帮助输出并回退到 `HellForge.exe`，不能按版本号硬编码。来源：[HellForge 外部启动器](https://github.com/SkyeShade/HellForge/blob/main/docs/external-launchers.md)、[安装目录](https://github.com/SkyeShade/HellForge/blob/main/docs/README.md#the-installations-folders)、[数据模组](https://github.com/SkyeShade/HellForge/blob/main/docs/modding/data.md)

- 游戏路径是安装根目录，补丁部署目标固定为其 `data` 子目录。只读取根目录或选中 Include 目录直属的合法补丁，不能递归部署未选变体或复制 manifest/预览图。每个资源前缀只按自己的原编号数值排序，再按配置顺序从 0 连续重编号；禁止跨 bundle 取原编号并集，否则不同编号（如 A.patch_32 / B.patch_18）会产生没有来源的空 base。缺失或零字节 base 必须在任何写入/落位前拒绝。**缺失的 `.stream` / `.gpu_resources` 不生成 0 字节占位**——游戏加载占位文件会直接报错崩溃（2026-10-25 用户真机反馈推翻旧约定），缺什么就不部署什么；历史部署残留的占位会在下次对账清理阶段自动删除。写入后再次检查 base 非零，冲突扫描也须把空 base 报为损坏，不能视为无资源的合法占位。所有最终目标在落位前登记；临时写入取消不产生最终目标。回归见 `DeploymentServiceIntegrationTests` 和 `ConflictDetectionServiceTests`。旧版 HD2PhysBone 兼容骨骼功能已废弃。依据：2026-10-17 用户报告与 2026-10-25 用户占位崩溃反馈；来源：https://learn.microsoft.com/dotnet/api/system.io.fileinfo.length 、https://learn.microsoft.com/windows/win32/fileio/hard-links-and-junctions

- 部署是**记录驱动的对账**：当前状态 = `(GamePath, ModId)` 的 DeploymentFiles，不再按“上次会话”匹配。文件身份用 `SourceKey`：普通补丁为 `patch:<相对模组根目录的源路径>`，HD2IAS 原生文件为 `native:<游戏根相对目标>`；模组更新后落在新的 GUID 目录不会改变键。旧记录没有键时先按 `(ModId, TargetPath)` 兜底，首次对账成功后回填。匹配顺序为键优先、目标兜底；同目标内容/链接身份未变则保留，编号位移则两阶段改名（先 `.hd2mimnt_stage_*` 避免互换占位），内容或方法变化才写入。Copy 必须流式比对内容（同长度也要比对，防外部篡改）、HardLink 用卷序列号+文件索引+链接数≥2、SymbolicLink 核对 LinkTarget；切换部署方式即使内容相同也必须重写为目标方式。改名是纯元数据操作，阶段内不响应取消；失败逆序回滚，残留临时名由下次对账清扫。完全一致时不写文件、不新建 Session；手动部署对整份配置对账，模组更新/切换版本后只对该模组自动增量对账，其他模组仅在编号位移时移动，不应用配置页里尚未部署的启用/排序/选项改动。自动部署只在当前安装存在该模组记录、记录所属配置仍启用该模组时执行；更新已提交但自动部署失败或取消不回滚更新，记录保留供再次部署或清理。依据：2026-10-25 用户“只部署变化模组、其余改名即可”和“更新后直接增量”要求；来源：https://learn.microsoft.com/dotnet/api/system.io.fileinfo.length 、https://learn.microsoft.com/dotnet/api/system.io.fileinfo.linktarget 、https://learn.microsoft.com/dotnet/api/system.io.file.move
