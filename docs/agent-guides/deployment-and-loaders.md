# 部署、配置与加载器

涉及 DeploymentService、ProfileService、补丁规划/清理、冲突检测或 BSL/MDL/SmoothBoot 时先读本文件；配置页排序交互另读 [UI 指南](ui-and-avalonia.md)。

## 易错点与正确做法

- “设置 → 配置文件 → 锁定加载器”保存为 `Profiles.LockLoaders`，缺省为 true。开启时沿用固定分组、不可选加载器和优先级规则；关闭时 Lua 依赖、BSL/MDL、SmoothBoot 按普通模组选择、拖动和数字定位，用户分隔符也按全部成员计数。ProfileService 不能在读取或保存时偷偷恢复固定排序；部署、冲突和离线接口共用带开关参数的 `BslProfilePolicy.DeploymentSequence`，关闭时对完整启用列表应用部署方向。BSL/MDL 互斥和部署依赖校验继续生效，批量切换加载器后回填全部启用状态。重新开启后按原规则归组，切换开关需刷新配置卡片并使旧冲突结果失效。新增服务依赖同步修改 App 的手动 DI 工厂。来源：https://learn.microsoft.com/dotnet/core/extensions/dependency-injection 、https://github.com/CowboyBingus/BingusSharedLoader/blob/main/docs/AUTHORING.md

- 离线接口固定留在 BootstrapPath 的 `interop` 目录，不能跟可迁移数据库一起搬走；旧主版本由保留的适配器持续生成。库、配置与部署必须从独立只读事务取得，后台不共享 UI 的 SQLite 连接；部署选项/版本读取当时快照，旧 null 不得用当前配置补造。预登记目标不等于已部署文件，导出需核对实际存在并列出缺失目标。契约见[离线模组接口](../features/OFFLINE_MOD_INTERFACE.md)。来源：https://www.sqlite.org/isolation.html 、https://semver.org/

- 文件复制部署按模组在一次事务中预登记全部目标，再开始写文件，不能每个文件单独提交 SQLite，也不能复制完后补登记。占位文件独立记录为 Copy；批量登记失败必须回滚且不写目标，复制取消后保留记录供清理。批量预登记可能包含尚未写入的目标，测试不能再假定取消后仅有一条文件记录。来源：https://learn.microsoft.com/dotnet/standard/data/sqlite/bulk-insert

- 大补丁复制使用池化 1MiB 缓冲区、顺序读取和目标空间预分配，关闭 FileStream 的额外缓冲；仍逐块检查取消，并在流关闭后删除取消产生的副本。预取消不能删除现有目标。优化用同一数据集交替测量并校验内容；热缓存复制及数据库登记的局部提速不能当作完整游戏部署提速。来源：https://learn.microsoft.com/dotnet/api/system.io.filestreamoptions

- 崩溃排查使用独立 `CrashDeploymentSnapshots` 保留最近部署及正常基准，不能依赖清理后会删除的部署记录，也不能用当前库/配置补造历史。时间匹配读取 minidump 文件头的 UTC 时间，排除崩溃后部署；比较共同成员的相对顺序，避免新增项引起索引平移后误报所有模组顺序改变。版本、库哈希与文件元数据变化只是候选线索，不能直接定为崩溃原因。功能边界见[崩溃排查](../features/CRASH_DIAGNOSTICS.md)。来源：https://github.com/rust-minidump/rust-minidump/blob/v0.27.0/minidump-processor/json-schema.md 、https://learn.microsoft.com/windows/win32/api/minidumpapiset/ns-minidumpapiset-minidump_header

- 冲突检测比较实际选中补丁内的 `(Type ID, Resource ID)`，不能按原补丁名判断（部署会重编号）。文件集与补丁数值顺序复用 `PatchDeploymentPlanner`，模组顺序复用 `BslProfilePolicy`；同模组多次提供资源不算自冲突，预期生效者取实际部署末项。版本/主数据/Stream/GPU 大小相同只能说明潜在覆盖，不能证明内容相同或游戏兼容；损坏补丁单独列出并标记检测不完整。测试应生成真实 TOC，覆盖异名同资源、同名异资源、相同 ID 不同类型、正反部署方向及选项过滤。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition

- BSL 扫描的正常取消使用协作式退出并将公开任务完成为 Canceled，不能返回成功的 None/null 或缓存识别版本；已知 GUID 快速路径及写入缓存前也必须检查取消。调试器的“抛出时中断”可能停在已处理的取消异常上，须区分调试中断与未处理崩溃，不能用吞掉取消异常掩盖问题。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 加载器 GUID 不是唯一识别依据：GUID 改变/缺失时以合法 TOC + Lua envelope 的独有资源入口兜底 MDL/SmoothBoot；BSL 必须同时匹配 Wwise 启动资源、`CowboyBingusModLoader`、`BingusSharedLoader.log`、`mods/cowboybingus/`，不能把 MDL 兼容 API 或其它 Wwise 替换认作 BSL。识别结果保存规则版本，规则升级后按需重扫旧缓存；失败/取消不标记识别成功。不执行 Lua、不按包名识别。来源：https://github.com/CowboyBingus/BingusSharedLoader/blob/main/src/shared_loader.lua

- Lua 分隔符是配置页固定的系统分组，不是伪造 Mod 或可拖动成员。规范优先级为普通模组 → Lua 依赖 → BSL / MDL（二选一）→ SmoothBoot；反向部署时固定区域反转置顶，SmoothBoot 始终最后部署。实际部署及冲突顺序共用 `BslProfilePolicy`。BSL GUID `612eaf70-d682-43c7-9efd-16dcc695f977`、MDL GUID `761188b2-c45b-4607-9241-f89ca221c47b`、SmoothBoot GUID `7c3e9a52-1b84-4f6a-9c2d-8e5f0a1b2c3d` 的特殊身份优先于通用依赖描述；MDL 与 SmoothBoot 的 Lua 入口为 `mods/mdl/runtime`、`mods/codex/smoothboot`。启用一个加载器时在同一次配置写入中停用其它加载器；批量同时选择多个加载器拒绝写入，部署预检再次拦截旧配置中的互斥状态。只依据 provenance `requires`、明确声明及资源契约识别依赖，不能按名称、作者或补丁前缀猜测；清单 `Version: 1` 是格式版本。依据为用户提供的 MDL 2.0.5 / SmoothBoot 3.0.22 包清单与资源及明确排序要求；通用 BSL 契约来源：https://github.com/CowboyBingus/BingusSharedLoader/blob/main/docs/AUTHORING.md

- 启动与备份恢复先执行 `ProfileService.InitializeAsync`，默认配置使用数据库 `IsDefault` 标记，服务层禁止删除、排序固定首位；旧库沿用最早配置并保留内容。`IsActive` 在切换时立即落库，启动不能无条件激活默认配置，否则覆盖用户上次使用的配置。

- 冲突检查跟随配置文件页选中的配置并显示名称；模组、配置或选项变动后取消旧扫描并使结果失效。扫描必须复用部署的 `ProfileModOptionResolver` 与 Include 文件收集逻辑，应用 Enabled、OptionsJson、选项目录拍平目标路径和部署顺序，不能直接遍历所有变体制造误报。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 模组启用状态与部署状态是两个独立状态。删除模组时同步清理对应部署记录，不能只删除数据库中的模组行。

- 删除模组前必须先按 DeploymentFiles.TargetPath 清理游戏文件，成功后才删除部署记录和模组；文件清理失败或取消保留记录以供重试，不能直接调用 DeleteByModIdAsync 丢失追踪。清理使用每个文件的 Method（旧空值才回退会话方式），链接部署的普通占位文件用 Copy；符号链接源文件缺失时仍要删除链接本身，不能仅用 File.Exists 判断。来源：https://learn.microsoft.com/dotnet/api/system.io.fileinfo.linktarget

- 删除已部署模组后按现有游戏目标编号保留实际部署顺序，为每个资源前缀独立从 patch_0 补齐连续编号；主文件、stream、gpu_resources 和占位文件一起移动，库内原文件不改名。改名完成后在同一数据库事务中更新剩余 TargetPath 并删除被移除模组的部署记录；移动或提交失败逆序回滚改名，不能覆盖未知目标文件或留下追踪旧路径的记录。来源：https://learn.microsoft.com/dotnet/api/system.io.file.move

- 配置文件批量启用/禁用必须在 ProfileService 内一次读取、一次更新；启用不在 ProfileMods 中的模组要先加入并分配稳定顺序，不能在 UI 中逐条提交。

- 部署清理只有在所有目标文件成功清理后才能删除 DeploymentRecord；任一文件失败必须保留 DeploymentFiles 和记录供重试，不能用会话级删除覆盖失败状态。

- 部署清理的最终进度消息要带 SessionId，便于任务中心把结果与失败恢复记录对应起来；测试断言应同时覆盖前缀和会话标识。

- 游戏路径是安装根目录，补丁部署目标固定为其 `data` 子目录。只读取根目录或选中 Include 目录直属的合法补丁，不能递归部署未选变体或复制 manifest/预览图。三元组按资源前缀和配置顺序从 0 重编号，保留目录原编号并集的空文件占位语义；占位文件必须先解除旧链接再创建，且所有目标在写入前登记，以支持取消后的清理。旧版 HD2PhysBone 兼容骨骼功能已废弃。来源：https://learn.microsoft.com/windows/win32/fileio/hard-links-and-junctions
