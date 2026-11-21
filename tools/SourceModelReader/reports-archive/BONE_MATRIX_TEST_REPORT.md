# BodyGroup/Bone dependency matrix — 0.5.0-alpha

## Test input

Real `gmpublisher.gma` from the supplied addon ZIP; GMA v3, 82 members; model `models/lc_84/aes_yz_pm.mdl`; MDL v48 / VVD v4 / VTX v7; 255 MDL bone definition records; 20 BodyGroups.

## Comparison semantics

The all-default selection (every BodyGroup option 0) is measured first. Then each of the 42 model options is selected individually while all other BodyGroups remain at option 0. The matrix compares the unique MDL bone indices referenced by positive vertex weights against the all-default reference set. It does not classify the purpose of a bone and does not infer animation, procedural, jiggle-bone, physics, attachment, or engine-side dependencies.

## Summary

- MDL bone definition records: **255**
- Default selection weight-referenced bone indices: **136**
- BodyGroups: **20**
- Model options tested: **42**
- Default geometry: **106,663 triangles**, **64,301 referenced vertices**

## Selected option comparisons

| BodyGroup | Option | Model | Weight-referenced bones | Added | Removed | Triangles |
|---|---:|---|---:|---:|---:|---:|
| AES_YZ_PM (`0`) | 0 | `AES_T.smd` | 136 | 0 | 0 | 106,663 |
| 皮肤 (`1`) | 0 | `AES_PF_D.smd` | 136 | 0 | 0 | 106,663 |
| 皮肤 (`1`) | 1 | `AES_PF_X.smd` | 136 | 0 | 0 | 106,663 |
| 头发 (`2`) | 0 | `AES_TF1.smd` | 136 | 0 | 0 | 106,663 |
| 头发 (`2`) | 1 | `AES_TF2.smd` | 132 | 6 | 10 | 106,145 |
| 帽子 (`3`) | 0 | `AES_MZ1.smd` | 136 | 0 | 0 | 106,663 |
| 帽子 (`3`) | 1 | `AES_MZ2.smd` | 136 | 0 | 0 | 106,663 |
| 帽子 (`3`) | 2 | `model_2` | 136 | 0 | 0 | 103,725 |
| 眼镜(默认关) (`4`) | 0 | `model_0` | 136 | 0 | 0 | 106,663 |
| 眼镜(默认关) (`4`) | 1 | `AES_YJ.smd` | 136 | 0 | 0 | 108,449 |
| 兽耳 (`5`) | 0 | `AES_ED.smd` | 136 | 0 | 0 | 106,663 |
| 兽耳 (`5`) | 1 | `model_1` | 131 | 0 | 5 | 103,039 |
| 尾巴 (`6`) | 0 | `AES_WB.smd` | 136 | 0 | 0 | 106,663 |
| 尾巴 (`6`) | 1 | `model_1` | 124 | 0 | 12 | 104,672 |
| 玩偶 (`7`) | 0 | `AES_WO.smd` | 136 | 0 | 0 | 106,663 |
| 玩偶 (`7`) | 1 | `model_1` | 131 | 0 | 5 | 96,392 |
| 铃铛 (`8`) | 0 | `AES_BS.smd` | 136 | 0 | 0 | 106,663 |
| 铃铛 (`8`) | 1 | `model_1` | 132 | 0 | 4 | 103,979 |
| 手饰_右 (`9`) | 0 | `AES_SS_Y.smd` | 136 | 0 | 0 | 106,663 |
| 手饰_右 (`9`) | 1 | `model_1` | 136 | 0 | 0 | 90,371 |
| 手饰_左 (`10`) | 0 | `AES_SS_Z.smd` | 136 | 0 | 0 | 106,663 |
| 手饰_左 (`10`) | 1 | `model_1` | 134 | 0 | 2 | 105,580 |
| 白丝 (`11`) | 0 | `AES_SW.smd` | 136 | 0 | 0 | 106,663 |
| 白丝 (`11`) | 1 | `model_1` | 136 | 0 | 0 | 100,631 |
| 脚环_左 (`12`) | 0 | `AES_JH_Z.smd` | 136 | 0 | 0 | 106,663 |
| 脚环_左 (`12`) | 1 | `model_1` | 127 | 0 | 9 | 105,269 |
| 脚环_右 (`13`) | 0 | `AES_JH_Y.smd` | 136 | 0 | 0 | 106,663 |
| 脚环_右 (`13`) | 1 | `model_1` | 127 | 0 | 9 | 105,269 |
| 鞋子_左 (`14`) | 0 | `AES_XZ_Z.smd` | 136 | 0 | 0 | 106,663 |
| 鞋子_左 (`14`) | 1 | `model_1` | 136 | 0 | 0 | 104,891 |
| 鞋子_右 (`15`) | 0 | `AES_XZ_Y.smd` | 136 | 0 | 0 | 106,663 |
| 鞋子_右 (`15`) | 1 | `model_1` | 136 | 0 | 0 | 104,891 |
| 上衣 (`16`) | 0 | `AES_SY_D.smd` | 136 | 0 | 0 | 106,663 |
| 上衣 (`16`) | 1 | `AES_SY_X.smd` | 136 | 0 | 0 | 106,663 |
| 上衣 (`16`) | 2 | `model_2` | 133 | 0 | 3 | 99,256 |
| 泳装 (`17`) | 0 | `AES_YY_D.smd` | 136 | 0 | 0 | 106,663 |
| 泳装 (`17`) | 1 | `AES_YY_X.smd` | 136 | 0 | 0 | 106,663 |
| 泳装 (`17`) | 2 | `model_2` | 136 | 0 | 0 | 103,849 |
| 裙子(默认关) (`18`) | 0 | `model_0` | 136 | 0 | 0 | 106,663 |
| 裙子(默认关) (`18`) | 1 | `AES_QZ.smd` | 236 | 100 | 0 | 112,523 |
| 内裤(默认关) (`19`) | 0 | `model_0` | 136 | 0 | 0 | 106,663 |
| 内裤(默认关) (`19`) | 1 | `AES_NK1.smd` | 136 | 0 | 0 | 107,225 |

## Focused findings

### Glasses: BodyGroup 4, option 1 (`AES_YJ.smd`)

- Weight-referenced bone indices: 136
- Added vs all-default: 0
- Removed vs all-default: 0
- Geometry: 108,449 triangles / 65,435 referenced vertices

### Skirt: BodyGroup 18, option 1 (`AES_QZ.smd`)

- Weight-referenced bone indices: 236
- Added vs all-default: 100
- Removed vs all-default: 0
- Geometry: 112,523 triangles / 68,057 referenced vertices
- Added bone definitions:
  - `143` `Qun_0_6`
  - `144` `Qun_1_6`
  - `145` `Qun_2_6`
  - `146` `Qun_3_6`
  - `147` `Qun_4_6`
  - `148` `Qun_0_7`
  - `149` `Qun_1_7`
  - `150` `Qun_2_7`
  - `151` `Qun_3_7`
  - `152` `Qun_4_7`
  - `153` `Qun_0_8`
  - `154` `Qun_1_8`
  - `155` `Qun_2_8`
  - `156` `Qun_3_8`
  - `157` `Qun_4_8`
  - `158` `Qun_0_9`
  - `159` `Qun_1_9`
  - `160` `Qun_2_9`
  - `161` `Qun_3_9`
  - `162` `Qun_4_9`
  - `163` `Qun_0_10`
  - `164` `Qun_1_10`
  - `165` `Qun_2_10`
  - `166` `Qun_3_10`
  - `167` `Qun_4_10`
  - `168` `Qun_0_11`
  - `169` `Qun_1_11`
  - `170` `Qun_2_11`
  - `171` `Qun_3_11`
  - `172` `Qun_4_11`
  - `173` `Qun_0_12`
  - `174` `Qun_1_12`
  - `175` `Qun_2_12`
  - `176` `Qun_3_12`
  - `177` `Qun_4_12`
  - `178` `Qun_0_13`
  - `179` `Qun_1_13`
  - `180` `Qun_2_13`
  - `181` `Qun_3_13`
  - `182` `Qun_4_13`
  - `183` `Qun_0_14`
  - `184` `Qun_1_14`
  - `185` `Qun_2_14`
  - `186` `Qun_3_14`
  - `187` `Qun_4_14`
  - `190` `Qun_0_0`
  - `191` `Qun_1_0`
  - `192` `Qun_2_0`
  - `193` `Qun_3_0`
  - `194` `Qun_4_0`
  - `196` `Qun_0_1`
  - `197` `Qun_1_1`
  - `198` `Qun_2_1`
  - `199` `Qun_3_1`
  - `200` `Qun_4_1`
  - `202` `Qun_0_2`
  - `203` `Qun_1_2`
  - `204` `Qun_2_2`
  - `205` `Qun_3_2`
  - `206` `Qun_4_2`
  - `208` `Qun_0_3`
  - `209` `Qun_1_3`
  - `210` `Qun_2_3`
  - `211` `Qun_3_3`
  - `212` `Qun_4_3`
  - `214` `Qun_0_4`
  - `215` `Qun_1_4`
  - `216` `Qun_2_4`
  - `217` `Qun_3_4`
  - `218` `Qun_4_4`
  - `220` `Qun_0_5`
  - `221` `Qun_1_5`
  - `222` `Qun_2_5`
  - `223` `Qun_3_5`
  - `224` `Qun_4_5`
  - `226` `Qun_0_15`
  - `227` `Qun_1_15`
  - `228` `Qun_2_15`
  - `229` `Qun_3_15`
  - `230` `Qun_4_15`
  - `232` `Qun_0_16`
  - `233` `Qun_1_16`
  - `234` `Qun_2_16`
  - `235` `Qun_3_16`
  - `236` `Qun_4_16`
  - `238` `Qun_0_17`
  - `239` `Qun_1_17`
  - `240` `Qun_2_17`
  - `241` `Qun_3_17`
  - `242` `Qun_4_17`
  - `244` `Qun_0_18`
  - `245` `Qun_1_18`
  - `246` `Qun_2_18`
  - `247` `Qun_3_18`
  - `248` `Qun_4_18`
  - `250` `Qun_0_19`
  - `251` `Qun_1_19`
  - `252` `Qun_2_19`
  - `253` `Qun_3_19`
  - `254` `Qun_4_19`

## Other notable variant changes

Several alternative BodyGroup options reduce the set of bone indices referenced by the combined geometry because they replace a default model with a different mesh. For example, the alternate hair option reports +6 / -10; an alternate tail option reports +0 / -12. These are set differences in geometry weight references, not deletion recommendations.

## Result interpretation

The skirt option is the only tested option in this sample that adds a large new set of weight references: 100 added indices, all named in the `Qun_*` family. The glasses option adds no new weight-referenced bone indices. The MDL definition table remains 255 records in every case. This is consistent with the user's explanation that individual SMD source slices can carry the broader skeleton definition even when their triangles reference only a subset.

The matrix is a per-option isolation test. It does not test interactions between two or more non-default options at the same time; use `diagnose` with explicit `--bodygroup` selections for combined configurations.

## Runtime

The full 42-option matrix scan completed in approximately 16 seconds on the development environment. This command intentionally does more work than a single `diagnose` run because it rebuilds geometry for each model option.
