# Source / HD2 Rest Joint Comparison

- Source model: models/lc_84/aes_yz_pm.mdl
- HD2 target Unit: 4d1c334d294dfa97
- Candidate mappings: 22
- Scale estimate (HD2 meters per Source unit): 0.030524 (median of 17 paired bone lengths, range 0.023017 to 0.080364)

## Axis hypothesis residuals after root alignment

| Hypothesis | Joints | Mean error | Median error | Max error | Worst joints |
|---|---:|---:|---:|---:|---|
| rot180_z | 22 | 0.0961 | 0.0847 | 0.2343 | Bip01_L_Hand@0.2343, Bip01_R_Hand@0.2343, Bip01_L_Toe0@0.1935 |
| scale_only | 22 | 0.3676 | 0.2639 | 1.3017 | Bip01_L_Hand@1.3017, Bip01_R_Hand@1.3017, Bip01_L_Forearm@0.8691 |
| mirror_x | 22 | 0.1009 | 0.0927 | 0.2435 | Bip01_L_Foot@0.2435, Bip01_R_Foot@0.2435, Bip01_L_Toe0@0.2029 |
| mirror_y | 22 | 0.3614 | 0.2603 | 1.3183 | Bip01_L_Hand@1.3183, Bip01_R_Hand@1.3183, Bip01_L_Forearm@0.8639 |

## Paired bone lengths behind the scale estimate

| Segment | Source length | Target meters | Meters per Source unit |
|---|---:|---:|---:|
| Bip01_Spine -> Bip01_Spine1 | 4.7807 | 0.1927 | 0.040310 |
| Bip01_Spine1 -> Bip01_Spine2 | 3.3799 | 0.1850 | 0.054733 |
| Bip01_Neck1 -> Bip01_Head1 | 4.4461 | 0.1416 | 0.031857 |
| Bip01_Pelvis -> Bip01_L_Thigh | 3.6024 | 0.0927 | 0.025719 |
| Bip01_L_Thigh -> Bip01_L_Calf | 14.7755 | 0.4295 | 0.029069 |
| Bip01_L_Calf -> Bip01_L_Foot | 18.3751 | 0.4229 | 0.023017 |
| Bip01_L_Foot -> Bip01_L_Toe0 | 3.3320 | 0.2678 | 0.080364 |
| Bip01_L_Clavicle -> Bip01_L_UpperArm | 2.0441 | 0.1556 | 0.076136 |
| Bip01_L_UpperArm -> Bip01_L_Forearm | 10.1350 | 0.3028 | 0.029881 |
| Bip01_L_Forearm -> Bip01_L_Hand | 9.4142 | 0.2874 | 0.030524 |
| Bip01_Pelvis -> Bip01_R_Thigh | 3.6024 | 0.0927 | 0.025719 |
| Bip01_R_Thigh -> Bip01_R_Calf | 14.7755 | 0.4295 | 0.029069 |
| Bip01_R_Calf -> Bip01_R_Foot | 18.3750 | 0.4229 | 0.023017 |
| Bip01_R_Foot -> Bip01_R_Toe0 | 3.3320 | 0.2678 | 0.080363 |
| Bip01_R_Clavicle -> Bip01_R_UpperArm | 2.0441 | 0.1556 | 0.076136 |
| Bip01_R_UpperArm -> Bip01_R_Forearm | 10.1349 | 0.3028 | 0.029881 |
| Bip01_R_Forearm -> Bip01_R_Hand | 9.4142 | 0.2874 | 0.030524 |

## Least-squares orientation test (threshold-free)

Center both joint clouds, fit A from the cross-covariance and read sign(det(A)).
Positive means the best fit preserves orientation, negative means a mirror.

- Joint pairs: 22
- Determinant: +1.997544e-05; normalized by column norms: +0.921355
- Orientation: **preserved (rotation plus scale)**
- Fit RMS: 0.0905 m; worst joint 0.1798 m
- Column norms: 0.033963, 0.022242, 0.028701
- Fitted matrix (row-major): -0.033963, -0.000029, -0.000000, -0.000000, -0.020575, -0.000277, 0.000009, 0.008448, 0.028699

## Frame chirality: per-triple sign survey

Sign of det(left, right, up) about the root joint, per rig. Same sign in both rigs
means an even number of axis negations, which is a rotation; opposite means a mirror.

| Rig | Triple | Signed volume | Volume / |L||R||U| | Usable |
|---|---|---:|---:|---|
| Source | ValveBiped.Bip01_L_UpperArm / ValveBiped.Bip01_R_UpperArm / ValveBiped.Bip01_Head1 about ValveBiped.Bip01_Pelvis | +162.379243 | 0.0255 | yes |
| Source | ValveBiped.Bip01_L_Forearm / ValveBiped.Bip01_R_Forearm / ValveBiped.Bip01_Head1 about ValveBiped.Bip01_Pelvis | +852.980579 | 0.1314 | yes |
| Source | ValveBiped.Bip01_L_Thigh / ValveBiped.Bip01_R_Thigh / ValveBiped.Bip01_Head1 about ValveBiped.Bip01_Pelvis | +91.116734 | 0.3164 | yes |
| HD2 | l_shoulder / r_shoulder / head about hips | +0.013535 | 0.0634 | yes |
| HD2 | l_elbow / r_elbow / head about hips | +0.020783 | 0.0837 | yes |
| HD2 | l_thigh / r_thigh / head about hips | -0.000291 | 0.0473 | yes |

- Usable pairs: 3; same-sign 2; opposite-sign 1
- Required negation parity: **undetermined**

## Candidate mapping review table

| Source | Target | Source parent | Target parent | Parent status | Active (src/tgt) | Disposition | Approval |
|---|---|---|---|---|---|---|---|
| ValveBiped.Bip01_Pelvis | hips | (none) | boss | not_checked_source_parent_has_no_candidate_alias | True/457 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_Spine | spine1 | ValveBiped.Bip01_Pelvis | boss | mismatch | True/1006 | candidate with structural mismatch | advisory_only |
| ValveBiped.Bip01_Spine1 | spine2 | ValveBiped.Bip01_Spine | spine1 | match | True/4731 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_Spine2 | chest | ValveBiped.Bip01_Spine1 | spine2 | match | True/7264 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_Neck1 | neck | ValveBiped.Bip01_Spine4 | chest | not_checked_source_parent_has_no_candidate_alias | True/1046 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_Head1 | head | ValveBiped.Bip01_Neck1 | neck | match | True/7473 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_L_Thigh | l_thigh | ValveBiped.Bip01_Pelvis | hips | match | True/1465 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_L_Calf | l_knee | ValveBiped.Bip01_L_Thigh | l_thigh | match | True/1079 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_L_Foot | l_foot | ValveBiped.Bip01_L_Calf | l_knee | match | True/441 | candidate with offset rest joint | advisory_only |
| ValveBiped.Bip01_L_Toe0 | l_toe | ValveBiped.Bip01_L_Foot | l_ball | mismatch | True/2 | candidate with structural mismatch | advisory_only |
| ValveBiped.Bip01_L_Clavicle | l_clavicle | ValveBiped.Bip01_Spine4 | chest | not_checked_source_parent_has_no_candidate_alias | True/745 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_L_UpperArm | l_shoulder | ValveBiped.Bip01_L_Clavicle | l_clavicle | match | True/702 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_L_Forearm | l_elbow | ValveBiped.Bip01_L_UpperArm | l_shoulder | match | True/1927 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_L_Hand | l_hand | ValveBiped.Bip01_L_Forearm | l_elbow | match | True/755 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_R_Thigh | r_thigh | ValveBiped.Bip01_Pelvis | hips | match | True/640 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_R_Calf | r_knee | ValveBiped.Bip01_R_Thigh | r_thigh | match | True/1072 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_R_Foot | r_foot | ValveBiped.Bip01_R_Calf | r_knee | match | True/441 | candidate with offset rest joint | advisory_only |
| ValveBiped.Bip01_R_Toe0 | r_toe | ValveBiped.Bip01_R_Foot | r_ball | mismatch | True/2 | candidate with structural mismatch | advisory_only |
| ValveBiped.Bip01_R_Clavicle | r_clavicle | ValveBiped.Bip01_Spine4 | chest | not_checked_source_parent_has_no_candidate_alias | True/788 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_R_UpperArm | r_shoulder | ValveBiped.Bip01_R_Clavicle | r_clavicle | match | True/700 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_R_Forearm | r_elbow | ValveBiped.Bip01_R_UpperArm | r_shoulder | match | True/820 | candidate with close rest joint | advisory_only |
| ValveBiped.Bip01_R_Hand | r_hand | ValveBiped.Bip01_R_Forearm | r_elbow | match | True/421 | candidate with close rest joint | advisory_only |

## Interpretation limits

- Scale is estimated from paired bone lengths among the candidate joints themselves, so it inherits their mapping errors; it is an estimate, not a calibration.
- Each axis hypothesis is fitted by aligning one root pair, which absorbs translation but not rotation or mirror choices.
- Joint residuals cannot separate a 180 degree rotation from a single-axis mirror on a near-symmetric body, so the frame chirality comparison decides the parity of axis negations instead.
- Chirality compares only the sign of det(left, right, up); the volume itself scales with units and body size, and near-coplanar triples are gated out rather than counted.
- Residuals compare rest joint positions only. No animation, deformation or in-game behavior is measured here.
- Every row keeps the advisory approval from the alias layer; this script approves no mapping and applies none.
- Rows whose source or target joint is missing stay without rest evidence rather than being dropped from the table.
