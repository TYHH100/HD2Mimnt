# Full Material Census Results

Date: 2026-11-05
Original raw report: `hd2_material_census_full.json` (41.7 MB; generated, validated, then removed from the active branch during repository cleanup). The older duplicate `hd2_material_census.json` had the same SHA-256 and was also removed.
Compact target-family summary retained: `hd2_material_census_full_summary.json`.

## 1. Scan completeness

The full scan completed successfully.

| Metric | Result |
|---|---:|
| Packages in index | 9,725 |
| Scannable packages | 3,522 |
| Packages scanned | 3,522 |
| Packages containing Materials | 2,773 |
| Package errors / warnings | 0 / 0 |
| Resource entry rows scanned | 322,790 |
| Material entries | 50,580 |
| Material parse successes / errors | 50,580 / 0 |
| Unique Material FileIDs | 10,121 |
| Duplicate Material FileIDs | 3,691 |
| Duplicate Material occurrences | 40,459 |
| Unique Material payload hashes | 8,112 |
| ParentMaterialIDs | 381 |
| Texture-node entries | 202,236 |
| Zero Texture-ID references | 309 |
| Unique texture-node type IDs | 394 |
| Shader-variable descriptors | 1,329,049 |
| Unique shader-variable IDs | 2,651 |
| Material parse warnings | 0 |

The census satisfies all completion gates: `scan_complete=true`, `package_scan_complete=true`, `material_parse_complete=true`, and `material_parse_error_count=0`. These counts are complete for the indexed/scannable package set represented by this local game-data installation.

Important counting distinction: the 50,580 entries are package occurrences. They are not 50,580 independent material definitions. The scan reports 10,121 unique Material FileIDs and 8,112 unique payload hashes.

## 2. Target-family results

| ParentMaterialID | Occurrences | Unique FileIDs | Unique payloads | Packages | Texture slots | Shader variables |
|---|---:|---:|---:|---:|---:|---:|
| `0x84e5de1cc6b74ada` (AQ glass / Community translucent) | 188 | 13 | 13 | 167 | 1 | 8 or 9 |
| `0x110199a9c3401242` (AQ translucent_flowing) | 3 | 2 | 2 | 3 | 5 | 87 |
| `0xd84d04634a1e2f60` (Alpha Clip) | 129 | 6 | 5 | 123 | 3 | 12 |
| `0xc6042e3403385d40` (Alpha Clip+) | 194 | 20 | 20 | 143 | 4 | 13 or 14 |

### 2.1 Glass / translucent: 13 distinct material payloads

The family is much more widely distributed than the 20-package smoke test suggested: 188 package occurrences across 167 packages, but only 13 distinct payloads.

All 13 distinct payloads have exactly one texture slot. Most use eight shader variables; one layout has nine. The 13 payloads split across eight shader-layout signatures, so this is not one single invariant parameter layout.

Selected distinct material samples for follow-up extraction:

| Material FileID | Example package | Shader variables | Payload SHA-256 |
|---|---|---:|---|
| `e2b99ac44dc829f2` | `0118c2cfda4f68a5` | 8 | `69ab731e8020ea8a132c1f6a3681818ea796fd7728bcf85d8c193b52400414e5` |
| `8b3ff5502ac52c25` | `01edaa2a7e84b840` | 8 | `4a2ef268ea448a5ebaee2b690f370ee80460513427a9a8fb610b1f8ce9ca52e9` |
| `2f80391371cce33e` | `029a193f2a08167c` | 8 | `4cefff5f857df0f988bc439b50411e4d4cbf42ba5a59f5ad6a75222cd2775f10` |
| `9423bb5f9a715c2e` | `09985dc611a3a8b6` | 9 | `9d130079e2e3d516e38c45d192f6bb30bb4231961f809f00d66757c1e5e378f0` |

The previous 1×1 texture finding applies only to the already inspected sample/reference. The full census confirms there are 13 distinct material payloads, but the compact summary does not contain their Texture FileIDs. Do not generalize the 1×1 result to all 13 until the distinct materials' texture references and payloads are inspected.

### 2.2 Translucent flowing: rare but structurally substantial

This family occurs only three times, across three packages, but represents two distinct Material FileIDs and two distinct payload hashes. Both payloads have five texture slots and 87 shader variables.

| Material FileID | Example package | Payload SHA-256 |
|---|---|---|
| `7b25f93956fd9aff` | `046d4441a6dae0a9` | `db31992e14521ff34ced276f9c87a619f62188eac05dff6c0d092f89d08ca284` |
| `344020be12d962b5` | `687d548654b91be6` | `ce6cf1b8cea0298f1ca05d2100c16fe216e3ebd1f581fbca1b10b2f245371d6d` |

This confirms the SDK template corresponds to real in-game Material resources. The 87-variable layout and five texture slots make this a specialized shader family, not a simple one-texture opacity material. The SDK documentation still identifies it as a semi-transparent flowing shield effect; the census does not establish cloth suitability.

### 2.3 Alpha Clip: six FileIDs, five payloads

The family has 129 occurrences across 123 packages, with six distinct Material FileIDs but only five distinct payload hashes. Every payload has three texture slots and 12 shader variables.

This family is common in the indexed resources, but remains a cutout/clip candidate rather than proof of continuous fractional transparency.

### 2.4 Alpha Clip+: 20 distinct payloads, one 14-variable outlier

The family has 194 occurrences across 143 packages and 20 unique Material FileIDs / payloads. All use four texture slots. The summary groups show 19 payloads with 13 variables and one payload with 14 variables.

The 14-variable outlier is:

- Package: `0149ef7d2ee3efca`
- Material FileID: `55a794f03a405c4c`
- Payload SHA-256: `eecf8cff8935d3e1682bbb0166f541420d11f7165700dfa350635fba0e3b7b18`

Its distinct shader-layout signature is `a9f52caf31b650fe`. It deserves targeted inspection to identify the extra descriptor and determine whether it is an additional render parameter, a variant-specific control, or something else. Do not infer its meaning from the descriptor count alone.

## 3. Research conclusions

1. The previous smoke sample was useful for discovering the families but substantially understated the full-game occurrence counts.
2. The general glass/translucent family has 13 distinct payloads, not just the handful seen in the smoke sample. Its texture references must now be inspected across those distinct payloads.
3. `translucent_flowing` is confirmed present in the game, but rare (two unique payloads) and complex (five textures / 87 variables). It is a separate research branch, likely effect-oriented.
4. Alpha Clip and Alpha Clip+ are widely used, but should remain classified as cutout families unless shader/render-state evidence demonstrates otherwise.
5. Package occurrence counts, Material FileID counts, and payload-hash counts must remain separate in all future reports.

## 4. Next extraction batch

Use `hd2-material` on representative distinct payloads, then resolve the returned texture FileIDs with `hd2-find --type Texture`. First batch:

- Glass/translucent: `e2b99ac44dc829f2`, `8b3ff5502ac52c25`, `2f80391371cce33e`, `9423bb5f9a715c2e`.
- Translucent flowing: `7b25f93956fd9aff`, `344020be12d962b5`.
- Alpha Clip+: `55a794f03a405c4c` (14-variable outlier).

Use the package IDs listed in the tables above. Extracting these seven materials is a bounded first pass that covers the main translucent families and the Alpha Clip+ outlier without expanding every material descriptor in the game.

## 5. Source artifacts

- `hd2_material_census_full.json`: 41,736,058-byte complete census output.
- `hd2_material_census_full_summary.json`: compact target-family summary derived from that exact full report; records the source blob SHA.


## Repository cleanup note

The large raw census JSON is intentionally not tracked in the current branch. The validated totals and the four target-family summaries remain in this document and in `hd2_material_census_full_summary.json`. The raw scan can be regenerated from the installed HD2 data directory with:

```powershell
py -3 .\\source_model_reader.py hd2-material-census "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data" --json ".\\research\\hd2_material_census_full.json"
```
