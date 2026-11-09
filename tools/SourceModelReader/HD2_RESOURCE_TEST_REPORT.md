# 0.8.1-alpha HD2 Resource Inspector Test Report

## Automated regression

Command:

```powershell
py -3 -m unittest discover -s tests -v
```

Result in the development container: **64/64 tests passed**.

The 0.8.1 additions cover:

- SLIM package catalog scanning from the `bundles.nxa` package index.
- Bounded TOC header and type-table reads without reconstructing GPU/stream sidecars.
- Package summaries for Unit, Bones, CompositeUnit, Material and Texture resources.
- Exclusion of `.gpu_resources` and `.stream` sidecar package names from archive candidates.
- Partial reads crossing DSAR bundle-resource chunk boundaries.
- `hd2-catalog` CLI JSON report generation and optional package scan limit.
- Expanded human-readable type labels for resource IDs documented by the public SDK.

## Source-side regression

The supplied `3647656951.zip` was opened directly and `aes_yz_pm.mdl` was inspected. The reader reported MDL v48, VVD v4, VTX v7, checksum 43233072, 255 MDL bone definitions, and 20 BodyGroups.

The unified GLB regression still reports 64,301 vertices, 106,663 triangles, 14 material primitives, 255 Skin joints, and zero bad weight sums. The independent GLB validator returned `OK`.

## Local HD2 acceptance still required

The user's 0.8.0 test inspected only package `9ba626afa44a3aa3`. That package contained 4,052 resources, 3 Units, and no Bones resources; its three Unit records had no Bones, CompositeUnit or StateMachine references. This is evidence about that package only, not the full game.

No original HD2 game-data directory was mounted in the development container. Therefore the new 0.8.1 catalog has passed synthetic SLIM fixtures but has not yet been run against the user's full installed game data. The next local test should run `hd2-catalog` against the entire `data` directory and then inspect one candidate archive containing Unit/Bones/CompositeUnit resources.

## Current parser boundary

Implemented: legacy/DSAR/SLIM package indexing, TOC inventory, single-package Unit header references, material resource IDs, Unit table counts, and Bones names/hashes/LOD metadata. The new catalog reads package TOC headers/type tables only.

Not implemented: global cross-archive reference resolution, full CompositeUnit geometry resolution, TransformInfo decoding, MeshInfo/StreamInfo record decoding, GPU vertex/index buffers, per-material BoneInfo `Remaps`, inverse bind matrices, mesh reconstruction, target rig deformation checks, or writing modified game resources.
