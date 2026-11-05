# Repository Research-Artifact Cleanup

Date: 2026-11-05  
Branch: `feature/source-model-reader-0.8.2`

## Result

Removed **54 generated-output paths**, representing **156,021,286 bytes (148.8 MiB)** of current-branch file payloads. This is a working-tree/branch cleanup, not a Git-history rewrite.

## What was removed

### Full-game Material census and lookup dumps

- `tools/SourceModelReader/research/glass_material_users.json` — 2,891,893 bytes
- `tools/SourceModelReader/research/hd2_material_census.json` — 41,736,058 bytes
- `tools/SourceModelReader/research/hd2_material_census_full.json` — 41,736,058 bytes
- `tools/SourceModelReader/research/hd2_material_census_smoke.json` — 798,498 bytes
- `tools/SourceModelReader/research/hd2_material_census_summary.json` — 12,014,497 bytes
- `tools/SourceModelReader/research/hd2_package_catalog.json` — 627,852 bytes
- `tools/SourceModelReader/research/translucent_normal_roughness_opacity.dds` — 152 bytes
- `tools/SourceModelReader/research/translucent_normal_roughness_opacity.gpu.bin` — 4 bytes

The two 41.7 MB census paths were byte-identical (same Git blob SHA). The 12 MB older summary and 798 KB Smoke Test output were superseded by the compact, validated target-family summary and Markdown analysis. The raw Glass user scan was replaced by the aggregate report and the 44-Unit appendix in `MATERIAL_USER_SCAN_RESULTS.md`.

### Old root-level generated inventories and diagnostic dumps

- `audit.json`
- `avatar_transform_check.json`
- `avatar_transform_check_082.json`
- `fs05_player_inventory.json`
- `fs05_transform_check.json`
- `fs05_transform_check_082.json`
- `fs05_unit_210fb2f60f8308ea.json`
- `fs05_unit_324fb206612ce4f7.json`
- `hd2_avatar_inventory.json`
- `hd2_avatar_unit.json`
- `hd2_base_inventory.json`
- `hd2_package_catalog.json`
- `smd_audit_061.json`

The tested results remain summarized in `README.md`, `ROADMAP.md`, `BONE_MATRIX_TEST_REPORT.md`, `GLB_TEST_REPORT.md`, and the other milestone reports. The CLI commands in README can recreate local JSON outputs from the installed game or Source sample.

### Extracted Texture binaries and previews

Removed 32 generated `.dds`, `.bin`, and `.png` files under `research/textures` and `research`. Their JSON inspection reports remain, including dimensions, DXGI formats, mip-chain metadata, payload hashes, and channel/alpha statistics. DDS, raw GPU/Stream copies, and PNG previews can be regenerated with `hd2-texture` and `--preview-png`.

### Duplicate Unit batch summary

- Removed `research/unit_inspections/glass_unit_batch_summary.json`; the human-readable `glass_unit_batch_summary.md` remains.
- Retained the five compact Unit JSON reports because they are the current inputs to `export_glass_unit_details.py` and are needed for the next geometry/skeleton analysis. These remain covered by the existing Git LFS rule.

## What was retained

- All parser and exporter source code, tests, README/ROADMAP/CHANGELOG, and milestone reports.
- `MATERIAL_FAMILY_AUDIT.md`, `FULL_MATERIAL_CENSUS_RESULTS.md`, `MATERIAL_INSTANCE_COMPARISON.md`, `MATERIAL_USER_SCAN_RESULTS.md`, and `TRANSLUCENT_TEXTURE_TRIAGE.md`.
- `hd2_material_census_full_summary.json`, which contains the compact full-scan totals and target-family data.
- The targeted Material JSON reports, Texture analysis JSON reports, and cross-package lookup summaries.
- The five active compact Unit reports and the two batch/export helper scripts.

## Regeneration

Run only when the corresponding data is needed:

- Material census: see the command in `FULL_MATERIAL_CENSUS_RESULTS.md`.
- Glass material users: rerun `hd2-material-users` with the 13 Material IDs listed in `MATERIAL_USER_SCAN_RESULTS.md`.
- Five candidate Units: `py -3 .\\research\\run_glass_unit_batch.py`.
- Detailed Unit Markdown: `py -3 .\\research\\export_glass_unit_details.py`.
- Texture payloads/previews: rerun `hd2-texture` for the corresponding FileIDs; the JSON inspection reports were retained.

## Important storage note

This commit removes the files from the **current branch tree** and adds ignore rules to prevent accidental re-commits. It does not rewrite earlier commits, so the large Git blobs remain reachable in repository history. Git LFS objects likewise are not explicitly purged from the server by deleting a pointer. A permanent historical-size reduction would require a separate coordinated history rewrite and LFS cleanup; that is deliberately not attempted as part of this safe working-branch cleanup.
