# Helldivers 2 Material and Texture Research Notes

Status: research only. This document records verified resource structure and community authoring behavior. It does **not** claim that the reader can yet decode every material instance or reconstruct the engine's complete render pipeline.

## 1. Resource identities and links

| Resource | 64-bit type ID |
|---|---:|
| Material | 0xeac0b497876adedf |
| Texture | 0xcd4238c6a0c69e32 |
| Unit | 0xe0a48d0be9a7453f |

A Unit contains material resource IDs, while a Material contains texture resource IDs. These are separate resources and must be followed by identity, not by assuming textures are embedded in the Unit or Material payload.

AQ Edition's public hd2_system/material_packaging.py documents the Material-to-Texture dependency: a material's TOC payload contains a texture count at byte offset 64 and 64-bit texture IDs beginning at 136 + 4 × NumTextures. Its packaging code preserves referenced texture resource entries and tracks unresolved external texture references. This is dependency evidence; the reader must implement its own parser and checks.

## 2. Material binary layout observed in both public SDK parser implementations

Both AQ Edition and Community Edition's stingray/material.py read the same common sequence:

| Offset | Field | Width | Notes |
|---:|---|---:|---|
| 0 | leading unknown bytes | 12 | preserved |
| 12 | EndOffset | 4 | semantics not established |
| 16 | unknown | 8 | preserved |
| 24 | ParentMaterialID | 8 | 64-bit parent/template identifier |
| 32 | unknown bytes | 32 | preserved |
| 64 | NumTextures | 4 | texture-node count |
| 68 | unknown bytes | 36 | preserved |
| 104 | NumVariables | 4 | shader-variable descriptor count |
| 108 | unknown bytes | 12 | preserved |
| 120 | VariableDataSize | 4 | payload size; exact boundary semantics need sample validation |
| 124 | unknown bytes | 12 | preserved |
| 136 | texture type IDs | 4 × NumTextures | AQ calls these TexUnks; Community calls them TextureTypes |
| 136 + 4N | texture resource IDs | 8 × NumTextures | 64-bit IDs, one per texture node |
| 136 + 12N | variable descriptors | 20 × NumVariables | five uint32 fields per variable |
| after descriptors | variable value payload | variable | values are addressed by descriptor offsets |

The common five-uint32 shader-variable descriptor is:
1. klass
2. elements
3. ID
4. offset
5. elementStride

The public parsers label klass 0/1/2/3 as Scalar/Vector2/Vector3/Vector4 and 12 as Other. They resolve ID through a shader-variable name hash list and seek to payload_start + offset to read scalar/vector float values. However, the displayed parser loop reads only one class-sized vector; the complete meaning of elements, elementStride, VariableDataSize, alignment and overlapping/gapped offsets has not been proven for all material families. A new reader must preserve raw descriptors and raw payload spans before interpreting arrays or strides.

### Real-sample VariableDataSize boundary evidence

Three user-provided FS-05 Material resources were inspected: `8cd6b9c938feaf90` (6 variables), `93aa849d6d00109d` (30 variables), and `a781f50f508ed81b` (87 variables). In all three, the bytes remaining after the descriptor table were exactly 8 bytes larger than `VariableDataSize` (56/64, 232/240, and 436/444 respectively). Every recognized scalar/vector preview range ended within the declared size. The current reader therefore treats `VariableDataSize` as the declared variable-value region, limits previews to that region, and preserves any subsequent bytes as a separate unresolved trailing payload. This is an observed pattern across three samples, not proof that all Material variants use an 8-byte trailer or that its semantics are known.

### User-provided FS-05 Material instance audit

The following dictionary matches were checked against AQ Edition's pinned shader-variable list and Community Edition's pinned texture-type list. Names are metadata labels only; they do not prove shader execution behavior.

| Material ID | ParentMaterialID | Texture types named | Shader variable IDs named | Texture IDs found in selected package |
|---|---|---:|---:|---:|
| `8cd6b9c938feaf90` | `70cd11d0bb7dbd9e` (unknown in both parent maps) | 0 / 0 | 5 / 6 | 0 / 0 |
| `93aa849d6d00109d` | `9e0a4db5c90ed6d3` (unknown in both parent maps) | 11 / 12 | 25 / 30 | 4 / 12 |
| `a781f50f508ed81b` | `5a4597ed9ab5f84f` (unknown in both parent maps) | 12 / 12 | 87 / 87 | 4 / 12 |

`93aa849d6d00109d` exposes variable labels associated with Blood, Bug Gunk, Weathering, Frost and related surface effects. `a781f50f508ed81b` exposes Color Variation, Blood Overlay, Mud, Tear, Scenario Overlay, World Overlay, Wetness and Scorch Marks labels. This makes both useful samples for studying dynamic surface effects, but neither is yet evidence of a translucent fabric shader.

A Texture ID reported as not found in the selected `8670598c1f4462dc` package is only an out-of-package reference; it must not be described as globally missing. Some Texture IDs are reused by multiple nodes in the same Material (for example, one resource ID appears under both BloodSplatterTiler and BugSplatterTiler in the `93aa...` instance). Preserve the node-to-type relationship and the resource ID separately; do not deduplicate texture nodes solely by ID.

AQ calls the per-texture uint32 values TexUnks; Community Edition treats the same values as texture-type IDs and resolves them through hashlists/texturetypes.txt. Preserve the raw integer and optionally attach a versioned lookup name.

## 3. Parent material IDs are not globally stable names

The 64-bit ParentMaterialID is read at byte offset 24. SDKs use lookup tables to display friendly material-family names. Those aliases are not consistent across forks:

| ParentMaterialID | AQ Edition alias | Community Edition alias |
|---:|---|---|
| 9576304397847579354 | glass | translucent |
| 15235712479575174153 | bloom | emissive |
| 17265463703140804126 | advanced_default | advanced |
| 8580182439406660688 | basic+Fixed | basic+ |
| 15586118709890920288 | alphaclip | alphaclip |
| 14268580320343776576 | alphaclip+ | alphaclip+ |

Other entries are fork-specific. AQ has aliases for flowing, translucent_flowing, reticle and cutout_scope; Community has its own scope and basic+ IDs.

Therefore:
- Always preserve the raw ParentMaterialID.
- Keep alias tables tied to a named SDK revision or verified game build.
- Do not let an alias such as glass or translucent silently decide rendering mode.
- An unknown parent ID is an unresolved shader family, not proof that a material is opaque or invalid.

This parent identifier is consistent with Stingray's parent/child material model, where child materials inherit a parent shader graph and override exposed values. For HD2, exact inheritance rules still need confirmation from game resources before applying defaults. See Autodesk's Stingray parent/child material documentation.

## 4. Texture binary and DDS preservation

The public SDK texture parsers read:
- 15 mipmap entries, each containing two uint32 offsets/sizes and two uint16 dimensions;
- a 148-byte DDS header;
- raw texture bytes from the .stream payload when present, otherwise from .gpu_resources;
- DDS width, height, mip count and DXGI format from the DDS header;
- Community Edition additionally reads the DX10 array size at offset 140.

The SDKs contain DXGI lookup tables for common BC-compressed and uncompressed formats. The texture reader must preserve the raw format ID, complete header, mip descriptors, array metadata and raw payload. It must not collapse an array texture into one image or discard mip levels.

## 5. Transparency is a material/shader behavior, not simply an RGBA flag

The community material documentation distinguishes multiple behaviors. These are not interchangeable.

### Alpha Clip / Alpha Clip+

The documented presets use a separate grayscale Alpha Mask. White is visible and black is transparent. The Wiki explicitly says this preset does not support partial transparency. This is a hard cutout/mask path, not a general fractional-opacity blend. Alpha Clip+ additionally has a multi-colour emissive texture.
Reference: https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/alphaclip.html

### Translucent

The documented Translucent preset exposes:
- OpacityBase scalar (0 is fully see-through; 1 is still not completely solid);
- RoughnessBase scalar;
- BaseColor Vector3.

The overview describes this family as accepting an alpha mask for uses such as hair/decals, while the detailed preset page describes a single 1×1 normal node and opacity/color/roughness controls. The exact source channel and per-pixel alpha interpretation need verification against the actual material instance and shader behavior; do not infer it from the word Translucent alone.
References:
- https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/translucent.html
- https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html

### Advanced / ordinary material RGBA alpha

The documented Advanced preset uses the alpha channel of its color/emissive texture as an emissive mask, not as transparency. Thus a texture can contain alpha values while the material remains opaque.
Reference: https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/advanced.html

### Generic RGBA texture data

The Texture documentation describes RGBA image alpha as a 0–255 transparency value, but the material shader decides how a given texture node/channel is interpreted. A pixel's alpha channel cannot be assumed to drive transparency in every material family.
Reference: https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/textures/overview.html

For an external model conversion pipeline, keep these concepts separate:
- texture channel content (raw alpha bytes and compression format);
- material texture-node role/type;
- shader family / parent material;
- shader-variable values such as opacity and thresholds;
- actual render behavior (cutout, fractional blend, additive, etc.), which may require verified material templates or in-game validation.

A lace pattern may be representable as an Alpha Clip silhouette if fully binary, but that discards fractional edge coverage. Stockings or fabric requiring graded translucency must not be converted to Alpha Clip by default. Preserve the source alpha channel and mark the output material mode unresolved until an HD2 material family is verified to support the required per-pixel opacity.

## 6. Material slot and section association

The community Wiki documents that, in legacy workflows, material slot order may also determine which vertex groups are assigned to bones; different body parts can have different slot orders. The reader already preserves each mesh section's MaterialIndex and resolves skinning Remaps using the matching section material index. Do not reorder material slots during conversion unless source/target section mapping is explicitly established.
Reference: https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/material-slot-order.html

## 7. Current gaps and next parser milestones

Implemented in the current experimental 0.8.4 branch:
- bounded Material TOC payload extraction with header, texture-node IDs, shader-variable descriptors and raw value-payload preservation;
- same-package Texture dependency presence checks;
- conservative first scalar/vector preview for recognized variable classes.

Not implemented yet:
- per-build shader-parent alias manifests;
- complete shader-variable payload/stride validation;
- a Texture resource/DDS array parser in Source Model Reader;
- per-mesh material ID to section MaterialIndex cross-reference output;
- classification of material render modes beyond verified template evidence;
- in-game tests for cutout versus fractional transparency.

Next:
1. Validate the new Material parser against real game resources and confirm the fixed-header/table/value-payload boundaries.
2. Resolve shader-variable IDs and texture-node types through revision-scoped dictionaries while preserving raw values.
3. Parse Texture DDS metadata and retain alpha-channel bytes without destructive conversion.
4. Interpret arrays/strides and attach versioned parent aliases only after real-instance validation.
5. Test real Alpha Clip, Translucent/Glass and ordinary opaque Material resources before implementing automatic material conversion.

## References inspected

- AQ Edition stingray/material.py, stingray/texture.py, utils/constants.py, hashlists/shadervariables.txt and hd2_system/material_packaging.py, pinned to commit 71b380046d33d552eef0892bd48661ff25591380:
  https://github.com/Estecsky/HD2SDK-AQ-Edition/tree/71b380046d33d552eef0892bd48661ff25591380
- Community Edition stingray/material.py, stingray/texture.py, utils/constants.py and hashlists/texturetypes.txt:
  https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition/tree/4df125a5e6a34d03532a06b545f700ec5353a98f
- Helldivers 2 Modding Wiki:
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/alphaclip.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/translucent.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/textures/overview.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/material-slot-order.html
- Autodesk Stingray parent/child material documentation:
  https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/stingray_help/lighting_rendering/shading_and_materials/parent_child_materials.html

## 6. Texture resource layout observed in the pinned public SDK readers

Both pinned `stingray/texture.py` implementations (`Boxofbiscuits97/HD2SDK-CommunityEdition` at `4df125a5e6a34d03532a06b545f700ec5353a98f` and `Estecsky/HD2SDK-AQ-Edition` at `71b380046d33d552eef0892bd48661ff25591380`) read the common Texture TOC sequence in this order:

| Region | Offset | Size | Fields |
|---|---:|---:|---|
| Fixed prefix | 0 | 12 | three uint32 values: `UnkID`, `Unk1`, `Unk2` |
| Mipmap table | 12 | 180 | 15 records × 12 bytes |
| DDS header | 192 | 148 | DDS magic + 124-byte DDS header + 20-byte DX10 extension |
| Common minimum | 0 | 340 | sum of the regions above |

Each mipmap record is read as `<IIHH>`: `Start` (uint32), `BytesLeft` (uint32), `Height` (uint16), and `Width` (uint16). All 15 records should be preserved even when some entries are zero or not currently understood.

The 148-byte DDS block is interpreted by the public SDKs as a DX10 DDS header. Relevant fields in the reader are:

- DDS magic at byte 0 of the 148-byte block;
- `dwSize`, flags, height, width, pitch/linear size, depth and mip count in the standard DDS header;
- `FourCC` at block offset 84, expected to be `DX10` by these SDK readers;
- DXGI format at offset 128;
- resource dimension at 132;
- misc flags at 136;
- array size at 140;
- misc flags 2 at 144.

The current Source Model Reader reports the numeric DXGI value and a friendly name only for values in its small checked dictionary. Unknown values remain explicit numeric IDs. Extra TOC bytes after byte 340 are retained as unresolved trailing data; no semantics are assigned to them.

### GPU versus Stream payload behavior

In both inspected SDK versions, the read-side logic selects `Stream.Data` when the Stream payload is non-empty; otherwise it selects `Gpu.Data`. The AQ and Community implementations agree on this precedence. This is implementation evidence for the selected DDS byte path, not proof that the non-selected payload is redundant or disposable in every resource.

Accordingly, the independent reader records the GPU and Stream sizes and SHA-256 hashes separately. `hd2-texture` writes the SDK-selected payload after the original 148-byte DDS header. With `--preserve-raw`, it also writes separate `.gpu.bin` and `.stream.bin` files for every non-empty sidecar, retaining both representations when both exist.

A FileID can be present in more than one SLIM package. Each package occurrence must be treated as a distinct candidate until DDS metadata and payload hashes have been compared. A same-ID occurrence in another package is not automatically interchangeable with the selected copy.

### Current interpretation limits

- The parser does not infer transparency from DXGI format, alpha-capable format names, or the mere presence of alpha bits.
- The optional pixel-preview path currently decodes only BC7_UNORM/BC7_UNORM_SRGB base mips through the external `texture2ddecoder` package. Other BC formats are not pixel-decoded yet.
- Texture arrays are reported by `ArraySize`; they are not flattened or split by the reader.
- Mipmap `Start` and `BytesLeft` are preserved as metadata but their exact relationship to payload offsets and streamed/resident mip partitions still needs validation against several real resources.
- A Texture used by an Alpha Clip, Translucent or Advanced Material must be interpreted together with its Material parent/family and shader variables. Texture-level evidence alone is insufficient to distinguish cutout, fractional opacity and emissive-mask usage.

The first real-game verification candidates from the FS-05 Material dependency scan include one Stream-backed Texture (`0d371130ba00addb`, 5,592,432 Stream bytes plus 1,392 GPU bytes) and one GPU-only Texture (`17c4075f57e4211a`, 349,552 GPU bytes) in package `18235e0c9ec0e636`. These are useful parser test inputs, not established transparency examples.

### Real-game BC7 mip-chain validation (FS-05 dependencies)

The first two Texture exports from package `18235e0c9ec0e636` provide a useful controlled comparison. Both DDS headers report DXGI format 98 (`BC7_UNORM`), DX10 resource dimension 3 (2D), and ArraySize 1.

| Texture FileID | Dimensions | Mips | GPU bytes | Stream bytes | Observed layout |
|---|---:|---:|---:|---:|---|
| `17c4075f57e4211a` | 512×512 | 10 | 349,552 | 0 | GPU contains the complete BC7 mip chain; the 15-entry TOC mipmap table is unpopulated in the text report |
| `0d371130ba00addb` | 2048×2048 | 12 | 1,392 | 5,592,432 | Stream contains the complete BC7 mip chain; GPU size exactly matches the contiguous mip tail from level 6 (32×32) through 1×1 |

For BC7, each mip level occupies `ceil(width/4) × ceil(height/4) × 16` bytes. Applying that block-size rule to the 2048×2048 sample produces a full-chain size of 5,592,432 bytes. Its calculated tail from level 6 is 1,392 bytes, exactly equal to the GPU resource size. The observed TOC records also match the calculated mip widths/heights, cumulative starts and `BytesLeft` values across all 12 levels.

For the 512×512 sample, the calculated 10-level chain is 349,552 bytes, exactly equal to the GPU resource size. The command printed no populated TOC mipmap records for this sample. This shows that dimensions/format/mip count from the DDS header can still establish the expected storage size even when the per-level TOC table is zeroed; it does not prove the TOC table is always optional for all Texture variants.

`hd2_texture.py` now calculates known BC and uncompressed mip-chain sizes, classifies GPU/Stream payload lengths as a full chain or a contiguous single-array mip tail when sizes match, and validates populated TOC records against expected width, height, start and bytes-left values. A table that is entirely unpopulated is reported as `not_populated`, separately from a metadata mismatch. This is byte-layout validation only; it does not decode BC7 pixels or determine channel/alpha meaning.

### Optional BC7 base-mip pixel preview

The reader can now optionally decode the selected full-resolution BC7 base mip through the external `texture2ddecoder` 1.0.6 package. Its `decode_bc7` function returns BGRA8 bytes; the reader reorders those bytes into a standards-compliant RGBA PNG using only Python's standard library. The dependency is not vendored and is not required for TOC/DDS inspection.

Supported preview scope is intentionally narrow: DXGI 98/99 (BC7 UNORM/SRGB), DX10 Texture2D, ArraySize 1, supported dimensions, and a selected payload classified as a complete mip chain. Only Mip 0 is decoded. A GPU resident mip tail is never treated as a complete base-level payload. Missing decoder dependency, incomplete payloads, unsupported formats or array/dimension variants produce explicit errors.

The JSON preview section records the decoder/version, source dimensions, mip level, byte counts and per-pixel Alpha histogram. It separately counts Alpha=0, Alpha=1..254 and Alpha=255. These counts describe the decoded texture bytes only. They do not prove the game's Material shader enables fractional blending or even consumes that channel as opacity.

Real-game pixel/Alpha inspection remains a manual validation step: inspect the generated PNG and JSON for the two FS-05 samples, then follow the actual Material texture-node link and parent/render family before classifying transparency.
