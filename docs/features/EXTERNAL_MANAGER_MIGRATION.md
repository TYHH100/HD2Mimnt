# 外部管理器迁移

“工具 → 管理器迁移”提供 Helldivers2ModManager、Echelon、HD2Arsenal 三个选项，可将其它管理器的模组库和配置导入 HD2Mimnt，也可将当前全部模组和配置直接迁回所选管理器。

选好管理器、方向及数据目录后，点击“开始迁移”即可扫描并执行；“预览”可先查看模组、配置、分隔符数量及问题。每个管理器的路径独立记忆。HD2MM 另需填写包含 `settings.json` 的安装目录，模组库位置可自动读取或手动覆盖。迁回前关闭目标管理器，完成后重新打开；界面支持取消，关闭 HD2Mimnt 时等待当前操作清理结束。

## 迁移内容与来源

| 来源 | 读取的数据 | 迁入内容 |
| --- | --- | --- |
| Helldivers2ModManager（本地样本文件版本 1.5.0） | 安装目录 `settings.json`、数据目录 `mod_manager.db`、模组库 `Mods/` | 模组文件、Legacy/V1 清单、标签、配置成员、启用状态、选项与子选项选择、选项排列、配置排序、自定义部署顺序、分隔符 |
| Echelon 0.36.2，`state.json` 版本 2 | `state.json`、`locations.json`，可选 `library.json`，实际模组目录与 `images/` | 模组文件集合、选项定义与默认值、配置成员及选择、排序、标签、预览图、普通分隔符与自动 Bingus 分隔符 |
| HD2Arsenal 0.36.1 | `hd2a_data.json`、其中引用的模组目录与图片 | 完整模组库、配置排列、成员启用和选项状态、排序、配置分隔符、库分隔符 |

来源格式依据用户提供的本地 JSON、SQLite 和安装包静态检查；以上版本是验证样本，不代表所有版本都兼容。

`DataPath` 是数据目录，也可传入其中的数据文件。`InstallationPath` 用于读取 HD2MM 的 `settings.json`；软件安装目录与数据目录需要分别传入。`ModsPath` 可覆盖实际模组库位置：HD2MM 可传存储根或其 `Mods` 子目录，Echelon 传包含各 `store` 目录的根，Arsenal 按原模组路径的末级目录名在该根下查找。

Echelon 明确传入的 `ModsPath` 优先。未传入时先读取 `locations.json`，逐项找不到目录再检查数据目录下的 `mods/` 和 `library.json` 中的已有导出路径。缺失目录会列入问题，不能用元数据生成空模组冒充文件迁移成功。

## 调用接口

类型位于 `HD2Mimnt.Core.Migration`，读取器接口为 `IExternalManagerReader`，写入器接口为 `IExternalManagerWriter`，业务入口位于 Application 层。

```csharp
var source = new ExternalMigrationSource(
    ExternalManagerKind.Helldivers2ModManager,
    DataPath: @"G:\Temp\HD2ModManager\Mods",
    InstallationPath: @"D:\HD2MM\Helldivers2ModManager");

var plan = await migrationService.PreviewAsync(source, cancellationToken);
// plan.Mods / Profiles 给出迁移内容，plan.Issues 给出缺失或不兼容项。
var result = await migrationService.MigrateAsync(plan, progress, cancellationToken);

// 迁回相同管理器，直接写入其原生数据格式。
var exportPlan = await exportService.PreviewAsync(source, cancellationToken);
var exported = await exportService.MigrateAsync(exportPlan, progress, cancellationToken);
```

- `PreviewAsync` 只读源数据，返回计划，不复制文件或写目标数据库。
- `MigrateAsync` 在后台依次处理模组和配置，使用 `IProgress<ExternalMigrationProgress>` 报告完成数量、阶段和当前项。
- `ExternalMigrationResult` 返回导入/复用数量、源 ID 到目标 ID 的映射、问题列表，以及源活动配置对应的 `SourceActiveProfileId`。
- `IsComplete` 仅在没有问题时为 true；调用方应展示 `Issues`。取消会抛出 `OperationCanceledException`，等待任务结束后，未提交副本的清理才已完成。
- 读取器可通过实现接口并注册到 DI 扩展。View/ViewModel 只调用业务入口，不自行读取数据库或复制文件。

`ExternalManagerExportService` 从当前库和全部配置取得快照，完整复制模组文件，保留每份配置的启用状态、选项、排序、分隔符与活动配置。`IsLibraryLayout` 标记 Arsenal 迁入时生成的库组织配置，迁回 Arsenal 时恢复到原生库列表。迁回使用所选管理器当前部署方向转换显示顺序，以保留覆盖优先级；预览中的 `IsBlocking` 问题必须解决后才能执行。

三个写入器分别输出 HD2MM 的 SQLite/`settings.json`、Echelon 的 `state.json`/`locations.json` 和 Arsenal 的 `hd2a_data.json`。已有模组、配置和偏好合并保留；普通模组使用源身份与内容指纹生成新 ID，同名配置追加编号，重复的相同快照复用。HD2MM 固定加载器必须保持原生 GUID，已有版本会先备份再替换。新增配置中所有成员的状态独立保存。

迁回先在目标数据目录的 `.hd2mimnt-migration-*` 中准备文件及数据库快照，完成后提交。原有数据文件在覆盖前生成 `.hd2mimnt-backup-*` 备份，备份路径由结果 `BackupPaths` 返回并展示。预览后目标数据或复制的源模组内容改变会拒绝提交；准备期间可取消，发布失败恢复已经更新的元数据及本次新增/替换目录，最后清理临时目录。正式备份保留供恢复。

## 保存与重试策略

源 JSON、数据库和模组文件保持只读。HD2MM 数据库使用 `Mode=ReadOnly;Pooling=False` 短连接；目标模组复制到自己的库目录，迁移后不依赖源模组目录。

每个模组先完成文件复制、图片、标签、清单转换、资源识别和完整内容哈希，再写入数据库。身份由管理器、源数据目录、源条目 ID 和准备后内容哈希共同生成；源作者 GUID 留在清单中用于加载器识别，不作为目标数据库 ID。同名或同 GUID 的用户已有模组不会被覆盖。

普通配置按来源和内容生成快照身份。相同快照重复迁入会复用记录，保留迁入后用户的编辑；源模组内容或配置内容改变会新增快照。同名配置使用 `(2)` 等后缀。迁入不自动切换目标活动配置。

来源默认配置直接合并到 HD2Mimnt 已有默认配置，保留其 ID、名称、默认保护和活动状态。HD2MM 根据固定默认 GUID `00000000-0000-0000-0000-000000000001` 识别，Arsenal 根据 `default` 键识别；Echelon `state.json` 版本 2 没有默认 ID 标记，只将唯一名为 `Default` 的配置视为原生默认，改名或重名时按普通配置处理。库组织配置不合并到默认配置。

合并保留目标默认配置中的无关成员和分隔符，迁入成员按源状态更新并按转换后的顺序追加；既有分隔符只保留其剩余成员范围，不扩展到追加成员。源配置再次变化时替换该来源先前迁入的成员及分隔符，避免同时启用旧、新模组版本，保留其它来源及用户分隔符。`DefaultMigrationStateJson` 与成员在同一事务保存已应用的来源快照、成员及分隔符身份；相同快照重试不会重复追加或覆盖用户后续编辑。

模组和配置分别提交，整批不是一个大事务。已提交项保留，取消或失败可重试。一个配置的成员、启用状态、选项和分隔符通过现有仓储在同一次事务中写入；真实文件缺失或复制失败导致任一成员未成功迁入时，整个配置跳过并报告问题，不生成残缺配置。HD2MM 的 `mod_groups.ModGuids` 缓存可能保留已删除成员；仅当引用不在源库且配置及全局状态表均无保存状态时过滤，并在预览与结果中提示。过滤后重算分隔符位置和有效成员范围。

## 选项、优先级和分隔符

HD2MM 的选项排列与子选项排列同时作用于定义和保存的选择索引，不能只移动名称。无效的排列拒绝预览。独立部署排序转换为目标配置的有效顺序；若它使分隔符成员不连续，报告问题并跳过该配置，供源软件调整后重试。

Echelon 根据 `sets[].files` 复制精确文件集合，每个集合映射到 `migration-sets/0000/` 等独立目录，避免同目录的其它集合被选中。有选项时仅部署选项引用的集合；无选项时部署全部声明的集合。成员缺失部分选项状态时补齐库中的 `option_defaults`，包括默认子选项选择。

Arsenal 的选项状态兼容 `options` 与 `optionsConfig`。库中独有的分隔符保存在一个成员全部禁用的“HD2Arsenal 模组库分组”配置中，使库顺序和分组可查看；当前目标模组库没有单独的分隔符实体。

分隔符保留名称、成员范围、背景颜色和折叠状态。颜色规范化为目标支持的 `#RRGGBB`；前景文字使用目标主题。Echelon 的自动 Bingus 分隔符映射到目标固定 Lua 分组的颜色和折叠状态。

源与目标部署方向不同，按当前目标全局方向转换普通模组顺序和整段分隔符范围，保留普通模组覆盖优先级，不修改全局设置。Lua 依赖、BSL/MDL、SmoothBoot 进入目标已有固定区域，分隔符范围排除固定成员；例如源分隔符前的加载器移入固定区域后，其数字位置会改变，但原普通成员归属仍保留。同时启用 BSL 和 MDL 的源配置会被拒绝。

清单转换保留包中未知字段，例如 provenance；`Version` 保存清单格式，`ModVersion` 保存模组版本。`Include: []` 表示显式不包含文件，与缺少 `Include` 的默认目录映射保持区别。

迁回 Echelon 和 Arsenal 时，Legacy 互斥变体转换成一个 V1 子选项；不完整 Include 映射增加常开的基本文件选项，保持当前目录文件的部署语义。Echelon 按目录直属补丁生成集合引用，不能递归包含未选中的嵌套变体。原生分隔符只有起点时，显式短范围及空组追加结束分隔符，防止折叠吞掉后续成员。

HD2MM 的分隔符为全局条目，无法完全表达多配置独立的显示布局；迁回会保留绑定成员并在预览中提示限制，附加的配置归属元数据供再次迁入时识别。目标开启独立部署排序时保留原排序，在末尾追加导出活动配置的顺序并提示。非空 SQLite WAL 要求正常退出目标管理器后重试，避免覆盖尚未保存的数据。

## 验证记录

2026-10-12，在隔离库和数据库中完成解决方案构建及测试：迁移专项 30/30，配置、选项、导入、部署、哈希和翻译相关回归 89/89（同轮合计 119/119）；工具页实际交互测试 1/1。测试覆盖三个管理器和正反排序下的迁入/迁回、多个配置独立状态、Legacy/V1 选项、分隔符范围与空组、Arsenal 库分隔符和已有数据保留、重复执行、源/目标预览后变化、图片与包元数据、取消清理、文件锁导致发布失败及回滚重试。往返后向假游戏目录部署，确认只包含选中变体，根目录无选项模组不部署嵌套备用文件，`patch_2` 在 `patch_10` 前并连续重编号。

工具页测试通过真实鼠标/键盘切换三个管理器与两个方向、直接开始/预览后开始迁移，检查路径记忆、三语、深浅主题与 960×600 窗口，并确认迁入后模组库立即刷新。未启动外部管理器核验真实界面，未进行真实游戏运行验证。

同日修复 HD2MM 删除缓存阻断配置的问题及三个来源的默认配置映射。新增 10 个用例覆盖无状态删除残留、有状态但文件缺失、目录仍在但清单损坏、非默认活动配置、Echelon 重名歧义、非空默认配置、重复及变化快照、事务失败回滚、重叠成员和加载器互斥、正反部署方向。修复后解决方案构建通过，迁移专项及配置、选项、导入、部署、哈希、多语言相关测试合计 135/135；工具页双向迁移交互回归 1/1。

真实 HD2MM 数据只读预览得到 85 个库模组及 67 个有效默认配置成员。使用正式数据库的隔离快照及源清单元数据验证默认配置合并，逐项检查启用、选项和排序，确认两处分隔符及重复执行复用。此验证不重新复制大模组库，也不写正式数据库。

在用户启用分隔符后的本地数据上进行只读扫描，并将 Arsenal 迁入隔离库：

| 来源 | 扫描结果 | 验证边界 |
| --- | --- | --- |
| HD2MM | 85 个库模组、1 个默认配置、67 个有效成员（65 启用、2 禁用）、2 个分隔符 | 过滤并提示 10 个无保存状态的已删除缓存引用；真实来源预览及隔离配置写入通过，未重复复制整库 |
| Echelon | 122 个库条目、1 个配置、121 个成员；普通分隔符和自动 Bingus 分隔符各 1 个 | 121 个旧模组目录缺失；分隔符位置、颜色与折叠状态读取验证通过，未执行整库迁入 |
| Arsenal | 3 个模组、1 个源配置、1 个库分隔符 | 隔离迁入成功，生成 2 个配置；黄色分隔符及两名普通成员范围准确保存；再次执行复用已有结果 |

这些数量是当日扫描快照。验证未写入用户的正式 HD2Mimnt 数据库，也未进行真实游戏运行验证。临时构建与隔离烟测目录需在流程结束后清理。

## 参考

- [Microsoft.Data.Sqlite 连接字符串](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)：只读模式与禁用连接池。
- [SQLite 事务](https://www.sqlite.org/lang_transaction.html)：配置及成员的提交与回滚。
- [Microsoft.Data.Sqlite 备份](https://learn.microsoft.com/dotnet/standard/data/sqlite/backup)：迁回前复制现有数据库到准备目录。
- [Avalonia UI 线程](https://docs.avaloniaui.net/docs/guides/development-guides/accessing-the-ui-thread)：迁移进度与绑定状态更新。
- [导入领域指南](../agent-guides/import-and-resource-types.md)、[部署与加载器指南](../agent-guides/deployment-and-loaders.md)、[UI 分隔符约定](../agent-guides/ui-and-avalonia.md)。
