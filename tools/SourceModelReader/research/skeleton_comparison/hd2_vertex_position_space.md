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

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_legs_hips_male_shadow | identity | 608 | 2432 | 0 | 6.054e-08 | 5.411e-08 | 6.054e-08 | True |
| 1 | 2 | g_legs_hips_male_LOD2 | identity | 339 | 1356 | 0 | 6.102e-08 | 5.397e-08 | 6.102e-08 | True |
| 2 | 1 | g_legs_hips_male_LOD1 | identity | 738 | 2952 | 0 | 6.101e-08 | 5.442e-08 | 6.101e-08 | True |
| 3 | 0 | g_legs_hips_male | identity | 1374 | 5496 | 0 | 4.548e-08 | 5.581e-09 | 4.548e-08 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_legs_hips_male_shadow | single_index_attr_0 | float32 scalar | 1 | 608 | 0 | 0.000000 | 0.000000 | 6.054e-08 | 0 | True |
| 1 | 2 | g_legs_hips_male_LOD2 | single_index_attr_0 | float32 scalar | 1 | 339 | 0 | 0.000000 | 0.000000 | 6.102e-08 | 0 | True |
| 2 | 1 | g_legs_hips_male_LOD1 | single_index_attr_0 | float32 scalar | 1 | 738 | 0 | 0.000000 | 0.000000 | 6.101e-08 | 0 | True |
| 3 | 0 | g_legs_hips_male | single_index_attr_0 | float32 scalar | 1 | 1374 | 0 | 0.000000 | 0.000000 | 4.548e-08 | 0 | True |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_legs_hips_male_shadow | 0 | 976 | 964 | 0 | 12 | 0.9398 | consistent |
| 1 | 2 | g_legs_hips_male_LOD2 | 0 | 488 | 484 | 0 | 4 | 0.9504 | consistent |
| 2 | 1 | g_legs_hips_male_LOD1 | 0 | 1222 | 1213 | 0 | 9 | 0.9434 | consistent |
| 3 | 0 | g_legs_hips_male | 0 | 2444 | 2408 | 0 | 36 | 0.9445 | consistent |
| 4 | -1 | hips_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 0 | 3 | g_legs_hips_male_shadow | identity | True | 2 | 0.0000, -0.0190, 0.9417 | -0.0003, -0.0122, 0.6884 | -0.0003, -0.0122, 0.6884 | 0.2534 | 0.2534 | identical |
| 1 | 2 | g_legs_hips_male_LOD2 | identity | True | 2 | 0.0000, -0.0190, 0.9417 | 0.0014, -0.0117, 0.6962 | 0.0014, -0.0117, 0.6962 | 0.2456 | 0.2456 | identical |
| 2 | 1 | g_legs_hips_male_LOD1 | identity | True | 2 | 0.0000, -0.0190, 0.9417 | -0.0007, -0.0089, 0.6843 | -0.0007, -0.0089, 0.6843 | 0.2576 | 0.2576 | identical |
| 3 | 0 | g_legs_hips_male | identity | True | 4 | 0.0000, -0.0202, 0.9576 | 0.0000, 0.0006, 0.6797 | 0.0000, 0.0006, 0.6797 | 0.2786 | 0.2786 | identical |

### Scale and rotation composition gate

- Transforms: 16; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## fs05_torso

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

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 4 | l_arm_culling | non_identity | 439 | 5268 | 0 | 2.167e-07 | 8.643e-08 | 1.595e+00 | True |
| 1 | 3 | g_torso_arm_l_female_shadow | identity | 1426 | 17112 | 0 | 3.305e-07 | 1.192e-07 | 3.305e-07 | True |
| 2 | 2 | g_torso_arm_l_female_LOD2 | identity | 838 | 10056 | 0 | 2.251e-07 | 1.131e-07 | 2.251e-07 | True |
| 3 | 1 | g_torso_arm_l_female_LOD1 | identity | 1707 | 20484 | 0 | 3.305e-07 | 1.115e-07 | 3.305e-07 | True |
| 4 | 0 | g_torso_arm_l_female | identity | 3115 | 37380 | 0 | 2.221e-07 | 4.469e-08 | 2.221e-07 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 4 | l_arm_culling | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 439 | 0 | 0.001955 | 0.000862 | 2.040e-07 | 0 | True |
| 0 | 4 | l_arm_culling | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 439 | 0 | 0.001955 | 0.000862 | 4.654e-08 | 0 | True |
| 0 | 4 | l_arm_culling | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 439 | 0 | 0.001955 | 0.000862 | 1.913e-07 | 0 | True |
| 0 | 4 | l_arm_culling | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 439 | 0 | 2.000000 | 1.997415 | 1.352e-07 | 0 | False |
| 1 | 3 | g_torso_arm_l_female_shadow | single_index_attr_0 | vec4_half | 3 | 1426 | 0 | 0.000366 | 0.000060 | 3.305e-07 | 0 | True |
| 1 | 3 | g_torso_arm_l_female_shadow | single_index_attr_1 | vec4_half | 3 | 1426 | 0 | 0.000366 | 0.000060 | 3.305e-07 | 0 | True |
| 1 | 3 | g_torso_arm_l_female_shadow | single_index_attr_2 | vec4_half | 3 | 1426 | 0 | 0.000366 | 0.000060 | 2.251e-07 | 0 | True |
| 1 | 3 | g_torso_arm_l_female_shadow | every_index_attr_shares_weights | vec4_half | 3 | 1426 | 0 | 2.000858 | 1.999986 | 2.934e-07 | 0 | False |
| 2 | 2 | g_torso_arm_l_female_LOD2 | single_index_attr_0 | vec4_half | 3 | 838 | 0 | 0.000275 | 0.000043 | 2.169e-07 | 0 | True |
| 2 | 2 | g_torso_arm_l_female_LOD2 | single_index_attr_1 | vec4_half | 3 | 838 | 0 | 0.000275 | 0.000043 | 2.169e-07 | 0 | True |
| 2 | 2 | g_torso_arm_l_female_LOD2 | single_index_attr_2 | vec4_half | 3 | 838 | 0 | 0.000275 | 0.000043 | 2.251e-07 | 0 | True |
| 2 | 2 | g_torso_arm_l_female_LOD2 | every_index_attr_shares_weights | vec4_half | 3 | 838 | 0 | 2.000813 | 1.999998 | 1.547e-07 | 0 | False |
| 3 | 1 | g_torso_arm_l_female_LOD1 | single_index_attr_0 | vec4_half | 3 | 1707 | 0 | 0.000366 | 0.000065 | 3.305e-07 | 0 | True |
| 3 | 1 | g_torso_arm_l_female_LOD1 | single_index_attr_1 | vec4_half | 3 | 1707 | 0 | 0.000366 | 0.000065 | 3.305e-07 | 0 | True |
| 3 | 1 | g_torso_arm_l_female_LOD1 | single_index_attr_2 | vec4_half | 3 | 1707 | 0 | 0.000366 | 0.000065 | 2.251e-07 | 0 | True |
| 3 | 1 | g_torso_arm_l_female_LOD1 | every_index_attr_shares_weights | vec4_half | 3 | 1707 | 0 | 2.001099 | 1.999997 | 2.934e-07 | 0 | False |
| 4 | 0 | g_torso_arm_l_female | single_index_attr_0 | vec4_half | 3 | 3115 | 0 | 0.000366 | 0.000073 | 2.221e-07 | 0 | True |
| 4 | 0 | g_torso_arm_l_female | single_index_attr_1 | vec4_half | 3 | 3115 | 0 | 0.000366 | 0.000073 | 2.221e-07 | 0 | True |
| 4 | 0 | g_torso_arm_l_female | single_index_attr_2 | vec4_half | 3 | 3115 | 0 | 0.000366 | 0.000073 | 3.981e-08 | 0 | True |
| 4 | 0 | g_torso_arm_l_female | every_index_attr_shares_weights | vec4_half | 3 | 3115 | 0 | 2.001099 | 1.999999 | 1.611e-07 | 0 | False |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 4 | l_arm_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |
| 1 | 3 | g_torso_arm_l_female_shadow | 0 | 2089 | 2004 | 5 | 80 | 0.8978 | consistent |
| 2 | 2 | g_torso_arm_l_female_LOD2 | 0 | 476 | 428 | 4 | 44 | 0.7969 | mixed |
| 2 | 2 | g_torso_arm_l_female_LOD2 | 1 | 568 | 548 | 0 | 20 | 0.9131 | consistent |
| 3 | 1 | g_torso_arm_l_female_LOD1 | 0 | 685 | 638 | 5 | 42 | 0.8300 | consistent |
| 3 | 1 | g_torso_arm_l_female_LOD1 | 1 | 1927 | 1885 | 0 | 42 | 0.9407 | consistent |
| 4 | 0 | g_torso_arm_l_female | 0 | 4531 | 4484 | 0 | 47 | 0.9620 | consistent |
| 4 | 0 | g_torso_arm_l_female | 1 | 696 | 649 | 5 | 42 | 0.8324 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 0 | 4 | l_arm_culling | non_identity | False | 5 | -0.1509, -0.0276, 1.3545 | 0.0140, -0.0000, -0.0046 | -0.4789, -0.0443, 1.3146 | 1.3693 | 0.3309 | placed |
| 1 | 3 | g_torso_arm_l_female_shadow | identity | True | 22 | -0.5844, 0.1524, 1.2159 | -0.3843, 0.0237, 1.3375 | -0.3843, 0.0237, 1.3375 | 0.2672 | 0.2672 | identical |
| 2 | 2 | g_torso_arm_l_female_LOD2 | identity | True | 22 | -0.5844, 0.1524, 1.2159 | -0.3428, 0.0086, 1.3565 | -0.3428, 0.0086, 1.3565 | 0.3143 | 0.3143 | identical |
| 3 | 1 | g_torso_arm_l_female_LOD1 | identity | True | 22 | -0.5844, 0.1524, 1.2159 | -0.4049, 0.0310, 1.3271 | -0.4049, 0.0310, 1.3271 | 0.2436 | 0.2436 | identical |
| 4 | 0 | g_torso_arm_l_female | identity | True | 25 | -0.5143, 0.1329, 1.1944 | -0.4967, 0.0807, 1.2760 | -0.4967, 0.0807, 1.2760 | 0.0984 | 0.0984 | identical |

### Scale and rotation composition gate

- Transforms: 38; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## avatar

- Unit: 4d1c334d294dfa97
- Package: 18235e0c9ec0e636
- Geometry source: CompositeUnit

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 0 | 102 | (unresolved) / 1278906229 | ok | identity | 27355 | -0.02868, 0.02340, 1.28921 | 2.47694 | - | bone_index0:vec4_uint8, bone_weight0:vec4_half, color0:rgba8, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |

No correspondence test was possible: this Unit lacks a second ordinary identity-matrix render mesh to compare against.

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 0 | (unresolved) | identity | 27355 | 109420 | 0 | 3.197e-07 | 2.217e-08 | 3.197e-07 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 0 | (unresolved) | single_index_attr_0 | vec4_half | 1 | 27355 | 0 | 0.000366 | 0.000046 | 3.184e-07 | 0 | True |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 0 | (unresolved) | 0 | 41165 | 40578 | 6 | 581 | 0.9401 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 0 | 0 | (unresolved) | identity | True | 88 | 0.0205, 0.1269, 1.1001 | -0.0287, 0.0234, 1.2892 | -0.0287, 0.0234, 1.2892 | 0.2211 | 0.2211 | identical |

### Scale and rotation composition gate

- Transforms: 107; non-uniform scale: 1; non-uniform scale with non-identity rotation (gated): 0

| Transform | Row norms | Scale ratio | Rotation | Status |
|---:|---|---:|---|---|
| 6 | 1.000000, 54.432270, 1.000000 | 54.432270 | identity | scale only |

## shoulder_90b57

- Unit: 90b57e547e7ff7bb
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 3 | 5 | g_shoulder_l_female_shadow / 1250810552 | ok | non_identity | 974 | -0.02780, -0.00223, 0.03695 | 0.51513 | 0.51513 | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 1 | 2 | 6 | g_shoulder_l_female_LOD2 / 2032678240 | ok | non_identity | 579 | -0.03099, -0.00133, 0.04199 | 0.51727 | 0.51727 | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 2 | 1 | 7 | g_shoulder_l_female_LOD1 / 1182727258 | ok | non_identity | 1142 | -0.02997, -0.00202, 0.03437 | 0.51399 | 0.51399 | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 0 | 8 | g_shoulder_l_female / 1997820557 | ok | non_identity | 1933 | -0.03875, -0.00081, 0.02628 | 0.51374 | 0.51374 | bone_index0:vec4_uint8, bone_weight0:float32, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | -1 | 17 | l_shoulder_culling / 3901334499 | no_skin_attributes | non_identity | 439 | 0.00908, -0.00779, -0.00359 | 1.49132 | 1.49132 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |

No correspondence test was possible: this Unit lacks a second ordinary identity-matrix render mesh to compare against.

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_shoulder_l_female_shadow | non_identity | 974 | 3896 | 0 | 1.036e-07 | 9.262e-08 | 1.436e+00 | True |
| 1 | 2 | g_shoulder_l_female_LOD2 | non_identity | 579 | 2316 | 0 | 1.038e-07 | 9.254e-08 | 1.436e+00 | True |
| 2 | 1 | g_shoulder_l_female_LOD1 | non_identity | 1142 | 4568 | 0 | 1.036e-07 | 9.242e-08 | 1.436e+00 | True |
| 3 | 0 | g_shoulder_l_female | non_identity | 1933 | 7732 | 0 | 2.980e-08 | 2.980e-08 | 1.436e+00 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_shoulder_l_female_shadow | single_index_attr_0 | float32 scalar | 1 | 974 | 0 | 0.000000 | 0.000000 | 1.036e-07 | 0 | True |
| 1 | 2 | g_shoulder_l_female_LOD2 | single_index_attr_0 | float32 scalar | 1 | 579 | 0 | 0.000000 | 0.000000 | 1.038e-07 | 0 | True |
| 2 | 1 | g_shoulder_l_female_LOD1 | single_index_attr_0 | float32 scalar | 1 | 1142 | 0 | 0.000000 | 0.000000 | 1.036e-07 | 0 | True |
| 3 | 0 | g_shoulder_l_female | single_index_attr_0 | float32 scalar | 1 | 1933 | 0 | 0.000000 | 0.000000 | 2.980e-08 | 0 | True |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_shoulder_l_female_shadow | 0 | 1200 | 1126 | 0 | 74 | 0.8613 | consistent |
| 1 | 2 | g_shoulder_l_female_LOD2 | 0 | 302 | 275 | 1 | 26 | 0.8299 | consistent |
| 1 | 2 | g_shoulder_l_female_LOD2 | 1 | 298 | 286 | 0 | 12 | 0.8797 | consistent |
| 2 | 1 | g_shoulder_l_female_LOD1 | 0 | 870 | 809 | 0 | 61 | 0.8536 | consistent |
| 2 | 1 | g_shoulder_l_female_LOD1 | 1 | 632 | 612 | 0 | 20 | 0.8882 | consistent |
| 3 | 0 | g_shoulder_l_female | 0 | 1186 | 1154 | 0 | 32 | 0.8948 | consistent |
| 3 | 0 | g_shoulder_l_female | 1 | 1818 | 1736 | 0 | 82 | 0.8751 | consistent |
| 4 | -1 | l_shoulder_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 0 | 3 | g_shoulder_l_female_shadow | non_identity | False | 1 | -0.3126, -0.0505, 1.4051 | -0.0278, -0.0022, 0.0369 | -0.3111, -0.0504, 1.4734 | 1.3983 | 0.0683 | placed |
| 1 | 2 | g_shoulder_l_female_LOD2 | non_identity | False | 1 | -0.3126, -0.0505, 1.4051 | -0.0310, -0.0013, 0.0420 | -0.3143, -0.0495, 1.4785 | 1.3928 | 0.0733 | placed |
| 2 | 1 | g_shoulder_l_female_LOD1 | non_identity | False | 1 | -0.3126, -0.0505, 1.4051 | -0.0300, -0.0020, 0.0344 | -0.3132, -0.0502, 1.4708 | 1.4004 | 0.0657 | placed |
| 3 | 0 | g_shoulder_l_female | non_identity | False | 7 | -0.0850, -0.0253, 1.2573 | -0.0387, -0.0008, 0.0263 | -0.3220, -0.0490, 1.4627 | 1.2321 | 0.3146 | placed |

### Scale and rotation composition gate

- Transforms: 19; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## unit_7e51

- Unit: 7e5111d05840be1a
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 3 | 5 | g_leg_undergarment_r_shadow / 824259603 | ok | identity | 317 | 0.14833, -0.03197, 0.43334 | 0.82044 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 1 | 2 | 6 | g_leg_undergarment_r_LOD2 / 1300361083 | ok | identity | 177 | 0.14783, -0.03607, 0.39496 | 0.82269 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 2 | 1 | 7 | g_leg_undergarment_r_LOD1 / 3058457341 | ok | identity | 388 | 0.14924, -0.03091, 0.43757 | 0.81941 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 0 | 8 | g_leg_undergarment_r / 792654830 | ok | identity | 728 | 0.16351, -0.03507, 0.34851 | 0.81905 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | -1 | 13 | hips_culling / 2249734937 | no_skin_attributes | non_identity | 439 | 0.01402, -0.00000, -0.00456 | 1.76465 | 1.76452 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |
| 5 | -1 | 16 | r_leg_culling / 3452158874 | no_skin_attributes | non_identity | 439 | 0.01323, 0.00000, -0.00430 | 1.66479 | 1.65462 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |

### Nearest-vertex distance to the reference render mesh

| Role | MeshInfo | LOD | Matrix | Reference | Sampled | Raw mean | Raw median | Raw p95 | Raw max | Mesh-world mean | Mesh-world median | Mesh-world p95 |
|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| control | 0 | 3 | identity | mesh 3 (LOD 0) | 317 | 0.000681 | 0.000475 | 0.002092 | 0.005460 | - | - | - |
| auxiliary | 4 | -1 | non_identity | mesh 3 (LOD 0) | 439 | 0.399557 | 0.394327 | 0.608603 | 0.651508 | 0.538500 | 0.566003 | 0.774727 |
| auxiliary | 5 | -1 | non_identity | mesh 3 (LOD 0) | 439 | 0.376505 | 0.372791 | 0.581197 | 0.623316 | 0.346411 | 0.372404 | 0.418733 |

### Auxiliary volume containment of the render union

union-outside is the largest distance the render meshes escape a candidate volume; box-slack is how much the candidate volume exceeds the render union. Zero union-outside means the volume fully wraps the render geometry.

| MeshInfo | LOD | Transform | Skinning | Render union min | Render union max | Union outside raw | Union outside mesh-world | Contains raw | Contains mesh-world | Box slack raw | Box slack mesh-world |
|---:|---:|---|---|---|---|---:|---:|---|---|---:|---:|
| 4 | -1 | hips_culling | no_skin_attributes | 0.04200, -0.16854, -0.02166 | 0.26461, 0.12990, 0.71291 | 0.20350 | 0.48577 | False | False | 0.55141 | 0.76992 |
| 5 | -1 | r_leg_culling | no_skin_attributes | 0.04200, -0.16854, -0.02166 | 0.26461, 0.12990, 0.71291 | 0.23232 | 0.06217 | False | False | 0.52258 | 0.37525 |

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_leg_undergarment_r_shadow | identity | 317 | 1268 | 0 | 5.669e-08 | 3.092e-08 | 5.669e-08 | True |
| 1 | 2 | g_leg_undergarment_r_LOD2 | identity | 177 | 708 | 0 | 5.678e-08 | 3.343e-08 | 5.678e-08 | True |
| 2 | 1 | g_leg_undergarment_r_LOD1 | identity | 388 | 1552 | 0 | 5.672e-08 | 3.417e-08 | 5.672e-08 | True |
| 3 | 0 | g_leg_undergarment_r | identity | 728 | 2912 | 0 | 8.350e-08 | 2.884e-08 | 8.350e-08 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_leg_undergarment_r_shadow | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 317 | 0 | 0.001955 | 0.000120 | 4.101e-08 | 0 | True |
| 1 | 2 | g_leg_undergarment_r_LOD2 | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 177 | 0 | 0.001955 | 0.000144 | 5.678e-08 | 0 | True |
| 2 | 1 | g_leg_undergarment_r_LOD1 | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 388 | 0 | 0.001955 | 0.000156 | 5.672e-08 | 0 | True |
| 3 | 0 | g_leg_undergarment_r | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 728 | 0 | 0.001955 | 0.000231 | 8.322e-08 | 0 | True |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_leg_undergarment_r_shadow | 0 | 509 | 475 | 0 | 34 | 0.8474 | consistent |
| 1 | 2 | g_leg_undergarment_r_LOD2 | 0 | 176 | 163 | 0 | 13 | 0.8212 | consistent |
| 1 | 2 | g_leg_undergarment_r_LOD2 | 1 | 77 | 77 | 0 | 0 | 0.9637 | consistent |
| 2 | 1 | g_leg_undergarment_r_LOD1 | 0 | 163 | 163 | 0 | 0 | 0.9808 | consistent |
| 2 | 1 | g_leg_undergarment_r_LOD1 | 1 | 474 | 441 | 0 | 33 | 0.8295 | consistent |
| 3 | 0 | g_leg_undergarment_r | 0 | 716 | 675 | 0 | 41 | 0.8428 | consistent |
| 3 | 0 | g_leg_undergarment_r | 1 | 560 | 560 | 0 | 0 | 0.9879 | consistent |
| 4 | -1 | hips_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |
| 5 | -1 | r_leg_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 0 | 3 | g_leg_undergarment_r_shadow | identity | True | 4 | 0.1817, -0.0024, 0.1586 | 0.1483, -0.0320, 0.4333 | 0.1483, -0.0320, 0.4333 | 0.2784 | 0.2784 | identical |
| 1 | 2 | g_leg_undergarment_r_LOD2 | identity | True | 4 | 0.1817, -0.0024, 0.1586 | 0.1478, -0.0361, 0.3950 | 0.1478, -0.0361, 0.3950 | 0.2412 | 0.2412 | identical |
| 2 | 1 | g_leg_undergarment_r_LOD1 | identity | True | 4 | 0.1817, -0.0024, 0.1586 | 0.1492, -0.0309, 0.4376 | 0.1492, -0.0309, 0.4376 | 0.2823 | 0.2823 | identical |
| 3 | 0 | g_leg_undergarment_r | identity | True | 7 | 0.1162, -0.0102, 0.5033 | 0.1635, -0.0351, 0.3485 | 0.1635, -0.0351, 0.3485 | 0.1637 | 0.1637 | identical |

### Scale and rotation composition gate

- Transforms: 20; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## unit_901f

- Unit: 901fc9c35857dad2
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | 3 | 5 | g_leg_undergarment_l_shadow / 3844486962 | ok | identity | 320 | -0.15578, -0.03444, 0.39927 | 0.80004 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 1 | 2 | 6 | g_leg_undergarment_l_LOD2 / 1466407635 | ok | identity | 179 | -0.15651, -0.03630, 0.35516 | 0.80287 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 2 | 1 | 7 | g_leg_undergarment_l_LOD1 / 3669701114 | ok | identity | 392 | -0.15421, -0.03384, 0.41564 | 0.79873 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 0 | 8 | g_leg_undergarment_l / 5149558 | ok | identity | 727 | -0.16468, -0.03509, 0.34401 | 0.79893 | - | bone_index0:vec4_uint8, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | -1 | 13 | hips_culling / 2249734937 | no_skin_attributes | non_identity | 439 | 0.01402, -0.00000, -0.00456 | 1.76465 | 1.76452 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |
| 5 | -1 | 16 | l_leg_culling / 3145176363 | no_skin_attributes | non_identity | 439 | 0.01323, -0.00000, -0.00430 | 1.66479 | 1.65462 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |

### Nearest-vertex distance to the reference render mesh

| Role | MeshInfo | LOD | Matrix | Reference | Sampled | Raw mean | Raw median | Raw p95 | Raw max | Mesh-world mean | Mesh-world median | Mesh-world p95 |
|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| control | 0 | 3 | identity | mesh 3 (LOD 0) | 320 | 0.000636 | 0.000426 | 0.002108 | 0.004154 | - | - | - |
| auxiliary | 4 | -1 | non_identity | mesh 3 (LOD 0) | 439 | 0.411186 | 0.411054 | 0.620168 | 0.651508 | 0.554766 | 0.582204 | 0.802221 |
| auxiliary | 5 | -1 | non_identity | mesh 3 (LOD 0) | 439 | 0.388142 | 0.389544 | 0.592026 | 0.623316 | 0.349060 | 0.373910 | 0.414348 |

### Auxiliary volume containment of the render union

union-outside is the largest distance the render meshes escape a candidate volume; box-slack is how much the candidate volume exceeds the render union. Zero union-outside means the volume fully wraps the render geometry.

| MeshInfo | LOD | Transform | Skinning | Render union min | Render union max | Union outside raw | Union outside mesh-world | Contains raw | Contains mesh-world | Box slack raw | Box slack mesh-world |
|---:|---:|---|---|---|---|---:|---:|---|---|---:|---:|
| 4 | -1 | hips_culling | no_skin_attributes | -0.26652, -0.16725, -0.02237 | -0.04739, 0.12776, 0.69174 | 0.18233 | 0.48649 | False | False | 0.55680 | 0.79109 |
| 5 | -1 | l_leg_culling | no_skin_attributes | -0.26652, -0.16725, -0.02237 | -0.04739, 0.12776, 0.69174 | 0.21115 | 0.06288 | False | False | 0.52797 | 0.38064 |

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_leg_undergarment_l_shadow | identity | 320 | 1280 | 0 | 9.389e-08 | 5.756e-08 | 9.389e-08 | True |
| 1 | 2 | g_leg_undergarment_l_LOD2 | identity | 179 | 716 | 0 | 9.397e-08 | 5.596e-08 | 9.397e-08 | True |
| 2 | 1 | g_leg_undergarment_l_LOD1 | identity | 392 | 1568 | 0 | 9.389e-08 | 5.454e-08 | 9.389e-08 | True |
| 3 | 0 | g_leg_undergarment_l | identity | 727 | 2908 | 0 | 9.273e-08 | 3.000e-08 | 9.273e-08 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_leg_undergarment_l_shadow | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 320 | 0 | 0.001955 | 0.000134 | 9.249e-08 | 0 | True |
| 1 | 2 | g_leg_undergarment_l_LOD2 | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 179 | 0 | 0.001955 | 0.000164 | 9.172e-08 | 0 | True |
| 2 | 1 | g_leg_undergarment_l_LOD1 | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 392 | 0 | 0.001955 | 0.000127 | 9.245e-08 | 0 | True |
| 3 | 0 | g_leg_undergarment_l | single_index_attr_0 | packed_10_10_10_2 decoded | 1 | 727 | 0 | 0.001955 | 0.000183 | 8.115e-08 | 0 | True |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | 3 | g_leg_undergarment_l_shadow | 0 | 509 | 483 | 0 | 26 | 0.8783 | consistent |
| 1 | 2 | g_leg_undergarment_l_LOD2 | 0 | 160 | 152 | 0 | 8 | 0.8426 | consistent |
| 1 | 2 | g_leg_undergarment_l_LOD2 | 1 | 94 | 94 | 0 | 0 | 0.9674 | consistent |
| 2 | 1 | g_leg_undergarment_l_LOD1 | 0 | 464 | 439 | 0 | 25 | 0.8474 | consistent |
| 2 | 1 | g_leg_undergarment_l_LOD1 | 1 | 173 | 173 | 0 | 0 | 0.9810 | consistent |
| 3 | 0 | g_leg_undergarment_l | 0 | 714 | 683 | 0 | 31 | 0.8561 | consistent |
| 3 | 0 | g_leg_undergarment_l | 1 | 562 | 562 | 0 | 0 | 0.9882 | consistent |
| 4 | -1 | hips_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |
| 5 | -1 | l_leg_culling | 0 | 760 | 760 | 0 | 0 | 0.9988 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 0 | 3 | g_leg_undergarment_l_shadow | identity | True | 4 | -0.1817, -0.0024, 0.1586 | -0.1558, -0.0344, 0.3993 | -0.1558, -0.0344, 0.3993 | 0.2442 | 0.2442 | identical |
| 1 | 2 | g_leg_undergarment_l_LOD2 | identity | True | 4 | -0.1817, -0.0024, 0.1586 | -0.1565, -0.0363, 0.3552 | -0.1565, -0.0363, 0.3552 | 0.2011 | 0.2011 | identical |
| 2 | 1 | g_leg_undergarment_l_LOD1 | identity | True | 4 | -0.1817, -0.0024, 0.1586 | -0.1542, -0.0338, 0.4156 | -0.1542, -0.0338, 0.4156 | 0.2604 | 0.2604 | identical |
| 3 | 0 | g_leg_undergarment_l | identity | True | 7 | -0.1162, -0.0102, 0.5033 | -0.1647, -0.0351, 0.3440 | -0.1647, -0.0351, 0.3440 | 0.1683 | 0.1683 | identical |

### Scale and rotation composition gate

- Transforms: 20; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## unit_be65

- Unit: be65eae060a0267c
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | -1 | 9 | torso_culling / 2787157324 | no_skin_attributes | non_identity | 24 | 0.00000, 0.00000, 0.00000 | 0.91424 | 0.95057 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |
| 1 | -1 | 12 | helmet_culling / 2148241058 | no_skin_attributes | non_identity | 24 | -0.00000, 0.00000, 0.00000 | 0.56878 | 0.57078 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half |
| 2 | 3 | 15 | g_torso_undergarment_female_shadow / 1705432636 | partial | identity | 1188 | -0.00002, -0.00296, 1.30512 | 0.95006 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 2 | 16 | g_torso_undergarment_female_LOD2 / 923885773 | partial | identity | 668 | -0.00116, 0.00036, 1.29730 | 0.94970 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | 1 | 17 | g_torso_undergarment_female_LOD1 / 760951203 | partial | identity | 1445 | -0.00128, -0.00408, 1.30624 | 0.94953 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 5 | 0 | 18 | g_torso_undergarment_female / 1146309845 | partial | identity | 2752 | 0.00109, -0.00092, 1.30180 | 0.95033 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |

No correspondence test was possible: this Unit lacks a second ordinary identity-matrix render mesh to compare against.

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 2 | 3 | g_torso_undergarment_female_shadow | identity | 1188 | 14202 | 54 | 7.624e-08 | 4.530e-08 | 7.624e-08 | True |
| 3 | 2 | g_torso_undergarment_female_LOD2 | identity | 668 | 7979 | 37 | 7.624e-08 | 6.482e-08 | 7.624e-08 | True |
| 4 | 1 | g_torso_undergarment_female_LOD1 | identity | 1445 | 17281 | 59 | 7.624e-08 | 4.616e-08 | 7.624e-08 | True |
| 5 | 0 | g_torso_undergarment_female | identity | 2752 | 32775 | 249 | 7.606e-08 | 2.070e-08 | 7.606e-08 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 2 | 3 | g_torso_undergarment_female_shadow | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 1188 | 0 | 0.001955 | 0.000059 | 7.550e-08 | 0 | True |
| 2 | 3 | g_torso_undergarment_female_shadow | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 1188 | 0 | 0.001955 | 0.000059 | 7.550e-08 | 0 | True |
| 2 | 3 | g_torso_undergarment_female_shadow | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 1160 | 54 | 0.824047 | 0.006634 | 7.624e-08 | 0 | False |
| 2 | 3 | g_torso_undergarment_female_shadow | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 1188 | 54 | 2.000000 | 1.969834 | 7.550e-08 | 0 | False |
| 3 | 2 | g_torso_undergarment_female_LOD2 | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 668 | 0 | 0.001955 | 0.000050 | 7.413e-08 | 0 | True |
| 3 | 2 | g_torso_undergarment_female_LOD2 | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 668 | 0 | 0.001955 | 0.000050 | 7.413e-08 | 0 | True |
| 3 | 2 | g_torso_undergarment_female_LOD2 | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 646 | 37 | 0.742913 | 0.009103 | 7.624e-08 | 0 | False |
| 3 | 2 | g_torso_undergarment_female_LOD2 | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 668 | 37 | 2.000000 | 1.958163 | 7.413e-08 | 0 | False |
| 4 | 1 | g_torso_undergarment_female_LOD1 | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 1445 | 0 | 0.001955 | 0.000061 | 7.551e-08 | 0 | True |
| 4 | 1 | g_torso_undergarment_female_LOD1 | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 1445 | 0 | 0.001955 | 0.000061 | 7.551e-08 | 0 | True |
| 4 | 1 | g_torso_undergarment_female_LOD1 | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 1418 | 59 | 0.742913 | 0.006671 | 7.624e-08 | 0 | False |
| 4 | 1 | g_torso_undergarment_female_LOD1 | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 1445 | 59 | 2.000000 | 1.974647 | 7.551e-08 | 0 | False |
| 5 | 0 | g_torso_undergarment_female | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 2752 | 0 | 0.001955 | 0.000199 | 7.169e-08 | 0 | True |
| 5 | 0 | g_torso_undergarment_female | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 2752 | 0 | 0.001955 | 0.000199 | 7.169e-08 | 0 | True |
| 5 | 0 | g_torso_undergarment_female | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 2712 | 249 | 0.980450 | 0.020604 | 4.441e-16 | 0 | False |
| 5 | 0 | g_torso_undergarment_female | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 2752 | 249 | 2.000000 | 1.964763 | 5.700e-08 | 0 | False |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | -1 | torso_culling | 0 | 12 | 12 | 0 | 0 | 1.0000 | consistent |
| 1 | -1 | helmet_culling | 0 | 12 | 12 | 0 | 0 | 1.0000 | consistent |
| 2 | 3 | g_torso_undergarment_female_shadow | 0 | 1918 | 1765 | 2 | 151 | 0.8341 | consistent |
| 3 | 2 | g_torso_undergarment_female_LOD2 | 0 | 100 | 100 | 0 | 0 | 0.9331 | consistent |
| 3 | 2 | g_torso_undergarment_female_LOD2 | 1 | 841 | 768 | 1 | 72 | 0.8094 | consistent |
| 4 | 1 | g_torso_undergarment_female_LOD1 | 0 | 210 | 210 | 0 | 0 | 0.9331 | consistent |
| 4 | 1 | g_torso_undergarment_female_LOD1 | 1 | 2162 | 2026 | 3 | 133 | 0.8528 | consistent |
| 5 | 0 | g_torso_undergarment_female | 0 | 3801 | 3580 | 10 | 211 | 0.8641 | consistent |
| 5 | 0 | g_torso_undergarment_female | 1 | 991 | 991 | 0 | 0 | 0.9516 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 2 | 3 | g_torso_undergarment_female_shadow | identity | True | 5 | -0.0000, -0.0211, 1.3443 | -0.0000, -0.0030, 1.3051 | -0.0000, -0.0030, 1.3051 | 0.0431 | 0.0431 | identical |
| 3 | 2 | g_torso_undergarment_female_LOD2 | identity | True | 5 | -0.0000, -0.0211, 1.3443 | -0.0012, 0.0004, 1.2973 | -0.0012, 0.0004, 1.2973 | 0.0516 | 0.0516 | identical |
| 4 | 1 | g_torso_undergarment_female_LOD1 | identity | True | 5 | -0.0000, -0.0211, 1.3443 | -0.0013, -0.0041, 1.3062 | -0.0013, -0.0041, 1.3062 | 0.0417 | 0.0417 | identical |
| 5 | 0 | g_torso_undergarment_female | identity | True | 6 | -0.0000, -0.0212, 1.2825 | 0.0011, -0.0009, 1.3018 | 0.0011, -0.0009, 1.3018 | 0.0280 | 0.0280 | identical |

### Scale and rotation composition gate

- Transforms: 19; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## unit_f271

- Unit: f2715c19b24ecd8b
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload

| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |
|---:|---:|---:|---|---|---|---:|---|---:|---:|---|
| 0 | -1 | 9 | torso_culling / 2787157324 | no_skin_attributes | non_identity | 24 | 0.00000, 0.00000, 0.00000 | 0.91424 | 0.95057 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half |
| 1 | -1 | 12 | helmet_culling / 2148241058 | no_skin_attributes | non_identity | 24 | -0.00000, 0.00000, 0.00000 | 0.56878 | 0.57078 | normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half |
| 2 | 3 | 15 | g_torso_undergarment_male_shadow / 1969322454 | partial | identity | 1238 | -0.00154, -0.00442, 1.31961 | 0.95365 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 3 | 2 | 16 | g_torso_undergarment_male_LOD2 / 1910610386 | partial | identity | 714 | -0.00219, -0.00314, 1.27711 | 0.94939 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 4 | 1 | 17 | g_torso_undergarment_male_LOD1 / 3961854394 | partial | identity | 1483 | 0.00075, -0.00393, 1.31062 | 0.95105 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |
| 5 | 0 | 18 | g_torso_undergarment_male / 531958952 | partial | identity | 2752 | 0.00047, -0.00432, 1.30037 | 0.95290 | - | bone_index0:vec4_uint8, bone_index1:vec4_uint8, bone_index2:vec4_uint32, bone_weight0:packed_10_10_10_2, normal0:packed_octahedral_normal, position0:vec3_float32, uv0:vec2_half, uv1:vec2_half, uv2:vec2_half |

No correspondence test was possible: this Unit lacks a second ordinary identity-matrix render mesh to compare against.

### Rest-pose vertex invariant

For every resolved influence lane, (p x palette) x bone_world must equal p x mesh_world. A non-zero error means the placement transform is applied twice or not at all in the stored representation.

| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |
|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| 2 | 3 | g_torso_undergarment_male_shadow | identity | 1238 | 14667 | 189 | 7.624e-08 | 3.148e-08 | 7.624e-08 | True |
| 3 | 2 | g_torso_undergarment_male_LOD2 | identity | 714 | 8527 | 41 | 7.624e-08 | 4.086e-08 | 7.624e-08 | True |
| 4 | 1 | g_torso_undergarment_male_LOD1 | identity | 1483 | 17624 | 172 | 7.624e-08 | 4.003e-08 | 7.624e-08 | True |
| 5 | 0 | g_torso_undergarment_male | identity | 2752 | 32780 | 244 | 7.606e-08 | 2.071e-08 | 7.606e-08 | True |

### Weight lane pairing

At rest every resolved lane maps the vertex onto the same placed point, so the
blend itself cannot separate pairings: it is normalized by the weight sum and
reported separately. The weight sum is what discriminates, because a pairing
that reuses one weight vector across several index attributes sums above one.

| MeshInfo | LOD | Transform | Pairing model | Weight format | Index attrs | Vertices | Unresolved lanes | Sum max dev | Sum mean dev | Normalized blend max err | Blend mismatch | Passes |
|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---|
| 2 | 3 | g_torso_undergarment_male_shadow | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 1238 | 0 | 0.001955 | 0.000298 | 7.624e-08 | 0 | True |
| 2 | 3 | g_torso_undergarment_male_shadow | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 1238 | 0 | 0.001955 | 0.000298 | 7.624e-08 | 0 | True |
| 2 | 3 | g_torso_undergarment_male_shadow | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 1190 | 189 | 0.433040 | 0.022562 | 6.705e-08 | 0 | False |
| 2 | 3 | g_torso_undergarment_male_shadow | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 1238 | 189 | 2.000000 | 1.938944 | 7.604e-08 | 0 | False |
| 3 | 2 | g_torso_undergarment_male_LOD2 | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 714 | 0 | 0.001955 | 0.000115 | 6.499e-08 | 0 | True |
| 3 | 2 | g_torso_undergarment_male_LOD2 | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 714 | 0 | 0.001955 | 0.000115 | 7.538e-08 | 0 | True |
| 3 | 2 | g_torso_undergarment_male_LOD2 | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 697 | 41 | 0.405670 | 0.005593 | 6.648e-08 | 0 | False |
| 3 | 2 | g_torso_undergarment_male_LOD2 | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 714 | 41 | 2.000000 | 1.970501 | 6.658e-08 | 0 | False |
| 4 | 1 | g_torso_undergarment_male_LOD1 | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 1483 | 0 | 0.001955 | 0.000263 | 7.605e-08 | 0 | True |
| 4 | 1 | g_torso_undergarment_male_LOD1 | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 1483 | 0 | 0.001955 | 0.000263 | 7.605e-08 | 0 | True |
| 4 | 1 | g_torso_undergarment_male_LOD1 | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 1451 | 172 | 0.433040 | 0.018988 | 6.705e-08 | 0 | False |
| 4 | 1 | g_torso_undergarment_male_LOD1 | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 1483 | 172 | 2.000000 | 1.959317 | 7.605e-08 | 0 | False |
| 5 | 0 | g_torso_undergarment_male | single_index_attr_0 | packed_10_10_10_2 decoded | 3 | 2752 | 0 | 0.001955 | 0.000204 | 7.169e-08 | 0 | True |
| 5 | 0 | g_torso_undergarment_male | single_index_attr_1 | packed_10_10_10_2 decoded | 3 | 2752 | 0 | 0.001955 | 0.000204 | 7.169e-08 | 0 | True |
| 5 | 0 | g_torso_undergarment_male | single_index_attr_2 | packed_10_10_10_2 decoded | 3 | 2713 | 244 | 0.998045 | 0.018672 | 2.220e-16 | 0 | False |
| 5 | 0 | g_torso_undergarment_male | every_index_attr_shares_weights | packed_10_10_10_2 decoded | 3 | 2752 | 244 | 2.000000 | 1.967013 | 5.754e-08 | 0 | False |

### Triangle winding against stored normals

The face normal uses the right-handed cross product of the stored vertex
order. Agreement with the stored normals shows the two conventions inside the
file do not contradict each other; it does not fix absolute handedness.

| MeshInfo | LOD | Transform | Section | Faces checked | Agree | Oppose | Degenerate | Mean dot | Conclusion |
|---:|---:|---|---:|---:|---:|---:|---:|---:|---|
| 0 | -1 | torso_culling | 0 | 12 | 12 | 0 | 0 | 1.0000 | consistent |
| 1 | -1 | helmet_culling | 0 | 12 | 12 | 0 | 0 | 1.0000 | consistent |
| 2 | 3 | g_torso_undergarment_male_shadow | 0 | 1895 | 1781 | 1 | 113 | 0.8685 | consistent |
| 3 | 2 | g_torso_undergarment_male_LOD2 | 0 | 218 | 218 | 0 | 0 | 0.9668 | consistent |
| 3 | 2 | g_torso_undergarment_male_LOD2 | 1 | 745 | 676 | 0 | 69 | 0.8099 | consistent |
| 4 | 1 | g_torso_undergarment_male_LOD1 | 0 | 687 | 686 | 0 | 1 | 0.9678 | consistent |
| 4 | 1 | g_torso_undergarment_male_LOD1 | 1 | 1668 | 1546 | 2 | 120 | 0.8444 | consistent |
| 5 | 0 | g_torso_undergarment_male | 0 | 3801 | 3588 | 10 | 203 | 0.8647 | consistent |
| 5 | 0 | g_torso_undergarment_male | 1 | 991 | 991 | 0 | 0 | 0.9616 | consistent |

### Distance from stored positions to the bones they skin to

For each mesh, compare the centroid distance to its palette bones before and
after applying the mesh's own TransformInfo matrix. The test only decides when
that matrix is not the identity, which is marked per row.

| MeshInfo | LOD | Transform | Matrix | Degenerate | Bones | Landmark centroid | Raw centroid | Placed centroid | Raw distance | Placed distance | Closer |
|---:|---:|---|---|---|---:|---|---|---|---:|---:|---|
| 2 | 3 | g_torso_undergarment_male_shadow | identity | True | 5 | -0.0000, -0.0211, 1.3443 | -0.0015, -0.0044, 1.3196 | -0.0015, -0.0044, 1.3196 | 0.0298 | 0.0298 | identical |
| 3 | 2 | g_torso_undergarment_male_LOD2 | identity | True | 5 | -0.0000, -0.0211, 1.3443 | -0.0022, -0.0031, 1.2771 | -0.0022, -0.0031, 1.2771 | 0.0695 | 0.0695 | identical |
| 4 | 1 | g_torso_undergarment_male_LOD1 | identity | True | 5 | -0.0000, -0.0211, 1.3443 | 0.0007, -0.0039, 1.3106 | 0.0007, -0.0039, 1.3106 | 0.0378 | 0.0378 | identical |
| 5 | 0 | g_torso_undergarment_male | identity | True | 6 | -0.0000, -0.0212, 1.2825 | 0.0005, -0.0043, 1.3004 | 0.0005, -0.0043, 1.3004 | 0.0246 | 0.0246 | identical |

### Scale and rotation composition gate

- Transforms: 19; non-uniform scale: 0; non-uniform scale with non-identity rotation (gated): 0

No transform in this Unit combines non-uniform scale with rotation, so the gated composition case stays untested here.

## Cross-Unit shared vertex buffers

| Left | Right | Vertices | Bitwise identical buffers | Differing buffers | Position max abs diff | Within tolerance |
|---|---|---:|---|---|---:|---|
| fs05_lower_body mesh 4 (hips_culling / LOD -1 / non_identity) | fs05_torso mesh 0 (l_arm_culling / LOD 4 / non_identity) | 439 | normal0, uv0 | position0 | 2.441e-17 | True |
| fs05_torso mesh 0 (l_arm_culling / LOD 4 / non_identity) | unit_7e51 mesh 4 (hips_culling / LOD -1 / non_identity) | 439 | normal0, uv0 | position0 | 2.441e-17 | True |
| fs05_torso mesh 0 (l_arm_culling / LOD 4 / non_identity) | unit_901f mesh 4 (hips_culling / LOD -1 / non_identity) | 439 | normal0, uv0 | position0 | 2.441e-17 | True |
| unit_be65 mesh 5 (g_torso_undergarment_female / LOD 0 / identity) | unit_f271 mesh 5 (g_torso_undergarment_male / LOD 0 / identity) | 2752 | uv0 | bone_index0, bone_index1, bone_index2, bone_weight0, normal0, position0, uv1, uv2 | 2.830e-02 | False |

## Interpretation limits

- Nearest-vertex distance measures which space a stored position already lives in; it does not prove the runtime shader order.
- The candidate mesh-world matrix comes from the parser's own hierarchy reconstruction, which is validated for internal consistency only.
- Vertices are read, never modified; no weight transfer or conversion is performed or implied by this probe.
- A control pair with two identity mesh-world matrices must show a near-zero raw distance before any auxiliary result is read.
- A rest-pose blend cannot identify which bone-index attribute a weight vector belongs to, because every resolved lane places the vertex identically at rest; only a posed transform separates those pairings.
- Weight sums are compared to one with a tolerance that covers half and ten-bit quantization, so a reported deviation is a storage precision limit, not a conversion error.
- A transform with non-uniform scale combined with non-identity rotation is reported as gated: the parser refuses to guess that composition, so agreement elsewhere does not license converting one.
- Buffer identity is decided by hashing decoded attribute values; a float32 rounding difference such as negative zero is reported as a difference, not silently ignored.
