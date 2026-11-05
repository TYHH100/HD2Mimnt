# HD2 Matrix Composition Census

- Package: `8670598c1f4462dc`
- Unit entries: 22; Units censused: 22; failures: 0
- Matrices classified: 1210
- Refused composition (rotation with non-uniform scale, shear, degenerate, unparseable) present: **False**
- Mirrored bases present: **False** (0)

## Counts by basis class

| Basis class | All | TransformInfo | BoneInfo palette |
|---|---:|---:|---:|
| basis_identity | 291 | 264 | 27 |
| basis_uniform_scale | 0 | 0 | 0 |
| basis_reflection | 0 | 0 | 0 |
| basis_anisotropic_scale | 0 | 0 | 0 |
| basis_rotation | 919 | 233 | 686 |
| basis_rotation_with_non_uniform_scale | 0 | 0 | 0 |
| basis_non_orthogonal | 0 | 0 | 0 |
| basis_degenerate | 0 | 0 | 0 |
| basis_unparseable | 0 | 0 | 0 |

## Matrices carrying a non-unit scale

None - every basis row has unit length.

## Scale spread

- Scale product closest to zero: 0.99999967; largest: 1.00000006
- Largest axis-scale ratio: 1.00000025; ratios above 1 + 0.0001: 0
- Max bottom-row deviation from `0 0 0 1`: 0.0

## Units carrying a mirrored basis

- None.

## Unusual cases

No mirrored, sheared, rotation-with-non-uniform-scale, degenerate or unparseable basis found.

## Per-Unit totals

| Unit | Composite | Transforms | BoneInfo records | Matrices | Identity | Uniform scale | Rotation | Mirrored |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| `210fb2f60f8308ea` | no | 16 | 4 | 26 | 13 | 0 | 13 | 0 |
| `324fb206612ce4f7` | no | 38 | 5 | 134 | 14 | 0 | 120 | 0 |
| `3bdfa900f32cd83f` | no | 18 | 4 | 38 | 13 | 0 | 25 | 0 |
| `418b4fb786159bac` | no | 17 | 4 | 34 | 13 | 0 | 21 | 0 |
| `64a71fbe6ef75b80` | no | 27 | 4 | 70 | 14 | 0 | 56 | 0 |
| `74b2e79c1c5af767` | no | 16 | 4 | 26 | 13 | 0 | 13 | 0 |
| `755f0bcd4d36a76d` | no | 19 | 4 | 29 | 13 | 0 | 16 | 0 |
| `7e5111d05840be1a` | no | 20 | 4 | 39 | 13 | 0 | 26 | 0 |
| `8d02a82d0c214171` | no | 19 | 4 | 44 | 13 | 0 | 31 | 0 |
| `901fc9c35857dad2` | no | 20 | 4 | 39 | 13 | 0 | 26 | 0 |
| `90b57e547e7ff7bb` | no | 19 | 4 | 29 | 13 | 0 | 16 | 0 |
| `99a130858b3d8ff1` | no | 19 | 4 | 29 | 13 | 0 | 16 | 0 |
| `99f001cb5a6e7116` | no | 16 | 4 | 26 | 13 | 0 | 13 | 0 |
| `be65eae060a0267c` | no | 19 | 4 | 40 | 13 | 0 | 27 | 0 |
| `c4c7a5de567a87fe` | no | 16 | 4 | 26 | 13 | 0 | 13 | 0 |
| `d0007ea2f834da58` | no | 38 | 5 | 134 | 14 | 0 | 120 | 0 |
| `d19d17bc42ab9275` | no | 17 | 4 | 34 | 13 | 0 | 21 | 0 |
| `db487f9a3f738dc5` | no | 38 | 5 | 131 | 13 | 0 | 118 | 0 |
| `e15680e4c4a89420` | no | 29 | 4 | 82 | 15 | 0 | 67 | 0 |
| `e40aa3f8f6141a86` | no | 38 | 5 | 131 | 13 | 0 | 118 | 0 |
| `effdfd2386aef629` | no | 19 | 4 | 29 | 13 | 0 | 16 | 0 |
| `f2715c19b24ecd8b` | no | 19 | 4 | 40 | 13 | 0 | 27 | 0 |

## Interpretation limits

- A census of stored matrices; it says nothing about how the runtime composes them.
- Only the 3x3 basis is classified, so a translation is never read as scale or rotation.
- Row normalization separates `anisotropic scale then rotation` (orthogonal rows of unequal length) from `rotation then anisotropic scale` (sheared rows).
- `basis_identity` means orthonormal unit rows; it does not assert the whole 4x4 matrix equals the identity, because the translation is excluded.
- Mirroring is decided by the determinant of the normalized basis and takes the `basis_reflection` class; its scale and rotation indicators remain in the row.
- Surveying one package says nothing about packages that were not read.

