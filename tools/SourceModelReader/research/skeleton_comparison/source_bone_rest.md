# Source Bone Rest Measurements

- Model: models/lc_84/aes_yz_pm.mdl
- Bone definitions: 255
- Chain ratio median (stored units per meter): 33.7831
- Ratio range: 5.1980 to 43.7502

| Chain | From | To | Stored length | Reference meters | Units per meter |
|---|---|---|---:|---:|---:|
| pelvis_to_head | ValveBiped.Bip01_Pelvis | ValveBiped.Bip01_Head1 | 22.1894 | 0.60 | 36.9823 |
| pelvis_to_spine | ValveBiped.Bip01_Pelvis | ValveBiped.Bip01_Spine | 0.6238 | 0.12 | 5.1980 |
| spine_chain | ValveBiped.Bip01_Spine | ValveBiped.Bip01_Spine4 | 13.8314 | 0.42 | 32.9320 |
| thigh_left | ValveBiped.Bip01_L_Thigh | ValveBiped.Bip01_L_Calf | 14.7755 | 0.45 | 32.8345 |
| calf_left | ValveBiped.Bip01_L_Calf | ValveBiped.Bip01_L_Foot | 18.3751 | 0.42 | 43.7502 |
| thigh_right | ValveBiped.Bip01_R_Thigh | ValveBiped.Bip01_R_Calf | 14.7755 | 0.45 | 32.8346 |
| upper_arm_left | ValveBiped.Bip01_L_UpperArm | ValveBiped.Bip01_L_Forearm | 10.1350 | 0.30 | 33.7832 |
| forearm_left | ValveBiped.Bip01_L_Forearm | ValveBiped.Bip01_L_Hand | 9.4142 | 0.26 | 36.2086 |
| upper_arm_right | ValveBiped.Bip01_R_UpperArm | ValveBiped.Bip01_R_Forearm | 10.1349 | 0.30 | 33.7831 |

## Rest position extent (relative to the skeleton root)

- X: min -25.6943, max 25.6943, size 51.3885
- Y: min -7.3181, max 16.7789, size 24.0969
- Z: min 3.7497, max 69.5191, size 65.7693

## Interpretation limits

- Segment lengths are compared against loose adult reference ranges, so the derived ratio is a scale indication, not a calibrated unit proof.
- Rest positions come from composing parent-relative pos and quat; the reader does not apply axis conversion flags beyond what the MDL already stores.
- Nothing here converts, rescales or rewrites Source geometry; it only measures it.
