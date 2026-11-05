# HD2 Unit Skinning Audit

- Unit: `4d1c334d294dfa97`
- Package: `18235e0c9ec0e636`
- Geometry source: `CompositeUnit`

## Unit summary

| Metric | Value |
|---|---:|
| Transforms | 107 |
| BoneInfo records | 1 |
| Meshes | 1 |
| Decoded meshes | 1 |
| Vertices | 27355 |
| Indices | 123495 |
| Triangles | 41165 |

## BoneInfo palettes

| Record | Bone entries | RealIndices entries | Material remaps |
|---:|---:|---:|---:|
| 0 | 88 | 88 | 1 |
- BoneInfo[0] Remap[0]: 61 entries, 61 unique palette slots, valid references 61/61, range 0..84.

## Mesh / section resolution

| Mesh | LOD | BoneInfo | Section | Material | Status | Vertices | Resolved lanes | Unresolved lanes |
|---|---:|---:|---:|---:|---|---:|---:|---:|
| `1278906229` | 0 | 0 | 0 | 0 | ok | 27355 | 42158 | 0 |

## Active TransformInfo usage

| Transform | Bone | Active lanes | Parent |
|---:|---|---:|---|
| 4 | root | 34 | (unresolved) |
| 7 | boss | 916 | root |
| 9 | hips | 457 | boss |
| 10 | r_thigh | 640 | hips |
| 12 | r_knee | 1072 | r_thigh |
| 13 | r_foot | 441 | r_knee |
| 14 | r_ball | 83 | r_foot |
| 15 | r_toe | 2 | r_ball |
| 16 | l_thigh | 1465 | hips |
| 17 | l_knee | 1079 | l_thigh |
| 18 | l_foot | 441 | l_knee |
| 19 | l_ball | 83 | l_foot |
| 20 | l_toe | 2 | l_ball |
| 21 | spine1 | 1006 | boss |
| 26 | spine2 | 4731 | spine1 |
| 27 | sling | 4 | spine2 |
| 28 | chest | 7264 | spine2 |
| 42 | l_clavicle | 745 | chest |
| 44 | l_shoulder | 702 | l_clavicle |
| 45 | l_shoulder_twist | 2019 | l_shoulder |
| 46 | l_elbow | 1927 | l_shoulder |
| 47 | l_hand | 755 | l_elbow |
| 49 | l_pinky_finger1 | 190 | l_hand |
| 50 | l_pinky_finger2 | 139 | l_pinky_finger1 |
| 51 | l_pinky_finger3 | 75 | l_pinky_finger2 |
| 52 | l_ring_finger1 | 196 | l_hand |
| 53 | l_ring_finger2 | 90 | l_ring_finger1 |
| 54 | l_ring_finger3 | 74 | l_ring_finger2 |
| 55 | l_middle_finger1 | 204 | l_hand |
| 56 | l_middle_finger2 | 132 | l_middle_finger1 |
| 57 | l_middle_finger3 | 70 | l_middle_finger2 |
| 58 | l_index_finger1 | 197 | l_hand |
| 59 | l_index_finger2 | 106 | l_index_finger1 |
| 60 | l_index_finger3 | 75 | l_index_finger2 |
| 61 | l_thumb_finger1 | 175 | l_hand |
| 62 | l_thumb_finger2 | 144 | l_thumb_finger1 |
| 63 | l_thumb_finger3 | 123 | l_thumb_finger2 |
| 64 | l_hand_twist | 266 | l_elbow |
| 65 | r_clavicle | 788 | chest |
| 67 | r_shoulder | 700 | r_clavicle |
| 68 | r_shoulder_twist | 811 | r_shoulder |
| 69 | r_elbow | 820 | r_shoulder |
| 70 | r_hand_twist | 281 | r_elbow |
| 71 | r_hand | 421 | r_elbow |
| 72 | r_pinky_finger1 | 184 | r_hand |
| 73 | r_pinky_finger2 | 77 | r_pinky_finger1 |
| 74 | r_pinky_finger3 | 67 | r_pinky_finger2 |
| 75 | r_ring_finger1 | 192 | r_hand |
| 76 | r_ring_finger2 | 64 | r_ring_finger1 |
| 77 | r_ring_finger3 | 50 | r_ring_finger2 |
| 78 | r_middle_finger1 | 164 | r_hand |
| 79 | r_middle_finger2 | 64 | r_middle_finger1 |
| 80 | r_middle_finger3 | 53 | r_middle_finger2 |
| 81 | r_index_finger1 | 175 | r_hand |
| 82 | r_index_finger2 | 90 | r_index_finger1 |
| 83 | r_index_finger3 | 72 | r_index_finger2 |
| 86 | r_thumb_finger1 | 181 | r_hand |
| 87 | r_thumb_finger2 | 168 | r_thumb_finger1 |
| 88 | r_thumb_finger3 | 93 | r_thumb_finger2 |
| 89 | neck | 1046 | chest |
| 90 | head | 7473 | neck |

## Detailed section mapping

### Mesh `1278906229` / LOD 0

- Skinning status: `ok`
- BoneInfo record: `0`
- Total resolved active lanes: 42158
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 42158 / 0

**Bone-index component 0**

- Active lanes: 42158
- Vertices with weight data: 27355
- Per-vertex influence distribution: 1 influence(s): 17452 vertices, 2 influence(s): 6255 vertices, 3 influence(s): 2396 vertices, 4 influence(s): 1252 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 1.000005; 27355/27355 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 6 | 90 | head | 7473 | 0.001167–1.000000 | ok |
| 1 | 0 | 4 | root | 34 | 0.001110–0.015152 | ok |
| 2 | 33 | 68 | r_shoulder_twist | 811 | 0.001047–1.000000 | ok |
| 3 | 56 | 45 | l_shoulder_twist | 2019 | 0.001005–1.000000 | ok |
| 4 | 3 | 26 | spine2 | 4731 | 0.001021–1.000000 | ok |
| 5 | 4 | 28 | chest | 7264 | 0.001075–1.000000 | ok |
| 6 | 35 | 42 | l_clavicle | 745 | 0.001001–0.885254 | ok |
| 7 | 11 | 65 | r_clavicle | 788 | 0.001018–1.000000 | ok |
| 8 | 71 | 27 | sling | 4 | 0.106812–0.187744 | ok |
| 9 | 5 | 89 | neck | 1046 | 0.001220–1.000000 | ok |
| 10 | 81 | 12 | r_knee | 1072 | 0.001382–1.000000 | ok |
| 11 | 82 | 13 | r_foot | 441 | 0.001374–1.000000 | ok |
| 12 | 75 | 16 | l_thigh | 1465 | 0.001119–1.000000 | ok |
| 13 | 76 | 17 | l_knee | 1079 | 0.001237–1.000000 | ok |
| 14 | 77 | 18 | l_foot | 441 | 0.001819–1.000000 | ok |
| 15 | 13 | 69 | r_elbow | 820 | 0.001015–1.000000 | ok |
| 16 | 12 | 67 | r_shoulder | 700 | 0.001076–1.000000 | ok |
| 17 | 32 | 70 | r_hand_twist | 281 | 0.001005–1.000000 | ok |
| 18 | 37 | 46 | l_elbow | 1927 | 0.004211–1.000000 | ok |
| 19 | 36 | 44 | l_shoulder | 702 | 0.001016–0.960938 | ok |
| 20 | 80 | 10 | r_thigh | 640 | 0.001062–1.000000 | ok |
| 21 | 74 | 9 | hips | 457 | 0.001065–0.936035 | ok |
| 22 | 1 | 7 | boss | 916 | 0.001004–1.000000 | ok |
| 23 | 2 | 21 | spine1 | 1006 | 0.001009–1.000000 | ok |
| 24 | 78 | 19 | l_ball | 83 | 0.001923–1.000000 | ok |
| 25 | 79 | 20 | l_toe | 2 | 0.129517–0.129517 | ok |
| 26 | 83 | 14 | r_ball | 83 | 0.001790–1.000000 | ok |
| 27 | 84 | 15 | r_toe | 2 | 0.129517–0.129517 | ok |
| 28 | 16 | 87 | r_thumb_finger2 | 168 | 0.001074–1.000000 | ok |
| 29 | 17 | 88 | r_thumb_finger3 | 93 | 0.001075–1.000000 | ok |
| 30 | 14 | 71 | r_hand | 421 | 0.001072–1.000000 | ok |
| 31 | 15 | 86 | r_thumb_finger1 | 181 | 0.001746–1.000000 | ok |
| 32 | 22 | 83 | r_index_finger3 | 72 | 0.001143–1.000000 | ok |
| 33 | 25 | 80 | r_middle_finger3 | 53 | 0.051758–1.000000 | ok |
| 34 | 27 | 76 | r_ring_finger2 | 64 | 0.017502–1.000000 | ok |
| 35 | 28 | 77 | r_ring_finger3 | 50 | 0.001031–1.000000 | ok |
| 36 | 21 | 82 | r_index_finger2 | 90 | 0.001325–1.000000 | ok |
| 37 | 20 | 81 | r_index_finger1 | 175 | 0.001323–1.000000 | ok |
| 38 | 23 | 78 | r_middle_finger1 | 164 | 0.001136–1.000000 | ok |
| 39 | 26 | 75 | r_ring_finger1 | 192 | 0.001068–1.000000 | ok |
| 40 | 24 | 79 | r_middle_finger2 | 64 | 0.002165–1.000000 | ok |
| 41 | 29 | 72 | r_pinky_finger1 | 184 | 0.001082–1.000000 | ok |
| 42 | 30 | 73 | r_pinky_finger2 | 77 | 0.001177–1.000000 | ok |
| 43 | 31 | 74 | r_pinky_finger3 | 67 | 0.001019–1.000000 | ok |
| 44 | 39 | 47 | l_hand | 755 | 0.001138–0.957031 | ok |
| 45 | 40 | 61 | l_thumb_finger1 | 175 | 0.001169–0.753418 | ok |
| 46 | 41 | 62 | l_thumb_finger2 | 144 | 0.022369–1.000000 | ok |
| 47 | 38 | 64 | l_hand_twist | 266 | 0.001220–0.333252 | ok |
| 48 | 42 | 63 | l_thumb_finger3 | 123 | 0.001047–1.000000 | ok |
| 49 | 45 | 60 | l_index_finger3 | 75 | 0.001471–1.000000 | ok |
| 50 | 48 | 57 | l_middle_finger3 | 70 | 0.001150–1.000000 | ok |
| 51 | 50 | 53 | l_ring_finger2 | 90 | 0.001114–1.000000 | ok |
| 52 | 49 | 52 | l_ring_finger1 | 196 | 0.001343–1.000000 | ok |
| 53 | 51 | 54 | l_ring_finger3 | 74 | 0.001740–1.000000 | ok |
| 54 | 47 | 56 | l_middle_finger2 | 132 | 0.001123–1.000000 | ok |
| 55 | 44 | 59 | l_index_finger2 | 106 | 0.001207–1.000000 | ok |
| 56 | 43 | 58 | l_index_finger1 | 197 | 0.001162–1.000000 | ok |
| 57 | 46 | 55 | l_middle_finger1 | 204 | 0.001251–1.000000 | ok |
| 58 | 52 | 49 | l_pinky_finger1 | 190 | 0.001041–1.000000 | ok |
| 59 | 53 | 50 | l_pinky_finger2 | 139 | 0.001600–1.000000 | ok |
| 60 | 54 | 51 | l_pinky_finger3 | 75 | 0.001388–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=6, transform=90, bone=head, status=ok
  - vertex 9003, lane 0: weight=0.2249755859375, local=4, palette=3, transform=26, bone=spine2, status=ok
  - vertex 9003, lane 1: weight=0.77490234375, local=5, palette=4, transform=28, bone=chest, status=ok
  - vertex 11655, lane 0: weight=0.0023632049560546875, local=4, palette=3, transform=26, bone=spine2, status=ok
  - vertex 11655, lane 1: weight=0.7958984375, local=5, palette=4, transform=28, bone=chest, status=ok
  - vertex 11655, lane 2: weight=0.201904296875, local=6, palette=35, transform=42, bone=l_clavicle, status=ok
  - vertex 19311, lane 0: weight=0.433349609375, local=22, palette=1, transform=7, bone=boss, status=ok
  - vertex 19311, lane 1: weight=0.31103515625, local=23, palette=2, transform=21, bone=spine1, status=ok
  - vertex 19311, lane 2: weight=0.245361328125, local=21, palette=74, transform=9, bone=hips, status=ok
  - vertex 19311, lane 3: weight=0.010101318359375, local=20, palette=80, transform=10, bone=r_thigh, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 61
- Matches / mismatches / unavailable: 61 / 0 / 0
- Unreferenced palette slots: 27
- Maximum absolute error: 1.7369378935150337e-07


## Decoder limitations

- Vertex-local bone indices are resolved through the active LOD BoneInfo palette and section material Remap into TransformInfo indices; raw bind-pose matrices remain uninterpreted.
- Format 29 is decoded as three normalized bone weights only when the component type is bone_weight; other Format 29 semantics remain raw.
- Transform matrices are emitted as raw stored floats; matrix convention and bind-pose semantics are not changed or inferred.

> This report aggregates influence data by section and local index. Use --include-all-influences only for a full per-lane dump.
