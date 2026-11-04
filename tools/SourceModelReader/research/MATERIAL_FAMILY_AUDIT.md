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
