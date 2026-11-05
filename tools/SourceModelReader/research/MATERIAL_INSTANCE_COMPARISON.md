# Material Instance Comparison — 2026-11-05

Status: six targeted Material reports parsed; SDK dictionary cross-check complete for the mapped IDs. Texture payload analysis and render-behavior validation remain pending.

## Scope and source revisions

This note compares the six targeted reports uploaded from the local game installation:

- material_glass_8b3ff5502c.json
- material_glass_2f80391371.json
- material_glass_9423bb5f9a.json
- material_flowing_7b25f939.json
- material_flowing_344020be.json
- material_alphaclip_plus_14var.json

The 13-variable Alpha Clip+ control is material_alphaclip_plus.json.

Dictionary sources are pinned to exact revisions:
- Community Edition: Boxofbiscuits97/HD2SDK-CommunityEdition, commit 4df125a5e6a34d03532a06b545f700ec5353a98f, files hashlists/texturetypes.txt and hashlists/shadervariables.txt.
- AQ Edition: Estecsky/HD2SDK-AQ-Edition, commit 71b380046d33d552eef0892bd48661ff25591380, files hashlists/shadervariables.txt, utils/constants.py, and __init__.py.

Friendly labels are revision-scoped metadata. Raw 32-bit node and variable IDs remain authoritative in this report.

## 1. Glass / Translucent: a non-placeholder texture instance was found

All three reports use ParentMaterialID 0x84e5de1cc6b74ada and a single texture node of raw type 0xdab4d0ee. The Community Edition dictionary labels that node NormalXyRoughnessOpacity; AQ's UI template labels the slot Glass stain. This discrepancy remains unresolved and must not be silently normalized.

| Material FileID | Package | TOC bytes | Texture FileID | In-package? | OpacityBase | RoughnessBase | SpecularBase | TextureTile |
|---|---|---:|---|---|---:|---:|---:|---:|
| 8b3ff5502ac52c25 | 01edaa2a7e84b840 | 384 | bf281073a422022c | No | 0.25 | 0.33 | 0.65 | 200 |
| 2f80391371cce33e | 029a193f2a08167c | 384 | bf281073a422022c | No | 1.0 | 0.28 | 0.63 | 0 |
| 9423bb5f9a715c2e | 09985dc611a3a8b6 | 416 | 4406087459ab9513 | Yes | 0.28 | 0.05 | 0.99 | 2 |

The 8b3... BaseColor is approximately (0.1522, 0.1522, 0.1522); the 2f803... BaseColor is (0, 0, 0); and the 9423... BaseColor is approximately (0.3152, 0.3152, 0.3587). AlphaColor is zero and AlphaColorWeight is zero in all three reports.

### Texture dependency significance

The first two materials refer to the previously inspected Texture FileID bf281073a422022c. Its selected global occurrence is a valid 1×1 RGBA8 resource with 4 GPU bytes and no Stream payload. This is a shared default/sentinel candidate, not evidence of a spatial opacity mask.

The third material instead references 4406087459ab9513, present in package 09985dc611a3a8b6, with:
- Texture TOC: 340 bytes
- GPU payload: 1,392 bytes
- Stream payload: 349,552 bytes

This size profile makes it the first strong candidate in this sample set for a nontrivial spatial map. Its dimensions, DXGI format, mip layout, decoded channels and alpha statistics have not yet been inspected. Payload size alone does not prove that it contains a spatial opacity channel or that the shader consumes it for fractional blending.

### Additional Glass descriptor

Material 9423bb5f9a715c2e has nine descriptors instead of eight. The extra descriptor is:
- raw variable ID 0x2e0bee05
- class: Scalar
- preview: 1.0
- value offset: 48

This ID was not found in either pinned shader-variable dictionary queried for this audit. Its name and semantics remain unresolved. Do not infer a friendly label from neighboring variable order.

## 2. Translucent Flowing: same five texture slots, parameterized instances

Both instances use ParentMaterialID 0x110199a9c3401242, aliased as translucent_flowing by AQ Edition. Each has 87 shader-variable descriptors and the two reports have exactly the same 87-variable ID set. Of those, 61 first-vector previews differ and 26 match.

The two material resources are:
- 7b25f93956fd9aff, package 046d4441a6dae0a9, TOC 2,704 bytes.
- 344020be12d962b5, package 687d548654b91be6, TOC 2,864 bytes.

### Ordered texture-node pairs

| Slot | Raw node type | Community dictionary label | Texture FileID |
|---:|---|---|---|
| 0 | 0x55c87149 | NoiseMap02 | fa0bb48a25ed50fe |
| 1 | 0x001ff6d4 | unresolved in pinned CE dictionary | 7f37735b6a73092a |
| 2 | 0xfbef0214 | NoiseMap01 | fa0bb48a25ed50fe |
| 3 | 0x5a3bc7c0 | NormalMap02 | 628b6b9b554e06e2 |
| 4 | 0x2b33d35f | NormalMap01 | 371ef38cf24b6cc2 |

All five slots in each material are external to that material's own package. There are four unique Texture FileIDs because slots 0 and 2 reuse fa0bb48a25ed50fe. Do not deduplicate the two nodes: they have distinct node-type roles despite sharing one resource ID.

AQ's pinned TextureTypeLookup["translucent_flowing"] labels the five ordered UI slots as Noise02 Mask, blank, Noise01 Mask, blank Pos, blank Pos. Its UI/help text describes this as a semi-transparent flowing material with two independently flowing masks and flow direction, sourced from the Illuminate Overseer shield. This supports classifying it as a specialized animated shield/effect material, not a generic fabric preset.

### Selected mapped parameters

Values below are the first scalar/vector previews, not a complete interpretation of any array or shader equation.

| Variable ID | AQ dictionary label | Instance A: 7b25... | Instance B: 344020... |
|---|---|---:|---:|
| 0x19618106 | OpacityCore | 0.35 | 0.633 |
| 0x718ea144 | OpacityMult | 0.5 | 1.0 |
| 0x78f6f5cc | OpacityFresnelExp | 5.0 | 7.223 |
| 0xfcb740fb | MinEdgeOpacity | 0.15 | 0 |
| 0xb961d0c7 | Noise02Speed | (-0.1, -0.1) | (0.35, 0.15) |
| 0x3f591185 | Noise01Speed | (0.2, 0.15) | (-0.15, 0.35) |
| 0x95da4b25 | Noise01Tile | (6, 3) | (3, 3) |
| 0x0dfd4183 | Noise02Tile | (8, 4) | (3, 3) |
| 0x1f62595d | Normal01Tile | (6, 3) | (3, 3) |
| 0xe91875c2 | Normal02Tile | (8, 4) | (3, 3) |
| 0x706e5dda | Normal01Speed | (0.2, 0.15) | (-0.15, 0.5) |
| 0x2e3d91be | Normal02Str | 0.1 | 1.0 |
| 0x0d690cf6 | Normal01Str | 0.1 | 1.0 |
| 0x06776dda | Color | (0, 0.4235, 1) | (0, 0.01, 0.05) |
| 0x0f3bc3a8 | ColorMult | 1.0 | 5.0 |
| 0x0f605308 | CurrentHealth | 0 | 0 |

The remaining five IDs are not all unresolved: four additional labels above (Color, ColorMult, CurrentHealth, Normal01Str) were found in the pinned AQ dictionary but not in the pinned CE dictionary. One variable, 0x01115993, remains unmapped in both checked dictionaries (A=1.0, B=0.52).

The 87 descriptors comprise 63 Scalar, 20 Vector2 and 4 Vector3 entries. The large number of moving noise/normal, Fresnel and opacity controls reinforce the SDK's shield-effect description. This is useful evidence for how the engine's specialized effect materials expose opacity controls, but it is not evidence that this family is suitable for stockings, lace or other stationary fabric.

## 3. Alpha Clip+: four in-package textures and one additional variable

The new 14-variable instance is:
- package 0149ef7d2ee3efca
- Material FileID 55a794f03a405c4c
- ParentMaterialID 0xc6042e3403385d40
- Material TOC: 528 bytes

All four texture dependencies resolve in the same package:

| Slot | Raw node type | Community dictionary label | Texture FileID | GPU bytes | Stream bytes |
|---:|---|---|---|---:|---:|
| 0 | 0x1d57dcf3 | NormalXyAoRoughMap | 4fd83b4cc6018cd4 | 1,392 | 349,552 |
| 1 | 0xca6f2cf1 | EmissiveFStop10IntensityMap | 151752e4f6318eca | 2,744 | 174,776 |
| 2 | 0x848ba63b | BaseColorMetalMap | a5e4843f7ac0a56d | 1,392 | 349,552 |
| 3 | 0xcbde381b | OpacityClipMap | 0e392f47257b0d3e | 2,744 | 174,776 |

Compared with the previously inspected 13-variable Alpha Clip+ baseline, all 13 variable IDs remain present. The additional descriptor is:
- 0x243f5a2f — ScalarfieldOpacitySharpness (present in both pinned dictionaries)
- class Scalar, value 1.0, offset 56.

The existing OpacityThreshold descriptor 0x529a4aaf remains 0.5. The added scalar-field sharpness parameter is a schema difference, not proof of partial-alpha blending. The family still has an explicit OpacityClipMap slot; retain its cutout/clip classification unless stronger template or in-game evidence shows otherwise.

The 13-variable baseline references a different fourth texture ID, 12d4692531c1fd35, whereas this 14-variable instance points to 0e392f47257b0d3e. These should be treated as separate instance dependencies, not one universal Alpha Clip+ texture set.

## 4. Material value-payload tails vary

The six new reports do not support a universal fixed-size trailer:

| Sample | Bytes available after descriptors | Declared variable-data bytes | Preserved trailing bytes |
|---|---:|---:|---:|
| Glass 8b3... | 76 | 76 | 0 |
| Glass 2f803... | 76 | 64 | 12 |
| Glass 9423... | 88 | 80 | 8 |
| Flowing 7b25... | 768 | 760 | 8 |
| Flowing 3440... | 928 | 916 | 12 |
| Alpha Clip+ 14-var | 64 | 60 | 4 |

Continue preserving the declared variable region and all trailing bytes independently. Do not hardcode an 8-byte or 12-byte trailer rule. This comparison also confirms that a Material can carry a different descriptor count and variable payload length while retaining the same parent family.

## 5. Evidence boundaries and conclusions

Established:
- A Glass/Translucent-family instance exists with a nontrivial referenced Texture resource (4406087459ab9513).
- The Glass family has instance-level values for OpacityBase, RoughnessBase, SpecularBase, BaseColor and TextureTile.
- The two Translucent Flowing instances share the exact five ordered node/type/resource pairs and the same 87-variable ID set, while 61 preview values differ.
- The 14-variable Alpha Clip+ sample adds ScalarfieldOpacitySharpness=1.0 and has all four Texture dependencies in-package.
- The variable-payload tail length varies between these real samples.

Not established:
- The decoded image dimensions, format, mip structure or per-channel meaning for 4406087459ab9513.
- Whether the Glass sample's NormalXyRoughnessOpacity label precisely describes the node's in-engine sampled channels; AQ's UI calls it Glass stain.
- The final blend equation, depth-write/sort behavior, culling/two-sided behavior or whether Glass supports spatial fractional opacity for cloth.
- The exact meaning of the extra Glass variable ID 0x2e0bee05 or Flowing variable ID 0x01115993.
- Whether any of these material instances are assigned to a character mesh; the census and these targeted material reports do not establish mesh usage.

For the fabric-conversion objective, the basic Glass/Translucent family remains the best next investigation target because it combines an OpacityBase control with a texture node whose Community dictionary label includes Opacity, and a newly found instance references a substantial texture payload. This is a priority hypothesis, not a verified fabric material solution. Alpha Clip remains a separate hard-cutout path; Translucent Flowing is a specialized animated shield/effect path.

## 6. Next extraction batch

1. Parse Glass texture 4406087459ab9513 from package 09985dc611a3a8b6 with hd2-texture, preserving DDS, GPU and Stream raw payloads.
2. Export the four Alpha Clip+ 14-variable textures from package 0149ef7d2ee3efca; these form a useful known-type reference set.
3. Globally locate the four unique Translucent Flowing Texture FileIDs (fa0bb48a25ed50fe, 7f37735b6a73092a, 628b6b9b554e06e2, 371ef38cf24b6cc2) before selecting one package per texture for export.
4. Only after texture headers and channels are checked, select character Unit/material-section references and design controlled in-game material tests.

No render mode should be inferred from the Material's friendly alias or the presence of alpha bytes alone.


## 7. AQ Edition bundled preset binaries: independent binary cross-check

The AQ Edition preset files at commit 71b380046d33d552eef0892bd48661ff25591380 were retrieved as Base64 file contents and decoded once into their raw Material payloads. They use the same 136-byte header, ordered texture tables, 20-byte variable descriptors and offset-based scalar/vector value payload that SourceModelReader parses. This is a comparison of public sample data, not a reuse of the SDK's implementation.

Pinned preset blob SHAs:
- materials/glass.material — c5880b8541924a6ee0d20a404f10bf4310e8e669
- materials/translucent_flowing.material — 14bb4bf38d8a4a2dbc8f5fccc4b0bd67c01fa19f

### Glass preset and the non-placeholder game instance

AQ's bundled glass.material parses as:
- 528 bytes; ParentMaterialID 0x84e5de1cc6b74ada
- one texture node, raw type 0xdab4d0ee
- Texture FileID 4406087459ab9513
- 14 variable descriptors; declared value data 96 bytes; 4 trailing bytes

The game Material 9423bb5f9a715c2e has the exact same ParentMaterialID, raw texture-node type and Texture FileID. Seven overlapping variable IDs also have exactly matching values in the preset and game instance:

| Variable ID | AQ dictionary name | Value in both |
|---|---|---:|
| 0xab58e70d | OpacityBase | 0.28 |
| 0x43695f7b | ResolutionSetting | 3 |
| 0x4dec56f9 | RoughnessBase | 0.05 |
| 0x393cf79e | SpecularBase | 0.99 |
| 0xcb577b8f | BaseColor | (0.3152, 0.3152, 0.3587) |
| 0x5a341fb6 | TextureTile | 2 |
| 0x2e0bee05 | unresolved in both checked dictionaries | 1 |

The game instance additionally contains AlphaColor=(0,0,0) and AlphaColorWeight=0, which are not present in the bundled AQ preset's descriptor list. Conversely, the preset carries seven additional Dirt/Weathering descriptors not present in this nine-descriptor game instance. The shared seven values and exact shared texture dependency are a strong binary-level cross-check that Texture FileID 4406087459ab9513 is the resource reference in AQ's Glass preset—not merely an unrelated large texture found in a package.

This materially raises the priority of extracting 4406087459ab9513. It still does not prove its image channels or the engine's final blend equation; the AQ UI label Glass stain and the Community dictionary label NormalXyRoughnessOpacity remain a documented semantic discrepancy.

### Translucent Flowing preset versus game instances

AQ's bundled translucent_flowing.material parses as:
- 2,864 bytes; ParentMaterialID 0x110199a9c3401242
- five texture nodes; 87 variable descriptors
- 916 declared variable-data bytes; 12 trailing bytes

Its 87-variable ID set exactly matches both inspected game instances. The preset's five Texture FileIDs differ from the five references shared by the two game instances, so the preset and game instances are different texture sets under the same parent/template schema.

Against game instance 344020be12d962b5 (the closer match), only nine of the 87 first scalar/vector previews differ from the AQ preset:
- Noise02Speed (0xb961d0c7)
- EllipseExp (0xa6facdc9)
- Noise01Tile (0x95da4b25)
- Noise01Speed (0x3f591185)
- EllipseMask (0x73b5fc89)
- NoiseFresnelMin (0xe0a06d64)
- OpacityMult (0x718ea144)
- LuminocityExp (0x2ca054d5)
- Color (0x06776dda)

This indicates a close parameter match to the SDK preset while retaining a different per-instance texture set. The other game instance 7b25f93956fd9aff differs from the preset in 61 preview values. These values are still raw first scalar/vector previews; the comparison does not claim to reproduce the shader equations.

### What the bundled presets do and do not prove

The AQ preset binaries provide valuable reference fixtures for validating Material parsing and understanding which IDs/values are exposed by a known authoring template. They are not proof that every game material is byte-identical to the SDK preset, that all default values are engine defaults, or that a given texture channel is consumed for opacity. Keep preset revision, raw payload identity, parent ID and texture IDs separate in future comparisons.


## 8. Uploaded Texture extraction results — 2026-11-05

The five requested Texture reports and their DDS/raw payload files are present in research/textures/. All five reports have status "ok", zero parser warnings, and a complete TOC mipmap-table match.

| Material role / report | Texture FileID | Dimensions | DXGI | Mips | Stream bytes | GPU bytes | Mipmap validation |
|---|---|---:|---|---:|---:|---:|---|
| Glass / NormalXyRoughnessOpacity candidate | 4406087459ab9513 | 512×512 | BC7_UNORM | 10 | 349,552 | 1,392 | match |
| Alpha Clip+ Normal/AO/Roughness | 4fd83b4cc6018cd4 | 512×512 | BC7_UNORM | 10 | 349,552 | 1,392 | match |
| Alpha Clip+ Emissive | 151752e4f6318eca | 512×512 | BC1_UNORM | 10 | 174,776 | 2,744 | match |
| Alpha Clip+ Base Color/Metal | a5e4843f7ac0a56d | 512×512 | BC7_UNORM | 10 | 349,552 | 1,392 | match |
| Alpha Clip+ OpacityClipMap | 0e392f47257b0d3e | 512×512 | BC1_UNORM | 10 | 174,776 | 2,744 | match |

For all five textures, Stream stores the full mip chain and GPU stores only a contiguous mip tail:
- BC7 resources: GPU mip tail begins at mip level 4 (32×32).
- BC1 resources: GPU mip tail begins at mip level 3 (64×64).
The extracted DDS selects Stream when non-empty, in agreement with the public SDK read path documented in MATERIAL_FORMAT_NOTES.md.

All ten populated mip records in each report match the calculated per-level dimensions, offsets and remaining-byte counts. The DDS alpha-mode metadata is unknown. No pixel decode or channel statistics were requested in this extraction batch, so no alpha-content claim can yet be made.

The Stream payload hashes differ across all five reports. Equal dimensions, format and payload sizes do not mean the texture contents are interchangeable.

## 9. Global lookup of Translucent Flowing texture dependencies

The newly uploaded translucent_flowing_texture_lookup.json reports a complete scan:
- 3,522 / 3,522 scannable packages scanned.
- 317,992 Texture resource-entry rows scanned.
- 4 requested Texture FileIDs found; 0 missing.
- 0 package errors.

| Texture FileID | Occurrences | Distinct packages | TOC / GPU / Stream bytes |
|---|---:|---:|---|
| 371ef38cf24b6cc2 | 24 | 24 | 340 / 349,552 / 1,398,128 |
| 628b6b9b554e06e2 | 21 | 21 | 340 / 349,552 / 1,398,128 |
| 7f37735b6a73092a | 2 | 2 | 340 / 2,744 / 174,776 |
| fa0bb48a25ed50fe | 3 | 3 | 340 / 1,392 / 1,398,128 |

Two packages contain all four unique texture dependencies:
- 70b4860e8169622e
- be01f1529f774970

Use package 70b4860e8169622e for the next extraction batch. The shared FileID fa0bb48a25ed50fe is used by two distinct Translucent Flowing texture nodes in each Material; retain both node-to-role assignments even though they resolve to the same resource.

The 1,398,128-byte full Stream payload size of the first, second and fourth resources is consistent with a 1024×1024 BC7 full mip chain, while the third resource's 174,776-byte Stream payload is consistent with a 512×512 BC1 full mip chain. These are size-derived format/dimension expectations only; inspect each Texture DDS header before assigning those properties as confirmed.

## 10. Pixel-content validation and next command batch

The existing optional preview decoder currently supports BC7_UNORM / BC7_UNORM_SRGB, Texture2D, ArraySize=1, and requires the optional package texture2ddecoder==1.0.6. It decodes only the base mip and reports minimum, maximum, unique, transparent, partial-alpha and opaque pixel counts. It does not currently decode BC1.

The Glass Texture is BC7_UNORM with a complete Stream mip chain, so it meets the decoder's structural preconditions. Pixel-level opacity evidence is still pending. Run:

```powershell
py -3 -m pip install texture2ddecoder==1.0.6
py -3 .\source_model_reader.py hd2-texture "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package 09985dc611a3a8b6 --texture 4406087459ab9513 --output ".\research\textures\glass_4406087459ab9513.dds" --preserve-raw --preview-png --json ".\research\textures\glass_4406087459ab9513.json"
```

The preview command re-exports the same files and updates the JSON with pixel-alpha statistics and a PNG path. A nonzero partial-alpha pixel count would prove that the decoded texture stores fractional alpha values; it would still not prove that the Glass shader samples alpha as opacity.

Then export the Translucent Flowing dependencies from the one package that contains all four:

```powershell
$gameData = "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data"
$package = "70b4860e8169622e"
$textures = @(
    @{ Name="flow_noise02"; Id="fa0bb48a25ed50fe" },
    @{ Name="flow_noise01"; Id="fa0bb48a25ed50fe" },
    @{ Name="flow_unknown_slot"; Id="7f37735b6a73092a" },
    @{ Name="flow_normal02"; Id="628b6b9b554e06e2" },
    @{ Name="flow_normal01"; Id="371ef38cf24b6cc2" }
)
foreach ($item in $textures) {
    py -3 .\source_model_reader.py hd2-texture $gameData --package $package --texture $item.Id --output ".\research\textures\$($item.Name).dds" --preserve-raw --json ".\research\textures\$($item.Name).json"
    if ($LASTEXITCODE -ge 2) { throw "Texture extraction failed: $($item.Name)" }
}
```

The first and second node roles intentionally share one Texture FileID. Extracting the same resource under two role labels is optional; one raw export is sufficient, but the node mapping must remain duplicated in any material graph representation.


## 11. Glass BC7 pixel-alpha decode — 2026-11-05

The user reran the Glass extraction with the optional `texture2ddecoder==1.0.6` preview path. The updated JSON report is `research/textures/glass_4406087459ab9513.json`, and the 512×512 decoded PNG is checked in beside it.

Decoder report:
- Status: ok; decoder: texture2ddecoder 1.0.6; codec: BC7; DXGI 98 (`BC7_UNORM`).
- Decoded mip: level 0, 512×512.
- Decoded input: 262,144 bytes; decoded output: 1,048,576 bytes RGBA8.
- Alpha minimum: 0; maximum: 255; unique values: 255.
- Fully transparent: 865 / 262,144 pixels (0.32997%).
- Partial alpha: 261,274 / 262,144 pixels (99.66812%).
- Fully opaque: 5 / 262,144 pixels (0.00191%).
- Non-opaque: 262,139 / 262,144 pixels (99.99809%).
- Alpha histogram: 256 bins; total count matches the full 512×512 pixel count.

The decoded PNG visually has a blue/purple, fine-detail appearance consistent with the look of a normal-map-style texture. This is a visual observation only; it does not independently identify the RGB channel layout.

### What this establishes

1. The BC7 texture payload genuinely decodes to a broad range of pixel-alpha values. The alpha component is not merely a binary 0/255 cutout mask in the decoded base mip.
2. This is consistent with the Community Edition node-type label `NormalXyRoughnessOpacity` and the AQ Glass preset's exact reference to this Texture FileID.
3. It raises confidence that the resource is a packed surface-data texture with meaningful alpha variation, and makes it the strongest current candidate for studying per-pixel opacity in the Glass family.

### What this does not establish

- The decoder only measures stored BC7 alpha values; it does not prove the Glass shader samples alpha as opacity.
- The pixel histogram alone cannot identify which RGB channel carries roughness or whether all RGB channels are used as expected.
- The screenshot/preview is not a proof of the actual channel packing.
- The render blend mode, depth-write/sort policy, culling and two-sided behavior remain unknown.

Therefore, do not yet implement a conversion rule that writes source transparency directly into this alpha channel. The next verification should correlate the texture's RGB/alpha components with an in-game material/mesh sample and a controlled Glass parameter change.

The Glass pixel-level report is a positive result: fractional alpha data exists in the exact resource referenced by AQ's Glass template. It is not yet a shader-behavior proof.
