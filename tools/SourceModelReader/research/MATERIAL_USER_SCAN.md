# Material User / Unit / Mesh Section Scan

Date: 2026-11-05

## Purpose

The Glass/Translucent material investigation has reached the point where texture evidence alone is no longer sufficient. The next question is whether any game Unit actually references the identified Glass Material FileIDs, and whether those references can be mapped down to real Mesh Sections.

The new `hd2-material-users` scanner traces:

`Unit Materials table (section_id -> Material FileID)`
→ `MeshInfo material slot`
→ `Mesh Section`

For CompositeUnit-backed geometry, it instead uses the referenced CompositeUnit's MeshInfo mapping, following the same data relationship used by the existing `hd2-unit` inspector.

## Scanner behavior

New module: `tools/SourceModelReader/hd2_material_users.py`

New CLI command: `hd2-material-users`

The scanner:
1. Reads each SLIM package's TOC header, type table and resource-entry rows.
2. Collects Unit and CompositeUnit entries, checking declared type counts against actual TOC rows.
3. Reads Unit TOC payloads only and parses the Unit header, Materials table and MeshInfo layout.
4. Resolves a Material FileID through Unit material `section_id` and Mesh Section `material_slot_id`.
5. For CompositeUnit-backed Units, resolves the CompositeUnit in the same package and uses its mesh-section layout. When it cannot be resolved, the Unit-level Material reference is still reported, but mesh-section matches are intentionally empty; the Unit-side placeholder MeshInfo is not treated as real geometry sections.
6. Records package, Unit ID, geometry source, LOD, mesh ID, section index, vertex/index ranges and CompositeUnit resolution state.

No GPU or Stream sidecar payloads are reconstructed. No vertex/index geometry is decoded. No render mode or final shader behavior is inferred.

## Report semantics

Important summary fields:
- `package_count_scanned` / `package_count_scannable`: package coverage.
- `unit_count_scanned`: Unit entries encountered in scanned package TOCs.
- `unit_parse_success_count` / `unit_parse_error_count`: Unit TOC parsing status.
- `material_unit_reference_count`: target rows in Unit Materials tables (one row per Unit material section).
- `mesh_section_match_count`: matching Mesh Sections mapped through those Unit material section IDs.
- `composite_warning_count`: CompositeUnit references that could not be mapped to section layouts.

`scan_complete=true` means all selected packages and their Unit TOC/material-table scans completed without package or Unit parsing failures. A complete scan can still have CompositeUnit warnings; in that case Unit-level Material references are complete, but Mesh Section mapping for those specific CompositeUnit-backed Units is incomplete. The report status is therefore `warning`.

A Material reported as not referenced means it was not found in the scanned Unit material tables. It does not prove absence from all non-Unit resource types or from any runtime-generated resource path.

## Target Glass Material FileIDs

The initial query should include all 13 distinct Material FileIDs from the completed full material census for ParentMaterialID `0x84e5de1cc6b74ada`:

- `e2b99ac44dc829f2`
- `8b3ff5502ac52c25`
- `2f80391371cce33e`
- `83f08d5592664073`
- `9423bb5f9a715c2e`
- `93d9723a39dab3d1`
- `0f3201437c248dfc`
- `6caeffbc028219fb`
- `68a3fcd7d278cafb`
- `5942631a102e7a6f`
- `932838e35b6ca353`
- `62650e2896e2b3e3`
- `b5c17c3e6bd81b1f`

These are Material resource FileIDs, not Texture FileIDs and not ParentMaterialIDs.

## Local validation and full-game query

From `tools/SourceModelReader`, first run the focused mapping tests:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_material_users.py" -v
```

Then run the full existing SourceModelReader test suite:

```powershell
py -3 -m unittest discover -s tests -v
```

After tests pass, run the full-game Glass reference scan:

```powershell
$gameData = "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data"
$materials = @(
    "e2b99ac44dc829f2",
    "8b3ff5502ac52c25",
    "2f80391371cce33e",
    "83f08d5592664073",
    "9423bb5f9a715c2e",
    "93d9723a39dab3d1",
    "0f3201437c248dfc",
    "6caeffbc028219fb",
    "68a3fcd7d278cafb",
    "5942631a102e7a6f",
    "932838e35b6ca353",
    "62650e2896e2b3e3",
    "b5c17c3e6bd81b1f"
)

$argsList = @("hd2-material-users", $gameData)
foreach ($id in $materials) {
    $argsList += @("--material", $id)
}
$argsList += @("--json", ".\research\glass_material_users.json")

py -3 .\source_model_reader.py @argsList
if ($LASTEXITCODE -ge 2) {
    throw "Glass material user scan failed."
}
```

The output report `research/glass_material_users.json` should be checked for:
- `summary.scan_complete`
- `summary.unit_count_scanned`
- `summary.unit_parse_error_count`
- `summary.material_unit_reference_count`
- `summary.mesh_section_match_count`
- `summary.composite_warning_count`
- each target resource's `unit_reference_count` and `references`

## Next step after the scan

For each referenced Unit:
1. Inspect its package/Unit report with `hd2-unit` to validate the full geometry and skinning context where the CompositeUnit is locally resolvable.
2. Identify the actual mesh IDs, LODs and material-bearing Mesh Sections.
3. Compare the Glass instance values and Texture FileID against the mapped sections.
4. Only after finding a real model assignment, investigate render-state/shader behavior and controlled in-game tests.

Do not start writing a fabric conversion preset from a Glass Material hit alone. A real Unit/Mesh Section assignment and shader-use evidence are still required.


## Implementation snapshot

The first scanner implementation is committed on the working branch:
- `hd2_material_users.py`: global Unit/Material/Mesh Section scan.
- `source_model_reader.py`: `hd2-material-users` CLI command with repeatable `--material`.
- `tests/test_hd2_material_users.py`: direct Unit mapping, multiple material-section rows, CompositeUnit section mapping, unresolved CompositeUnit safety, a synthetic end-to-end SLIM Unit scan, and CLI help coverage.

The synthetic end-to-end fixture creates one package with one Unit entry and verifies that its Unit Materials table resolves to the expected Mesh Section without requiring GPU/Stream data. The scanner's CompositeUnit path is covered at the section-mapping level with an isolated fixture.

These new tests have **not yet been run in the user's Windows environment**. Pull the latest branch and run the focused test module before executing the full-game scan.


## Mapping correction — exact Unit material slot identity

The first Windows test run exposed a duplicate-match bug in `_material_mesh_matches` when one Material FileID appeared in more than one Unit Materials row.

The initial implementation resolved each Mesh Section's `material_slot_id` to a Material FileID, then matched only by Material FileID. When two Unit material sections referenced the same Material resource, each row therefore collected both sets of Mesh Sections. This duplicated the per-row section counts and could attribute sibling sections to the wrong Unit material slot.

The corrected rule is stricter and follows the existing `hd2-unit` mapping: a Mesh Section belongs to a particular Unit Materials row only when its `material_slot_id` exactly equals that row's `section_id`. The Material FileID is then the value attached to that exact row.

This correction addresses both observed failures:
- a single Material FileID repeated in two Unit material slots now yields one Mesh Section per slot, rather than both sections on each row;
- CompositeUnit-backed sections now preserve their individual index ranges under the correct slot, so the second slot reports its own 60 indices instead of the first slot's 120.

Fix commit: `dd23b79584d0e6c33d1846e3306c8c18b008aa63`.

The two failing Windows tests have not yet been rerun after this correction. Please sync the fix and rerun the focused six-test module. The expected result is 6/6, but runtime confirmation is still pending.
