# HD2IAS 管理：运行时安装与 data 模组

入口为“工具 → HD2IAS 管理”，位于“开发工具”和“关于”之间，不增加顶层导航。默认 Native 路线，可手动选择 Lua。

## 当前流程

1. 配置游戏目录，管理页自动读取 `bin/HD2IndependentRig.build.json` 的版本与路线声明。声明与文件哈希验证分别显示。
2. 点击“检查版本”，从官方 Raw `UPDATE_HISTORY.txt` 读取独占一行的数字版本，数值倒序排列。下载 `updates/<版本>/HD2IAS-<版本>-update.zip`；仅在 HTTP 404 时尝试 `HD2IAS-<版本>-direct-update.zip`，不调用 GitHub API。也可选择本地完整运行包。
3. 点击“安装运行包并更新 data 模组”。重新验证清单、源文件 SHA-256、大小、路线及游戏指纹；运行时按覆盖式方案安装，用户数据保留。
4. 管理器直接生成纯 data 模组目录，通过 ModService 自动加入模组库；库中已有对应模块时，自动更新原条目。无需生成 ZIP、保存导出文件或手动导入。
5. data 与普通模组一样加入配置、选择启用或停用、排序，再通过配置文件部署。安装运行包本身不复制 data 到游戏，不保留专用 patch_0 槽位。首次加入配置沿用常规模组“自动添加导入模组到当前配置”的设置。

## 备用下载与请求频率

优先请求官方 Raw。连接失败、超时或 HTTP 408 / 500 / 502 / 503 / 504 时，按用户提供顺序尝试以下前缀，成功后停止，不做并行竞速：

1. `https://api.gitproxy.dev/raw.githubusercontent.com/`
2. `https://gh-proxy.com/raw.githubusercontent.com/`
3. `https://fastgit.cc/https://raw.githubusercontent.com/`

后续拼接相同的官方仓库路径。版本列表与 ZIP 共用串行请求闸门；公益源同一主机的请求至少间隔 2 秒，连接失败后该主机冷却 5 分钟。全部失败后暂缓新请求，不后台轮询。收到 429 立即停止，不切换地址绕过限流；版本列表响应带更长 Retry-After 时遵守该时间。

版本列表缓存 15 分钟；同一版本已有完整且通过清单校验的本地 ZIP 时直接复用，下载请求串行合并。下载采用单连接，关闭分段、自动重试和断点恢复，避免探测和重试放大请求量。404 只在同源尝试一次历史 `direct-update.zip` 文件名；403、429、非法 ZIP 或校验失败不继续遍历其它源。下载后的版本、路线、大小和 SHA-256 校验保持不变。

这些地址仅按用户指定配置，测试通过模拟 HTTP 验证回退顺序、缓存、冷却、取消和请求次数，不把模拟通过表述为加速源线上可用性验证。

## 卸载与运行时缺失提示

管理页提供“卸载 HD2IAS 运行时”，操作前显示确认。仅卸载管理器活动安装记录中同路径且哈希仍匹配的系统文件；无安装记录时不按名称猜测删除。`dxgi.dll` 始终保留，包括管理器曾安装或后来被 ReShade 替换的情况。用户 INI、未知文件、被外部修改的文件及模组库 data 均保留；已修改系统文件列入日志和结果提示。卸载不清理普通配置的 data 部署，停用/删除 data 仍走常规模组操作。

卸载与安装共用真实文件备份、逐文件事务和失败/取消恢复。成功写入独立卸载完成记录，再清理未提交日志；中断恢复可区分“尚未完成的卸载”和“已提交、仅剩日志待清理的卸载”，不会把成功卸载的系统自动恢复回来。

卸载前清理 `bin/ReShade.ini` 的 HD2IAS 字体引用：只处理 `[OVERLAY]` / `[STYLE]` 中实际解析到当前游戏 `bin/HD2IndependentRig/fonts/` 的 `Font`、`EditorFont`、`LatinFont`。主字体优先恢复 `[HD2Interface] previous_font` 中仍存在且不在该目录的旧字体；没有可用旧字体时清空，交回 ReShade 默认字体逻辑。指向系统字体的旧字体备忘也清空。自定义字体、其它配置、UTF-8 BOM 与原换行保留，ReShade.ini 文件和 dxgi.dll 均不删除。配置修改先于系统文件删除，并纳入同一事务的真实备份、逐文件校验及取消/失败回滚。依据为用户提供的实际配置；默认字体行为参考 [ReShade runtime_gui.cpp](https://github.com/crosire/reshade/blob/v6.6.2/source/runtime_gui.cpp)，开源许可证见 [LICENSE.md](https://github.com/crosire/reshade/blob/v6.6.2/LICENSE.md)，仅核对行为，未复制或重分发 ReShade 代码。

启动、库内容变化、游戏路径变化和管理页刷新时，按标准清单身份、sidecar 或旧 data 的真实资源入口检测 HD2IAS data，不能仅凭模组名称匹配。若库中存在 data，但游戏运行时缺失或不完整，显示安装通知（可跳转管理页）及页面提示。同一游戏目录缺失状态持续期间不重复通知；安装恢复后再次缺失可重新提示。已有 build.json 的版本声明不能代替运行时文件存在性检查。检测为本地操作，不自动发起更新请求或安装。

反向检测同时检查模组库的实际主补丁：已有运行时但没有 data 条目，或已识别条目的目录、主补丁缺失、为空或无法通过资源入口回读时，显示 data 修复通知及页面提示。两个空伴随文件是正常格式；未部署到游戏或在配置中停用不算缺失。身份识别与文件可用性分开，修复同版本时仍更新原 ID 并保留配置引用，不能因数据库哈希相同就跳过。运行时与 data 的通知分别去重，恢复后再次缺失可重新提示。

## data 的识别与更新

- 新条目使用标准 V1 `manifest.json`，将版本等扩展放 `hd2mimnt.json`；路线、模块及源码身份写独立 `hd2mimnt-hd2ias.json`，不扩展标准清单。
- 生成目录只包含补丁、标准清单及独立元数据，不包含运行时 DLL、app 或 Foundation。
- 对已有条目按生成清单身份、sidecar 的模块/资源 ID 识别；旧手动导入包可通过严格的单资源 Stingray 布局、Lua 类型及源码模块入口识别。不能只凭显示名称替换，也不接管包含角色组件的 HD2IR / LogicalMod。
- 多个条目同时匹配则停止并提示先保留一份，避免任意选择。
- 复用 ModService 的本地更新流程与目录输入能力，保留原 Mod ID、配置成员、启用状态、排序和选项；新副本准备好后才提交内容更新。
- data 内容更新与常规模组更新语义一致：清理该条目的旧部署，保留模组库内容并等待配置重新部署。再次安装相同版本且内容未变时复用已有条目，不生成重复条目。
- 部署、清理和删除后的补丁重编号完全复用现有普通模组流程，不为系统 data 预留特殊编号。加载器依赖识别及 MDL / BSL 互斥继续沿用现有规则。

## 运行时覆盖安装

运行包使用 `HD2RuntimeDistribution1`，Foundation 使用 `HD2SystemFoundation2`；两者版本独立。`appearance_source = native-v1` 不等于 `pose_route = native`。构建记录 `HD2IndependentRig.build.json` 明确映射到游戏 `bin/HD2IndependentRig.build.json`，随运行时更新、切换或降级，并纳入同一备份和回滚；读取包时校验其 schema、版本、路线与运行时清单一致。其它 `target=null` 的源文件仍不部署。

安装前读取游戏 `bin/helldivers2.exe`、`data/bundle_database.data` 和 `bin/lua51.dll` 的指纹。游戏 data 文件只用于校验；运行时安装拒绝 data 目标，不写入游戏 data 目录。

| 对比结果 | 处理方式 |
| --- | --- |
| SHA-256 相同 | 跳过，不改内容与修改时间 |
| 缺失的清单文件 | 补齐 |
| 认可旧哈希、同包另一条路线的明确载荷，或与活动安装记录同路径哈希相同 | 覆盖更新或降级 |
| 已退役或旧管理记录中移除的系统文件 | 核对原哈希后清理 |
| 用户 INI / 未分类文件 | 保留 |
| 来源未知 | 停止自动替换 |

`runtime.ini`、`free_parts_selections.ini`、`physics_control.ini` 保存用户选项；未知 INI 也保留。`helmet_manifest.ini`、`transmog_manifest.ini` 是系统目录清单，继续校验和更新。不删除整个 bin 或 HD2IndependentRig 目录，先卸后装暂不启用。

降级不能只检查旧包的 `previous_sha256`：旧包不可能包含未来版本的哈希。已由管理器安装的文件须与当前活动记录的同路径哈希完全一致，才能用所选旧包替换；被外部修改的文件仍阻止覆盖，不能仅凭版本号放行。旧管理器曾将 build.json 记为未分类文件，迁移时仅从当前活动版本的已下载缓存包（重新完整校验后）补足该构建记录及其认可旧哈希；未分类清册本身不提供所有权。若缺少该缓存且旧包也不认识现有构建记录，应先重新安装当前版本以建立记录，再降级。

应用数据目录 `HD2IAS/<游戏路径哈希>/` 保存运行时活动记录、交叉比对清册、暂存载荷和真实旧文件备份；清册包括已知部署目标、HD2IndependentRig 内其它文件及已有构建记录。生成的 data 在独立 `data-mod` 子目录中准备，仅用于模组库导入，不属于运行时的游戏目标所有权。

所有运行时文件先暂存校验、备份，再持久化逐文件变更日志，最后发布活动记录。失败或取消恢复已开始处理的文件，进程中断后下次安装先恢复未提交事务；恢复失败保留日志与备份供重试。若运行时已安装，但后续模组库更新失败，明确显示部分完成及重试提示。没有跨运行时安装与模组库更新的原子事务，也没有用户可选的运行时版本恢复界面。

## Lua 与归档生成

1. 校验所选路线的 runtime.lua 与 independent_loader.lua 完整文件哈希。
2. UTF-8 解码、去 BOM、统一 LF、去正文末尾空白，验证 body_sha256。
3. 模板标记必须各出现一次；替换 MODULE_IDENTITY 与 RUNTIME_BODY，验证最终 source_sha256。
4. 身份取清单声明，由最终源码哈希约束；不猜测上游身份算法。
5. 使用已验证 SHA-256 的游戏 lua51.dll，仅调用 loadbuffer 检查语法，不执行系统 Lua。
6. 构建单资源 Stingray Archive：magic `0xf0000011`，72 字节头、32 字节类型记录、80 字节资源记录，数据偏移 184。Lua type `a14e8dfa2cd117e2`，资源 ID 来自清单；envelope 保存源码长度与版本 2。
7. 写入纯模组目录的 `data/9ba626afa44a3aa3.patch_0` 和两个空伴随文件，回读校验源码与完整布局，再交给 ModService。

保留的 ZIP 导出 API 只用于格式回归，不是管理页安装流程。

## 验证范围

2026-10-17 ReShade 字体引用修正：覆盖旧 `[OVERLAY]` 与新 `[STYLE]`、绝对/相对及混合分隔符路径、有效旧字体恢复、缺失旧字体回退、无关自定义字体保留、BOM/中文/换行保留，以及清理配置后卸载失败或取消的字节级回滚。108 项 HD2IAS / 翻译相关测试和 1 项卸载界面测试通过；未进行游戏内字体渲染验收。

2026-10-17 data 缺失提示修正：解决方案构建通过，100 项 HD2IAS / 翻译 / 模组替换相关服务测试通过，3 项 HD2IAS Headless 界面测试分别运行通过。新增覆盖无运行时且无 data 不提示、手动与受管理运行时缺 data 提示、库目录或主补丁删除、空/损坏补丁、同版本原 ID 修复且保留配置状态、旧包根目录补丁及空伴随文件识别，以及通知跳转、持续缺失去重、恢复后重新提示和三语页面更新。测试写入均在隔离目录，真实游戏未修改。

2026-10-17 后续验证：构建通过，137 项相关服务/部署/翻译测试通过，2 项 HD2IAS Headless 界面测试分别运行通过。新增覆盖公益源精确 URL / 顺序回退、缓存合并、失败冷却、429 停止轮换、取消节流等待、卸载保留原版及外部替换的 dxgi.dll、用户 INI / data 保留、卸载失败恢复、已提交卸载的中断恢复，以及缺失通知跳转和实际卸载按钮的取消/确认操作。所有网络回退测试采用模拟响应，未对公益源执行下载压测；卸载写入在隔离目录中，真实游戏未修改。

2026-10-17：解决方案构建通过。相关服务、部署和翻译回归验证运行时覆盖、相同文件跳过、用户配置保留、失败/取消回滚及模拟中断恢复。降级回归覆盖 Lua / Native 同路线与跨路线降级、再次升级、构建记录同步与回滚、修改后文件拦截，以及旧未分类构建记录的缓存校验迁移。data 回归覆盖首次入库、重复复用、版本更新、双向路线切换、旧手动导入包更新、同名无关包不被替换、保留配置选项及启用状态，以及通过普通配置部署、删除后重编号和更新后待重新部署。Headless 界面测试通过实际按钮验证下载后安装、两路线更新同一库条目、三语主题和退出取消。

本轮所有写入均在隔离测试目录，真实游戏安装未修改，游戏内验收仍待用户执行。

降级修正后验证：117 项相关服务/部署/翻译测试及 1 项 Headless 界面测试通过。另用已下载的真实 1.4.5 / 1.4.2 包（各 23 项源文件校验通过）在隔离目录执行 Native `1.4.5 → 1.4.2 → 1.4.5`，验证旧未管理构建记录的缓存迁移、真实 Lua VM 编译、data 归档回读及用户设置保留。临时程序与游戏副本已清理；没有修改真实游戏安装，也不代表游戏内验收完成。

此前对用户提供的真实 1.4.4 包做过只读验证：23 项源文件全部通过，运行包版本 1.4.4、Foundation 1.1.3。真实游戏 VM 编译检查及产物回读验证通过：Lua 主补丁 548899 字节、Native 35686 字节；Lua 主补丁与用户提供的 1.4.0 Lua 样本逐字节相同，Native 未提供原程序样本作逐字节比较。这些是离线结果，不代表游戏内兼容性结论。

更新源此前实际验证：Raw 记录解析出 21 个版本，最高 1.4.5；下载 70,072,794 字节更新包并通过 23 项清单校验。合计 6 次 Raw HTTP 请求，API 请求 0 次。版本与最高版本只是当时的验证记录，不是固定常量。

## 来源与许可证

- 用户指定的[官方版本发布仓库](https://github.com/Estecsky/HD2-Integrated-Armor-System)，此前核对提交 `e8d4cd17f435a6b42d6cb341f8457aacd7ca5156`。当时仓库为更新包与发布信息，未找到生成器源码或上游 LICENSE；本实现不内置或重分发其运行包和游戏 DLL。运行包由用户选择本地文件或通过 Raw 从官方源下载。
- 用户指定的[Raw 更新记录](https://raw.githubusercontent.com/Estecsky/HD2-Integrated-Armor-System/refs/heads/main/UPDATE_HISTORY.txt)、[官方更新目录](https://github.com/Estecsky/HD2-Integrated-Armor-System/tree/main/updates)。下载与内部清单校验确认一致性，不宣称为作者签名。
- 用户提供的公益加速地址：[gitproxy](https://api.gitproxy.dev/)、[gh-proxy](https://gh-proxy.com/)、[fastgit](https://fastgit.cc/)。本实现只配置备用 URL，不复制其服务端代码。
- 用户提供的 Lua 样本、运行包 `package.json` / `foundation.json` / `update_notes.json` 为实际格式依据。
- 本地 `hd2_mods-test/tools/game_data/archive.py` 的归档结构参考：[hd2_ArmorIsolation](https://github.com/TYHH100/hd2_ArmorIsolation)，本地提交 `465edb4d3ec45f4eaa1733449f6961c94580690e`。原项目 [MIT 许可证副本](../references/licenses/hd2_ArmorIsolation.LICENSE.txt)。本实现针对用户样本的 184 字节源码布局，未复用该工具的 192 字节 Wwise 回调布局。
- [Lua 5.1 C API：luaL_loadbuffer](https://www.lua.org/manual/5.1/manual.html#luaL_loadbuffer)、[.NET ZipArchive](https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchive)。
- HD2Mimnt 的实现代码遵循仓库 [LICENSE](../../LICENSE)。
