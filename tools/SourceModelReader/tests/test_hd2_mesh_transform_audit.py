import json
import tempfile
import unittest
from pathlib import Path

from research.audit_hd2_mesh_transform_bindings import load_unit, render_markdown, build_report


def identity_matrix():
    return [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0,
    ]


class HD2MeshTransformAuditTests(unittest.TestCase):
    def test_preserves_direct_mesh_metadata_matrix_and_skinning_validation(self):
        matrix = identity_matrix()
        fixture = {
            "unit": {"file_id": "unit-1", "geometry_source": "Unit GPU payload"},
            "package": {"name": "package-1"},
            "transform_info": {
                "transforms": [
                    {
                        "index": 0, "name_hash": 100, "parent_index_raw": 65535,
                        "matrix_4x4_raw": matrix,
                    },
                    {
                        "index": 1, "name_hash": 200, "parent_index_raw": 0,
                        "matrix_4x4_raw": matrix,
                    },
                ],
                "resolved_skeleton": [
                    {"transform_index": 0, "name": "root"},
                    {"transform_index": 1, "name": "render_mesh"},
                ],
                "hierarchy_matrix_validation": {
                    "entries": [
                        {"transform_index": 0, "status": "matches", "max_abs_error": 0.0},
                        {"transform_index": 1, "status": "matches", "max_abs_error": 0.0},
                    ]
                },
            },
            "mesh_info": {
                "records": [{
                    "index": 0, "mesh_id": 1234, "transform_index": 1, "lod_index": 0,
                }]
            },
            "geometry": [{
                "mesh_info_index": 0,
                "vertex_count": 128,
                "index_count": 384,
                "triangle_count": 128,
                "skinning": {
                    "status": "ok",
                    "resolved_active_influence_count": 256,
                    "unresolved_active_influence_count": 0,
                    "matrix_validation": {
                        "status": "matches",
                        "comparison_formula_candidate": "mesh_world_times_inverse_bone_world",
                        "matched_active_palette_slot_count": 3,
                        "mismatched_active_palette_slot_count": 0,
                        "unavailable_active_palette_slot_count": 0,
                        "max_abs_error": 1.0e-7,
                    },
                },
            }],
        }

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unit.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            unit = load_unit(path, "fixture", 1.0e-4)

        self.assertEqual(unit["unit_id"], "unit-1")
        self.assertEqual(unit["summary"]["direct_mesh_count"], 1)
        self.assertEqual(unit["summary"]["skinned_mesh_count"], 1)
        mesh = unit["meshes"][0]
        self.assertEqual(mesh["transform_name"], "render_mesh")
        self.assertEqual(mesh["parent_transform_name"], "root")
        self.assertEqual(mesh["parent_chain_status"], "root_reached")
        self.assertEqual(mesh["skinning_status"], "ok")
        self.assertEqual(mesh["matrix_validation_matched_slots"], 3)
        self.assertEqual(mesh["hierarchy_validation_status"], "matches")
        self.assertEqual(mesh["stored_matrix_identity_status"], "identity")
        self.assertEqual(mesh["stored_matrix_4x4"], matrix)

    def test_does_not_infer_skinning_from_a_culling_like_name(self):
        matrix = identity_matrix()
        fixture = {
            "unit": {"file_id": "unit-2", "geometry_source": "Unit GPU payload"},
            "package": {"name": "package-2"},
            "transform_info": {
                "transforms": [{
                    "index": 0, "name_hash": 300, "parent_index_raw": 65535,
                    "matrix_4x4_raw": matrix,
                }],
                "resolved_skeleton": [{"transform_index": 0, "name": "arm_culling"}],
                "hierarchy_matrix_validation": {
                    "entries": [{"transform_index": 0, "status": "matches", "max_abs_error": 0.0}]
                },
            },
            "mesh_info": {
                "records": [{
                    "index": 0, "mesh_id": 5678, "transform_index": 0, "lod_index": 4,
                }]
            },
            "geometry": [{
                "mesh_info_index": 0,
                "vertex_count": 439,
                "index_count": 2616,
                "triangle_count": 872,
                "skinning": {
                    "status": "ok",
                    "resolved_active_influence_count": 2616,
                    "unresolved_active_influence_count": 0,
                    "matrix_validation": {
                        "status": "matches",
                        "matched_active_palette_slot_count": 5,
                        "mismatched_active_palette_slot_count": 0,
                        "unavailable_active_palette_slot_count": 0,
                        "max_abs_error": 9.4e-8,
                    },
                },
            }],
        }

        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unit.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            unit = load_unit(path, "fixture", 1.0e-4)

        mesh = unit["meshes"][0]
        self.assertEqual(mesh["transform_name"], "arm_culling")
        self.assertEqual(mesh["lod_index"], 4)
        self.assertEqual(mesh["skinning_status"], "ok")
        self.assertEqual(mesh["resolved_active_influence_count"], 2616)
        self.assertEqual(mesh["matrix_validation_status"], "matches")
        self.assertEqual(unit["summary"]["skinned_mesh_count"], 1)
        self.assertEqual(unit["summary"]["no_skin_attribute_mesh_count"], 0)

    def test_markdown_and_report_include_matrix_and_interpretation_limits(self):
        unit = {
            "label": "fixture",
            "unit_id": "unit-3",
            "package_id": "package-3",
            "geometry_source": "CompositeUnit",
            "mesh_count": 0,
            "summary": {
                "direct_mesh_count": 0,
                "identity_stored_matrix_count": 0,
                "non_identity_stored_matrix_count": 0,
                "skinned_mesh_count": 0,
                "no_skin_attribute_mesh_count": 0,
                "bone_matrix_validation_matches": 0,
                "bone_matrix_validation_mismatches": 0,
                "hierarchy_validation_matches": 0,
            },
            "meshes": [],
        }
        report = build_report([unit, unit, unit], 1.0e-4)
        markdown = render_markdown(report)
        self.assertEqual(report["schema"], "SourceModelReader-HD2MeshTransformAudit1")
        self.assertIn("Interpretation limits", markdown)
        self.assertIn("Stored 4x4 matrix", markdown)


if __name__ == "__main__":
    unittest.main()
