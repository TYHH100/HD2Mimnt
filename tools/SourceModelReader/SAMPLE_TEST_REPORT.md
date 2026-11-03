# Sample test report

Test input: the uploaded `3647656951.zip`, selecting `gmpublisher/models/lc_84/aes_yz_pm.mdl`.

## Parsed headers

| Resource | Version | Checksum |
|---|---:|---:|
| MDL | 48 | 43233072 |
| VVD | 4 | 43233072 |
| DX90 VTX | 7 | 43233072 |
| DX80 VTX | 7 | 43233072 |

The checksums matched. The reader found 255 bones, 94,858 VVD LOD0 vertices, and 20 BodyGroups.

## Structural checks

- All 94,858 decoded vertex records had finite position, normal, UV, and weight values.
- Every populated vertex weight set summed to approximately 1.0 (observed min/max: 0.99999985 / 1.00000015).
- Highest referenced bone index: 254. All parsed bone parent indices were within the model's bone range or `-1`.
- DX90 and DX80 exports produced the same reconstructed topology counts for this model.
- The generated SMD was checked for 106,663 material/triangle blocks and 319,989 vertex records.

## Export results

| Export | BodyGroup overrides | Triangles | Referenced VVD vertices |
|---|---|---:|---:|
| Default | none (all options default to 0) | 106,663 | 64,301 |
| Variant | `1=1, 2=1, 3=1, 4=1, 16=1, 17=1, 18=1, 19=1` | 114,353 | 69,307 |

The default SMD was approximately 40.8 MiB. This report records parser-level verification only; an actual visual import in Blender has not been performed in this environment.
