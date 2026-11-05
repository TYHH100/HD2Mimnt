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
## Section material reference census

Batch-scoped: a reference is a shared family only when at least two Units in this
run use it. Names are listed for verification only and never decide a profile.

| Reference | Units | Bindings | Profile | LOD indices | Skinning states | Classes | Names (verification only) |
|---:|---:|---:|---|---|---|---|---|
| 155175220 | 7 | 11 | no_base_lod_negative_lod | -1, 4 | no_skin_attributes, ok | auxiliary_unskinned, skinned_non_identity_placement | helmet_culling, hips_culling, l_arm_culling |
| 1772995352 | 7 | 7 | no_base_lod_upper_lod | 3 | ok, partial | skinned_non_identity_placement, skinned_render_lod | g_leg_undergarment_l_shadow, g_leg_undergarment_r_shadow, g_legs_hips_male_shadow |
| 1913034217 | 3 | 9 | base_lod_reference | 0, 1, 2 | ok, partial | skinned_render_lod | g_torso_arm_l_female, g_torso_arm_l_female_LOD1, g_torso_arm_l_female_LOD2 |
| 3910610061 | 3 | 9 | base_lod_reference | 0, 1, 2 | ok, partial | skinned_render_lod | g_torso_arm_l_female, g_torso_arm_l_female_LOD1, g_torso_arm_l_female_LOD2 |
| 793921433 | 2 | 6 | base_lod_reference | 0, 1, 2 | ok | skinned_render_lod | g_leg_undergarment_l, g_leg_undergarment_l_LOD1, g_leg_undergarment_l_LOD2 |
| 2013274869 | 2 | 6 | base_lod_reference | 0, 1, 2 | ok | skinned_render_lod | g_leg_undergarment_l, g_leg_undergarment_l_LOD1, g_leg_undergarment_l_LOD2 |
| 1256504776 | 1 | 3 | base_lod_reference | 0, 1, 2 | ok | skinned_render_lod | g_legs_hips_male, g_legs_hips_male_LOD1, g_legs_hips_male_LOD2 |
| 1242939700 | 1 | 3 | base_lod_reference | 0, 1, 2 | ok | skinned_non_identity_placement | g_shoulder_l_female, g_shoulder_l_female_LOD1, g_shoulder_l_female_LOD2 |
| 3140659873 | 1 | 3 | base_lod_reference | 0, 1, 2 | ok | skinned_non_identity_placement | g_shoulder_l_female, g_shoulder_l_female_LOD1, g_shoulder_l_female_LOD2 |
| 2780682112 | 1 | 1 | base_lod_reference | 0 | ok | skinned_render_lod | (unresolved) |

## Reference profile by structure class

| Profile | Bindings | Classes |
|---|---:|---|
| base_lod_bound | 22 | skinned_non_identity_placement=3, skinned_render_lod=19 |
| family_no_base_lod_negative_lod | 11 | auxiliary_unskinned=10, skinned_non_identity_placement=1 |
| family_no_base_lod_upper_lod | 7 | skinned_non_identity_placement=1, skinned_render_lod=6 |

## Per-binding evidence

### fs05_lower_body (Unit 210fb2f60f8308ea)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 2607622032 | 3 | ok | 608 | 608 | identity | matches | skinned_render_lod | 1772995352 | family_no_base_lod_upper_lod | g_legs_hips_male_shadow |
| 1 | 524905431 | 2 | ok | 339 | 339 | identity | matches | skinned_render_lod | 1256504776 | base_lod_bound | g_legs_hips_male_LOD2 |
| 2 | 3301214708 | 1 | ok | 738 | 738 | identity | matches | skinned_render_lod | 1256504776 | base_lod_bound | g_legs_hips_male_LOD1 |
| 3 | 562172190 | 0 | ok | 1374 | 1374 | identity | matches | skinned_render_lod | 1256504776 | base_lod_bound | g_legs_hips_male |
| 4 | 2249734937 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | hips_culling |

### fs05_torso_left_arm (Unit 324fb206612ce4f7)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 3648454405 | 4 | ok | 439 | 2616 | non_identity | matches | skinned_non_identity_placement | 155175220 | family_no_base_lod_negative_lod | l_arm_culling |
| 1 | 522128404 | 3 | ok | 1426 | 7779 | identity | matches | skinned_render_lod | 1772995352 | family_no_base_lod_upper_lod | g_torso_arm_l_female_shadow |
| 2 | 3766176514 | 2 | ok | 838 | 4059 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_arm_l_female_LOD2 |
| 3 | 2627263307 | 1 | ok | 1707 | 9696 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_arm_l_female_LOD1 |
| 4 | 46827504 | 0 | ok | 3115 | 18888 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_arm_l_female |

### avatar_composite (Unit 4d1c334d294dfa97)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 1278906229 | 0 | ok | 27355 | 42158 | identity | matches | skinned_render_lod | 2780682112 | base_lod_bound | (unresolved) |

### fs05_shoulder_armor (Unit 90b57e547e7ff7bb)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 1250810552 | 3 | ok | 974 | 974 | non_identity | matches | skinned_non_identity_placement | 1772995352 | family_no_base_lod_upper_lod | g_shoulder_l_female_shadow |
| 1 | 2032678240 | 2 | ok | 579 | 579 | non_identity | matches | skinned_non_identity_placement | 1242939700, 3140659873 | base_lod_bound | g_shoulder_l_female_LOD2 |
| 2 | 1182727258 | 1 | ok | 1142 | 1142 | non_identity | matches | skinned_non_identity_placement | 1242939700, 3140659873 | base_lod_bound | g_shoulder_l_female_LOD1 |
| 3 | 1997820557 | 0 | ok | 1933 | 1933 | non_identity | matches | skinned_non_identity_placement | 1242939700, 3140659873 | base_lod_bound | g_shoulder_l_female |
| 4 | 3901334499 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | l_shoulder_culling |

### fs05_undergarment_torso_f (Unit be65eae060a0267c)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 2787157324 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | torso_culling |
| 1 | 2148241058 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | helmet_culling |
| 2 | 1705432636 | 3 | partial | 1188 | 3714 | identity | matches | skinned_render_lod | 1772995352 | family_no_base_lod_upper_lod | g_torso_undergarment_female_shadow |
| 3 | 923885773 | 2 | partial | 668 | 2069 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_undergarment_female_LOD2 |
| 4 | 760951203 | 1 | partial | 1445 | 4528 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_undergarment_female_LOD1 |
| 5 | 1146309845 | 0 | partial | 2752 | 9789 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_undergarment_female |

### fs05_undergarment_torso_m (Unit f2715c19b24ecd8b)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 2787157324 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | torso_culling |
| 1 | 2148241058 | -1 | no_skin_attributes | 24 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | helmet_culling |
| 2 | 1969322454 | 3 | partial | 1238 | 4710 | identity | matches | skinned_render_lod | 1772995352 | family_no_base_lod_upper_lod | g_torso_undergarment_male_shadow |
| 3 | 1910610386 | 2 | partial | 714 | 2365 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_undergarment_male_LOD2 |
| 4 | 3961854394 | 1 | partial | 1483 | 5522 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_undergarment_male_LOD1 |
| 5 | 531958952 | 0 | partial | 2752 | 9788 | identity | matches | skinned_render_lod | 1913034217, 3910610061 | base_lod_bound | g_torso_undergarment_male |

### fs05_undergarment_leg_r (Unit 7e5111d05840be1a)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 824259603 | 3 | ok | 317 | 361 | identity | matches | skinned_render_lod | 1772995352 | family_no_base_lod_upper_lod | g_leg_undergarment_r_shadow |
| 1 | 1300361083 | 2 | ok | 177 | 208 | identity | matches | skinned_render_lod | 793921433, 2013274869 | base_lod_bound | g_leg_undergarment_r_LOD2 |
| 2 | 3058457341 | 1 | ok | 388 | 460 | identity | matches | skinned_render_lod | 793921433, 2013274869 | base_lod_bound | g_leg_undergarment_r_LOD1 |
| 3 | 792654830 | 0 | ok | 728 | 919 | identity | matches | skinned_render_lod | 793921433, 2013274869 | base_lod_bound | g_leg_undergarment_r |
| 4 | 2249734937 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | hips_culling |
| 5 | 3452158874 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | r_leg_culling |

### fs05_undergarment_leg_l (Unit 901fc9c35857dad2)

| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | References | Profile | Name (reference only) |
|---:|---:|---:|---|---:|---:|---|---|---|---|---|---|
| 0 | 3844486962 | 3 | ok | 320 | 368 | identity | matches | skinned_render_lod | 1772995352 | family_no_base_lod_upper_lod | g_leg_undergarment_l_shadow |
| 1 | 1466407635 | 2 | ok | 179 | 215 | identity | matches | skinned_render_lod | 793921433, 2013274869 | base_lod_bound | g_leg_undergarment_l_LOD2 |
| 2 | 3669701114 | 1 | ok | 392 | 449 | identity | matches | skinned_render_lod | 793921433, 2013274869 | base_lod_bound | g_leg_undergarment_l_LOD1 |
| 3 | 5149558 | 0 | ok | 727 | 870 | identity | matches | skinned_render_lod | 793921433, 2013274869 | base_lod_bound | g_leg_undergarment_l |
| 4 | 2249734937 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | hips_culling |
| 5 | 3145176363 | -1 | no_skin_attributes | 439 | None | non_identity | matches | auxiliary_unskinned | 155175220 | family_no_base_lod_negative_lod | l_leg_culling |

## Interpretation limits

- Bone, mesh and node names are recorded for reference only and are never used to decide a class.
- Classes describe stored structure, not runtime use; only native or SDK-side observation can confirm a runtime role.
- The hierarchy and matrix states come from the parser's candidate conventions, which are internal-consistency results.
- A binding is reported undecoded_geometry when the Unit JSON has no geometry row; that is a decode scope fact, not a property of the mesh.
- The separating property is whether the reference also appears on a LOD 0 binding, not whether it is reused across Units: male and female twins of one garment share a garment reference, while shadow and culling references never touch LOD 0.
- Batch-scoped: a single-Unit run can only see that Unit's own LOD values, so profiles need the widest sample available to be meaningful.
- A profile marks a family only from reference roles; that shows structure, not runtime purpose, and only native or SDK-side observation can name the role.
- mixed_references marks a binding that uses both kinds of reference; it is left unsplit rather than assigned to whichever reference is listed first.
