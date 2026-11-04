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

**Completed implementation baseline: 0.8.1-alpha**

- Scan all SLIM package names using `bundles.nxa`.
- Read only package TOC headers and type tables to identify model-bearing archives without reconstructing GPU/stream sidecars.
- Report archives containing Unit, Bones and CompositeUnit resources, plus Material/Texture counts.
- Expand known resource TypeID labels and retain unknown values.

Exit criteria: run the catalog against the user's full HD2 `data` directory, select a candidate with Unit/Bones resources, then validate that candidate with `hd2-inspect` and the SDK.

## 0.8.2 — HD2 Unit and GPU reader

**Accepted structural baseline: 0.8.2-alpha + 0.8.2.1-alpha — synthetic tests and three real-game samples validated**

- Parse TransformInfo local data, raw matrices, parent indices and name hashes.
- Parse BoneInfo matrices, RealIndices and per-material Remap arrays.
- Parse StreamInfo component descriptors and MeshInfo section/LOD relationships.
- Decode supported direct Unit GPU vertex/index layouts and CompositeUnit-backed shared geometry.
- Decode Format 30 octahedral normals and Format 29 three-channel normalized bone weights when their component semantics identify those uses.
- Preserve raw packed words alongside additive semantic decoded values.
- Keep matrix convention, bind-pose semantics, and Source-to-HD2 retargeting out of this milestone.

Real-game acceptance was completed locally using two FS-05 Units and one Avatar Unit backed by a CompositeUnit. All selected geometry ranges and palette chains were checked against the sample data. Visual comparison against a separate trusted renderer remains a later validation task.

## 0.8.3 — HD2 skeleton palette resolver

**Implemented: 0.8.3-alpha — resolver unit tests added; three attached real-game JSON samples audit clean**

- Resolve TransformInfo names and parent relationships through Bones hashes, while preserving unmatched helper transforms.
- Resolve BoneInfo palette slots through RealIndices and attach the corresponding raw BoneInfo matrix.
- Resolve each MeshInfo material section using its own BoneInfo Remap.
- Emit per-vertex active influences with weight, local index, palette slot, TransformInfo index and bone name.
- Preserve multiple bone_index component indices as independent sets; do not merge them.
- Keep matrix convention, bind-pose semantics and inverse-bind interpretation unverified.

Real-sample audit: two FS-05 Units and one Avatar Unit have zero unresolved active influence lanes. All active influence TransformInfo indices have matching Bones names. This does not yet prove correct deformation in Blender or establish matrix conventions.

## 0.8.4 — Target rig comparison and deformation validation

**In progress: 0.8.4-alpha — TransformInfo hierarchy and active palette matrix diagnostics implemented**

- Rebuild TransformInfo world matrices from local rotation/position/scale and the parent graph using the sample-validated row-vector `Local × ParentWorld` candidate; compare against the stored matrices.
- Compare actively used BoneInfo matrices with the observed `MeshWorld × inverse(BoneWorld)` relationship. Unreferenced palette slots are excluded from active-slot validation but remain visible in the palette.
- Add synthetic tests for matrix composition, parent-cycle handling, active palette filtering and mismatch reporting.

The current local linear-matrix builder uses `rotation_3x3 × diag(scale)`. The only non-unit scale in the three samples is on `attach_cam_1`, whose rotation is identity, so non-uniform scale combined with a non-identity rotation remains unverified; the validator refuses to guess if that combination appears.

Real-sample audit: all 161 TransformInfo matrices across two FS-05 Units and one Avatar CompositeUnit Unit matched the reconstructed hierarchy within (2.58×10^{-7}). All 162 actively used BoneInfo palette slots matched the mesh-space comparison within (1.74×10^{-7}). 27 unreferenced Avatar palette slots were excluded; this is not proof of their semantic purpose.

The resolver also marks which TransformInfo nodes are actually reached by positive-weight vertex lanes and reports named/unmatched/actively weighted counts through `transform_info.weight_usage_summary`. On the inspected Avatar mesh, 90 names map to TransformInfo but only 61 distinct named transforms are actively weighted; 27 BoneInfo palette entries are unreferenced by that mesh. The corresponding actively weighted TransformInfo counts are 2 for FS-05 lower-body, 23 for FS-05 torso/left-arm and 61 for the Avatar mesh. This gives the next bone-mapping step a data-backed starting set rather than assuming every named attachment/helper is a deformation bone.

A name-and-Rest-matrix cross-check against AQ Edition's pinned 63-entry Avatar reference (`hd2_system/avatar_source.json`, commit `71b380046d33d552eef0892bd48661ff25591380`) found all 63 names in the Unit's named TransformInfo entries. After transposing the Unit's row-vector 4×4 world matrices to the reference's model-space 3×4 layout, the 63 Rest matrices match with mean per-bone maximum absolute error `8.07e-7`, maximum `1.34e-5`, and maximum translation error `5.40e-7`. Sixty of those reference bones are actively weighted by the Avatar mesh; the remaining three are unweighted helpers. The Unit has one extra active weighted node, `sling`, absent from the AQ reference. Because AQ explicitly stores model-space Rest rather than Blender authoring hierarchy, this validates names and rest transforms, not parent-graph or animated behavior.

**Remaining 0.8.4 work**

- Compare candidate source bones to the verified HD2 target skeleton by explicit mapping rules.
- Report unmapped, ambiguous, extra and target-only bones.
- Measure bind-pose alignment and simulate test rotations on representative target joints.
- Validate geometry and deformation against native HD2 reference behavior.

Exit criteria: pelvis, spine, head, shoulder, elbow, wrist, hip, knee and ankle deformation checks pass against a real target Unit.

## Parallel 0.8.x track — Material/Texture preservation

**Implemented research parsers: `MATERIAL_FORMAT_NOTES.md` records the common Material and Texture layouts; `hd2_material.py` performs bounded raw Material extraction and same-package Texture checks; `hd2-find` locates resource IDs across SLIM TOC entry tables; `hd2_texture.py` and `hd2-texture` read one Texture's TOC/GPU/Stream ranges, parse the 15 mipmap records and DDS DX10 header, hash each payload, and export an SDK-style DDS. Real-game texture decoding and material-instance validation remain pending.**

Verified research direction:
- A Unit references Material resources; each Material references Texture resources.
- Material stores a raw ParentMaterialID, per-node 32-bit texture-type values, 64-bit texture IDs and shader-variable descriptors/value payload.
- ParentMaterialID aliases differ between AQ Edition and Community Edition. Preserve raw IDs and keep all friendly-name mappings revision-scoped.
- A texture's RGBA alpha channel does not automatically mean transparency. Alpha Clip is documented as binary mask/cutout; Translucent exposes an OpacityBase scalar and has an alpha-mask use case, but exact per-pixel source-channel behavior still needs instance-level validation; Advanced uses its color texture alpha as an emissive mask.
- Preserve compressed texture bytes, DDS headers, mipmaps, array size and alpha bytes. Do not flatten arrays or discard source alpha.

Next milestones:
1. Validate the new bounded Material decoder against real game Material entries, especially the variable-value boundary and table offsets.
2. Validate shader-variable payload lengths and array/stride semantics; add versioned ID dictionaries without replacing raw IDs.
3. Decode representative real Texture payloads (including BC-compressed formats and texture arrays) to inspect channel content, while preserving original compressed bytes and mipmaps.
4. Validate real Alpha Clip, Translucent/Glass and opaque Material resources against their linked Texture data before deciding any source-to-HD2 material mapping.

No auto classification or output conversion should be implemented from names alone. In particular, partially transparent stockings/fabric must not be silently routed through Alpha Clip.

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
