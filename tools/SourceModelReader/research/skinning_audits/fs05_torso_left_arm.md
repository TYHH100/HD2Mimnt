# HD2 Unit Skinning Audit

- Unit: `324fb206612ce4f7`
- Package: `8670598c1f4462dc`
- Geometry source: `Unit GPU payload`

## Unit summary

| Metric | Value |
|---|---:|
| Transforms | 38 |
| BoneInfo records | 5 |
| Meshes | 5 |
| Decoded meshes | 5 |
| Vertices | 7525 |
| Indices | 35196 |
| Triangles | 11732 |

## BoneInfo palettes

| Record | Bone entries | RealIndices entries | Material remaps |
|---:|---:|---:|---:|
| 0 | 25 | 25 | 2 |
- BoneInfo[0] Remap[0]: 23 entries, 23 unique palette slots, valid references 23/23, range 0..24.
- BoneInfo[0] Remap[1]: 2 entries, 2 unique palette slots, valid references 2/2, range 0..5.
| 1 | 22 | 22 | 2 |
- BoneInfo[1] Remap[0]: 2 entries, 2 unique palette slots, valid references 2/2, range 0..12.
- BoneInfo[1] Remap[1]: 22 entries, 22 unique palette slots, valid references 22/22, range 0..21.
| 2 | 22 | 22 | 2 |
- BoneInfo[2] Remap[0]: 2 entries, 2 unique palette slots, valid references 2/2, range 0..12.
- BoneInfo[2] Remap[1]: 22 entries, 22 unique palette slots, valid references 22/22, range 0..21.
| 3 | 22 | 22 | 1 |
- BoneInfo[3] Remap[0]: 22 entries, 22 unique palette slots, valid references 22/22, range 0..21.
| 4 | 5 | 5 | 1 |
- BoneInfo[4] Remap[0]: 5 entries, 5 unique palette slots, valid references 5/5, range 0..4.

## Mesh / section resolution

| Mesh | LOD | BoneInfo | Section | Material | Status | Vertices | Resolved lanes | Unresolved lanes |
|---|---:|---:|---:|---:|---|---:|---:|---:|
| `3648454405` | 4 | 4 | 0 | 0 | ok | 439 | 2616 | 0 |
| `522128404` | 3 | 3 | 0 | 0 | ok | 1426 | 7779 | 0 |
| `3766176514` | 2 | 2 | 0 | 0 | ok | 408 | 1224 | 0 |
| `3766176514` | 2 | 2 | 1 | 1 | ok | 430 | 2835 | 0 |
| `2627263307` | 1 | 1 | 0 | 0 | ok | 517 | 1551 | 0 |
| `2627263307` | 1 | 1 | 1 | 1 | ok | 1190 | 8145 | 0 |
| `46827504` | 0 | 0 | 0 | 0 | ok | 2594 | 17325 | 0 |
| `46827504` | 0 | 0 | 1 | 1 | ok | 521 | 1563 | 0 |

## Active TransformInfo usage

| Transform | Bone | Active lanes | Parent |
|---:|---|---:|---|
| 7 | spine2 | 64 | spine1 |
| 8 | chest | 1738 | spine2 |
| 10 | l_clavicle | 1501 | chest |
| 11 | l_shoulder | 12324 | l_clavicle |
| 12 | l_shoulder_twist | 2820 | l_shoulder |
| 13 | l_elbow | 12084 | l_shoulder |
| 15 | l_hand | 2136 | l_elbow |
| 16 | l_pinky_finger1 | 468 | l_hand |
| 17 | l_pinky_finger2 | 374 | l_pinky_finger1 |
| 18 | l_pinky_finger3 | 470 | l_pinky_finger2 |
| 19 | l_ring_finger1 | 500 | l_hand |
| 20 | l_ring_finger2 | 432 | l_ring_finger1 |
| 21 | l_ring_finger3 | 272 | l_ring_finger2 |
| 22 | l_middle_finger1 | 684 | l_hand |
| 23 | l_middle_finger2 | 376 | l_middle_finger1 |
| 24 | l_middle_finger3 | 238 | l_middle_finger2 |
| 25 | l_index_finger1 | 612 | l_hand |
| 26 | l_index_finger2 | 454 | l_index_finger1 |
| 27 | l_index_finger3 | 266 | l_index_finger2 |
| 28 | l_thumb_finger1 | 584 | l_hand |
| 29 | l_thumb_finger2 | 484 | l_thumb_finger1 |
| 30 | l_thumb_finger3 | 392 | l_thumb_finger2 |
| 31 | l_hand_twist | 3765 | l_elbow |

## Detailed section mapping

### Mesh `3648454405` / LOD 4

- Skinning status: `ok`
- BoneInfo record: `4`
- Total resolved active lanes: 2616
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 2616 / 0

**Bone-index component 0**

- Active lanes: 872
- Vertices with weight data: 439
- Per-vertex influence distribution: 1 influence(s): 93 vertices, 2 influence(s): 259 vertices, 3 influence(s): 87 vertices
- Per-vertex weight sum: min 0.998045, max 1.000000, mean 0.999138; 402/439 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 1 | 8 | chest | 124 | 0.003910–1.000000 | ok |
| 1 | 2 | 10 | l_clavicle | 135 | 0.001955–1.000000 | ok |
| 2 | 0 | 7 | spine2 | 64 | 0.100684–1.000000 | ok |
| 3 | 3 | 11 | l_shoulder | 287 | 0.033236–1.000000 | ok |
| 4 | 4 | 13 | l_elbow | 262 | 0.229717–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 37, lane 0: weight=1.0, local=3, palette=3, transform=11, bone=l_shoulder, status=ok
  - vertex 0, lane 0: weight=0.4995112414467253, local=0, palette=1, transform=8, bone=chest, status=ok
  - vertex 0, lane 1: weight=0.4995112414467253, local=1, palette=2, transform=10, bone=l_clavicle, status=ok
  - vertex 1, lane 0: weight=0.3675464320625611, local=0, palette=1, transform=8, bone=chest, status=ok
  - vertex 1, lane 1: weight=0.3675464320625611, local=1, palette=2, transform=10, bone=l_clavicle, status=ok
  - vertex 1, lane 2: weight=0.26295210166177907, local=3, palette=3, transform=11, bone=l_shoulder, status=ok

**Bone-index component 1**

- Active lanes: 872
- Vertices with weight data: 439
- Per-vertex influence distribution: 1 influence(s): 93 vertices, 2 influence(s): 259 vertices, 3 influence(s): 87 vertices
- Per-vertex weight sum: min 0.998045, max 1.000000, mean 0.999138; 402/439 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 3 | 3 | 11 | l_shoulder | 610 | 0.001955–1.000000 | ok |
| 4 | 4 | 13 | l_elbow | 262 | 0.229717–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 37, lane 0: weight=1.0, local=3, palette=3, transform=11, bone=l_shoulder, status=ok
  - vertex 0, lane 0: weight=0.4995112414467253, local=3, palette=3, transform=11, bone=l_shoulder, status=ok
  - vertex 0, lane 1: weight=0.4995112414467253, local=3, palette=3, transform=11, bone=l_shoulder, status=ok
  - vertex 1, lane 0: weight=0.3675464320625611, local=3, palette=3, transform=11, bone=l_shoulder, status=ok
  - vertex 1, lane 1: weight=0.3675464320625611, local=3, palette=3, transform=11, bone=l_shoulder, status=ok
  - vertex 1, lane 2: weight=0.26295210166177907, local=3, palette=3, transform=11, bone=l_shoulder, status=ok

**Bone-index component 2**

- Active lanes: 872
- Vertices with weight data: 439
- Per-vertex influence distribution: 1 influence(s): 93 vertices, 2 influence(s): 259 vertices, 3 influence(s): 87 vertices
- Per-vertex weight sum: min 0.998045, max 1.000000, mean 0.999138; 402/439 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 1 | 8 | chest | 641 | 0.001955–1.000000 | ok |
| 1 | 2 | 10 | l_clavicle | 231 | 0.100684–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 37, lane 0: weight=1.0, local=1, palette=2, transform=10, bone=l_clavicle, status=ok
  - vertex 0, lane 0: weight=0.4995112414467253, local=1, palette=2, transform=10, bone=l_clavicle, status=ok
  - vertex 0, lane 1: weight=0.4995112414467253, local=0, palette=1, transform=8, bone=chest, status=ok
  - vertex 1, lane 0: weight=0.3675464320625611, local=1, palette=2, transform=10, bone=l_clavicle, status=ok
  - vertex 1, lane 1: weight=0.3675464320625611, local=0, palette=1, transform=8, bone=chest, status=ok
  - vertex 1, lane 2: weight=0.26295210166177907, local=0, palette=1, transform=8, bone=chest, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 5
- Matches / mismatches / unavailable: 5 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 9.369958597904571e-08

### Mesh `522128404` / LOD 3

- Skinning status: `ok`
- BoneInfo record: `3`
- Total resolved active lanes: 7779
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 7779 / 0

**Bone-index component 0**

- Active lanes: 2593
- Vertices with weight data: 1426
- Per-vertex influence distribution: 1 influence(s): 717 vertices, 2 influence(s): 312 vertices, 3 influence(s): 336 vertices, 4 influence(s): 61 vertices
- Per-vertex weight sum: min 0.999634, max 1.000286, mean 0.999995; 1426/1426 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 905 | 0.001049–1.000000 | ok |
| 1 | 0 | 18 | l_pinky_finger3 | 17 | 0.004452–1.000000 | ok |
| 2 | 2 | 13 | l_elbow | 279 | 0.001009–1.000000 | ok |
| 3 | 8 | 31 | l_hand_twist | 262 | 0.001519–1.000000 | ok |
| 4 | 5 | 12 | l_shoulder_twist | 313 | 0.001075–1.000000 | ok |
| 5 | 3 | 15 | l_hand | 157 | 0.001451–1.000000 | ok |
| 6 | 17 | 10 | l_clavicle | 99 | 0.001030–0.913574 | ok |
| 7 | 20 | 8 | chest | 127 | 0.001417–1.000000 | ok |
| 8 | 1 | 21 | l_ring_finger3 | 22 | 0.001063–1.000000 | ok |
| 9 | 19 | 30 | l_thumb_finger3 | 26 | 0.001535–1.000000 | ok |
| 10 | 4 | 27 | l_index_finger3 | 19 | 0.005951–1.000000 | ok |
| 11 | 7 | 24 | l_middle_finger3 | 18 | 0.093445–1.000000 | ok |
| 12 | 13 | 23 | l_middle_finger2 | 26 | 0.001813–1.000000 | ok |
| 13 | 15 | 19 | l_ring_finger1 | 33 | 0.003809–0.967773 | ok |
| 14 | 21 | 20 | l_ring_finger2 | 32 | 0.014557–1.000000 | ok |
| 15 | 9 | 17 | l_pinky_finger2 | 30 | 0.002796–1.000000 | ok |
| 16 | 10 | 16 | l_pinky_finger1 | 36 | 0.002289–1.000000 | ok |
| 17 | 18 | 22 | l_middle_finger1 | 50 | 0.001043–1.000000 | ok |
| 18 | 6 | 25 | l_index_finger1 | 42 | 0.003456–1.000000 | ok |
| 19 | 11 | 26 | l_index_finger2 | 31 | 0.001254–1.000000 | ok |
| 20 | 14 | 28 | l_thumb_finger1 | 38 | 0.004860–1.000000 | ok |
| 21 | 16 | 29 | l_thumb_finger2 | 31 | 0.004807–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 3, lane 0: weight=0.994140625, local=2, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.00579071044921875, local=3, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 17, lane 0: weight=0.634765625, local=2, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 17, lane 1: weight=0.002033233642578125, local=4, palette=5, transform=12, bone=l_shoulder_twist, status=ok
  - vertex 17, lane 2: weight=0.36328125, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1077, lane 0: weight=0.00495147705078125, local=15, palette=9, transform=17, bone=l_pinky_finger2, status=ok
  - vertex 1077, lane 1: weight=0.010772705078125, local=16, palette=10, transform=16, bone=l_pinky_finger1, status=ok
  - vertex 1077, lane 2: weight=0.9677734375, local=13, palette=15, transform=19, bone=l_ring_finger1, status=ok
  - vertex 1077, lane 3: weight=0.0163421630859375, local=14, palette=21, transform=20, bone=l_ring_finger2, status=ok

**Bone-index component 1**

- Active lanes: 2593
- Vertices with weight data: 1426
- Per-vertex influence distribution: 1 influence(s): 717 vertices, 2 influence(s): 312 vertices, 3 influence(s): 336 vertices, 4 influence(s): 61 vertices
- Per-vertex weight sum: min 0.999634, max 1.000286, mean 0.999995; 1426/1426 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 905 | 0.001030–1.000000 | ok |
| 1 | 0 | 18 | l_pinky_finger3 | 17 | 0.004452–1.000000 | ok |
| 2 | 2 | 13 | l_elbow | 279 | 0.001009–1.000000 | ok |
| 3 | 8 | 31 | l_hand_twist | 262 | 0.001519–1.000000 | ok |
| 4 | 5 | 12 | l_shoulder_twist | 290 | 0.001075–1.000000 | ok |
| 5 | 3 | 15 | l_hand | 157 | 0.001451–1.000000 | ok |
| 6 | 17 | 10 | l_clavicle | 150 | 0.001049–0.807129 | ok |
| 7 | 20 | 8 | chest | 99 | 0.019699–1.000000 | ok |
| 8 | 1 | 21 | l_ring_finger3 | 22 | 0.001063–1.000000 | ok |
| 9 | 19 | 30 | l_thumb_finger3 | 26 | 0.001535–1.000000 | ok |
| 10 | 4 | 27 | l_index_finger3 | 19 | 0.005951–1.000000 | ok |
| 11 | 7 | 24 | l_middle_finger3 | 18 | 0.093445–1.000000 | ok |
| 12 | 13 | 23 | l_middle_finger2 | 26 | 0.001813–1.000000 | ok |
| 13 | 15 | 19 | l_ring_finger1 | 33 | 0.003809–0.967773 | ok |
| 14 | 21 | 20 | l_ring_finger2 | 32 | 0.014557–1.000000 | ok |
| 15 | 9 | 17 | l_pinky_finger2 | 30 | 0.002796–1.000000 | ok |
| 16 | 10 | 16 | l_pinky_finger1 | 36 | 0.002289–1.000000 | ok |
| 17 | 18 | 22 | l_middle_finger1 | 50 | 0.001043–1.000000 | ok |
| 18 | 6 | 25 | l_index_finger1 | 42 | 0.003456–1.000000 | ok |
| 19 | 11 | 26 | l_index_finger2 | 31 | 0.001254–1.000000 | ok |
| 20 | 14 | 28 | l_thumb_finger1 | 38 | 0.004860–1.000000 | ok |
| 21 | 16 | 29 | l_thumb_finger2 | 31 | 0.004807–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 3, lane 0: weight=0.994140625, local=2, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.00579071044921875, local=3, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 17, lane 0: weight=0.634765625, local=2, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 17, lane 1: weight=0.002033233642578125, local=4, palette=5, transform=12, bone=l_shoulder_twist, status=ok
  - vertex 17, lane 2: weight=0.36328125, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1077, lane 0: weight=0.00495147705078125, local=15, palette=9, transform=17, bone=l_pinky_finger2, status=ok
  - vertex 1077, lane 1: weight=0.010772705078125, local=16, palette=10, transform=16, bone=l_pinky_finger1, status=ok
  - vertex 1077, lane 2: weight=0.9677734375, local=13, palette=15, transform=19, bone=l_ring_finger1, status=ok
  - vertex 1077, lane 3: weight=0.0163421630859375, local=14, palette=21, transform=20, bone=l_ring_finger2, status=ok

**Bone-index component 2**

- Active lanes: 2593
- Vertices with weight data: 1426
- Per-vertex influence distribution: 1 influence(s): 717 vertices, 2 influence(s): 312 vertices, 3 influence(s): 336 vertices, 4 influence(s): 61 vertices
- Per-vertex weight sum: min 0.999634, max 1.000286, mean 0.999995; 1426/1426 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 2377 | 0.001009–1.000000 | ok |
| 1 | 0 | 18 | l_pinky_finger3 | 216 | 0.001049–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 3, lane 0: weight=0.994140625, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 3, lane 1: weight=0.00579071044921875, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 17, lane 0: weight=0.634765625, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 17, lane 1: weight=0.002033233642578125, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 17, lane 2: weight=0.36328125, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1077, lane 0: weight=0.00495147705078125, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1077, lane 1: weight=0.010772705078125, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1077, lane 2: weight=0.9677734375, local=0, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1077, lane 3: weight=0.0163421630859375, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 22
- Matches / mismatches / unavailable: 22 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 1.7369378912945876e-07

### Mesh `3766176514` / LOD 2

- Skinning status: `ok`
- BoneInfo record: `2`
- Total resolved active lanes: 4059
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 1224 / 0

**Bone-index component 0**

- Active lanes: 408
- Vertices with weight data: 408
- Per-vertex influence distribution: 1 influence(s): 408 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 408/408 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 408 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

**Bone-index component 1**

- Active lanes: 408
- Vertices with weight data: 408
- Per-vertex influence distribution: 1 influence(s): 408 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 408/408 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 408 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

**Bone-index component 2**

- Active lanes: 408
- Vertices with weight data: 408
- Per-vertex influence distribution: 1 influence(s): 408 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 408/408 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 408 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

#### Section 1 / material 1

- Status: `ok`
- Resolved / unresolved: 2835 / 0

**Bone-index component 0**

- Active lanes: 945
- Vertices with weight data: 430
- Per-vertex influence distribution: 1 influence(s): 121 vertices, 2 influence(s): 134 vertices, 3 influence(s): 144 vertices, 4 influence(s): 31 vertices
- Per-vertex weight sum: min 0.999725, max 1.000271, mean 0.999999; 430/430 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 13 | l_elbow | 108 | 0.001065–1.000000 | ok |
| 1 | 8 | 31 | l_hand_twist | 107 | 0.004032–1.000000 | ok |
| 2 | 0 | 18 | l_pinky_finger3 | 8 | 0.004452–1.000000 | ok |
| 3 | 16 | 29 | l_thumb_finger2 | 10 | 0.062744–1.000000 | ok |
| 4 | 19 | 30 | l_thumb_finger3 | 10 | 0.004299–1.000000 | ok |
| 5 | 3 | 15 | l_hand | 68 | 0.001664–1.000000 | ok |
| 6 | 14 | 28 | l_thumb_finger1 | 13 | 0.004860–1.000000 | ok |
| 7 | 10 | 16 | l_pinky_finger1 | 18 | 0.009399–1.000000 | ok |
| 8 | 9 | 17 | l_pinky_finger2 | 13 | 0.004223–1.000000 | ok |
| 9 | 6 | 25 | l_index_finger1 | 20 | 0.010063–0.897949 | ok |
| 10 | 11 | 26 | l_index_finger2 | 19 | 0.002371–0.945312 | ok |
| 11 | 18 | 22 | l_middle_finger1 | 27 | 0.002827–0.931641 | ok |
| 12 | 1 | 21 | l_ring_finger3 | 12 | 0.003838–1.000000 | ok |
| 13 | 21 | 20 | l_ring_finger2 | 17 | 0.014557–1.000000 | ok |
| 14 | 15 | 19 | l_ring_finger1 | 17 | 0.019028–0.825195 | ok |
| 15 | 13 | 23 | l_middle_finger2 | 16 | 0.014153–1.000000 | ok |
| 16 | 12 | 11 | l_shoulder | 179 | 0.001369–0.829102 | ok |
| 17 | 7 | 24 | l_middle_finger3 | 10 | 0.093445–1.000000 | ok |
| 18 | 4 | 27 | l_index_finger3 | 12 | 0.005951–1.000000 | ok |
| 19 | 5 | 12 | l_shoulder_twist | 129 | 0.001342–0.997559 | ok |
| 20 | 20 | 8 | chest | 80 | 0.001417–1.000000 | ok |
| 21 | 17 | 10 | l_clavicle | 52 | 0.001030–0.807129 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 4, lane 0: weight=1.0, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 0: weight=0.99267578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.0070953369140625, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 9, lane 0: weight=0.3369140625, local=5, palette=3, transform=15, bone=l_hand, status=ok
  - vertex 9, lane 1: weight=0.55322265625, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 9, lane 2: weight=0.10968017578125, local=6, palette=14, transform=28, bone=l_thumb_finger1, status=ok
  - vertex 14, lane 0: weight=0.5146484375, local=5, palette=3, transform=15, bone=l_hand, status=ok
  - vertex 14, lane 1: weight=0.06512451171875, local=9, palette=6, transform=25, bone=l_index_finger1, status=ok
  - vertex 14, lane 2: weight=0.356689453125, local=6, palette=14, transform=28, bone=l_thumb_finger1, status=ok
  - vertex 14, lane 3: weight=0.0634765625, local=3, palette=16, transform=29, bone=l_thumb_finger2, status=ok

**Bone-index component 1**

- Active lanes: 945
- Vertices with weight data: 430
- Per-vertex influence distribution: 1 influence(s): 121 vertices, 2 influence(s): 134 vertices, 3 influence(s): 144 vertices, 4 influence(s): 31 vertices
- Per-vertex weight sum: min 0.999725, max 1.000271, mean 0.999999; 430/430 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 13 | l_elbow | 108 | 0.001065–1.000000 | ok |
| 1 | 8 | 31 | l_hand_twist | 107 | 0.004032–1.000000 | ok |
| 2 | 0 | 18 | l_pinky_finger3 | 8 | 0.004452–1.000000 | ok |
| 3 | 16 | 29 | l_thumb_finger2 | 10 | 0.062744–1.000000 | ok |
| 4 | 19 | 30 | l_thumb_finger3 | 10 | 0.004299–1.000000 | ok |
| 5 | 3 | 15 | l_hand | 68 | 0.001664–1.000000 | ok |
| 6 | 14 | 28 | l_thumb_finger1 | 13 | 0.004860–1.000000 | ok |
| 7 | 10 | 16 | l_pinky_finger1 | 18 | 0.009399–1.000000 | ok |
| 8 | 9 | 17 | l_pinky_finger2 | 13 | 0.004223–1.000000 | ok |
| 9 | 6 | 25 | l_index_finger1 | 20 | 0.010063–0.897949 | ok |
| 10 | 11 | 26 | l_index_finger2 | 19 | 0.002371–0.945312 | ok |
| 11 | 18 | 22 | l_middle_finger1 | 27 | 0.002827–0.931641 | ok |
| 12 | 1 | 21 | l_ring_finger3 | 12 | 0.003838–1.000000 | ok |
| 13 | 21 | 20 | l_ring_finger2 | 17 | 0.014557–1.000000 | ok |
| 14 | 15 | 19 | l_ring_finger1 | 17 | 0.019028–0.825195 | ok |
| 15 | 13 | 23 | l_middle_finger2 | 16 | 0.014153–1.000000 | ok |
| 16 | 12 | 11 | l_shoulder | 167 | 0.001030–0.829102 | ok |
| 17 | 7 | 24 | l_middle_finger3 | 10 | 0.093445–1.000000 | ok |
| 18 | 4 | 27 | l_index_finger3 | 12 | 0.005951–1.000000 | ok |
| 19 | 5 | 12 | l_shoulder_twist | 114 | 0.001342–0.997559 | ok |
| 20 | 20 | 8 | chest | 63 | 0.019699–1.000000 | ok |
| 21 | 17 | 10 | l_clavicle | 96 | 0.001369–0.807129 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 4, lane 0: weight=1.0, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 0: weight=0.99267578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.0070953369140625, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 9, lane 0: weight=0.3369140625, local=5, palette=3, transform=15, bone=l_hand, status=ok
  - vertex 9, lane 1: weight=0.55322265625, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 9, lane 2: weight=0.10968017578125, local=6, palette=14, transform=28, bone=l_thumb_finger1, status=ok
  - vertex 14, lane 0: weight=0.5146484375, local=5, palette=3, transform=15, bone=l_hand, status=ok
  - vertex 14, lane 1: weight=0.06512451171875, local=9, palette=6, transform=25, bone=l_index_finger1, status=ok
  - vertex 14, lane 2: weight=0.356689453125, local=6, palette=14, transform=28, bone=l_thumb_finger1, status=ok
  - vertex 14, lane 3: weight=0.0634765625, local=3, palette=16, transform=29, bone=l_thumb_finger2, status=ok

**Bone-index component 2**

- Active lanes: 945
- Vertices with weight data: 430
- Per-vertex influence distribution: 1 influence(s): 121 vertices, 2 influence(s): 134 vertices, 3 influence(s): 144 vertices, 4 influence(s): 31 vertices
- Per-vertex weight sum: min 0.999725, max 1.000271, mean 0.999999; 430/430 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 13 | l_elbow | 827 | 0.001030–1.000000 | ok |
| 1 | 8 | 31 | l_hand_twist | 118 | 0.001369–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 4, lane 0: weight=1.0, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 0: weight=0.99267578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.0070953369140625, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 9, lane 0: weight=0.3369140625, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 9, lane 1: weight=0.55322265625, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 9, lane 2: weight=0.10968017578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 14, lane 0: weight=0.5146484375, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 14, lane 1: weight=0.06512451171875, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 14, lane 2: weight=0.356689453125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 14, lane 3: weight=0.0634765625, local=0, palette=2, transform=13, bone=l_elbow, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 22
- Matches / mismatches / unavailable: 22 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 1.7369378912945876e-07

### Mesh `2627263307` / LOD 1

- Skinning status: `ok`
- BoneInfo record: `1`
- Total resolved active lanes: 9696
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 1551 / 0

**Bone-index component 0**

- Active lanes: 517
- Vertices with weight data: 517
- Per-vertex influence distribution: 1 influence(s): 517 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 517/517 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 517 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

**Bone-index component 1**

- Active lanes: 517
- Vertices with weight data: 517
- Per-vertex influence distribution: 1 influence(s): 517 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 517/517 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 517 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

**Bone-index component 2**

- Active lanes: 517
- Vertices with weight data: 517
- Per-vertex influence distribution: 1 influence(s): 517 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 517/517 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 12 | 11 | l_shoulder | 517 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=12, transform=11, bone=l_shoulder, status=ok

#### Section 1 / material 1

- Status: `ok`
- Resolved / unresolved: 8145 / 0

**Bone-index component 0**

- Active lanes: 2715
- Vertices with weight data: 1190
- Per-vertex influence distribution: 1 influence(s): 259 vertices, 2 influence(s): 425 vertices, 3 influence(s): 418 vertices, 4 influence(s): 88 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 0.999998; 1190/1190 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 13 | l_elbow | 380 | 0.001009–1.000000 | ok |
| 1 | 8 | 31 | l_hand_twist | 353 | 0.001519–1.000000 | ok |
| 2 | 0 | 18 | l_pinky_finger3 | 20 | 0.004452–1.000000 | ok |
| 3 | 12 | 11 | l_shoulder | 507 | 0.001049–0.898926 | ok |
| 4 | 5 | 12 | l_shoulder_twist | 403 | 0.001075–1.000000 | ok |
| 5 | 3 | 15 | l_hand | 201 | 0.001024–1.000000 | ok |
| 6 | 17 | 10 | l_clavicle | 126 | 0.001030–0.921387 | ok |
| 7 | 20 | 8 | chest | 152 | 0.001417–1.000000 | ok |
| 8 | 1 | 21 | l_ring_finger3 | 24 | 0.001063–1.000000 | ok |
| 9 | 19 | 30 | l_thumb_finger3 | 35 | 0.001275–1.000000 | ok |
| 10 | 4 | 27 | l_index_finger3 | 24 | 0.002491–1.000000 | ok |
| 11 | 7 | 24 | l_middle_finger3 | 21 | 0.035461–1.000000 | ok |
| 12 | 13 | 23 | l_middle_finger2 | 37 | 0.001813–1.000000 | ok |
| 13 | 21 | 20 | l_ring_finger2 | 46 | 0.001411–1.000000 | ok |
| 14 | 9 | 17 | l_pinky_finger2 | 33 | 0.001781–1.000000 | ok |
| 15 | 10 | 16 | l_pinky_finger1 | 41 | 0.002289–1.000000 | ok |
| 16 | 15 | 19 | l_ring_finger1 | 51 | 0.001549–0.967773 | ok |
| 17 | 18 | 22 | l_middle_finger1 | 69 | 0.001547–1.000000 | ok |
| 18 | 6 | 25 | l_index_finger1 | 58 | 0.001541–1.000000 | ok |
| 19 | 11 | 26 | l_index_finger2 | 39 | 0.002371–1.000000 | ok |
| 20 | 14 | 28 | l_thumb_finger1 | 48 | 0.004860–1.000000 | ok |
| 21 | 16 | 29 | l_thumb_finger2 | 47 | 0.004807–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 4, lane 0: weight=1.0, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 0: weight=0.9931640625, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.006855010986328125, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 28, lane 0: weight=0.9892578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 28, lane 1: weight=0.0015192031860351562, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 28, lane 2: weight=0.00930023193359375, local=3, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1366, lane 0: weight=0.00495147705078125, local=14, palette=9, transform=17, bone=l_pinky_finger2, status=ok
  - vertex 1366, lane 1: weight=0.010772705078125, local=15, palette=10, transform=16, bone=l_pinky_finger1, status=ok
  - vertex 1366, lane 2: weight=0.9677734375, local=16, palette=15, transform=19, bone=l_ring_finger1, status=ok
  - vertex 1366, lane 3: weight=0.0163421630859375, local=13, palette=21, transform=20, bone=l_ring_finger2, status=ok

**Bone-index component 1**

- Active lanes: 2715
- Vertices with weight data: 1190
- Per-vertex influence distribution: 1 influence(s): 259 vertices, 2 influence(s): 425 vertices, 3 influence(s): 418 vertices, 4 influence(s): 88 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 0.999998; 1190/1190 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 13 | l_elbow | 380 | 0.001009–1.000000 | ok |
| 1 | 8 | 31 | l_hand_twist | 353 | 0.001519–1.000000 | ok |
| 2 | 0 | 18 | l_pinky_finger3 | 20 | 0.004452–1.000000 | ok |
| 3 | 12 | 11 | l_shoulder | 507 | 0.001030–0.921387 | ok |
| 4 | 5 | 12 | l_shoulder_twist | 375 | 0.001075–1.000000 | ok |
| 5 | 3 | 15 | l_hand | 201 | 0.001024–1.000000 | ok |
| 6 | 17 | 10 | l_clavicle | 188 | 0.001049–0.807129 | ok |
| 7 | 20 | 8 | chest | 118 | 0.019699–1.000000 | ok |
| 8 | 1 | 21 | l_ring_finger3 | 24 | 0.001063–1.000000 | ok |
| 9 | 19 | 30 | l_thumb_finger3 | 35 | 0.001275–1.000000 | ok |
| 10 | 4 | 27 | l_index_finger3 | 24 | 0.002491–1.000000 | ok |
| 11 | 7 | 24 | l_middle_finger3 | 21 | 0.035461–1.000000 | ok |
| 12 | 13 | 23 | l_middle_finger2 | 37 | 0.001813–1.000000 | ok |
| 13 | 21 | 20 | l_ring_finger2 | 46 | 0.001411–1.000000 | ok |
| 14 | 9 | 17 | l_pinky_finger2 | 33 | 0.001781–1.000000 | ok |
| 15 | 10 | 16 | l_pinky_finger1 | 41 | 0.002289–1.000000 | ok |
| 16 | 15 | 19 | l_ring_finger1 | 51 | 0.001549–0.967773 | ok |
| 17 | 18 | 22 | l_middle_finger1 | 69 | 0.001547–1.000000 | ok |
| 18 | 6 | 25 | l_index_finger1 | 58 | 0.001541–1.000000 | ok |
| 19 | 11 | 26 | l_index_finger2 | 39 | 0.002371–1.000000 | ok |
| 20 | 14 | 28 | l_thumb_finger1 | 48 | 0.004860–1.000000 | ok |
| 21 | 16 | 29 | l_thumb_finger2 | 47 | 0.004807–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 4, lane 0: weight=1.0, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 0: weight=0.9931640625, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.006855010986328125, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 28, lane 0: weight=0.9892578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 28, lane 1: weight=0.0015192031860351562, local=1, palette=8, transform=31, bone=l_hand_twist, status=ok
  - vertex 28, lane 2: weight=0.00930023193359375, local=3, palette=12, transform=11, bone=l_shoulder, status=ok
  - vertex 1366, lane 0: weight=0.00495147705078125, local=14, palette=9, transform=17, bone=l_pinky_finger2, status=ok
  - vertex 1366, lane 1: weight=0.010772705078125, local=15, palette=10, transform=16, bone=l_pinky_finger1, status=ok
  - vertex 1366, lane 2: weight=0.9677734375, local=16, palette=15, transform=19, bone=l_ring_finger1, status=ok
  - vertex 1366, lane 3: weight=0.0163421630859375, local=13, palette=21, transform=20, bone=l_ring_finger2, status=ok

**Bone-index component 2**

- Active lanes: 2715
- Vertices with weight data: 1190
- Per-vertex influence distribution: 1 influence(s): 259 vertices, 2 influence(s): 425 vertices, 3 influence(s): 418 vertices, 4 influence(s): 88 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 0.999998; 1190/1190 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 13 | l_elbow | 2450 | 0.001009–1.000000 | ok |
| 1 | 8 | 31 | l_hand_twist | 265 | 0.001049–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 4, lane 0: weight=1.0, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 0: weight=0.9931640625, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 3, lane 1: weight=0.006855010986328125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 28, lane 0: weight=0.9892578125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 28, lane 1: weight=0.0015192031860351562, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 28, lane 2: weight=0.00930023193359375, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 1366, lane 0: weight=0.00495147705078125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 1366, lane 1: weight=0.010772705078125, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 1366, lane 2: weight=0.9677734375, local=0, palette=2, transform=13, bone=l_elbow, status=ok
  - vertex 1366, lane 3: weight=0.0163421630859375, local=0, palette=2, transform=13, bone=l_elbow, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 22
- Matches / mismatches / unavailable: 22 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 1.7369378912945876e-07

### Mesh `46827504` / LOD 0

- Skinning status: `ok`
- BoneInfo record: `0`
- Total resolved active lanes: 18888
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 17325 / 0

**Bone-index component 0**

- Active lanes: 5775
- Vertices with weight data: 2594
- Per-vertex influence distribution: 1 influence(s): 677 vertices, 2 influence(s): 910 vertices, 3 influence(s): 750 vertices, 4 influence(s): 257 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 1.000000; 2594/2594 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 6 | 13 | l_elbow | 669 | 0.001001–1.000000 | ok |
| 1 | 7 | 31 | l_hand_twist | 787 | 0.001204–1.000000 | ok |
| 3 | 5 | 11 | l_shoulder | 760 | 0.001049–0.898926 | ok |
| 4 | 24 | 12 | l_shoulder_twist | 619 | 0.001075–1.000000 | ok |
| 5 | 8 | 15 | l_hand | 642 | 0.001024–1.000000 | ok |
| 6 | 4 | 10 | l_clavicle | 179 | 0.001030–0.945312 | ok |
| 7 | 3 | 8 | chest | 190 | 0.001417–1.000000 | ok |
| 8 | 12 | 25 | l_index_finger1 | 186 | 0.001059–1.000000 | ok |
| 9 | 13 | 26 | l_index_finger2 | 138 | 0.001254–1.000000 | ok |
| 10 | 15 | 22 | l_middle_finger1 | 196 | 0.001043–1.000000 | ok |
| 11 | 16 | 23 | l_middle_finger2 | 109 | 0.001523–1.000000 | ok |
| 12 | 18 | 19 | l_ring_finger1 | 149 | 0.001160–1.000000 | ok |
| 13 | 19 | 20 | l_ring_finger2 | 121 | 0.001113–1.000000 | ok |
| 14 | 21 | 16 | l_pinky_finger1 | 139 | 0.001529–1.000000 | ok |
| 15 | 22 | 17 | l_pinky_finger2 | 111 | 0.001638–1.000000 | ok |
| 16 | 20 | 21 | l_ring_finger3 | 78 | 0.001063–1.000000 | ok |
| 17 | 23 | 18 | l_pinky_finger3 | 82 | 0.001320–1.000000 | ok |
| 18 | 17 | 24 | l_middle_finger3 | 70 | 0.035461–1.000000 | ok |
| 19 | 14 | 27 | l_index_finger3 | 78 | 0.002491–1.000000 | ok |
| 20 | 10 | 29 | l_thumb_finger2 | 154 | 0.001629–1.000000 | ok |
| 21 | 11 | 30 | l_thumb_finger3 | 125 | 0.001275–1.000000 | ok |
| 22 | 9 | 28 | l_thumb_finger1 | 193 | 0.001189–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 1, lane 0: weight=1.0, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 0, lane 0: weight=0.994140625, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 0, lane 1: weight=0.00579071044921875, local=1, palette=7, transform=31, bone=l_hand_twist, status=ok
  - vertex 16, lane 0: weight=0.00930023193359375, local=3, palette=5, transform=11, bone=l_shoulder, status=ok
  - vertex 16, lane 1: weight=0.9892578125, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 16, lane 2: weight=0.0015192031860351562, local=1, palette=7, transform=31, bone=l_hand_twist, status=ok
  - vertex 1292, lane 0: weight=0.03466796875, local=5, palette=8, transform=15, bone=l_hand, status=ok
  - vertex 1292, lane 1: weight=0.9296875, local=8, palette=12, transform=25, bone=l_index_finger1, status=ok
  - vertex 1292, lane 2: weight=0.01678466796875, local=9, palette=13, transform=26, bone=l_index_finger2, status=ok
  - vertex 1292, lane 3: weight=0.019012451171875, local=10, palette=15, transform=22, bone=l_middle_finger1, status=ok

**Bone-index component 1**

- Active lanes: 5775
- Vertices with weight data: 2594
- Per-vertex influence distribution: 1 influence(s): 677 vertices, 2 influence(s): 910 vertices, 3 influence(s): 750 vertices, 4 influence(s): 257 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 1.000000; 2594/2594 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 6 | 13 | l_elbow | 669 | 0.001001–1.000000 | ok |
| 1 | 7 | 31 | l_hand_twist | 787 | 0.001204–1.000000 | ok |
| 3 | 5 | 11 | l_shoulder | 782 | 0.001030–0.945312 | ok |
| 4 | 24 | 12 | l_shoulder_twist | 577 | 0.001075–1.000000 | ok |
| 5 | 8 | 15 | l_hand | 642 | 0.001024–1.000000 | ok |
| 6 | 4 | 10 | l_clavicle | 245 | 0.001049–0.807129 | ok |
| 7 | 3 | 8 | chest | 144 | 0.019699–1.000000 | ok |
| 8 | 12 | 25 | l_index_finger1 | 186 | 0.001059–1.000000 | ok |
| 9 | 13 | 26 | l_index_finger2 | 138 | 0.001254–1.000000 | ok |
| 10 | 15 | 22 | l_middle_finger1 | 196 | 0.001043–1.000000 | ok |
| 11 | 16 | 23 | l_middle_finger2 | 109 | 0.001523–1.000000 | ok |
| 12 | 18 | 19 | l_ring_finger1 | 149 | 0.001160–1.000000 | ok |
| 13 | 19 | 20 | l_ring_finger2 | 121 | 0.001113–1.000000 | ok |
| 14 | 21 | 16 | l_pinky_finger1 | 139 | 0.001529–1.000000 | ok |
| 15 | 22 | 17 | l_pinky_finger2 | 111 | 0.001638–1.000000 | ok |
| 16 | 20 | 21 | l_ring_finger3 | 78 | 0.001063–1.000000 | ok |
| 17 | 23 | 18 | l_pinky_finger3 | 82 | 0.001320–1.000000 | ok |
| 18 | 17 | 24 | l_middle_finger3 | 70 | 0.035461–1.000000 | ok |
| 19 | 14 | 27 | l_index_finger3 | 78 | 0.002491–1.000000 | ok |
| 20 | 10 | 29 | l_thumb_finger2 | 154 | 0.001629–1.000000 | ok |
| 21 | 11 | 30 | l_thumb_finger3 | 125 | 0.001275–1.000000 | ok |
| 22 | 9 | 28 | l_thumb_finger1 | 193 | 0.001189–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 1, lane 0: weight=1.0, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 0, lane 0: weight=0.994140625, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 0, lane 1: weight=0.00579071044921875, local=1, palette=7, transform=31, bone=l_hand_twist, status=ok
  - vertex 16, lane 0: weight=0.00930023193359375, local=3, palette=5, transform=11, bone=l_shoulder, status=ok
  - vertex 16, lane 1: weight=0.9892578125, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 16, lane 2: weight=0.0015192031860351562, local=1, palette=7, transform=31, bone=l_hand_twist, status=ok
  - vertex 1292, lane 0: weight=0.03466796875, local=5, palette=8, transform=15, bone=l_hand, status=ok
  - vertex 1292, lane 1: weight=0.9296875, local=8, palette=12, transform=25, bone=l_index_finger1, status=ok
  - vertex 1292, lane 2: weight=0.01678466796875, local=9, palette=13, transform=26, bone=l_index_finger2, status=ok
  - vertex 1292, lane 3: weight=0.019012451171875, local=10, palette=15, transform=22, bone=l_middle_finger1, status=ok

**Bone-index component 2**

- Active lanes: 5775
- Vertices with weight data: 2594
- Per-vertex influence distribution: 1 influence(s): 677 vertices, 2 influence(s): 910 vertices, 3 influence(s): 750 vertices, 4 influence(s): 257 vertices
- Per-vertex weight sum: min 0.999634, max 1.000366, mean 1.000000; 2594/2594 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 6 | 13 | l_elbow | 5411 | 0.001001–1.000000 | ok |
| 1 | 7 | 31 | l_hand_twist | 364 | 0.001030–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 1, lane 0: weight=1.0, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 0, lane 0: weight=0.994140625, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 0, lane 1: weight=0.00579071044921875, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 16, lane 0: weight=0.00930023193359375, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 16, lane 1: weight=0.9892578125, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 16, lane 2: weight=0.0015192031860351562, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 1292, lane 0: weight=0.03466796875, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 1292, lane 1: weight=0.9296875, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 1292, lane 2: weight=0.01678466796875, local=0, palette=6, transform=13, bone=l_elbow, status=ok
  - vertex 1292, lane 3: weight=0.019012451171875, local=0, palette=6, transform=13, bone=l_elbow, status=ok

#### Section 1 / material 1

- Status: `ok`
- Resolved / unresolved: 1563 / 0

**Bone-index component 0**

- Active lanes: 521
- Vertices with weight data: 521
- Per-vertex influence distribution: 1 influence(s): 521 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 521/521 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 5 | 11 | l_shoulder | 521 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 2594, lane 0: weight=1.0, local=0, palette=5, transform=11, bone=l_shoulder, status=ok

**Bone-index component 1**

- Active lanes: 521
- Vertices with weight data: 521
- Per-vertex influence distribution: 1 influence(s): 521 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 521/521 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 5 | 11 | l_shoulder | 521 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 2594, lane 0: weight=1.0, local=0, palette=5, transform=11, bone=l_shoulder, status=ok

**Bone-index component 2**

- Active lanes: 521
- Vertices with weight data: 521
- Per-vertex influence distribution: 1 influence(s): 521 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 521/521 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 5 | 11 | l_shoulder | 521 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 2594, lane 0: weight=1.0, local=0, palette=5, transform=11, bone=l_shoulder, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 22
- Matches / mismatches / unavailable: 22 / 0 / 0
- Unreferenced palette slots: 3
- Maximum absolute error: 1.7369378912945876e-07


## Decoder limitations

- Vertex-local bone indices are resolved through the active LOD BoneInfo palette and section material Remap into TransformInfo indices; raw bind-pose matrices remain uninterpreted.
- Format 29 is decoded as three normalized bone weights only when the component type is bone_weight; other Format 29 semantics remain raw.
- Transform matrices are emitted as raw stored floats; matrix convention and bind-pose semantics are not changed or inferred.

> This report aggregates influence data by section and local index. Use --include-all-influences only for a full per-lane dump.
