# 0.7.0-alpha Animation Export Test Report

## Automated tests

- `python -m unittest discover -s tests -v`
- Result: **45/45 passed**.
- Covers Euler-to-quaternion normalization, dense and sparse tracks, non-fabrication of missing transform records, model skeleton name/parent mapping, animated GLB export, valid animation accessors, and rejection of non-monotonic animation times.

## End-to-end sample-model smoke test

The project was opened from the supplied `3647656951.zip`, model `aes_yz_pm.mdl`.

An included synthetic SMD was generated from the sample MDL's 255 bone names, parent hierarchy, local positions, and local rotations. It has two authored frames (`time 0` and `time 30`) at an explicitly supplied 30 FPS. The second frame rotates the right upper arm and forearm by small test offsets. This is synthetic data, not an original Workshop-author animation.

Observed export:

| Metric | Result |
|---|---:|
| Bone definitions / Skin joints | 255 |
| Unified vertices | 64,301 |
| Triangles | 106,663 |
| Material primitives | 14 |
| Source frames | 2 (`0`, `30`) |
| Clip duration | 1 second |
| Animated bones | 2 |
| Static tracks elided | 253 |
| Animation channels | 4 |

The resulting GLB passed the independent validator:

- Status: `OK`
- Mesh nodes: 1
- Primitives: 14
- Skin joints: 255
- Bad skin-weight sums: 0
- Animations: 1
- Animation channels: 4
- Animated nodes: 2

## Not yet verified

- The original `idle_all_01.smd` has not been provided to this environment as bytes; the user has it in their local decompiled directory.
- Blender has not been run in this environment. Actual playback, axis orientation, and deformation need user verification in Blender.
- The frame rate must be supplied from the corresponding QC/project configuration. The tool intentionally does not guess FPS.
- Source controllers, procedural/jiggle bones, IK, flexes, animation blending, proportion/corrective animation semantics, and ragdoll runtime behavior are not exported in 0.7.0-alpha.

## Coordinate conversion sanity check

For the supplied sample MDL, all 255 bone Euler rotations converted from SMD XYZ Euler values matched the MDL's stored quaternions after Source-to-glTF basis conversion, within floating-point tolerance. This checks the rest-pose rotation conversion against the compiled model's independent quaternion representation; it does not replace Blender playback validation.
