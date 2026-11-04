# Changelog

## 0.8.4-alpha

- Rebuild TransformInfo world matrices from local rotation, position, scale and parent indices using the observed row-vector local-times-parent-world convention; compare the result with the stored 4x4 matrices.
- Compare actively used BoneInfo palette matrices with the observed MeshWorld multiplied by inverse(BoneWorld) relationship, using positive-weight vertex lanes only.
- Keep raw TransformInfo and BoneInfo matrices unchanged; report per-transform and per-mesh matrix diagnostics without claiming completed animation deformation or universal inverse-bind semantics.
- Refuse to compose a transform that combines non-uniform scale with non-identity rotation until their multiplication order is validated; the current real samples do not distinguish that case.
- Added synthetic tests for row-vector hierarchy composition, parent-cycle detection, mesh-space matrix comparison, active palette filtering and mismatch reporting.
- Audited three real-game JSON samples: all 161 TransformInfo hierarchy matrices matched; all 192 actively used BoneInfo palette slots matched within 1e-4. Two additional Avatar palette slots (aim_weapon and backpack) were unreferenced by active influences and excluded from the active-slot comparison.

## 0.8.3-alpha

- Resolve TransformInfo name hashes against the Unit's Bones resource, retaining unmatched helper transforms and explicit parent indices.
- Resolve each BoneInfo palette slot through RealIndices to TransformInfo, with the corresponding raw BoneInfo matrix kept alongside that palette slot.
- Resolve each material section independently through its matching BoneInfo Remap table.
- Emit per-vertex active influences with local bone index, palette slot, TransformInfo index, resolved bone name and weight.
- Preserve each bone_index component index as a separate set; never merge legacy/grouped index sets into one influence stream.
- Ignore zero-weight lanes when counting unresolved active influences, while retaining their raw indices and aligned transform-index placeholders.
- Added three synthetic resolver tests for named hierarchy/palette mapping, material-specific remaps, unresolved active lanes, and LOD -1 behavior.
- Audited the three attached real-game JSON samples: all active influences resolve through their material remaps to valid TransformInfo entries and all active influence names are present in the Bones resource. BoneInfo matrices remain uninterpreted raw 4x4 arrays.

## 0.8.2.1-alpha

- Added Format 30 octahedral normal decoding for normal components.
- Added Format 29 three-channel UNORM bone-weight decoding when the component semantic is `bone_weight`; the top two bits remain preserved in the raw word and the fourth decoded influence is zero.
- Kept the original packed integers in each attribute's `values` array and added `decoded_semantics` / `decoded_values` for semantic output.
- Kept unknown uses of Format 29/30 raw and emits a warning instead of guessing their semantic meaning.
- Added five regression tests for octahedral axes, upper-bit handling, compressed weights, raw-value preservation, and semantic gating.
- Verified against the three user-provided real HD2 samples: FS-05 lower-body Unit, FS-05 torso/left-arm Unit, and Avatar CompositeUnit. The three samples' Format 30 normal attributes and known Format 29 bone-weight attribute decode without format warnings.

## 0.8.2-alpha

- Added `hd2-unit` to inspect one Unit's TransformInfo, BoneInfo, StreamInfo, MeshInfo and supported GPU geometry.
- Added direct Unit GPU decoding and CompositeUnit-backed shared geometry/stream mapping.
- Preserved local transforms, raw 4x4 matrices, parent indices, Transform name hashes, BoneInfo matrices, RealIndices and per-material Remap arrays in JSON.
- Decoded supported float32, float16, uint8, uint32 and RGBA8 vertex components plus 16-bit/32-bit index buffers; packed normal/10-10-10-2 values remain raw.
- Added bounds checks for table offsets, component strides, vertex/index buffer ranges, section ranges and palette index references.
- Added five synthetic tests for TransformInfo, BoneInfo/remaps, StreamInfo/GPU geometry, MeshInfo and CompositeUnit mapping.
- Synthetic parser tests: 5/5 passing. Real FS-05 and Avatar package decoding remains a local acceptance step; no original game package was decoded in the build environment.

## 0.8.1-alpha

- Added `hd2-catalog` to scan the entire SLIM package index and identify archives containing Unit, Bones, CompositeUnit, Material, and Texture resources.
- Added bounded `SlimBundleIndex.reconstruct_range` reads so package discovery reads only TOC headers and type tables instead of rebuilding complete package payloads and GPU/stream sidecars.
- Added package-level summaries, aggregate type totals, scan limits, JSON output, and per-package error reporting.
- Expanded known resource type names from the public SDK type-ID table while preserving unknown type IDs.
- Clarified that `hd2-inspect` inspects one package only and that its default `9ba626afa44a3aa3` selection is not a whole-game scan.
- Added SLIM catalog fixtures, sidecar exclusion tests, partial range tests, and CLI JSON tests.
- Full test suite: 64/64 passing. No original HD2 game-data directory is available in this build environment; real-game catalog validation remains a local acceptance step.

## 0.8.0-alpha

- Added a standalone read-only HD2 resource inspector in `hd2_resources.py`; it does not import Blender or copy SDK code.
- Added legacy package TOC parsing (`0xF0000011`), bounded resource-entry validation, type inventory and sidecar size accounting.
- Added DSAR chunk-table parsing and a small pure-Python LZ4 block decoder for compressed package chunks.
- Added SLIM package-map parsing from `bundles.nxa` and reconstruction of selected TOC/GPU/stream package files from `bundles.XX.nxa` chunks.
- Added Unit header inspection, including BonesRef, CompositeRef, StateMachineRef, table offsets/counts, and material section/resource IDs.
- Added Bones resource inspection for declared name count, LOD count, bone hashes, names and LOD levels.
- Added `hd2-inspect <game-data-directory-or-package> [--package ARCHIVE_ID] [--json report.json]`.
- Dependency references not found in the selected archive are reported as out-of-package references, not as globally missing resources.
- Added synthetic legacy TOC, DSAR/LZ4 and SLIM reconstruction regression fixtures (60/60 tests passing).
- 0.8.0 does not yet decode Unit GPU vertex formats, BoneInfo palettes/remaps, or mesh geometry; those are the next milestones.


## 0.7.0-alpha

- Added `smd_animation.py` to convert multi-frame SMD skeleton sections into name-mapped per-bone animation tracks.
- Added `export-animated-glb`, combining the selected unified skinned model scene and one external animation SMD in a single GLB.
- Requires explicit `--fps` and preserves authored source frame indices/time origin in GLB extras and the JSON report.
- Converts SMD local positions and XYZ Euler-radian rotations to glTF Y-up translation and XYZW quaternion tracks.
- Keeps sparse records sparse; `auto` selects LINEAR for dense bone tracks and STEP for sparse bone tracks without fabricating key values.
- Elides tracks that are constant and equal to the GLB rest transform, while retaining constant animation offsets that differ from rest; caches identical sampler time accessors.
- Rejects incompatible bone-name/parent hierarchies and non-animation SMD roles instead of silently retargeting.
- Extended `validate-glb` to independently inspect animation sampler/channel references, accessors, time ordering, output counts/types, finite values, and quaternion normalization.
- Added a sample-matched 255-bone synthetic animation fixture, dense/sparse track tests, and GLB animation round-trip/invalid-time validation tests (45/45 total tests passing).
- Verified animated GLB export end-to-end against the supplied 3647656951 ZIP using an included generated two-frame, 255-bone smoke-test SMD. The smoke SMD is synthetic, not the author's original idle animation.

## 0.6.2-alpha

- Separated structural parser warnings from informational sparse-frame diagnostics for animation-like SMD roles.
- Kept incomplete model-slice/physics frames, empty frames, undefined bone IDs, missing parents, and hierarchy issues as warnings.
- Added per-frame transform coverage (declared, explicit, omitted, unknown counts and ratio); no omitted transforms are synthesized or assumed to inherit previous values.
- Added regression coverage for a corrective animation frame containing 1 explicit transform for a 3-node/255-node skeleton, plus empty-frame and model-slice safety cases.
- Added a ready-to-run sparse corrective-animation fixture and milestone roadmap through 1.0.0.

## 0.6.1-alpha

- Added conservative SMD role classification for ordinary model slices, physics geometry, animation, proportion adjustment, corrective animation, animation reference, ragdoll pose, and unknown files.
- Kept node/name/parent-relationship comparisons active for every parsed SMD type.
- Restricted bind/reference-pose comparisons to ordinary model slices; animation and physics first frames are no longer treated as rest/bind poses.
- Applied the same pose-comparison policy to optional MDL mapping while retaining name/index/parent checks for every SMD.
- Changed automatic reference selection to prefer `AES_T.smd`, then the first model slice, with an explicit `--reference` override.
- Added separate structure and reference-pose statuses, pose skipped counts, and SMD type counts to audit reports.
- Added regression tests for classification, auto-reference selection, and animation pose false-positive suppression.


## 0.6.0-alpha

- Added a text SMD parser for `nodes` and `skeleton` sections, including multiple skeleton frames and a time-0/first-frame reference pose.
- Added hierarchy checks for duplicate nodes, missing parents, and cycles; added pose validation for non-finite values, undefined node IDs, and missing frame transforms.
- Added `smd-audit` for direct SMD files, folders, ZIP archives, and uncompressed GMA archives without extracting files.
- Added cross-slice comparison by bone name, parent name, and reference-pose local transforms; numeric node-ID differences are reported as remaps rather than automatic failures.
- Added optional name-based cross-checks against an MDL bone table via `--mdl`.
- Added synthetic SMD fixtures, directory/ZIP/GMA scan tests, and real Workshop package regression confirming that the supplied package contains no raw SMD source files.
- Preserved all 0.5.0 model parsing, BodyGroup matrix, SMD export, GLB export, and GLB validation features.


## 0.5.0-alpha

- Corrected diagnostic terminology: MDL bone definitions are reported separately from bone indices directly referenced by the selected geometry's vertex weights.
- Removed the misleading "helper/unweighted bones" classification; absence of a current mesh weight reference is no longer interpreted as a bone role.
- Added `bone-matrix`, which evaluates every BodyGroup model option with all other BodyGroups at option 0 and reports added/removed weight-referenced bone indices and names relative to the all-default selection.
- Included explicit comparison semantics and limitations: the matrix does not infer animation, procedural, physics, attachment, or runtime dependencies.
- Added unit coverage and a real GMA option-by-option matrix regression report.

## 0.4.0-alpha

- Added `diagnose` for skeleton hierarchy, bone usage, vertex influence, weight-sum, attribute finite-value, degenerate-triangle, and BodyGroup coverage analysis.
- Added full bone hierarchy/usage tables and a full BodyGroup option catalogue to the JSON diagnosis report.
- Distinguished informational unused bones and unreferenced VVD vertices from actual errors.
- Added `validate-glb` to independently validate GLB chunks, accessors, buffers, primitive indices, skin joints, inverse-bind matrices, and skin-weight sums.
- Added Windows drag-and-drop helpers for Source model diagnostics and GLB validation.
- Added diagnostic and GLB validator unit tests and re-ran integration tests against the real GMA sample.

## 0.3.0-alpha

- Added the internal `ModelScene` representation.
- Added unified skinned glTF 2.0 binary (`export-glb`) output.
- Combined selected BodyGroup choices into one mesh node and one shared skeleton.
- Preserved material boundaries as multiple primitives within the unified mesh.
- Added Source Z-up to glTF Y-up transform conversion.
- Added Source `poseToBone` inverse bind matrices and a bind-pose consistency check.
- Retained the SMD exporter as a compatibility path.
- Added GLB export tests and real-GMA integration validation.

## 0.2.0-alpha

- Added direct uncompressed GMA v1-v3 archive reading.
- Added GMA member CRC checking and unsafe-path rejection.

## 0.1.0-alpha

- Added initial MDL/VVD/VTX inspection and weighted SMD export.
