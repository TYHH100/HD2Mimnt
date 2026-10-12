# 外部管理器迁移

后端通过 `ExternalManagerMigrationService` 迁入 Helldivers2ModManager、Echelon、HD2Arsenal 的模组库和完整配置。服务已注册到应用 DI；当前没有新增迁移页面或按钮。

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

类型位于 `HD2Mimnt.Core.Migration`，读取器接口为 `IExternalManagerReader`，业务入口位于 Application 层。

```csharp
var source = new ExternalMigrationSource(
    ExternalManagerKind.Helldivers2ModManager,
    DataPath: @"G:\Temp\HD2ModManager\Mods",
    InstallationPath: @"D:\HD2MM\Helldivers2ModManager");

var plan = await migrationService.PreviewAsync(source, cancellationToken);
// plan.Mods / Profiles 给出迁移内容，plan.Issues 给出缺失或不兼容项。
var result = await migrationService.MigrateAsync(plan, progress, cancellationToken);
```

- `PreviewAsync` 只读源数据，返回计划，不复制文件或写目标数据库。
- `MigrateAsync` 在后台依次处理模组和配置，使用 `IProgress<ExternalMigrationProgress>` 报告完成数量、阶段和当前项。
- `ExternalMigrationResult` 返回导入/复用数量、源 ID 到目标 ID 的映射、问题列表，以及源活动配置对应的 `SourceActiveProfileId`。
- `IsComplete` 仅在没有问题时为 true；调用方应展示 `Issues`。取消会抛出 `OperationCanceledException`，等待任务结束后，未提交副本的清理才已完成。
- 读取器可通过实现接口并注册到 DI 扩展。View/ViewModel 只调用业务入口，不自行读取数据库或复制文件。

## 保存与重试策略

源 JSON、数据库和模组文件保持只读。HD2MM 数据库使用 `Mode=ReadOnly;Pooling=False` 短连接；目标模组复制到自己的库目录，迁移后不依赖源模组目录。

每个模组先完成文件复制、图片、标签、清单转换、资源识别和完整内容哈希，再写入数据库。身份由管理器、源数据目录、源条目 ID 和准备后内容哈希共同生成；源作者 GUID 留在清单中用于加载器识别，不作为目标数据库 ID。同名或同 GUID 的用户已有模组不会被覆盖。

配置按来源和内容生成快照身份。相同快照重复迁入会复用记录，保留迁入后用户的编辑；源模组内容或配置内容改变会新增快照。同名配置使用 `(2)` 等后缀。迁入配置不成为默认配置，也不自动切换目标活动配置。

模组和配置分别提交，整批不是一个大事务。已提交项保留，取消或失败可重试。一个配置的成员、启用状态、选项和分隔符通过现有仓储在同一次事务中写入；任一成员未成功迁入，整个配置跳过并报告问题，不生成残缺配置。

## 选项、优先级和分隔符

HD2MM 的选项排列与子选项排列同时作用于定义和保存的选择索引，不能只移动名称。无效的排列拒绝预览。独立部署排序转换为目标配置的有效顺序；若它使分隔符成员不连续，报告问题并跳过该配置，供源软件调整后重试。

Echelon 根据 `sets[].files` 复制精确文件集合，每个集合映射到 `migration-sets/0000/` 等独立目录，避免同目录的其它集合被选中。有选项时仅部署选项引用的集合；无选项时部署全部声明的集合。成员缺失部分选项状态时补齐库中的 `option_defaults`，包括默认子选项选择。

Arsenal 的选项状态兼容 `options` 与 `optionsConfig`。库中独有的分隔符保存在一个成员全部禁用的“HD2Arsenal 模组库分组”配置中，使库顺序和分组可查看；当前目标模组库没有单独的分隔符实体。

分隔符保留名称、成员范围、背景颜色和折叠状态。颜色规范化为目标支持的 `#RRGGBB`；前景文字使用目标主题。Echelon 的自动 Bingus 分隔符映射到目标固定 Lua 分组的颜色和折叠状态。

源与目标部署方向不同，按当前目标全局方向转换普通模组顺序和整段分隔符范围，保留普通模组覆盖优先级，不修改全局设置。Lua 依赖、BSL/MDL、SmoothBoot 进入目标已有固定区域，分隔符范围排除固定成员；例如源分隔符前的加载器移入固定区域后，其数字位置会改变，但原普通成员归属仍保留。同时启用 BSL 和 MDL 的源配置会被拒绝。

清单转换保留包中未知字段，例如 provenance；`Version` 保存清单格式，`ModVersion` 保存模组版本。`Include: []` 表示显式不包含文件，与缺少 `Include` 的默认目录映射保持区别。

## 验证记录

2026-10-12，在隔离库和数据库中完成构建及测试：迁移专项 17/17，配置、选项、导入和部署相关回归 85/85。测试覆盖多配置独立状态、Legacy/V1 及排列转换、分隔符范围和方向、Echelon 实际选中集合部署、重复迁入、源内容改变、图片与包元数据、取消清理、模组/配置提交失败及重试。

在用户启用分隔符后的本地数据上进行只读扫描，并将 Arsenal 迁入隔离库：

| 来源 | 扫描结果 | 验证边界 |
| --- | --- | --- |
| HD2MM | 85 个库模组、1 个配置、77 个成员、2 个分隔符 | 配置中有 10 个引用不在源库中；未执行整库迁入 |
| Echelon | 122 个库条目、1 个配置、121 个成员；普通分隔符和自动 Bingus 分隔符各 1 个 | 121 个旧模组目录缺失；分隔符位置、颜色与折叠状态读取验证通过，未执行整库迁入 |
| Arsenal | 3 个模组、1 个源配置、1 个库分隔符 | 隔离迁入成功，生成 2 个配置；黄色分隔符及两名普通成员范围准确保存；再次执行复用已有结果 |

这些数量是当日扫描快照。验证未写入用户的正式 HD2Mimnt 数据库，也未进行真实游戏运行验证。临时构建与隔离烟测目录完成后删除。

## 参考

- [Microsoft.Data.Sqlite 连接字符串](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)：只读模式与禁用连接池。
- [SQLite 事务](https://www.sqlite.org/lang_transaction.html)：配置及成员的提交与回滚。
- [导入领域指南](../agent-guides/import-and-resource-types.md)、[部署与加载器指南](../agent-guides/deployment-and-loaders.md)、[UI 分隔符约定](../agent-guides/ui-and-avalonia.md)。
