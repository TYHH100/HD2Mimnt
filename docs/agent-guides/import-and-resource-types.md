# 导入与资源识别

涉及 ModImportService、压缩包、TOC、Type ID、Hash、导入浮窗或开发工具测试模组时先读本文件；外部拖放事件另读 [UI 指南](ui-and-avalonia.md)。

## 易错点与正确做法

- 反向迁回使用 Application 快照与原生写入器；预览和执行前校验目标数据指纹，复制后的模组指纹与预览一致才提交。元数据备份与真正发布项分别记录，写入失败只恢复已发布项，避免锁住尚未更新的文件时中断其余回滚和清理。目标管理器正常退出后才写入；SQLite 使用备份 API 复制旧库，不能覆盖非空 WAL。来源：[双向迁移说明](../features/EXTERNAL_MANAGER_MIGRATION.md)、https://learn.microsoft.com/dotnet/standard/data/sqlite/backup

- Echelon 的选项引用映射须与 HD2Mimnt 部署一致：只匹配 Include 目录直属补丁，不能用路径前缀递归包含未选变体。Legacy 子目录转换为一个互斥子选项；原生只有分隔符起点时，为显式短范围和空组添加结束条目。库组织配置须用持久化标记识别，不能凭显示名称猜测。来源：[原生格式与往返验证](../features/EXTERNAL_MANAGER_MIGRATION.md)

- 外部管理器迁移复用 Application 的导入和配置服务；模组身份包含管理器、源数据目录、源条目 ID 与准备后内容哈希，不能只按名称或作者 GUID 覆盖用户记录。文件、图片、标签、清单和哈希完成后才落库；成功项保留供重试，未提交副本在失败/取消时清理。真实文件缺失或复制失败仍跳过整个配置。HD2MM 的 `ModGuids` 是缓存，只有不在源库且在配置及全局状态表中均无保存状态的删除残留才可过滤，并提示数量；不能把有状态的缺失成员静默丢弃。来源：[迁移模块说明](../features/EXTERNAL_MANAGER_MIGRATION.md)、https://www.sqlite.org/lang_transaction.html

- 来源默认配置映射到目标已有 `IsDefault` 配置，保留目标 ID、名称、默认保护和活动状态；HD2MM 认固定默认 GUID，Arsenal 认 `default` 键，Echelon 版本 2 只认唯一 `Default` 名称，不能猜第一份或活动配置。合并保留无关成员及分隔符，重算剩余成员范围；成员、选项、分隔符及迁移快照标记在同一事务提交，重复快照保留用户后续编辑，失败不能先留下成功标记。来源更新后替换它先前迁入的成员及分隔符，不能把新旧内容快照同时留在默认配置中启用；旧库模组副本仍保留。来源：[默认配置合并策略](../features/EXTERNAL_MANAGER_MIGRATION.md)、https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions

- 迁移清单时保留未知包元数据；`Version` 只保存格式、`ModVersion` 保存模组版本，Legacy 不写 `Version`。缺少 `Include` 与显式 `Include: []` 语义不同，不能序列化成相同结果。Echelon 按集合的真实文件路径复制到独立目录，有选项时只部署被引用集合，无选项才部署全部集合；选项排列须同时转换选择索引。来源：[来源格式及验证范围](../features/EXTERNAL_MANAGER_MIGRATION.md)

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
