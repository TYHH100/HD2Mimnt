# 导入与资源识别

涉及 ModImportService、压缩包、TOC、Type ID、Hash、导入浮窗或开发工具测试模组时先读本文件；外部拖放事件另读 [UI 指南](ui-and-avalonia.md)。

## 易错点与正确做法

- 游戏参考 Unit 的 TOC 尺寸可包含内部结束记录之后的填充，不能沿用模组重建的 `ending + 8 == TOC size` 条件拒绝参考。详细读取验证 `ending >= 0x60` 且 `ending + 8 <= TOC size`，另行检查 LOD 范围；索引仍保存原始 TOC 尺寸，保留资源读取上限和歧义判断。真实样本的结束记录后多 8 字节，版本读取成功不代表详细读取成功，回归须覆盖带填充参考的预览、修复与恢复。预览失败展示缺失、歧义或具体读取错误；版本/LOD 已一致的样本无动作是有效结果。来源：[旧管理器参考读取](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/Services/VersionCheck/GameUnitReferenceReader.cs)，[Apache 2.0 许可证](../../src/HD2Mimnt.Infrastructure/ThirdParty/Helldivers2ModManager/LICENSE.txt)。

- DSAR 块大小不等于单次资源读取大小。真实 `bundles*.nxa` 存在约 857 MiB 的合法未压缩块，块表不能套用 Unit 的 64 MiB 读取上限；未压缩块按存储偏移随机读取所需区间，不能解除上限后仍整块分配。磁盘索引载入也须接受大块，继续校验文件范围、顺序和未压缩尺寸一致性。回归包含大块内 TOC/Unit 读取、缓存复用和分配量；合成小块通过后仍需真实归档构建验收。依据为本地实际块表与 [旧管理器 DSAR 读取](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/Services/VersionCheck/GameUnitReferenceReader.cs)，原有 [Apache 2.0 许可证](../../src/HD2Mimnt.Infrastructure/ThirdParty/Helldivers2ModManager/LICENSE.txt)保留。

- 游戏参考数据复用必须保存 bundle 块表、包映射、过滤器与 Unit 定位，不能只保存“已构建”标志。索引放当前 CachePath，按安装路径及归档指纹失效，跨进程加载校验摘要；构建完成后再次核对归档再原子发布，失败/取消保留旧文件，但不将旧定位视作当前参考。版本、LOD 与伴随内容按请求读取当前游戏，不为缓存复制整份游戏资源。见[兼容性说明](../features/MOD_COMPATIBILITY.md)，来源：[File.Move](https://learn.microsoft.com/dotnet/api/system.io.file.move)、[SHA256](https://learn.microsoft.com/dotnet/api/system.security.cryptography.sha256)。

- 兼容性扫描与修复通过 `ModCompatibilityService`；“TOC 可读”或“库内多数 Unit 版本”不能证明游戏兼容。参考按 Unit ID 比较；缺失、歧义或索引不完整保持未确认。DSAR 映射偏移 0 为归档内 uint64、偏移 8 为 bundle 内 uint32，不能互换。完整 Unit 定位索引按归档指纹失效，句柄每次调用释放。伴随按条目 offset/size 校验，恢复须主内容一致且来源唯一；暂存复检用原伴随或恢复路径。自动迁移保留版本已匹配的作者 LOD，不将一个自定义 Unit 的判断扩散到整包。失败/取消回滚当前模组，完成后清除内容缓存。范围及许可证见[后端说明](../features/MOD_COMPATIBILITY.md)。
- 模型兼容性判定必须只取 Unit 类型（`0xe0a48d0be9a7453f`）参与游戏版本比较。Lua、IK 骨骼、动画、Wwise 音效等资源没有对应游戏模型参考时，显示为检测范围外或未确认原因，不能把整套模组压成未知，也不能生成模型修复动作；同一模组中有可比对模型时，只按已找到参考的模型决定版本是否匹配，缺失参考只作为详情说明。TOC `entry_index`、尺寸、伴随范围及 GPU 采样诊断不能覆盖模型版本结果；`HasBlockingIssues` 只用于结构诊断，修复仍须独立预览与复检。真实库曾有 24 个可运行模组因权重采样和条目序号等规则误报。UI 角标只标记模型版本不匹配，未知/检测范围外不使用同一警告角标。来源：[旧管理器 Unit 提取](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/Services/VersionCheck/VersionCheckService.cs)、[资源类型目录](../../src/HD2Mimnt.Core/Resources/ResourceTypeCatalog.cs)、[Apache 2.0 许可证](../../src/HD2Mimnt.Infrastructure/ThirdParty/Helldivers2ModManager/LICENSE.txt)。

- 在线名称的资源 ID 与归档 ID 分开：HD2SDK friendlynames.txt 为十进制 ulong 资源 ID，archivehashes.json 为十六进制归档 ID，可能省略前导零；不能把归档护甲名直接赋给同数字 Unit。整表更新、原子保存，检查批次查询内存，失败保留旧表和原哈希。`0xA4CD36` 与十进制 `10800438` 是同一个版本，禁止当成两个 schema。来源：[hashlists](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/tree/main/hashlists)、[许可状态](../../src/HD2Mimnt.Infrastructure/ThirdParty/HD2SDK-CommunityEdition/LICENSE-STATUS.md)。

- HD2IAS data 的清单 GUID / sidecar 只能证明库条目身份，不能证明主补丁仍存在。状态检测须回读库内真实资源；未部署、停用和空 stream/GPU 伴随文件不能误报。缺文件仍保留身份供原位修复，同版本修复不能被旧数据库哈希的“未变化”分支跳过。见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md)，依据为用户反馈 data 缺失没有提示；格式来源：[官方发布仓库](https://github.com/Estecsky/HD2-Integrated-Armor-System)。

- HD2IAS 系统 Lua 生成按运行包所选路线的模板与正文构建，分别验证原文件、规范化正文和最终源码哈希。用户提供的 1.4.0 Lua 样本是 184 字节索引之后的源码 envelope（版本 2），不能套用 Wwise 回调字节码的 192 字节布局；编译检查仅 loadbuffer，不运行系统 Lua。身份取清单声明并由最终源码哈希约束，自定义追踪放独立 sidecar。运行时匹配不代表已安装 data 包有效；运行时不能混入普通 HD2IR 角色导入，生成的纯 data 则通过 ModService 作为常规模组入库和更新，不直接部署游戏。按清单身份、sidecar 或严格单资源模块入口匹配原条目，更新保留 ID 与配置引用，不能按名称覆盖。格式及双路线离线验证见 [HD2IAS 管理](../features/HD2IAS_MANAGEMENT.md)。来源：[Lua C API](https://www.lua.org/manual/5.1/manual.html#luaL_loadbuffer)、[官方版本包](https://github.com/Estecsky/HD2-Integrated-Armor-System)。

- 重复导入不能只比较 ZIP 文件名或常规目录 Hash：旧包规范化会生成清单 GUID，HD2IR 映射含运行时 ID/时间。保留原 Hash 契约，另按作者元数据和完整文件内容判断重复，排除生成身份；检查与落库串行，拒绝后清理准备副本。版本保留与重复导入是两个独立默认关闭的选项；更新/切换统一走 ModService，保持 Mod/Profile 身份并在失败时恢复版本索引。见[本地版本说明](../features/MOD_VERSION_HISTORY.md)。来源：[SHA256](https://learn.microsoft.com/dotnet/api/system.security.cryptography.sha256)、[File.Move](https://learn.microsoft.com/dotnet/api/system.io.file.move)。

- HD2IR 分类结果不等于可部署文件：前端通过 Application 生成库内独立副本和组件映射，组件选择复用配置选项，骨骼与补丁统一登记到原部署追踪；不能只保存 LogicalMod 名称就报告导入成功。作者双文件清单保持原字节，组件映射放独立元数据。嵌套组合需要确认时不得静默合并，更新包不能当作角色包部署。回归原包移除后的部署、独立配置选择及取消清理，见 [HD2IR 接入](../features/HD2IR_INTEGRATION.md)。来源：[Avalonia UI 线程](https://docs.avaloniaui.net/docs/guides/development-guides/accessing-the-ui-thread)、[任务取消](https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation)。

- 创建页导出 7z 使用随包 `Tools/7zip/7za.exe`，保留原版许可证及来源说明，并检查构建／发布输出；不能只改后缀或依赖用户安装 7-Zip。仅暂存生成的清单和 UTF-8 文件列表，不复制整个源模组；按所选 LZMA2 档位压缩，非零退出码（含跳过文件警告）不可发布。取消时终止并等待子进程，再清理本次暂存和临时包；真实 7z 测试覆盖重导入、各档位、文件占用及取消。来源：[7-Zip](https://www.7-zip.org/)、[许可证](../../src/HD2Mimnt.Infrastructure/Tools/7zip/License.txt)。

- 创建页的格式选择必须贯通打开、预览、保存和 ZIP 导出；Legacy 省略 `Version`，`Options` 写相对目录字符串数组，校验所选目录，不校验隐藏的 V1 草稿。两种格式的内存草稿切换时保留各自内容，打开清单后按真实格式恢复；不能只改 UI 标签或把旧格式写成 V1 对象数组。来源：[旧版创建页面](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/ViewModels/CreatePageViewModel.cs)。

- `manifest.json` 必须保持旧管理器原有标准，自定义作者、网站、模组版本、标签、默认启用和未知扩展字段写到同目录 `hd2mimnt.json`，不得增加扩展引用字段。使用 `ManifestPackage` 分离／合并和双文件保存，文件读取走 `ManifestParser.ParseFromFile[Async]`，不能只解析标准清单字符串而遗漏扩展。创建、导入、迁移、反向导出和开发测试数据共用这一边界；部署／清理排除扩展文件，加载器依赖检测仍读取原扩展声明。来源：[创建说明](../features/MOD_CREATION.md)、[旧版 V1 定义](https://github.com/TYHH100/Helldivers2ModManager/blob/test/src/Helldivers2ModManager/Models/V1ModManifest.cs)。

- V1 父选项只有 `SubOptions` 时可以不写 `Include`，按启用状态收集所选子选项的目录，不能因此丢弃整份清单的映射并只检查根目录；独立叶选项缺少 `Include` 与显式空数组仍区分。导入和迁移的预览优先读取清单 `IconPath`，以迁入后的模组目录解析并保存库内路径；来源管理器未额外提供图片字段时也要保留清单图片。历史预览为空或失效时按相同规则回填，保留仍有效的自定义封面。来源：已复现的白鲸、Plum、Beacon 等 9 份清单及 42 个缺少预览字段的迁移记录；[.NET 相对路径解析](https://learn.microsoft.com/dotnet/api/system.io.path.getfullpath#system-io-path-getfullpath(system-string-system-string))。

- 清单的可选字段类型异常不能让已解析的名称、选项和图片一起丢失：GUID 先按字符串检查，Nexus ModId 先检查 JSON 类型再尝试读取整数，非法或超范围值只忽略该可选字段。`Include: null` 与字段缺失一致，只有数组才构成映射，不能变成 `Include: []` 的明确无文件语义。来源：[JsonElement.TryGetInt32 类型及范围要求](https://learn.microsoft.com/dotnet/api/system.text.json.jsonelement.trygetint32)。

- 正向导入、反向迁回及往返后部署共用 `ProfileModOptionResolver` 的映射完整性规则；反向写入器不能独立要求父分组必有 Include，否则会清空子选项映射并错误添加根目录“全部文件”。预览图回退也需覆盖每种管理器读取器，包括 Echelon 的源清单 IconPath。来源：[迁移模块说明](../features/EXTERNAL_MANAGER_MIGRATION.md)。

- 反向迁回使用 Application 快照与原生写入器；预览和执行前校验目标数据指纹，复制后的模组指纹与预览一致才提交。元数据备份与真正发布项分别记录，写入失败只恢复已发布项，避免锁住尚未更新的文件时中断其余回滚和清理。目标管理器正常退出后才写入；SQLite 使用备份 API 复制旧库，不能覆盖非空 WAL。来源：[双向迁移说明](../features/EXTERNAL_MANAGER_MIGRATION.md)、https://learn.microsoft.com/dotnet/standard/data/sqlite/backup

- Echelon 的选项引用映射须与 HD2Mimnt 部署一致：只匹配 Include 目录直属补丁，不能用路径前缀递归包含未选变体。Legacy 子目录转换为一个互斥子选项；原生只有分隔符起点时，为显式短范围和空组添加结束条目。库组织配置须用持久化标记识别，不能凭显示名称猜测。来源：[原生格式与往返验证](../features/EXTERNAL_MANAGER_MIGRATION.md)

- 外部管理器迁移复用 Application 的导入和配置服务；模组身份包含管理器、源数据目录、源条目 ID 与准备后内容哈希，不能只按名称或作者 GUID 覆盖用户记录。文件、图片、标签、清单和哈希完成后才落库；成功项保留供重试，未提交副本在失败/取消时清理。真实文件缺失或复制失败仍跳过整个配置。HD2MM 的 `ModGuids` 是缓存，只有不在源库且在配置及全局状态表中均无保存状态的删除残留才可过滤，并提示数量；不能把有状态的缺失成员静默丢弃。来源：[迁移模块说明](../features/EXTERNAL_MANAGER_MIGRATION.md)、https://www.sqlite.org/lang_transaction.html

- 来源默认配置映射到目标已有 `IsDefault` 配置，保留目标 ID、名称、默认保护和活动状态；HD2MM 认固定默认 GUID，Arsenal 认 `default` 键，Echelon 版本 2 只认唯一 `Default` 名称，不能猜第一份或活动配置。合并保留无关成员及分隔符，重算剩余成员范围；成员、选项、分隔符及迁移快照标记在同一事务提交，重复快照保留用户后续编辑，失败不能先留下成功标记。来源更新后替换它先前迁入的成员及分隔符，不能把新旧内容快照同时留在默认配置中启用；旧库模组副本仍保留。来源：[默认配置合并策略](../features/EXTERNAL_MANAGER_MIGRATION.md)、https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions

- 迁移清单时将未知包元数据保留在独立扩展 JSON；`Version` 只保存格式、`ModVersion` 在扩展中保存模组版本，Legacy 不写 `Version`。缺少 `Include` 与显式 `Include: []` 语义不同，不能序列化成相同结果。Echelon 按集合的真实文件路径复制到独立目录，有选项时只部署被引用集合，无选项才部署全部集合；选项排列须同时转换选择索引。来源：[来源格式及验证范围](../features/EXTERNAL_MANAGER_MIGRATION.md)

- 源部署方向与目标不同时转换完整分隔符成员范围；固定 Lua 成员归入现有固定区域，库分隔符用独立禁用配置保留组织关系。自定义排序导致分组成员不连续时报告不兼容并跳过配置，不能悄悄扩大分组。HD2MM 源库用只读短连接，禁止把源设置中的凭据写入迁移记录或日志。来源：https://learn.microsoft.com/dotnet/standard/data/sqlite/connection-strings 、[迁移模块说明](../features/EXTERNAL_MANAGER_MIGRATION.md)

- 冲突扫描的补丁索引按块读取并以有限容量 LRU 缓存完整解析成功的结果；缓存键校验路径、长度、修改/创建时间，不能缓存损坏索引或选项/部署顺序。资源聚合的唯一条目避免逐条分配展示对象，只为真实跨模组覆盖生成详情；保留同一模组多个补丁的最终覆盖语义。修改索引读取后覆盖跨块读取及 Unit 版本随机定位，取消时归还缓冲区。来源：https://learn.microsoft.com/dotnet/api/system.buffers.arraypool-1

- SharpCompress `ReaderFactory` 的流式格式不包含 7z；先按内容签名检测 7z 并重置流位置，再用 `ArchiveFactory.OpenArchive` + `ExtractAllEntries`，其它格式保留 ReaderFactory 以支持 tar.gz 等复合流。格式支持测试必须实际生成/读取该格式，不能用 tar.gz 通过来代表 7z 已支持，也不能只凭扩展名选择读取器。来源：https://github.com/adamhathcock/sharpcompress/blob/master/docs/FORMATS.md

- 开发工具批量生成启用状态也必须调用批量接口，不能通过 AddModToProfileAsync 与 SetModEnabledAsync 的逐条组合模拟批量操作。

- 开发工具生成测试模组必须通过 Application 层服务完成导入和删除；ViewModel 不得直接创建 manifest、复制模组或删除模组目录，临时源目录必须按每项完成/失败清理。

- 开发工具的批量生成、启用状态和清理命令必须传递 CancellationToken，并提供可见取消入口；Application 服务在每项操作前检查取消并保证临时源目录清理。

- Application 导入服务创建 Mod 时必须显式赋值 `ImportedAt`，初始创建/修改/导入时间使用同一 UTC 时间；实体默认日期不是有效导入时间。详情页转成本地时间显示，旧记录缺失时显示“未记录”，不能用修改时间冒充导入时间。来源：https://learn.microsoft.com/dotnet/api/system.datetime.tolocaltime

- 模组库分类按 HD2SDK 的资源 Type ID 匹配，使用 `ResourceTypeCatalog` 与 `Mod.ResourceTypeIds`，不要按旧 `ModCategory`、中文名称或预览图扩展名猜测。一个模组可包含多种类型；未知 ID 保留原值。Type ID 使用 `ulong`，SQLite 持久化为固定 16 位十六进制字符串 JSON，不能转成有符号整数或浮点数。旧库只增加列，保留原分类和用户标签。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/blob/main/utils/constants.py

- 读取 Stingray TOC 分类只需索引：magic `0xF0000011`，72 字节头部、32 字节类型记录、80 字节资源记录，资源 Type ID 位于资源记录偏移 8；先校验索引长度，再读取，保持只读并检查取消。未扫描用 null，已扫描无类型用空列表，损坏索引失败不能缓存成成功。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition

- 模组添加按钮和外部拖放共用导入浮窗及批次入口。阶段进度由 Application 服务报告，未知耗时步骤显示不确定进度；剩余数量包含当前未完成项。排队的 UI 进度回调须校验批次/文件是否仍有效，不能覆盖完成或取消结果；取消不得被通用异常处理吞掉，未落库副本须清理，已落库模组不能因缩略图取消被算作失败。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 模组详情 Hash 通过 Application 的 `ModHashService` 调用 Core 的 `ModDirectoryHash`，导入与迁回复制校验共用同一算法：规范化相对路径按 Ordinal 排序，以路径长度前缀和完整文件 SHA-256 摘要生成目录指纹；不能用文件名/大小或首尾快速指纹冒充内容哈希。新导入在落库前计算并支持取消；旧记录只在打开详情时补算空值并保存，不能在加载列表时扫描整个库。来源：https://learn.microsoft.com/dotnet/api/system.security.cryptography.incrementalhash
