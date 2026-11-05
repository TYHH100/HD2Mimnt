# HD2 Unit Skinning Audit

- Unit: `210fb2f60f8308ea`
- Package: `8670598c1f4462dc`
- Geometry source: `Unit GPU payload`

## Unit summary

| Metric | Value |
|---|---:|
| Transforms | 16 |
| BoneInfo records | 4 |
| Meshes | 5 |
| Decoded meshes | 5 |
| Vertices | 3498 |
| Indices | 17670 |
| Triangles | 5890 |

## BoneInfo palettes

| Record | Bone entries | RealIndices entries | Material remaps |
|---:|---:|---:|---:|
| 0 | 4 | 4 | 1 |
- BoneInfo[0] Remap[0]: 2 entries, 2 unique palette slots, valid references 2/2, range 2..3.
| 1 | 2 | 2 | 1 |
- BoneInfo[1] Remap[0]: 2 entries, 2 unique palette slots, valid references 2/2, range 0..1.
| 2 | 2 | 2 | 1 |
- BoneInfo[2] Remap[0]: 2 entries, 2 unique palette slots, valid references 2/2, range 0..1.
| 3 | 2 | 2 | 1 |
- BoneInfo[3] Remap[0]: 2 entries, 2 unique palette slots, valid references 2/2, range 0..1.

## Mesh / section resolution

| Mesh | LOD | BoneInfo | Section | Material | Status | Vertices | Resolved lanes | Unresolved lanes |
|---|---:|---:|---:|---:|---|---:|---:|---:|
| `2607622032` | 3 | 3 | 0 | 0 | ok | 608 | 608 | 0 |
| `524905431` | 2 | 2 | 0 | 0 | ok | 339 | 339 | 0 |
| `3301214708` | 1 | 1 | 0 | 0 | ok | 738 | 738 | 0 |
| `562172190` | 0 | 0 | 0 | 0 | ok | 1374 | 1374 | 0 |

## Active TransformInfo usage

| Transform | Bone | Active lanes | Parent |
|---:|---|---:|---|
| 14 | r_thigh | 1532 | hips |
| 15 | l_thigh | 1527 | hips |

## Detailed section mapping

### Mesh `2607622032` / LOD 3

- Skinning status: `ok`
- BoneInfo record: `3`
- Total resolved active lanes: 608
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 608 / 0

**Bone-index component 0**

- Active lanes: 608
- Vertices with weight data: 608
- Per-vertex influence distribution: 1 influence(s): 608 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 608/608 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 1 | 14 | r_thigh | 303 | 1.000000–1.000000 | ok |
| 1 | 0 | 15 | l_thigh | 305 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=1, transform=14, bone=r_thigh, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 2
- Matches / mismatches / unavailable: 2 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 1.1821749723583963e-07

### Mesh `524905431` / LOD 2

- Skinning status: `ok`
- BoneInfo record: `2`
- Total resolved active lanes: 339
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 339 / 0

**Bone-index component 0**

- Active lanes: 339
- Vertices with weight data: 339
- Per-vertex influence distribution: 1 influence(s): 339 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 339/339 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 0 | 15 | l_thigh | 165 | 1.000000–1.000000 | ok |
| 1 | 1 | 14 | r_thigh | 174 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=0, transform=15, bone=l_thigh, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 2
- Matches / mismatches / unavailable: 2 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 1.1821749723583963e-07

### Mesh `3301214708` / LOD 1

- Skinning status: `ok`
- BoneInfo record: `1`
- Total resolved active lanes: 738
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 738 / 0

**Bone-index component 0**

- Active lanes: 738
- Vertices with weight data: 738
- Per-vertex influence distribution: 1 influence(s): 738 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 738/738 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 0 | 15 | l_thigh | 370 | 1.000000–1.000000 | ok |
| 1 | 1 | 14 | r_thigh | 368 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=0, transform=15, bone=l_thigh, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 2
- Matches / mismatches / unavailable: 2 / 0 / 0
- Unreferenced palette slots: 0
- Maximum absolute error: 1.1821749723583963e-07

### Mesh `562172190` / LOD 0

- Skinning status: `ok`
- BoneInfo record: `0`
- Total resolved active lanes: 1374
- Total unresolved active lanes: 0

#### Section 0 / material 0

- Status: `ok`
- Resolved / unresolved: 1374 / 0

**Bone-index component 0**

- Active lanes: 1374
- Vertices with weight data: 1374
- Per-vertex influence distribution: 1 influence(s): 1374 vertices
- Per-vertex weight sum: min 1.000000, max 1.000000, mean 1.000000; 1374/1374 within 1e-3 of 1.0.

| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |
|---:|---:|---:|---|---:|---:|---|
| 0 | 2 | 15 | l_thigh | 687 | 1.000000–1.000000 | ok |
| 1 | 3 | 14 | r_thigh | 687 | 1.000000–1.000000 | ok |
- Mapping conflicts: none detected.

**Representative influence rows**

  - vertex 0, lane 0: weight=1.0, local=0, palette=2, transform=15, bone=l_thigh, status=ok

**BoneInfo matrix consistency check**

- Status: `matches`
- Candidate formula: `mesh_world_times_inverse_bone_world`
- Vector convention candidate: `row_vector`
- Active palette slots: 2
- Matches / mismatches / unavailable: 2 / 0 / 0
- Unreferenced palette slots: 2
- Maximum absolute error: 1.1821749723583963e-07

### Mesh `2249734937` / LOD -1

- Skinning status: `no_skin_attributes`
- BoneInfo record: `None`
- Total resolved active lanes: 0
- Total unresolved active lanes: 0


## Decoder limitations

- Vertex-local bone indices are resolved through the active LOD BoneInfo palette and section material Remap into TransformInfo indices; raw bind-pose matrices remain uninterpreted.
- Format 29 is decoded as three normalized bone weights only when the component type is bone_weight; other Format 29 semantics remain raw.
- Transform matrices are emitted as raw stored floats; matrix convention and bind-pose semantics are not changed or inferred.

> This report aggregates influence data by section and local index. Use --include-all-influences only for a full per-lane dump.
