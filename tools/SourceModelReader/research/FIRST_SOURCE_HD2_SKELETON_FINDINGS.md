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
5. Analyze accessory/secondary chains (hair, ribbons, tail, ears, `Qun_*`) separately from the humanoid core.
6. Extend VVD analysis to BodyGroup/LOD-specific vertex usage before using the active-bone set for a conversion.

## Reproduction

The machine-readable JSON report is intentionally local/ignored. The shareable Markdown report is `research/skeleton_comparison/source_hd2_skeleton.md`.

Run the six synthetic comparison tests:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_skeleton_compare.py" -v
```
