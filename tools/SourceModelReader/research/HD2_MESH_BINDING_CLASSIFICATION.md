# HD2 网格绑定分类（Phase 2 第一轮）

- 工具：[research/classify_hd2_mesh_bindings.py](classify_hd2_mesh_bindings.py)（只读消费 Unit JSON，`--unit LABEL=PATH` 可重复）
- 可复现报告：[skeleton_comparison/hd2_mesh_binding_classification.md](skeleton_comparison/hd2_mesh_binding_classification.md)（同目录 JSON 按 `.gitignore` 只留本地）
- 合成测试：[../tests/test_hd2_mesh_binding_classification.py](../tests/test_hd2_mesh_binding_classification.py)（12 项）
- 分类器不修改共享解析器：解析、矩阵约定与原始值保持原样。

## 1. 决策输入（只用存储结构，绝不用名字）

每个直接 MeshInfo 记录只凭以下字段定级：`lod_index`、`skinning_status`、`vertex_count`、
`resolved/unresolved_active_influence_count`、该网格自身 TransformInfo 存储矩阵的
`identity/non_identity/unavailable` 判定、以及 `hierarchy_validation_status`。

`transform_name` 与 mesh 名一并输出，但只作对照参考（字段名带
`_reference_only`），不参与任何分支——这是针对交接包「`hips_culling` 无蒙皮而
`l_arm_culling` 有蒙皮」那条陷阱：同一名字的两种结构必须能落到不同等级，而不同名字的
同一结构必须落到同一等级（测试 `test_names_are_reference_only_and_never_decide_class`）。

## 2. 等级定义与兜底

| 等级 | 触发条件 | 本轮数值样本 |
|---|---|---|
| `skinned_render_lod` | 几何已解码 + 有蒙皮属性 + 活跃通道 >0 + 自身矩阵为单位 + 网链校验一致 | 25 / 40 |
| `skinned_non_identity_placement` | 活跃通道 >0 且自身矩阵非单位（放置经该矩阵生效） | 5 / 40 |
| `auxiliary_unskinned` | 声明无蒙皮属性且 `lod_index == -1` | 10 / 40 |
| `unskinned_at_non_negative_lod` | 无蒙皮属性但 LOD ≥ 0 | 0（仅合成覆盖） |
| `skinned_hierarchy_unverified` | 有蒙皮但网链校验不为 `matches` | 0（仅合成覆盖） |
| `undecidable_placement` | 自身矩阵存在但无法解析（无法判单位性） | 0（仅合成覆盖） |
| `skinned_without_lanes` | 有蒙皮属性但活跃通道为 0 | 0（仅合成覆盖） |
| `undecoded_geometry` | 该绑定没有对应的已解码几何行 | 0（仅合成覆盖） |
| `unknown` | 未识别的蒙皮状态或缺失 LOD，**不**用名字猜 | 0（合成覆盖两种可达路径） |

本轮 8 个 Unit / 40 条绑定未触发兜底等级；`unknown` 可达性由合成样本保证（早前版本
`skinning` 分支写在 lanes 分支之后，兜底不可达——该缺陷已被测试抓到并修复）。

## 3. 本轮分类得到的结构事实（Observed）

- 40 条绑定里 25 条属于 `skinned_render_lod`，且这些网格的自身矩阵**全部为单位矩阵**：
  这正是位置空间无法从渲染 LOD 判别的原因（见
  [HD2_VERTEX_POSITION_SPACE_FINDINGS.md](HD2_VERTEX_POSITION_SPACE_FINDINGS.md) 第 2 节）。
- 5 条 `skinned_non_identity_placement`：肩甲 Unit `90b57e547e7ff7bb` 的 4 个渲染 LOD
  （共享非单位矩阵）与躯干样本 `l_arm_culling`。**有蒙皮并不等于矩阵为单位**，
  因此放置位置结论必须按网格级字段判断，不能按「渲染 LOD」这一角色假设。
- 10 条 `auxiliary_unskinned` 全部是 `lod_index == -1` 且无蒙皮属性，但自身矩阵**全部非单位**；
  其中两类形态明显不同：439 顶点 / 760 面的身体轮廓型（`hips_culling`、`r_leg_culling`、
  `l_arm_culling` 缓冲共用）与 24 顶点的简单盒型（`torso_culling`、`helmet_culling`）。
  本轮只按结构区分，**不**据此宣称遮挡/剔除用途。
- 名字为 `*_shadow` 与 `*_LOD1/2` 的绑定在结构等级上仍相同（结构一致），单靠结构量不足；
  分离性质在第二维度里找到，见第 4 节。

## 4. 第二维度：材质引用族（这轮找到了可分离字段）

结构等级本身不足以区分阴影几何与常规 LOD，于是量了第二维度：每个绑定 section 的
`material_slot_id`。在 8 个 Unit / 40 条绑定 / 10 个不同引用上：

| 引用 | 单位数 | 绑定 | 是否出现在 LOD 0 | 引用族等级 | 出现的 LOD | 蒙皮状态 |
|---:|---:|---:|---|---|---|---|
| `155175220` | 7 | 11 | 否 | `no_base_lod_negative_lod` | -1, 4 | 无蒙皮属性 + 有蒙皮 |
| `1772995352` | 7 | 7 | 否 | `no_base_lod_upper_lod` | 3（仅此） | ok + partial |
| `1913034217` / `3910610061` | 3 | 9 | 是 | `base_lod_reference` | 0, 1, 2 | ok + partial |
| `793921433` / `2013274869` | 2 | 6 | 是 | `base_lod_reference` | 0, 1, 2 | ok |
| `1256504776` / `1242939700` / `3140659872` / `2780682112` | 1 | 各 3/3/3/1 | 是 | `base_lod_reference` | 0, 1, 2（或仅 0） | ok |

按引用族重分绑定：**22 条** `base_lod_bound`（19 常规渲染 LOD + 3 非单位放置）、
**7 条** `family_no_base_lod_upper_lod`（全部恰在 LOD 3）、
**11 条** `family_no_base_lod_negative_lod`（10 条无蒙皮属性 + 有蒙皮非单位放置的 `l_arm_culling`），
`mixed_references` / `no_section_references` 本轮为 0（由合成样本可达）。

- **关键反证（已推翻本轮最初的设想）**：「跨 Unit 被多个装备复用」不是分离性质——男女孪生装备共用同一衣物引用（样本中 3 个 Unit 共用 `1913034217`），因此若按复用分族，衣物与阴影会被并成一族。真正的分离性质是**该引用是否也出现在 LOD 0**：每件装备都在自己的 LOD 0 上拥有其引用，而阴影与剔除引用从不触碰 LOD 0。该反证由合成样本 `test_reuse_across_units_alone_is_not_a_family` 固定为回归。
- **Observed**：阴影族与剔除族各自只有一个引用 id，且各自在 7 个 Unit 上稳定复用；剔除族是唯一同时横跨「有蒙皮 / 无蒙皮」与「LOD −1 / LOD 4」的引用。
- 只到「引用角色」为止：**阴影/剔除的运行时用途仍是 Unknown**，命名族等级（`family_*`）不是角色声明；名字列仅作对照，不进入任何分支。

## 5. 仍未定案（Unknown）与下一个最小实验

1. **LOD 3 阴影族与 LOD −1 剔除族的运行时语义**：现在有了稳定的存储判别量（引用是否触碰
   LOD 0），但仍需原生侧（SDK 导出或游戏内观察）确认遮挡/剔除/阴影用途。
2. **第三个 bone_index 组件集语义**：见位置空间结论文档第 2.11 节，已否证三条读法，仍待定案；
   它直接阻塞权重传递。
3. **覆盖范围**：本轮覆盖包 `8670598c1f4462dc` 已解码的 8 个 Unit（该包共 22 个 Unit）加
   Avatar CompositeUnit。要继续 firm 等级表需继续解码剩余 Unit（每 5 个约 1 分钟，JSON 只留
   本地），这是覆盖度问题，不是新语义问题。
4. **跨包抽样**：不同包的材质槽 id 可能不同，族等级是当前批次内的角色，已在解读限制中声明。

## 6. 复现

```
py -3 research/classify_hd2_mesh_bindings.py --unit fs05_lower_body=research/skinning_audits/fs05_lower_body.json --unit fs05_torso_left_arm=research/skinning_audits/fs05_torso_left_arm.json --unit avatar_composite=research/skinning_audits/avatar_composite.json --unit fs05_shoulder_armor=research/skeleton_comparison/unit_90b57e547e7ff7bb.json --unit fs05_undergarment_torso_f=research/skeleton_comparison/unit_be65eae060a0267c.json --unit fs05_undergarment_torso_m=research/skeleton_comparison/unit_f2715c19b24ecd8b.json --unit fs05_undergarment_leg_r=research/skeleton_comparison/unit_7e5111d05840be1a.json --unit fs05_undergarment_leg_l=research/skeleton_comparison/unit_901fc9c35857dad2.json --output-markdown research/skeleton_comparison/hd2_mesh_binding_classification.md
py -3 -m unittest discover -s tests -p "test_hd2_mesh_binding_classification.py" -v
```

解码剩余 Unit 的定向方式（不解全游戏）：

```
py -3 research/survey_hd2_unit_mesh_matrices.py "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 8670598c1f4462dc --limit 1 --output-markdown research\skeleton_comparison\survey_8670598c.md
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 8670598c1f4462dc --unit <ID> --json research\skeleton_comparison\unit_<ID>.json
```
