# HD2 Mesh Binding Classification

- Units: 8
- Direct bindings: 40

## Class tally

| Class | Bindings |
|---|---:|
| auxiliary_unskinned | 10 |
| skinned_non_identity_placement | 5 |
| skinned_render_lod | 25 |

## Class definitions

| Class | Requires |
|---|---|
| skinned_render_lod | decoded geometry, skin attributes, resolved lanes above zero, identity mesh matrix, matching hierarchy |
| skinned_non_identity_placement | resolved lanes above zero and a non-identity mesh matrix; placement is applied through that matrix |
| auxiliary_unskinned | no skin attributes and LOD -1 |
| unskinned_at_non_negative_lod | no skin attributes at LOD 0 or above; not seen in these samples |
| skinned_hierarchy_unverified | skinned mesh whose hierarchy validation is not matching |
| undecidable_placement | mesh's own TransformInfo matrix present but unparseable, so identity cannot be decided |
| skinned_without_lanes | skin attributes present but no resolved lanes; not seen in these samples |
| undecoded_geometry | no decoded geometry row for the binding |
| unknown | fallback whenever no rule fires; never replaced by a name-based guess |

## Per-binding evidence

### fs05_lower_body (Unit 210fb2f60f8308ea)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 2607622032 | 3 | ok | 608 | 608 | identity | matches | skinned_render_lod | g_legs_hips_male_shadow |
| 1 | 524905431 | 2 | ok | 339 | 339 | identity | matches | skinned_render_lod | g_legs_hips_male_LOD2 |
| 2 | 3301214708 | 1 | ok | 738 | 738 | identity | matches | skinned_render_lod | g_legs_hips_male_LOD1 |
| 3 | 562172190 | 0 | ok | 1374 | 1374 | identity | matches | skinned_render_lod | g_legs_hips_male |
| 4 | 2249734937 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | hips_culling |

### fs05_torso_left_arm (Unit 324fb206612ce4f7)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 3648454405 | 4 | ok | 439 | 2616 | non_identity | matches | skinned_non_identity_placement | l_arm_culling |
| 1 | 522128404 | 3 | ok | 1426 | 7779 | identity | matches | skinned_render_lod | g_torso_arm_l_female_shadow |
| 2 | 3766176514 | 2 | ok | 838 | 4059 | identity | matches | skinned_render_lod | g_torso_arm_l_female_LOD2 |
| 3 | 2627263307 | 1 | ok | 1707 | 9696 | identity | matches | skinned_render_lod | g_torso_arm_l_female_LOD1 |
| 4 | 46827504 | 0 | ok | 3115 | 18888 | identity | matches | skinned_render_lod | g_torso_arm_l_female |

### avatar_composite (Unit 4d1c334d294dfa97)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 1278906229 | 0 | ok | 27355 | 42158 | identity | matches | skinned_render_lod | (unresolved) |

### fs05_shoulder_armor (Unit 90b57e547e7ff7bb)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 1250810552 | 3 | ok | 974 | 974 | non_identity | matches | skinned_non_identity_placement | g_shoulder_l_female_shadow |
| 1 | 2032678240 | 2 | ok | 579 | 579 | non_identity | matches | skinned_non_identity_placement | g_shoulder_l_female_LOD2 |
| 2 | 1182727258 | 1 | ok | 1142 | 1142 | non_identity | matches | skinned_non_identity_placement | g_shoulder_l_female_LOD1 |
| 3 | 1997820557 | 0 | ok | 1933 | 1933 | non_identity | matches | skinned_non_identity_placement | g_shoulder_l_female |
| 4 | 3901334499 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | l_shoulder_culling |

### fs05_undergarment_torso_f (Unit be65eae060a0267c)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 2787157324 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | torso_culling |
| 1 | 2148241058 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | helmet_culling |
| 2 | 1705432636 | 3 | partial | 1188 | 3714 | identity | matches | skinned_render_lod | g_torso_undergarment_female_shadow |
| 3 | 923885773 | 2 | partial | 668 | 2069 | identity | matches | skinned_render_lod | g_torso_undergarment_female_LOD2 |
| 4 | 760951203 | 1 | partial | 1445 | 4528 | identity | matches | skinned_render_lod | g_torso_undergarment_female_LOD1 |
| 5 | 1146309845 | 0 | partial | 2752 | 9789 | identity | matches | skinned_render_lod | g_torso_undergarment_female |

### fs05_undergarment_torso_m (Unit f2715c19b24ecd8b)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 2787157324 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | torso_culling |
| 1 | 2148241058 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | helmet_culling |
| 2 | 1969322454 | 3 | partial | 1238 | 4710 | identity | matches | skinned_render_lod | g_torso_undergarment_male_shadow |
| 3 | 1910610386 | 2 | partial | 714 | 2365 | identity | matches | skinned_render_lod | g_torso_undergarment_male_LOD2 |
| 4 | 3961854394 | 1 | partial | 1483 | 5522 | identity | matches | skinned_render_lod | g_torso_undergarment_male_LOD1 |
| 5 | 531958952 | 0 | partial | 2752 | 9788 | identity | matches | skinned_render_lod | g_torso_undergarment_male |

### fs05_undergarment_leg_r (Unit 7e5111d05840be1a)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 824259603 | 3 | ok | 317 | 361 | identity | matches | skinned_render_lod | g_leg_undergarment_r_shadow |
| 1 | 1300361083 | 2 | ok | 177 | 208 | identity | matches | skinned_render_lod | g_leg_undergarment_r_LOD2 |
| 2 | 3058457341 | 1 | ok | 388 | 460 | identity | matches | skinned_render_lod | g_leg_undergarment_r_LOD1 |
| 3 | 792654830 | 0 | ok | 728 | 919 | identity | matches | skinned_render_lod | g_leg_undergarment_r |
| 4 | 2249734937 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | hips_culling |
| 5 | 3452158874 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | r_leg_culling |

### fs05_undergarment_leg_l (Unit 901fc9c35857dad2)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|
| 0 | 3844486962 | 3 | ok | 320 | 368 | identity | matches | skinned_render_lod | g_leg_undergarment_l_shadow |
| 1 | 1466407635 | 2 | ok | 179 | 215 | identity | matches | skinned_render_lod | g_leg_undergarment_l_LOD2 |
| 2 | 3669701114 | 1 | ok | 392 | 449 | identity | matches | skinned_render_lod | g_leg_undergarment_l_LOD1 |
| 3 | 5149558 | 0 | ok | 727 | 870 | identity | matches | skinned_render_lod | g_leg_undergarment_l |
| 4 | 2249734937 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | hips_culling |
| 5 | 3145176363 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | l_leg_culling |

## Interpretation limits

- Bone, mesh and node names are recorded for reference only and are never used to decide a class.
- Classes describe stored structure, not runtime use; only native or SDK-side observation can confirm a runtime role.
- The hierarchy and matrix states come from the parser's candidate conventions, which are internal-consistency results.
- A binding is reported undecoded_geometry when the Unit JSON has no geometry row; that is a decode scope fact, not a property of the mesh.
