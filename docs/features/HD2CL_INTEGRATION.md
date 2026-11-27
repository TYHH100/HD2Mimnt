# HD2CL（角色库）原生适配

HD2CL 是角色库工具的缩写（CL = Character Library）。本功能把 `WorkflowType=HD2IAS` 的模型模组编译成独立的 HD2CL 角色库模组，并在管理器内完成入库与配置切换，不依赖外部 exe。

## 入口与行为

- 工具页分类「HD2CL 角色库」：列出模组库中含 IAS 配套（`*.rigbin` / `*.hd2phys` / `*.hd2shared` / `*.hd2pose` / `*.hd2morph` / `*.hd2preview` / `*.isolation.ini` / `mod_group.json`）的 HD2IAS 模组，逐个一键转换。
- 模组卡片右键「转换为 HD2CL 角色库…」：对单个 IAS 模组执行同一流程；模组库与配置页当前 Profile 的模组列表都提供该入口。
- 合集模组（一个模组内包含多个版本/组件）在转换前弹出选择浮窗：按组件文件名里的资源编号自动分组（配套组件归入对应模型组件，纯材质组件随每个版本携带），只扫描所选版本的组件，成品名取所选版本名；普通模组不提示。头/躯干拆开导出的模组共用同一份配套，会自动合并为一个模型，不弹选择。
- 带选项的模组（颜色/部件切换）：只把模型本体编译进成品，配色/部件分支按「Material/组序号/选项序号」原样带进成品，并在成品清单里生成同名的互斥选项；模型与自带材质由首个根目录开关负责部署，配色部署时只展开选中的那一份。当前选择会写成成品默认值（没保存过选择时按默认：选项全开、子选项取第一个）。作者把配色写成多个独立 V1 开关、模型目录在多个配色开关间重复出现时，会归一化成一个互斥材质组；列表中不带材质的 HD2IAS 配套等开关继续作为模型扫描入口，不参与配色组。模型目录只属于单项的模组仍退回原选择，避免误合并不同模型版本。
- 分支目录里混有 Unit 等非材质资源时无法安全保留选项，退回「只转换当前选中分支」；成品选项的部署与源模组一致，修改成品选项后重新部署即可换色。
- 模组库与配置页支持多选：选中多个 HD2IAS 模组后点「转为 HD2CL 角色库」批量转换。先逐个确认版本（多版本才弹浮窗），再并发执行，同时最多 2 个 Worker（单个峰值约 1 GB）；取消版本选择只跳过该模组，失败不影响其余模组，完成后统一提示成功/跳过/失败数量。
- 成品显示名使用模组名或所选版本名，并去掉导入时自动加的「类型 · 」前缀（`Model · `、`HD2IAS · ` 等）；编译出的模型标题（会裁掉“替换 xxx”并追加 `▲`）只保留在游戏内登记信息里。成品入库时打上 `HD2CL` 标签，模组库与配置页显示 `HD2CL` 徽标，用来和 `HD2IAS` 源模组区分。
- 成品：Worker 直接把成品文件写入临时目录，主程序按文件夹导入管理器库（不再先压 7z 再解压）；若存在当前配置，则把成品加入当前配置并启用，同时把源 IAS 模组在当前配置中禁用。源模组文件不覆盖、不删除。需要单独分发时点「导出压缩包…」：转换后把成品打成 7z 写到所选目录，不导入模组库、不修改当前配置；每次转换前选择目录，并记住上次选择。导出包在名字末尾追加 `▲`（同时改写包内 `manifest.json` 的 `Name` 与 7z 文件名），因为游戏内看不到管理器徽标，靠 ▲ 与同名源 IAS 模组区分；已有 ▲ 结尾时不重复添加。此改写只作用于导出副本，不影响入库成品名。
- 材质：源模组自带的可见材质及其贴图沿父链收集后打进 `9ba626afa44a3aa3.patch_2`（含 `.gpu_resources`/`.stream`），因此禁用源模组后成品仍显示原材质；游戏原生材质仍只按资源编号引用，不复制。
- 转换使用默认选择：全部配套组件、`Slim` 体型、随模型携带的材质与图标；不做交互式部件/材质选择。
- 扫描、编译、打包与成品写出都在 `HD2Mimnt.Worker` 独立进程执行（进程 Worker 类型 `Hd2ClConversion`），主程序只负责结果入库与配置开关。转换期间的高 CPU 与大内存不进入 UI 进程，并可在任务中心查看进度。

## MMD 资源包适配

工具页「HD2CL 角色库」内分为「角色模型 / MMD 资源包」两个标签页。MMD 页把原始动作资源编译成独立播放的 HD2CL 数据包：模型与 MMD 使用各自的输入和检查结果，互不替换游戏原有表情动作。

- 来源：ZIP、7z、完整模组目录，或单个 `.patch_*` 三件套入口。清单目录外还有散落文件时拒绝；一个来源包含多份 `manifest.json` 时按合集处理：递归找出含 MMD 动作或适配标记的曲包，排除共用运行时等非 MMD 组件后列出供选择，选中的曲包目录作为后续检查/转换的来源。
- 检查（`MmdAdapter.Inspect`）：读取动作补丁资源，识别“已是独立数据包”、“旧版 90 骨骼动作模组”与“MDL3 曲包”三种来源，列出曲目、时长、帧率、音乐/镜头是否包含、原包动作选项与覆盖告警。合集或来源清单有多个加载选项时必须先选一项再检查；曲包内仍有多个选项时以 `曲包::选项` 组合键继续选择。
- MDL3 曲包：动作仍是 90 骨骼 Stingray 片段，但关键帧记录改用紧凑布局（无 4 字节通道头，记录直接从 `subtype` 开始）；镜头是烘焙到 30 FPS 的 8 浮点采样行；音轨是 32 kHz 双声道 MS-ADPCM，按微软规范解码后重采样到 48 kHz PCM 再打包。登记表缺少 `rate`/`channels` 时从 WAVEFORMATEX 头读取。
- 待机播放方式：不修改时沿用原包（旧包默认 `loop` 持续姿势），可改为 `once` 完整演出；检查结果里的方式回填到界面，用户改动后才作为覆盖值。
- 转换（`MmdAdapter.Convert`）：解码 Stingray 动作关键帧为 `HD2POSE1` 逐帧姿势，提取镜头烘焙表与 48 kHz 双声道 PCM 音轨，按 `mods/hd2cl/mmd/v1/<id>/{catalog,pose,camera,wav,preview}` 打包，成品为 `9ba626afa44a3aa3.patch_0` 三件套加清单；导入模组库并打 `HD2CL` 标记，存在当前配置时自动加入并启用。
- 导出：与角色模型一致，「导出压缩包…」把成品打成 7z 写到所选目录，不导入模组库、不改当前配置；同样记住上次选择的目录。导出名同样追加 `▲`，并同步改写包内清单 `Name`，以便与同名源 IAS 区分。
- 缺清单的裸包需要补充名称与稳定来源标识：`Guid = uuid5(NAMESPACE_URL, "HD2ManualMMDSource/" + key)`；成品 Guid 为 `uuid5(NAMESPACE_URL, "HD2CharacterLibrary/MMD/<id>/adapted-mmd")`。
- 检查与转换都在 `HD2Mimnt.Worker` 的 `MmdAdaptation` 任务中执行（`Action=inspect/convert`），主程序只负责导入与配置开关。

代码分层：

| 层 | 文件 | 对应上游模块 |
| --- | --- | --- |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/Mmd/MmdMotion.cs` | `mmd_motion.py`（90 骨骼动作解码，兼容两种关键帧记录布局） |
| Infrastructure | `ModelConversion/Mmd/MmdBundle.cs` | `mmd_bundle.py`（登记/校验/打包/解包） |
| Infrastructure | `ModelConversion/Mmd/MmdSourceReader.cs` | `mmd_adapter._source` + `source_io` 读取子集、合集曲包探测 |
| Infrastructure | `ModelConversion/Mmd/MmdAdapter.cs` | `mmd_adapter.inspect/convert` |
| Infrastructure | `ModelConversion/Mmd/MmdMdl3Reader.cs` | MDL3 注册表/镜头/音轨脚本解析（上游 `mmd_adapter` 不支持） |
| Infrastructure | `ModelConversion/Mmd/MmdAudio.cs` | MS-ADPCM 解码与 48 kHz 重采样（无上游对应实现） |
| Application | `src/HD2Mimnt.Application/Services/MmdAdaptationService.cs` | 拉起 Worker、导入成品、加入配置、导出 7z |
| Application | `src/HD2Mimnt.Application/Services/Hd2ClArchiveExport.cs` | 导出命名清洗与去重、追加 `▲` 并改写包内清单 `Name`、临时包写完再改名 |
| Worker | `src/HD2Mimnt.Worker/MmdAdaptationWorkerTask.cs` | `Action=inspect/convert` |
| UI | `MmdAdaptationViewModel.cs`、`MmdSourceInfoPromptService.cs`、`Hd2ClMmdView.axaml`、`MoreView.axaml` | 工具页标签页与来源信息弹窗 |

验证基线：合成样例（1 秒静止 90 骨骼动作 + 单音轨 + 31 帧镜头 + 1×1 图标）在 Python 上游与 C# 下产出的 `9ba626afa44a3aa3.patch_0` 与 `manifest.json` SHA-256 完全一致（见 `MmdAdapterTests.Convert_MatchUpstreamHashes`），已适配包再次转换的补丁字节也一致。仅完成结构验证，游戏内播放仍需实机验收。

MDL3 与合集另外用真实资源核对过：管理器导入的 13 首 MDL3 合集能列出 13 个曲包并排除共用运行时，全部检查为 `ready`；`146_Idol_拳击_MDL3` 转换得到 2,704 帧姿势、2,705 行镜头与约 90.1 秒 48 kHz PCM（4,325,376 帧，峰值 5,176、RMS 1,447，无削波），并通过 `MmdBundle.Check` 回读。合成样例覆盖紧凑关键帧、ADPCM 单块解码与 32 kHz→48 kHz 重采样（`MmdMdl3Tests`、`MmdAudioTests`）。

## 代码分层

| 层 | 文件 | 对应上游模块 |
| --- | --- | --- |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/Hd2ClArtifactBuilder.cs` | 扫描组件 → 合并配套 → 编译 → 打包（可在 Worker 独立进程执行，不访问数据库） |
| Infrastructure | `ModelConversion/IasPayload.cs` | 资源载荷视图（内存 / 补丁边车文件段），大文件按段读写 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/Hd2ClVariantPlanner.cs` | 合集模组按组件文件名编号分组出多个可转换版本 |
| Infrastructure | `src/HD2Mimnt.Infrastructure/ModelConversion/IasModelCompiler.cs` | `ias_builder.compile_model`（IAS 路径） |
| Infrastructure | `IasModelPackager.cs` | `manual_backend.package_data`（非 bundled 形态）与成品成员写出 |
| Infrastructure | `IasBodyCatalog.cs` + `Data/ias_body_types.json`、`Data/ias_stock_recipes.json` | `ias_body_types`、`ias_stock_recipes` |
| Infrastructure | `IasCompanionSets.cs` | `companion_sets.read_set` |
| Infrastructure | `IasMorphFormat.cs` / `IasMorphInputs.cs` | `morph_format`、`morph_inputs`（sidecar 完整性、项目归属与 Unit 摘要配对） |
| Infrastructure | `IasNativeMorph.cs` / `IasNativeMorphMaterial.cs` / `IasNativeMorphShader.cs` | `native_morph_companion`、`morph_conversion`（v3 顶点流、私有材质、DXBC 注入与 WARP 校验） |
| Infrastructure | `IasUnit.cs` / `IasCompanion.cs` / `IasArchive.cs` 等 | Unit、Rig/物理/共享、Stingray Archive、序列化 |
| Application | `src/HD2Mimnt.Application/Services/Hd2ClConversionService.cs` | 候选扫描、版本识别、拉起 Worker、导入成品、配置切换、导出 7z |
| Worker | `src/HD2Mimnt.Worker/Hd2ClConversionWorkerTask.cs` | 接收参数并驱动 `Hd2ClArtifactBuilder` |
| UI | `Hd2ClViewModel.cs`、`Hd2ClVariantPromptService.cs`、`Hd2ClBatchConversion.cs`、`MoreView.axaml`、`LibraryView.axaml.cs` | 工具页卡片、版本选择浮窗、多选批量转换与模组右键入口 |

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
- 超二 GB 材质包实测（`VRChat_AIRI_作战服 替换 CM-10`：2.7 GB 独立材质球 + 配套 + 两套模型补丁）：完整转换 19 s、峰值工作集约 1.4 GB，成品 `patch_1` 4,916,832 字节 + GPU 662,864,800 字节，`patch_2` 140,524 字节 + GPU 2,482,658,352 字节（51 个材质、70 张贴图，按模型实际引用筛选，不是整包照抄）；只转模型部分时 `patch_1` 为 4,915,856 字节，与改造前逐字节一致。
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
- 成品成员直接写入目录，导入端不再解压；需要分发时由「导出压缩包…」另打 7z（不入库）；
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
- 导出压缩包必须把成品目录的内容放在压缩包根层（`manifest.json` 直接位于根），不能额外包一层同名目录；否则重新导入时会被当成合集或嵌套清单。
- 成品目录必须位于本次转换的临时目录内，由服务端 `finally` 统一删除；文件夹导入是复制语义，导入完成前不能提前清理。
- 成品目录根层必须直接放 `manifest.json`，不要额外包一层同名子目录；否则导入器会把它当成合集或嵌套清单重新定位根目录。
- 并行 Unit 分类时，每个 Unit 的缓存只能由该 Unit 的任务访问；共享的只有只读 GPU 数组。并行异常要拆掉 `AggregateException`，否则任务中心只剩一句“One or more errors occurred”。
- 合集模组不能把所有组件合并扫描：多个版本的 Unit/材质编号相同，会互相覆盖甚至报“冲突的重复资源”；必须先用 `Hd2ClVariantPlanner` 按组件文件名里的 16 位编号分组，再只扫描所选版本。
- 选项模组不能全量扫描：颜色/部件变体用相同资源编号、内容不同，全部合并会让成品材质来自不同分支且体积暴涨。模型目录与材质分支要分开处理：模型只取非分支目录，分支原样复制并在清单里生成 V1 选项。
- 带 `Options` 的成品清单必须写 `"Version": 1`，否则按 Legacy 解析，选项被忽略、部署回退为全量目录。
- 成品加入配置后要把源模组的选项状态写成成品默认值（`SetModOptionsAsync`），否则会落到第 0 个分支，与用户当前选择不一致。
- 成品清单的第一个选项必须映射根目录（`Include: ["."]`）：有 Include 映射时部署只展开列出的目录，根层模型补丁与自带材质补丁不列就会被静默跳过；对应选项状态也要在最前面补一个 `true`/`0` 占位，否则后续配色开关整体错位。
- 选项预览图要在打包前复制进成品，并把清单 `Image` 改写成成品内相对路径（`Material/组[/选项]/icon.*`）；沿用源模组的 `components/...` 路径在成品里不存在，游戏内面板就不显示缩略图。
- 模型预览（注册信息里的 `preview`）必须登记成 catalog 的兄弟模块 `.../v1/preview`，资源也写在该哈希下；写成 `.../v1/catalog/preview` 时注册信息看似完整，但游戏内选护甲时取不到预览图。
- 选中的目录可能只到 `HD2IndependentRig` 子目录：配套成员键必须补回 `HD2IndependentRig/` 前缀，否则 `.hd2shared` 的引用校验报 "shared reference changed"。
- 头/躯干拆开导出时，同一份配套会同时匹配多个补丁组，这表示它们是同一个模型的不同部件，应合并成一个版本；不同版本各有自己的配套，不会同时命中。
- 部件编号可能只出现在 `hd2author`/`hd2preview` 文件名里，编号提取必须覆盖所有非补丁文件，只认 `rigbin`/`hd2phys` 会漏掉另一部件，导致重新拆成两个版本。
- 同一版本内补丁按加载顺序覆盖同名资源（材质差分包会以相同编号重复出现），扫描顺序即覆盖顺序，后者生效；不要恢复成“内容不同就报错”。
- 服饰姿态（`HD2POSE1`）的 Rig 指纹按上游 `TARGET` 布局拼接：名称 64 字节，`parent/source/slot/flags` 各 4 字节，`translation_scale` 与 12 个 `rest_local` 都是 **float32**。C# 内部用 `double` 保存这些值，序列化时漏转 `(float)` 会让每条骨骼多出一半字节，指纹永远不匹配。
- 蒙皮调色板要按分区行末尾的 `group` 字段取重映射表，不能用分区序号：同一调色板组会被多个分区共用（真实模组里常见 16 个分区只有 8 组），用序号会在分区数大于表数时抛 “Index was out of range”，不越界时则会静默取到别的调色板，蒙皮整体错位。
- 补丁边车（`.gpu_resources`/`.stream`）不能整块 `ReadAllBytes`：真实材质包单文件可达 2.7 GB，超过 .NET 单数组上限。资源载荷统一走 `IasPayload`（内存或「文件 + 偏移 + 长度」视图），扫描只登记视图，写出用 `IasArchive.WriteTo` 边读边写；需要整块解析的小资源（Unit 顶点）首次访问时缓存，材质/贴图永远不整块载入。
- 成品清单名应使用用户可见的模组/版本名；编译出的模型标题只是游戏内登记名（上游会裁掉“替换 xxx”并追加 `▲`），不能拿来当成品名。**入库成品**的识别靠 `HD2CL` 标签徽标，不靠名称后缀；**导出压缩包**则靠名字末尾的 `▲` 区分，因为分发包脱离管理器后看不到徽标。两处场景不同，不冲突。
- 旧版本转换出的入库成品没有 `HD2CL` 标签、名字里带 `▲`，需要删除后重新转换。
- 批量转换的版本选择必须在启动 Worker 之前全部确认完：并发里再弹模态窗会互相抢占界面；版本浮窗点取消只跳过当前模组。
- 批量并发上限为 2：每个 Worker 转换完整模型时峰值约 1 GB，带超大材质包（成品 3 GB 级）时约 1.4 GB，放开并发会把内存打满。
- MMD 动作片段头部是 24 字节 `<IIfIII`，但骨骼通道从偏移 60 开始（26 字节头 + 2 字节保留 + 34 字节通道标志位）；从 26 直接读会把标志位当成平移，随后报“动作含未支持的缩放通道”。
- 镜头数据按 Python `splitlines()` 语义切行，尾部换行不产生空行；用 `Split('\n')` 会多出一行导致“MMD 镜头帧数或切点无效”。
- MMD 登记行与清单的 JSON 键顺序参与哈希：动作行顺序为 `protocol,id,title,fps,duration,stage,origin,idle_playback,delivery,sha256[,preview]`；清单 `json.dumps(indent=2)` 无尾随换行（角色模型清单才带换行）。
- 成品描述常量使用全角分号（“镜头；安装时”）；字符不一致会改变 `manifest.json` 摘要。
- 已适配包再次转换必须按 Lua 资源里的 `-- HD2CL_MMD_V1 ` 标记优先识别，不能落回旧版动作解析；否则会把登记资源当成作者脚本处理。
- MMD 来源常为 GBK 文件名 ZIP：压缩包读取按 cp936 回退，成员路径统一成 posix 并拒绝 `..`、盘符与 Windows 保留名。
- 待机播放方式不要用 ComboBox：Avalonia 的选中项在运行时切换语言后按旧文本宽度裁切，改用两个单选项。
- MDL3 工具导出的动作片段关键帧记录没有 4 字节通道头，记录直接以 `subtype` 开头；按经典布局硬读会报“未知的动作通道 0”。解码前先用首条记录判断两种布局（`subtype ∈ {2,4,5,6}` 且经典 sub 不成立才算紧凑），两种布局不能混用。
- MS-ADPCM 预测除数是 **256** 不是 64：`(s1*c1 + s2*c2) / 256`（[MultimediaWiki](https://wiki.multimedia.cx/index.php/Microsoft_ADPCM)）。用 64 会让整轨削波成方波（峰值 32768、RMS 接近满幅），看起来“能播”但全是噪声。
- MDL3 音轨 Lua 不一定写 `rate`/`channels`，按 WAVEFORMATEX 头（声道数偏移 2、采样率偏移 4）回退。
- MS-ADPCM 块头 14 字节（预测器各 1 字节，delta/sample1/sample2 各 2 字节交错）；每个数据字节高半字节为左声道、低半字节为右声道，一个字节只产生一帧，块内样本数 = 2 + (块长 − 14)。
- 合集曲包探测直接读 `*.patch_0` 的表：有 Animation 类型行或 Lua 行带 `-- HD2CL_MMD_V1 ` 标记才算 MMD 曲包；按 `manifest.json` 名称判断会把“共用运行时”也列进来。只有一份含 MMD 曲包时自动选中，避免用户多点一次。

## 已知限制

- 只实现非 bundled 单模型成品：源模组自带的可见材质与其贴图会打进 `patch_2`，外部材质包仍按原资源编号引用。
- `HD2Morph1` 形态件在转换前核对预览/独立清单归属、项目与 Archive 身份以及源 Unit 三件套摘要；通过后生成 `native_vertex_morph_v1`（version=3）声明、stride 56→160 的保留原脸顶点流和私有形态材质，私有材质单独写入 `patch_3`。DXBC 注入通过 D3D11 WARP 校验，原始网格、UV、蒙皮、索引与非顶点着色阶段保持不变。
- 当前形态件转换使用来源模组当前选中的材质分支；多分支形态材质差分尚未生成互相绑定的成品选项，选择其它分支后需重新转换。每个 Unit 最多支持 6 个有有效变化的语义控制，且只接受已审阅的 stride 56 顶点布局。
- 单个资源载荷上限为 4 GB（归档行用 32 位长度）；单文件超过 4 GB 的补丁仍会拒绝。当前实测最大单文件为 2.7 GB，可正常转换。
- 等价皮肤判断用于旧版部件迁移，按上游可见属性/蒙皮/位置误差 `< 2e-5` 复核；皮肤通道不可访问时会退化为更保守的结果。
- 成品默认以文件夹交付并直接入库；「导出压缩包…」走独立路径，把同一份成品打成 7z 放到所选目录，不导入模组库、不改当前配置。导出包根层直接是成品内容，可直接分发或重新导入。
- 合集版本按组件文件名编号分组：无法与任何模型组件关联的配套组件会附到每个版本，暂不支持把多个版本合并进一个成品。
- 成品选项支持「一级选项 + 一层互斥子选项」的材质分支，以及每个配色开关映射一个独立材质目录、模型目录在多个开关间重复出现的独立配色开关；多层嵌套、单项映射多个材质目录或分支含非材质资源时退回单分支转换。选项预览图按清单 `Image` 复制到 `Material/组[/选项]/icon.*` 并改写为成品内相对路径，未声明时退回分支目录的 `icon.*`；源模组没有图就没有缩略图。
- 形态件离线验证覆盖 sidecar 完整性、来源配对、BLAKE2s 归属哈希、DXBC 注入/WARP 与私有材质补丁打包；未用真实人物形态件做游戏内外观、眨眼和材质动画验收。
- 不校验作者签名、不做游戏内运行验证；转换结果仍需实机验收。
- MDL3 音轨从 32 kHz 重采样到 48 kHz 使用 Catmull-Rom 插值，属于有损重建，音高/时长正确但高频细节与原生重采样不完全一致；不含内嵌音轨的曲包（检查结果音乐为否）转换后没有 wav，需曲包自带音乐或依赖共用运行时。
- 紧凑关键帧布局目前只见到 `subtype` 记录（平移/四元数原始浮点）；若其他工具在同一布局里混入压缩记录，会报“未知的动作通道”。

## 许可与来源

本功能是用户提供并授权集成的已恢复 Python 业务模块的 C# 原生移植，不随程序分发上游 exe 或 Python 运行时，也不包含第三方贴图/字体。原始模型与材质仍归各自作者所有；成品只引用其原始资源编号。数据表 provenance 以 JSON 内记录为准。
