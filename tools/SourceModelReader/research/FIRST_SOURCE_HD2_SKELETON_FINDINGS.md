# First Source MDL/VVD to HD2 Avatar Skeleton Comparison

Date: 2026-11-05

Status: first real sample captured; exact-name baseline works, but no semantic retarget mapping is approved.

## Inputs

### Source model

- Container: `gmpublisher.gma`
- Model: `models/lc_84/aes_yz_pm.mdl`
- MDL / VVD / VTX: 48 / 4 / 7
- Checksum: `43233072`
- Compiled MDL bones: 255
- VVD stored vertex pool: 94,858
- Positive-weight source bones: 242

### HD2 target

- Package: `18235e0c9ec0e636`
- Unit: `4d1c334d294dfa97`
- Geometry source: `CompositeUnit`
- Unit decode status: `ok`
- Decoded meshes: 1
- Decoded vertices: 27,355
- TransformInfo entries: 107
- Named transforms: 90
- Active weighted target transforms: 61

This is the previously selected Avatar Unit baseline. It is not established that this exact Unit is the intended armor replacement target for the GMod sample.

## Results

| Metric | Result |
|---|---:|
| Exact name matches | 0 |
| Active Source exact name matches | 0 |
| Active Source bones without exact target name | 242 |
| Source declared bones without exact target name | 255 |
| Target-only named transforms | 90 |
| Target names unresolved from the current dictionary | 17 |
| Parent mismatches among exact matches | 0 |
| Parent unresolved among exact matches | 0 |

The zero parent mismatch count is vacuous: there are no exact-name matches on which to compare parents. It does not show that the two hierarchies are equivalent.

## Interpretation

The Source model uses a Source/ValveBiped-style naming scheme, including names such as:

- `ValveBiped.Bip01_Pelvis`
- `ValveBiped.Bip01_Spine`, `Spine1`, `Spine2`
- `ValveBiped.Bip01_Neck1`, `Head1`
- `ValveBiped.Bip01_L_Thigh`, `L_Calf`, `L_Foot`
- `ValveBiped.Bip01_L_Clavicle`, `L_UpperArm`, `L_Forearm`, `L_Hand`

The HD2 Avatar target uses a different convention, including `hips`, `spine1`, `spine2`, `chest`, `neck`, `head`, `l_thigh`, `l_knee`, `l_foot`, `l_clavicle`, `l_shoulder`, `l_elbow`, and `l_hand`.

Therefore, zero exact-name matches are expected from the strict baseline and should not be interpreted as zero anatomical correspondences. A separate candidate-alias pass is warranted. It must remain advisory until hierarchy, rest pose, and vertex deformation evidence have been checked.

The 13 declared Source bones with no positive-weight lane in the stored VVD pool are:
- `Qun_0_0_VRD`
- `Qun_0_0_VRD2`
- `Qun_0_1_VRD` through `Qun_0_5_VRD`
- `Qun_0_15_VRD` through `Qun_0_19_VRD`
- `ValveBiped.Bip01_Spine4`

This supports treating the `*_VRD` nodes as currently unweighted helper candidates in this vertex-pool scan, not as active skinning bones. It does not prove they are unused by every animation, jiggle/physics system, or alternate model configuration.

## Candidate mapping hypotheses (not approved mappings)

| Source candidate | HD2 candidate | Confidence / evidence required |
|---|---|---|
| `ValveBiped.Bip01_Pelvis` | `hips` | Verify root-relative hierarchy and rest-pose position |
| `ValveBiped.Bip01_Spine` | `spine1` | Verify parent chain and axial position |
| `ValveBiped.Bip01_Spine1` | `spine2` | Verify parent chain and axial position |
| `ValveBiped.Bip01_Spine2` | `chest` | Verify parent chain and axial position |
| `ValveBiped.Bip01_Neck1` | `neck` | Verify parent chain and rest pose |
| `ValveBiped.Bip01_Head1` | `head` | Verify parent chain and rest pose |
| `ValveBiped.Bip01_L_Thigh` / `R_Thigh` | `l_thigh` / `r_thigh` | Verify side, joint position, and parent |
| `ValveBiped.Bip01_L_Calf` / `R_Calf` | `l_knee` / `r_knee` | Naming differs semantically; verify joint center and chain |
| `ValveBiped.Bip01_L_Foot` / `R_Foot` | `l_foot` / `r_foot` | Verify side and joint position |
| `ValveBiped.Bip01_L_Toe0` / `R_Toe0` | `l_toe` / `r_toe` | Verify side and joint position |
| `ValveBiped.Bip01_L_Clavicle` / `R_Clavicle` | `l_clavicle` / `r_clavicle` | Verify parent and shoulder girdle |
| `ValveBiped.Bip01_L_UpperArm` / `R_UpperArm` | `l_shoulder` / `r_shoulder` | Verify joint location; target has twist/armor branches |
| `ValveBiped.Bip01_L_Forearm` / `R_Forearm` | `l_elbow` / `r_elbow` | Verify joint location and twist branches |
| `ValveBiped.Bip01_L_Hand` / `R_Hand` | `l_hand` / `r_hand` | Verify finger subtree and helper bones |

These are naming/anatomy hypotheses only. In particular, do not directly map all hair, ribbon, tail, ear, or `Qun_*` chains onto HD2 cape bones. Those are separate deformation systems and may need mesh/weight adaptation or may be unrepresentable on the target rig.

## New structural warning from the advisory alias design

The first proposed humanoid alias table is intentionally not treated as a retarget map. In particular, the inspected HD2 Avatar hierarchy has `hips` parented to `boss`, while `spine1` is also parented to `boss`; `spine2` is parented to `spine1`, and `chest` to `spine2`. The conventional Source ValveBiped chain is expected to run pelvis -> spine -> spine1 -> spine2. Therefore, the candidate `ValveBiped.Bip01_Spine -> spine1` is expected to expose a parent mismatch if the Source parent is `ValveBiped.Bip01_Pelvis`.

This is precisely why candidate aliases must be checked against hierarchy rather than accepted from anatomical labels alone. It may still be possible to retarget these joints through a transform adapter, but direct parent-preserving substitution is not established. The next comparison should consider the Source and HD2 skeletons as transform graphs and explicitly represent virtual/root branches instead of silently reparenting either side.

## First real alias-candidate report (2026-11-05)

The updated local comparison report now contains 22 explicit humanoid alias candidates:

- 15 candidate parent relationships match.
- 3 candidate parent relationships mismatch.
- 4 parent relationships are deferred because the Source parent has no alias candidate.
- All 22 candidate target transforms are reported as **not actively weighted** in this selected Avatar Unit.

The last point is a critical diagnostic observation, not yet proof that the Unit is invalid. The report's target active-weight fields come from the decoded Unit mesh weight-usage summary, while the named target transforms come from the Unit's Bones resource. Before interpreting the 22 inactive candidates as an actual rig incompatibility, inspect the mesh's per-section bone palette, local-to-global transform indices, and the exact weight lanes used by vertices. The current report collapses this into a transform-level boolean and does not yet provide the per-section evidence needed to distinguish a real inactive transform from an index/palette accounting problem.

The three structural mismatches are:
- `ValveBiped.Bip01_Spine -> spine1`: Source parent candidate is `hips`, while HD2 `spine1` is parented to `boss`. This indicates a root/hip branch difference.
- `ValveBiped.Bip01_L_Toe0 -> l_toe` and right-side equivalent: Source toe is directly parented to the Source foot candidate, while HD2 `l_toe/r_toe` are parented to `l_ball/r_ball`. HD2 inserts a ball joint between foot and toe.

These differences should not be "fixed" by changing the aliases blindly. The transform graph and vertex palette must be examined first.

## Corrected target influence status and next diagnostic

The latest regenerated comparison report confirms that all 22 humanoid alias candidates resolve to target transforms with active vertex influence. The earlier all-inactive display was a report-field propagation bug in the advisory alias layer, not evidence that the selected Avatar Unit lacked skinning weights. That earlier inference is withdrawn.

The next evidence needed is the compact per-Unit skinning chain:
`vertex-local bone index -> material Remap[local index] -> BoneInfo RealIndices[palette slot] -> TransformInfo index`.
The Unit decoder already resolves this chain per section and per index set. A new helper, `research/summarize_hd2_skinning.py`, converts the full `hd2-unit --json` output into a compact Markdown audit containing BoneInfo records, RealIndices, material remaps, section lane totals, active TransformInfo usage, and the resolved per-vertex influence tuples.

## Important limitations

1. Source active-bone usage is currently a union over the stored VVD vertex pool, not split by BodyGroup or LOD.
2. Non-zero VVD fixup tables are not applied to produce per-LOD vertex usage.
3. The selected HD2 Unit is a baseline Avatar target, not yet confirmed as the matching target for this specific armor/model.
4. Seventeen HD2 TransformInfo entries have unresolved names in the current local hash dictionary.
5. No cross-engine rest-pose matrix comparison, coordinate conversion, retargeting, or weight transfer has been performed.
6. The current comparison is exact-name-only by design; zero matches is not a failed parse.

## Next steps

1. Add a separate candidate-alias report for the conventional humanoid core only. Do not modify exact-name matching or approve aliases automatically.
2. Resolve the 17 HD2 transform hashes using the local SDK/hash dictionaries and structural parent context.
3. Compare the two skeleton hierarchies and rest-pose transforms only after coordinate, unit-scale, matrix-space, and bind-pose conventions are validated.
4. Identify the correct HD2 target Unit for the armor replacement workflow; keep the Avatar Unit as a reference baseline only.
5. Analyze accessory/secondary chains (hair, ribbons, tail, ears, `Qun_*`) separately from the humanoid core. The BodyGroup survey now gives the quantitative seam: 77 bones are used by every one of the 42 combinations, and one skirt choice alone contributes 100 bones that no other combination needs.
6. BodyGroup-level vertex usage is measured (see the survey below: 77 invariant core out of a 242 union). LOD-level usage cannot be measured on this model: it has one VVD LOD, one VTX LOD and zero fixups, so it needs a different Source model rather than a reader change.

## Reproduction

The machine-readable JSON report is intentionally local/ignored. The shareable Markdown report is `research/skeleton_comparison/source_hd2_skeleton.md`.

Run the six synthetic comparison tests:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_skeleton_compare.py" -v
```

## BodyGroup 级活跃骨骼拆分（上面第 6 项的部分完成）

工具：`research/survey_source_bodygroup_bone_use.py`（只读复用 `collect_geometry` 与
`_weighted_bone_usage_for_triangles`，不改顶点、权重或资源字节）。对 `aes_yz_pm.mdl`
LOD0/skin0 的 20 个 BodyGroup、42 个选项组合实测：

| 指标 | 数值 |
|---|---:|
| 任何选项都无法移除的恒定核心 | **77** |
| 全部选项并集 | **242** |
| 单组合活跃骨骼 min / median / max | 124 / 136 / 236 |
| 与默认组合有差异的选项数 | 10 / 42 |

- **Observed**：并集 242 与既有报告的池并集数字一致（独立路径的交叉校验）。
- **Observed**：`group 18 choice 1 [AES_QZ.smd]` 单独 **+100** 条骨骼（组合达到 236），
  发型类 `group 2 choice 1 [AES_TF2.smd]` 是 +6 / −10；其余选项多为纯减少（−2 … −12）。
  这给路线图要求的「人形核心 vs 二级链（发/缎带/尾/耳）」分界提供了数量依据：**恒定核心 77 条**
  是可先做重定向的部分，`AES_QZ.smd` 那 100 条应作为独立子系统处理。
- 语义限制（必须一起读）：每行是**整身组合**而非隔离的部件几何 —— 读者是相对默认组合的
  `+/-`，不是"该部件只用这些骨骼"；隔离需要按 `ModelInfo.vertex_byte_offset` 与 mesh 顶点区间
  过滤三角形（reader 的三角形元组只带材质，不带分组）。VVD fixup 仍未应用，
  因此 LOD 特定的顶点重映射未覆盖；恒定核心按 LOD0 与 skin0 计算。
- 复现：

```powershell
py -3 research/survey_source_bodygroup_bone_use.py gmpublisher.gma --model "models/lc_84/aes_yz_pm.mdl" --output-json research/skeleton_comparison/source_bodygroup_bone_use.json
```

### 更正：LOD 级顶点用量在本样本上不适用（Observed）

上面第 6 项里「按 LOD 拆」的部分，先前被记成"reader 未应用非零 VVD fixup"造成的缺口。直接读 VVD 头后事实是：

| 字段 | 值 |
|---|---:|
| `vvd_num_lods` | 1 |
| `vvd_lod_counts` | `(94858, 94858, …)` 8 项同值，仅索引 0 有意义 |
| `vvd_num_fixups` | **0** |
| `vtx_num_lods` | 1 |
| `vvd_vertex_start` / `vvd_tangent_start` | 64 / 4553248（`fixup_start` 亦为 64，即 fixup 区为空） |
| LOD0 重建三角形 | 106,663；请求 LOD1–LOD3 直接 `LOD n is unavailable` |

- **Observed**：该模型只有一个 LOD，且没有任何 fixup 记录；reader 的"存在 N 条 fixup 未应用"告警只在 `num_fixups != 0` 时追加，因此在本样本上从未触发。
- 结论修正：LOD 级顶点/骨骼用量不是本仓库的代码缺口，而是**样本能力问题**——需要一个 `vvd_num_lods > 1` 或含非零 fixup 的 Source 模型才能验证。这条与 1.0.0 的「测试多个独立模型」要求是同一件事，已在 ROADMAP 的 0.8.4 剩余工作里改口径。
- 因此本节上方的 BodyGroup 组合拆分是本样本上唯一可做的"活跃集合收缩"实验，其 77 / 242 结果不受 fixup 影响。
