# 0.6.1 Synthetic 31-file SMD regression

This is a **synthetic fixture**, not a copy of the user's decompiled model assets. It mirrors the 31-file role/name layout in the user's 0.6.0 PowerShell output and uses a generated 255-node skeleton in every file. Five animation-related files contain deliberately changed first-frame transforms so the pose-skip policy can be checked.

Expected command:

```powershell
py -3 source_model_reader.py smd-audit examples/smd_role_fixture --json smd_role_audit.json
```

Observed with 0.6.1-alpha:

- 31 files parsed; 0 parse errors.
- `AES_T.smd` selected automatically as the reference.
- Type counts: 25 `model_slice`, 1 `physics_geometry`, 1 `proportion_animation`, 1 `corrective_animation`, 1 `animation`, 1 `ragdoll_pose`, 1 `animation_reference`.
- 30 structural comparisons, all clean.
- 24 model-slice pose comparisons; 6 pose comparisons skipped by type.
- Overall status: `OK`.

The fixture validates the new classification/selection/skip behavior. It does **not** replace the user's follow-up test against the original 31 decompiled SMD files, which are not present in this development container.
