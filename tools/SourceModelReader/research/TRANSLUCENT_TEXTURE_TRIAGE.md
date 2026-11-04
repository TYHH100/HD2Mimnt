# Translucent Texture and Variable-ID Triage

Date: 2026-11-05  
Status: metadata cross-check complete; actual texture payload still requires local inspection.

## 1. A useful dictionary resolution

The raw texture-node type used by both targeted Translucent samples is:

| Raw ID | Pinned SDK dictionary label | Source |
|---|---|---|
| `0xdab4d0ee` | `NormalXyRoughnessOpacity` | Community Edition `hashlists/texturetypes.txt`, commit `4df125a5e6a34d03532a06b545f700ec5353a98f`; independently present in AQ Edition's pinned `hashlists/shadervariables.txt` |

This is a much more useful clue than the generic word “Translucent”: the material's only texture node is labelled as a combined Normal-XY / Roughness / Opacity input in the pinned dictionary. It suggests that the texture may carry several material channels, including an opacity-related channel. The dictionary label alone does not prove the exact channel packing or how the shader uses it.

## 2. The eight Translucent sample variables are now named

The following IDs from `material_translucent_a.json` and `material_translucent_b.json` match the AQ Edition shader-variable dictionary pinned at commit `71b380046d33d552eef0892bd48661ff25591380`:

| Variable ID | SDK label | Sample A | Sample B |
|---|---|---:|---:|
| `0xab58e70d` | `OpacityBase` | 1.0 | 0.4 |
| `0x43695f7b` | `ResolutionSetting` | 3 | 3 |
| `0x4dec56f9` | `RoughnessBase` | ~0.28 | ~0.1 |
| `0x393cf79e` | `SpecularBase` | ~0.63 | 0.5 |
| `0xcb577b8f` | `BaseColor` | (0, 0, 0) | (~0.4511, ~0.1576, ~0.1576) |
| `0x3c8b121f` | `AlphaColor` | (0, 0, 0) | (0, 0, 0) |
| `0x5ac3e52f` | `AlphaColorWeight` | 0 | 0 |
| `0x5a341fb6` | `TextureTile` | 0 | 0 |

The strongest current evidence is the pairing of an `OpacityBase` scalar with a texture slot explicitly labelled `NormalXyRoughnessOpacity`. The two child-material samples also demonstrate that opacity and surface parameters are instance-level values: the parent ID, texture node type, texture ID and variable layout are the same while `OpacityBase`, roughness, specular and base colour differ. A separate file, `material_translucent_parent.json`, has ParentMaterialID zero and `OpacityBase` approximately 0.02; it must not be conflated with sample A (whose value is 1.0).

This is still not a complete shader contract. In particular, the name `OpacityBase` does not establish whether final opacity is multiplied by a texture channel, used as a constant, clamped, or combined with another value. That needs the matching material template/shader evidence or a controlled in-game test.

## 3. Global lookup result for the referenced Texture ID

The full lookup report `translucent_texture_lookup.json` scanned all 3,522 scannable packages and 317,992 resource-entry rows without package errors. It found FileID `bf281073a422022c` in 182 distinct packages.

Every one of the 182 occurrences has the same TOC/GPU/Stream sizes:

- TOC: 340 bytes
- GPU: 4 bytes
- Stream: 0 bytes

This is a highly unusual payload size for a conventional BC-compressed image. Four bytes could be consistent with a single uncompressed 1×1 RGBA texel, but that is only a hypothesis until the embedded DDS header is parsed. The 182 matching size records do not prove the payload bytes are identical; the lookup report did not hash TOC or GPU payloads.

Do not classify this resource as a missing texture, a valid 1×1 placeholder, or a transparency mask until the DDS header and 4-byte payload are inspected.

Representative package selected for the first local inspection: `00cf0ef7df912a6e`.

## 4. Exact next local commands

Run from `tools/SourceModelReader` (adjust the game data path if needed):

```powershell
py -3 source_model_reader.py hd2-texture "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 00cf0ef7df912a6e --texture bf281073a422022c --output "research\translucent_normal_roughness_opacity.dds" --preserve-raw --json "research\translucent_normal_roughness_opacity.json"
```

This does not request BC7 decoding. The first objective is to capture the DDS width, height, DXGI format, mip count, alpha mode, raw header, and GPU SHA-256. If the parser reports a 1×1 uncompressed format, the four payload bytes can then be inspected as a candidate texel; if the header declares a larger or compressed texture, investigate the resource-layout mismatch instead.

The unresolved fourth Alpha Clip+ dependency should be globally located separately:

```powershell
py -3 source_model_reader.py hd2-find "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --type Texture --resource 12d4692531c1fd35 --json "research\alphaclip_plus_texture_lookup.json"
```

After locating it, inspect one matching package with `hd2-texture`, preserving raw payloads.

## 5. Interpretation for fabric and sheer materials

The evidence now supports a more specific research hypothesis:

- Alpha Clip / Alpha Clip+ are documented mask/cutout families; the Wiki states the Alpha Clip preset does not support partial transparency.
- The Translucent family has a distinct SDK alias and a texture slot labelled `NormalXyRoughnessOpacity`, plus an `OpacityBase` parameter.
- Therefore, Translucent is the more promising family to investigate for graded opacity in sheer fabric, stockings, lace and similar materials.
- This does **not** yet prove that it can reproduce all desired fabric effects. The exact opacity channel, blend behavior, depth sorting, two-sided rendering, and texture authoring constraints still need verification.

The practical conversion target should preserve the original source alpha information and map it only after the HD2 material's actual opacity input is established. Do not flatten partial alpha into an Alpha Clip mask as an automatic fallback.


## 6. Alpha Clip+ variable dictionary cross-check

The 13 Alpha Clip+ variable IDs in `material_alphaclip_plus.json` also resolve against the same pinned AQ shader-variable dictionary:

| Variable ID | SDK label |
|---|---|
| `0x529a4aaf` | `OpacityThreshold` |
| `0xa3351311` | `DirtGlobalAmount` |
| `0x43695f7b` | `ResolutionSetting` |
| `0x64aab07b` | `DirtAoCoverage` |
| `0x6fd0b9e7` | `DirtGradientMin` |
| `0x60e7d2a1` | `WeatheringVariant` |
| `0x4a7cd0ef` | `WeatheringCoverage` |
| `0xbd16a396` | `WeatheringDynamicTiling` |
| `0x32c02400` | `TemporaryEmissiveRemapRange` |
| `0xc012efe1` | `PreventTerrainDeformation` |
| `0xa83f44cd` | `DirtDetailMasking` |
| `0x6ddbae8f` | `DirtGradientMax` |
| `0x9ed04da2` | `WeatheringMasking` |

This is an important caution: an SDK variable dictionary is global, and a variable name does not by itself establish that the corresponding feature is active or meaningful in every material family. The Alpha Clip+ sample includes IDs whose friendly names refer to weathering, dirt and deformation. Preserve the raw values and the family/template context; do not implement a generic material serializer that assumes every known global variable is semantically active in every parent.

The Alpha Clip+ sample's `OpacityThreshold` is 0.5 in the extracted preview. This is consistent with a threshold parameter name, but the actual clipping equation and texture channel still require the Alpha Clip+ template/shader contract. It is not evidence for fractional transparency.


## 7. SDK template UI labels reveal a mapping discrepancy

A further check of AQ Edition's pinned Blender add-on `__init__.py` (same commit `71b380046d33d552eef0892bd48661ff25591380`) adds an important caveat.

Its `Global_Materials` table calls ParentMaterialID `84e5de1cc6b74ada` the `glass` template. Its `TextureTypeLookup["glass"]` presents a single UI slot labelled `Glass stain`. However, the actual one-node Translucent sample's raw node type ID `0xdab4d0ee` resolves through the Community/AQ texture-type hash dictionary as `NormalXyRoughnessOpacity`.

These labels are not obviously equivalent. Possible explanations include a fork-specific or legacy UI slot mapping, a distinction between a generic node-type hash and the template's displayed slot label, or an outdated alias/slot table. The available evidence does not decide between them.

Consequences:
- Do not build a hardcoded mapping from ParentMaterialID directly to a universal ordered list of semantic texture slots.
- Preserve both the raw per-node type ID and the SDK-specific UI slot label, including the SDK revision.
- Treat `Glass stain` versus `NormalXyRoughnessOpacity` as an explicit unresolved dictionary/template discrepancy.
- Before authoring a material converter, compare the actual sample's resource structure against the AQ add-on's import/export code and Community Edition's corresponding material handling.

This discrepancy is especially important for fabric conversion: if the one texture is genuinely a packed normal/roughness/opacity input, it may be directly relevant to sheer fabric; if the AQ UI slot mapping represents a different role, we must not infer the packed-channel layout from one dictionary label alone.


## 8. Alpha Clip texture-node IDs provide a useful control case

The same pinned Community Edition texture-type dictionary resolves the Alpha Clip sample's three node IDs as follows:

| Raw texture-node type ID | Dictionary label | Observed sample role |
|---|---|---|
| `0x1d57dcf3` | `NormalXyAoRoughMap` | Normal / AO / roughness |
| `0xcbde381b` | `OpacityClipMap` | Alpha cutout mask |
| `0x848ba63b` | `BaseColorMetalMap` | Base color / metallic |

These align with AQ Edition's pinned `TextureTypeLookup["alphaclip"]` UI slot order: Normal/AO/Roughness, Alpha Mask, Base Color/Metallic.

Alpha Clip+ resolves to:
- `0x1d57dcf3` — `NormalXyAoRoughMap`
- `0xca6f2cf1` — `EmissiveFStop10IntensityMap`
- `0x848ba63b` — `BaseColorMetalMap`
- `0xcbde381b` — `OpacityClipMap`

This also matches AQ Edition's Alpha Clip+ UI slot order: Normal/AO/Roughness, Emission, Base Color/Metallic, Alpha Mask. The unresolved fourth Texture FileID in the Alpha Clip+ sample therefore belongs to the node whose type is `OpacityClipMap`, not an unknown extra channel.

This is a useful positive control: the Alpha Clip family’s type-ID dictionary and UI slot order agree, while the Glass/Translucent single-node sample has the discrepancy documented in section 7. That makes the discrepancy specific and actionable rather than evidence that the entire texture-type dictionary is unreliable.


## 9. Parsed result for the globally shared Texture FileID

The user supplied the output of the exact `hd2-texture` command above. The parser confirms:

| Field | Parsed value |
|---|---|
| Texture FileID | `bf281073a422022c` |
| Package inspected | `00cf0ef7df912a6e` |
| TOC / GPU / Stream | 340 / 4 / 0 bytes |
| DDS dimensions | 1 × 1 |
| DXGI format | `R8G8B8A8_UNORM` (28) |
| Mip count / array size | 1 / 1 |
| Calculated mip payload | 4 bytes |
| TOC mipmap records | All zero / not populated |
| GPU SHA-256 | `bb4e0cc3512cd5ab57d7608d50c7a948932c43d9d8b48c1b442aad4b499fd296` |
| DDS alpha mode | Unknown (not a declared straight/premultiplied/opaque mode) |

The resource is therefore a validly described 1×1 RGBA8 texture with a four-byte GPU payload. It is not a BC texture and it is not a useful full-resolution material map. Its presence as the same FileID in 182 scanned packages strongly suggests a widely reused default/sentinel/placeholder texture candidate. That is a working classification, not a proven engine semantic.

Important limitations:
- The lookup report proves the same FileID and identical resource sizes across 182 package occurrences, but it did not hash each occurrence's payload. Do not claim all 182 GPU payloads are byte-identical yet.
- The checked-in raw `.gpu.bin` has a Git blob SHA, and the JSON records the GPU SHA-256 above. The connector's text-only binary retrieval path cannot safely expose the four raw byte values; obtain the actual bytes locally if channel values are needed.
- DDS `alpha_mode_name: unknown` does not mean alpha is absent. The actual 8-bit alpha value is in the fourth RGBA byte, which has not yet been recorded here.
- This texture must not be used as evidence that the Translucent material's intended opacity map has been found. Both sample A and B point to this shared 1×1 resource, which is likely an unbound/default slot in those specific instances.

### Consequence for the fabric hypothesis

The material family still has an `OpacityBase` parameter and its texture-node type dictionary label still includes `Opacity`. However, these two samples do not demonstrate a real spatial opacity texture: their referenced resource is only 1×1. The evidence for graded transparency must come from another material instance using the same family but referencing a non-placeholder texture, or from the actual shader/template contract.

### Next targeted data collection

1. Search the material census for every material with ParentMaterialID `84e5de1cc6b74ada` and retain all texture FileIDs, not just the two hand-picked samples.
2. Group those materials by texture FileID and dimensions/payload size. Prioritize non-1×1 textures with a populated GPU or Stream payload.
3. Inspect a few occurrences of `bf281073a422022c` in different packages and compare their GPU SHA-256 values to determine whether the 182 entries are identical payload copies.
4. Record the four RGBA bytes from the local `.gpu.bin` using Python `Path.read_bytes().hex()`; do not infer the alpha byte from the DDS alpha-mode flag.
5. Keep the root-parent sample (`material_translucent_parent.json`, ParentMaterialID zero, OpacityBase ~0.02) separate from child samples A/B (ParentMaterialID `84e5de1cc6b74ada`, OpacityBase 1.0 and 0.4 respectively).


## 10. Cross-reference: the Alpha Clip+ opacity input is already named in the compatibility migration code

A repository-wide code search found an existing compatibility transform in `src/HD2Mimnt.Infrastructure/Analysis/CompatibilityResourceTransforms.cs` (default-branch commit `6ab020a7880b3fdb472bab843c61eb2871b0b358`). This code declares:

- Current Alpha Clip+ parent: `0xC6042E3403385D40`
- Current opacity texture FileID: `0x12D4692531C1FD35`
- Current Alpha Clip+ texture semantics: `NormalXyAoRoughMap`, `EmissiveFStop10IntensityMap`, `BaseColorMetalMap`, `OpacityClipMap`

Its migration routine appends the `CurrentEmissiveOpacityTextureId` as the fourth texture reference when converting the legacy three-input emissive material to the current four-input layout. Its comment explicitly describes the new input as an opacity-clip input.

This is a valuable cross-check for the raw sample:
- The unresolved fourth Texture FileID in `material_alphaclip_plus.json` is exactly the same `0x12d4692531c1fd35` constant.
- The raw fourth texture-node type is `0xcbde381b`, which the pinned dictionary resolves as `OpacityClipMap`.
- The existing migration code therefore corroborates that this ID is intended to occupy the Alpha Clip+ opacity-clip slot.

Provenance and limits matter: the compatibility transform itself carries a source-code comment saying it was adapted from Helldivers2ModManager (Apache-2.0). Treat it as a repository cross-reference and historical implementation evidence, not an independent reverse-engineering proof or a source to copy into SourceModelReader. The actual Texture resource still needs to be located and inspected in the current game data; the constant's name and use do not prove its payload dimensions, color values, or whether it is a neutral/default mask.

This also narrows the next lookup: prioritize inspecting `0x12d4692531c1fd35` as an Alpha Clip opacity-mask candidate, then compare its payload and actual material instances with the known 1×1 shared Texture `0xbf281073a422022c`.


## 11. Correction to the texture-type dictionary provenance

A fresh direct fetch of AQ Edition's pinned `hashlists/texturetypes.txt` returned 404, and the same raw ID was not verified in its `hashlists/shadervariables.txt`. The earlier wording that the `0xdab4d0ee` texture-type label was independently present in AQ's shader-variable dictionary was incorrect: those are different dictionaries and must not be conflated.

The verified evidence for `0xdab4d0ee → NormalXyRoughnessOpacity` is the Community Edition `hashlists/texturetypes.txt` entry. AQ Edition independently supplies the `glass` template's UI slot label `Glass stain` in its `__init__.py`, but that is a template UI mapping, not an independent hash-name resolution for the raw node type.

This correction narrows the discrepancy precisely:
- Community Edition hash dictionary: raw node type ID → `NormalXyRoughnessOpacity`.
- AQ Edition template UI: ParentMaterialID alias `glass` → one UI slot labelled `Glass stain`.
- The mapping between those two representations remains unresolved.
