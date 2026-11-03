# Helldivers 2 resource format notes — 0.8.1-alpha

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

It reports mismatches between the declared name count, hash count and parsed string count. This milestone does not yet resolve the full Unit TransformInfo hierarchy or BoneInfo index palettes.

## DSAR and SLIM containers

The observed DSAR structure has a chunk count at byte offset `0x08`, followed by 32-byte chunk descriptors beginning at `0x20`. A descriptor is read as `<QQIIBB6x`: uncompressed offset, compressed offset, uncompressed size, compressed size, compression type, chunk flags, and six reserved bytes.

The implemented compression types are `0` (uncompressed) and `3` (raw LZ4 block). Chunk flag `0x02` is treated as the start of a new resource when reading a resource spanning multiple chunks. This is the observed interpretation used by the SDK's DSAR reader.

For SLIM installs, the inspector reads `bundles.nxa` metadata to map package names and archive offsets to bundle indices and uncompressed bundle offsets, then retrieves the matching byte ranges from `bundles.XX.nxa` files. It also looks for companion package names ending in `.gpu_resources` and `.stream`.

## Explicit limitations

- The inspector has not yet been run against an original HD2 game-data directory in this environment.
- It does not yet resolve package dependencies globally across all archives.
- It does not decode CompositeUnit geometry, TransformInfo matrices, MeshInfo sections, StreamInfo component layouts, GPU vertex/index buffers, BoneInfo inverse-bind matrices or material-specific bone remaps.
- It does not write or modify HD2 resources, Archives or Patches.


## Package catalog (0.8.1-alpha)

For SLIM installs, `hd2-catalog` enumerates package names from `bundles.nxa` and reads only each package's 72-byte TOC header and type-table range. It does not reconstruct `.gpu_resources` or `.stream` packages during this first-pass census. Candidate archives are those whose type table declares Unit, Bones, or CompositeUnit resources. This is an archive-discovery aid, not a full dependency resolver: Unit payload references and cross-archive ownership still require inspecting candidate packages individually.
