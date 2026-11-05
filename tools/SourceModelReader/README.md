# Source Model Reader (alpha 0.8.4)

A dependency-free Python tool for reading a bounded subset of Source 1 model resources, reconstructing unified skinned scenes, and independently inspecting Helldivers 2 resource packages. The HD2 path is read-only and experimental.

Material research notes: [MATERIAL_FORMAT_NOTES.md](MATERIAL_FORMAT_NOTES.md). These document the observed Material/Texture resource layout and transparency distinctions; no automatic material conversion is implemented yet.

Large game-data inventories, full Unit dumps, raw census JSON, and exported texture binaries/previews are local generated artifacts. They are intentionally excluded by `.gitignore`; the durable research findings are kept in Markdown and compact metadata JSON. The commands below still generate local output files when needed.

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

### Read-only Material inspection (experimental)

The 0.8.4 branch includes a bounded read-only Material resource parser. It extracts the 64-bit ParentMaterialID, raw texture-node type IDs, linked Texture resource IDs, shader-variable descriptor fields and the raw variable-value payload. It checks whether each referenced Texture resource exists in the selected package.

It deliberately does not guess a material's rendering mode. ParentMaterialID aliases differ between SDK forks, and texture alpha may mean cutout, opacity, emissive mask or other shader data depending on the parent material.

Example:

```powershell
py -3 .\source_model_reader.py hd2-material "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 9ba626afa44a3aa3 --material 0123456789abcdef --json "material_report.json"
```

Replace the sample Material ID with a 16-digit hexadecimal ID from an inspected Unit or package inventory. Texture dependencies absent from the selected package are reported as not present in that package; they may reside in another archive. The JSON preserves raw variable payload bytes, with only a conservative first scalar/vector preview for known classes. Arrays, strides, parent aliases and actual alpha/blend behavior remain unresolved.

`VariableDataSize` is treated as the declared variable-value region. Bytes following that region are preserved separately as unresolved trailing data; three inspected FS-05 Material samples each had an 8-byte trailer. This is sample evidence, not a universal format guarantee.

### Full-game Material census

Use `hd2-material-census` to parse Material TOC payloads across the indexed SLIM packages. It scans package type/entry tables, then reads only the byte ranges belonging to Material resources. It does **not** reconstruct GPU or Stream sidecars and does not decode Texture payloads.

Start with a small diagnostic scan:

```powershell
py -3 .\\source_model_reader.py hd2-material-census "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data" --max-packages 20 --json ".\\research\\hd2_material_census_smoke.json"
```

Run the complete scan after the smoke test has been checked:

```powershell
py -3 .\\source_model_reader.py hd2-material-census "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data" --json ".\\research\\hd2_material_census.json"
```

The JSON report records each Material FileID, package occurrence, TOC byte count, raw ParentMaterialID, payload SHA-256, texture-layout signature and shader-layout signature. Aggregate sections count ParentMaterialIDs, raw texture-node type IDs and raw shader-variable IDs. It also distinguishes repeated FileIDs from identical payload hashes, so a same-ID copy with different bytes is not silently merged.

Use `--include-descriptors` only when a detailed per-material listing is needed; it adds every ordered texture-node pair and shader-variable descriptor/preview to each Material row and makes the report larger. `--max-packages` always produces a partial scan and the JSON summary marks it incomplete. Package errors and individual Material parse errors are isolated and recorded.

Example with detailed descriptors for a deliberately small scan:

```powershell
py -3 .\\source_model_reader.py hd2-material-census "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data" --max-packages 5 --include-descriptors --json ".\\research\\hd2_material_census_detailed.json"
```

The census preserves identifiers as raw values. A ParentMaterialID matching a Material FileID is reported as an identity match only; it is not taken as proof of inheritance semantics. Texture-node and shader-variable names require pinned, versioned dictionaries, and no render-mode or transparency classification is inferred.

### Locate out-of-package resource references

Use `hd2-find` to locate Texture FileIDs across the full SLIM package set. It reads only package TOC entry tables in bounded chunks; it does not load Material, Texture, GPU or Stream payloads:

```powershell
py -3 .\\source_model_reader.py hd2-find "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data" --type Texture --resource f8b2508e99fb9f28 --resource 0d371130ba00addb --resource 7db10f0a89343dc7 --resource cf0cc31b981786c9 --resource 51260792de823d20 --resource e21128ca48e27a13 --resource 17c4075f57e4211a --resource f37a4e756300ab32 --resource c6faeeb7ba467e0b --resource 8e346e2c8187abaf --resource bfefc8b1b381a00f --resource f001498ea40718fb --resource 4656ac419388a0d4 --resource 68acd4177fa5f4db --resource 8e42f23230ab525e --resource 7fc8b2e634181f66 --resource 82ba9a2d6997cc4b --resource bf281073a422022c --resource 9e0595f7f9d158d1 --resource 486c27ee43065178 --json "fs05_texture_locations.json"
```

A hit reports every package containing the requested FileID and its resource type/sidecar sizes. A miss means "not found in successfully scanned packages"; an incomplete scan or package error prevents a global-absence conclusion. `--max-packages` is available for partial diagnostics, but should be omitted for a full lookup.



### Read-only Texture inspection and DDS export

The 0.8.4 branch now includes `hd2_texture.py` and `hd2-texture`. The reader uses the global SLIM package index to locate one Texture entry inside a selected package, then reads only that resource's TOC/GPU/Stream ranges. It does not reconstruct the full `.gpu_resources` or `.stream` sidecar.

The common Texture TOC layout observed in the pinned AQ and Community SDK readers is:

- 12-byte fixed prefix (`UnkID`, `Unk1`, `Unk2`);
- 15 mipmap records, each 12 bytes (`Start`, `BytesLeft`, `Height`, `Width`);
- 148-byte DDS DX10 header.

That is 340 bytes before any trailing TOC data. The parser preserves unknown trailing bytes and all 15 raw mipmap records, and reports DDS dimensions, DXGI format, mip count, resource dimension and array size. Unknown DXGI values remain numeric rather than being guessed.
For known BC and common uncompressed DXGI formats, the report calculates every mip's expected byte size, cumulative offset and remaining-byte count. It classifies GPU/Stream payloads as a complete chain or a matching contiguous mip tail when the sizes prove that relationship, and checks populated TOC mipmap records against the calculated chain. An entirely zeroed mipmap table is reported as `not_populated`, not silently treated as a failed decode.
The selected Texture entry is checked against the observed SLIM TOC row convention: stored `entry_index` must equal the zero-based TOC row. The report keeps both values separately (`entry_index` and `toc_row_index`). The two tested game entries confirm this relationship (rows 3678 and 3687 store the same values). Do not generalize this result to unrelated patch-file formats without checking their own format rules.

Example:

```powershell
py -3 .\\source_model_reader.py hd2-texture "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data" --package 18235e0c9ec0e636 --texture 0d371130ba00addb --output "texture_0d371130ba00addb.dds" --preserve-raw --json "texture_0d371130ba00addb.json"
```

DDS payload selection follows the inspected public SDK read behavior: use Stream when its payload is non-empty, otherwise use GPU. `--preserve-raw` also writes separate `.gpu.bin` and `.stream.bin` copies when those payloads exist, so a resource with both sidecars is not reduced to one representation. The JSON report includes each payload's byte count and SHA-256 hash for package-copy comparison.

A FileID can appear in multiple archives. Use the exact package IDs returned by `hd2-find` and inspect each candidate separately; differing sizes or hashes mean those package copies must remain distinct during analysis. This command does not infer that an alpha channel is transparency, cutout or emissive data. Actual alpha interpretation still requires texture decoding and Material/render-family evidence.


### Optional BC7 pixel preview and alpha statistics

BC7 preview uses the optional third-party `texture2ddecoder` package; it is not a required Source Model Reader dependency and no decoder source or binary is vendored into this repository.

`texture2ddecoder` 1.0.6 is published under the MIT License and provides `decode_bc7`, which returns BGRA pixel bytes. PyPI provides a CPython 3.11+ Windows x86-64 ABI3 wheel, compatible with CPython 3.12. The BCn decoder source is attributed by that project to Perfare's AssetStudio BCn implementation.

Install only when a pixel preview is needed:

```powershell
py -3 -m pip install texture2ddecoder==1.0.6
py -3 -c "import texture2ddecoder; print(texture2ddecoder.__version__, callable(texture2ddecoder.decode_bc7))"
```

Add `--preview-png` to an existing `hd2-texture` command. The base-mip preview is written beside the requested DDS using the same filename stem and a `.png` extension. The JSON report adds the decoder version, decoded dimensions and input/output byte counts, plus the 256-bin Alpha histogram and counts for fully transparent, partial-alpha and fully opaque pixels.

Example:

```powershell
py -3 .\source_model_reader.py hd2-texture "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 18235e0c9ec0e636 --texture 0d371130ba00addb --output ".\texture_0d371130ba00addb.dds" --preserve-raw --preview-png --json ".\texture_0d371130ba00addb.json"
```

The first implementation is deliberately limited to BC7_UNORM/BC7_UNORM_SRGB, Texture2D, ArraySize 1, and a selected payload classified as a complete mip chain. It decodes Mip 0 only; it refuses to decode a resident mip tail as if it were the full-resolution image. The preview is a pixel-data inspection aid, not a final in-game material render. An alpha histogram describes values stored in the texture; it does not prove that a Material uses those values for fractional blending rather than cutout, emissive masking or another shader path.

Third-party source and license: [texture2ddecoder](https://github.com/K0lb3/texture2ddecoder) (MIT; optional external package), whose README attributes its BCn codec to [Perfare/AssetStudio](https://github.com/Perfare/AssetStudio/tree/master/Texture2DDecoder). The Source Model Reader does not copy or embed either implementation.


See [MATERIAL_FORMAT_NOTES.md](MATERIAL_FORMAT_NOTES.md) for the current binary layout and transparency research.

## Installing or refreshing the 0.8.4 patch over an existing supported folder

Download the following into a separate temporary folder, preserving the `tests` subfolder:

- `install_0.8.4.py`
- `source_model_reader.py`
- `hd2_unit.py`
- `hd2_resources.py`
- `hd2_material.py`
- `hd2_material_census.py`
- `hd2_texture.py`
- `VERSION`
- `tests/test_hd2_unit.py`
- `tests/test_hd2_material.py`
- `tests/test_hd2_material_census.py`
- `tests/test_hd2_texture.py`
- `tests/test_hd2_resources.py`
- `tests/test_smd_animation.py`
- `tests/test_smd_reader.py`

Run the installer and target the existing Source Model Reader folder:

```powershell
py -3 .\install_0.8.4.py --root "G:\Temp\Githud\HD2Mimnt\tools\SourceModelReader"
```

The installer accepts `0.8.2-alpha`, `0.8.2.1-alpha`, `0.8.3-alpha` or an earlier `0.8.4-alpha` patch build, creates `.pre-0.8.4.bak` backups only when absent, and does not modify Helldivers 2 game files. After installation, run the test suite:

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


### Advisory humanoid alias candidates

The comparison command also writes a separate `Advisory humanoid alias candidates` section. It currently contains only explicit conventional ValveBiped-to-Avatar name hypotheses for the humanoid core. It does not change the exact-name result, perform fuzzy matching, or approve retargeting.

A candidate's parent check is performed only when its Source parent also has an explicit alias candidate. The expected HD2 parent is then compared with the actual parent of the target candidate. A mismatch is a useful warning that the proposed name correspondence may be structurally wrong; it is not silently repaired. A missing Source/target row is reported as absent rather than guessed.

Hair, ear, tail, ribbon, Qun/VRD, physics and accessory chains are intentionally excluded. Finger mappings are also deferred until the core hand hierarchy and target finger semantics have been inspected. All aliases remain advisory until rest-pose, coordinate-space and deformation checks pass.

Run the alias-specific tests with:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_bone_aliases.py" -v
```

## Source-to-HD2 skeleton comparison (experimental)

The research helper `research/compare_source_hd2_skeleton.py` compares the compiled Source MDL bone table and positive-weight VVD bone usage with a decoded HD2 Unit skeleton. It reads the Source model from a GMA, ZIP, model directory, or MDL file, then reads the target Unit from the installed game's data directory.

Example using the GMod sample and the known HD2 Avatar target Unit:

```powershell
py -3 .\\research\\compare_source_hd2_skeleton.py `
    --source-container "D:\\Models\\gmpublisher.gma" `
    --model "models/lc_84/aes_yz_pm.mdl" `
    --game-data "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data"
```

The default VTX sibling is `.dx90.vtx`; override it with `--vtx-extension ".dx80.vtx"` when needed. The default target is the previously inspected Avatar Unit (`18235e0c9ec0e636 / 4d1c334d294dfa97`). Use `--target-package` and `--target-unit` to compare another HD2 Unit.

The report includes:
- Exact case-insensitive bone-name matches (edge whitespace is trimmed; punctuation and aliases are not guessed).
- Source bones without an exact target name, separately showing which source bones receive positive VVD vertex weights.
- Named HD2 transforms absent from the Source skeleton.
- Active target vertex-influence usage for each matched target transform.
- Counts of active Source bones that exactly match, remain unmapped, or are ambiguous.
- Parent-hierarchy mismatches, unresolved names, and ambiguous target names.

The active Source bone set is the union of positive-weight lanes across the VVD vertex pool. It is not yet split by BodyGroup or LOD, and the current Source reader warns when VVD fixup tables are present because LOD remapping is not applied. Do not treat the union count as the exact deform-bone set of one BodyGroup selection.

This is a diagnostic baseline only. It does not apply a retarget, rename bones, or compare rest-pose numbers. Source MDL/VVD and HD2 TransformInfo require a validated axis/handedness, unit-scale, matrix-space, and bind-pose conversion before numeric pose comparison is meaningful.

The JSON output is local/ignored by default. The Markdown report is written to `research/skeleton_comparison/source_hd2_skeleton.md` and can be committed/shared after review. Exit code 1 means a report was produced but mapping/structure warnings were found; it is not an extraction crash. Run its six synthetic regression tests with:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_skeleton_compare.py" -v
```
