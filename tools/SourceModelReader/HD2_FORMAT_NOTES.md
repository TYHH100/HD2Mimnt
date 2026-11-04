# Helldivers 2 resource format notes — 0.8.4-alpha

These notes describe the structures implemented by the independent read-only inspector. They are based on the publicly available parser structure in the HD2 SDK projects below. They are **not** a claim that every game build or every resource variant has been validated against original game files.

## Reference implementations studied

- [HD2SDK Community Edition](https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition)
- [HD2SDK AQ Edition](https://github.com/Estecsky/HD2SDK-AQ-Edition)
- AQ `TocEntry` / `StreamToc` and resource loaders: [`__init__.py`](https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/__init__.py)
- AQ archive and SLIM reconstruction: [`utils/slim.py`](https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/utils/slim.py)
- AQ Unit / GPU / mesh structures: [`stingray/unit.py`](https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/unit.py)
- AQ Bones resource: [`stingray/bones.py`](https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/stingray/bones.py)
- AQ resource type constants: [`utils/constants.py`](https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/main/utils/constants.py)

This project has its own parser and data model. It does not import the SDK modules or copy their implementation code.

## Legacy package TOC

The observed legacy TOC begins with a 72-byte header:

| Offset | Field | Type |
|---:|---|---|
| `0x00` | Magic (`0xF0000011`) | `uint32` |
| `0x04` | Type count | `uint32` |
| `0x08` | Resource entry count | `uint32` |
| `0x0C` | Unknown | `uint32` |
| `0x10` | Reserved bytes | 56 bytes |

Each type-table entry is 32 bytes (`<QQQII`): unknown `uint64`, TypeID `uint64`, resource count `uint64`, and two `uint32` values.

Each resource-table entry is 80 bytes (`<7Q6I`):

1. FileID
2. TypeID
3. TOC payload offset
4. Stream payload offset
5. GPU payload offset
6. Unknown `uint64`
7. Unknown `uint64`
8. TOC payload size
9. Stream payload size
10. GPU payload size
11. Unknown `uint32`
12. Unknown `uint32`
13. Entry index

The resource payloads are stored in the package TOC file, the `.gpu_resources` sidecar, and the `.stream` sidecar. Offsets are relative to their respective files.

## Resource type IDs implemented

These 64-bit IDs are recorded in AQ Edition's constants table:

| Resource | Type ID (hex) |
|---|---|
| CompositeUnit | `c4f0f4be7fb0c8d6` |
| Unit | `e0a48d0be9a7453f` |
| Texture | `cd4238c6a0c69e32` |
| Material | `eac0b497876adedf` |
| Bones | `18dead01056b72e9` |
| Animation | `931e336d7646cc26` |
| StateMachine | `a486d4045106165c` |
| Physics | `5f7203c8f280dab8` |

The inspector keeps unknown TypeIDs instead of discarding them.

## Unit header subset

The Unit parser reads the 128-byte header subset exposed by AQ's `StingrayMeshFile.Serialize`:

- Five initial `uint64` references: two unknown references, `BonesRef`, `CompositeRef`, and `StateMachineRef` (in their on-disk order: unknown, Bones, Composite, unknown, StateMachine).
- `HeaderData1` and Unit version (`uint32` each).
- LOD-group, TransformInfo, light, callback and other section offsets.
- Customization, connecting-bone-hash, BoneInfo, StreamInfo, ending, MeshInfo and Materials offsets.

The inspector currently decodes the direct Bones, CompositeUnit, StateMachine and material IDs. It reads the first count field at BoneInfo, StreamInfo and MeshInfo offsets, except that `StreamInfoOffset == 1` is treated as the external CompositeUnit stream sentinel rather than a byte offset.

The Unit material table is read as a `uint32` count, followed by that many `uint32` section IDs and that many `uint64` material resource IDs.

## Bones resource subset

The Bones reader consumes:

- `NumNames` and `NumLODLevels` (`uint32`)
- `NumLODLevels` float values
- `NumNames` `uint32` bone hashes
- `NumLODLevels` `uint32` LOD levels
- Null-terminated UTF-8 bone names

It reports mismatches between the declared name count, hash count and parsed string count. The 0.8.2 structural reader preserved TransformInfo and BoneInfo tables; the 0.8.3 resolver now maps named transforms and material-scoped vertex palettes as described above.

## Transform hierarchy and active skin-matrix diagnostics (0.8.4-alpha)

The reader builds a candidate local 4x4 matrix from each TransformInfo record's 3x3 rotation, 3D position and scale. It then walks the parent graph and composes:

`World[i] = Local[i] × World[parent[i]]`

Root entries are those with a self-parent index or `0xffff`. Matrices are treated as row-major 4x4 values acting on row vectors. The computed world matrix is compared with the stored `matrix_4x4_raw`; the raw data is never overwritten. Invalid parents, cycles, incomplete local fields and singular matrices remain visible as unresolved diagnostics.

For each skinned mesh, the reader gathers BoneInfo palette slots actually referenced by positive-weight vertex lanes. For every such slot it compares the stored matrix with:

`MeshWorld × inverse(BoneWorld)`

Here `MeshWorld` is the world matrix at the MeshInfo TransformIndex, and `BoneWorld` is the world matrix at the slot's TransformInfo index. This is an observed mesh-space matrix relationship, not a final statement about the engine's semantic name for the matrix. Slots never used by a positive-weight lane are counted as unreferenced and intentionally excluded from the active-slot pass/fail result.

The diagnostics are emitted at:
- `transform_info.hierarchy_matrix_validation`
- `geometry[].skinning.matrix_validation`

### Three real-game sample audit

| Sample | TransformInfo hierarchy comparisons | Actively used BoneInfo slots |
|---|---:|---:|
| FS-05 lower-body Unit | 16 / 16 | 8 / 8 |
| FS-05 torso/left-arm Unit | 38 / 38 | 93 / 93 |
| Avatar CompositeUnit Unit | 107 / 107 | 61 / 61 |
| **Total** | **161 / 161** | **162 / 162** |

The current local linear-matrix builder uses `rotation_3x3 × diag(scale)`. The only non-unit scale in these samples belongs to `attach_cam_1`, whose rotation matrix is identity. Therefore, the samples do not discriminate the multiplication order for a transform that combines non-uniform scale with non-identity rotation; the report explicitly flags this as unverified. If a future Unit contains that combination, the validator leaves the affected transform unresolved rather than selecting an unverified order.

### Vertex-weighted transform classification

The resolver now annotates every `resolved_skeleton[]` entry with:
- `has_active_vertex_influence`: whether at least one positive-weight lane resolves to this TransformInfo index.
- `active_vertex_influence_lane_count`: number of resolved positive-weight lane occurrences across the decoded sections and independent `bone_index` component sets.

It also emits `transform_info.weight_usage_summary`, including total/named/unmatched TransformInfo counts, distinct actively weighted transform counts split by whether the name resolved, and resolved/unresolved active-lane totals. These lane totals are **not** unique vertex counts; repeated `bone_index` sets are kept separately.

On the Avatar sample, 107 TransformInfo entries contain 90 named entries and 17 unnamed/hash-only entries. Its BoneInfo palette has 88 unique TransformInfo targets, but only 61 distinct named transforms are actually referenced by positive-weight lanes in this mesh; 27 palette slots are not referenced by active vertex weights. This is a mesh-specific usage classification, not a claim that all named transforms across the Unit are deformers.

Maximum absolute error was approximately (2.58×10^{-7}) for the TransformInfo hierarchy comparisons and (1.74×10^{-7}) for the active BoneInfo palette comparisons. Both are below the implementation's (10^{-4}) diagnostic tolerance.

The Avatar BoneInfo record contains 27 palette slots not referenced by positive-weight lanes in this mesh. Two of those unreferenced slots, `aim_weapon` and `backpack`, have stored matrices that do not satisfy the comparison; because no active vertex lane uses them, they are reported as unreferenced rather than failed active skin matrices. The extra FS-05 lower-body mesh with LOD -1 has no skin attributes and is not included in the skinned-mesh comparison.

This validates the hierarchy composition and matrix relationship for these selected samples only. It does not yet prove correct animated deformation, all Unit variants, or complete Source-to-HD2 bind-space adaptation.

## DSAR and SLIM containers

The observed DSAR structure has a chunk count at byte offset `0x08`, followed by 32-byte chunk descriptors beginning at `0x20`. A descriptor is read as `<QQIIBB6x`: uncompressed offset, compressed offset, uncompressed size, compressed size, compression type, chunk flags, and six reserved bytes.

The implemented compression types are `0` (uncompressed) and `3` (raw LZ4 block). Chunk flag `0x02` is treated as the start of a new resource when reading a resource spanning multiple chunks. This is the observed interpretation used by the SDK's DSAR reader.

For SLIM installs, the inspector reads `bundles.nxa` metadata to map package names and archive offsets to bundle indices and uncompressed bundle offsets, then retrieves the matching byte ranges from `bundles.XX.nxa` files. It also looks for companion package names ending in `.gpu_resources` and `.stream`.

## Packed vertex formats and real-game validation (0.8.2.1-alpha)

The decoder now has semantic handling for two packed formats in Unit version 10800438:

### Format 30 — octahedral normal

For a component whose semantic type is `normal`, the low ten bits and the next ten bits are decoded as two unsigned normalized coordinates and remapped to [-1, 1]. The third coordinate is reconstructed as `1 - abs(x) - abs(y)`; the lower hemisphere is folded, and the resulting vector is normalized.

The upper twelve bits are not used by this normal representation. They remain intact in the original raw uint32 stored in the JSON attribute's `values` array. The decoded vector is stored additively in `decoded_values`, with `decoded_semantics = "octahedral_normal"`.

### Format 29 — 10:10:10:2 packed value

The real FS-05 torso/left-arm LOD 4 sample identifies Format 29 as component type `bone_weight`. For this semantic, the low three ten-bit values are decoded as unsigned normalized values by dividing each by 1023. The high two bits are retained in the raw packed value; the fourth decoded influence is zero under the decoder convention observed in the AQ SDK.

The JSON adds `decoded_semantics = "unorm10_10_10_2_bone_weights"` and a four-value `decoded_values` array. This interpretation is gated on component type `bone_weight`; other Format 29 component types remain raw and produce a warning.

### User-provided real sample checks

| Sample | Format 30 normals | Format 29 bone weights |
|---|---:|---:|
| FS-05 lower-body Unit `210fb2f60f8308ea` | 3,498 vertices | None |
| FS-05 torso/left-arm Unit `324fb206612ce4f7` | 7,525 vertices | 439 vertices |
| Avatar CompositeUnit `4d1c334d294dfa97` | 27,355 vertices | None |
| **Total** | **38,378 vertices** | **439 vertices** |

Across all 38,378 decoded Format 30 normals, every component was finite and every resulting vector had length 1 within floating-point precision. In the 439 Format 29 weights, the high two bits were 3 for all vertices, the fourth decoded influence was zero, and the sum of the three decoded weights ranged from 0.998045 to 1.000000.

The three sample exports also confirmed that the Format 30 upper twelve bits vary substantially; the decoder must not treat those bits as part of the normal's X/Y coordinates.

These checks validate decoding and data consistency. They do not constitute a visual comparison against an independent renderer, nor do they establish bind-pose matrix conventions.

## Named skeleton and skinning resolution (0.8.3-alpha)

The reader resolves the following chain independently for each MeshInfo material section:

`vertex-local bone index -> BoneInfo.Remaps[material_index] -> BoneInfo.RealIndices -> TransformInfo -> Bones.name_hash`

The selected BoneInfo record follows the MeshInfo LOD index. LOD -1 selects the final BoneInfo record, matching the SDK's negative-index lookup behavior when skin attributes are present.

TransformInfo entries are matched to Bones records by exact 32-bit name hash. The report preserves every TransformInfo entry, including helper entries without a Bones name. It adds a convenience parent index/name while retaining the original raw parent field. A self-parenting root is represented as having no resolved parent; its raw value remains unchanged.

BoneInfo matrix i and RealIndices[i] share the same palette slot in the SDK data model. The 0.8.3 report attaches each raw 4x4 matrix to that palette slot. The 0.8.4 diagnostics add a sample-validated matrix comparison, described below, without replacing the raw values or asserting a universal inverse-bind semantic.

Each bone_index component index is an independent set. This is important for legacy/grouped layouts: the reader does not combine component sets or pretend that repeated sets are a single larger influence array. Each set resolves the active lanes against the section's material-specific Remap and emits vertex-local influences with the source lane, weight, local index, palette slot, TransformInfo index and Bones name.

Zero-weight lanes remain represented in the raw vertex attributes and aligned transform-index arrays, but do not count as unresolved active influences. Missing mappings on positive-weight lanes produce a partial skinning status and a diagnostic.

### Real sample audit

| Sample | TransformInfo names matched | Active influence lanes | Unresolved active lanes |
|---|---:|---:|---:|
| FS-05 lower-body Unit | 15 / 16 | 3,059 | 0 |
| FS-05 torso/left-arm Unit | 38 / 38 | 43,038 | 0 |
| Avatar CompositeUnit Unit | 90 / 107 | 42,158 | 0 |

The active lane counts include separate bone_index component sets independently; they must not be summed as though each set were an additional simultaneous influence stream. The 17 unmatched Avatar TransformInfo entries are not used by any active influence in this sample.

This audit validates hash, palette, remap and active-lane range consistency. It does not validate mesh deformation, matrix convention, inverse-bind semantics or Source-to-HD2 retargeting.

## Explicit limitations

- Real-game structural acceptance has been completed locally for two FS-05 Units and one Avatar Unit backed by a CompositeUnit. The packed-format decoders added in 0.8.2.1 were checked against the same three JSON outputs.
- It does not yet resolve package dependencies globally across all archives.
- BoneInfo matrices, RealIndices and material Remap arrays are resolved into named per-vertex influences. The 0.8.4 diagnostic compares actively used matrices with the observed mesh-space matrix relationship, but does not retarget them to an Avatar/target skeleton or alter the raw values.
- Format 30 octahedral normals are decoded when the component type is `normal`; the original uint32 remains in `values` and the unit vector is added in `decoded_values`. The upper twelve bits are not used by this format.
- Format 29 is decoded as three unsigned-normalized 10-bit bone weights only when the component type is `bone_weight`; the fourth decoded influence is zero under the reviewed AQ decoder convention. The original packed uint32 remains in `values`. Other component semantics remain raw and warn rather than being guessed.
- Transform matrices are emitted as stored floats. The 0.8.4 sample-based diagnostic supports a row-vector/local-times-parent-world layout and a mesh-space BoneInfo matrix relationship for the inspected samples; universal Unit coverage, bind-pose semantics and inverse-bind meaning remain unproven.
- It does not write or modify HD2 resources, Archives or Patches.


## Package catalog (0.8.1-alpha)

For SLIM installs, `hd2-catalog` enumerates package names from `bundles.nxa` and reads only each package's 72-byte TOC header and type-table range. It does not reconstruct `.gpu_resources` or `.stream` packages during this first-pass census. Candidate archives are those whose type table declares Unit, Bones, or CompositeUnit resources. This is an archive-discovery aid, not a full dependency resolver: Unit payload references and cross-archive ownership still require inspecting candidate packages individually.


## Unit and GPU geometry subset (0.8.2-alpha)

The `hd2-unit` command reads one Unit resource by FileID and produces a neutral JSON report. It does not modify the original package or write a model Patch.

### TransformInfo

The parser preserves the 12-byte reserved area after the transform count, then reads parallel arrays for each transform: a 64-byte local transform, a 64-byte matrix, a 4-byte pair of 16-bit increment/parent values, and a 32-bit name hash. Matrix values are reported in stored order. Parent indices and hashes are not remapped or reinterpreted.

### BoneInfo

The Unit BoneInfo table is a count plus a relative-offset array. Each record begins with four 32-bit fields: bone count, matrix offset, RealIndices offset and FakeIndices offset. The reader preserves the per-record 4x4 matrices, RealIndices and per-material Remap arrays. Remap arrays are stored as indices into RealIndices; this alpha validates their ranges but does not resolve them through TransformInfo/Bones names.

### StreamInfo and MeshInfo

StreamInfo records provide component descriptors, current component format IDs, vertex/index buffer IDs, counts, strides and buffer offsets/sizes. Component byte offsets are calculated only while all preceding component sizes are known. If an unknown component format occurs, subsequent component offsets are left unresolved rather than guessed.

MeshInfo records preserve the mesh identifier, TransformIndex, LOD index, stream index, material slot IDs and section vertex/index ranges. The decoder checks that component ranges fit VertexStride and that vertex/index ranges fit the declared GPU buffers.

For Unit version 10800438, the current component decoder recognizes float32, float16, uint8, uint32 and RGBA8 vector/scalar formats. Format 30 octahedral normals and Format 29 bone weights are decoded only when their component semantics match; the original packed words remain in the raw `values` arrays. Index buffers support 16-bit and 32-bit entries.

### CompositeUnit

When a Unit references a CompositeUnit, the reader finds the Unit row by its Unit hash, uses its mesh-ID map to resolve each MeshInfo record to a shared mesh layout, and reads the shared StreamInfo/GPU payload from the CompositeUnit resource. This path is used by the Avatar sample; the FS-05 sample uses direct Unit GPU payloads.

The decoder's JSON is intended as an inspectable intermediate representation. It is not yet an export-ready skinned model: BoneInfo palette/name resolution, influence interpretation, bind-pose convention validation and Source-to-HD2 retargeting remain later milestones.
