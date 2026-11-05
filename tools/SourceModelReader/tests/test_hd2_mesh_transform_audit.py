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


def translation_matrix(offset_x, offset_y, offset_z):
    return [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        float(offset_x), float(offset_y), float(offset_z), 1.0,
    ]


def unit_fixture(*, file_id, package, transforms, skeleton, meshes, geometry, hierarchy=None):
    return {
        "unit": {"file_id": file_id, "geometry_source": "Unit GPU payload"},
        "package": {"name": package},
        "transform_info": {
            "transforms": transforms,
            "resolved_skeleton": skeleton,
            "hierarchy_matrix_validation": {"entries": hierarchy or []},
        },
        "mesh_info": {"records": meshes},
        "geometry": geometry,
    }


def skinned_geometry(*, mesh_info_index=0, lanes=2616):
    return {
        "mesh_info_index": mesh_info_index,
        "vertex_count": 439,
        "index_count": 2616,
        "triangle_count": 872,
        "skinning": {
            "status": "ok",
            "resolved_active_influence_count": lanes,
            "unresolved_active_influence_count": 0,
            "matrix_validation": {
                "status": "matches",
                "comparison_formula_candidate": "mesh_world_times_inverse_bone_world",
                "matched_active_palette_slot_count": 5,
                "mismatched_active_palette_slot_count": 0,
                "unavailable_active_palette_slot_count": 0,
                "max_abs_error": 9.4e-8,
            },
        },
    }


def no_skin_geometry(*, mesh_info_index=0):
    return {
        "mesh_info_index": mesh_info_index,
        "vertex_count": 439,
        "index_count": 2280,
        "triangle_count": 760,
        "skinning": {"status": "no_skin_attributes"},
    }


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

    def load(self, fixture, label):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unit.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            return load_unit(path, label, 1.0e-4)

    def test_markdown_and_report_include_matrix_and_interpretation_limits(self):
        skinned = self.load(unit_fixture(
            file_id="unit-skinned",
            package="package-skinned",
            transforms=[
                {"index": 0, "name_hash": 100, "parent_index_raw": 65535,
                 "matrix_4x4_raw": identity_matrix()},
                {"index": 1, "name_hash": 200, "parent_index_raw": 0,
                 "matrix_4x4_raw": translation_matrix(2, 3, 4)},
            ],
            skeleton=[
                {"transform_index": 0, "name": "root"},
                {"transform_index": 1, "name": "arm_culling"},
            ],
            hierarchy=[
                {"transform_index": 0, "status": "matches", "max_abs_error": 0.0},
                {"transform_index": 1, "status": "matches", "max_abs_error": 1.2e-8},
            ],
            meshes=[{"index": 0, "mesh_id": 5678, "transform_index": 1, "lod_index": 4}],
            geometry=[skinned_geometry()],
        ), "skinned_fixture")

        unskinned = self.load(unit_fixture(
            file_id="unit-no-skin",
            package="package-no-skin",
            transforms=[{"index": 0, "name_hash": 300, "parent_index_raw": 65535,
                         "matrix_4x4_raw": identity_matrix()}],
            skeleton=[{"transform_index": 0, "name": "hips_culling"}],
            meshes=[{"index": 0, "mesh_id": 9012, "transform_index": 0, "lod_index": -1}],
            geometry=[no_skin_geometry()],
        ), "no_skin_fixture")

        dangling = self.load(unit_fixture(
            file_id="unit-dangling",
            package="package-dangling",
            transforms=[],
            skeleton=[],
            meshes=[{"index": 0, "mesh_id": 3456, "transform_index": 77, "lod_index": 0}],
            geometry=[],
        ), "dangling_fixture")

        report = build_report([skinned, unskinned, dangling], 1.0e-4)
        markdown = render_markdown(report)

        self.assertEqual(report["schema"], "SourceModelReader-HD2MeshTransformAudit1")
        self.assertIn("Interpretation limits", markdown)
        self.assertEqual(markdown.count("Stored 4x4 matrix (row-major):"), 3)
        self.assertIn("2, 3, 4, 1", markdown)
        for label in ("skinned_fixture", "no_skin_fixture", "dangling_fixture"):
            self.assertIn(f"## {label}", markdown)

        self.assertEqual(skinned["summary"]["skinned_mesh_count"], 1)
        self.assertEqual(skinned["summary"]["no_skin_attribute_mesh_count"], 0)
        self.assertEqual(skinned["summary"]["non_identity_stored_matrix_count"], 1)
        self.assertEqual(skinned["meshes"][0]["hierarchy_validation_status"], "matches")

        self.assertEqual(unskinned["summary"]["skinned_mesh_count"], 0)
        self.assertEqual(unskinned["summary"]["no_skin_attribute_mesh_count"], 1)
        self.assertEqual(unskinned["summary"]["bone_matrix_validation_matches"], 0)
        self.assertEqual(unskinned["meshes"][0]["matrix_validation_status"], "not_applicable")

        # A binding to an absent TransformInfo record must degrade, not crash.
        self.assertEqual(dangling["meshes"][0]["transform_name"], None)
        self.assertEqual(dangling["meshes"][0]["stored_matrix_identity_status"], "unavailable")
        self.assertEqual(dangling["meshes"][0]["stored_matrix_4x4"], None)
        self.assertEqual(dangling["meshes"][0]["skinning_status"], "geometry_unavailable")
        self.assertEqual(dangling["meshes"][0]["parent_chain_status"], "transform_missing")
        self.assertIn("(unresolved)", markdown)
        self.assertIn("(unavailable)", markdown)

    def test_parent_chain_exposes_cycles_and_missing_parents(self):
        unit = self.load(unit_fixture(
            file_id="unit-chain",
            package="package-chain",
            transforms=[
                {"index": 0, "name_hash": 10, "parent_index_raw": 1,
                 "matrix_4x4_raw": identity_matrix()},
                {"index": 1, "name_hash": 11, "parent_index_raw": 0,
                 "matrix_4x4_raw": identity_matrix()},
                {"index": 5, "name_hash": 12, "parent_index_raw": 9,
                 "matrix_4x4_raw": identity_matrix()},
            ],
            skeleton=[],
            meshes=[
                {"index": 0, "mesh_id": 111, "transform_index": 0, "lod_index": 0},
                {"index": 1, "mesh_id": 222, "transform_index": 5, "lod_index": 0},
            ],
            geometry=[],
        ), "chain_fixture")

        cyclic, orphaned = unit["meshes"]
        self.assertEqual(cyclic["parent_chain_status"], "cycle_detected")
        self.assertEqual([node["transform_index"] for node in cyclic["parent_chain"]], [0, 1])
        self.assertEqual(orphaned["parent_chain_status"], "parent_missing")
        self.assertEqual(orphaned["transform_name"], None)
        self.assertEqual(orphaned["parent_transform_name_hash"], None)


if __name__ == "__main__":
    unittest.main()
