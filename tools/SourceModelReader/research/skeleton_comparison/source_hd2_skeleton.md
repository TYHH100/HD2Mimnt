# Source / HD2 Skeleton Comparison

## Source Model

- MDL: gmpublisher.gma::models/lc_84/aes_yz_pm.mdl
- MDL / VVD / VTX versions: 48 / 4 / 7
- VVD vertex pool: 94858
- Active weighted source bones: 242
- Checksum: 43233072

## Target Unit

- Unit: 4d1c334d294dfa97
- Package: 18235e0c9ec0e636
- Geometry source: CompositeUnit
- Bones resource: 4d1c334d294dfa97

## Summary

| Metric | Value |
|---|---:|
| status | warning |
| source skeleton type | compiled_mdl_v48 |
| source node count | 255 |
| source active weighted node count | 242 |
| source active weighted exact match count | 0 |
| source active weighted unmapped count | 242 |
| source active weighted ambiguous count | 0 |
| target transform count | 107 |
| target named transform count | 90 |
| target active weighted transform count | 61 |
| exact name match count | 0 |
| exact name match active target count | 0 |
| source unmapped count | 255 |
| target only named transform count | 90 |
| ambiguous source match count | 0 |
| ambiguous target name count | 0 |
| unresolved target name count | 17 |
| parent mismatch count | 0 |
| parent unresolved count | 0 |
| source vertex pool count | 94858 |
| source active weighted bone count | 242 |
| target unit decode status | ok |
| target unit mesh count | 1 |
| target unit vertex count | 27355 |

## Exact name matches

| Source node | Source parent | Active Source influence | HD2 Transform | HD2 name | HD2 parent | Active target influence | Parent relation |
|---|---|---|---:|---|---|---|---|
| — | — | — | — | — | — | — | — |

## Advisory humanoid alias candidates

This section is hypothesis-only. It does not modify exact-name matches or approve a retarget.

| Source bone | Source active | HD2 candidate | Target active | Parent check |
|---|---|---|---|---|
| ValveBiped.Bip01_Pelvis | yes | hips | no | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_Spine | yes | spine1 | no | mismatch |
| ValveBiped.Bip01_Spine1 | yes | spine2 | no | match |
| ValveBiped.Bip01_Spine2 | yes | chest | no | match |
| ValveBiped.Bip01_Neck1 | yes | neck | no | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_Head1 | yes | head | no | match |
| ValveBiped.Bip01_L_Thigh | yes | l_thigh | no | match |
| ValveBiped.Bip01_L_Calf | yes | l_knee | no | match |
| ValveBiped.Bip01_L_Foot | yes | l_foot | no | match |
| ValveBiped.Bip01_L_Toe0 | yes | l_toe | no | mismatch |
| ValveBiped.Bip01_L_Clavicle | yes | l_clavicle | no | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_L_UpperArm | yes | l_shoulder | no | match |
| ValveBiped.Bip01_L_Forearm | yes | l_elbow | no | match |
| ValveBiped.Bip01_L_Hand | yes | l_hand | no | match |
| ValveBiped.Bip01_R_Thigh | yes | r_thigh | no | match |
| ValveBiped.Bip01_R_Calf | yes | r_knee | no | match |
| ValveBiped.Bip01_R_Foot | yes | r_foot | no | match |
| ValveBiped.Bip01_R_Toe0 | yes | r_toe | no | mismatch |
| ValveBiped.Bip01_R_Clavicle | yes | r_clavicle | no | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_R_UpperArm | yes | r_shoulder | no | match |
| ValveBiped.Bip01_R_Forearm | yes | r_elbow | no | match |
| ValveBiped.Bip01_R_Hand | yes | r_hand | no | match |

Candidate count: 22. Parent matches: 15. Parent mismatches: 3. Parent checks deferred: 4.

All candidates remain advisory-only; rest-pose, matrix-space, side and deformation validation is still required.

## Source bones without exact HD2 name

| Name | ID / Transform | Active Source influence | Details |
|---|---:|---|---|
| Back_Hair_Ponytail_Swim_Style1_01_01_L | 57 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_01_R | 52 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_02_L | 58 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_02_R | 53 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_03_L | 59 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_03_R | 54 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_04_L | 60 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_04_R | 55 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_05_L | 61 | yes |  |
| Back_Hair_Ponytail_Swim_Style1_01_05_R | 56 | yes |  |
| Back_Hair_Ponytail_Swim_Style2_01_01_L | 70 | yes |  |
| Back_Hair_Ponytail_Swim_Style2_01_01_R | 67 | yes |  |
| Back_Hair_Ponytail_Swim_Style2_01_02_L | 71 | yes |  |
| Back_Hair_Ponytail_Swim_Style2_01_02_R | 68 | yes |  |
| Back_Hair_Ponytail_Swim_Style2_01_03_L | 72 | yes |  |
| Back_Hair_Ponytail_Swim_Style2_01_03_R | 69 | yes |  |
| Breast_L | 127 | yes |  |
| Breast_R | 126 | yes |  |
| Breast_Ribbon_Swim_01 | 129 | yes |  |
| Breast_Ribbon_Swim_02 | 130 | yes |  |
| Breast_Ribbon_Swim_Root | 128 | yes |  |
| Ear_01_L | 65 | yes |  |
| Ear_01_R | 63 | yes |  |
| Ear_03_L | 66 | yes |  |
| Ear_03_R | 64 | yes |  |
| Hair_Aho_01 | 62 | yes |  |
| Hair_Frount_01_01 | 50 | yes |  |
| Hair_Frount_01_02 | 51 | yes |  |
| Hair_Frount_02_01 | 47 | yes |  |
| Hair_Frount_02_02 | 48 | yes |  |
| Hair_Frount_02_03 | 49 | yes |  |
| Hair_Frount_03_01 | 45 | yes |  |
| Hair_Frount_03_02 | 46 | yes |  |
| Hair_Side_01_01_L | 42 | yes |  |
| Hair_Side_01_01_R | 39 | yes |  |
| Hair_Side_01_02_L | 43 | yes |  |
| Hair_Side_01_02_R | 40 | yes |  |
| Hair_Side_01_03_L | 44 | yes |  |
| Hair_Side_01_03_R | 41 | yes |  |
| Hair_Side_02_01_L | 36 | yes |  |
| Hair_Side_02_01_R | 33 | yes |  |
| Hair_Side_02_02_L | 37 | yes |  |
| Hair_Side_02_02_R | 34 | yes |  |
| Hair_Side_02_03_L | 38 | yes |  |
| Hair_Side_02_03_R | 35 | yes |  |
| LeftForeArm_Ribbon_Swim_01_01 | 97 | yes |  |
| LeftForeArm_Ribbon_Swim_01_02 | 98 | yes |  |
| Neck_Ribbon_01_01_L | 76 | yes |  |
| Neck_Ribbon_01_01_R | 75 | yes |  |
| Neck_Ribbon_02_01 | 74 | yes |  |
| Neck_Ribbon_Root | 73 | yes |  |
| Qun_0_0 | 190 | yes |  |
| Qun_0_0_VRD | 188 | no |  |
| Qun_0_0_VRD2 | 189 | no |  |
| Qun_0_1 | 196 | yes |  |
| Qun_0_10 | 163 | yes |  |
| Qun_0_11 | 168 | yes |  |
| Qun_0_12 | 173 | yes |  |
| Qun_0_13 | 178 | yes |  |
| Qun_0_14 | 183 | yes |  |
| Qun_0_15 | 226 | yes |  |
| Qun_0_15_VRD | 225 | no |  |
| Qun_0_16 | 232 | yes |  |
| Qun_0_16_VRD | 231 | no |  |
| Qun_0_17 | 238 | yes |  |
| Qun_0_17_VRD | 237 | no |  |
| Qun_0_18 | 244 | yes |  |
| Qun_0_18_VRD | 243 | no |  |
| Qun_0_19 | 250 | yes |  |
| Qun_0_19_VRD | 249 | no |  |
| Qun_0_1_VRD | 195 | no |  |
| Qun_0_2 | 202 | yes |  |
| Qun_0_2_VRD | 201 | no |  |
| Qun_0_3 | 208 | yes |  |
| Qun_0_3_VRD | 207 | no |  |
| Qun_0_4 | 214 | yes |  |
| Qun_0_4_VRD | 213 | no |  |
| Qun_0_5 | 220 | yes |  |
| Qun_0_5_VRD | 219 | no |  |
| Qun_0_6 | 143 | yes |  |
| Qun_0_7 | 148 | yes |  |
| Qun_0_8 | 153 | yes |  |
| Qun_0_9 | 158 | yes |  |
| Qun_1_0 | 191 | yes |  |
| Qun_1_1 | 197 | yes |  |
| Qun_1_10 | 164 | yes |  |
| Qun_1_11 | 169 | yes |  |
| Qun_1_12 | 174 | yes |  |
| Qun_1_13 | 179 | yes |  |
| Qun_1_14 | 184 | yes |  |
| Qun_1_15 | 227 | yes |  |
| Qun_1_16 | 233 | yes |  |
| Qun_1_17 | 239 | yes |  |
| Qun_1_18 | 245 | yes |  |
| Qun_1_19 | 251 | yes |  |
| Qun_1_2 | 203 | yes |  |
| Qun_1_3 | 209 | yes |  |
| Qun_1_4 | 215 | yes |  |
| Qun_1_5 | 221 | yes |  |
| Qun_1_6 | 144 | yes |  |
| Qun_1_7 | 149 | yes |  |
| Qun_1_8 | 154 | yes |  |
| Qun_1_9 | 159 | yes |  |
| Qun_2_0 | 192 | yes |  |
| Qun_2_1 | 198 | yes |  |
| Qun_2_10 | 165 | yes |  |
| Qun_2_11 | 170 | yes |  |
| Qun_2_12 | 175 | yes |  |
| Qun_2_13 | 180 | yes |  |
| Qun_2_14 | 185 | yes |  |
| Qun_2_15 | 228 | yes |  |
| Qun_2_16 | 234 | yes |  |
| Qun_2_17 | 240 | yes |  |
| Qun_2_18 | 246 | yes |  |
| Qun_2_19 | 252 | yes |  |
| Qun_2_2 | 204 | yes |  |
| Qun_2_3 | 210 | yes |  |
| Qun_2_4 | 216 | yes |  |
| Qun_2_5 | 222 | yes |  |
| Qun_2_6 | 145 | yes |  |
| Qun_2_7 | 150 | yes |  |
| Qun_2_8 | 155 | yes |  |
| Qun_2_9 | 160 | yes |  |
| Qun_3_0 | 193 | yes |  |
| Qun_3_1 | 199 | yes |  |
| Qun_3_10 | 166 | yes |  |
| Qun_3_11 | 171 | yes |  |
| Qun_3_12 | 176 | yes |  |
| Qun_3_13 | 181 | yes |  |
| Qun_3_14 | 186 | yes |  |
| Qun_3_15 | 229 | yes |  |
| Qun_3_16 | 235 | yes |  |
| Qun_3_17 | 241 | yes |  |
| Qun_3_18 | 247 | yes |  |
| Qun_3_19 | 253 | yes |  |
| Qun_3_2 | 205 | yes |  |
| Qun_3_3 | 211 | yes |  |
| Qun_3_4 | 217 | yes |  |
| Qun_3_5 | 223 | yes |  |
| Qun_3_6 | 146 | yes |  |
| Qun_3_7 | 151 | yes |  |
| Qun_3_8 | 156 | yes |  |
| Qun_3_9 | 161 | yes |  |
| Qun_4_0 | 194 | yes |  |
| Qun_4_1 | 200 | yes |  |
| Qun_4_10 | 167 | yes |  |
| Qun_4_11 | 172 | yes |  |
| Qun_4_12 | 177 | yes |  |
| Qun_4_13 | 182 | yes |  |
| Qun_4_14 | 187 | yes |  |
| Qun_4_15 | 230 | yes |  |
| Qun_4_16 | 236 | yes |  |
| Qun_4_17 | 242 | yes |  |
| Qun_4_18 | 248 | yes |  |
| Qun_4_19 | 254 | yes |  |
| Qun_4_2 | 206 | yes |  |
| Qun_4_3 | 212 | yes |  |
| Qun_4_4 | 218 | yes |  |
| Qun_4_5 | 224 | yes |  |
| Qun_4_6 | 147 | yes |  |
| Qun_4_7 | 152 | yes |  |
| Qun_4_8 | 157 | yes |  |
| Qun_4_9 | 162 | yes |  |
| Shoes_Ribbon_Swim_01_01_L | 12 | yes |  |
| Shoes_Ribbon_Swim_01_01_R | 25 | yes |  |
| Shoes_Ribbon_Swim_01_02_L | 13 | yes |  |
| Shoes_Ribbon_Swim_01_02_R | 26 | yes |  |
| Shoes_Ribbon_Swim_02_01_L | 10 | yes |  |
| Shoes_Ribbon_Swim_02_01_R | 23 | yes |  |
| Shoes_Ribbon_Swim_02_02_L | 11 | yes |  |
| Shoes_Ribbon_Swim_02_02_R | 24 | yes |  |
| Shoes_Ribbon_Swim_03_01_L | 8 | yes |  |
| Shoes_Ribbon_Swim_03_01_R | 21 | yes |  |
| Shoes_Ribbon_Swim_03_02_L | 9 | yes |  |
| Shoes_Ribbon_Swim_03_02_R | 22 | yes |  |
| Shoes_Ribbon_Swim_04_01_L | 6 | yes |  |
| Shoes_Ribbon_Swim_04_01_R | 19 | yes |  |
| Shoes_Ribbon_Swim_04_02_L | 7 | yes |  |
| Shoes_Ribbon_Swim_04_02_R | 20 | yes |  |
| Shoes_Ribbon_Swim_Root_L | 5 | yes |  |
| Shoes_Ribbon_Swim_Root_R | 18 | yes |  |
| Tail_0_1 | 131 | yes |  |
| Tail_10_1 | 141 | yes |  |
| Tail_11_1 | 142 | yes |  |
| Tail_1_1 | 132 | yes |  |
| Tail_2_1 | 133 | yes |  |
| Tail_3_1 | 134 | yes |  |
| Tail_4_1 | 135 | yes |  |
| Tail_5_1 | 136 | yes |  |
| Tail_6_1 | 137 | yes |  |
| Tail_7_1 | 138 | yes |  |
| Tail_8_1 | 139 | yes |  |
| Tail_9_1 | 140 | yes |  |
| ValveBiped.Bip01_Head1 | 32 | yes |  |
| ValveBiped.Bip01_L_Calf | 2 | yes |  |
| ValveBiped.Bip01_L_Clavicle | 77 | yes |  |
| ValveBiped.Bip01_L_Finger0 | 87 | yes |  |
| ValveBiped.Bip01_L_Finger01 | 88 | yes |  |
| ValveBiped.Bip01_L_Finger02 | 89 | yes |  |
| ValveBiped.Bip01_L_Finger1 | 84 | yes |  |
| ValveBiped.Bip01_L_Finger11 | 85 | yes |  |
| ValveBiped.Bip01_L_Finger12 | 86 | yes |  |
| ValveBiped.Bip01_L_Finger2 | 81 | yes |  |
| ValveBiped.Bip01_L_Finger21 | 82 | yes |  |
| ValveBiped.Bip01_L_Finger22 | 83 | yes |  |
| ValveBiped.Bip01_L_Finger3 | 93 | yes |  |
| ValveBiped.Bip01_L_Finger31 | 94 | yes |  |
| ValveBiped.Bip01_L_Finger32 | 95 | yes |  |
| ValveBiped.Bip01_L_Finger4 | 90 | yes |  |
| ValveBiped.Bip01_L_Finger41 | 91 | yes |  |
| ValveBiped.Bip01_L_Finger42 | 92 | yes |  |
| ValveBiped.Bip01_L_Foot | 3 | yes |  |
| ValveBiped.Bip01_L_Forearm | 79 | yes |  |
| ValveBiped.Bip01_L_Forearm_Helperbone | 99 | yes |  |
| ValveBiped.Bip01_L_Hand | 80 | yes |  |
| ValveBiped.Bip01_L_Hand_Helperbone | 96 | yes |  |
| ValveBiped.Bip01_L_Thigh | 1 | yes |  |
| ValveBiped.Bip01_L_Toe0 | 4 | yes |  |
| ValveBiped.Bip01_L_UpperArm | 78 | yes |  |
| ValveBiped.Bip01_Neck1 | 31 | yes |  |
| ValveBiped.Bip01_Pelvis | 0 | yes |  |
| ValveBiped.Bip01_R_Calf | 15 | yes |  |
| ValveBiped.Bip01_R_Clavicle | 105 | yes |  |
| ValveBiped.Bip01_R_Finger0 | 115 | yes |  |
| ValveBiped.Bip01_R_Finger01 | 116 | yes |  |
| ValveBiped.Bip01_R_Finger02 | 117 | yes |  |
| ValveBiped.Bip01_R_Finger1 | 112 | yes |  |
| ValveBiped.Bip01_R_Finger11 | 113 | yes |  |
| ValveBiped.Bip01_R_Finger12 | 114 | yes |  |
| ValveBiped.Bip01_R_Finger2 | 109 | yes |  |
| ValveBiped.Bip01_R_Finger21 | 110 | yes |  |
| ValveBiped.Bip01_R_Finger22 | 111 | yes |  |
| ValveBiped.Bip01_R_Finger3 | 121 | yes |  |
| ValveBiped.Bip01_R_Finger31 | 122 | yes |  |
| ValveBiped.Bip01_R_Finger32 | 123 | yes |  |
| ValveBiped.Bip01_R_Finger4 | 118 | yes |  |
| ValveBiped.Bip01_R_Finger41 | 119 | yes |  |
| ValveBiped.Bip01_R_Finger42 | 120 | yes |  |
| ValveBiped.Bip01_R_Foot | 16 | yes |  |
| ValveBiped.Bip01_R_Forearm | 107 | yes |  |
| ValveBiped.Bip01_R_Forearm_Helperbone | 125 | yes |  |
| ValveBiped.Bip01_R_Hand | 108 | yes |  |
| ValveBiped.Bip01_R_Hand_Helperbone | 124 | yes |  |
| ValveBiped.Bip01_R_Thigh | 14 | yes |  |
| ValveBiped.Bip01_R_Toe0 | 17 | yes |  |
| ValveBiped.Bip01_R_UpperArm | 106 | yes |  |
| ValveBiped.Bip01_Spine | 27 | yes |  |
| ValveBiped.Bip01_Spine1 | 28 | yes |  |
| ValveBiped.Bip01_Spine2 | 29 | yes |  |
| ValveBiped.Bip01_Spine4 | 30 | no |  |
| Wolfdoll_Ear_01_01_L | 104 | yes |  |
| Wolfdoll_Ear_01_01_R | 103 | yes |  |
| Wolfdoll_Root | 100 | yes |  |
| Wolfdoll_Root_001 | 102 | yes |  |
| Wolfdoll_Tail_01 | 101 | yes |  |

## HD2-only named transforms

| Name | ID / Transform | Active Source influence | Details |
|---|---:|---|---|
| aim_weapon | 33 | — | attach_weapon |
| attach_cam_1 | 6 | — | root |
| attach_hand_l | 48 | — | l_hand |
| attach_hand_r | 84 | — | r_hand |
| attach_intelpad | 23 | — | spine1 |
| attach_knife | 25 | — | spine1 |
| attach_samplepouch | 22 | — | spine1 |
| attach_weapon | 32 | — | chest |
| backpack | 29 | — | chest |
| boss | 7 | — | root |
| boss_aim | 8 | — | boss |
| cape1 | 34 | — | chest |
| cape2 | 35 | — | cape1 |
| cape3 | 36 | — | cape2 |
| cape4 | 37 | — | cape3 |
| cape5 | 38 | — | cape4 |
| cape6 | 39 | — | cape5 |
| cape7 | 40 | — | cape6 |
| cape8 | 41 | — | cape7 |
| chest | 28 | — | spine2 |
| climb_ref | 5 | — | root |
| FbxAxisSystem_ConvertNode | 1 | — | StingrayEntityRoot |
| head | 90 | — | neck |
| head_aim | 91 | — | head |
| hips | 9 | — | boss |
| l_ball | 19 | — | l_foot |
| l_clavicle | 42 | — | chest |
| l_elbow | 46 | — | l_shoulder |
| l_foot | 18 | — | l_knee |
| l_hand | 47 | — | l_elbow |
| l_hand_twist | 64 | — | l_elbow |
| l_index_finger1 | 58 | — | l_hand |
| l_index_finger2 | 59 | — | l_index_finger1 |
| l_index_finger3 | 60 | — | l_index_finger2 |
| l_knee | 17 | — | l_thigh |
| l_middle_finger1 | 55 | — | l_hand |
| l_middle_finger2 | 56 | — | l_middle_finger1 |
| l_middle_finger3 | 57 | — | l_middle_finger2 |
| l_pinky_finger1 | 49 | — | l_hand |
| l_pinky_finger2 | 50 | — | l_pinky_finger1 |
| l_pinky_finger3 | 51 | — | l_pinky_finger2 |
| l_ring_finger1 | 52 | — | l_hand |
| l_ring_finger2 | 53 | — | l_ring_finger1 |
| l_ring_finger3 | 54 | — | l_ring_finger2 |
| l_shoulder | 44 | — | l_clavicle |
| l_shoulder_twist | 45 | — | l_shoulder |
| l_shoulderarmour | 43 | — | l_clavicle |
| l_thigh | 16 | — | hips |
| l_thumb_finger1 | 61 | — | l_hand |
| l_thumb_finger2 | 62 | — | l_thumb_finger1 |
| l_thumb_finger3 | 63 | — | l_thumb_finger2 |
| l_toe | 20 | — | l_ball |
| neck | 89 | — | chest |
| pistol | 11 | — | r_thigh |
| r_ball | 14 | — | r_foot |
| r_clavicle | 65 | — | chest |
| r_elbow | 69 | — | r_shoulder |
| r_foot | 13 | — | r_knee |
| r_hand | 71 | — | r_elbow |
| r_hand_twist | 70 | — | r_elbow |
| r_index_finger1 | 81 | — | r_hand |
| r_index_finger2 | 82 | — | r_index_finger1 |
| r_index_finger3 | 83 | — | r_index_finger2 |
| r_knee | 12 | — | r_thigh |
| r_middle_finger1 | 78 | — | r_hand |
| r_middle_finger2 | 79 | — | r_middle_finger1 |
| r_middle_finger3 | 80 | — | r_middle_finger2 |
| r_pinky_finger1 | 72 | — | r_hand |
| r_pinky_finger2 | 73 | — | r_pinky_finger1 |
| r_pinky_finger3 | 74 | — | r_pinky_finger2 |
| r_ring_finger1 | 75 | — | r_hand |
| r_ring_finger2 | 76 | — | r_ring_finger1 |
| r_ring_finger3 | 77 | — | r_ring_finger2 |
| r_shoulder | 67 | — | r_clavicle |
| r_shoulder_twist | 68 | — | r_shoulder |
| r_shoulderarmour | 66 | — | r_clavicle |
| r_thigh | 10 | — | hips |
| r_thumb_finger1 | 86 | — | r_hand |
| r_thumb_finger2 | 87 | — | r_thumb_finger1 |
| r_thumb_finger3 | 88 | — | r_thumb_finger2 |
| r_toe | 15 | — | r_ball |
| root | 4 | — |  |
| sling | 27 | — | spine2 |
| spine1 | 21 | — | boss |
| spine2 | 26 | — | spine1 |
| StingrayEntityRoot | 0 | — |  |
| support | 31 | — | chest |
| support_mg | 30 | — | chest |
| target_designator | 24 | — | spine1 |
| weapon_aim | 85 | — | attach_hand_r |

## Ambiguous source matches

| Name | ID / Transform | Active Source influence | Details |
|---|---:|---|---|
| — | — | — | none |

## Ambiguous / unresolved HD2 names

| Transform | Name hash | Ambiguous |
|---:|---:|---|
| 2 | 3365605331 | False |
| 3 | 2539449277 | False |
| 92 | 1965514668 | False |
| 93 | 438988530 | False |
| 94 | 2645162950 | False |
| 95 | 2748617169 | False |
| 96 | 1134132846 | False |
| 97 | 3244124788 | False |
| 98 | 4187370070 | False |
| 99 | 395203055 | False |
| 100 | 828166497 | False |
| 101 | 3083944529 | False |
| 102 | 1278906229 | False |
| 103 | 2781352283 | False |
| 104 | 2088035612 | False |
| 105 | 2753605768 | False |
| 106 | 3105235463 | False |

## Parent hierarchy mismatches

| Bone | Source parent | HD2 parent |
|---|---|---|
| — | — | none |

## Matching policy and limitations

- The Source skeleton comes from compiled MDL bone definitions. Active source bones are derived from positive-weight VVD lanes across the stored vertex pool.
- The VVD active-bone set is a union across the stored model vertex pool; it is not a per-BodyGroup or per-LOD usage report. Non-zero VVD fixup tables remain a parser limitation.
- Names are matched exactly after Unicode case-folding and trimming edge whitespace.
- No punctuation stripping, prefix guessing, fuzzy matching, or automatic aliasing is performed.
- Parent relationships are compared only when both parents resolve to names.
- Rest-pose numeric values are not compared. Source MDL local bone transforms and poseToBone matrices require a validated coordinate, unit-scale, and matrix-space conversion before comparison with HD2 TransformInfo.
- No retargeting or bone mapping is applied. This is a diagnostic report only.

## Source warnings

None.

## Target Unit warnings

None.
