# Source-to-HD2 Skeleton Mapping Baseline

Date: 2026-11-05

Status: diagnostic implementation added; local regression and real-sample comparison are pending.

## Why this step exists

The Source reader already reconstructs the Source 1 model skeleton and can compare SMD slices to their compiled MDL bone definitions. The HD2 Unit reader resolves target TransformInfo names and parents through Bones hashes, and identifies which target transforms are reached by positive-weight vertex lanes.

The next missing link is a conservative comparison between these two independently parsed skeletons.

## Implemented files

- `hd2_skeleton_compare.py`: pure comparison logic.
- `tests/test_hd2_skeleton_compare.py`: synthetic tests for exact name matching, parent mismatch, duplicate/ambiguous target names, unresolved target hashes, and the rule that rest-pose numeric comparison is not performed.
- `research/compare_source_hd2_skeleton.py`: one-command bridge that reads a Source SMD from a GMA/ZIP/directory/file, inspects a selected HD2 Unit from local game data, and writes JSON plus Markdown.

## Matching policy

1. Compare names after trimming edge whitespace and applying Unicode case-folding.
2. Do not remove punctuation, prefixes, suffixes, or abbreviations.
3. Do not infer aliases from similar-looking names.
4. Keep duplicate/ambiguous target names out of automatic matches.
5. Compare parent names only when both parent references resolve.
6. Report whether the matched HD2 TransformInfo node is actively reached by positive-weight target vertex lanes.
7. Never apply mapping or reparenting as a side effect.

The Source list currently consists of every node declared by the selected SMD. It includes helper or currently unweighted nodes; the SMD parser does not yet filter this list using the Source VVD vertex-weight data. Therefore, `source_unmapped_count` is not yet equivalent to “number of source deformation bones missing from HD2”.

## Coordinate and pose limitation

The tool deliberately does not compare numeric rest-pose transforms.

Source SMD stores local position and Euler rotation values. The HD2 reader currently reports TransformInfo hierarchy entries and raw matrices, with a separately validated HD2 matrix reconstruction path. Before comparing pose numbers across engines, a dedicated adapter must establish:
- axis orientation and handedness;
- unit scale;
- local versus model/world matrix space;
- Euler/quaternion convention and rotation order;
- parent hierarchy and bind/inverse-bind interpretation.

Until those are validated, exact names and resolved parent relationships are the defensible comparison surface.

## Default target sample

The default target is the previously inspected Avatar Unit:

- Package: `18235e0c9ec0e636`
- Unit: `4d1c334d294dfa97`

This is a known HD2 Avatar skeleton reference sample. It is not claimed to be one of the five Glass Unit candidates or a model that uses the investigated Glass material. Other Unit targets can be selected with `--target-package` and `--target-unit`.

## Run

From `tools/SourceModelReader`:

```powershell
py -3 -m unittest discover -s tests -p "test_hd2_skeleton_compare.py" -v
```

Then compare the sample Source GMA against the Avatar target:

```powershell
py -3 .\\research\\compare_source_hd2_skeleton.py `
    --source-container "D:\\Models\\gmpublisher.gma" `
    --source-entry "AES_T.smd" `
    --game-data "G:\\AppData\\SteamLibrary\\steamapps\\common\\Helldivers 2\\data"
```

The SMD entry can be a full member path or a basename when unique. If the GMA does not contain raw SMD files, supply the extracted SMD directory or ZIP instead.

## Outputs

- `research/skeleton_comparison/source_hd2_skeleton.json`: ignored local machine-readable report.
- `research/skeleton_comparison/source_hd2_skeleton.md`: shareable human-readable report.

## Acceptance still pending

The code is committed, but no local Windows test result is claimed yet. The next review should check:
- synthetic test results;
- exact-name match coverage for the sample;
- parent mismatch and unresolved-name counts;
- whether active HD2 deformation transforms are represented among matched names;
- whether the Source-side unmatched list shrinks after a reviewed explicit alias table is introduced.

An alias table must be explicit, reviewable, and backed by structural/semantic evidence. Fuzzy matching may be used to suggest candidates in a separate report, but must not silently create retarget mappings.
