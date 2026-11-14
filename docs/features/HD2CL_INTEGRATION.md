# HD2CL（角色库）原生适配

HD2CL 是角色库工具的缩写（CL = Character Library）。本功能把 `WorkflowType=HD2IAS` 的模型模组编译成独立的 HD2CL 角色库模组，并在管理器内完成入库与配置切换，不依赖外部 exe。

## 入口与行为

- 工具页分类「HD2CL 角色库」：列出模组库中含 IAS 配套（`*.rigbin` / `*.hd2phys` / `*.hd2shared` / `*.hd2pose` / `mod_group.json`）的 HD2IAS 模组，逐个一键转换。
- 模组卡片右键「转换为 HD2CL 角色库…」：对单个 IAS 模组执行同一流程；模组库与配置页当前 Profile 的模组列表都提供该入口。
- 合集模组（一个模组内包含多个版本/组件）在转换前弹出选择浮窗：按组件文件名里的资源编号自动分组（配套组件归入对应模型组件，纯材质组件随每个版本携带），只扫描所选版本的组件，成品名取所选版本名；普通模组不提示。
- 成品显示名使用模组名或所选版本名，并去掉导入时自动加的「类型 · 」前缀（`Model · `、`HD2IAS · ` 等）；编译出的模型标题（会裁掉“替换 xxx”并追加 `▲`）只保留在游戏内登记信息里。成品入库时打上 `HD2CL` 标签，模组库与配置页显示 `HD2CL` 徽标，用来和 `HD2IAS` 源模组区分。
- 成品：Worker 直接把成品文件写入临时目录，主程序按文件夹导入管理器库（不再先压 7z 再解压）；若存在当前配置，则把成品加入当前配置并启用，同时把源 IAS 模组在当前配置中禁用。源模组文件不覆盖、不删除。如需单独分发压缩包，后续再加导出选项。
- 材质：源模组自带的可见材质及其贴图沿父链收集后打进 `9ba626afa44a3aa3.patch_2`（含 `.gpu_resources`/`.stream`），因此禁用源模组后成品仍显示原材质；游戏原生材质仍只按资源编号引用，不复制。
- 转换使用默认选择：全部配套组件、`Slim` 体型、随模型携带的材质与图标；不做交互式部件/材质选择。
- 扫描、编译、打包与成品写出都在 `HD2Mimnt.Worker` 独立进程执行（进程 Worker 类型 `Hd2ClConversion`），主程序只负责结果入库与配置开关。转换期间的高 CPU 与大内存不进入 UI 进程，并可在任务中心查看进度。

## 代码分层

| 层 | 文件 | 对应上游模块 |
| --- | --- | --- |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/Hd2ClArtifactBuilder.cs` | 扫描组件 → 合并配套 → 编译 → 打包（可在 Worker 独立进程执行，不访问数据库） |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/Hd2ClVariantPlanner.cs` | 合集模组按组件文件名编号分组出多个可转换版本 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/IasModelCompiler.cs` | `ias_builder.compile_model`（IAS 路径） |
| Infrastructure | `IasModelPackager.cs` | `manual_backend.package_data`（非 bundled 形态）与成品成员写出 |
| Infrastructure | `IasBodyCatalog.cs` + `Data/ias_body_types.json`、`Data/ias_stock_recipes.json` | `ias_body_types`、`ias_stock_recipes` |
| Infrastructure | `IasCompanionSets.cs` | `companion_sets.read_set` |
| Infrastructure | `IasUnit.cs` / `IasCompanion.cs` / `IasArchive.cs` 等 | Unit、Rig/物理/共享、Stingray Archive、序列化 |
| Application | `src/HD2Mimnt.Application/Services/Hd2ClConversionService.cs` | 候选扫描、版本识别、拉起 Worker、导入成品、配置切换 |
| Worker | `src/HD2Mimnt.Worker/Hd2ClConversionWorkerTask.cs` | 接收参数并驱动 `Hd2ClArtifactBuilder` |
| UI | `Hd2ClViewModel.cs`、`Hd2ClVariantPromptService.cs`、`MoreView.axaml`、`LibraryView.axaml.cs` | 工具页卡片、版本选择浮窗与模组右键入口 |

序列化必须与上游逐字节一致：Lua 目录使用 `IasLua.Literal`，登记与原生数据分别使用 `IasLua.JsonBytes`（ASCII、键排序）和 `System.Text.Json`（小写字段名、不转义 `+`/`/`）。改动这些序列化会直接改变成品哈希。

## 数据表来源

`Data/*.json` 由随附的已恢复 Python 模块导出（只读数据，不含可执行代码）：

- `ias_body_types.json`：`NATIVE_BODY_TYPES`、`NATIVE_SOURCE_BODY_TYPES`、来源与策略说明；
- `ias_stock_recipes.json`：原生外观装配配方（`METADATA_SHA256` 一并保留）。

数据表 provenance 记录在 JSON 的 `provenance` / `runtime_provenance` 字段中；本仓库不再新增手工抄写表。

## 验证基线

以真实 Ramune IAS 模组（4 个组件：材质、模型、头盔物理、盔甲物理）对照上游 Python 产物：

- 目录字面量：C# 与上游完全一致（UTF-8 1,571,916 字节，不含模型代码末尾换行）；
- `manifest.json`：129 字节，字节一致（SHA-256 `f1ef30308d5e0b72…`）；
- `9ba626afa44a3aa3.patch_1`：2,345,804 字节，字节一致（SHA-256 `abe495d28a8622f2…`）；
- `.gpu_resources`：94,315,616 字节，字节一致（SHA-256 `50c14707f91c4098…`）；
- `.stream`：0 字节；
- 私有 Unit 数量 33（15 个 TP + 16 个分区视图 + 私有 Bones），原生物理域 2 个；
- 成员在写出前由 `IasArchive.VerifyReadback` 逐资源回读校验；成品以文件夹交付，不再生成压缩包。
- 源模组自带材质时另写出 `9ba626afa44a3aa3.patch_2` 及其 `.gpu_resources`/`.stream`，模型补丁本身仍不含材质与贴图。
- 合集样例（3 个模型组件 + 3 个配套组件 + 1 个材质组件）能识别出 3 个版本，逐版本转换均成功，材质包自动随每个版本携带。
- 含 v3 共享身体与服饰姿态的真实模组（`VRchat_AIRI_CM-09`）此前因指纹按 `double` 拼接报 “clothing pose Rig fingerprint differs”，修正为 float32 后转换通过。

性能改造后另做了一次回归对照（本机真实模组：35 个源 Unit、4 个补丁组、GPU 合计约 181 MB）：
优化前后的四个成品文件（`manifest.json`、`.patch_1`、`.gpu_resources`、`.stream`）SHA-256 全部一致。

## 性能与内存（实测）

同一真实模组，在独立 Worker 中从零转换到成品写出（不含主程序入库复制）：

| 指标 | 改造前（提交 `e0318cf`） | 改造后 |
| --- | --- | --- |
| 总耗时 | 约 143 s | 约 10 s |
| 其中编译 | 约 20 s | 约 8–9 s |
| 其中压缩与回读 | 约 120 s | 0（直接写成品文件） |
| 迁移前内存峰值 | UI 进程内，扫描期整份模组至少两份副本 | Worker 进程约 1.0 GB，UI 仅承担导入与配置切换 |

主要改动：

- 扫描时只保留配套文件，补丁三元组逐个解析后立即释放，不再把整份模组读入再复制；
- `IasArchive.Write` 按对齐规则先算精确长度再一次性分配，去掉 `List<byte>` 扩容与 `ToArray` 副本；
- 成品成员直接写入目录，导入端不再解压；需要分发压缩包时再加单独导出选项；
- 顶点通道偏移、蒙皮布局与零蒙皮判定缓存；几何签名改为结构比较，分区掩码写入与校验改为零分配 span；
- 每个 Unit 的几何分类与视图分区互不共享状态，按 4/3 并行度执行。

对应探针脚本仅在开发期临时使用（仓库外），不随代码入库。

## 易错点与正确做法

- `Archive` 行的 `meta0/meta1` 是 8 字节 `Q`，其余尺寸字段才是 `I`；按 32 位读会导致整行错位。
- Rig/预览 Rig 中源矩阵和节点 Rest 都是 **32 位 float**（`12f`），C# 写 `double` 会让长度与 CRC 校验失败。
- 原生数据 JSON 必须使用小写字段名，并关闭 `+`/`/` 的默认转义（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`）；否则 base64 会被转义成 `\u002B`，体积和摘要都会变。
- 目录里的 `sources` 与 `source_native_graph` 把 `q/p` 直接展开在节点上；`profiles` 的节点才是 `local_rest`/`rest` 嵌套。
- `tp_grip_nodes` 的键是 `l_hand`/`r_hand`，值是 socket；不要用 socket 名当键。
- 材质依赖要沿 Material TOC 偏移 24 的父链展开；只按可见 Unit 收集会少一个父材质依赖。
- 材质的贴图依赖在 Material TOC 偏移 64 处记录数量（上限 4096），贴图编号从 `136 + 4*count` 起按 `count` 个 `uint64` 排列；打包材质时必须把这些贴图一并写入 `patch_2`，否则禁用源模组后材质贴图丢失。
- 精确对照时，Lua 目录字面量本身不含代码末尾换行；比较时要显式处理这一字节。
- 配套成员表不能简单“只保留 companion 后缀”：`mod_group.json` 会按清单核对任意列出文件的摘要，漏掉图片或作者文件会导致摘要不符。当前做法是排除补丁本体与 `.gpu_resources`/`.stream` 边车，其余保留。
- 成品目录必须位于本次转换的临时目录内，由服务端 `finally` 统一删除；文件夹导入是复制语义，导入完成前不能提前清理。
- 成品目录根层必须直接放 `manifest.json`，不要额外包一层同名子目录；否则导入器会把它当成合集或嵌套清单重新定位根目录。
- 并行 Unit 分类时，每个 Unit 的缓存只能由该 Unit 的任务访问；共享的只有只读 GPU 数组。并行异常要拆掉 `AggregateException`，否则任务中心只剩一句“One or more errors occurred”。
- 合集模组不能把所有组件合并扫描：多个版本的 Unit/材质编号相同，会互相覆盖甚至报“冲突的重复资源”；必须先用 `Hd2ClVariantPlanner` 按组件文件名里的 16 位编号分组，再只扫描所选版本。
- 同一版本内补丁按加载顺序覆盖同名资源（材质差分包会以相同编号重复出现），扫描顺序即覆盖顺序，后者生效；不要恢复成“内容不同就报错”。
- 服饰姿态（`HD2POSE1`）的 Rig 指纹按上游 `TARGET` 布局拼接：名称 64 字节，`parent/source/slot/flags` 各 4 字节，`translation_scale` 与 12 个 `rest_local` 都是 **float32**。C# 内部用 `double` 保存这些值，序列化时漏转 `(float)` 会让每条骨骼多出一半字节，指纹永远不匹配。
- 成品清单名应使用用户可见的模组/版本名；编译出的模型标题只是游戏内登记名（上游会裁掉“替换 xxx”并追加 `▲`），不能拿来当成品名。识别成品靠 `HD2CL` 标签徽标，不靠名称后缀。
- 旧版本转换出的成品没有 `HD2CL` 标签、名字里带 `▲`，需要删除后重新转换。

## 已知限制

- 只实现非 bundled 单模型成品：源模组自带的可见材质与其贴图会打进 `patch_2`，但不重打包外部材质包，也不处理形态键私有材质。
- 形态键（morph）仍需上游手动转换入口；检测到 `HD2Morph1` 会在配套解析阶段直接拒绝。
- 等价皮肤判断用于旧版部件迁移，按上游可见属性/蒙皮/位置误差 `< 2e-5` 复核；皮肤通道不可访问时会退化为更保守的结果。
- 成品以文件夹交付并直接入库，不保留中间压缩包；如果以后需要单独分发 `.7z`，再增加显式导出选项。
- 合集版本按组件文件名编号分组：无法与任何模型组件关联的配套组件会附到每个版本，暂不支持把多个版本合并进一个成品。
- 不校验作者签名、不做游戏内运行验证；转换结果仍需实机验收。

## 许可与来源

本功能是用户提供并授权集成的已恢复 Python 业务模块的 C# 原生移植，不随程序分发上游 exe 或 Python 运行时，也不包含第三方贴图/字体。原始模型与材质仍归各自作者所有；成品只引用其原始资源编号。数据表 provenance 以 JSON 内记录为准。
