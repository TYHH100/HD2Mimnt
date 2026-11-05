# Source Model LOD Coverage Survey

- Input: `gmpublisher.gma`
- Models profiled: 7; failures: 1
- Models with more than one VVD LOD: 0
- Highest VVD LOD count: 1; highest VTX LOD count: 1
- Models carrying VVD fixups: 0
- LOD rows whose weighted bone set differs from their own LOD 0: 0
- Distinct skeletons: 4

| Model | Bones | Bodygroups | Options | Textures | VVD lods | VTX lods | Fixups | LOD 0 bones | All LOD bones |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---|
| `models/fj/aes_jh_y.mdl` | 1 | 1 | 1 | 1 | 1 | 1 | 0 | 1 | 1 |
| `models/fj/aes_jh_z.mdl` | 1 | 1 | 1 | 1 | 1 | 1 | 0 | 1 | 1 |
| `models/fj/aes_xz_y.mdl` | 1 | 1 | 1 | 1 | 1 | 1 | 0 | 1 | 1 |
| `models/fj/aes_xz_z.mdl` | 1 | 1 | 1 | 1 | 1 | 1 | 0 | 1 | 1 |
| `models/lc_84/aes_yz_pm.mdl` | 255 | 20 | 42 | 17 | 1 | 1 | 0 | 136 | 136 |
| `models/lc_84e/aes_yz_e.mdl` | 243 | 20 | 42 | 17 | 1 | 1 | 0 | 124 | 124 |
| `models/lc_84f/aes_yz_f.mdl` | 255 | 20 | 42 | 17 | 1 | 1 | 0 | 136 | 136 |

## Skeleton groups

- 255 bones: `models/lc_84/aes_yz_pm.mdl`, `models/lc_84f/aes_yz_f.mdl`
- 1 bones: `models/fj/aes_jh_y.mdl`, `models/fj/aes_xz_y.mdl`
- 1 bones: `models/fj/aes_jh_z.mdl`, `models/fj/aes_xz_z.mdl`
- 243 bones: `models/lc_84e/aes_yz_e.mdl`

## Per-LOD bone sets

- `models/fj/aes_jh_y.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.
- `models/fj/aes_jh_z.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.
- `models/fj/aes_xz_y.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.
- `models/fj/aes_xz_z.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.
- `models/lc_84/aes_yz_pm.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.
- `models/lc_84e/aes_yz_e.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.
- `models/lc_84f/aes_yz_f.mdl`: single VVD LOD, so per-LOD bone use cannot be measured on this model.

## Failures

- `models/shoubi/aes_yz_sb.mdl`: DX90 attempt: This alpha supports MDL v48 only; found v49
DX80 attempt: Missing sibling resource in GMA: models/shoubi/aes_yz_sb.dx80.vtx

## Interpretation limits

- Counts come from the compiled headers and the stored vertex pools; nothing was rendered or converted.
- Per-LOD bone sets are measured with every BodyGroup at its default choice, so a bodygroup-only bone can still look shared.
- A model with one VVD LOD cannot contribute to the per-LOD question at all; it is listed rather than skipped so the coverage gap stays visible.
- Skeleton groups compare lowercased bone-name sets, which is a name-level comparison, not a semantic mapping.

