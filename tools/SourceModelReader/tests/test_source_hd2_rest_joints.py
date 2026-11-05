import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from compare_source_hd2_rest_joints import (  # noqa: E402
    _invert3,
    build_rows,
    chirality_comparison,
    disposition,
    hypothesis_residuals,
    least_squares_map,
    paired_lengths,
)


def candidate(source, target, parent=None, target_parent=None, status="not_checked",
              lanes=10):
    return {
        "source_name": source, "target_name": target,
        "source_parent_name": parent, "target_parent_name": target_parent,
        "parent_status": status, "source_active_vertex_influence": True,
        "target_active_vertex_influence_lane_count": lanes, "approval": "advisory_only",
    }


class SourceHD2RestJointTests(unittest.TestCase):
    # Source skeleton: pelvis at the origin, thigh below it. HD2 mirrors that with a
    # 0.5 scale and the left/right and front/back axes both negated.
    SOURCE = {
        "ValveBiped.Bip01_Pelvis": [0.0, 0.0, 0.0],
        "ValveBiped.Bip01_L_Thigh": [2.0, 4.0, -8.0],
    }
    TARGET = {"hips": [0.0, 0.0, 0.0], "l_thigh": [-1.0, -2.0, -4.0]}
    ROWS = [
        candidate("ValveBiped.Bip01_Pelvis", "hips"),
        candidate("ValveBiped.Bip01_L_Thigh", "l_thigh",
                  parent="ValveBiped.Bip01_Pelvis", target_parent="hips", status="match"),
    ]

    def test_scale_comes_from_paired_bone_lengths(self):
        segments = paired_lengths(self.SOURCE, self.TARGET, self.ROWS)
        self.assertEqual(len(segments), 1)
        row = segments[0]
        self.assertAlmostEqual(row["source_length"], (2.0 * 2.0 + 4.0 * 4.0 + 8.0 * 8.0) ** 0.5)
        self.assertAlmostEqual(row["hd2_meters_per_source_unit"], 0.5)
        self.assertEqual(row["segment"], "ValveBiped.Bip01_Pelvis -> ValveBiped.Bip01_L_Thigh")

    def test_segment_needs_both_endpoints_mapped(self):
        orphan = [self.ROWS[0], candidate("ValveBiped.Bip01_L_Forearm", "l_forearm",
                                          parent="ValveBiped.Bip01_L_UpperArm")]
        self.assertEqual(paired_lengths(self.SOURCE, self.TARGET, orphan), [])

    def test_residuals_pick_the_rotation_that_matches_the_data(self):
        residuals = hypothesis_residuals(self.SOURCE, self.TARGET, self.ROWS, 0.5)
        # The fixture is exactly a 180 degree rotation about Z at scale 0.5.
        self.assertAlmostEqual(residuals["rot180_z"]["mean_residual"], 0.0, places=9)
        self.assertGreater(residuals["mirror_x"]["mean_residual"], 0.9)
        self.assertGreater(residuals["scale_only"]["mean_residual"], 0.9)
        self.assertEqual(residuals["rot180_z"]["joint_count"], 2)
        self.assertEqual(residuals["rot180_z"]["root_offset"], [0.0, 0.0, 0.0])

    def test_disposition_keeps_advisory_status_and_flags_structure(self):
        self.assertEqual(disposition(self.ROWS[0], 0.02), "candidate with close rest joint")
        self.assertEqual(disposition(candidate("a", "b", status="mismatch"), 0.01),
                         "candidate with structural mismatch")
        self.assertEqual(disposition(candidate("a", "b"), None), "candidate without rest evidence")
        self.assertEqual(disposition(candidate("a", "b"), 0.9), "candidate with far rest joint")
        self.assertEqual(disposition({"approval": "approved"}, 0.01), "needs review")

    def test_rows_keep_missing_joints_and_record_dispositions(self):
        rows = build_rows(self.SOURCE, self.TARGET, self.ROWS + [candidate("ghost_bone", "hips")],
                          0.5, hypothesis_residuals(self.SOURCE, self.TARGET, self.ROWS, 0.5))
        self.assertEqual(len(rows), 3)
        ghost = rows[2]
        self.assertIsNone(ghost["source_rest"])
        self.assertEqual(ghost["disposition"], "candidate without rest evidence")
        self.assertEqual(ghost["approval"], "advisory_only")
        self.assertEqual(rows[0]["disposition"], "candidate with close rest joint")
        self.assertEqual(rows[1]["target_name"], "l_thigh")


class OrientationFitTests(unittest.TestCase):
    SOURCE = {
        "j0": [0.0, 0.0, 0.0],
        "j1": [2.0, 1.0, 3.0],
        "j2": [-1.5, 2.5, 1.0],
        "j3": [1.0, -2.0, 2.0],
        "j4": [0.5, 1.5, -2.5],
        "j5": [-2.0, -1.0, 1.5],
        "j6": [1.5, 0.5, 4.0],
        "j7": [-0.5, 3.0, -1.0],
    }
    ROWS = [candidate(name, name.replace("j", "h_")) for name in SOURCE]

    def target(self, signs, scale=0.5, offset=(1.0, 2.0, 3.0)):
        return {
            name.replace("j", "h_"): [signs[axis] * scale * value[axis] + offset[axis]
                                      for axis in range(3)]
            for name, value in self.SOURCE.items()
        }

    def test_rotation_fit_keeps_orientation_positive(self):
        fit = least_squares_map(self.SOURCE, self.target((-1.0, -1.0, 1.0)), self.ROWS)
        self.assertIsNotNone(fit)
        self.assertEqual(fit["joint_pairs"], 8)
        self.assertGreater(fit["determinant"], 0.0)
        self.assertEqual(fit["orientation"], "preserved (rotation plus scale)")
        self.assertLess(fit["fit_rms_meters"], 1.0e-9)
        self.assertGreater(fit["normalized_determinant"], 0.9)

    def test_single_axis_flip_is_detected_as_a_mirror(self):
        fit = least_squares_map(self.SOURCE, self.target((-1.0, 1.0, 1.0)), self.ROWS)
        self.assertLess(fit["determinant"], 0.0)
        self.assertEqual(fit["orientation"], "reversed (mirror)")

    def test_fit_needs_enough_pairs(self):
        short = self.ROWS[:5]
        positions = {name: self.SOURCE[name] for name in ("j0", "j1", "j2", "j3", "j4")}
        self.assertIsNone(least_squares_map(positions, self.target((-1.0, -1.0, 1.0)), short))

    def test_singular_spread_has_no_inverse(self):
        flat = [[1.0, 0.0, 0.0], [2.0, 0.0, 0.0], [3.0, 0.0, 0.0]]
        self.assertIsNone(_invert3(flat))
        ok = [[2.0, 0.0, 0.0], [0.0, 3.0, 0.0], [0.0, 0.0, 4.0]]
        inverse = _invert3(ok)
        self.assertAlmostEqual(inverse[0][0], 0.5)
        self.assertAlmostEqual(inverse[2][2], 0.25)

    TRIPLE = (("j1", "j2", "j3", "j0"), ("j5", "j4", "j6", "j0"))

    SOURCE_TRIPLE = (("j1", "j2", "j3", "j0"), ("j5", "j4", "j6", "j0"))
    TARGET_TRIPLE = (("h_1", "h_2", "h_3", "h_0"), ("h_5", "h_4", "h_6", "h_0"))

    def test_chirality_survey_sees_even_parity_under_two_negations(self):
        result = chirality_comparison(self.SOURCE, self.target((-1.0, -1.0, 1.0)),
                                      self.SOURCE_TRIPLE, self.TARGET_TRIPLE)
        self.assertEqual(result["usable_pair_count"], 2)
        self.assertEqual(result["same_sign_pairs"], 2)
        self.assertTrue(result["chirality_agrees"])
        self.assertEqual(result["required_negation_parity"], "even (rotation)")

    def test_chirality_survey_sees_odd_parity_under_one_negation(self):
        result = chirality_comparison(self.SOURCE, self.target((-1.0, 1.0, 1.0)),
                                      self.SOURCE_TRIPLE, self.TARGET_TRIPLE)
        self.assertEqual(result["usable_pair_count"], 2)
        self.assertEqual(result["opposite_sign_pairs"], 2)
        self.assertTrue(result["chirality_disagrees"])
        self.assertEqual(result["required_negation_parity"], "odd (mirror)")

    def test_thin_frames_are_gated_instead_of_voting(self):
        # Near-collinear joints carry no sign information and must be dropped
        # rather than counted, or the parity answer depends on the gate value.
        flat = {"j0": [0.0, 0.0, 0.0], "j1": [1.0, 0.0, 0.0],
                "j2": [2.0, 0.0, 0.0], "j3": [3.0, 0.0, 0.0]}
        triple = (("j1", "j2", "j3", "j0"),)
        result = chirality_comparison(flat, flat, triple, triple)
        self.assertEqual(result["usable_pair_count"], 0)
        self.assertEqual(result["required_negation_parity"], "undetermined")
        self.assertTrue(all(row["degenerate"] for row in result["source_frames"]))


if __name__ == "__main__":
    unittest.main()
