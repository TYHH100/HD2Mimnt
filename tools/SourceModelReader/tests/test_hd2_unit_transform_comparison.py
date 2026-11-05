import json
import tempfile
import unittest
from pathlib import Path

from research.compare_hd2_unit_transforms import (
    build_report,
    compare_pair,
    load_unit,
    render_markdown,
    _transform_role,
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
            "mesh_binding_count": row.get("mesh_binding_count", 0),
            "mesh_binding_info_available": row.get("mesh_binding_info_available", True),
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
        "mesh_binding_info_available": True,
        "mesh_binding_record_count": sum(row["mesh_binding_count"] for row in records),
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
        self.assertEqual(report["summary"]["pairwise_no_overlap"], 3)
        self.assertEqual(report["summary"]["status"], "warning")

    def test_loads_only_transform_metadata_from_hd2_unit_json(self):
        fixture = {
            "unit": {"file_id": "unit-1", "geometry_source": "CompositeUnit"},
            "package": {"name": "package-1"},
            "summary": {"status": "ok"},
            "mesh_info": {
                "records": [{"index": 0, "transform_index": 0}],
            },
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
        self.assertEqual(unit["records"][0]["mesh_binding_count"], 1)
        self.assertEqual(unit["records"][0]["mesh_binding_info_available"], True)
        self.assertEqual(unit["records"][0]["position"], [1.0, 2.0, 3.0])


    def test_mesh_transform_matrix_difference_is_not_counted_as_skeleton_mismatch(self):
        identity = [
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0,
        ]
        translated = [
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.25, 0.5, 1.0,
        ]
        units = [
            make_unit("lower", [
                {"index": 0, "name": "hips", "active": True},
                {"index": 1, "name": "game_mesh", "active": False,
                 "mesh_binding_count": 1, "matrix_4x4_raw": identity},
            ]),
            make_unit("torso", [
                {"index": 3, "name": "hips", "active": True},
                {"index": 4, "name": "game_mesh", "active": False,
                 "mesh_binding_count": 1, "matrix_4x4_raw": translated},
            ]),
            make_unit("avatar", [
                {"index": 8, "name": "hips", "active": True},
                {"index": 9, "name": "game_mesh", "active": False,
                 "mesh_binding_count": 1, "matrix_4x4_raw": identity},
            ]),
        ]
        report = build_report(units, 1.0e-4)
        pair = report["pairwise_comparisons"]["lower__vs__torso"]
        mesh_row = next(row for row in pair["transforms"] if row["name"] == "game_mesh")

        self.assertEqual(mesh_row["left_transform_role"], "mesh_transform")
        self.assertEqual(mesh_row["right_transform_role"], "mesh_transform")
        self.assertEqual(mesh_row["comparison_scope"], "mesh_transform")
        self.assertEqual(mesh_row["stored_matrix_status"], "mismatch")
        self.assertEqual(pair["summary"]["stored_matrix_mismatch_count"], 1)
        self.assertEqual(pair["summary"]["skeleton_stored_matrix_mismatch_count"], 0)
        self.assertEqual(pair["summary"]["mesh_transform_stored_matrix_mismatch_count"], 1)
        self.assertEqual(report["summary"]["pairwise_skeleton_matrix_mismatches"], 0)
        self.assertGreater(report["summary"]["pairwise_mesh_transform_matrix_mismatches"], 0)
        self.assertEqual(report["summary"]["status"], "review")


    def test_unweighted_non_mesh_transform_remains_in_skeleton_scope(self):
        unit = make_unit("unit", [
            {"index": 0, "name": "helper_joint", "active": False},
        ])
        pair = compare_pair(unit, unit, 1.0e-4)
        row = pair["transforms"][0]
        self.assertEqual(row["left_transform_role"], "unweighted_skeleton")
        self.assertEqual(row["comparison_scope"], "skeleton_transform")


    def test_classifies_mesh_only_ancestors_but_keeps_shared_active_ancestors_as_skeleton(self):
        fixture = {
            "unit": {"file_id": "mesh-hierarchy-unit", "geometry_source": "Unit"},
            "package": {"name": "mesh-hierarchy-package"},
            "summary": {"status": "ok"},
            "mesh_info": {
                "records": [{"index": 0, "mesh_id": 900, "transform_index": 4}],
            },
            "transform_info": {
                "transforms": [
                    {"index": 0, "name_hash": 10, "parent_index_raw": 65535,
                     "position": [0, 0, 0], "rotation_3x3": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                     "scale": [1, 1, 1], "matrix_4x4_raw": [
                         1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]},
                    {"index": 1, "name_hash": 11, "parent_index_raw": 0,
                     "position": [0, 0, 0], "rotation_3x3": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                     "scale": [1, 1, 1], "matrix_4x4_raw": [
                         1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]},
                    {"index": 2, "name_hash": 12, "parent_index_raw": 1,
                     "position": [0, 0, 0], "rotation_3x3": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                     "scale": [1, 1, 1], "matrix_4x4_raw": [
                         1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]},
                    {"index": 3, "name_hash": 13, "parent_index_raw": 2,
                     "position": [0, 0, 0], "rotation_3x3": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                     "scale": [1, 1, 1], "matrix_4x4_raw": [
                         1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]},
                    {"index": 4, "name_hash": 14, "parent_index_raw": 3,
                     "position": [0, 0, 0], "rotation_3x3": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                     "scale": [1, 1, 1], "matrix_4x4_raw": [
                         1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]},
                    {"index": 5, "name_hash": 15, "parent_index_raw": 1,
                     "position": [0, 0, 0], "rotation_3x3": [1, 0, 0, 0, 1, 0, 0, 0, 1],
                     "scale": [1, 1, 1], "matrix_4x4_raw": [
                         1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]},
                ],
                "resolved_skeleton": [
                    {"transform_index": 0, "name": "entity_root", "name_ambiguous": False,
                     "has_active_vertex_influence": False, "active_vertex_influence_lane_count": 0},
                    {"transform_index": 1, "name": "shared_root", "name_ambiguous": False,
                     "has_active_vertex_influence": False, "active_vertex_influence_lane_count": 0},
                    {"transform_index": 2, "name": "game_mesh", "name_ambiguous": False,
                     "has_active_vertex_influence": False, "active_vertex_influence_lane_count": 0},
                    {"transform_index": 3, "name": "mesh_group", "name_ambiguous": False,
                     "has_active_vertex_influence": False, "active_vertex_influence_lane_count": 0},
                    {"transform_index": 4, "name": "render_mesh", "name_ambiguous": False,
                     "has_active_vertex_influence": False, "active_vertex_influence_lane_count": 0},
                    {"transform_index": 5, "name": "hips", "name_ambiguous": False,
                     "has_active_vertex_influence": True, "active_vertex_influence_lane_count": 4},
                ],
            },
        }
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "mesh-hierarchy.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            unit = load_unit(path, "mesh-hierarchy")

        roles = {
            row["name"]: _transform_role(row)
            for row in unit["records"]
            if row["name"]
        }
        self.assertEqual(roles["render_mesh"], "mesh_transform")
        self.assertEqual(roles["mesh_group"], "mesh_hierarchy_transform")
        self.assertEqual(roles["game_mesh"], "mesh_hierarchy_transform")
        self.assertEqual(roles["shared_root"], "unweighted_skeleton")
        self.assertEqual(roles["entity_root"], "unweighted_skeleton")
        self.assertEqual(roles["hips"], "weighted_skeleton")

    def test_weight_usage_difference_does_not_change_skeleton_transform_scope(self):
        left = make_unit("left", [
            {"index": 0, "name": "spine", "active": True},
        ])
        right = make_unit("right", [
            {"index": 3, "name": "spine", "active": False},
        ])
        pair = compare_pair(left, right, 1.0e-4)
        row = pair["transforms"][0]

        self.assertEqual(row["left_transform_role"], "weighted_skeleton")
        self.assertEqual(row["right_transform_role"], "unweighted_skeleton")
        self.assertEqual(row["comparison_scope"], "skeleton_transform")
        self.assertEqual(row["transform_role_status"], "matches")
        self.assertEqual(row["active_status"], "differs")
        self.assertEqual(pair["summary"]["transform_role_mismatch_count"], 0)
        self.assertEqual(pair["summary"]["active_flag_difference_count"], 1)


if __name__ == "__main__":
    unittest.main()
