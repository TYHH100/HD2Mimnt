# HD2CL（角色库）原生适配

HD2CL 是角色库工具的缩写（CL = Character Library）。本功能把 `WorkflowType=HD2IAS` 的模型模组编译成独立的 HD2CL 角色库模组，并在管理器内完成入库与配置切换，不依赖外部 exe。

## 入口与行为

- 工具页分类「HD2CL 角色库」：列出模组库中含 IAS 配套（`*.rigbin` / `*.hd2phys` / `*.hd2shared` / `*.hd2pose` / `mod_group.json`）的 HD2IAS 模组，逐个一键转换。
- 模组卡片右键「转换为 HD2CL 角色库…」：对单个 IAS 模组执行同一流程。
- 成品：`<名称>_角色库.7z` 导入管理器库；若存在当前配置，则把成品加入当前配置并启用，同时把源 IAS 模组在当前配置中禁用。源模组文件不覆盖、不删除。
- 转换使用默认选择：全部配套组件、`Slim` 体型、随模型携带的材质与图标；不做交互式部件/材质选择。

## 代码分层

| 层 | 文件 | 对应上游模块 |
| --- | --- | --- |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/IasModelCompiler.cs` | `ias_builder.compile_model`（IAS 路径） |
| Infrastructure | `IasModelPackager.cs` | `manual_backend.package_data`（非 bundled 形态）+ `7z` LZMA2 |
| Infrastructure | `IasBodyCatalog.cs` + `Data/ias_body_types.json`、`Data/ias_stock_recipes.json` | `ias_body_types`、`ias_stock_recipes` |
| Infrastructure | `IasCompanionSets.cs` | `companion_sets.read_set` |
| Infrastructure | `IasUnit.cs` / `IasCompanion.cs` / `IasArchive.cs` 等 | Unit、Rig/物理/共享、Stingray Archive、序列化 |
| Application | `src/HD2Mimnt.Application/Services/Hd2ClConversionService.cs` | 扫描组件、编译、打包、导入、配置切换 |
| UI | `Hd2ClViewModel.cs`、`MoreView.axaml`、`LibraryView.axaml.cs` | 工具页卡片与模组右键入口 |

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
- Box 压缩成员回读一致，7z 使用 LZMA2、128 MB 字典、`-mfb=273`、单线程固实块。

对应探针测试在 `tests/HD2Mimnt.Tests/ModelConversion/FixtureProbeTests.cs`（依赖本机样本路径，仅用于开发核对，不入库）。

## 易错点与正确做法

- `Archive` 行的 `meta0/meta1` 是 8 字节 `Q`，其余尺寸字段才是 `I`；按 32 位读会导致整行错位。
- Rig/预览 Rig 中源矩阵和节点 Rest 都是 **32 位 float**（`12f`），C# 写 `double` 会让长度与 CRC 校验失败。
- 原生数据 JSON 必须使用小写字段名，并关闭 `+`/`/` 的默认转义（`JavaScriptEncoder.UnsafeRelaxedJsonEscaping`）；否则 base64 会被转义成 `\u002B`，体积和摘要都会变。
- 目录里的 `sources` 与 `source_native_graph` 把 `q/p` 直接展开在节点上；`profiles` 的节点才是 `local_rest`/`rest` 嵌套。
- `tp_grip_nodes` 的键是 `l_hand`/`r_hand`，值是 socket；不要用 socket 名当键。
- 材质依赖要沿 Material TOC 偏移 24 的父链展开；只按可见 Unit 收集会少一个父材质依赖。
- 精确对照时，Lua 目录字面量本身不含代码末尾换行；比较时要显式处理这一字节。

## 已知限制

- 只实现非 bundled 单模型成品：不重打包外部材质、不处理形态键私有材质、不生成预览 Lua 之外的额外资源。
- 形态键（morph）仍需上游手动转换入口；检测到 `HD2Morph1` 会在配套解析阶段直接拒绝。
- 等价皮肤判断用于旧版部件迁移，按上游可见属性/蒙皮/位置误差 `< 2e-5` 复核；皮肤通道不可访问时会退化为更保守的结果。
- 7z 归档字节不要求与 py7zr 相同，仅要求成员内容与清单一致；成员顺序固定为字典序。
- 不校验作者签名、不做游戏内运行验证；转换结果仍需实机验收。

## 许可与来源

本功能是用户提供并授权集成的已恢复 Python 业务模块的 C# 原生移植，不随程序分发上游 exe 或 Python 运行时，也不包含第三方贴图/字体。原始模型与材质仍归各自作者所有；成品只引用其原始资源编号。数据表 provenance 以 JSON 内记录为准。
