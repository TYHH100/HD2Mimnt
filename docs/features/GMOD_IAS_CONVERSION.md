# GMOD 模型转 HD2 IAS 模型

> 状态（2026-11-06）：模块已实现并接入 CLI 与「工具 → GMOD 模型转换」页面。每个已发布 Unit 都会写成
> 游戏原生 Stingray 资源（`.unit` / `.material` / 贴图），打进 `9ba626afa44a3aa3.patch_0` 三件套，
> 并附标准 V1 `manifest.json` 与 `hd2mimnt.json` ——`package/` 目录本身即可导入模组库并部署。
> 尚未在真实游戏内验收；近似处理与边界见文末。

## 功能

把 Garry's Mod（Source 引擎）addon 中的 `.mdl` 模型转换成 HD2 IAS（Independent Armor System）转换包：

- **MDL v48 与 v49**：v48（CS:GO/GMOD 主流）与 v49（Alien Swarm / L4D2 / SFM 分支）布局均已验证支持；
  两者仅差异是 v49 的 jiggle bone 没有 boing 数据块（boing 参数自动置零）。其余版本仍拒绝并明确报错。
- **身体/头盔拆分**：HD2 把身体护甲与头盔作为两套 IAS 包装备，而 GMOD 的 MDL 头与身体在同一模型里，
  因此默认按骨骼权重把网格拆成 `body/` 与 `helmet/` 两个完整 IAS 包（头盔包的 isolation/manifest id
  加 `-helmet` 后缀、Archive ID 可用 `--helmet-archive` 指定，否则自动派生）。动骨链也按根部骨骼归属
  拆分到对应包；头部骨骼识别不到时只产出身体包并记录 `HeadBoneNotFound` 提示。
- **模型完整**：重建所有 `$bodygroup` 备选 × 所有 skin 家族 × 所有 mesh 的几何，保留顶点位置、法线、
  UV、切线、最多三路骨骼权重，以及全部骨骼、rest pose、附件与 34 个 `$jigglebone` 参数。
- **动骨**：`$jigglebone`、`BONE_PHYSICALLY_SIMULATED` 骨骼转成 HD2PHY1 弹簧链，每个已发布 Unit
  一份物理资源；`.phy` 原始字节与不能直接映射的 procedural bone 一并保留并在报告中注明。
- **纹理材质**：全部 VMT 参数/条件/代理以结构化 JSON 保留，VTF 尽量解码成 PNG，VMT/VTF 原始字节
  逐字节保留。
- **开关**：bodygroup 与 skin 家族发布为 IAS v6 `free_parts` 选项，并通过 `free_route` / `free_rig_profile`
  关联到已发布 Unit；同时生成 `HD2IRG1` rig 与 isolation 清单。可用 `--switches skin,bodygroup:N`
  只发布部分开关，组合选项数超过 256 时自动裁剪并记录 `SwitchOptionsCapped`。
- **可直接运行的模组包**：每个已发布 Unit 写成原生 `.unit`，配 `.material` 与 BC1/BC3 贴图，打进
  `9ba626afa44a3aa3.patch_0` 三件套；`package/` 内含标准 V1 `manifest.json` 与 `hd2mimnt.json`，
  可直接导入模组库并部署，不再需要经 Blender 重新导出网格。
- **可核对**：输出 `conversion-report.json`（保真统计与全部提示）、`split.json`（拆分模式下的部位清单）、
  `runnable.json`（每个部位的 Unit/材质/贴图与补丁体积）与 `manifest.json`（每个文件的 SHA-256）。

## 使用

### 图形界面

「工具 → GMOD 模型转换」：选择 addon（`.gma` 或已解压目录）后**自动扫描其内容**，把 addon 内所有
可转换的 `.mdl` 填进「模型路径」下拉框（每项显示骨骼/网格/开关/动骨/材质数量），默认选中玩家模型或
网格最多的模型；缺少 VVD/VTX、或版本不是 v48/v49 的 `.mdl` 会列在「分析说明」里而不进入下拉框。
之后填写输出目录；「IAS 发布选项」中可设置项目 ID、Archive ID、头盔 Archive ID、Equipment Key、
Native Unit ID、内容域、**身体/头盔拆分方式**（身体与头盔 / 仅身体 / 仅头盔 / 整包）、开关模式、
**发布的开关**（如 `skin,bodygroup:1`，留空发布全部）与三个保真开关。修改 addon 路径会自动重新扫描
（350 ms 防抖），也可点「分析 addon」手动重扫。写入前会确认输出目录，转换过程可取消，取消与失败
都不留下不完整产物。

### 命令行

```text
gmod-ias list <addon.gma|目录> [--groups]
gmod-ias convert <addon.gma|目录> <model.mdl> <输出目录> --yes \
  [--project ID] [--archive 0x…] [--helmet-archive 0x…] [--domain body|helmet] \
  [--equipment 0x…] [--native 0x…,0x…] [--switch-mode auto|independent|composite] \
  [--split both|body|helmet|none] [--switches skin,bodygroup:N] \
  [--no-skin] [--no-physics] [--no-source]
```

每个部位目录里都会多出 `package/`（可直接导入的 data 模组，含原生 Stingray 补丁）与 `runnable.json`；
选项越多补丁越大（每个选项一份 Unit），体积过大时报告会提示用 `--switches` 收窄。

`--yes` 必须显式追加（会写入输出目录）。`list` 扫描 addon 内全部 `.mdl`（`--groups` 附带每个模型的
bodygroup 编号/名称/备选，供 `--switches` 参考）。未提供的 ID 会按项目名与模型路径生成稳定的非零默认值；
实际部署到游戏时应填写目标 Unit 的真实资源 ID。

### API

- `HD2Mimnt.Application.Services.GmodIasConversionService.AnalyzeAsync` — 扫描 addon，返回可转换模型清单与逐模型诊断。
- `HD2Mimnt.Application.Services.GmodIasConversionService.ConvertAsync` — 应用层编排与落盘。
- `HD2Mimnt.ModelConversion.Gmod.GmodIasConverter.Convert / ConvertAsync` — 纯转换，返回内存中的全部产物。

## 产物布局

| 路径 | 内容 |
| --- | --- |
| `body/`、`helmet/`（拆分模式） | 身体 / 头盔各自的完整 IAS 包（下表产物都在部位目录内） |
| `ias/{project32}.{archive}.rigbin` | HD2IRG1 rig；文件名必须是「32 位项目身份 + 16 位 Archive」，游戏与导入端按它认领骨架 |
| `ias/{manifest_id}.difference.ini` | IAS v6 free-parts 差分清单（`manifest_id` 形如 `hd2free_xxxxxxxxxxxxxxxx`） |
| `ias/{isolation_id}.isolation.ini` | 隔离清单（`isolation_id` 形如 `owned_` + 24 位十六进制）；文件名与文件内的 `isolation_id` 必须一致 |
| `<project>.hd2irpack.zip` | 上述 rigbin / 物理 / 差分 / 隔离的配套运行时包（见下一节） |
| `geometry/model.hd2geom` | 完整几何中间包（二进制，含全部备选/skin/骨骼/动骨参数/IAS 选项组合） |
| `part-report.json`、`part-manifest.json` | 部位统计与部位文件校验清单（部位目录里的 `manifest.json` 只属于 `package/` 模组包） |
| `materials/materials.json`、`materials/raw/**` | 材质描述、通道角色映射、原始 VMT（所有部位共享，只写一份） |
| `textures/**.png`、`textures/raw/**` | 解码贴图、原始 VTF |
| `physics/**.hd2phys`、`.hd2physpack.json`、`jiggle-parameters.json` | 动骨物理资源、清单与全部 jiggle 参数 |
| `package/manifest.json`、`package/hd2mimnt.json` | 标准 V1 模组清单与 HD2Mimnt 扩展；`Options[0].Include = ["data"]` |
| `package/data/9ba626afa44a3aa3.patch_0{,.gpu_resources,.stream}` | 游戏原生 Stingray 补丁：每个已发布 Unit 一份 `.unit`，加上它引用的 `.material` 与贴图。**三个伴随文件必须同时存在**，哪怕是 0 字节（AQSDK `PACK_SUFFIXES` 就是这么定义的，读取侧无条件打开三者） |
| `package/hd2mimnt-gmod.json` | 包内资源索引：Unit / 材质 / 贴图 ID、TOC 条目偏移与体积（便于排查） |
| `runnable.json` | 该部位的包摘要：GUID、Unit 数、材质/贴图数、补丁体积、每个 Unit 的选项与网格统计 |
| `source/**`、`source/index.json` | addon 全部源文件逐字节副本与路径索引 |
| `conversion-report.json`、`manifest.json`、`split.json` | 保真统计/提示、全包校验清单、拆分部位索引 |

## 独立骨架运行时配套包（`.hd2irpack.zip`）

`package/` 只是 `data/` 补丁（模型、材质、贴图），骨架与物理不在里面。IAS 护甲要真正跑起来，
还需要一套 `bin/HD2IndependentRig/` 运行时文件；社区把这套文件打成 `xxx.hd2irpack.zip`
（根目录带 `mod_group.json`），与 data 模组一起导入。转换器一次产出两份，互相配套：

```
sample.hd2irpack.zip
├─ mod_group.json                       # HD2RuntimeGroup1：schema/id/name/members[{project, archive, files[{path, sha256}]}]
└─ HD2IndependentRig/
   ├─ {project32}.{archive}.rigbin
   ├─ {project32}.{archive}.{slot}.{unit}.hd2phys   # 每个已发布 Unit 一份
   ├─ hd2free_xxxxxxxxxxxxxxxx.difference.ini
   └─ owned_xxxxxxxxxxxxxxxxxxxxxxxx.isolation.ini
```

命名不是随便定的，导入端会按下列规则**整包拒收**不合约的配套（参见 HD2SDK-AQ-Edition
`hd2_system/runtime_group.py` 与 `resource_isolation.py`）：

| 规则 | 依据 |
| --- | --- |
| 条目只能位于 `HD2IndependentRig/` 下，叶名须匹配 `[A-Za-z0-9_-][A-Za-z0-9_.-]*.(rigbin\|hd2phys\|hd2shared\|hd2pose\|hd2preview\|ini\|hd2author)` | `runtime_group.LEAF` |
| 每个 `.rigbin` 必须叫 `{32 位十六进制项目}.{16 位十六进制 Archive}.rigbin`，且一个成员包里只能出现同一组 (project, archive) | `runtime_group.IDENTITY` / `read_member` |
| `isolation_id = "owned_" + blake2s("{project}\0{archive}", 12)`，文件名与之一致 | `resource_isolation.binding_for_plan` |
| `manifest_id = "hd2free_" + blake2s("{project}\0{archive}", 16, person="HD2DfV4")[:16]`，文件名与之一致 | `free_runtime_manifest` / `runtime_manifest._stable_label` |
| `mod_group.json` 的 `id` 是 32 位十六进制组身份；成员按 (project, archive) 排序，`files[].sha256` 是条目内容的 SHA-256 | `runtime_group.write_group` |
| `mod_group.json` 必须带 `exported_at`（ISO 时间）。缺这个字段导入端报「Mod 组身份、名称或时间无效」并整包拒收——社区样本里它是 `2026-09-17T19:39:21.285664+08:00` 这种形态 | 实测：管理器的 `mod_library.json` 把缺字段的包记成 `err=Mod 组身份、名称或时间无效` |

因此项目名会先被折算成 32 位十六进制身份（`IasIdentity.Project`，已经是 32 位十六进制时原样保留），
隔离/差分 ID 再按上表派生——同一个项目重复转换得到同一套 ID，导入即更新而不是叠出第二份。
BLAKE2s 由 `Ias/Blake2s.cs` 实现，并有固定向量把算法钉死（含社区样本 `owned_9d2637de639edfa9c55b7045` 的原值）。

**故意不产出**的两类可选配套：

- `.hd2author`：由官方 `HD2AuthorSeal.exe` 加密的作者声明；管理器自身也把它当作作者元数据，
  部署时跳过、不写入游戏目录，因此转换器不伪造这个密文。
- `.hd2preview`：给预览用的骨骼见证数据（`HD2PRV1` 容器，引用 `shared_*.consumers.rigbin`）；
  只影响预览，不影响骨架/物理/开关加载。
- `shared_*` 共享骨架（`hd2shared` + `canonical/consumers.rigbin` + `requires-shared-v2.rigbin`）：
  多个护甲共用一套骨架时才需要；本转换每个护甲自带独立 rig。

## 开关发布策略

IAS free-parts 要求每个 part 占用不同 slot，且不同 part 的首个选项不能争用同一 Native Unit 路由。
GMOD 模型的多个 bodygroup 通常共用同一套模型资源，因此：

- `auto`（默认）：只有一个开关时发布为独立 part；多个开关自动合并为**一个组合 part**，选项为各轴的笛卡尔积。
- `composite`：始终使用组合开关。
- `independent`：多个开关各自独立，必须通过 `NativeUnitsByAxis` 显式分配互不重叠的 Native Unit 集合，
  否则直接报错而不是发布互相覆盖的路由。

组合选项数上限 256；发布的 Unit 数上限 512（HD2IRG1 profile 上限），native 路由上限沿用 IAS 的 32 槽限制。

选择目标护甲后，每个选项只接管该护甲的**载体 Unit**，同一护甲里没被接管的 Unit 全部路由到共享的零面积占位；
所以 Unit 总数 = 选项数 + （护甲 Unit 数 − 1），不会随选项数乘上整套护甲。若只选了身体甲却要求拆分头盔，
头盔侧没有可接管的目标：转换器跳过该部位并给出 `TargetArmorRequiredForPart` 警告，
而不是退化成「每个选项接管整套 23 个 Unit」（那样会撞上 512 上限、整次转换失败）。

## 保真边界（重要）

| 项目 | 处理 |
| --- | --- |
| Source shader / proxy 运行时行为、flex、动画、Lua | **不转换**；原始字节保留，报告中注明 |
| VTF 无已验证解码器的布局、cube/volume 布局 | **不猜测**；原始 VTF 字节逐字节保留 |
| `.phy` IVP 碰撞体与 ragdoll 约束 | **不编译**成 HD2PHY1 collider；原始字节保留并注明 solid 数 |
| 非 jiggle 的 procedural bone、无法构成 2 节点链的孤立物理骨 | 保留于几何包与 `jiggle-parameters.json`，报告标注 |
| 游戏原生 Stingray `.unit` / `.material` / 贴图 | **已生成**（写入 `package/` 内的补丁） |
| Source 多贴图通道 → HD2 打包通道（金属度/粗糙度/AO） | 只做确定性通道搬运；缺失通道用中性值（非金属、偏粗糙、AO=1） |
| 游戏内运行验收 | **未做**：结构自检通过，但未在真实游戏中加载验证 |
| 运行时配套（`rigbin`/`hd2phys`/`difference`/`isolation`/`mod_group.json`） | **已生成**，命名与 `mod_group.json` 结构按官方 SDK 规则对齐 |
| 可选配套 `.hd2author` / `.hd2preview` / `shared_*` 共享骨架 | **不产出**（原因见上一节）；缺少它们不影响骨架、物理与开关加载 |

`.unit` 由 `Hd2UnitBuilder` 按 Unit 版本 10800438 写出（TransformInfo / BoneInfo 调色板 / StreamInfo /
MeshInfo / 材质表），写完后用 `Hd2UnitInspector` 复刻读取侧硬约束逐项复核，失败即抛异常、不落半成品；
顶点布局固定为 RGBA8 颜色 + float3 位置 + 八面体 10:10 法线 + float2 UV + half4 权重 + u8×4 骨骼（40 字节）。
材质用 `basic` / `alphaclip` 两个实测模板（父材质、槽语义 `mra|albedo|normal` /
`nar|opacity_clip_map|base_color_metal_map`、13/11 个标量参数），只有贴图资源 ID 可变；
贴图按 BC1（无 alpha）/BC3（颜色图 A 通道承载金属度）编码，mip 链写进 `.gpu_resources`。
Rig profile 的 `rest_local` 与 Unit 的 TransformInfo 共用同一份 Source rest 局部变换实现；needle 按
AQSDK 的 scene-node 约定使用 16 字节零值与 `needle_to_palette=0`。

**体积**：每个开关选项都独占一份 Unit 载荷，所以选项数直接决定补丁大小。255 骨骼/19 组开关的玩家模型
在默认组合发布（256 选项）下 body 包约 430 MiB、helmet 包约 120 MiB；超过 256 MiB 时转换报告会给出
`PackageSizeLarge`，用 `--switches` / 界面「发布的开关」收窄选项即可。

这份体积无法靠共享载荷消掉：Unit 的 StreamInfo 里 `VertexOffset` / `IndexOffset` 是**相对本资源
GPU 块**的偏移（读取侧一律按 `条目GpuOffset + StreamOffset` 定位），所以两个 Unit 不能指向同一段顶点
数据——不同 bodygroup 组合本来就选出不同网格，几何也确实不同。选项数少一半，包体就少一半。

## 格式与来源

- `HD2PHY1`：`HD2-PhysBoneTool/physbone/core/format_v1.py::build` 的冻结线格式（80 字节头，
  chain/joint/collider/link 记录，capability 位与可选 joint collision filter 尾表）。
  测试用该参考实现生成 `hd2phys-basic-golden.bin` 做逐字节比对。
- `HD2IRG1` / IAS free-parts v6 / isolation：沿用 `HD2Mimnt.ModelConversion/Ias/` 既有编解码器，
  未改动既有类。
- `HD2GEOM1`：本模块新增的自描述中间格式（36 字节头 = 8 字节 magic + 7 个 uint32；长度前缀记录），
  带 CRC32 与严格读取器，便于后续演进。
- Stingray 补丁：72 字节头 + 32 字节类型表 + 80 字节条目表 + 主数据；类型 ID 为 Unit
  `0xE0A48D0BE9A7453F`、Material `0xEAC0B497876ADEDF`、Texture `0xCD4238C6A0C69E32`。GPU 与 stream
  载荷按 64 字节对齐写进伴随文件；同一缓冲区被多个资源复用时条目共享偏移（同几何多选项不再重复存储）。
  空 `.stream` / `.gpu_resources` 不写文件（0 字节占位会让游戏报错崩溃）。
- 槽语义与名称哈希：`murmur64_hash` 取高 32 位（AQSDK `stingray/hash.py`）。`mra` / `albedo` / `normal` /
  `opacity_clip_map` / `base_color_metal_map` 的哈希已与模板二进制逐一比对；资源 ID 用 FNV-1a 64 并掺入
  Archive ID，保证 body / helmet 两包不共用材质与贴图 ID。
- Source MDL/VVD/VTX/VMT/VTF/GMA/PHY 解析沿用 `HD2Mimnt.ModelConversion/Source/`；
  GMA 格式参考 [Facepunch/gmad](https://github.com/Facepunch/gmad)。

## 验证

- 全解决方案构建 0 错误；`HD2Mimnt.ModelConversion.Tests` 全量通过（含 HD2PHY1 golden 逐字节、
  几何包往返、端到端转换、身体/头盔拆分、v49 解析、动骨链路、VTF 解码）。
- CLI 注册/参数校验/真实落盘回归通过；Headless UI 回归覆盖新工具页渲染、三语切换与经服务真实转换。
- 真实 GMA（278 MB）验收：`gmod-ias list` 列出 8 个可转换模型；v48 玩家模型（255 骨/34 网格/186 动骨）
  拆分产出 `body/`（256 选项）与 `helmet/`（96 选项）两包；v49 模型 `models/shoubi/aes_yz_sb.mdl`
  转换成功（45 骨/6 网格/33695 三角面/2 动骨）。
- 运行层新增 11 例：补丁 TOC 逐字段、每个 Unit 通过读取侧自检、槽语义哈希、alpha 判定、通道合成、
  空选项占位 Unit、载荷共享、标准清单/扩展、`SampleFact` 真实模型端到端（IAS 路由里的每个
  Published Unit 都在补丁里且可被读取器解析）。
- 真实 GMA 端到端（`models/shoubi/aes_yz_sb.mdl`，45 骨/6 网格/33695 三角面）：24 个选项 →
  24 份 Unit + 3 材质 + 7 贴图（2048×2048 BC1/BC3，12 级 mip），主数据 265 KiB + GPU 34.7 MiB，
  交叉引用（Unit 材质表 → 材质 → 贴图）全部可解析。
- 未在真实游戏内验收：本模块只写转换目录，不写游戏目录；能否在游戏中正确显示取决于目标护甲骨架
  与 IAS rig 的配合，需真机验证。

### 落盘产物的独立复查

转换器自检之外，另用一份**不依赖写入代码假设**的独立读取器（按 TOC / Unit / Material / Texture
的字段定义重新解析磁盘上的文件）复核了拆分模式的真实产物 `models/lc_84/aes_yz_pm.mdl`
（一次性脚本；耐久回归由 `GmodStingrayPackageTests` + `Hd2UnitInspector` 承担）：

| 部位 | 补丁 | GPU | Unit | 材质 | 贴图 | 网格 | 结果 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `body/` | 11.3 MiB | 402 MiB | 256 | 6 | 13 | 2176 | 全通过 |
| `helmet/` | 2.0 MiB | 114 MiB | 48 | 10 | 17 | 448 | 全通过 |

检查项：TOC magic/类型表计数与条目表一致、条目对齐值 16/64、每个资源的 `MainOffset+Size` 与
`GpuOffset+Size` 都在文件内、主数据区间互不覆盖、Unit 版本 10800438 且 `EndingOffset+8 == 资源长度`、
每条 StreamInfo 的 stride=40 与顶点/索引字节数自洽、材质表 → 材质资源 → 贴图资源三段交叉引用全部可解析、
`difference.ini` 里 256 / 48 个 Published Unit 全部能在补丁里找到、`manifest.json` 的
`Options[0].Include == ["data"]`。两个部位的资源 ID 完全不相交，可同时装进同一份
`9ba626afa44a3aa3.patch_0`（模组管理器合并补丁时不会互相覆盖）。

### 运行时配套包对照官方 SDK 的复查

用 HD2SDK-AQ-Edition 里的 `hd2_system/runtime_group.py` 现成规则逐条比对转换产物
`sample.hd2irpack.zip`（真实样本 `models/shoubi/aes_yz_sb.mdl` + `TR-62 Knight`，28 个条目）：

- 全部条目都在 `HD2IndependentRig/` 下（外加根级 `mod_group.json`），每个叶名通过 `LEAF` 正则。
- `IDENTITY` 提取出的 (project, archive) 恰好一组：`(f8713b2c3b883d769dd7c8701b5eda63, 08241ba732c97a9d)`。
- `mod_group.json` 键集合为 `id/members/name/schema`，成员 `project` 为 32 位十六进制、`files[].sha256`
  与条目内容逐一相符。
- `isolation_id` / `manifest_id` 与文件名一致；同一 (项目, Archive) 重算 BLAKE2s 得到社区样本
  `owned_9d2637de639edfa9c55b7045` 的原值（见 `IasIdentityTests`）。

### 用管理器的模型浏览器验收

跑完转换后直接调用管理器的预览读取器（`PatchResourceInspectionService.PreviewModelAsync`，
与界面模型浏览器同一条 TOC → Unit → 材质 → 贴图 路径）读取落盘的 `package/data`：

| 产物 | 预览结果 |
| --- | --- |
| `aes_yz_sb.mdl` + TR-62 Knight（46 Unit，35 MiB） | 86 个网格 / 220,662 顶点 / 393,706 三角面 / 7 张贴图 / `Error = null` / 跳过 0 个流；首个网格带骨骼蒙皮、材质与 3 张贴图 |
| `aes_yz_pm.mdl` + TR-62 Knight（拆分，body 278 Unit，413 MiB） | 180 个网格 / 575,537 顶点 / 999,560 三角面 / 13 张贴图 / `Error = null`；预览按 300 万索引预算截停 2018 个流，属读取侧的容量保护而非资源损坏 |
