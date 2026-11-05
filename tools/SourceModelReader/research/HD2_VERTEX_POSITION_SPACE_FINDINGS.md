# HD2 顶点位置空间研究（Phase 1 第一轮）

- 分支 / HEAD：`feature/source-model-reader-0.8.2`，起始 `0725e3f`，本轮提交见 git log
- 工具：[research/probe_hd2_vertex_position_space.py](probe_hd2_vertex_position_space.py)
- 输出：[skeleton_comparison/hd2_vertex_position_space.md](skeleton_comparison/hd2_vertex_position_space.md)（JSON 同目录，按 `.gitignore` 只保留本地）
- 样本：`research/skinning_audits/fs05_lower_body.json`、`fs05_torso_left_arm.json`、`avatar_composite.json`
- 真值标签沿用交接包定义：Observed / Validated / Candidate / Hypothesis / Unknown

## 1. 问题与判别思路

Phase 1 的 P0 问题是：存储的顶点位置处于哪个坐标空间，以及哪个矩阵真正作用于它。

只比较矩阵无法回答这个问题：三个样本里所有常规渲染 LOD 的 TransformInfo 存储矩阵都是单位矩阵（Observed），因此"施加"与"不施加"网格世界矩阵在渲染 LOD 上完全不可区分。需要找一个存储矩阵非单位的网格作为判别样本。两个 FS-05 Unit 各有一个这样的网格：`hips_culling`（LOD -1，无蒙皮属性）和 `l_arm_culling`（LOD 4，有蒙皮）。

为此设计两个相互独立的检验：

1. **最近顶点距离（控制组 + 表面检验）**：同一 Unit 内两个单位矩阵渲染 LOD 之间的最近距离必须接近 0，先证明度量本身可用，再读其他结论。
2. **体积包含检验**：代理网格通常不是渲染表面的简化副本，逐点距离没有意义，改问"哪个空间下的包围盒真正包住该 Unit 的渲染几何"。

## 2. 结果

### 2.1 控制组通过（Validated）

| Unit | 对照网格 | 采样顶点 | 最近距离 mean | median | p95 | max |
|---|---|---:|---:|---:|---:|---:|
| FS-05 下半身 | shadow LOD3 vs LOD0 | 608 | 0.000485 | 0.000273 | 0.001677 | 0.004653 |
| FS-05 躯干/左臂 | shadow LOD3 vs LOD0 | 1426 | 0.000232 | 0.000076 | 0.000929 | 0.002669 |

同一 Unit 的渲染 LOD 存储位置处于同一空间，且该空间就是渲染空间（Observed）。

### 2.2 两个 culling 网格是同一份顶点缓冲（Observed）

| 项 | `hips_culling` | `l_arm_culling` |
|---|---|---|
| Unit | `210fb2f60f8308ea` | `324fb206612ce4f7` |
| Mesh ID | `2249734937` | `3648454405` |
| LOD 索引 | -1 | 4 |
| 蒙皮 | `no_skin_attributes` | `ok`（2616 条活跃影响） |
| 顶点数 | 439 | 439 |
| `normal0` / `uv0` 缓冲 | SHA-256 与另一 Unit 逐字节相同 | 同左 |
| `position0` 缓冲 | 逐字节不同 | 逐字节不同 |
| 位置逐元素最大绝对差 | `2.441e-17`（x 分量 -0.0 与极小负值之差） | 同左 |

两个不同 Unit、不同 MeshID、不同 LOD 索引、一个有蒙皮一个没有蒙皮的网格，实际共用同一份代理几何；位置差异只有 float32 零符号噪声。原始包围盒是以局部原点为中心、三轴半extent 均为 `0.5094` 的正立方体。

结论：**LOD 索引与命名都不能推断网格角色，代理资产会跨 Unit 复用**（Observed，直接支持 Phase 2 的分类要求）。

### 2.3 体积包含：候选 mesh-world 才是放置变换（Observed + Candidate）

`union-outside` = 渲染并集逃出候选体积的最大距离；为 0 表示完全包住。

| Unit | 代理网格 | 渲染并集 z 范围 | union-outside（原始） | 原始包住 | union-outside（施加 p×M 后） | 施加后包住 | box-slack 施加后 |
|---|---|---|---:|---|---:|---|---:|
| FS-05 下半身 | `hips_culling` | 0.56257 … 0.90217 | 0.39275 | False | 0.00000 | **True** | 0.58066 |
| FS-05 躯干/左臂 | `l_arm_culling` | 1.08176 … 1.56145 | 1.05203 | False | 0.00000 | **True** | 0.40463 |

原始空间里代理盒子贴在原点（z 半extent 只有 0.5094），包不住位于 z≈0.56–1.56 的渲染几何；按行向量约定施加该网格自己的 TransformInfo 矩阵后，两个 Unit 都恰好把渲染并集完整包住，且仍留有 0.40–0.58 的余量（代理体积大于装备本体，符合"包裹整个身体的遮挡/裁剪体积"形态）。

- **Observed**：两个 FS-05 Unit 都呈现同一模式，且控制组通过。
- **Candidate**：存储位置处于"该网格自己的 TransformInfo 节点定义的空间"。矩阵为单位时该空间与渲染空间重合（所以渲染 LOD 测不出差别）；矩阵非单位时必须用它放置几何。
- **Candidate（与既有 162 个 BoneInfo palette 匹配一致）**：palette 公式 `mesh_world × inverse(bone_world)` 已经把 `mesh_world` 包含在内。因此 `mesh_world` 只能生效一次——蒙皮路径经由 palette 隐式生效，无蒙皮路径直接生效。若在蒙皮之后再显式乘一次 `mesh_world`，静止姿态会得到 `p × mesh_world²`，破坏静止不变性。
- 仍为 **Unknown**：运行时着色器的实际乘法顺序；无蒙皮辅助几何走哪条管线；遮挡查询是否真的使用该体积；非均匀缩放 + 非单位旋转的组合。

### 2.4 附带发现：同一 Unit 内顶点属性布局并不统一（Observed）

| 网格 | bone_weight 布局 | bone_index 布局 | UV |
|---|---|---|---|
| FS-05 下半身渲染 LOD | `float32`（单通道） | 1 × `vec4_uint8` | 3 × `vec2_half` |
| FS-05 躯干/左臂渲染 LOD | `vec4_half` | 2 × `vec4_uint8` + 1 × `vec4_uint32` | 3 × `vec2_half` |
| `l_arm_culling` 代理 | `packed_10_10_10_2` | 2 × `vec4_uint8` + 1 × `vec4_uint32` | 1 × `vec2_half` |
| Avatar CompositeUnit | `vec4_half` | 1 × `vec4_uint8` | 1 × `vec2_half` + `rgba8` 颜色 |

影响传递（Phase 5）不能假设单一权重布局：同一 Unit 的不同网格就会用不同的量化格式，下半身还是单影响 `float32`。

### 2.5 逐顶点静止不变量：放置变换只生效一次（Validated，针对存储数据）

对每个可解析的影响通道计算 `(p × palette_i) × bone_world_i`，与 `p × mesh_world` 比较（`mesh_world` 为该网格自己 TransformInfo 的存储矩阵）。

| 范围 | 网格数 | 通道数 | 未解析通道 | 最大误差 | 平均误差 |
|---|---:|---:|---:|---:|---:|
| 三个样本全部蒙皮网格 | 10 | 211,956 | 0 | `3.305e-07` | ≤ `1.192e-07` |

| 网格 | 矩阵 | 顶点 | 通道 | 误差 vs `p × mesh_world` | 误差 vs 原始 `p` |
|---|---|---:|---:|---:|---:|
| `g_torso_arm_l_female`（LOD0） | identity | 3115 | 37,380 | `2.221e-07` | `2.221e-07` |
| `l_arm_culling`（LOD4） | non_identity | 439 | 5,268 | `2.167e-07` | `1.595e+00` |
| Avatar CompositeUnit | identity | 27355 | 109,420 | `3.197e-07` | `3.197e-07` |

判别点全在 `l_arm_culling`：它的静止两步乘积精确落在 `p × mesh_world`（`2.167e-07`，float32 噪声量级），而距离原始 `p` 差 `1.595`。若 `mesh_world` 被重复携带，乘积会落在 `p × mesh_world²`，误差不会是零；若完全没被携带，误差也不会是零。因此在这三个样本的存储数据上：

- **Validated**：palette 已经包含一次且仅一次 `mesh_world`；静止输出等于 `p × mesh_world`。
- **Candidate**：把它当作运行时着色器契约仍未经过原生验证（没有实际游戏/SDK 逐顶点参考）。
- 说明：单位矩阵网格的两种误差数值相同，再次说明它们不具备判别力；未解析通道为 0 表示所有通道值都落在有效 palette 槽位上（含重复槽位），因此该方法无法区分"填充通道"与"真实第二影响"。
- `packed_10_10_10_2` 权重目前未解码（`skinning` 只给出通道索引），所以本轮没有做按权重求和的 LBS 检验，只做了逐通道检验；权重求和误差对 Avatar 已知在 `0.999634 … 1.000366`。

### 2.6 加权静止混合与权重求和（Validated，针对存储数据）

把每条通道按权重合起来再除以权重和（归一化 LBS），与 `p × mesh_world` 比较；同时单独统计权重求和偏离 1 的程度。

| 权重格式 | 网格 | 归一化混合最大误差 | 权重和最大偏离 | 权重和平均偏离 |
|---|---|---:|---:|---:|
| `float32` 标量（下半身渲染 LOD，单影响） | 4 | ≤ `6.102e-08` | `0.000000` | `0.000000` |
| `vec4_half`（躯干渲染 LOD） | 4 | ≤ `3.305e-07` | `3.66e-4` | `6.0e-5` |
| `vec4_half`（Avatar CompositeUnit） | 1 | `0.000e+00`（抽样） | `0.000000`（抽样） | `0.000000`（抽样） |
| `packed_10_10_10_2` 解码（`l_arm_culling` 代理） | 1 | `2.040e-07` | `1.955e-3` | `8.62e-4` |

通道配对判别结果：

- `single_index_attr_0 / _1 / _2`（把权重向量配给某一个索引属性）三者数值**完全相同**，归一化混合误差都是 `~1e-7`。**静止姿态在原理上无法区分它们**：每条已解析通道都单独把顶点送到同一个放置点，所以任何配对的凸组合都落在同一点。
- `every_index_attr_shares_weights`（同一个权重向量重复用于 3 个索引属性）权重和偏离**恰好为 `2.000000`**（三倍计数），被否证。
- 结论：权重向量与索引属性的一对多关系不成立，但"哪一个索引属性拥有这 4 个权重"必须靠**带姿态的变换**或 SDK/编辑器参考才能定；本轮把它记为 Unknown，并留下 `--sample-limit` 可复现的表格。

对后续权重传递（Phase 5）的硬约束：`vec4_half` 与 10 位量化权重的和本身就有 `3.7e-4` / `2.0e-3` 量级偏差，因此转换时不能假设"和恰好为 1"，也不得静默重归一化——必须按存储格式报告偏差。

补充更正：`packed_10_10_10_2` 权重在 `hd2_unit.decode_packed_weights_1010102` 中**已经解码**（三个 10 位归一化权重 + 顶部 2 位作为非权重元数据，第四个影响返回 0）。本轮独立的求和检验（439/439 顶点落在 `[0.9980, 1.0000]`）与该解码方式一致，属于对外部约定的二次确认，而不是新解码实现。

### 2.7 三角形绕序与存储法向一致（Validated，相对一致性）

对每个 section 用右手叉乘 `(v1-v0) × (v2-v0)` 计算面法向，与该面三个顶点存储法向的平均值比较（全量顶点，58,787 个面）：

| 结果 | 数量 |
|---|---:|
| 参与统计的 section | 14 |
| 判为一致（≥90% 面点积为正） | 13 |
| 判为整体反向 | 0 |
| 判为混合 | 1（躯干 LOD2 section 0：428 一致 / 4 反向 / 44 退化） |

- **Observed**：存储的顶点顺序与存储法向在右手叉乘下互相一致，没有出现整体反向的网格；少量反向面集中在退化/细长三角形上，与"平均顶点法向在曲面上的插值"效应一致，不构成反向证据。
- **Observed**：两个代理网格各自 760/760 面全部一致，平均点积 `0.9988`、零退化面——它是一个封闭、干净的体积网格，这比名字更能支持"辅助包围体积"的角色判断（Phase 2）。
- **Unknown**：HD2 空间本身的绝对手性。相对一致性只能保证转换时不能把"顺序"和"法向"分别翻转；绝对手性需要一个外部参考（同一 Unit 在 Blender/SDK 侧的导入结果）才能定。

## 3. 复现命令

在 `tools/SourceModelReader` 下执行：

```
py -3 research/probe_hd2_vertex_position_space.py
py -3 -m unittest discover -s tests -p "test_hd2_vertex_position_space.py" -v
py -3 -m unittest discover -s tests -p "test_hd2_mesh_transform_audit.py" -v
py -3 research/dump_hd2_bones_resource.py "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 8670598c1f4462dc --unit 210fb2f60f8308ea --unit-json research\skinning_audits\fs05_lower_body.json --output-markdown research\skeleton_comparison\hd2_bones_structure_lower_body.md
py -3 -m unittest discover -s tests -p "test_hd2_resources.py" -v
```

`--sample-limit` 可限制最近距离检验的采样顶点数，默认使用全部顶点；`--output-json` / `--output-markdown` 可改写输出路径。工具只读，不修改任何顶点。

## 4. 下一个最小实验

1. 现有渲染 LOD 的单位矩阵使 P0 无法从渲染网格判定，需要新增样本：**同一 Unit 内至少两个 LOD、且直接绑定 MeshInfo 的矩阵非单位**（或带非单位缩放的网格）。可用 `run_hd2_skinning_batch.py` 的清单方式做小批量定向抽取，不做全游戏扫描。
2. **Bones 资源不是第二份变换数据（已否证）**：用 `research/dump_hd2_bones_resource.py` 直接读取下半身 Unit 的 Bones payload（`210fb2f60f8308ea`，与 Unit 同 FileID、不同 TypeID）：总共 1470 字节，结构被完全消费，名字块之后 **0 字节**。分区为 header(8) → 6 个 float32 → 82 个 hash → 6 个 uint32 → 1086 字节名字表。把该 Unit 全部 TransformInfo/BoneInfo 矩阵的 float32 位模式在整个 payload 里搜索，**一次命中都没有**，所以 Bones 资源只保存名称/哈希表和一组 LOD 数值，不保存绑定矩阵，不能用作独立于本解析器的变换来源。顺带取回原本被丢弃的数据：那 6 个 float 是 `0.25, 0.15, 0.07, 0.04, 0.02, 0.01`（严格递减，候选 LOD 切换阈值），6 个 uint32 全是 82（等于名字数，候选"每个 LOD 使用的骨骼数"）。`parse_bones_resource` 现在把该浮点数组导出为 `lod_float_values`，语义仍标为候选。
3. 因此 Phase 1 要求的"独立参考校验"必须来自本仓库之外：SDK 侧的 Unit/Bones 参考导出（AQ Edition / Community SDK，按固定提交作用域）或原生游戏内观测；在此之前，`p × mesh_world` 的空间解释保持 **Candidate**（§2.5 的逐通道结论属于同一份存储数据的内部一致性，虽然已是顶点级、21 万通道的强度）。
4. 用独立参考校验同一批顶点的放置方向：优先把 `l_arm_culling` 的包围盒与 SDK/编辑器侧导出的同名代理体积对齐。

## 5. 外部参考

- HD2SDK-AQ-Edition（Unit / Bones 结构参考，含固定提交）：<https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/unit.py>、<https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/bones.py>、<https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/tree/4df125a5e6a34d03532a06b545f700ec5353a98f>
- HD2 Modding Wiki（材质与纹理语义）：<https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html>
- Autodesk Stingray 渲染/材质参考：<https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/stingray_help/lighting_rendering/shading_and_materials/parent_child_materials.html>

SDK 字典与 ID 表按版本作用域使用，不作为通用真值；本仓库对第三方 SDK 的许可证与来源约束见 [README](../../README.md) 与 `docs/agent-guides/platforms-and-downloads.md`。
