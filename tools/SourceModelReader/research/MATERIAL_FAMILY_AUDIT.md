# Material Family Audit — 2026-11-05

Status: preliminary research note. This document records cross-checks between the current Source Model Reader reports, the 20-package census smoke test, and the version-pinned SDK/Wiki notes in [MATERIAL_FORMAT_NOTES.md](../MATERIAL_FORMAT_NOTES.md). It does not assign render modes beyond families corroborated by the existing SDK documentation.

## 1. Census smoke-test baseline

The committed `hd2_material_census_smoke.json` covers 20 of 3,522 scannable SLIM packages, so it is a smoke test, not a representative full-game census.

| Metric | Result |
|---|---:|
| Packages scanned / scannable | 20 / 3,522 |
| Packages containing Materials | 17 |
| Material entries | 949 |
| Material parse successes / errors | 949 / 0 |
| Unique Material FileIDs | 936 |
| Duplicate FileID groups / extra occurrences | 13 / 13 |
| Unique payload hashes | 926 |
| Duplicate payload-hash groups | 19 |
| Distinct ParentMaterialIDs | 86 |
| Texture nodes / distinct texture-node type IDs | 1,757 / 153 |
| Shader-variable descriptors / distinct IDs | 6,978 / 1,084 |

The report correctly marks the scan incomplete because only 20 packages were selected. Zero Material parse errors in this sample is useful parser evidence, not proof that all game Materials parse correctly.

The most common ParentMaterialID in this sample is zero (639 occurrences). Treat this as a distinct observed value; do not infer that these Materials are opaque, invalid, or all share one render behavior.

## 2. Verified sample-family cross-checks

The following raw 64-bit ParentMaterialIDs from the targeted sample reports match IDs listed in the pinned SDK alias notes:

| Sample | Raw ParentMaterialID | Decimal value | SDK alias evidence | Safe conclusion |
|---|---|---:|---|---|
| `material_alphaclip.json` | `d84d04634a1e2f60` | 15586118709890920288 | `alphaclip` in AQ and Community lookup notes | Alpha Clip family candidate, corroborated by SDK alias |
| `material_alphaclip_plus.json` | `c6042e3403385d40` | 14268580320343776576 | `alphaclip+` in AQ and Community lookup notes | Alpha Clip+ family candidate, corroborated by SDK alias |
| `material_translucent_a.json`, `material_translucent_b.json` | `84e5de1cc6b74ada` | 9576304397847579354 | AQ calls it `glass`; Community calls it `translucent` | Same raw parent ID; alias wording is fork-specific |

The raw ID is the stable key for this research. Friendly names must remain attached to the SDK repository and pinned revision that supplied them. In particular, do not silently normalize the `glass` / `translucent` alias disagreement into a universal engine name.

## 3. What the targeted translucent samples establish

Both translucent samples have:
- the same ParentMaterialID: `84e5de1cc6b74ada`;
- one texture node, with raw type ID `0xdab4d0ee`;
- the same referenced Texture FileID: `bf281073a422022c`;
- eight shader-variable descriptors, with the same ordered variable IDs and classes.

Their instance values differ. For example, the first scalar preview is approximately 0.02 in sample A and 0.4 in sample B; the third and fourth scalar previews also differ, and sample B has a non-zero Vector3 value of approximately (0.4511, 0.1576, 0.1576).

This is evidence that the two instances share a parent/template and texture-node layout while overriding instance parameters. It is not yet sufficient to name each raw variable ID or to assert exactly how the shader consumes the texture channels.

The referenced Texture FileID was not present in the selected package used by the Material inspector. The separate global lookup report found that FileID elsewhere in the scanned package set. Therefore, “missing from this package” must not be reported as “missing from the game.”

## 4. What the Alpha Clip samples establish

The Alpha Clip sample contains three texture nodes and 12 scalar descriptors. All three referenced Texture IDs resolve in the selected package.

The Alpha Clip+ sample contains four texture nodes and 13 scalar descriptors; three of its four texture dependencies resolve in the selected package. The remaining reference is unresolved in that package and requires global lookup before any missing-resource conclusion.

The Alpha Clip and Alpha Clip+ samples have different raw ParentMaterialIDs and different texture-node layouts. Do not treat Alpha Clip+ as merely an Alpha Clip instance with one extra texture unless the SDK template and in-game behavior support that interpretation.

## 5. Important interpretation boundaries

1. A ParentMaterialID alias is metadata, not a complete description of runtime blending.
2. A texture-node type ID is not a decoded texture channel or a semantic label until resolved through a pinned dictionary.
3. A shader-variable ID and its first scalar/vector preview do not establish its name or full array/stride semantics.
4. A texture dependency absent from one package may be shared from another package.
5. RGBA alpha data alone does not prove that a material uses alpha for transparency.
6. Alpha Clip's documented binary mask behavior is not a substitute for graded translucency needed by sheer fabric or stockings.

## 6. Next research steps

1. Build a revision-scoped dictionary audit for the raw texture-node type IDs and shader-variable IDs used by these samples. Keep source revision and unresolved IDs explicit.
2. Parse the globally located Texture resource `bf281073a422022c` and inspect its DDS format, dimensions, mip records, GPU/Stream payload selection, and decoded channel statistics. Preserve both raw payload paths.
3. Run the same global lookup for the unresolved Alpha Clip+ texture ID `12d4692531c1fd35`.
4. Compare the targeted sample descriptors against the matching SDK material templates, without assigning variable names based only on similar values or order.
5. After dictionary and texture evidence is recorded, select representative Unit/mesh references and validate the material-to-section association.
6. Keep fractional-opacity fabric support as an explicit in-game validation gate; do not automatically map it to Alpha Clip.

## Source reports

- `hd2_material_census_smoke.json`
- `material_alphaclip.json`
- `material_alphaclip_plus.json`
- `material_translucent_a.json`
- `material_translucent_b.json`
- `material_translucent_parent.json`
- `translucent_texture_lookup.json`
- `MATERIAL_FORMAT_NOTES.md`


## 7. Family-specific counts from the 20-package smoke census

A direct re-read of the committed `hd2_material_census_smoke.json` confirms these targeted family occurrences in its 949 parsed Material records:

| ParentMaterialID | Occurrences | Distinct packages | Texture count | Shader-variable count |
|---|---:|---:|---|---|
| `84e5de1cc6b74ada` | 3 | 3 | 1 each | 8 each |
| `d84d04634a1e2f60` | 1 | 1 | 3 | 12 |
| `c6042e3403385d40` | 5 | 3 | 4 each | 13 or 14 |

These are smoke-test observations only (20 packages scanned), not game-wide family totals.

### Translucent family observations

Two of the three `84e5de1cc6b74ada` occurrences share the same Material FileID `e2b99ac44dc829f2`, payload SHA-256, TOC size (384), texture-layout signature, and shader-layout signature across packages `0118c2cfda4f68a5` and `0149ef7d2ee3efca`. They are duplicate occurrences of the same payload, not independent material instances.

The third occurrence in package `01edaa2a7e84b840` is Material FileID `8b3ff5502ac52c25`, with the same 384-byte TOC and texture-layout signature but a different payload hash and shader-layout signature. This is the only distinct Translucent payload in this smoke sample. The sample set is therefore not enough to establish whether the family uses other texture IDs or non-placeholder maps; the full material-instance scan is still needed.

### Alpha Clip+ family observations

Five Alpha Clip+ occurrences appear in three packages:
- One occurrence in `00c928ec7e330f9e`.
- One occurrence in `0149ef7d2ee3efca`.
- Three occurrences in `01c1becaa12a1643`.

All five have four texture nodes and the same texture-layout signature `8c3faab1d66e0456`. Four have 13 shader-variable descriptors; one has 14. The latter is Material FileID `55a794f03a405c4c` in package `0149ef7d2ee3efca`, with a 528-byte TOC and a distinct shader-layout signature. This is a useful candidate for a targeted full parse: determine which additional variable descriptor is present and whether it changes the known Alpha Clip+ material schema.

### Alpha Clip family observations

Only one Alpha Clip occurrence appears in this 20-package sample. It should be treated as a parser/SDK control sample, not as evidence about the prevalence or variation of Alpha Clip materials.

## 8. Follow-up local extraction targets

Use these package/Material FileID pairs to retrieve the actual Material payloads from the local game data and compare their full texture IDs and variable descriptors:

| Family | Package | Material FileID | Reason |
|---|---|---|---|
| Translucent | `01edaa2a7e84b840` | `8b3ff5502ac52c25` | Distinct payload in smoke census |
| Alpha Clip+ | `0149ef7d2ee3efca` | `55a794f03a405c4c` | 14-variable schema variant |
| Alpha Clip+ | `01c1becaa12a1643` | `9436103fd778c403` | Compare with targeted 13-variable sample |
| Alpha Clip+ | `01c1becaa12a1643` | `d39f16ff0acb02b9` | Same package, distinct material instance |

Do not assume that the same texture-layout signature means identical texture FileIDs: the signature records the sequence of texture-node type IDs, not the referenced texture resource IDs.



## 9. Additional AQ-only translucent template discovered

A further inspection of AQ Edition's pinned `utils/constants.py` and `__init__.py` identified a second translucent-related family that must not be conflated with the `glass` parent `84e5de1cc6b74ada`.

AQ maps decimal ParentMaterialID `1225429528025764418` to raw ID `0x110199a9c3401242`, alias `translucent_flowing`. Its pinned `TextureTypeLookup` defines five ordered inputs:

1. `Noise02 Mask`
2. An unlabeled slot
3. `Noise01 Mask`
4. `blank Pos`
5. `blank Pos`

AQ's template description calls it a semi-transparent flowing material, supports two independently configurable flowing masks and flow direction, and says it was sourced from an Illuminate Overseer shield. This is direct SDK author documentation for a distinct translucent-capable template, although it describes a flowing shield effect rather than a fabric shader.

The 20-package Material census smoke sample contains zero occurrences of ParentMaterialID `0x110199a9c3401242`. That is not evidence that the material is absent from the game; the smoke scan covered only 20 of 3,522 scannable packages. It is a strong candidate for a targeted lookup in the full package inventory, especially because the SDK description explicitly identifies it as semi-transparent.

The AQ constants file also maps `0x84e5de1cc6b74ada` to `glass` and comments out an older `translucent` alias for the same ID. The AQ UI labels its single texture slot `Glass stain`. This is consistent with the separate Community Edition naming `translucent`, but it is not the same thing as AQ's distinct `translucent_flowing` family.

### Research priority update

For transparent/fabric-capable material research, keep three separate candidates:

| ParentMaterialID | AQ alias / Community alias | Evidence | Relevance |
|---|---|---|---|
| `0x84e5de1cc6b74ada` | AQ `glass`; Community `translucent` | One-node sample; `OpacityBase` variable; raw texture-node type dictionary label includes `Opacity` | Strongest candidate for a general opacity-controlled surface, but sample points to a 1×1 resource |
| `0x110199a9c3401242` | AQ `translucent_flowing` | Five slots, two noise masks, SDK explicitly describes semi-transparent flowing effect | Valuable alternate family; may be specialized for animated effects, not yet shown suitable for cloth |
| `0xd84d04634a1e2f60` / `0xc6042e3403385d40` | AQ `alphaclip` / `alphaclip+` | Explicit Alpha Mask / OpacityClipMap slots | Cutout family; not a substitute for graded translucency |

The next full-game search should include `0x110199a9c3401242`, alongside the two existing families, and should collect actual material instances and their texture FileIDs before prioritizing any one template for conversion.


## 10. Full-census execution plan and interpretation guardrails

The committed smoke census scans the first 20 package names from a lexicographically sorted index (`all_names[:max_packages]`). It is a deterministic parser smoke test, not a representative sample. The current counts in this document must not be extrapolated to the full game.

The SourceModelReader CLI already supports the next step:
- `hd2-material-census` without `--max-packages` scans all indexed non-sidecar packages.
- Omit `--include-descriptors` for the full pass. This keeps the report focused on per-material structure, ParentMaterialID, texture-node IDs and shader-variable summary statistics without embedding every descriptor for every material.
- Accept the family totals as complete only when `summary.scan_complete=true`, `package_scan_complete=true`, and `material_parse_error_count=0`. If not, report exact scanned/scannable counts and errors; do not silently treat partial results as full-game totals.

After the full census:
1. Compare all occurrences of `0x84e5de1cc6b74ada`, `0x110199a9c3401242`, `0xd84d04634a1e2f60`, and `0xc6042e3403385d40`.
2. Group by Material FileID and payload hash so duplicate package occurrences are not counted as independent instances.
3. Extract full descriptor/texture-node details only for distinct payloads in the families of interest.
4. Resolve every referenced Texture FileID independently; a missing Texture in the same package is only an external-package reference, not a globally missing resource.
5. Prioritize non-1×1 textures and payloads with meaningful GPU/Stream data for alpha-channel inspection. Do not equate an Alpha Clip mask with fractional transparency, and do not assume a translucent alias alone proves fabric suitability.

This staged workflow keeps the global census reasonably small while preserving a path to complete family-specific evidence. It also avoids making the all-material descriptor mode the default, which would inflate the report without improving the initial family counts.
