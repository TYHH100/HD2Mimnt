# HD2 Cross-Unit Transform Comparison

- Tolerance: 0.0001
- Overall diagnostic status: **warning**

## Input Units

| Sample | Unit | Package | Geometry | Transform records |
|---|---|---|---|---:|
| fs05_lower_body | 210fb2f60f8308ea | 8670598c1f4462dc | Unit GPU payload | 16 |
| fs05_torso_left_arm | 324fb206612ce4f7 | 8670598c1f4462dc | Unit GPU payload | 38 |
| avatar_composite | 4d1c334d294dfa97 | 18235e0c9ec0e636 | CompositeUnit | 107 |

## Pairwise summary

| Pair | Comparison | Shared names | Parent match / mismatch | Position match / mismatch | Rotation match / mismatch | Scale match / mismatch | Stored matrix match / mismatch / unavailable |
|---|---|---:|---:|---:|---:|---:|---:|
| fs05_lower_body vs fs05_torso_left_arm | compared | 6 | 4 / 0 | 5 / 1 | 6 / 0 | 6 / 0 | 5 / 1 / 0 |
| fs05_lower_body vs avatar_composite | compared | 7 | 6 / 0 | 7 / 0 | 7 / 0 | 7 / 0 | 7 / 0 / 0 |
| fs05_torso_left_arm vs avatar_composite | compared | 28 | 27 / 0 | 28 / 0 | 28 / 0 | 28 / 0 | 28 / 0 / 0 |

## Pairwise transform details

### fs05_lower_body vs fs05_torso_left_arm

- Shared unique names: 6
- Maximum local-position component error: 0.9279849529266357
- Maximum stored-matrix component error: 0.9279849529266357

| Bone | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |
|---|---|---|---|---:|---:|---:|---:|---:|---|
| FbxAxisSystem_ConvertNode | StingrayEntityRoot | StingrayEntityRoot | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| StingrayEntityRoot | None | None | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| boss | root | root | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| game_mesh | (unresolved) | mech_pilot_light | unavailable | 0.9279849529266357 | 0.0 | 0.0 | 0.9279849529266357 | 0.9279849529266357 | no/no |
| root | skeleton | skeleton | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| skeleton | (unresolved) | mech_pilot_light | unavailable | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |

### fs05_lower_body vs avatar_composite

- Shared unique names: 7
- Maximum local-position component error: 0.0
- Maximum stored-matrix component error: 0.0

| Bone | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |
|---|---|---|---|---:|---:|---:|---:|---:|---|
| FbxAxisSystem_ConvertNode | StingrayEntityRoot | StingrayEntityRoot | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| StingrayEntityRoot | None | None | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| boss | root | root | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| hips | boss | boss | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| l_thigh | hips | hips | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| r_thigh | hips | hips | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| root | skeleton | (unresolved) | unavailable | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |

### fs05_torso_left_arm vs avatar_composite

- Shared unique names: 28
- Maximum local-position component error: 4.2632563192693945e-16
- Maximum stored-matrix component error: 4.17216951955566e-15

| Bone | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |
|---|---|---|---|---:|---:|---:|---:|---:|---|
| FbxAxisSystem_ConvertNode | StingrayEntityRoot | StingrayEntityRoot | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| StingrayEntityRoot | None | None | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| boss | root | root | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| chest | spine2 | spine2 | matches | 1.4210854397564648e-16 | 7.790833713024973e-15 | 0.0 | 3.619542884272971e-15 | 2.675904135318423e-17 | yes/yes |
| l_clavicle | chest | chest | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_elbow | l_shoulder | l_shoulder | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_hand | l_elbow | l_elbow | matches | 2.842176702864442e-16 | 2.7755575615628914e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_hand_twist | l_elbow | l_elbow | matches | 1.0658138812940016e-16 | 2.7755575615628914e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_index_finger1 | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_index_finger2 | l_index_finger1 | l_index_finger1 | matches | 7.105420581337424e-17 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_index_finger3 | l_index_finger2 | l_index_finger2 | matches | 2.842172467699706e-16 | 5.551115123125783e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_middle_finger1 | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_middle_finger2 | l_middle_finger1 | l_middle_finger1 | matches | 2.84223599517075e-16 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_middle_finger3 | l_middle_finger2 | l_middle_finger2 | matches | 4.2630829422130034e-16 | 8.326672684688674e-17 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_pinky_finger1 | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_pinky_finger2 | l_pinky_finger1 | l_pinky_finger1 | matches | 1.421086233849853e-16 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_pinky_finger3 | l_pinky_finger2 | l_pinky_finger2 | matches | 4.2632563192693945e-16 | 1.6653347354610818e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_ring_finger1 | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_ring_finger2 | l_ring_finger1 | l_ring_finger1 | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_ring_finger3 | l_ring_finger2 | l_ring_finger2 | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_shoulder | l_clavicle | l_clavicle | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_shoulder_twist | l_shoulder | l_shoulder | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_thumb_finger1 | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_thumb_finger2 | l_thumb_finger1 | l_thumb_finger1 | matches | 2.8427780962569926e-16 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_thumb_finger3 | l_thumb_finger2 | l_thumb_finger2 | matches | 4.2630829422130034e-16 | 2.220446049250313e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| root | skeleton | (unresolved) | unavailable | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| spine1 | boss | boss | matches | 0.0 | 1.3322677354293063e-15 | 0.0 | 1.3322677354293063e-15 | 0.0 | no/yes |
| spine2 | spine1 | spine1 | matches | 3.552713599391162e-17 | 2.8729246341244836e-15 | 0.0 | 4.17216951955566e-15 | 4.3976567575459085e-17 | yes/yes |

## Names shared by all three samples

- Unique resolved names shared by all three: 4

| Bone | Transform indices (lower / torso / Avatar) | Parent names consistent | Position Δ vs lower-body | Stored matrix Δ vs lower-body | All stored matrices match |
|---|---|---|---:|---:|---|
| FbxAxisSystem_ConvertNode | 1 / 1 / 1 | yes | 0.0 | 0.0 | yes |
| StingrayEntityRoot | 0 / 0 / 0 | yes | 0.0 | 0.0 | yes |
| boss | 11 / 5 / 7 | yes | 0.0 | 0.0 | yes |
| root | 10 / 4 / 4 | no | 0.0 | 0.0 | yes |

## Interpretation limits

- Only unique, resolved TransformInfo names are compared. Duplicate or unresolved names are excluded from name-based matching.
- Position, rotation_3x3 and scale are compared as stored local fields; no axis, handedness, unit-scale or quaternion conversion is applied.
- matrix_4x4_raw is compared as the stored TransformInfo 4x4 value. The existing parser independently checks its relationship to reconstructed hierarchy matrices for each Unit, but this cross-Unit comparison does not establish universal world-space or inverse-bind semantics.
- A numerical match is evidence of similarity between these selected Unit samples only; it does not prove animated deformation, runtime behavior, or Source-to-HD2 retarget correctness.
- Active vertex influence flags are mesh-specific. A bone can exist in the shared skeleton but be unused by one selected mesh.
