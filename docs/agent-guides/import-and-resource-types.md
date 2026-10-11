# 导入与资源识别

涉及 ModImportService、压缩包、TOC、Type ID、Hash、导入浮窗或开发工具测试模组时先读本文件；外部拖放事件另读 [UI 指南](ui-and-avalonia.md)。

## 易错点与正确做法

- SharpCompress `ReaderFactory` 的流式格式不包含 7z；先按内容签名检测 7z 并重置流位置，再用 `ArchiveFactory.OpenArchive` + `ExtractAllEntries`，其它格式保留 ReaderFactory 以支持 tar.gz 等复合流。格式支持测试必须实际生成/读取该格式，不能用 tar.gz 通过来代表 7z 已支持，也不能只凭扩展名选择读取器。来源：https://github.com/adamhathcock/sharpcompress/blob/master/docs/FORMATS.md

- 开发工具批量生成启用状态也必须调用批量接口，不能通过 AddModToProfileAsync 与 SetModEnabledAsync 的逐条组合模拟批量操作。

- 开发工具生成测试模组必须通过 Application 层服务完成导入和删除；ViewModel 不得直接创建 manifest、复制模组或删除模组目录，临时源目录必须按每项完成/失败清理。

- 开发工具的批量生成、启用状态和清理命令必须传递 CancellationToken，并提供可见取消入口；Application 服务在每项操作前检查取消并保证临时源目录清理。

- Application 导入服务创建 Mod 时必须显式赋值 `ImportedAt`，初始创建/修改/导入时间使用同一 UTC 时间；实体默认日期不是有效导入时间。详情页转成本地时间显示，旧记录缺失时显示“未记录”，不能用修改时间冒充导入时间。来源：https://learn.microsoft.com/dotnet/api/system.datetime.tolocaltime

- 模组库分类按 HD2SDK 的资源 Type ID 匹配，使用 `ResourceTypeCatalog` 与 `Mod.ResourceTypeIds`，不要按旧 `ModCategory`、中文名称或预览图扩展名猜测。一个模组可包含多种类型；未知 ID 保留原值。Type ID 使用 `ulong`，SQLite 持久化为固定 16 位十六进制字符串 JSON，不能转成有符号整数或浮点数。旧库只增加列，保留原分类和用户标签。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/blob/main/utils/constants.py

- 读取 Stingray TOC 分类只需索引：magic `0xF0000011`，72 字节头部、32 字节类型记录、80 字节资源记录，资源 Type ID 位于资源记录偏移 8；先校验索引长度，再读取，保持只读并检查取消。未扫描用 null，已扫描无类型用空列表，损坏索引失败不能缓存成成功。来源：https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition

- 模组添加按钮和外部拖放共用导入浮窗及批次入口。阶段进度由 Application 服务报告，未知耗时步骤显示不确定进度；剩余数量包含当前未完成项。排队的 UI 进度回调须校验批次/文件是否仍有效，不能覆盖完成或取消结果；取消不得被通用异常处理吞掉，未落库副本须清理，已落库模组不能因缩略图取消被算作失败。来源：https://learn.microsoft.com/dotnet/standard/parallel-programming/task-cancellation

- 模组详情 Hash 由 Application 的 `ModHashService` 计算：规范化相对路径按 Ordinal 排序，以路径长度前缀和完整文件 SHA-256 摘要生成目录指纹；不能用文件名/大小或首尾快速指纹冒充内容哈希。新导入在落库前计算并支持取消；旧记录只在打开详情时补算空值并保存，不能在加载列表时扫描整个库。来源：https://learn.microsoft.com/dotnet/api/system.security.cryptography.incrementalhash
