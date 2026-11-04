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
| `0xab58e70d` | `OpacityBase` | 0.02 | 0.4 |
| `0x43695f7b` | `ResolutionSetting` | 3 | 3 |
| `0x4dec56f9` | `RoughnessBase` | ~0.28 | ~0.1 |
| `0x393cf79e` | `SpecularBase` | ~0.63 | 0.5 |
| `0xcb577b8f` | `BaseColor` | (0, 0, 0) | (~0.4511, ~0.1576, ~0.1576) |
| `0x3c8b121f` | `AlphaColor` | (0, 0, 0) | (0, 0, 0) |
| `0x5ac3e52f` | `AlphaColorWeight` | 0 | 0 |
| `0x5a341fb6` | `TextureTile` | 0 | 0 |

The strongest current evidence is the pairing of an `OpacityBase` scalar with a texture slot explicitly labelled `NormalXyRoughnessOpacity`. The two samples also demonstrate that opacity and surface parameters are instance-level values: the parent ID, texture node type, texture ID and variable layout are the same while `OpacityBase`, roughness, specular and base colour differ.

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
