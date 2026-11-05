# Glass Material User Scan — Full-Game Results

Date: 2026-11-05

## Scope and integrity

Source report: `research/glass_material_users.json` (blob SHA `57baa509b5aa03ff36dcbf86dd5279d650f04074`).

The scan covered all 3,522 scannable packages in the 9,725-package index. It encountered 48,020 Unit resource entries across package TOCs and parsed all 48,020 successfully. There were no package scan errors and no Unit parse errors.

The report has `scan_complete=true` and `status=warning`. The warning comes from 137 CompositeUnit references across the scanned game data that could not be resolved inside the same package. This is a global scanner warning count, not 137 failed Glass matches.

For the 13 queried Glass Material FileIDs specifically:
- 11 Material IDs were found in Unit Materials tables.
- 2 Material IDs were not found: `68a3fcd7d278cafb` and `932838e35b6ca353`.
- All 10 target references that used CompositeUnit-backed geometry were resolved successfully; none of the target Glass references had an unresolved CompositeUnit mapping.
- 942 is the number of target Unit material-table rows across package occurrences, not the number of distinct Units.
- 6,349 is the number of matched Mesh Section records across those package occurrences, not a count of unique physical meshes.

## Per-material results

Counts below distinguish raw references in package occurrences from Unit IDs and Package IDs. The Unit-ID count is an identity-level de-duplication; it does not prove that every occurrence with the same Unit FileID has byte-identical TOC/GPU/Stream payloads.

| Material FileID | Raw Unit refs | Distinct Unit IDs | Distinct Packages | Mesh Section matches |
|---|---:|---:|---:|---:|
| `0f3201437c248dfc` | 13 | 3 | 13 | 47 |
| `2f80391371cce33e` | 645 | 11 | 121 | 5,925 |
| `5942631a102e7a6f` | 1 | 1 | 1 | 2 |
| `62650e2896e2b3e3` | 2 | 1 | 2 | 4 |
| `68a3fcd7d278cafb` | 0 | 0 | 0 | 0 |
| `6caeffbc028219fb` | 2 | 1 | 2 | 8 |
| `83f08d5592664073` | 2 | 1 | 2 | 2 |
| `8b3ff5502ac52c25` | 13 | 8 | 9 | 45 |
| `932838e35b6ca353` | 0 | 0 | 0 | 0 |
| `93d9723a39dab3d1` | 3 | 1 | 3 | 18 |
| `9423bb5f9a715c2e` | 6 | 3 | 4 | 36 |
| `b5c17c3e6bd81b1f` | 3 | 3 | 1 | 10 |
| `e2b99ac44dc829f2` | 252 | 11 | 195 | 252 |
| **Total** | **942** | **44** | **353 material/package pairs** | **6,349** |

The 44 distinct Unit IDs do not overlap between the 11 referenced Material FileIDs in this report. The 353 package figure is the sum of per-material distinct package counts; a package may contain more than one queried Material.

### Notable patterns

- `2f80391371cce33e` is the broadest Unit-table hit: 645 raw references across 121 packages collapse to 11 Unit IDs. Those 11 Unit IDs expose 91 distinct Mesh IDs in the report. This is a strong candidate for inspecting package duplication and Unit payload identity before interpreting the raw reference volume as widespread usage.
- `e2b99ac44dc829f2` has 252 raw references across 195 packages, but only 11 Unit IDs and five distinct Mesh IDs. Its mapped sections are small (the largest recorded section has 842 vertices). This pattern looks unlike a large character-clothing surface, but the Unit geometry still needs inspection before assigning an asset category.
- `9423bb5f9a715c2e`, the Glass instance previously matched against the AQ `glass.material` texture `4406087459ab9513`, appears in three Unit IDs. Two are CompositeUnit-backed and resolved; one is a direct Unit with multiple MeshInfo LOD entries.
- `8b3ff5502ac52c25` appears in eight Unit IDs. Five references use a resolved CompositeUnit layout.
- `b5c17c3e6bd81b1f` appears in three Unit IDs in one package, all mapped through the same resolved CompositeUnit `ddf83b892394e370`.

No Unit or mesh names are available in this scan. Geometry counts and LOD indices alone are not enough to classify an asset as a character, armor piece, glass panel, or prop.

## Candidate Unit inspections

The next step is to use the existing `hd2-unit` command, which decodes the selected Unit's TransformInfo, BoneInfo, StreamInfo, MeshInfo, and GPU geometry. The chosen examples cover the Glass instance matched to the AQ template, a high-frequency Material FileID, a second Glass instance, a direct LOD case, and a successfully resolved CompositeUnit case.

| Purpose | Package | Unit FileID | Source Material |
|---|---|---|---|
| AQ Glass-template instance, direct Unit | `09985dc611a3a8b6` | `12738f0c0c22b686` | `9423bb5f9a715c2e` |
| High-frequency Glass material representative | `029a193f2a08167c` | `246144231754fff4` | `2f80391371cce33e` |
| Second Glass payload, direct Unit | `01edaa2a7e84b840` | `2d3bc1683a54298d` | `8b3ff5502ac52c25` |
| Direct Unit with four LOD entries | `1e33cc1600ff38f3` | `4e401cc877093220` | `0f3201437c248dfc` |
| CompositeUnit-backed Glass instance | `fc5b6bff0db90aab` | `5367951cd4da72d9` | `9423bb5f9a715c2e` |

Run from `tools/SourceModelReader`:

```powershell
$gameData = "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data"

$jobs = @(
    @{ Name="glass_9423_direct"; Package="09985dc611a3a8b6"; Unit="12738f0c0c22b686" },
    @{ Name="glass_2f803_common"; Package="029a193f2a08167c"; Unit="246144231754fff4" },
    @{ Name="glass_8b3ff_direct"; Package="01edaa2a7e84b840"; Unit="2d3bc1683a54298d" },
    @{ Name="glass_0f320_lod"; Package="1e33cc1600ff38f3"; Unit="4e401cc877093220" },
    @{ Name="glass_9423_composite"; Package="fc5b6bff0db90aab"; Unit="5367951cd4da72d9" }
)

New-Item -ItemType Directory -Force ".\\research\\unit_inspections" | Out-Null

foreach ($job in $jobs) {
    Write-Host "`nInspecting $($job.Name)..."

    py -3 .\\source_model_reader.py hd2-unit $gameData `
        --package $job.Package `
        --unit $job.Unit `
        --json ".\\research\\unit_inspections\\$($job.Name).json"

    $code = $LASTEXITCODE
    if ($code -ge 2) {
        throw "Unit inspection failed: $($job.Name), exit code $code"
    }
    elseif ($code -eq 1) {
        Write-Warning "Unit report generated with warnings: $($job.Name)"
    }
}
```

The output must be inspected for Transform count, BoneInfo count, Stream/mesh counts, geometry vertex/index totals, skinning diagnostics, and CompositeUnit status. Do not classify any sample as a character or cloth until its decoded structure supports that conclusion.

## Current limitations and next research questions

1. Same Unit FileID in different packages is de-duplicated for the counts above, but payload hashes have not yet been compared across those package copies. A follow-up should compare Unit TOC payload hashes and, where useful, referenced GPU/Stream resource IDs.
2. The scan does not decode geometry or infer resource names. `hd2-unit` is the next validation stage.
3. The 137 global CompositeUnit warnings arise when the referenced CompositeUnit is not in the Unit's own package. The scanner currently does not perform cross-package CompositeUnit resolution.
4. A Material FileID not found in a Unit Materials table is not proof that it is unused by every resource type or runtime path.
5. No blend mode, depth-write state, culling mode, or final shader behavior is inferred by this report.

The immediate goal is to determine whether any of the mapped Units are skinned character/armor geometry, and whether the Glass sections correspond to actual clothing surfaces or only environmental/prop geometry.


## Accelerated next step — one-command Unit batch

To avoid five separate inspection commands and manual JSON triage, a batch runner was added:

`research/run_glass_unit_batch.py`

From `tools/SourceModelReader`, run:

```powershell
py -3 .\\research\\run_glass_unit_batch.py
```

Or supply a different game data path:

```powershell
py -3 .\\research\\run_glass_unit_batch.py --game-data "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data"
```

The runner:
1. Executes the focused Texture and Material User regression-test modules and saves their logs.
2. Inspects all five candidate Units in sequence.
3. Continues to the next candidate if an individual Unit fails.
4. Writes compact per-Unit JSON files containing geometry layouts, material/section mappings, stream component descriptors, skeleton names, BoneInfo palettes/remaps, active skinning counts, matrix-validation summaries, and warnings.
5. Deletes each temporary full per-vertex JSON report after producing its compact result.
6. Produces `unit_inspections/glass_unit_batch_summary.json` and `glass_unit_batch_summary.md`.

A non-zero final exit code means at least one focused test or Unit inspection failed. Successful candidate reports are retained. The script reads game data only; it does not modify packages or game files.

This is a research batch helper, not a new parser or a claim that the five candidates are character assets. Their actual roles must be classified from the resulting geometry, skeleton, and material-section evidence.
