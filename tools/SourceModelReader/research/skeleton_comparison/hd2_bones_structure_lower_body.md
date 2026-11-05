# HD2 Bones Resource Raw Structure

- Package: 8670598c1f4462dc
- Unit: 210fb2f60f8308ea
- Bones resource: 210fb2f60f8308ea
- TOC payload bytes: 1470
- GPU payload bytes: 0
- Stream payload bytes: 0
- Declared names: 82 / read: 82
- Declared LOD count: 6
- Bytes left after the name block: 0

## Regions, in the order the current parser assumes

| Region | Offset | Size | Layout |
|---|---:|---:|---|
| header | 0 | 8 | uint32 name_count, uint32 lod_count |
| lod_float_array | 8 | 24 | 6 float32 (skipped by the current parser) |
| hash_array | 32 | 328 | 82 uint32 |
| lod_levels | 360 | 24 | 6 uint32 |
| names | 384 | 1086 | NUL-terminated strings |

- Skipped float array: 0.25, 0.15, 0.07, 0.04, 0.02, 0.01
- LOD levels: 82, 82, 82, 82, 82, 82
- First names: StingrayEntityRoot, FbxAxisSystem_ConvertNode, mech_pilot_light, skeleton, collision, game_mesh, root, c_cullingvolume, c_culling, grp_legs_hips_male, boss, g_legs_hips_male

## Tail after the name block

- Size: 0 (size mod 4 = 0)
- Hex: `(empty)`
- ASCII: `(empty)`

## Unit float values found inside the Bones payload

A hit means a float32 stored in the Unit also appears in this resource at the
reported offset; it is a starting point for locating a second copy of transforms,
not proof that the Bones resource stores them.

| (no Unit float32 value was found in the Bones payload) | - | - |

## Interpretation limits

- Region boundaries follow the layout assumed by `parse_bones_resource`; a tail of
  zero bytes means no further region was found after the names.
- Hits are individual float32 matches. Repeated values such as a shared bone offset
  can coincide without proving a matrix layout.
- The payload is read without modification; nothing here rewrites or re-encodes data.
