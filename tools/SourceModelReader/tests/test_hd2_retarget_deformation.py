import contextlib
import io
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from research import verify_hd2_retarget_deformation as harness  # noqa: E402
from research.verify_hd2_retarget_deformation import (  # noqa: E402
    HD2_METERS_PER_SOURCE_UNIT,
    RIGID_TOLERANCE,
    adapter,
    blend,
    build_report,
    case_adapter_scale_and_orientation,
    case_adapter_maps_joint_deltas,
    case_double_application_refused,
    case_non_uniform_scale_gate,
    case_single_bone_rigid,
    case_translated_root,
    case_two_bone_blend,
    case_weight_sum_scale_invariance,
    distance,
    evaluate,
    identity,
    invert_affine,
    joint_pose,
    main,
    multiply,
    palette_for,
    render_markdown,
    rotate_x,
    rotate_z,
    transform_point,
    translate,
)

JOINT_A = [0.0, 0.0, 0.9417]
JOINT_B = [0.0, 0.0, 0.5163]


def _flat_close(left: list[float], right: list[float], tolerance: float = 1.0e-12) -> bool:
    return max(abs(left[k] - right[k]) for k in range(3)) <= tolerance


class HD2RetargetMatrixHelpersTests(unittest.TestCase):
    def test_multiply_applies_left_and_then_right(self):
        point = [1.0, 2.0, 3.0]
        shift = translate(0.5, -0.25, 1.0)
        spin = rotate_x(math.radians(90.0))

        shift_then_spin = transform_point(point, multiply(shift, spin))
        spin_then_shift = transform_point(point, multiply(spin, shift))

        self.assertTrue(_flat_close(shift_then_spin,
                                    transform_point(transform_point(point, shift), spin)))
        self.assertTrue(_flat_close(spin_then_shift,
                                    transform_point(transform_point(point, spin), shift)))
        self.assertGreater(distance(shift_then_spin, spin_then_shift), 0.1)

    def test_transform_point_reads_translation_from_floats_twelve_to_fourteen(self):
        self.assertEqual(transform_point([2.0, 3.0, 4.0], translate(10.0, 20.0, 30.0)),
                         [12.0, 23.0, 34.0])

    def test_invert_affine_undoes_a_rotated_translation(self):
        matrix = multiply(rotate_x(math.radians(37.0)), translate(-0.4, 1.25, 0.075))
        point = [0.31, -0.22, 0.09]
        moved = transform_point(point, matrix)
        restored = transform_point(moved, invert_affine(matrix))

        self.assertGreater(distance(moved, point), 1.0)
        self.assertTrue(_flat_close(restored, point))
        self.assertEqual([matrix[k] for k in (3, 7, 11, 15)], [0.0, 0.0, 0.0, 1.0])
        self.assertEqual([invert_affine(matrix)[k] for k in (3, 7, 11, 15)],
                         [0.0, 0.0, 0.0, 1.0])

    def test_invert_affine_matches_negative_translation_for_translation_only(self):
        matrix = translate(-0.28328, -0.04818, 1.43647)
        self.assertTrue(_flat_close(invert_affine(matrix)[12:15],
                                    [-matrix[12], -matrix[13], -matrix[14]]))

    def test_invert_affine_refuses_a_singular_rotation_part(self):
        degenerate = [1.0, 0.0, 0.0, 0.0,
                      2.0, 0.0, 0.0, 0.0,
                      0.0, 0.0, 1.0, 0.0,
                      0.0, 0.0, 0.0, 1.0]
        with self.assertRaises(ValueError):
            invert_affine(degenerate)

    def test_joint_pose_keeps_the_joint_but_translate_then_rotate_does_not(self):
        joint = [0.1, -0.2, 0.9417]
        angle = math.radians(45.0)
        posed = joint_pose(angle, joint)

        self.assertTrue(_flat_close(transform_point([0.0, 0.0, 0.0], posed), joint))
        wrong_order = transform_point([0.0, 0.0, 0.0], multiply(translate(*joint),
                                                                rotate_x(angle)))
        self.assertGreater(distance(wrong_order, joint), 0.3)
        self.assertTrue(_flat_close(wrong_order, transform_point(joint, rotate_x(angle))))

    def test_palette_then_bone_world_reproduces_mesh_world(self):
        mesh_world = multiply(rotate_z(math.radians(12.0)),
                              translate(-0.28328, -0.04818, 1.43647))
        bone_world = multiply(rotate_x(math.radians(31.0)), translate(0.11, 0.02, 1.4051))
        palette = palette_for(mesh_world, bone_world)
        point = [0.03, -0.017, 0.021]

        self.assertTrue(_flat_close(transform_point(transform_point(point, palette), bone_world),
                                    transform_point(point, mesh_world)))
        self.assertFalse(_flat_close(transform_point(point, palette),
                                     transform_point(point, mesh_world)))


class HD2RetargetBlendTests(unittest.TestCase):
    def setUp(self):
        self.palettes = [palette_for(identity(), translate(*JOINT_A)),
                         palette_for(identity(), translate(*JOINT_B))]

    def test_rest_pose_reproduces_the_stored_position(self):
        vertex = [0.05, 0.02, -0.20]
        rest = blend(vertex, [self.palettes[0]], [translate(*JOINT_A)], [1.0])
        self.assertTrue(_flat_close(rest, vertex, RIGID_TOLERANCE))

    def test_blend_refuses_weights_that_sum_to_zero(self):
        vertex = [0.02, -0.01, 0.75]
        with self.assertRaises(ValueError):
            blend(vertex, self.palettes, [translate(*JOINT_A), translate(*JOINT_B)], [0.0, 0.0])

    def test_uniform_weight_scaling_does_not_move_the_vertex(self):
        vertex = [0.02, -0.01, 0.75]
        poses = [joint_pose(math.radians(20.0), JOINT_A), translate(*JOINT_B)]
        reference = blend(vertex, self.palettes, poses, [0.7, 0.3])
        scaled = blend(vertex, self.palettes, poses, [2.1, 0.9])
        self.assertTrue(_flat_close(reference, scaled, RIGID_TOLERANCE))

    def test_swap_of_the_palette_order_breaks_the_rest_invariant(self):
        mesh_world = multiply(rotate_z(math.radians(15.0)),
                              translate(-0.28328, -0.04818, 1.43647))
        bone_world = multiply(rotate_x(math.radians(25.0)), translate(0.0, 0.0, 1.4051))
        vertex = [0.03, 0.01, 0.02]
        swapped = multiply(invert_affine(bone_world), mesh_world)

        got = blend(vertex, [swapped], [bone_world], [1.0])
        self.assertGreater(distance(got, transform_point(vertex, mesh_world)), 1.0e-3)


class HD2RetargetCaseTests(unittest.TestCase):
    def test_every_case_exposes_only_boolean_checks(self):
        rows = [case() for case in harness.CASES]
        self.assertEqual(len(rows), len(harness.CASES))
        for row in rows:
            self.assertEqual(set(row), {"case", "checks", "measures"})
            self.assertTrue(row["checks"], row["case"])
            for name, value in row["checks"].items():
                self.assertIsInstance(value, bool, "{}/{}".format(row["case"], name))
                self.assertNotIn(name, row["measures"])

    def test_evaluate_passes_on_the_shipped_cases(self):
        report = evaluate()
        self.assertEqual(report["failing"], [])
        self.assertTrue(report["all_passed"])

    def test_evaluate_reports_the_case_and_check_that_failed(self):
        original = harness.CASES

        def broken() -> dict:
            row = case_single_bone_rigid()
            row["checks"]["joint_stays_fixed"] = False
            return row

        harness.CASES = (broken,)
        try:
            report = evaluate()
        finally:
            harness.CASES = original
        self.assertFalse(report["all_passed"])
        self.assertEqual(report["failing"], ["single_bone_rigid_rotation/joint_stays_fixed"])

    def test_single_bone_case_matches_a_rigid_rotation_about_the_joint(self):
        row = case_single_bone_rigid()
        self.assertEqual(row["measures"]["expected_minus_blended"], 0.0)
        self.assertAlmostEqual(row["measures"]["vertex_to_joint"],
                               distance(row["measures"]["vertex"], row["measures"]["joint"]),
                               places=6)
        self.assertGreater(row["measures"]["displacement_from_rest"], 0.0)

    def test_two_bone_sweep_endpoints_and_monotonic_displacement(self):
        row = case_two_bone_blend()
        samples = row["measures"]["samples"]
        self.assertEqual(len(samples), 11)
        self.assertEqual(samples[0]["weight_on_moving_bone"], 0.0)
        self.assertEqual(samples[0]["distance_from_stored"], 0.0)
        self.assertEqual(samples[-1]["weight_on_moving_bone"], 1.0)
        displacements = [sample["distance_from_stored"] for sample in samples]
        self.assertEqual(displacements, sorted(displacements))
        rest_radius = row["measures"]["vertex_to_upper_joint"]
        radii = [sample["distance_to_upper_joint"] for sample in samples]
        self.assertAlmostEqual(radii[0], rest_radius, places=6)
        self.assertAlmostEqual(radii[-1], rest_radius, places=6)
        self.assertLess(min(radii), rest_radius)

    def test_weight_sum_case_is_scale_invariant_and_rejects_zero(self):
        row = case_weight_sum_scale_invariance()
        self.assertEqual(row["measures"]["sum_two_minus_normalized"], [0.0, 0.0, 0.0])
        self.assertTrue(row["checks"]["all_zero_weights_refused"])
        self.assertTrue(row["checks"]["more_weight_on_the_static_bone_moves_it"])

    def test_translated_root_moves_the_vertex_by_the_joint_offset(self):
        row = case_translated_root()
        self.assertEqual(row["measures"]["vertex_offset"], row["measures"]["joint_offset"])
        self.assertAlmostEqual(row["measures"]["offset_magnitude"],
                               math.sqrt(0.4 ** 2 + 0.05 ** 2 + 0.6 ** 2), places=6)

    def test_double_application_and_palette_swap_report_larger_errors(self):
        row = case_double_application_refused()
        self.assertEqual(row["measures"]["single_application_error"], 0.0)
        self.assertGreater(row["measures"]["double_application_error"], 1.0)
        self.assertGreater(row["measures"]["palette_order_swap_error"], 1.0e-3)

    def test_non_uniform_scale_gate_refuses_rotation_composition(self):
        row = case_non_uniform_scale_gate()
        self.assertEqual(row["measures"]["scale_ratio"], 2.0)
        self.assertEqual(row["measures"]["uniform_reference_scale_ratio"], 1.0)
        self.assertTrue(row["checks"]["rotation_with_non_uniform_scale_refused"])
        self.assertTrue(row["checks"]["pure_uniform_scale_not_misread_as_rotation"])

    def test_adapter_orientation_case_is_a_rotation_and_not_a_mirror(self):
        row = case_adapter_scale_and_orientation()
        self.assertEqual(row["measures"]["linear_determinant"], 1.0)
        left = row["measures"]["adapted_left_hand"]
        right = row["measures"]["adapted_right_hand"]
        self.assertAlmostEqual(left[0], -right[0], places=6)
        self.assertLess(left[0], right[0])
        self.assertEqual(adapter([1.0, 0.0, 0.0])[0], -HD2_METERS_PER_SOURCE_UNIT)

    def test_adapter_maps_deltas_and_is_translation_equivariant(self):
        row = case_adapter_maps_joint_deltas()
        measures = row["measures"]
        self.assertAlmostEqual(measures["adapted_delta_length_meters"]
                               / measures["source_delta_length"],
                               HD2_METERS_PER_SOURCE_UNIT, places=6)
        self.assertTrue(row["checks"]["delta_mapping_is_linear"])
        self.assertTrue(row["checks"]["shifting_the_source_model_does_not_change_deltas"])
        self.assertNotEqual(measures["absolute_adapted_hand_vs_other_model_landmark"], 0.0)


class HD2RetargetReportTests(unittest.TestCase):
    def test_report_shape_and_assumptions(self):
        report = build_report()
        self.assertEqual(report["schema"], "SourceModelReader-HD2RetargetDeformationChecks1")
        self.assertEqual(report["assumptions"]["matrix_convention"],
                         "row_major_4x4_row_vector")
        self.assertEqual(report["assumptions"]["palette_formula_candidate"],
                         "mesh_world_times_inverse_bone_world")
        self.assertEqual(report["assumptions"]["bone_pose_construction"],
                         "rotate_about_bone_origin_then_translate_to_joint")
        self.assertTrue(report["checks"]["all_passed"])
        self.assertTrue(any("translate-then-rotate" in item
                            for item in report["interpretation_limits"]))

    def test_markdown_renders_every_case_and_the_sample_table(self):
        report = build_report()
        markdown = render_markdown(report)
        self.assertIn("# HD2 Retarget Deformation Checks (Synthetic)", markdown)
        self.assertIn("Interpretation limits", markdown)
        for row in report["checks"]["results"]:
            self.assertIn("## {}".format(row["case"]), markdown)
        self.assertIn(
            "| weight_on_moving_bone | distance_from_stored | distance_to_upper_joint |",
            markdown)
        self.assertNotIn("FAIL", markdown)

    def test_cli_writes_both_outputs_and_returns_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            json_path = Path(tmp) / "checks.json"
            markdown_path = Path(tmp) / "checks.md"
            argv = [sys.argv[0], "--output-json", str(json_path),
                    "--output-markdown", str(markdown_path)]
            with contextlib.redirect_stdout(io.StringIO()):
                code = _run_main(argv)
            self.assertEqual(code, 0)
            payload = json.loads(json_path.read_text(encoding="utf-8"))
            self.assertTrue(payload["checks"]["all_passed"])
            self.assertIn("## two_bone_blend_sweep",
                          markdown_path.read_text(encoding="utf-8"))

    def test_cli_returns_one_when_a_check_fails(self):
        original = harness.evaluate
        harness.evaluate = lambda: {"results": [], "failing": ["case/check"], "all_passed": False}
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                code = _run_main([sys.argv[0]])
        finally:
            harness.evaluate = original
        self.assertEqual(code, 1)


def _run_main(argv: list[str]) -> int:
    previous = sys.argv
    sys.argv = argv
    try:
        return main()
    finally:
        sys.argv = previous


if __name__ == "__main__":
    unittest.main()
