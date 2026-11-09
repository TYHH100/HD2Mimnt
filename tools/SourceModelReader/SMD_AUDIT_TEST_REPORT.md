# SMD Skeleton Audit Test Report — 0.6.2-alpha

## 0.6.2 behavior

- Animation-like SMD roles (`animation`, `proportion_animation`, `corrective_animation`, `animation_reference`, and `ragdoll_pose`) may contain non-empty sparse transform frames. These are reported as informational diagnostics rather than parse warnings.
- Sparse handling is intentionally observational: the parser records explicit and omitted transforms but does not synthesize missing poses or claim that omitted values inherit previous frames.
- Missing transforms in ordinary model slices and physics geometry remain warnings. Empty frames remain warnings for every type. Transforms referencing undefined node IDs remain warnings for every type.
- JSON reports include per-frame coverage: declared nodes, explicit transforms, omitted transforms, unknown transform IDs, coverage ratio/percent, and sparse status.
- Summary reports include warning/info groups and sparse frame counts.

## Automated tests

`py -3 -m unittest discover -s tests -v`

- 38 tests pass in the build environment.
- Added tests for sparse corrective frames, sparse model slices remaining warnings, empty animation frames remaining warnings, and an overall audit staying `OK` when the only diagnostic is a recognized sparse corrective frame.

## Sparse corrective-animation fixture

Input: `examples/smd_sparse_fixture`

- `AES_T.smd`: 255 node definitions, full time-0 transform set, one triangle.
- `aes_yz_pm_anims/a_proportions_corrective_animation.smd`: the same 255 node definitions, but only one explicit transform at time 0.
- Expected result: no structure differences; corrective animation frame reports 1/255 explicit transforms and 254 omitted as `INFO`; overall status remains `OK`.
- The fixture is synthetic and is not the user's original SMD file. It reproduces the sparse record pattern shown in the user's console output.

## Regression boundaries

- The 0.6.1 local log supplied by the user identifies `a_proportions_corrective_animation.smd` as the sole warning source: frame 0 contains one explicit transform and omits 254 of 255 node transforms.
- The original 31 SMD files are not present in this build container. Therefore the new policy is tested against a synthetic fixture matching the observed record shape, not by re-reading the user's real corrective-animation file.
- Sparse transform records are preserved as authored. This release does not reconstruct omitted transforms, evaluate QC `$sequence` delta/predelta behavior, or implement Source animation playback.

