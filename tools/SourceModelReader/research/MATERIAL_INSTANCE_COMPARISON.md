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
