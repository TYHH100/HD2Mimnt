import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from dump_source_bone_rest import build_world_positions, chain_lengths  # noqa: E402


IDENTITY = (0.0, 0.0, 0.0, 1.0)


class SourceBoneRestTests(unittest.TestCase):
    def test_root_translation_is_used_directly(self):
        positions = build_world_positions([
            {"index": 0, "name": "root", "parent": -1, "pos": [0.0, -1.0, 38.6],
             "quat": IDENTITY},
        ])
        self.assertEqual(positions["root"], [0.0, -1.0, 38.6])

    def test_parent_rotation_applies_to_child_translation(self):
        # Ninety degrees about Z maps a local +X offset onto world +Y.
        positions = build_world_positions([
            {"index": 0, "name": "pelvis", "parent": -1, "pos": [1.0, 2.0, 3.0],
             "quat": (0.0, 0.0, 0.7071067811865476, 0.7071067811865476)},
            {"index": 1, "name": "child", "parent": 0, "pos": [5.0, 0.0, 0.0],
             "quat": IDENTITY},
        ])
        for axis, expected in enumerate([1.0, 7.0, 3.0]):
            self.assertAlmostEqual(positions["child"][axis], expected, places=9)

    def test_self_parented_bone_is_treated_as_root(self):
        positions = build_world_positions([
            {"index": 0, "name": "loop", "parent": 0, "pos": [2.0, 0.0, 0.0],
             "quat": IDENTITY},
        ])
        self.assertEqual(positions["loop"], [2.0, 0.0, 0.0])

    def test_missing_parent_leaves_bone_at_its_own_translation(self):
        positions = build_world_positions([
            {"index": 1, "name": "orphan", "parent": 42, "pos": [0.0, 0.0, 9.0],
             "quat": IDENTITY},
        ])
        self.assertEqual(positions["orphan"], [0.0, 0.0, 9.0])

    def test_chain_lengths_report_measured_and_missing_rows(self):
        positions = {
            "ValveBiped.Bip01_L_Thigh": [0.0, 0.0, 0.0],
            "ValveBiped.Bip01_L_Calf": [0.0, 0.0, -0.45],
        }
        rows = {row["chain"]: row for row in chain_lengths(positions)}
        measured = rows["thigh_left"]
        self.assertEqual(measured["status"], "measured")
        self.assertAlmostEqual(measured["stored_units"], 0.45, places=9)
        self.assertAlmostEqual(measured["stored_units_per_meter"], 1.0, places=9)
        self.assertEqual(rows["thigh_right"]["status"], "bone_not_found")


if __name__ == "__main__":
    unittest.main()
