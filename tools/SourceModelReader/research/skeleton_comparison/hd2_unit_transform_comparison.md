# HD2 Cross-Unit Transform Comparison

- Tolerance: 0.0001
- Overall diagnostic status: **review**

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

## Classification summary

| Pair | Skeleton transforms | Skeleton matrix mismatches | Direct Mesh transforms | Direct Mesh matrix mismatches | Mesh-only ancestors | Ancestor matrix mismatches | Role differences |
|---|---:|---:|---:|---:|---:|---:|---:|
| fs05_lower_body vs fs05_torso_left_arm | 5 | 0 | 0 | 0 | 1 | 1 | 0 |
| fs05_lower_body vs avatar_composite | 7 | 0 | 0 | 0 | 0 | 0 | 0 |
| fs05_torso_left_arm vs avatar_composite | 28 | 0 | 0 | 0 | 0 | 0 | 0 |

## Pairwise transform details

### fs05_lower_body vs fs05_torso_left_arm

- Shared unique names: 6
- Maximum local-position component error: 0.9279849529266357
- Maximum stored-matrix component error: 0.9279849529266357

| Transform | Scope | Role A/B | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |
|---|---|---|---|---|---|---:|---:|---:|---:|---:|---|
| FbxAxisSystem_ConvertNode | skeleton_transform | unweighted_skeleton/unweighted_skeleton | StingrayEntityRoot | StingrayEntityRoot | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| StingrayEntityRoot | skeleton_transform | unweighted_skeleton/unweighted_skeleton | None | None | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| boss | skeleton_transform | unweighted_skeleton/unweighted_skeleton | root | root | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| game_mesh | mesh_hierarchy_transform | mesh_hierarchy_transform/mesh_hierarchy_transform | (unresolved) | mech_pilot_light | unavailable | 0.9279849529266357 | 0.0 | 0.0 | 0.9279849529266357 | 0.9279849529266357 | no/no |
| root | skeleton_transform | unweighted_skeleton/unweighted_skeleton | skeleton | skeleton | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| skeleton | skeleton_transform | unweighted_skeleton/unweighted_skeleton | (unresolved) | mech_pilot_light | unavailable | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |

### fs05_lower_body vs avatar_composite

- Shared unique names: 7
- Maximum local-position component error: 0.0
- Maximum stored-matrix component error: 0.0

| Transform | Scope | Role A/B | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |
|---|---|---|---|---|---|---:|---:|---:|---:|---:|---|
| FbxAxisSystem_ConvertNode | skeleton_transform | unweighted_skeleton/unweighted_skeleton | StingrayEntityRoot | StingrayEntityRoot | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| StingrayEntityRoot | skeleton_transform | unweighted_skeleton/unweighted_skeleton | None | None | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| boss | skeleton_transform | unweighted_skeleton/weighted_skeleton | root | root | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| hips | skeleton_transform | unweighted_skeleton/weighted_skeleton | boss | boss | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| l_thigh | skeleton_transform | weighted_skeleton/weighted_skeleton | hips | hips | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| r_thigh | skeleton_transform | weighted_skeleton/weighted_skeleton | hips | hips | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| root | skeleton_transform | unweighted_skeleton/weighted_skeleton | skeleton | (unresolved) | unavailable | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |

### fs05_torso_left_arm vs avatar_composite

- Shared unique names: 28
- Maximum local-position component error: 4.2632563192693945e-16
- Maximum stored-matrix component error: 4.17216951955566e-15

| Transform | Scope | Role A/B | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |
|---|---|---|---|---|---|---:|---:|---:|---:|---:|---|
| FbxAxisSystem_ConvertNode | skeleton_transform | unweighted_skeleton/unweighted_skeleton | StingrayEntityRoot | StingrayEntityRoot | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| StingrayEntityRoot | skeleton_transform | unweighted_skeleton/unweighted_skeleton | None | None | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/no |
| boss | skeleton_transform | unweighted_skeleton/weighted_skeleton | root | root | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| chest | skeleton_transform | weighted_skeleton/weighted_skeleton | spine2 | spine2 | matches | 1.4210854397564648e-16 | 7.790833713024973e-15 | 0.0 | 3.619542884272971e-15 | 2.675904135318423e-17 | yes/yes |
| l_clavicle | skeleton_transform | weighted_skeleton/weighted_skeleton | chest | chest | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_elbow | skeleton_transform | weighted_skeleton/weighted_skeleton | l_shoulder | l_shoulder | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_hand | skeleton_transform | weighted_skeleton/weighted_skeleton | l_elbow | l_elbow | matches | 2.842176702864442e-16 | 2.7755575615628914e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_hand_twist | skeleton_transform | weighted_skeleton/weighted_skeleton | l_elbow | l_elbow | matches | 1.0658138812940016e-16 | 2.7755575615628914e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_index_finger1 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_index_finger2 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_index_finger1 | l_index_finger1 | matches | 7.105420581337424e-17 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_index_finger3 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_index_finger2 | l_index_finger2 | matches | 2.842172467699706e-16 | 5.551115123125783e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_middle_finger1 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_middle_finger2 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_middle_finger1 | l_middle_finger1 | matches | 2.84223599517075e-16 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_middle_finger3 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_middle_finger2 | l_middle_finger2 | matches | 4.2630829422130034e-16 | 8.326672684688674e-17 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_pinky_finger1 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_pinky_finger2 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_pinky_finger1 | l_pinky_finger1 | matches | 1.421086233849853e-16 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_pinky_finger3 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_pinky_finger2 | l_pinky_finger2 | matches | 4.2632563192693945e-16 | 1.6653347354610818e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_ring_finger1 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_ring_finger2 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_ring_finger1 | l_ring_finger1 | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_ring_finger3 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_ring_finger2 | l_ring_finger2 | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_shoulder | skeleton_transform | weighted_skeleton/weighted_skeleton | l_clavicle | l_clavicle | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_shoulder_twist | skeleton_transform | weighted_skeleton/weighted_skeleton | l_shoulder | l_shoulder | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_thumb_finger1 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_hand | l_hand | matches | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_thumb_finger2 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_thumb_finger1 | l_thumb_finger1 | matches | 2.8427780962569926e-16 | 0.0 | 0.0 | 0.0 | 0.0 | yes/yes |
| l_thumb_finger3 | skeleton_transform | weighted_skeleton/weighted_skeleton | l_thumb_finger2 | l_thumb_finger2 | matches | 4.2630829422130034e-16 | 2.220446049250313e-16 | 0.0 | 0.0 | 0.0 | yes/yes |
| root | skeleton_transform | unweighted_skeleton/weighted_skeleton | skeleton | (unresolved) | unavailable | 0.0 | 0.0 | 0.0 | 0.0 | 0.0 | no/yes |
| spine1 | skeleton_transform | unweighted_skeleton/weighted_skeleton | boss | boss | matches | 0.0 | 1.3322677354293063e-15 | 0.0 | 1.3322677354293063e-15 | 0.0 | no/yes |
| spine2 | skeleton_transform | weighted_skeleton/weighted_skeleton | spine1 | spine1 | matches | 3.552713599391162e-17 | 2.8729246341244836e-15 | 0.0 | 4.17216951955566e-15 | 4.3976567575459085e-17 | yes/yes |

## Names shared by all three samples

- Unique resolved names shared by all three: 4

| Transform | Scope | Roles (lower / torso / Avatar) | Transform indices (lower / torso / Avatar) | Parent names consistent | Position Δ vs lower-body | Stored matrix Δ vs lower-body | All stored matrices match |
|---|---|---|---|---|---:|---:|---|
| FbxAxisSystem_ConvertNode | skeleton_transform | unweighted_skeleton / unweighted_skeleton / unweighted_skeleton | 1 / 1 / 1 | yes | 0.0 | 0.0 | yes |
| StingrayEntityRoot | skeleton_transform | unweighted_skeleton / unweighted_skeleton / unweighted_skeleton | 0 / 0 / 0 | yes | 0.0 | 0.0 | yes |
| boss | skeleton_transform | unweighted_skeleton / unweighted_skeleton / weighted_skeleton | 11 / 5 / 7 | yes | 0.0 | 0.0 | yes |
| root | skeleton_transform | unweighted_skeleton / unweighted_skeleton / weighted_skeleton | 10 / 4 / 4 | no | 0.0 | 0.0 | yes |

## Interpretation limits

- Only unique, resolved TransformInfo names are compared. Duplicate or unresolved names are excluded from name-based matching.
- A TransformInfo referenced directly by MeshInfo.records[].transform_index is classified as a mesh transform. This is an explicit structural association, not a claim that the transform is disposable or unrelated to all bone behavior.
- An unweighted ancestor of a MeshInfo-bound transform is classified as mesh_hierarchy_transform only when its full descendant subtree contains no active vertex-skinning transforms. Ancestors shared with an active skeleton branch remain in skeleton scope; this is a conservative structural heuristic.
- Named transforms not directly referenced by MeshInfo and not proven to be mesh-only ancestors remain in the skeleton comparison, including unweighted transforms. Lack of direct vertex influence alone never removes a transform from skeleton analysis.
- When a transform is both MeshInfo-bound and actively weighted, it is reported as mesh_and_weighted and placed in the mesh-transform comparison scope so its dual role remains visible.
- Position, rotation_3x3 and scale are compared as stored local fields; no axis, handedness, unit-scale or quaternion conversion is applied.
- matrix_4x4_raw is compared as the stored TransformInfo 4x4 value. The existing parser independently checks its relationship to reconstructed hierarchy matrices for each Unit, but this cross-Unit comparison does not establish universal world-space or inverse-bind semantics.
- A numerical match is evidence of similarity between these selected Unit samples only; it does not prove animated deformation, runtime behavior, or Source-to-HD2 retarget correctness.
- A mesh-transform-only difference yields diagnostic status 'review' rather than a skeleton warning; mesh-space differences remain visible and require interpretation before vertex conversion.
