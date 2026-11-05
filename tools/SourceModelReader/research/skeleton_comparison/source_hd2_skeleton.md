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
| ValveBiped.Bip01_Pelvis | yes | hips | yes | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_Spine | yes | spine1 | yes | mismatch |
| ValveBiped.Bip01_Spine1 | yes | spine2 | yes | match |
| ValveBiped.Bip01_Spine2 | yes | chest | yes | match |
| ValveBiped.Bip01_Neck1 | yes | neck | yes | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_Head1 | yes | head | yes | match |
| ValveBiped.Bip01_L_Thigh | yes | l_thigh | yes | match |
| ValveBiped.Bip01_L_Calf | yes | l_knee | yes | match |
| ValveBiped.Bip01_L_Foot | yes | l_foot | yes | match |
| ValveBiped.Bip01_L_Toe0 | yes | l_toe | yes | mismatch |
| ValveBiped.Bip01_L_Clavicle | yes | l_clavicle | yes | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_L_UpperArm | yes | l_shoulder | yes | match |
| ValveBiped.Bip01_L_Forearm | yes | l_elbow | yes | match |
| ValveBiped.Bip01_L_Hand | yes | l_hand | yes | match |
| ValveBiped.Bip01_R_Thigh | yes | r_thigh | yes | match |
| ValveBiped.Bip01_R_Calf | yes | r_knee | yes | match |
| ValveBiped.Bip01_R_Foot | yes | r_foot | yes | match |
| ValveBiped.Bip01_R_Toe0 | yes | r_toe | yes | mismatch |
| ValveBiped.Bip01_R_Clavicle | yes | r_clavicle | yes | not_checked_source_parent_has_no_candidate_alias |
| ValveBiped.Bip01_R_UpperArm | yes | r_shoulder | yes | match |
| ValveBiped.Bip01_R_Forearm | yes | r_elbow | yes | match |
| ValveBiped.Bip01_R_Hand | yes | r_hand | yes | match |

Candidate count: 22. Parent matches: 15. Parent mismatches: 3. Parent checks deferred: 4.

All candidates remain advisory-only; rest-pose, matrix-space, side and deformation validation is still required.

## Source bones without exact HD2 name

| Name | Transform | Active target influence | Lanes | Parent |
|---|---:|---|---:|---|
| Back_Hair_Ponytail_Swim_Style1_01_01_L | 57 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_01_R | 52 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_02_L | 58 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_02_R | 53 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_03_L | 59 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_03_R | 54 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_04_L | 60 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_04_R | 55 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_05_L | 61 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style1_01_05_R | 56 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style2_01_01_L | 70 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style2_01_01_R | 67 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style2_01_02_L | 71 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style2_01_02_R | 68 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style2_01_03_L | 72 | — | 0 |  |
| Back_Hair_Ponytail_Swim_Style2_01_03_R | 69 | — | 0 |  |
| Breast_L | 127 | — | 0 |  |
| Breast_R | 126 | — | 0 |  |
| Breast_Ribbon_Swim_01 | 129 | — | 0 |  |
| Breast_Ribbon_Swim_02 | 130 | — | 0 |  |
| Breast_Ribbon_Swim_Root | 128 | — | 0 |  |
| Ear_01_L | 65 | — | 0 |  |
| Ear_01_R | 63 | — | 0 |  |
| Ear_03_L | 66 | — | 0 |  |
| Ear_03_R | 64 | — | 0 |  |
| Hair_Aho_01 | 62 | — | 0 |  |
| Hair_Frount_01_01 | 50 | — | 0 |  |
| Hair_Frount_01_02 | 51 | — | 0 |  |
| Hair_Frount_02_01 | 47 | — | 0 |  |
| Hair_Frount_02_02 | 48 | — | 0 |  |
| Hair_Frount_02_03 | 49 | — | 0 |  |
| Hair_Frount_03_01 | 45 | — | 0 |  |
| Hair_Frount_03_02 | 46 | — | 0 |  |
| Hair_Side_01_01_L | 42 | — | 0 |  |
| Hair_Side_01_01_R | 39 | — | 0 |  |
| Hair_Side_01_02_L | 43 | — | 0 |  |
| Hair_Side_01_02_R | 40 | — | 0 |  |
| Hair_Side_01_03_L | 44 | — | 0 |  |
| Hair_Side_01_03_R | 41 | — | 0 |  |
| Hair_Side_02_01_L | 36 | — | 0 |  |
| Hair_Side_02_01_R | 33 | — | 0 |  |
| Hair_Side_02_02_L | 37 | — | 0 |  |
| Hair_Side_02_02_R | 34 | — | 0 |  |
| Hair_Side_02_03_L | 38 | — | 0 |  |
| Hair_Side_02_03_R | 35 | — | 0 |  |
| LeftForeArm_Ribbon_Swim_01_01 | 97 | — | 0 |  |
| LeftForeArm_Ribbon_Swim_01_02 | 98 | — | 0 |  |
| Neck_Ribbon_01_01_L | 76 | — | 0 |  |
| Neck_Ribbon_01_01_R | 75 | — | 0 |  |
| Neck_Ribbon_02_01 | 74 | — | 0 |  |
| Neck_Ribbon_Root | 73 | — | 0 |  |
| Qun_0_0 | 190 | — | 0 |  |
| Qun_0_0_VRD | 188 | — | 0 |  |
| Qun_0_0_VRD2 | 189 | — | 0 |  |
| Qun_0_1 | 196 | — | 0 |  |
| Qun_0_10 | 163 | — | 0 |  |
| Qun_0_11 | 168 | — | 0 |  |
| Qun_0_12 | 173 | — | 0 |  |
| Qun_0_13 | 178 | — | 0 |  |
| Qun_0_14 | 183 | — | 0 |  |
| Qun_0_15 | 226 | — | 0 |  |
| Qun_0_15_VRD | 225 | — | 0 |  |
| Qun_0_16 | 232 | — | 0 |  |
| Qun_0_16_VRD | 231 | — | 0 |  |
| Qun_0_17 | 238 | — | 0 |  |
| Qun_0_17_VRD | 237 | — | 0 |  |
| Qun_0_18 | 244 | — | 0 |  |
| Qun_0_18_VRD | 243 | — | 0 |  |
| Qun_0_19 | 250 | — | 0 |  |
| Qun_0_19_VRD | 249 | — | 0 |  |
| Qun_0_1_VRD | 195 | — | 0 |  |
| Qun_0_2 | 202 | — | 0 |  |
| Qun_0_2_VRD | 201 | — | 0 |  |
| Qun_0_3 | 208 | — | 0 |  |
| Qun_0_3_VRD | 207 | — | 0 |  |
| Qun_0_4 | 214 | — | 0 |  |
| Qun_0_4_VRD | 213 | — | 0 |  |
| Qun_0_5 | 220 | — | 0 |  |
| Qun_0_5_VRD | 219 | — | 0 |  |
| Qun_0_6 | 143 | — | 0 |  |
| Qun_0_7 | 148 | — | 0 |  |
| Qun_0_8 | 153 | — | 0 |  |
| Qun_0_9 | 158 | — | 0 |  |
| Qun_1_0 | 191 | — | 0 |  |
| Qun_1_1 | 197 | — | 0 |  |
| Qun_1_10 | 164 | — | 0 |  |
| Qun_1_11 | 169 | — | 0 |  |
| Qun_1_12 | 174 | — | 0 |  |
| Qun_1_13 | 179 | — | 0 |  |
| Qun_1_14 | 184 | — | 0 |  |
| Qun_1_15 | 227 | — | 0 |  |
| Qun_1_16 | 233 | — | 0 |  |
| Qun_1_17 | 239 | — | 0 |  |
| Qun_1_18 | 245 | — | 0 |  |
| Qun_1_19 | 251 | — | 0 |  |
| Qun_1_2 | 203 | — | 0 |  |
| Qun_1_3 | 209 | — | 0 |  |
| Qun_1_4 | 215 | — | 0 |  |
| Qun_1_5 | 221 | — | 0 |  |
| Qun_1_6 | 144 | — | 0 |  |
| Qun_1_7 | 149 | — | 0 |  |
| Qun_1_8 | 154 | — | 0 |  |
| Qun_1_9 | 159 | — | 0 |  |
| Qun_2_0 | 192 | — | 0 |  |
| Qun_2_1 | 198 | — | 0 |  |
| Qun_2_10 | 165 | — | 0 |  |
| Qun_2_11 | 170 | — | 0 |  |
| Qun_2_12 | 175 | — | 0 |  |
| Qun_2_13 | 180 | — | 0 |  |
| Qun_2_14 | 185 | — | 0 |  |
| Qun_2_15 | 228 | — | 0 |  |
| Qun_2_16 | 234 | — | 0 |  |
| Qun_2_17 | 240 | — | 0 |  |
| Qun_2_18 | 246 | — | 0 |  |
| Qun_2_19 | 252 | — | 0 |  |
| Qun_2_2 | 204 | — | 0 |  |
| Qun_2_3 | 210 | — | 0 |  |
| Qun_2_4 | 216 | — | 0 |  |
| Qun_2_5 | 222 | — | 0 |  |
| Qun_2_6 | 145 | — | 0 |  |
| Qun_2_7 | 150 | — | 0 |  |
| Qun_2_8 | 155 | — | 0 |  |
| Qun_2_9 | 160 | — | 0 |  |
| Qun_3_0 | 193 | — | 0 |  |
| Qun_3_1 | 199 | — | 0 |  |
| Qun_3_10 | 166 | — | 0 |  |
| Qun_3_11 | 171 | — | 0 |  |
| Qun_3_12 | 176 | — | 0 |  |
| Qun_3_13 | 181 | — | 0 |  |
| Qun_3_14 | 186 | — | 0 |  |
| Qun_3_15 | 229 | — | 0 |  |
| Qun_3_16 | 235 | — | 0 |  |
| Qun_3_17 | 241 | — | 0 |  |
| Qun_3_18 | 247 | — | 0 |  |
| Qun_3_19 | 253 | — | 0 |  |
| Qun_3_2 | 205 | — | 0 |  |
| Qun_3_3 | 211 | — | 0 |  |
| Qun_3_4 | 217 | — | 0 |  |
| Qun_3_5 | 223 | — | 0 |  |
| Qun_3_6 | 146 | — | 0 |  |
| Qun_3_7 | 151 | — | 0 |  |
| Qun_3_8 | 156 | — | 0 |  |
| Qun_3_9 | 161 | — | 0 |  |
| Qun_4_0 | 194 | — | 0 |  |
| Qun_4_1 | 200 | — | 0 |  |
| Qun_4_10 | 167 | — | 0 |  |
| Qun_4_11 | 172 | — | 0 |  |
| Qun_4_12 | 177 | — | 0 |  |
| Qun_4_13 | 182 | — | 0 |  |
| Qun_4_14 | 187 | — | 0 |  |
| Qun_4_15 | 230 | — | 0 |  |
| Qun_4_16 | 236 | — | 0 |  |
| Qun_4_17 | 242 | — | 0 |  |
| Qun_4_18 | 248 | — | 0 |  |
| Qun_4_19 | 254 | — | 0 |  |
| Qun_4_2 | 206 | — | 0 |  |
| Qun_4_3 | 212 | — | 0 |  |
| Qun_4_4 | 218 | — | 0 |  |
| Qun_4_5 | 224 | — | 0 |  |
| Qun_4_6 | 147 | — | 0 |  |
| Qun_4_7 | 152 | — | 0 |  |
| Qun_4_8 | 157 | — | 0 |  |
| Qun_4_9 | 162 | — | 0 |  |
| Shoes_Ribbon_Swim_01_01_L | 12 | — | 0 |  |
| Shoes_Ribbon_Swim_01_01_R | 25 | — | 0 |  |
| Shoes_Ribbon_Swim_01_02_L | 13 | — | 0 |  |
| Shoes_Ribbon_Swim_01_02_R | 26 | — | 0 |  |
| Shoes_Ribbon_Swim_02_01_L | 10 | — | 0 |  |
| Shoes_Ribbon_Swim_02_01_R | 23 | — | 0 |  |
| Shoes_Ribbon_Swim_02_02_L | 11 | — | 0 |  |
| Shoes_Ribbon_Swim_02_02_R | 24 | — | 0 |  |
| Shoes_Ribbon_Swim_03_01_L | 8 | — | 0 |  |
| Shoes_Ribbon_Swim_03_01_R | 21 | — | 0 |  |
| Shoes_Ribbon_Swim_03_02_L | 9 | — | 0 |  |
| Shoes_Ribbon_Swim_03_02_R | 22 | — | 0 |  |
| Shoes_Ribbon_Swim_04_01_L | 6 | — | 0 |  |
| Shoes_Ribbon_Swim_04_01_R | 19 | — | 0 |  |
| Shoes_Ribbon_Swim_04_02_L | 7 | — | 0 |  |
| Shoes_Ribbon_Swim_04_02_R | 20 | — | 0 |  |
| Shoes_Ribbon_Swim_Root_L | 5 | — | 0 |  |
| Shoes_Ribbon_Swim_Root_R | 18 | — | 0 |  |
| Tail_0_1 | 131 | — | 0 |  |
| Tail_10_1 | 141 | — | 0 |  |
| Tail_11_1 | 142 | — | 0 |  |
| Tail_1_1 | 132 | — | 0 |  |
| Tail_2_1 | 133 | — | 0 |  |
| Tail_3_1 | 134 | — | 0 |  |
| Tail_4_1 | 135 | — | 0 |  |
| Tail_5_1 | 136 | — | 0 |  |
| Tail_6_1 | 137 | — | 0 |  |
| Tail_7_1 | 138 | — | 0 |  |
| Tail_8_1 | 139 | — | 0 |  |
| Tail_9_1 | 140 | — | 0 |  |
| ValveBiped.Bip01_Head1 | 32 | — | 0 |  |
| ValveBiped.Bip01_L_Calf | 2 | — | 0 |  |
| ValveBiped.Bip01_L_Clavicle | 77 | — | 0 |  |
| ValveBiped.Bip01_L_Finger0 | 87 | — | 0 |  |
| ValveBiped.Bip01_L_Finger01 | 88 | — | 0 |  |
| ValveBiped.Bip01_L_Finger02 | 89 | — | 0 |  |
| ValveBiped.Bip01_L_Finger1 | 84 | — | 0 |  |
| ValveBiped.Bip01_L_Finger11 | 85 | — | 0 |  |
| ValveBiped.Bip01_L_Finger12 | 86 | — | 0 |  |
| ValveBiped.Bip01_L_Finger2 | 81 | — | 0 |  |
| ValveBiped.Bip01_L_Finger21 | 82 | — | 0 |  |
| ValveBiped.Bip01_L_Finger22 | 83 | — | 0 |  |
| ValveBiped.Bip01_L_Finger3 | 93 | — | 0 |  |
| ValveBiped.Bip01_L_Finger31 | 94 | — | 0 |  |
| ValveBiped.Bip01_L_Finger32 | 95 | — | 0 |  |
| ValveBiped.Bip01_L_Finger4 | 90 | — | 0 |  |
| ValveBiped.Bip01_L_Finger41 | 91 | — | 0 |  |
| ValveBiped.Bip01_L_Finger42 | 92 | — | 0 |  |
| ValveBiped.Bip01_L_Foot | 3 | — | 0 |  |
| ValveBiped.Bip01_L_Forearm | 79 | — | 0 |  |
| ValveBiped.Bip01_L_Forearm_Helperbone | 99 | — | 0 |  |
| ValveBiped.Bip01_L_Hand | 80 | — | 0 |  |
| ValveBiped.Bip01_L_Hand_Helperbone | 96 | — | 0 |  |
| ValveBiped.Bip01_L_Thigh | 1 | — | 0 |  |
| ValveBiped.Bip01_L_Toe0 | 4 | — | 0 |  |
| ValveBiped.Bip01_L_UpperArm | 78 | — | 0 |  |
| ValveBiped.Bip01_Neck1 | 31 | — | 0 |  |
| ValveBiped.Bip01_Pelvis | 0 | — | 0 |  |
| ValveBiped.Bip01_R_Calf | 15 | — | 0 |  |
| ValveBiped.Bip01_R_Clavicle | 105 | — | 0 |  |
| ValveBiped.Bip01_R_Finger0 | 115 | — | 0 |  |
| ValveBiped.Bip01_R_Finger01 | 116 | — | 0 |  |
| ValveBiped.Bip01_R_Finger02 | 117 | — | 0 |  |
| ValveBiped.Bip01_R_Finger1 | 112 | — | 0 |  |
| ValveBiped.Bip01_R_Finger11 | 113 | — | 0 |  |
| ValveBiped.Bip01_R_Finger12 | 114 | — | 0 |  |
| ValveBiped.Bip01_R_Finger2 | 109 | — | 0 |  |
| ValveBiped.Bip01_R_Finger21 | 110 | — | 0 |  |
| ValveBiped.Bip01_R_Finger22 | 111 | — | 0 |  |
| ValveBiped.Bip01_R_Finger3 | 121 | — | 0 |  |
| ValveBiped.Bip01_R_Finger31 | 122 | — | 0 |  |
| ValveBiped.Bip01_R_Finger32 | 123 | — | 0 |  |
| ValveBiped.Bip01_R_Finger4 | 118 | — | 0 |  |
| ValveBiped.Bip01_R_Finger41 | 119 | — | 0 |  |
| ValveBiped.Bip01_R_Finger42 | 120 | — | 0 |  |
| ValveBiped.Bip01_R_Foot | 16 | — | 0 |  |
| ValveBiped.Bip01_R_Forearm | 107 | — | 0 |  |
| ValveBiped.Bip01_R_Forearm_Helperbone | 125 | — | 0 |  |
| ValveBiped.Bip01_R_Hand | 108 | — | 0 |  |
| ValveBiped.Bip01_R_Hand_Helperbone | 124 | — | 0 |  |
| ValveBiped.Bip01_R_Thigh | 14 | — | 0 |  |
| ValveBiped.Bip01_R_Toe0 | 17 | — | 0 |  |
| ValveBiped.Bip01_R_UpperArm | 106 | — | 0 |  |
| ValveBiped.Bip01_Spine | 27 | — | 0 |  |
| ValveBiped.Bip01_Spine1 | 28 | — | 0 |  |
| ValveBiped.Bip01_Spine2 | 29 | — | 0 |  |
| ValveBiped.Bip01_Spine4 | 30 | — | 0 |  |
| Wolfdoll_Ear_01_01_L | 104 | — | 0 |  |
| Wolfdoll_Ear_01_01_R | 103 | — | 0 |  |
| Wolfdoll_Root | 100 | — | 0 |  |
| Wolfdoll_Root_001 | 102 | — | 0 |  |
| Wolfdoll_Tail_01 | 101 | — | 0 |  |

## HD2-only named transforms

| Name | Transform | Active target influence | Lanes | Parent |
|---|---:|---|---:|---|
| aim_weapon | 33 | no | 0 | attach_weapon |
| attach_cam_1 | 6 | no | 0 | root |
| attach_hand_l | 48 | no | 0 | l_hand |
| attach_hand_r | 84 | no | 0 | r_hand |
| attach_intelpad | 23 | no | 0 | spine1 |
| attach_knife | 25 | no | 0 | spine1 |
| attach_samplepouch | 22 | no | 0 | spine1 |
| attach_weapon | 32 | no | 0 | chest |
| backpack | 29 | no | 0 | chest |
| boss | 7 | yes | 916 | root |
| boss_aim | 8 | no | 0 | boss |
| cape1 | 34 | no | 0 | chest |
| cape2 | 35 | no | 0 | cape1 |
| cape3 | 36 | no | 0 | cape2 |
| cape4 | 37 | no | 0 | cape3 |
| cape5 | 38 | no | 0 | cape4 |
| cape6 | 39 | no | 0 | cape5 |
| cape7 | 40 | no | 0 | cape6 |
| cape8 | 41 | no | 0 | cape7 |
| chest | 28 | yes | 7264 | spine2 |
| climb_ref | 5 | no | 0 | root |
| FbxAxisSystem_ConvertNode | 1 | no | 0 | StingrayEntityRoot |
| head | 90 | yes | 7473 | neck |
| head_aim | 91 | no | 0 | head |
| hips | 9 | yes | 457 | boss |
| l_ball | 19 | yes | 83 | l_foot |
| l_clavicle | 42 | yes | 745 | chest |
| l_elbow | 46 | yes | 1927 | l_shoulder |
| l_foot | 18 | yes | 441 | l_knee |
| l_hand | 47 | yes | 755 | l_elbow |
| l_hand_twist | 64 | yes | 266 | l_elbow |
| l_index_finger1 | 58 | yes | 197 | l_hand |
| l_index_finger2 | 59 | yes | 106 | l_index_finger1 |
| l_index_finger3 | 60 | yes | 75 | l_index_finger2 |
| l_knee | 17 | yes | 1079 | l_thigh |
| l_middle_finger1 | 55 | yes | 204 | l_hand |
| l_middle_finger2 | 56 | yes | 132 | l_middle_finger1 |
| l_middle_finger3 | 57 | yes | 70 | l_middle_finger2 |
| l_pinky_finger1 | 49 | yes | 190 | l_hand |
| l_pinky_finger2 | 50 | yes | 139 | l_pinky_finger1 |
| l_pinky_finger3 | 51 | yes | 75 | l_pinky_finger2 |
| l_ring_finger1 | 52 | yes | 196 | l_hand |
| l_ring_finger2 | 53 | yes | 90 | l_ring_finger1 |
| l_ring_finger3 | 54 | yes | 74 | l_ring_finger2 |
| l_shoulder | 44 | yes | 702 | l_clavicle |
| l_shoulder_twist | 45 | yes | 2019 | l_shoulder |
| l_shoulderarmour | 43 | no | 0 | l_clavicle |
| l_thigh | 16 | yes | 1465 | hips |
| l_thumb_finger1 | 61 | yes | 175 | l_hand |
| l_thumb_finger2 | 62 | yes | 144 | l_thumb_finger1 |
| l_thumb_finger3 | 63 | yes | 123 | l_thumb_finger2 |
| l_toe | 20 | yes | 2 | l_ball |
| neck | 89 | yes | 1046 | chest |
| pistol | 11 | no | 0 | r_thigh |
| r_ball | 14 | yes | 83 | r_foot |
| r_clavicle | 65 | yes | 788 | chest |
| r_elbow | 69 | yes | 820 | r_shoulder |
| r_foot | 13 | yes | 441 | r_knee |
| r_hand | 71 | yes | 421 | r_elbow |
| r_hand_twist | 70 | yes | 281 | r_elbow |
| r_index_finger1 | 81 | yes | 175 | r_hand |
| r_index_finger2 | 82 | yes | 90 | r_index_finger1 |
| r_index_finger3 | 83 | yes | 72 | r_index_finger2 |
| r_knee | 12 | yes | 1072 | r_thigh |
| r_middle_finger1 | 78 | yes | 164 | r_hand |
| r_middle_finger2 | 79 | yes | 64 | r_middle_finger1 |
| r_middle_finger3 | 80 | yes | 53 | r_middle_finger2 |
| r_pinky_finger1 | 72 | yes | 184 | r_hand |
| r_pinky_finger2 | 73 | yes | 77 | r_pinky_finger1 |
| r_pinky_finger3 | 74 | yes | 67 | r_pinky_finger2 |
| r_ring_finger1 | 75 | yes | 192 | r_hand |
| r_ring_finger2 | 76 | yes | 64 | r_ring_finger1 |
| r_ring_finger3 | 77 | yes | 50 | r_ring_finger2 |
| r_shoulder | 67 | yes | 700 | r_clavicle |
| r_shoulder_twist | 68 | yes | 811 | r_shoulder |
| r_shoulderarmour | 66 | no | 0 | r_clavicle |
| r_thigh | 10 | yes | 640 | hips |
| r_thumb_finger1 | 86 | yes | 181 | r_hand |
| r_thumb_finger2 | 87 | yes | 168 | r_thumb_finger1 |
| r_thumb_finger3 | 88 | yes | 93 | r_thumb_finger2 |
| r_toe | 15 | yes | 2 | r_ball |
| root | 4 | yes | 34 |  |
| sling | 27 | yes | 4 | spine2 |
| spine1 | 21 | yes | 1006 | boss |
| spine2 | 26 | yes | 4731 | spine1 |
| StingrayEntityRoot | 0 | no | 0 |  |
| support | 31 | no | 0 | chest |
| support_mg | 30 | no | 0 | chest |
| target_designator | 24 | no | 0 | spine1 |
| weapon_aim | 85 | no | 0 | attach_hand_r |

## Ambiguous source matches

| Name | Transform | Active target influence | Lanes | Parent |
|---|---:|---|---:|---|
| — | — | — | — | none |

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
