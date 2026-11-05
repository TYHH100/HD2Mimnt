import json
import tempfile
import unittest
from pathlib import Path

from research.compare_hd2_unit_transforms import (
    build_report,
    compare_pair,
    load_unit,
    render_markdown,
)


def make_unit(label, transforms):
    records = []
    for row in transforms:
        records.append({
            "index": row["index"],
            "name": row["name"],
            "name_ambiguous": row.get("name_ambiguous", False),
            "name_hash": row.get("name_hash", row["index"] + 100),
            "parent_name": row.get("parent_name"),
            "parent_resolved": row.get("parent_resolved", True),
            "parent_index_raw": row.get("parent_index_raw", 0xFFFF),
            "active": row.get("active", True),
            "active_lane_count": row.get("active_lane_count", 1),
            "position": row.get("position", [0.0, 0.0, 0.0]),
            "rotation_3x3": row.get("rotation_3x3", [
                1.0, 0.0, 0.0,
                0.0, 1.0, 0.0,
                0.0, 0.0, 1.0,
            ]),
            "scale": row.get("scale", [1.0, 1.0, 1.0]),
            "matrix_4x4_raw": row.get("matrix_4x4_raw", [
                1.0, 0.0, 0.0, 0.0,
                0.0, 1.0, 0.0, 0.0,
                0.0, 0.0, 1.0, 0.0,
                0.0, 0.0, 0.0, 1.0,
            ]),
        })
    return {
        "label": label,
        "input": label + ".json",
        "unit_id": label + "-unit",
        "package_id": label + "-package",
        "geometry_source": "Unit",
        "summary": {},
        "records": records,
    }


class HD2UnitTransformComparisonTests(unittest.TestCase):
    def test_matches_same_named_transforms_and_parent_relationship(self):
        left = make_unit("left", [
            {"index": 0, "name": "hips"},
            {"index": 1, "name": "spine1", "parent_name": "hips",
             "parent_index_raw": 0},
        ])
        right = make_unit("right", [
            {"index": 3, "name": "hips"},
            {"index": 4, "name": "spine1", "parent_name": "hips",
             "parent_index_raw": 3},
        ])
        pair = compare_pair(left, right, 1.0e-4)
        self.assertEqual(pair["summary"]["shared_unique_named_transform_count"], 2)
        self.assertEqual(pair["summary"]["parent_mismatch_count"], 0)
        self.assertEqual(pair["summary"]["stored_matrix_mismatch_count"], 0)
        self.assertEqual(pair["summary"]["position_mismatch_count"], 0)

    def test_reports_parent_and_matrix_mismatches(self):
        left = make_unit("left", [
            {"index": 0, "name": "hips"},
            {"index": 1, "name": "spine1", "parent_name": "hips",
             "parent_index_raw": 0},
        ])
        changed_matrix = [
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            2.0, 0.0, 0.0, 1.0,
        ]
        right = make_unit("right", [
            {"index": 0, "name": "hips"},
            {"index": 1, "name": "spine1", "parent_name": "boss",
             "parent_index_raw": 2, "matrix_4x4_raw": changed_matrix},
            {"index": 2, "name": "boss"},
        ])
        pair = compare_pair(left, right, 1.0e-4)
        spine = next(row for row in pair["transforms"] if row["name"] == "spine1")
        self.assertEqual(spine["parent_status"], "mismatch")
        self.assertEqual(spine["stored_matrix_status"], "mismatch")
        self.assertEqual(pair["summary"]["parent_mismatch_count"], 1)
        self.assertEqual(pair["summary"]["stored_matrix_mismatch_count"], 1)

    def test_excludes_duplicate_and_unresolved_names_from_matching(self):
        left = make_unit("left", [
            {"index": 0, "name": "hips"},
            {"index": 1, "name": "duplicate"},
            {"index": 2, "name": "duplicate"},
        ])
        right = make_unit("right", [
            {"index": 0, "name": "hips"},
            {"index": 1, "name": "other"},
        ])
        left["records"].append({
            **left["records"][0],
            "index": 3,
            "name": None,
            "name_ambiguous": True,
        })
        pair = compare_pair(left, right, 1.0e-4)
        self.assertEqual(pair["summary"]["shared_unique_named_transform_count"], 1)
        self.assertEqual(pair["ambiguous_names"]["left"], ["duplicate"])

    def test_builds_three_pairwise_reports_and_triple_intersection(self):
        units = [
            make_unit("lower", [{"index": 0, "name": "hips"}]),
            make_unit("torso", [{"index": 4, "name": "hips"}]),
            make_unit("avatar", [{"index": 9, "name": "hips"}]),
        ]
        report = build_report(units, 1.0e-4)
        self.assertEqual(len(report["pairwise_comparisons"]), 3)
        self.assertEqual(report["common_to_all_three"]["count"], 1)
        self.assertEqual(report["summary"]["status"], "ok")
        self.assertIn("Interpretation limits", render_markdown(report))

    def test_flags_pair_with_no_shared_unique_names(self):
        units = [
            make_unit("lower", [{"index": 0, "name": "l_thigh"}]),
            make_unit("torso", [{"index": 0, "name": "chest"}]),
            make_unit("avatar", [{"index": 0, "name": "hips"}]),
        ]
        report = build_report(units, 1.0e-4)
        lower_torso = report["pairwise_comparisons"]["lower__vs__torso"]
        self.assertEqual(
            lower_torso["summary"]["comparison_status"],
            "no_shared_unique_names",
        )
        self.assertEqual(report["summary"]["pairwise_no_overlap"], 2)
        self.assertEqual(report["summary"]["status"], "warning")

    def test_loads_only_transform_metadata_from_hd2_unit_json(self):
        fixture = {
            "unit": {"file_id": "unit-1", "geometry_source": "CompositeUnit"},
            "package": {"name": "package-1"},
            "summary": {"status": "ok"},
            "transform_info": {
                "transforms": [{
                    "index": 0,
                    "name_hash": 123,
                    "parent_index_raw": 65535,
                    "position": [1, 2, 3],
                    "rotation_3x3": [
                        1, 0, 0, 0, 1, 0, 0, 0, 1,
                    ],
                    "scale": [1, 1, 1],
                    "matrix_4x4_raw": [
                        1, 0, 0, 0, 0, 1, 0, 0,
                        0, 0, 1, 0, 1, 2, 3, 1,
                    ],
                }],
                "resolved_skeleton": [{
                    "transform_index": 0,
                    "name": "hips",
                    "name_ambiguous": False,
                    "parent_name": None,
                    "has_active_vertex_influence": True,
                    "active_vertex_influence_lane_count": 7,
                }],
            },
            "geometry": [{"large": "payload omitted from compact result"}],
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unit.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            unit = load_unit(path, "fixture")
        self.assertEqual(unit["unit_id"], "unit-1")
        self.assertEqual(unit["geometry_source"], "CompositeUnit")
        self.assertEqual(len(unit["records"]), 1)
        self.assertEqual(unit["records"][0]["name"], "hips")
        self.assertEqual(unit["records"][0]["parent_name"], None)
        self.assertTrue(unit["records"][0]["active"])
        self.assertEqual(unit["records"][0]["active_lane_count"], 7)
        self.assertEqual(unit["records"][0]["position"], [1.0, 2.0, 3.0])


if __name__ == "__main__":
    unittest.main()
