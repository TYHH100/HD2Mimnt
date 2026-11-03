# Source Model Reader — Version Roadmap

This roadmap defines milestones, not a promise that every minor number must be released. A version is cut when its acceptance checks are met; fixes may add a patch/minor release, while a substantial new capability advances the feature milestone.

## 0.6.x — Source structure and audit hardening

**Completed baseline: 0.6.2-alpha**

- Read SMD node and skeleton records.
- Compare slice skeleton structure and reference poses where semantically appropriate.
- Map SMD node names/hierarchy to compiled MDL bone definitions.
- Report sparse animation transform coverage without inventing missing values.
- Continue regression against the known GMA/MDL/VVD/VTX sample and multiple SMD roles.

Exit criteria: parser warnings distinguish malformed/incomplete reference data from intentionally sparse animation records; test suite covers real-world file-role edge cases.

## 0.7.0 — Source animation data

**Completed baseline: 0.7.0-alpha**

- Parse multi-frame SMD skeleton sections into per-bone tracks, preserving authored frame indices and sparse-key information.
- Require explicit FPS input; do not guess frame rate from SMD alone.
- Export a selected unified skinned character scene plus one SMD animation clip to GLB.
- Map by bone name and verify parent-name hierarchy before export.
- Write dense tracks as LINEAR and sparse tracks as STEP in `auto` mode; all sparse data remains explicit and is not forward-filled.
- Independently validate animation accessors, time ordering, output types/counts, channel targets, and quaternion normalization.
- Keep unsupported Source animation controllers, procedural bones, IK, flexes, and engine-specific runtime behavior visible as limitations.

Exit criteria: a real known animation is imported into Blender via GLB, scrubbed, and visually compared against BlenderSourceTools or another trusted Source SMD importer. 0.7.0 alpha has passed synthetic tests and a generated 255-bone end-to-end smoke test; actual source-clip visual fidelity remains to be confirmed locally.

## 0.8.0 — HD2 resource inventory

**Completed baseline: 0.8.0-alpha**

- Independently parse legacy archive TOCs and `.gpu_resources` / `.stream` sidecars.
- Read DSAR containers and SLIM package mappings from `bundles.nxa` / `bundles.XX.nxa`.
- Inventory resource IDs and known resource types.
- Parse Unit header references, material IDs and table counts.
- Parse Bones resource hashes, names and LOD metadata.
- Preserve the distinction between a reference absent from one archive and a resource absent from the whole game.

Exit criteria: synthetic TOC/DSAR/SLIM fixtures pass. A user-provided game-data test of the base package was completed; it contained only three Units and no Bones resources, so it is not yet a suitable skinned-armor sample.

## 0.8.1 — HD2 package catalog

**Current: 0.8.1-alpha**

- Scan all SLIM package names using `bundles.nxa`.
- Read only package TOC headers and type tables to identify model-bearing archives without reconstructing GPU/stream sidecars.
- Report archives containing Unit, Bones and CompositeUnit resources, plus Material/Texture counts.
- Expand known resource TypeID labels and retain unknown values.

Exit criteria: run the catalog against the user's full HD2 `data` directory, select a candidate with Unit/Bones resources, then validate that candidate with `hd2-inspect` and the SDK.

## 0.8.2 — HD2 Unit and GPU reader

- Parse TransformInfo, MeshInfo, StreamInfo and BoneInfo records.
- Decode supported vertex component layouts, index buffers, material sections and LOD relationships.
- Correctly handle external CompositeUnit geometry and stream resources.
- Export a neutral intermediate mesh representation; do not retarget Source bones yet.

Exit criteria: one original HD2 armor Unit is reconstructed and its positions, triangle counts, normals, UVs and material boundaries match the SDK/Blender import.

## 0.8.3 — HD2 skeleton palette resolver

- Resolve TransformInfo names through Bones hashes and preserve the real hierarchy.
- Resolve BoneInfo `RealIndices`, inverse bind matrices and per-material `Remaps`.
- Validate each vertex's local bone indices against the active material-section palette.
- Recover source-to-target bind-pose transforms without importing any GMod animation clip.

Exit criteria: one original HD2 armor can be exported as a correctly skinned neutral GLB and passes independent palette/weight/bind-pose validation.

## 0.8.4 — Target rig comparison and deformation validation

- Compare candidate source bones to the verified HD2 target skeleton by explicit mapping rules.
- Report unmapped, ambiguous, extra and target-only bones.
- Measure bind-pose alignment and simulate test rotations on representative target joints.
- Validate geometry and deformation against native HD2 reference behavior.

Exit criteria: pelvis, spine, head, shoulder, elbow, wrist, hip, knee and ankle deformation checks pass against a real target Unit.

## 0.9.0 — Experimental Source-to-HD2 conversion preparation

- Add target-specific mesh coordinate, scale and bind-space adaptation as reversible operations.
- Convert Source vertex influences into HD2 section palettes with explicit limits and validated quantization.
- Preserve material sections and all body geometry in the target Unit layout.
- Produce pre-export diagnostics and a complete conversion report.

Exit criteria: a test character can be mapped to an original HD2 armor Unit, with quantified vertex, influence, palette and deformation changes. This is not yet a universal automatic converter.

## 1.0.0 — Stable Source Model Reader

- Freeze and document the supported Source 1 format subset.
- Keep GMA/ZIP/folder intake, MDL/VVD/VTX reconstruction, BodyGroup selection, GLB export, SMD audit, animation, and supported material conversion covered by integration tests.
- Test multiple independent models, not just the current 3647656951 sample.
- Validate GLB exports in Blender and cross-check geometry/skin data against trusted tooling.
- Publish compatibility notes, known limitations, and reproducible test fixtures.

1.0.0 means the **Source reading/reconstruction pipeline** is stable. It does not mean every Source shader/animation feature is supported or that Helldivers 2 conversion is complete.

## After 1.0 — HD2 conversion and packaging

The HD2 resource reader may mature before the Source-side 1.0.0 release, but full target writing, material conversion and game packaging remain gated by original-game integration tests. The final version count is not predetermined; each release should correspond to a tested capability rather than a cosmetic version bump.

## Versioning rule of thumb

- `0.6.x`: compatibility and correctness fixes within SMD audit.
- `0.7.x`: Source animation import/export correctness fixes.
- `0.8.x`: HD2 resource discovery, Unit decoding, and target skeleton analysis.
- `0.9.x`: experimental Source-to-HD2 conversion preparation.
- `1.0`: stable, documented Source reader/reconstructor. This does not imply that HD2 conversion is complete.
- Later releases: complete target model writing, rig adaptation, patch packaging and supported material conversion, each gated by real-game tests.
