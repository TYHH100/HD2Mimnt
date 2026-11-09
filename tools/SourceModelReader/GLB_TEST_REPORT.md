# GLB export test report — 0.3.0-alpha baseline

## Test input

The real `gmpublisher.gma` embedded in the sample addon archive was used for integration testing. The archive is GMA v3 and contains 82 files. The selected model is `models/lc_84/aes_yz_pm.mdl` (MDL v48, VVD v4, VTX v7; checksum 43233072).

## Integration results

| Check | Default BodyGroups | Glasses + skirt |
|---|---:|---:|
| BodyGroup overrides | none | `4=1`, `18=1` |
| Bones | 255 | 255 |
| Unique unified vertices | 64,301 | 69,191 |
| Triangles | 106,663 | 114,309 |
| Material primitives | 14 | 14 |
| GLB file size | 4,963,828 bytes | 5,329,428 bytes |
| Bind-pose warning | none | none |

The triangle counts match the previous SMD reconstruction path for both combinations. The GLB contains one mesh node and one shared skin with 255 joints; material splits are represented as primitives within that mesh.

## Additional structural validation

- GLB header, version, total length, JSON chunk, and BIN chunk were parsed and checked.
- The inverse-bind-matrix accessor contains one MAT4 per joint.
- The glTF JSON references the shared vertex attributes, and primitive index totals match the expected triangle counts.
- The GLB files were loaded successfully by the independent `trimesh` GLB reader in the development environment. It reported 14 geometry primitives and the expected face totals.
- The Source bone hierarchy and `poseToBone` inverse bind matrices were cross-checked; maximum rest-pose transform error remained below the warning threshold for this sample.
- All exported vertex weights were finite, stayed in the 0–1 range, and summed to 1.0 within `4.5e-8`; all joint indices stayed within the 255-joint skeleton.
- Built-in unit tests: **10/10 passed**.

## Not yet visually verified

Blender was not installed in the development environment, so a human visual inspection in Blender has not been completed. The user should verify mesh appearance, orientation, normals, UVs, armature hierarchy, and deformation locally before treating the GLB as a production conversion source.

## Known material limitation

Material names are retained and each GLB material has a neutral placeholder PBR setup. VMT/VTF textures and Source shader parameters are not converted in this release.

## 0.4.0-alpha regression re-validation

The new independent `validate-glb` command re-opened both default and glasses + skirt GLBs generated from the same GMA sample:

| Check | Default | Glasses + skirt |
|---|---:|---:|
| Mesh nodes | 1 | 1 |
| Primitives | 14 | 14 |
| Triangles | 106,663 | 114,309 |
| Skin joints | 255 | 255 |
| Unique shared vertices checked | 64,301 | 69,191 |
| Weight-sum failures | 0 | 0 |
| Independent validator status | OK | OK |

The 0.4.0-alpha built-in test suite now contains **15 passing tests**.

## 0.5.0-alpha regression re-validation

After the 0.5.0 diagnostic terminology and BodyGroup/Bone matrix changes, the real GMA was exported again through `export-glb` and re-opened with `validate-glb`.

| Metric | 0.5.0 result |
|---|---:|
| Mesh nodes | 1 |
| Material primitives | 14 |
| Triangles | 106,663 |
| MDL bone definitions / GLB Skin joints | 255 / 255 |
| Unified vertices | 64,301 |
| Bad weight sums | 0 |
| Validation status | OK |

The DX80 backend also reproduced the default diagnostic counts from DX90: 64,301 referenced vertices, 106,663 triangles, 255 MDL bone definitions, and 136 bone indices directly referenced by the selected geometry's weights.

The 0.5.0 automated test suite contains **16 passing tests**.
