# HD2 Retarget Deformation Checks (Synthetic)

- Matrix convention: row_major_4x4_row_vector
- Palette candidate: `mesh_world_times_inverse_bone_world`
- Bone pose construction: `rotate_about_bone_origin_then_translate_to_joint`
- Adapter: scale 0.030524 with signs [-1.0, -1.0, 1.0]
- All checks passed: **True**
- Failing checks: none

Every case is built from synthetic vertices plus the measured rest joints. No HD2 Unit payload is decoded and no weight is transferred.

## single_bone_rigid_rotation

| Check | Result |
|---|:---|
| rest_reproduces_stored_position | pass |
| joint_stays_fixed | pass |
| matches_rigid_rotation_about_joint | pass |
| vertex_to_joint_distance_preserved | pass |

- joint: [0.0, 0.0, 0.9417]
- vertex: [0.05, 0.02, -0.2]
- vertex_to_joint: 1.142969
- displacement_from_rest: 0.591078
- expected_minus_blended: 0.0

## two_bone_blend_sweep

| Check | Result |
|---|:---|
| weight_one_equals_rigid_rotation_about_joint | pass |
| weight_zero_equals_stored_position | pass |
| rigid_side_keeps_joint_distance | pass |
| displacement_grows_with_moving_weight | pass |
| blend_volume_loss_is_recorded | pass |

- vertex_to_upper_joint: 0.2417
- vertex_to_lower_joint: 0.1837
- smallest_distance_to_upper_joint: 0.223302
- largest_distance_from_stored: 0.184989
- samples:

| weight_on_moving_bone | distance_from_stored | distance_to_upper_joint |
|---:|---:|---:|
| 0.0 | 0.0 | 0.2417 |
| 0.1 | 0.018499 | 0.235242 |
| 0.2 | 0.036998 | 0.230095 |
| 0.3 | 0.055497 | 0.226346 |
| 0.4 | 0.073996 | 0.224067 |
| 0.5 | 0.092495 | 0.223302 |
| 0.6 | 0.110994 | 0.224067 |
| 0.7 | 0.129492 | 0.226346 |
| 0.8 | 0.147991 | 0.230095 |
| 0.9 | 0.16649 | 0.235242 |
| 1.0 | 0.184989 | 0.2417 |


## weight_sum_scale_invariance

| Check | Result |
|---|:---|
| sum_one_equals_sum_two | pass |
| sum_one_equals_sum_half | pass |
| sum_one_equals_sum_hundred | pass |
| all_zero_weights_refused | pass |
| more_weight_on_the_static_bone_moves_it | pass |

- normalized_result: [0.02, 0.036318, 0.755699]
- sum_two_minus_normalized: [0.0, 0.0, 0.0]
- equal_weight_result: [0.02, 0.023084, 0.75407]

## translated_root

| Check | Result |
|---|:---|
| rest_reproduces_stored_position | pass |
| translation_applied_once | pass |
| offset_magnitude_matches_joint_move | pass |

- vertex_offset: [0.4, 0.05, 0.6]
- joint_offset: [0.4, 0.05, 0.6]
- offset_magnitude: 0.722842

## double_application_refused

| Check | Result |
|---|:---|
| single_application_matches_mesh_world | pass |
| double_application_rejected | pass |
| palette_order_swap_rejected | pass |

- expected_position: [-0.25689, -0.030756, 1.45647]
- single_application_error: 0.0
- double_application_error: 1.465405
- palette_order_swap_error: 0.621278

## non_uniform_scale_gate

| Check | Result |
|---|:---|
| rotation_with_non_uniform_scale_refused | pass |
| pure_uniform_scale_not_misread_as_rotation | pass |
| rotation_detected_only_after_row_normalization | pass |

- axis_scales: [2.0, 1.0, 2.0]
- scale_ratio: 2.0
- off_diagonal_after_normalization: 1.0
- uniform_reference_scale_ratio: 1.0

## adapter_scale_and_orientation

| Check | Result |
|---|:---|
| adapter_is_rotation_not_mirror | pass |
| left_stays_left_of_right | pass |
| scale_is_isotropic | pass |
| height_order_preserved | pass |

- adapted_left_hand: [-0.125612, -0.028458, 1.677132]
- adapted_right_hand: [0.125612, -0.028458, 1.677129]
- adapted_hip: [-0.0, -0.027472, 1.648296]
- linear_determinant: 1.0
- signs: [-1.0, -1.0, 1.0]
- scale: 0.030524

## adapter_maps_joint_deltas

| Check | Result |
|---|:---|
| delta_mapping_is_linear | pass |
| shifting_the_source_model_does_not_change_deltas | pass |
| delta_length_uses_the_measured_ratio | pass |

- source_delta: [1.6152, -0.0677, -1.0553]
- adapted_delta: [-0.049302, 0.002066, -0.032212]
- source_delta_length: 1.930573
- adapted_delta_length_meters: 0.058929
- hd2_reference_hand_landmark: [-0.6566, 0.1401, 1.2189]
- absolute_adapted_hand: [-0.125612, -0.028458, 1.677132]
- absolute_adapted_hand_vs_other_model_landmark: 0.721343

## Interpretation limits

- These are algebraic invariants of the measured conventions, not observations of the game runtime.
- Passing means no transform is dropped or applied twice in the modelled cases; it does not prove the shader order.
- The non-uniform-scale-with-rotation composition is refused rather than modelled, so it stays unverified.
- Joint positions reuse the measured HD2 rest heights only as plausible pivots; no HD2 Unit geometry is decoded here.
- The weight-sum case normalizes by the sum, which is the term the earlier residual analysis showed a non-normalizing blend biases by (sum-1) times the static target.
- Blend volume loss inside a two-bone sweep is recorded, not corrected; this harness does not claim length preservation mid-blend.
- A bone pose built as translate-then-rotate swings the joint around the parent origin; this harness uses rotate-then-translate and checks that the joint does not move.
- The adapter is a delta map: its ratio was measured from paired bone-to-bone lengths, so adapted absolute coordinates are reported for scale only and are not compared against another model's landmarks.
