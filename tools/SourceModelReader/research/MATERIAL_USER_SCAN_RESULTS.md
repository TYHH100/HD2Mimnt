# Glass Material User Scan — Full-Game Results

Date: 2026-11-05

## Scope and integrity

Original scan JSON (`research/glass_material_users.json`, blob SHA `57baa509b5aa03ff36dcbf86dd5279d650f04074`) was a generated 2.9 MB dump and has been removed from the active branch after extracting the full aggregate results and the 44 Unit identities listed in the appendix below.

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


### Detailed report export (after the batch)

The five compact JSON reports are tracked with Git LFS to keep the repository history manageable. The ordinary GitHub contents API returns only LFS pointer files for those JSON assets. To produce an upload-friendly text report locally, run:

```powershell
py -3 .\\research\\export_glass_unit_details.py
```

This reads the five compact JSON files from `research/unit_inspections` and writes:

`research/unit_inspections/glass_unit_detailed_inspection.md`

The Markdown report includes per-candidate material tables, active skeleton transforms, mesh/LOD/Section layouts, vertex component formats, skinning status per section, BoneInfo remap sizes, and parser warnings. Share this Markdown file for the next analysis pass; the LFS JSON files do not need to be uploaded again.

This exporter is a reporting step only. It does not reread game packages, change parser behavior, or claim asset classification from geometry counts alone.


## Appendix — all 44 distinct Unit IDs discovered

The original 2.9 MB scan JSON was removed from the active branch after its aggregate results and this de-duplicated candidate index were extracted. The table below retains all 44 Unit identities, their geometry source, any CompositeUnit ID, the number of distinct packages where that Unit ID was observed, and up to three example packages. Package samples are examples, not a complete occurrence listing.

| Material FileID | Unit FileID | Geometry source | CompositeUnit | Package copies | Example packages |
|---|---|---|---|---:|---|
| `0f3201437c248dfc` | `4e401cc877093220` | Unit | — | 11 | `1e33cc1600ff38f3`, `23bb68bd2e366fdf`, `2c26bc4c6592fa14` |
| `0f3201437c248dfc` | `9bb22e306a372df6` | Unit | — | 1 | `b18d1859b2cc7be1` |
| `0f3201437c248dfc` | `dec89a1588c9d726` | Unit | — | 1 | `fc5b6bff0db90aab` |
| `2f80391371cce33e` | `0e8c6a2fe8d85cd4` | Unit | — | 30 | `08e7b8b93e0dc696`, `09d2644a0c95e209`, `1230a746045f365f` |
| `2f80391371cce33e` | `246144231754fff4` | Unit | — | 70 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `36cb1938d1756a85` | Unit | — | 34 | `0692f83587540ae5`, `08e7b8b93e0dc696`, `09d2644a0c95e209` |
| `2f80391371cce33e` | `4b7cd895ff08ec3f` | Unit | — | 62 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `9cc3230408df50f5` | Unit | — | 61 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `af02e6fdc559e24b` | Unit | — | 69 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `af5f4fa5c016cb57` | Unit | — | 72 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `b75bfa3a59b88a66` | Unit | — | 80 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `d4a33051e7e84676` | Unit | — | 64 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `2f80391371cce33e` | `da6fa6152cbadcef` | Unit | — | 34 | `08e7b8b93e0dc696`, `09d2644a0c95e209`, `1230a746045f365f` |
| `2f80391371cce33e` | `fbf9b84a311e56d8` | Unit | — | 69 | `029a193f2a08167c`, `031216af8ef63607`, `064e2fb84ddc7ab7` |
| `5942631a102e7a6f` | `aecb777074d05515` | Unit | — | 1 | `7ed1f941859987b4` |
| `62650e2896e2b3e3` | `48d71ca5d261c852` | Unit | — | 2 | `9bfa7eb1324c29a5`, `9c3d59d7095b4c6a` |
| `6caeffbc028219fb` | `16bc189b4d7cb3fe` | Unit | — | 2 | `5dcc1c4250a662aa`, `dc340e17314bc86a` |
| `83f08d5592664073` | `ca4a3eeb62f8b328` | Unit | — | 2 | `09985dc611a3a8b6`, `fc5b6bff0db90aab` |
| `8b3ff5502ac52c25` | `1fb1f1a548b29abf` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `8b3ff5502ac52c25` | `2d3bc1683a54298d` | Unit | — | 4 | `01edaa2a7e84b840`, `4b34b7e4d302fb84`, `ab58b061f0aec8e7` |
| `8b3ff5502ac52c25` | `4aff102712823c1e` | Unit | — | 3 | `161694e194f95f99`, `7f76463558ffe625`, `f2b6fce59b619737` |
| `8b3ff5502ac52c25` | `551e7b935eff221a` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `8b3ff5502ac52c25` | `560ebb3b22a00adf` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `8b3ff5502ac52c25` | `7643744b05735c7e` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `8b3ff5502ac52c25` | `773c4184e4bad0df` | Unit | — | 1 | `6286516637d5c967` |
| `8b3ff5502ac52c25` | `d7ea827cb1503bc4` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `93d9723a39dab3d1` | `9ea89ceea6f8e766` | Unit | — | 3 | `0a9651116d7d42b5`, `7c5edf52fd8827de`, `9514b4daa7f4c881` |
| `9423bb5f9a715c2e` | `12738f0c0c22b686` | Unit | — | 2 | `09985dc611a3a8b6`, `3b2c86dd5b316eb2` |
| `9423bb5f9a715c2e` | `33cb06b89be208da` | Unit | — | 2 | `79219a4244b09ed8`, `fc5b6bff0db90aab` |
| `9423bb5f9a715c2e` | `5367951cd4da72d9` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `b5c17c3e6bd81b1f` | `3b9bcf29e38da0a6` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `b5c17c3e6bd81b1f` | `4c1cb88bb68e1731` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `b5c17c3e6bd81b1f` | `8df85c4ad06c8d72` | CompositeUnit | `ddf83b892394e370` | 1 | `fc5b6bff0db90aab` |
| `e2b99ac44dc829f2` | `15e08c8f0cdd1e08` | Unit | — | 51 | `0118c2cfda4f68a5`, `0149ef7d2ee3efca`, `03fbef05fbf99b12` |
| `e2b99ac44dc829f2` | `1664e06696cb08e3` | Unit | — | 60 | `009da023c64d178d`, `03fbef05fbf99b12`, `08e7b8b93e0dc696` |
| `e2b99ac44dc829f2` | `1a226f730402d1f7` | Unit | — | 1 | `09985dc611a3a8b6` |
| `e2b99ac44dc829f2` | `41959e4ac040df31` | Unit | — | 10 | `1f881b1c0b28d702`, `36f864f8c720ddb2`, `4a7ea58e8d4be432` |
| `e2b99ac44dc829f2` | `566076565486f3b8` | Unit | — | 4 | `6530ed7ec71647d1`, `9bfa7eb1324c29a5`, `b3694945bb1617ed` |
| `e2b99ac44dc829f2` | `5ae9a9da3521e252` | Unit | — | 84 | `039fb224d76ccab4`, `04e78679f001a447`, `078d187f158c0d8f` |
| `e2b99ac44dc829f2` | `a4958229b27acad1` | Unit | — | 3 | `6530ed7ec71647d1`, `b3694945bb1617ed`, `c38aeb09bc59b2c4` |
| `e2b99ac44dc829f2` | `b6e76076e5c6fe44` | Unit | — | 4 | `0b930572f0d4d0ab`, `50ec5ffdc83537a5`, `947df36a9103d1c8` |
| `e2b99ac44dc829f2` | `ddce99c2b35bd47a` | Unit | — | 22 | `09985dc611a3a8b6`, `149952f0a7588552`, `2a6f3b4aa3866047` |
| `e2b99ac44dc829f2` | `df685b4dc75bc874` | Unit | — | 9 | `0149ef7d2ee3efca`, `0dc8fcabdb02111d`, `1230a746045f365f` |
| `e2b99ac44dc829f2` | `fce2415f35ce21fb` | Unit | — | 4 | `6530ed7ec71647d1`, `b3694945bb1617ed`, `c38aeb09bc59b2c4` |
