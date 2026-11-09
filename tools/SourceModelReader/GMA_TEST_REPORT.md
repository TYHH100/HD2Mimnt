# Direct GMA integration test report — alpha 0.2.0

## Test input

The uploaded `3647656951.zip` contained the original `gmpublisher.gma` as an archive member. That original GMA was tested directly, without extracting its contents.

| Property | Result |
|---|---|
| GMA format | GMAD v3 |
| Addon name | 艾尔莎-泳装水手服 PM/NPC |
| Author field | Author Name |
| Entries | 82 |
| GMA file size | 278,839,903 bytes |
| Target model | `models/lc_84/aes_yz_pm.mdl` |
| GMA sibling resources | MDL, VVD, DX90 VTX |
| CRC32 validation | All three selected resources passed |

## Direct GMA model results

| Test | Bones | Triangles | Referenced VVD vertices |
|---|---:|---:|---:|
| Default BodyGroups | 255 | 106,663 | 64,301 |
| `--bodygroup 4=1 --bodygroup 18=1` | 255 | 114,309 | 69,191 |

The direct GMA reader produced the same SMD bytes as the current ZIP backend when both were pointed at the identical underlying MDL/VVD/VTX resources.

## Automated tests

`python -m unittest discover -s tests -v` — **8/8 passed**. This includes GMA metadata/member reads, case-insensitive lookup, CRC mismatch rejection, path traversal rejection, and truncated payload rejection.

## Scope

The GMA backend reads GMAD/GMA format versions 1–3 in place and does not unpack the full archive. Compressed Workshop `.bin` wrappers are not implemented. Visual import into Blender remains a separate user-side verification step.
