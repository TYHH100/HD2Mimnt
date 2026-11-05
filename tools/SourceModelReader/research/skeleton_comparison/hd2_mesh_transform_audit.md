# HD2 Mesh Transform Binding Audit

- Tolerance: 0.0001

This report preserves direct MeshInfo bindings, geometry/LOD metadata, skinning status, hierarchy validation and the full stored 4x4 matrix.

## fs05_lower_body

- Unit: 210fb2f60f8308ea
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload
- Meshes: 5
- Stored matrices: 4 identity / 1 non-identity
- Skinned meshes: 4; no-skin-attribute meshes: 1
- Bone matrix validation: 4 matches / 0 mismatches

| MeshInfo | Mesh ID | Transform | Name / hash | LOD | Vertices | Triangles | Skinning | Active lanes | Bone matrix check | Hierarchy check | Stored matrix |
|---:|---:|---:|---|---:|---:|---:|---|---:|---|---|---|
| 0 | 2607622032 | 5 | g_legs_hips_male_shadow / 2607622032 | 3 | 608 | 976 | ok | 608 | matches (1.1821749723583963e-07) | matches (0.0) | identity |
| 1 | 524905431 | 6 | g_legs_hips_male_LOD2 / 524905431 | 2 | 339 | 488 | ok | 339 | matches (1.1821749723583963e-07) | matches (0.0) | identity |
| 2 | 3301214708 | 7 | g_legs_hips_male_LOD1 / 3301214708 | 1 | 738 | 1222 | ok | 738 | matches (1.1821749723583963e-07) | matches (0.0) | identity |
| 3 | 562172190 | 8 | g_legs_hips_male / 562172190 | 0 | 1374 | 2444 | ok | 1374 | matches (1.1821749723583963e-07) | matches (0.0) | identity |
| 4 | 2249734937 | 13 | hips_culling / 2249734937 | -1 | 439 | 760 | no_skin_attributes | None | not_applicable (None) | matches (2.7755575615628914e-17) | non_identity |

### MeshInfo 0 / Mesh 2607622032

- Transform: 5 — g_legs_hips_male_shadow
- LOD: 3; vertices: 608; triangles: 976
- Skinning: ok; active lanes: 608; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 2/0/0; max error: 1.1821749723583963e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[5] g_legs_hips_male_shadow (hash=2607622032, parent=4)
  [4] grp_legs_hips_male (hash=1964365244, parent=3)
    [3] game_mesh (hash=828166497, parent=2)
      [2] (unresolved) (hash=45638919, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 1 / Mesh 524905431

- Transform: 6 — g_legs_hips_male_LOD2
- LOD: 2; vertices: 339; triangles: 488
- Skinning: ok; active lanes: 339; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 2/0/0; max error: 1.1821749723583963e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[6] g_legs_hips_male_LOD2 (hash=524905431, parent=4)
  [4] grp_legs_hips_male (hash=1964365244, parent=3)
    [3] game_mesh (hash=828166497, parent=2)
      [2] (unresolved) (hash=45638919, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 2 / Mesh 3301214708

- Transform: 7 — g_legs_hips_male_LOD1
- LOD: 1; vertices: 738; triangles: 1222
- Skinning: ok; active lanes: 738; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 2/0/0; max error: 1.1821749723583963e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[7] g_legs_hips_male_LOD1 (hash=3301214708, parent=4)
  [4] grp_legs_hips_male (hash=1964365244, parent=3)
    [3] game_mesh (hash=828166497, parent=2)
      [2] (unresolved) (hash=45638919, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 3 / Mesh 562172190

- Transform: 8 — g_legs_hips_male
- LOD: 0; vertices: 1374; triangles: 2444
- Skinning: ok; active lanes: 1374; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 2/0/0; max error: 1.1821749723583963e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[8] g_legs_hips_male (hash=562172190, parent=4)
  [4] grp_legs_hips_male (hash=1964365244, parent=3)
    [3] game_mesh (hash=828166497, parent=2)
      [2] (unresolved) (hash=45638919, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 4 / Mesh 2249734937

- Transform: 13 — hips_culling
- LOD: -1; vertices: 439; triangles: 760
- Skinning: no_skin_attributes; active lanes: None; unresolved lanes: None
- Bone matrix validation: not_applicable; matched/mismatched/unavailable slots: None/None/None; max error: None
- Hierarchy validation: matches; max error: 2.7755575615628914e-17
- Stored matrix identity classification: non_identity; identity error: 1.0
- Parent chain status: root_reached

~~~text
[13] hips_culling (hash=2249734937, parent=12)
  [12] hips (hash=1720674920, parent=11)
    [11] boss (hash=2601604451, parent=10)
      [10] root (hash=3167719909, parent=9)
        [9] skeleton (hash=2539449277, parent=2)
          [2] (unresolved) (hash=45638919, parent=1)
            [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
              [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
4.2885736e-16, 0.171287864, 0.985221028, 0
-3.70024589e-17, 0.985221028, -0.171287864, 0
-1, 3.70024589e-17, 4.2885736e-16, 0
-2.27840977e-17, -0.0214501955, 0.973472714, 1
~~~

## fs05_torso_left_arm

- Unit: 324fb206612ce4f7
- Package: 8670598c1f4462dc
- Geometry source: Unit GPU payload
- Meshes: 5
- Stored matrices: 4 identity / 1 non-identity
- Skinned meshes: 5; no-skin-attribute meshes: 0
- Bone matrix validation: 5 matches / 0 mismatches

| MeshInfo | Mesh ID | Transform | Name / hash | LOD | Vertices | Triangles | Skinning | Active lanes | Bone matrix check | Hierarchy check | Stored matrix |
|---:|---:|---:|---|---:|---:|---:|---|---:|---|---|---|
| 0 | 3648454405 | 14 | l_arm_culling / 3648454405 | 4 | 439 | 760 | ok | 2616 | matches (9.369958597904571e-08) | matches (9.369958831051406e-08) | non_identity |
| 1 | 522128404 | 34 | g_torso_arm_l_female_shadow / 522128404 | 3 | 1426 | 2089 | ok | 7779 | matches (1.7369378912945876e-07) | matches (0.0) | identity |
| 2 | 3766176514 | 35 | g_torso_arm_l_female_LOD2 / 3766176514 | 2 | 838 | 1044 | ok | 4059 | matches (1.7369378912945876e-07) | matches (0.0) | identity |
| 3 | 2627263307 | 36 | g_torso_arm_l_female_LOD1 / 2627263307 | 1 | 1707 | 2612 | ok | 9696 | matches (1.7369378912945876e-07) | matches (0.0) | identity |
| 4 | 46827504 | 37 | g_torso_arm_l_female / 46827504 | 0 | 3115 | 5227 | ok | 18888 | matches (1.7369378912945876e-07) | matches (0.0) | identity |

### MeshInfo 0 / Mesh 3648454405

- Transform: 14 — l_arm_culling
- LOD: 4; vertices: 439; triangles: 760
- Skinning: ok; active lanes: 2616; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 5/0/0; max error: 9.369958597904571e-08
- Hierarchy validation: matches; max error: 9.369958831051406e-08
- Stored matrix identity classification: non_identity; identity error: 1.7371262907981873
- Parent chain status: root_reached

~~~text
[14] l_arm_culling (hash=3648454405, parent=13)
  [13] l_elbow (hash=480431462, parent=11)
    [11] l_shoulder (hash=58010776, parent=10)
      [10] l_clavicle (hash=3296230222, parent=8)
        [8] chest (hash=938412064, parent=7)
          [7] spine2 (hash=1514912418, parent=6)
            [6] spine1 (hash=1490585339, parent=5)
              [5] boss (hash=2601604451, parent=4)
                [4] root (hash=3167719909, parent=3)
                  [3] skeleton (hash=2539449277, parent=2)
                    [2] mech_pilot_light (hash=4079346489, parent=1)
                      [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
                        [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
-0.641299486, 0.675258398, -0.364363641, 0
-0.569404542, -0.737126291, -0.363899916, 0
-0.514308631, -0.0258984938, 0.857214093, 0
-0.472293019, -0.0539086536, 1.3236357, 1
~~~

### MeshInfo 1 / Mesh 522128404

- Transform: 34 — g_torso_arm_l_female_shadow
- LOD: 3; vertices: 1426; triangles: 2089
- Skinning: ok; active lanes: 7779; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 22/0/0; max error: 1.7369378912945876e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[34] g_torso_arm_l_female_shadow (hash=522128404, parent=33)
  [33] grp_torso_arm_l_female (hash=3706024594, parent=32)
    [32] game_mesh (hash=828166497, parent=2)
      [2] mech_pilot_light (hash=4079346489, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 2 / Mesh 3766176514

- Transform: 35 — g_torso_arm_l_female_LOD2
- LOD: 2; vertices: 838; triangles: 1044
- Skinning: ok; active lanes: 4059; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 22/0/0; max error: 1.7369378912945876e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[35] g_torso_arm_l_female_LOD2 (hash=3766176514, parent=33)
  [33] grp_torso_arm_l_female (hash=3706024594, parent=32)
    [32] game_mesh (hash=828166497, parent=2)
      [2] mech_pilot_light (hash=4079346489, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 3 / Mesh 2627263307

- Transform: 36 — g_torso_arm_l_female_LOD1
- LOD: 1; vertices: 1707; triangles: 2612
- Skinning: ok; active lanes: 9696; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 22/0/0; max error: 1.7369378912945876e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[36] g_torso_arm_l_female_LOD1 (hash=2627263307, parent=33)
  [33] grp_torso_arm_l_female (hash=3706024594, parent=32)
    [32] game_mesh (hash=828166497, parent=2)
      [2] mech_pilot_light (hash=4079346489, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

### MeshInfo 4 / Mesh 46827504

- Transform: 37 — g_torso_arm_l_female
- LOD: 0; vertices: 3115; triangles: 5227
- Skinning: ok; active lanes: 18888; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 22/0/0; max error: 1.7369378912945876e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[37] g_torso_arm_l_female (hash=46827504, parent=33)
  [33] grp_torso_arm_l_female (hash=3706024594, parent=32)
    [32] game_mesh (hash=828166497, parent=2)
      [2] mech_pilot_light (hash=4079346489, parent=1)
        [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
          [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

## avatar_composite

- Unit: 4d1c334d294dfa97
- Package: 18235e0c9ec0e636
- Geometry source: CompositeUnit
- Meshes: 1
- Stored matrices: 1 identity / 0 non-identity
- Skinned meshes: 1; no-skin-attribute meshes: 0
- Bone matrix validation: 1 matches / 0 mismatches

| MeshInfo | Mesh ID | Transform | Name / hash | LOD | Vertices | Triangles | Skinning | Active lanes | Bone matrix check | Hierarchy check | Stored matrix |
|---:|---:|---:|---|---:|---:|---:|---|---:|---|---|---|
| 0 | 1278906229 | 102 | (unresolved) / 1278906229 | 0 | 27355 | 41165 | ok | 42158 | matches (1.7369378935150337e-07) | matches (0.0) | identity |

### MeshInfo 0 / Mesh 1278906229

- Transform: 102 — (unresolved)
- LOD: 0; vertices: 27355; triangles: 41165
- Skinning: ok; active lanes: 42158; unresolved lanes: 0
- Bone matrix validation: matches; matched/mismatched/unavailable slots: 61/0/0; max error: 1.7369378935150337e-07
- Hierarchy validation: matches; max error: 0.0
- Stored matrix identity classification: identity; identity error: 0.0
- Parent chain status: root_reached

~~~text
[102] (unresolved) (hash=1278906229, parent=100)
  [100] (unresolved) (hash=828166497, parent=2)
    [2] (unresolved) (hash=3365605331, parent=1)
      [1] FbxAxisSystem_ConvertNode (hash=2133911068, parent=0)
        [0] StingrayEntityRoot (hash=1243096897, parent=0)
~~~

Stored 4x4 matrix (row-major):

~~~text
1, 0, 0, 0
0, 1, 0, 0
0, 0, 1, 0
0, 0, 0, 1
~~~

## Interpretation limits

- The stored 4x4 matrix is reported without rewriting its values. The parser validates it against a reconstructed hierarchy matrix using the row-major row-vector and local-times-parent-world candidates; these conventions are candidates, not universal runtime proof.
- MeshInfo direct binding establishes a structural association, not that every bound mesh is a visible render mesh or a disposable culling proxy.
- A mesh with no skin attributes is not treated as a skinned mesh. A mesh with valid skin attributes and matching palette checks remains a skinning participant even if its name contains words such as culling.
- A matching hierarchy or BoneInfo matrix check validates internal consistency of the parsed representation; it does not prove in-game rendering, animation, or Source-to-HD2 vertex conversion.
- Vertex positions are not transformed or modified by this audit.
