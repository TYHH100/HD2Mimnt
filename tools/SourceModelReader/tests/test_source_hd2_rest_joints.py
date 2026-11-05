import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from compare_source_hd2_rest_joints import (  # noqa: E402
    build_rows,
    disposition,
    hypothesis_residuals,
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


if __name__ == "__main__":
    unittest.main()
