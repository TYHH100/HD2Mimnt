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
  https://github.com/Boxofbiscuits97/HD2SDK-CommunityEdition
- Helldivers 2 Modding Wiki:
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/overview.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/alphaclip.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/translucent.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/textures/overview.html
  https://boxofbiscuits97.github.io/HD2-Modding-Wiki/dev/materials/material-slot-order.html
- Autodesk Stingray parent/child material documentation:
  https://help.autodesk.com/cloudhelp/ENU/Stingray-Help/stingray_help/lighting_rendering/shading_and_materials/parent_child_materials.html
