# HD2 Vertex Position-Space Probe

- Matrix convention candidate: row_major_4x4_row_vector
- Identity tolerance: 0.0001
- Position buffer tolerance: 1e-06
- Vertex sample limit per mesh: all

Question: are stored vertex positions already in the space the render LODs share, or must the non-identity mesh-world matrix be applied to them? Two independent tests answer it: nearest-vertex distance for surfaces that should coincide, and volume containment for auxiliary geometry that is not a copy of the render surface.

## fs05_lower_body

- Unit: 210fb2f60f8308ea
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 3 | 5 | g_legs_hips_male_shadow / 2607622032 | ok | identity | 608 | -0.00032, -0.01222, 0.68840 | 0.66156 | - | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 1 | 2 | 6 | g_legs_hips_male_LOD2 / 524905431 | ok | identity | 339 | 0.00141, -0.01168, 0.69621 | 0.66408 | - | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 2 | 1 | 7 | g_legs_hips_male_LOD1 / 3301214708 | ok | identity | 738 | -0.00071, -0.00888, 0.68425 | 0.65909 | - | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 0 | 8 | g_legs_hips_male / 562172190 | ok | identity | 1374 | 0.00000, 0.00064, 0.67972 | 0.65822 | - | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | -1 | 13 | hips_culling / 2249734937 | no_skin_attributes | non_identity | 439 | 0.01402, -0.00000, -0.00456 | 1.76465 | 1.76452 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |

### Nearest-vertex distance to the reference render mesh

| Role | MeshInfo | LOD | Matrix | Reference | Sampled | Raw mean | Raw median | Raw p95 | Raw max | Mesh-world mean | Mesh-world median | Mesh-world p95 |
|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| control | 0 | 3 | identity | mesh 3 (LOD 0) | 608 | 0.000485 | 0.000273 | 0.001677 | 0.004653 | - | - | - |
| auxiliary | 4 | -1 | non_identity | mesh 3 (LOD 0) | 439 | 0.667140 | 0.704800 | 1.012846 | 1.075329 | 0.372658 | 0.387135 | 0.555640 |

### Auxiliary volume containment of the render union

union-outside is the largest distance the render meshes escape a candidate volume; box-slack is how much the candidate volume exceeds the render union. Zero union-outside means the volume fully wraps the render geometry.

| MeshInfo | LOD | Transform | Skinning | Render union min | Render union max | Union outside raw | Union outside mesh-world | Contains raw | Contains mesh-world | Box slack raw | Box slack mesh-world |
|---:|---:|---|---|---|---|---:|---:|---|---|---:|---:|
| 4 | -1 | hips_culling | no_skin_attributes | -0.25456, -0.13770, 0.56257 | 0.25397, 0.12263, 0.90217 | 0.39275 | 0.00000 | False | True | 1.07198 | 0.58066 |

## fs05_torso_left_arm

- Unit: 324fb206612ce4f7
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 4 | 14 | l_arm_culling / 3648454405 | ok | non_identity | 439 | 0.01402, -0.00000, -0.00456 | 1.76465 | 1.76030 | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |
| 1 | 3 | 34 | g_torso_arm_l_female_shadow / 522128404 | ok | identity | 1426 | -0.38426, 0.02374, 1.33755 | 0.92728 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:vec4_half, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 2 | 2 | 35 | g_torso_arm_l_female_LOD2 / 3766176514 | ok | identity | 838 | -0.34277, 0.00860, 1.35647 | 0.92484 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:vec4_half, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 1 | 36 | g_torso_arm_l_female_LOD1 / 2627263307 | ok | identity | 1707 | -0.40489, 0.03102, 1.32713 | 0.92749 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:vec4_half, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | 0 | 37 | g_torso_arm_l_female / 46827504 | ok | identity | 3115 | -0.49673, 0.08069, 1.27596 | 0.92714 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:vec4_half, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |

### Nearest-vertex distance to the reference render mesh

| Role | MeshInfo | LOD | Matrix | Reference | Sampled | Raw mean | Raw median | Raw p95 | Raw max | Mesh-world mean | Mesh-world median | Mesh-world p95 |
|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| control | 1 | 3 | identity | mesh 4 (LOD 0) | 1426 | 0.000232 | 0.000076 | 0.000929 | 0.002669 | - | - | - |
| auxiliary | 0 | 4 | non_identity | mesh 4 (LOD 0) | 439 | 1.352348 | 1.369809 | 1.742210 | 1.821279 | 0.338105 | 0.366554 | 0.455663 |

### Auxiliary volume containment of the render union

union-outside is the largest distance the render meshes escape a candidate volume; box-slack is how much the candidate volume exceeds the render union. Zero union-outside means the volume fully wraps the render geometry.

| MeshInfo | LOD | Transform | Skinning | Render union min | Render union max | Union outside raw | Union outside mesh-world | Contains raw | Contains mesh-world | Box slack raw | Box slack mesh-world |
|---:|---:|---|---|---|---|---:|---:|---|---|---:|---:|
| 0 | 4 | l_arm_culling | ok | -0.75140, -0.15803, 1.08176 | -0.10045, 0.29723, 1.56145 | 1.05203 | 0.00000 | False | True | 1.59117 | 0.40463 |

## avatar_composite

- Unit: 4d1c334d294dfa97
- Package: 18235e0c9ec0e636
- Geometry source: CompositeUnit

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 0 | 102 | (unresolved) / 1278906229 | ok | identity | 27355 | -0.02868, 0.02340, 1.28921 | 2.47694 | - | bone_index0:vec4_uint8, bone_weight0:vec4_half, color0:rgba8, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |

No correspondence test was possible: this Unit lacks a second ordinary identity-matrix render mesh to compare against.

## Cross-Unit shared vertex buffers

| Left | Right | Vertices | Bitwise identical buffers | Differing buffers | Position max abs diff | Within tolerance |
|---|---|---:|---|---|---:|---|
| fs05_lower_body mesh 4 (hips_culling / LOD -1 / non_identity) | fs05_torso_left_arm mesh 0 (l_arm_culling / LOD 4 / non_identity) | 439 | normal0, uv0 | position0 | 2.441e-17 | True |

## Interpretation limits

- Nearest-vertex distance measures which space a stored position already lives in; it does not prove the runtime shader order.
- The candidate mesh-world matrix comes from the parser's own hierarchy reconstruction, which is validated for internal consistency only.
- Vertices are read, never modified; no weight transfer or conversion is performed or implied by this probe.
- A control pair with two identity mesh-world matrices must show a near-zero raw distance before any auxiliary result is read.
- Buffer identity is decided by hashing decoded attribute values; a float32 rounding difference such as negative zero is reported as a difference, not silently ignored.
