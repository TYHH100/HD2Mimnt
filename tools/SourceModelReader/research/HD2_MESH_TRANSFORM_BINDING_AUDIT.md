# HD2 Mesh Transform Binding Audit

Date: 2026-11-05

Status: read-only audit CLI and synthetic regression tests added. Local execution against the three real Unit reports is pending.

## Purpose

Record each direct `MeshInfo.records[].transform_index` binding together with:
- mesh ID, LOD index, decoded vertex/index/triangle counts;
- TransformInfo name/hash and complete parent chain;
- the stored 4x4 matrix and whether it is identity within tolerance;
- per-Unit hierarchy-matrix validation;
- actual skinning status, resolved active influence lanes, and BoneInfo palette-matrix validation.

The audit does not apply matrices to vertex positions. It does not infer a culling proxy from a node name.

## Run

From `tools/SourceModelReader`:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_mesh_transform_audit.py" -v
py -3 .\research\audit_hd2_mesh_transform_bindings.py
```

Outputs:
- `research/skeleton_comparison/hd2_mesh_transform_audit.md`
- `research/skeleton_comparison/hd2_mesh_transform_audit.json`

Optional explicit input paths:
```powershell
py -3 .\research\audit_hd2_mesh_transform_bindings.py `
    --lower-body .\research\skinning_audits\fs05_lower_body.json `
    --torso-left-arm .\research\skinning_audits\fs05_torso_left_arm.json `
    --avatar .\research\skinning_audits\avatar_composite.json
```

## Current observations from the checked-in sample audit reports and manual matrix inspection

### FS-05 lower body

- MeshInfo binds four ordinary render/LOD meshes:
  - shadow: Transform 5, LOD 3;
  - LOD2: Transform 6, LOD 2;
  - LOD1: Transform 7, LOD 1;
  - base mesh: Transform 8, LOD 0.
- All four direct mesh TransformInfo matrices are identity.
- The additional `hips_culling` binding is Transform 13, LOD -1. Its geometry reports `no_skin_attributes`; it is not an actively skinned render LOD in this sample.
- Its non-identity matrix is inherited along the `hips -> boss -> root -> skeleton` branch. The hierarchy validator reports a match.

### FS-05 torso / left arm

- The `l_arm_culling` binding is Transform 14, LOD 4, with 439 vertices.
- It has valid skin attributes and 2,616 resolved active influence lanes.
- Its BoneInfo matrix check reports 5 matched active palette slots, 0 mismatches and 0 unavailable slots; maximum reported error is approximately `9.37e-8`.
- Its TransformInfo hierarchy matrix check reports a match, with maximum error approximately `9.37e-8`.
- The other four direct render/LOD transforms (shadow, LOD2, LOD1, base) store identity matrices.
- Therefore the word `culling` in the node name is not enough to classify this mesh as unskinned or disposable. The LOD/geometry/skinning records are the evidence.

### Avatar CompositeUnit

- The single direct MeshInfo binding (Transform 102) stores an identity matrix.
- Its hierarchy matrix validation reports a match.
- Its geometry contains 27,355 vertices and 42,158 resolved active influence lanes, as recorded in the existing skinning audit.

## Matrix convention and limits

The Unit parser reconstructs hierarchy matrices using a row-major 4x4 row-vector candidate and the composition candidate `local_times_parent_world`, then compares the reconstructed matrix with the stored TransformInfo matrix. The parser records these as candidates; scale/rotation order is not independently proven for every possible transform.

The BoneInfo check compares active palette matrices with the candidate formula `mesh_world_times_inverse_bone_world` using the row-vector convention. The selected samples match within small floating-point errors. This is internal consistency evidence, not proof of final in-game shader behavior or a universal inverse-bind contract.

The manually inspected ordinary render/LOD nodes currently have identity stored matrices, while two special bindings have non-identity matrices:
- `hips_culling`: no skin attributes in the lower-body sample;
- `l_arm_culling`: actively skinned LOD 4 in the torso/left-arm sample.

Do not generalize those two samples to every Unit or infer how the engine consumes the special meshes without further resource/engine evidence.

## Files

- `research/audit_hd2_mesh_transform_bindings.py`: read-only CLI.
- `tests/test_hd2_mesh_transform_audit.py`: synthetic regression tests.
- `research/HD2_CROSS_UNIT_TRANSFORM_COMPARISON.md`: cross-Unit TransformInfo comparison.
