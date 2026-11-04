# Source Model Reader (alpha 0.8.4)

A dependency-free Python tool for reading a bounded subset of Source 1 model resources, reconstructing unified skinned scenes, and independently inspecting Helldivers 2 resource packages. The HD2 path is read-only and experimental.

## 0.8.4 — Transform hierarchy and mesh-space matrix diagnostics

The HD2 Unit report now reconstructs the TransformInfo hierarchy from each transform's local rotation, position, scale and parent index. The candidate row-vector composition is:

`LocalTransform × ParentWorld`

The rebuilt world matrices are compared against the original `matrix_4x4_raw` values. The report records the comparison convention, tolerance, per-transform result and aggregate counts under `transform_info.hierarchy_matrix_validation`.

For meshes with skin attributes, the reader also compares only actively used BoneInfo palette slots (positive-weight lanes) against the observed relation:

`BoneInfoMatrix[palette_slot] ≈ MeshWorld × inverse(BoneWorld)`

The diagnostic is stored in `geometry[].skinning.matrix_validation`. Unreferenced palette slots are counted but not used to fail the comparison. This avoids treating unused attachment/helper slots as if they deform vertices. Raw TransformInfo and BoneInfo matrices remain unchanged.

**Three real-game sample audit**

| Sample | TransformInfo matrices | Active BoneInfo slots | Distinct weighted transforms |
|---|---:|---:|
| FS-05 lower-body Unit | 16 / 16 | 8 / 8 | 2 |
| FS-05 torso/left-arm Unit | 38 / 38 | 93 / 93 | 23 |
| Avatar CompositeUnit Unit | 107 / 107 | 61 / 61 (27 additional slots unreferenced) | 61 |
| **Total** | **161 / 161** | **162 / 162** | **per-Unit counts above** |

The current local linear-matrix builder uses `rotation_3x3 × diag(scale)`. The only non-unit scale in these samples is on `attach_cam_1`, whose rotation is identity, so the multiplication order for a transform combining non-uniform scale with a non-identity rotation remains unverified. When such a transform is encountered, the validator marks it unresolved instead of guessing. The diagnostic exposes this uncertainty explicitly.

The report also separates named transforms from actual vertex deformers. Every `resolved_skeleton[]` entry now includes `has_active_vertex_influence` and `active_vertex_influence_lane_count`; `transform_info.weight_usage_summary` aggregates named/unmatched transforms and the number of distinct transforms reached by positive-weight lanes. Lane counts are still per component set and section; they are not unique vertex counts.

On the Avatar sample, 107 TransformInfo entries resolve to 90 named entries and 17 unnamed/helper entries. The palette has 88 unique TransformInfo targets, but only 61 named transforms are actually reached by positive-weight vertex lanes in this mesh; 27 palette slots are unused by those lanes. This is why the 90 Bones names must not all be treated as body deformation bones.

All TransformInfo comparisons were within a maximum absolute error of (2.58×10^{-7}); all active BoneInfo palette comparisons were within (1.74×10^{-7}), against a diagnostic tolerance of (10^{-4}). The Avatar palette has 27 unreferenced slots. Of those, `aim_weapon` and `backpack` are the two whose raw matrices do not satisfy the comparison; neither is referenced by a positive-weight vertex lane in this sample.

### Rest-pose cross-check against the public AQ Avatar reference

AQ Edition publishes a 63-entry `hd2_system/avatar_source.json` reference skeleton and describes it as a validated Avatar source used by its runtime profiles: [pinned source file](https://github.com/Estecsky/HD2SDK-AQ-Edition/blob/71b380046d33d552eef0892bd48661ff25591380/hd2_system/avatar_source.json).

All **63/63** AQ source-bone names are present among the target Unit's 90 uniquely named TransformInfo entries. The reference file stores each bone's model-space 3×4 Rest matrix. For comparison, the target Unit's stored row-vector 4×4 world matrix was transposed into the same 3×4 layout. The full Rest matrices then matched across all 63 names:
- Mean per-bone maximum absolute component error: approximately (8.07×10^{-7}).
- Median per-bone maximum absolute component error: approximately (4.77×10^{-7}).
- Maximum component error: approximately (1.34×10^{-5}), on `l_shoulder_twist`.
- Maximum translation-component error: approximately (5.40×10^{-7}).

Of those 63 reference bones, 60 are actively weighted by the inspected Avatar mesh and 3 (`support_mg`, `attach_intelpad`, `attach_samplepouch`) are named helper/attachment entries not used by this mesh's positive-weight vertex lanes. The Unit has one additional actively weighted transform, `sling`, that is not present in the 63-entry AQ reference.

The AQ file's provenance explicitly says its `parent=-1` entries store model-space Rest, not Blender's authoring hierarchy. This comparison validates name correspondence and model-space Rest matrices; it does not replace the Unit's TransformInfo parent graph, establish animated behavior, or prove that `sling` should be discarded. Keep `sling` as an explicit Unit-specific weighted node. No AQ implementation code or rig data is imported by this reader.

These results support the matrix layout/composition candidate for the inspected samples. They do **not** establish a universal convention for every Unit variant, prove the semantic role of every BoneInfo matrix, or replace animation/deformation validation.

## Installing the 0.8.4 patch over an existing 0.8.3 folder

Download the following into a separate temporary folder, preserving the `tests` subfolder:

- `install_0.8.4.py`
- `source_model_reader.py`
- `hd2_unit.py`
- `hd2_resources.py`
- `VERSION`
- `tests/test_hd2_unit.py`
- `tests/test_smd_animation.py`
- `tests/test_smd_reader.py`

Run the installer and target the existing Source Model Reader folder:

```powershell
py -3 .\install_0.8.4.py --root "G:\Temp\Githud\HD2Mimnt\tools\SourceModelReader"
```

The installer accepts `0.8.2-alpha`, `0.8.2.1-alpha` or `0.8.3-alpha`, creates `.pre-0.8.4.bak` backups only when absent, and does not modify Helldivers 2 game files. After installation, run the test suite:

```powershell
Set-Location "G:\Temp\Githud\HD2Mimnt\tools\SourceModelReader"
py -3 -m unittest discover -s tests -v
```

## 0.8.3 — named HD2 skinning resolution

The HD2 Unit report now resolves the skinning chain:

`vertex-local bone index -> Remaps[material_index] -> RealIndices -> TransformInfo -> Bones name_hash`

Each MeshInfo material section is resolved independently, using the BoneInfo record selected by the mesh LOD. Each `bone_index` component index is retained as its own set; sets are not merged. The JSON adds per-vertex active influence rows containing the source lane, local index, palette slot, TransformInfo index, resolved bone name and weight.

TransformInfo hierarchy entries now include the resolved parent index/name where available. Each BoneInfo palette entry also carries the matching raw BoneInfo matrix for later bind-pose investigation. Matrix layout and inverse-bind meaning are intentionally not interpreted yet.

The three supplied real-game samples were audited: no active influence lane failed the local-index -> material remap -> RealIndices -> TransformInfo chain, and every actively used TransformInfo index resolved to a Bones name. This is structural validation, not a visual skin-deformation or bind-pose validation.

## Installing the 0.8.3 patch over an existing 0.8.2.x folder

Download the following into a separate temporary folder, preserving the `tests` subfolder:

- `install_0.8.3.py`
- `source_model_reader.py`
- `hd2_unit.py`
- `hd2_resources.py`
- `VERSION`
- `tests/test_hd2_unit.py`

Run the installer and explicitly target the existing project folder:

```powershell
py -3 .\install_0.8.3.py --root "G:\Temp\Githud\HD2Mimnt\tools\SourceModelReader"
```

The installer accepts an existing `0.8.2-alpha` or `0.8.2.1-alpha` installation. It creates `.pre-0.8.3.bak` backups (only when absent), replaces the reader module, CLI entry point, resolver tests and VERSION file, and does not touch Helldivers 2 game files.

Then run the tests from the target project folder:

```powershell
Set-Location "G:\Temp\Githud\HD2Mimnt\tools\SourceModelReader"
py -3 -m unittest discover -s tests -v
```

## What 0.8.2.1 adds — packed normal and weight decoding

The 0.8.2.1 patch builds on the real FS-05 and Avatar Unit inspections:

- Format 30 is decoded as a 10+10-bit octahedral normal when the component type is `normal`.
- Format 29 is decoded as three unsigned-normalized 10-bit weights when the component type is `bone_weight`. The upper two packed bits remain available in the raw value; the fourth decoded influence is zero.
- Every attribute retains the original stored values in `values`. Packed attributes also include `decoded_semantics` and `decoded_values`; this avoids destroying source bytes or changing the meaning of the existing raw field.
- Format 29/30 uses with a different component semantic remain raw and produce a warning rather than being guessed.

Real-game acceptance samples used:
- FS-05 lower-body Unit: `8670598c1f4462dc / 210fb2f60f8308ea`
- FS-05 torso/left-arm Unit: `8670598c1f4462dc / 324fb206612ce4f7`
- Avatar Unit using CompositeUnit geometry: `18235e0c9ec0e636 / 4d1c334d294dfa97`

The reader remains read-only. Transform matrices are still preserved as raw values; bind-pose and matrix-convention interpretation is a separate, uncompleted validation step.

## What 0.8.2 adds — HD2 Unit and GPU geometry inspection

The new read-only `hd2-unit` command decodes one Unit after an archive has been identified with `hd2-catalog` and `hd2-inspect`.

- Reads TransformInfo local values, raw 4x4 matrices, parent indices and name hashes.
- Reads BoneInfo matrices, RealIndices and per-material Remap arrays without guessing their final skeleton mapping.
- Reads StreamInfo component descriptors, vertex strides, buffer offsets/sizes and supported formats.
- Reads MeshInfo records, LOD/transform/stream selection, material sections and index ranges.
- Decodes direct Unit GPU buffers and CompositeUnit-backed shared geometry into a neutral JSON representation.
- Does not write or patch game resources. Bone palette/name resolution and Source-to-HD2 retargeting are not implemented yet.

Start with the FS-05 player armor archive and one Unit:

```powershell
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package "8670598c1f4462dc" --unit "210fb2f60f8308ea" --json "fs05_unit_210fb2f60f8308ea.json"
```

Then test a Unit with five BoneInfo records:

```powershell
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package "8670598c1f4462dc" --unit "324fb206612ce4f7" --json "fs05_unit_324fb206612ce4f7.json"
```

Finally, test the Avatar Unit whose geometry is stored in a CompositeUnit:

```powershell
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package "18235e0c9ec0e636" --unit "4d1c334d294dfa97" --json "hd2_avatar_unit.json"
```

Run the test suite after installation:

```powershell
py -3 -m unittest discover -s tests -v
```

The five 0.8.2 parser tests and five 0.8.2.1 packed-format tests are synthetic regression tests. The three real-package runs above were also completed locally against the user's installed game data.

## What 0.8.1 adds — HD2 package catalog and resource discovery

- Keeps the read-only `hd2-inspect` command for one legacy/DSAR/SLIM package.
- Adds `hd2-catalog`, which scans all SLIM package TOC headers and resource-type tables to find packages containing Unit, Bones, and CompositeUnit resources.
- Catalog scanning reads only package metadata ranges; it does not rebuild complete GPU/stream sidecars or load every resource payload.
- Adds bounded `SlimBundleIndex.reconstruct_range` for partial reads across DSAR bundle chunks.
- Expands resource type names using the public SDK type-ID table, retaining unknown IDs rather than discarding them.
- Keeps Unit header, material ID and Bones metadata inspection for individual candidate packages.
- Does **not** modify game data or write a Patch. Per-Unit geometry decoding is provided by the 0.8.2 `hd2-unit` command; global dependency resolution and palette/name resolution remain future work.

First, scan the installed game's complete SLIM package catalog. This is the recommended starting point; the base archive alone is not a representative armor sample:

```powershell
py -3 source_model_reader.py hd2-catalog "D:\SteamLibrary\steamapps\common\HELLDIVERS 2\data" --json "hd2_package_catalog.json"
```

The catalog reports packages with Unit, Bones and CompositeUnit resources. It reads TOC type tables only and skips GPU/stream sidecar reconstruction. To limit a diagnostic run, add `--max-packages 100`.

Then inspect one candidate archive/package by its package name or 16-character archive ID:

```powershell
py -3 source_model_reader.py hd2-inspect "D:\SteamLibrary\steamapps\common\HELLDIVERS 2\data" --package "<candidate_package>" --json "candidate_inventory.json"
```

A standalone legacy/DSAR package can also be passed directly:

```powershell
py -3 source_model_reader.py hd2-inspect "D:\HD2Extracted\<candidate_package>"
```

`hd2-inspect` examines exactly one package. When the input is a game data directory and `--package` is omitted, it defaults to `9ba626afa44a3aa3`; this is a base package, not a scan of the whole game. Use `hd2-catalog` to discover model-bearing archives first.

The output deliberately says **“not present in this package”**, not “missing from the game”: resource dependencies can be stored in other archives. For a SLIM install, `bundles.nxa`, `bundles.XX.nxa`, and the sidecar package entries must be present in the game `data` directory.

## Previous milestone — 0.7.0 SMD animation export

- Reads multi-frame SMD skeleton transforms as time-indexed local bone tracks.
- Requires an explicit `--fps`; SMD frame indices are not assumed to encode a frame rate.
- Maps SMD node names and parent names to the compiled MDL skeleton and rejects ambiguous/mismatched hierarchies.
- Exports the unified character mesh, shared skin, and one animation clip into the same GLB file.
- Preserves source frame indices and FPS in GLB animation `extras` and the companion JSON report.
- Keeps sparse tracks sparse: missing transforms are never synthesized. In `auto` mode, dense tracks use `LINEAR` and sparse tracks use `STEP`; choose an explicit interpolation mode to override this policy. Tracks unchanged and equal to the GLB rest transform are omitted to reduce redundant channels.
- Extends `validate-glb` to validate animation sampler accessors, time ordering, output types/counts, target nodes/paths, duplicate targets, finite values, and rotation quaternion normalization.

Example:

```powershell
py -3 source_model_reader.py export-animated-glb "D:\Models\addon.gma" --model "aes_yz_pm.mdl" --animation "D:\Models\decompiled\aes_yz_pm_anims\idle_all_01.smd" --fps 30 -o "D:\Export\aes_yz_pm_idle.glb"
py -3 source_model_reader.py validate-glb "D:\Export\aes_yz_pm_idle.glb"
```

For an end-to-end smoke test against the sample AES model, use the included **synthetic** 255-bone clip (it is not an original Workshop animation):

```powershell
py -3 source_model_reader.py export-animated-glb "D:\Models\gmpublisher.gma" --model aes_yz_pm.mdl --animation "examples\animation_fixture\synthetic_arm_pose.smd" --fps 30 -o "synthetic_arm_pose.glb"
py -3 source_model_reader.py validate-glb "synthetic_arm_pose.glb"
```

`--fps` is mandatory and must be supplied from known QC/project settings or a trusted source. The tool does not infer it; replace the example `30` with the actual `$sequence` FPS from your QC or project settings. The exporter currently supports ordinary multi-frame animation SMDs; single-pose references, ragdoll poses, proportion/corrective animation semantics, procedural bones, controllers, IK, flexes, and Source runtime animation behaviors are not retargeted. Sparse per-bone records are represented as explicit keys only; `auto` chooses `STEP` for sparse tracks as a conservative glTF playback policy, not a claim about every Source runtime's interpolation semantics. Tracks whose explicit values all equal the target model's GLB rest transform are elided; constant offsets that differ from rest are retained. Validate actual motion in Blender before treating the clip as production-ready.

## Previous milestones retained (0.6.2 and earlier)

- Separates structural parse warnings from informational sparse-frame coverage for animation, proportion, corrective-animation, animation-reference, and ragdoll SMDs.
- Keeps omitted transforms in model/reference slices and empty animation frames as warnings; undefined bone IDs remain warnings for every SMD type.
- Reports per-frame declared, explicit, omitted, and unknown transform counts plus coverage ratios. Missing transforms are not synthesized or assumed to inherit a previous frame.
- Adds a sparse corrective-animation regression matching the observed `1 / 255` transform-record pattern; the overall audit remains `OK` with an informational note.
- Adds `ROADMAP.md` with milestone goals through 1.0.0 and the HD2 target-resource workstream.

## What 0.6.1 adds

- Classifies common SMD roles: model slices, physics geometry, animation, proportion adjustment, corrective animation, animation reference, ragdoll pose, and unknown.
- Keeps skeleton node/name/parent checks active for every SMD type, but compares bind/reference poses only between ordinary model slices.
- Prevents animation frame 0 from being misreported as a bind-pose mismatch. The same policy applies to SMD-to-MDL pose checks.
- Automatically prefers `AES_T.smd` as the base character slice when present; otherwise selects the first classified model slice. Explicit `--reference` remains available.
- Reports pose checks as `compared` or `skipped` separately from skeleton structure status, and adds SMD type counts to JSON/console output.

## What 0.6.0 adds

- Reads SMD `nodes` and `skeleton` sections, including multiple pose frames.
- Audits hierarchy, node IDs/names, parent references, reference-pose transforms, and cross-slice consistency.
- Compares slices by bone name and parent name; reports numeric node-ID remaps separately.
- Optionally maps raw SMD skeletons to an MDL bone table by name.
- Scans SMD files directly from folders, ZIP, or uncompressed GMA archives without extraction.


- Reads MDL v48, VVD v4, and VTX v7 (DX90 or DX80).
- Opens a direct `.mdl`, a model folder, a `.zip` archive, or an uncompressed `.gma` archive directly.
- Reads GMA members in place; it does not unpack the archive to disk.
- Checks MDL/VVD/VTX checksums and GMA member CRC32 values.
- Lists bones, materials, BodyGroups, model options, and mesh metadata.
- Reconstructs triangle topology from VTX strip/stripgroup indices and maps it to VVD vertices.
- Builds an internal unified scene with one skeleton, a shared vertex pool, and material-separated primitives.
- Exports a skinned GLB with positions, normals, UV0, up to three source bone influences (stored in glTF's four-slot attributes), inverse bind matrices, and a unified BodyGroup selection.
- Converts Source's Z-up coordinate basis to glTF's Y-up basis.
- Validates the rest-pose skeleton against Source `poseToBone` matrices during scene construction.
- Retains SMD export as a compatibility option.
- Writes a companion `.report.json` beside each export.
- Diagnoses MDL bone definitions separately from the bone indices directly referenced by selected geometry vertex weights; it does not infer semantic/runtime bone roles from weight usage.
- Adds `bone-matrix` to compare weight-referenced bone dependencies for every BodyGroup option against the all-default configuration, including added and removed bone indices/names.
- Exports a detailed machine-readable diagnosis JSON containing the complete bone table and all BodyGroup model options.
- Independently validates an exported GLB container, buffer/accessor ranges, primitive indices, Skin joints, inverse-bind matrices, and normalized skin weights.

## BodyGroup/Bone dependency matrix (0.5.0)

Run a full per-option scan:

```powershell
py -3 source_model_reader.py bone-matrix "D:\Models\addon.gma" --model "aes_yz_pm.mdl" --json "D:\Export\bone_matrix.json"
```

The command first measures the all-default BodyGroup selection. It then selects each option one at a time while keeping every other BodyGroup at option 0. For each option it reports geometry coverage, unique bone definitions referenced by positive vertex weights, and the added/removed bone-index sets relative to the all-default selection.

This is a **weight-reference dependency matrix**, not a complete runtime dependency analysis. It cannot infer animation, procedural/jiggle-bone, physics, attachment, or engine-side requirements. An MDL bone definition that is not referenced by the current mesh weights must not be assumed to be useless or safe to delete. SMD source slices may each carry the complete node/skeleton definition even when their triangles use only a subset of that skeleton.

Example shape of the real sample result:

| BodyGroup option | Weighted bone references | Delta vs all-default |
|---|---:|---:|
| Glasses `AES_YJ.smd` | 136 | +0 / -0 |
| Skirt `AES_QZ.smd` | 236 | +100 / -0 |

## SMD skeleton audit (0.6.2)

Scan one SMD file, a directory, ZIP, or uncompressed GMA without extracting archive members. The audit reads the `nodes` and `skeleton` sections, checks node IDs/names/parent references for every file type, classifies common mesh/physics/animation roles, and only compares bind/reference poses when both files are ordinary model slices.

```powershell
# Audit all raw SMD files in a source folder
py -3 source_model_reader.py smd-audit "D:\Models\SourceProject" --json "D:\Export\smd_audit.json"

# Compare SMD slices and map each one to the compiled MDL bone table
py -3 source_model_reader.py smd-audit "D:\Models\SourceProject" --mdl "D:\Models\addon.gma" --model "aes_yz_pm.mdl" --reference "AES_T.smd" --json "D:\Export\smd_audit.json"

# Choose a specific reference slice and a looser tolerance
py -3 source_model_reader.py smd-audit "D:\Models\SourceProject.zip" --reference "models\body.smd" --tolerance 0.001

# Run the included 31-file synthetic fixture for role classification and pose-skip behavior
py -3 source_model_reader.py smd-audit "examples\smd_role_fixture" --json "smd_role_audit.json"
```

Comparison is performed by **bone name**, not numeric node ID. Differences in numeric IDs are reported as remaps; missing/extra names and changed parent-name relationships are checked for every SMD type. Pose comparison is deliberately narrower: it runs only between ordinary geometry slices classified as `model_slice`. Animation, proportion, corrective-animation, animation-reference, ragdoll, physics, and unknown files still receive full node/parent structural checks, but their first frame is not assumed to be a bind pose. The optional `--mdl` argument performs a name-based cross-check against the MDL bone table for all SMDs, while MDL pose comparison is likewise limited to ordinary model slices.

When `--reference` is omitted, the audit prefers `AES_T.smd` when found. Otherwise it chooses the first classified model slice, and only falls back to the first parsed file if no model slice exists. Use `--reference` to select a specific baseline explicitly. Rotation tolerance is measured in radians; translation tolerance uses Source model units. SMD type classification is a conservative path/name/section heuristic, not a claim about the author's exact QC intent; unrecognized files are classified as `unknown` and are excluded from bind-pose comparisons.

Sparse frame records are handled conservatively. In animation-like SMD types, a non-empty frame that explicitly records only some defined nodes is reported as `INFO`, with exact coverage in `frame_coverage`; no missing transform is fabricated or forward-filled. Empty animation frames, unknown node IDs, and incomplete model/physics reference frames remain warnings. This records file contents without claiming a specific runtime inheritance rule.

Run the included sparse corrective-animation fixture to test this behavior:

```powershell
py -3 source_model_reader.py smd-audit "examples\smd_sparse_fixture" --json "sparse_audit.json"
```

The supplied sample Workshop package has no raw `.smd` files, only compiled MDL/VVD/VTX/PHY resources and materials. Running `smd-audit` on that package therefore reports `NOT_FOUND` rather than pretending that the source slices were present. For a quick parser smoke test, use `examples/smd_fixture`; for sparse corrective-animation coverage, use `examples/smd_sparse_fixture`.

See [`ROADMAP.md`](ROADMAP.md) for the milestone sequence and [`HD2_FORMAT_NOTES.md`](HD2_FORMAT_NOTES.md) for the currently implemented HD2 binary structures and their limitations. The HD2 inspector is an independent read-only target-side reader; it does not apply GMod animations to the character. The eventual conversion target is the native Helldivers 2 rig and its runtime animation system.

## What the unified GLB represents

The GLB contains one logical character mesh node and one shared skin/skeleton. Material boundaries are preserved as separate glTF primitives inside that mesh. This is intentional: a complete character should not be welded into one material or lose its submesh/material assignments.

Selected BodyGroup variants are combined into that one character scene. The choices that were not selected are not exported into the active scene. BodyGroups are variant selectors, not a command to layer every option on top of each other.

The GLB currently writes placeholder PBR material values and preserves the Source material names. **VMT/VTF texture decoding and conversion are not implemented yet**, so the model may appear grey or use neutral placeholder materials until that stage is added.

## Requirements

- Python 3.10 or newer.
- No pip packages required by the tool itself.

## Windows quick start

Open PowerShell or Command Prompt in this folder.

Inspect a GMA directly:

```powershell
py -3 source_model_reader.py inspect "D:\Models\addon.gma" --model "character.mdl"
```

Diagnose the selected Source model before exporting:

```powershell
py -3 source_model_reader.py diagnose "D:\Models\addon.gma" --model "aes_yz_pm.mdl" --json "D:\Export\model_diagnosis.json"
```

Diagnose the glasses + skirt combination:

```powershell
py -3 source_model_reader.py diagnose "D:\Models\addon.gma" --model "aes_yz_pm.mdl" --bodygroup 4=1 --bodygroup 18=1 --json "D:\Export\parts_diagnosis.json"
```

Export one unified skinned character to GLB:

```powershell
py -3 source_model_reader.py export-glb "D:\Models\addon.gma" --model "aes_yz_pm.mdl" -o "D:\Export\aes_yz_pm.glb"
```

Enable glasses and skirt in the same character scene:

```powershell
py -3 source_model_reader.py export-glb "D:\Models\addon.gma" --model "aes_yz_pm.mdl" -o "D:\Export\aes_yz_pm_parts.glb" --bodygroup 4=1 --bodygroup 18=1
```

Validate a GLB after export (independent of the Source parser):

```powershell
py -3 source_model_reader.py validate-glb "D:\Export\aes_yz_pm.glb" --json "D:\Export\aes_yz_pm_validation.json"
```

The same commands accept a ZIP, an extracted folder, or a direct MDL path. If an archive contains multiple models, specify the intended one with `--model`.

SMD compatibility export is still available:

```powershell
py -3 source_model_reader.py export "D:\Models\addon.gma" --model "aes_yz_pm.mdl" -o "D:\Export\aes_yz_pm.smd"
```

## Import into Blender

Use Blender's built-in glTF 2.0 importer: **File → Import → glTF 2.0 (.glb/.gltf)**, then select the exported `.glb`. The expected result is one character mesh object with material slots/primitives and one armature hierarchy. Check the rest pose, object orientation, vertex groups, and a few deformations. Textures are not embedded in this alpha release, so neutral materials are expected.

## Output files

- `name.glb`: one skinned character scene, with a shared skeleton and material-separated primitives; animated exports also contain a glTF animation clip.
- `name.report.json`: source archive/model details, BodyGroup choices, counts, material names, output size, and warnings.
- `name.smd`: optional legacy/compatibility export when using `export`.
- `model_diagnosis.json`: optional detailed Source-model diagnosis from the `diagnose` command.
- `model_validation.json`: optional independent GLB structural/skin validation from `validate-glb`.

## Reading diagnostic results

`diagnose` reports both the complete VVD vertex pool and the vertices referenced by the selected BodyGroup combination. It also includes the complete bone table and every BodyGroup option, with per-selection mesh and triangle coverage.

- **Error** means the selected data has a structural issue that needs attention, such as invalid active bone references or a broken hierarchy.
- **Warning** means the data may still be usable but should be inspected, such as unweighted active vertices or abnormal weight sums.
- **Info** is not automatically a defect. An MDL bone definition not directly referenced by the current selection's vertex weights is simply unreferenced by those weights; its animation, procedural, physics, attachment, or runtime role is not inferred. VVD vertices not referenced by one selected BodyGroup combination may belong to alternate clothing or accessories.

`validate-glb` reads the emitted GLB independently and checks its container, binary buffer ranges, mesh/primitive attributes, triangle indices, Skin joint references, inverse-bind matrices, and weight sums. It does not replace a Blender visual test or a live pose/deformation test.

## Supported BodyGroup selection

`inspect` prints BodyGroup indices and available model choices. Specify only the options you want to override; unspecified BodyGroups use choice `0`.

```powershell
--bodygroup 4=1 --bodygroup 18=1
```

means choose model option 1 for BodyGroups 4 and 18.

## Validation

Run the built-in test suite:

```powershell
py -3 -m unittest discover -s tests -v
```

See [`GLB_TEST_REPORT.md`](GLB_TEST_REPORT.md), [`DIAGNOSTICS_TEST_REPORT.md`](DIAGNOSTICS_TEST_REPORT.md), [`BONE_MATRIX_TEST_REPORT.md`](BONE_MATRIX_TEST_REPORT.md), [`SMD_AUDIT_TEST_REPORT.md`](SMD_AUDIT_TEST_REPORT.md), and [`HD2_RESOURCE_TEST_REPORT.md`](HD2_RESOURCE_TEST_REPORT.md) for validation details. `run_bone_matrix.bat` and `run_hd2_inspect.bat` are included as optional Windows helpers.

## Known alpha limitations

- MDL v48 / VVD v4 / VTX v7 only.
- VVD fixup-table remapping is not applied yet. The sample supplied for this test has zero fixups.
- LOD 0 is the highest-detail path tested; other LODs have not received equivalent sample validation.
- Source animation sequence/controller semantics, flexes, jiggle-bone simulation, hitboxes, physics, attachments, and procedural bones are not reconstructed. The separate SMD animation exporter supports ordinary multi-frame animation SMDs only.
- VMT/VTF texture decoding, shader translation, and material texture embedding are not implemented.
- GMA v1-v3 uncompressed GMAD archives are supported. Compressed Workshop `.bin` wrappers are not supported; unpack those with GMAD/7-Zip first.
- The HD2 tools are read-only. `hd2-unit` decodes supported vertex/index layouts and preserves BoneInfo remaps, but does not resolve palettes to TransformInfo names, adapt the target skeleton, convert materials, write Units or package Patches.

## Safety / file handling

The reader only opens model resources and writes the requested output files. It does not modify the original archive or game directory. GMA member paths are treated as logical archive paths; extraction is not performed.

## Format references

The bounded Source layout implementation is based on Valve's Source SDK 2013 public headers: [`studio.h`](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/studio.h) and [`optimize.h`](https://github.com/ValveSoftware/source-sdk-2013/blob/master/src/public/optimize.h). The output container follows the glTF 2.0 / GLB structure.
