# Diagnostics and GLB validation report — 0.5.0-alpha

## Test input

The real `gmpublisher.gma` embedded in the supplied addon ZIP was used. It is a GMA v3 archive with 82 members. The selected model is `models/lc_84/aes_yz_pm.mdl` (MDL v48, VVD v4, VTX v7; checksum 43233072; 255 bones; 94,858 stored LOD0 VVD vertices; 20 BodyGroups).

## Source-model diagnostics

| Metric | Default choices | Glasses + skirt (`4=1`, `18=1`) |
|---|---:|---:|
| Selected vertices referenced by triangles | 64,301 | 69,191 |
| Triangles | 106,663 | 114,309 |
| Material groups | 14 | 14 |
| MDL bone definitions | 255 | 255 |
| MDL bone definitions | 255 | 255 |
| Bone indices directly referenced by selected geometry weights | 136 | 236 |
| MDL definitions not referenced by selected geometry weights | 119 | 19 |
| Active vertices with no valid positive influence | 0 | 0 |
| Weight sums outside 1 ± 0.001 | 0 | 0 |
| Invalid active bone references | 0 | 0 |
| Non-finite active position/normal/UV values | 0 | 0 |
| Parser/structural warnings | 0 | 0 |
| Overall diagnostic status | OK | OK |

The MDL reports 255 bone definitions, one root and 68 leaves. The 119 / 19 values are only the counts of MDL bone indices not directly referenced by positive vertex weights in each selected geometry. They do not classify those definitions as helper bones, and do not imply that they can be removed. In particular, source SMD slices may carry a complete node/skeleton definition even when their own triangles reference only a subset.

The hierarchy has no detected cycles or duplicate names. All 255 bone transforms are finite, all bone quaternions are within 0.001 of unit length, and the maximum rest-pose/inverse-bind identity error is approximately `2.3753e-05` (below the `1e-3` diagnostic threshold).

The stored VVD pool contains 94,858 vertices. The default selection does not reference 30,557 of those stored vertices, while the glasses + skirt selection does not reference 25,667. These are also informational because the VVD pool contains vertices for alternate BodyGroup/model data, not only the currently selected combination.

### Active vertex influence distribution

| Effective influence count | Default | Glasses + skirt |
|---|---:|---:|
| 1 bone | 28,386 | 30,189 |
| 2 bones | 14,768 | 17,759 |
| 3 bones | 21,147 | 21,243 |
| 0 valid influences | 0 | 0 |

The raw active weight sum range is approximately `0.9999998538` to `1.0000001528` for both selections. The report JSON includes the full 255-definition table (parent, children, depth, vertex influence count, accumulated weight) and all 20 BodyGroup option catalogues.

## Independent GLB validation

| Metric | Default GLB | Glasses + skirt GLB |
|---|---:|---:|
| Mesh nodes | 1 | 1 |
| Material primitives | 14 | 14 |
| Triangles | 106,663 | 114,309 |
| Skin joints | 255 | 255 |
| Unique shared vertices checked | 64,301 | 69,191 |
| Weight sums outside 1 ± 0.001 | 0 | 0 |
| Validation status | OK | OK |

The validator checked GLB container lengths/chunks, embedded buffer and accessor bounds, mesh attribute counts, triangle index bounds, material indices, skin joint node references, inverse-bind matrix count/type/finite values, and weight sums.

## Automated tests

Built-in unit tests in 0.5.0: **16/16 passed**, including a synthetic BodyGroup/Bone matrix test. The real GMA matrix scan and option-level results are documented in `BONE_MATRIX_TEST_REPORT.md`.

## Remaining limits

The diagnosis is static. It cannot prove that every joint deforms correctly in Blender or that the rig behaves under every animation. The previous manual Blender test confirmed that rotating the pelvis deforms the character mesh; arm, elbow, knee, neck, hair, tail, and accessory pose tests remain useful. Animation sequences, Flex, jiggle-bone simulation, hitboxes, physics, attachments, procedural bones, and VMT/VTF conversion are still outside the current alpha scope.

## Backend parity

The default diagnostic was also run against the addon ZIP wrapper and against the GMA with the DX80 VTX backend explicitly selected. Both produced the same 64,301 selected vertices, 106,663 triangles, 255 bones, 136 weight-referenced bone indices, and the same bind-pose/weight metrics as the default DX90 GMA run.
