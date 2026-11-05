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

## 3. 复现命令

在 `tools/SourceModelReader` 下执行：

```
py -3 research/probe_hd2_vertex_position_space.py
py -3 -m unittest discover -s tests -p "test_hd2_vertex_position_space.py" -v
py -3 -m unittest discover -s tests -p "test_hd2_mesh_transform_audit.py" -v
```

`--sample-limit` 可限制最近距离检验的采样顶点数，默认使用全部顶点；`--output-json` / `--output-markdown` 可改写输出路径。工具只读，不修改任何顶点。

## 4. 下一个最小实验

1. 现有渲染 LOD 的单位矩阵使 P0 无法从渲染网格判定，需要新增样本：**同一 Unit 内至少两个 LOD、且直接绑定 MeshInfo 的矩阵非单位**（或带非单位缩放的网格）。可用 `run_hd2_skinning_batch.py` 的清单方式做小批量定向抽取，不做全游戏扫描。
2. 用单影响顶点做静止不变量检查：`p × palette_i × bone_world_i == p × mesh_world`，把候选 palette 公式与逐顶点数据直接连起来，而不是只比较矩阵。
3. 用独立参考校验：同一 Unit 的 SDK/Bones 参考输出或 Blender 侧导入结果，验证 `p × mesh_world` 的放置方向；本轮结论仍是 Candidate。

## 5. 外部参考

- HD2SDK-AQ-Edition（Unit / Bones 结构参考，含固定提交）：<https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/unit.py>、<https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/bones.py>、<https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/tree/4df125a5e6a34d03532a06b545f700ec5353a98f>
- HD2 Modding Wiki（材质与纹理语义）：<https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html>
- Autodesk Stingray 渲染/材质参考：<https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/stingray_help/lighting_rendering/shading_and_materials/parent_child_materials.html>

SDK 字典与 ID 表按版本作用域使用，不作为通用真值；本仓库对第三方 SDK 的许可证与来源约束见 [README](../../README.md) 与 `docs/agent-guides/platforms-and-downloads.md`。
