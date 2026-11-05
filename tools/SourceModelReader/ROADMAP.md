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

**Vertex position-space probe (first pass):** `research/probe_hd2_vertex_position_space.py` reads stored vertex positions and compares two hypotheses — positions already in the shared render space versus positions placed by the mesh's own TransformInfo matrix. Evidence and labels are in [research/HD2_VERTEX_POSITION_SPACE_FINDINGS.md](research/HD2_VERTEX_POSITION_SPACE_FINDINGS.md). The control passes: ordinary render LODs of one Unit agree to `4.7e-3` maximum nearest-vertex distance, and every ordinary LOD's stored matrix is identity, which means render meshes alone cannot decide the question. The deciding samples are the two non-identity direct bindings: `hips_culling` (LOD -1, no skin attributes) and `l_arm_culling` (LOD 4, actively skinned) store the same 439-vertex proxy — `normal0` and `uv0` buffers are bitwise identical and positions differ only by `2.4e-17` float32 noise, while MeshIDs, LOD indices and skinning presence all differ. Raw, the proxy is an origin-centered cube of half-extent `0.5094` that leaves the Unit's render union outside by `0.39`/`1.05`; after the candidate row-vector `p × mesh_world` it wraps the render union exactly in both Units (`union_outside == 0`, box slack `0.58`/`0.40`). The per-lane rest check moves the evidence from matrices to vertices: across all 10 skinned meshes `211,956` influence lanes resolve, and `(p × palette) × bone_world` equals `p × mesh_world` within `3.31e-07`; on the non-identity `l_arm_culling` the same product sits `1.595` away from the unplaced vertex, so the placement transform is carried exactly once — neither dropped nor doubled. Candidate reading: positions live in the space their own TransformInfo node defines, and because the BoneInfo palette candidate already contains `mesh_world`, that matrix must take effect exactly once — implicitly through the palette for skinned meshes, directly for unskinned auxiliary meshes. Runtime shader order, LOD -1 semantics and non-uniform-scale composition remain unknown. `research/dump_hd2_bones_resource.py` then tested the obvious independent source and ruled it out: the Bones resource is 1470 bytes fully consumed by a header, six float32 values (`0.25, 0.15, 0.07, 0.04, 0.02, 0.01`, a candidate LOD threshold list now exported as `lod_float_values`), 82 hashes, six uint32 values and the name block, with zero trailing bytes and no Unit matrix float appearing anywhere, so it stores no bind transforms. The independent check for the placement contract therefore has to come from SDK-side or in-game references.

**Placement hypothesis decided on a non-degenerate sample:** `research/survey_hd2_unit_mesh_matrices.py` reads only the Unit, TransformInfo and MeshInfo tables, which made a cheap pass over package `8670598c1f4462dc` possible: all 22 Units there have at least two LODs and at least one non-identity direct binding. Unit `90b57e547e7ff7bb` (left shoulder armour) stores all four render LODs with a shared non-identity matrix `(-0.28328, -0.04818, 1.43647)` and skins them to `l_shoulder_twist`, so raw and placed positions finally differ. Measuring the centroid distance to the bones each mesh skins to gives 14 tests, 5 non-degenerate, and all 5 are closer after applying the matrix — `1.3983` to `0.0683`, `1.3928` to `0.0733`, `1.4004` to `0.0657`, `1.2321` to `0.3146`, plus `l_arm_culling` at `0.3309` — while zero tests favour the raw space and the nine identity-matrix rows are marked `identical` instead of counted as evidence. The stored space is therefore the mesh node's own local space, and the earlier render-LOD samples could not show it only because their matrices happened to be the identity. A converter that assumed "vertices are already in Unit space" would misplace this pauldron by about 1.4 meters. The same probe now also audits the one composition the parser refuses to guess: across 180 transforms in four Units, exactly one has non-uniform scale (`attach_cam_1`, identity rotation) and none combine non-uniform scale with rotation, so that case stays explicitly reported as untested instead of being hidden behind aggregate agreement.

**Eight-Unit generalization run and two new findings:** decoding four more Units from the same package and probing them together with the three checked-in samples gives 30 skinned meshes, 389,215 resolved influence lanes and zero meshes outside the rest tolerance, but only 5 non-degenerate placement tests — 4 shoulder-armour LODs plus `l_arm_culling` — and all 5 again favour the placed space, so the placement conclusion holds on every decidable sample while the decidable set is still two Units. The wider pass also split auxiliary geometry into two observed classes, a 439-vertex body hull (`hips_culling`, `r_leg_culling`, `l_leg_culling`) and a 24-vertex box (`torso_culling`, `helmet_culling`), all unskinned at LOD -1, and surfaced `g_torso_undergarment_*` meshes whose skinning status is `partial` with 1,045 lanes unresolved against the mesh-level palette. That partial case does not affect the rest checks but does block weight transfer, so it is the next concrete defect to localize. It is now localized: every unresolved lane comes from the third bone-index attribute (`vec4_uint32`), which holds a single repeated non-zero value per mesh — `16` (`hips` in the 82-entry Bones name table) on both undergarment Units and `1` (`FbxAxisSystem_ConvertNode`) on the torso sample — and those values fall outside the mesh palette slots (`0..5`) and outside `BoneInfo.real_indices` (`[5,6,7,8,10,11]`). A census over the whole package (22 Units, 114 decoded meshes) found 44 meshes carrying such a value and **no mesh with more than one distinct value**, while the marked vertices sit at the top of each mesh (`z` mean `1.62` against `1.29` unmarked, mesh range `0.974-1.686`) rather than near the `hips` bone at `z` about `0.97`. That disproves the tempting "anchor into the global bone table" reading, so the attribute's semantics stay Unknown - most likely a physics, cloth-region or otherwise unrecognized index space - the shared resolver is left unchanged, and the question must be settled before Phase 5 weight transfer. Raw values stay preserved throughout, and because every resolved lane still passes the rest invariant, the placement conclusion above is unaffected.

**Cross-engine scale and axis, first measurement:** `research/dump_source_bone_rest.py` composes the MDL's parent-relative bone translations and quaternions into rest positions and measures landmark segment lengths against the HD2 Avatar rig. HD2 is unambiguously meter-scale and Z-up (`hips` z `0.9735`, `head` z `1.6908`, `l_foot` z `0.1042`), while this Source sample stores `Pelvis` z `38.6179` and `Head1` z `60.759`. Matching thigh, upper arm, forearm and pelvis-to-head segments give a scale of `0.029–0.032` HD2 meters per Source unit, agreeing within about five percent; the shin is the one outlier at `0.023`, which points at an ankle-definition difference rather than a scale disagreement. Neither "Source unit is one meter" nor "one centimeter" survives that measurement, so the converter must derive scale per model and must not hardcode a constant. Observed in the same pass: joints named `L_` sit at `+X` in Source and at `-X` in HD2 while both put height on `+Z`, which first reads as a mirror. The facing evidence settles it: seven `Hair_Frount_*` bones average Y `-3.6425` while a `Tail_2_1` to `Tail_11_1` chain plus the ponytail average Y `+9.2069`, so this Source model faces `-Y`, and HD2 faces `+Y` because `head_aim` sits at Y `+1.9973` ahead of `head` at Y `-0.0251`. The two frames therefore share one chirality and differ by a 180° rotation about `+Z` rather than a mirror, which agrees with the measured winding consistency: vertex order and normals can carry over unflipped. The probe also records per-mesh attribute layouts, which differ inside a single Unit: `float32` single-lane weights in FS-05 lower-body, `vec4_half` in the torso and Avatar render meshes, and `packed_10_10_10_2` with three bone-index attributes in the skinned proxy.

**Remaining 0.8.4 work**

**Implemented diagnostic baseline:** `hd2_skeleton_compare.py` and `research/compare_source_hd2_skeleton.py` compare compiled Source MDL bone definitions and positive-weight VVD bone usage against an HD2 Unit skeleton. The report includes exact case-folded name matches, active source/target weight status, parent relationships, and explicit missing/ambiguous/target-only lists. `hd2_bone_aliases.py` adds a separate advisory-only ValveBiped humanoid alias candidate layer; it does not alter exact matches or approve retargeting. Candidate parent relationships are checked when the Source parent has an explicit candidate alias. The source active-bone set is currently a union across the stored VVD vertex pool, not split by BodyGroup/LOD.

**Implemented mesh binding classification baseline:** `research/classify_hd2_mesh_bindings.py` classifies every direct MeshInfo binding from stored structure only - LOD index, skin attribute state, vertex and resolved-lane counts, the bound TransformInfo matrix identity state and hierarchy validation state - with names recorded purely for reference and an explicit `unknown` fallback. Across eight decoded Units and 40 bindings the observed partition is 25 `skinned_render_lod` (all with identity matrices, which is why render LODs alone cannot decide the position-space question), 5 `skinned_non_identity_placement` (four shoulder-armour LODs plus `l_arm_culling`) and 10 `auxiliary_unskinned` (all LOD -1, all non-identity, in two distinct shapes: a 439-vertex body hull and a 24-vertex box). Negative classes never fired on real data, so the synthetic suite covers each one, and it also caught two real defects in the first draft: the `unknown` fallback was unreachable behind the lane rule, and an unparseable matrix was being absorbed as a render LOD. Evidence and definitions are in [research/HD2_MESH_BINDING_CLASSIFICATION.md](research/HD2_MESH_BINDING_CLASSIFICATION.md). Shadow and ordinary LODs still share one class because no stored field separates them, and LOD -1 runtime purpose remains unknown.

**Remaining work:**
- Review the advisory ValveBiped-to-Avatar candidates and their parent-graph mismatches; do not promote them to approved mappings yet. Generate the compact per-section BoneInfo palette audit with `research/summarize_hd2_skinning.py`; all 22 candidate transforms are now confirmed active in the corrected comparison report.
- Run the updated comparison against the supplied GMod sample and a verified HD2 Avatar/armor target; inspect the generated candidate table and identify virtual/root branches.
- Validate Source-to-HD2 coordinate, handedness, unit-scale and matrix-space conversion before comparing rest poses.
- Measure bind-pose alignment and simulate test rotations on representative target joints.
- Validate geometry and deformation against native HD2 reference behavior.

Exit criteria: pelvis, spine, head, shoulder, elbow, wrist, hip, knee and ankle deformation checks pass against a real target Unit.

## Parallel 0.8.x track — Material/Texture preservation

**Implemented research parsers: `MATERIAL_FORMAT_NOTES.md` records the common Material and Texture layouts; `hd2_material.py` performs bounded raw Material extraction and same-package Texture checks; `hd2-find` locates resource IDs across SLIM TOC entry tables; `hd2_texture.py` and `hd2-texture` read one Texture's TOC/GPU/Stream ranges, parse DDS DX10 metadata, calculate known-format mip-chain sizes, validate populated mipmap records, detect full-chain versus resident mip-tail payloads, hash each payload, and export an SDK-style DDS. Two real FS-05 BC7 textures confirm both GPU-only full-chain and Stream-full-chain plus GPU-mip-tail layouts. Optional BC7 Mip-0 PNG preview and Alpha histograms are implemented; visual inspection of real-game decoded pixels and material-instance validation remain pending.**

Verified research direction:
- A Unit references Material resources; each Material references Texture resources.
- Material stores a raw ParentMaterialID, per-node 32-bit texture-type values, 64-bit texture IDs and shader-variable descriptors/value payload.
- ParentMaterialID aliases differ between AQ Edition and Community Edition. Preserve raw IDs and keep all friendly-name mappings revision-scoped.
- A texture's RGBA alpha channel does not automatically mean transparency. Alpha Clip is documented as binary mask/cutout; Translucent exposes an OpacityBase scalar and has an alpha-mask use case, but exact per-pixel source-channel behavior still needs instance-level validation; Advanced uses its color texture alpha as an emissive mask.
- Preserve compressed texture bytes, DDS headers, mipmaps, array size and alpha bytes. Do not flatten arrays or discard source alpha.

Next milestones:
1. Validate the new bounded Material decoder against real game Material entries, especially the variable-value boundary and table offsets.
2. Validate shader-variable payload lengths and array/stride semantics; add versioned ID dictionaries without replacing raw IDs.
3. Expand pixel decoding beyond BC7 base mips to other BC formats and texture arrays where justified; keep the current optional BC7 preview as the first inspection path. Inspect real FS-05 PNG/Alpha output and verify it against the linked Material family.
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
