import json
import tempfile
import unittest
from pathlib import Path

from research.probe_hd2_vertex_position_space import (
    POSITION_BUFFER_TOLERANCE,
    REST_INVARIANT_TOLERANCE,
    _box_vs_union,
    _max_abs_point_diff,
    build_report,
    load_unit,
    render_markdown,
    rest_invariant_tests,
    transform_point,
)


def identity_matrix():
    return [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0,
    ]


def translated_matrix(offset_z):
    return [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, float(offset_z), 1.0,
    ]


def attribute(name, index, fmt, values):
    return {
        "component_name": name,
        "component_index": index,
        "format_name": fmt,
        "values": values,
    }


def render_geometry(mesh_info_index, transform_index, lod_index, positions, mesh_id):
    return {
        "mesh_info_index": mesh_info_index,
        "mesh_id": mesh_id,
        "transform_index": transform_index,
        "lod_index": lod_index,
        "vertex_count": len(positions),
        "index_count": 0,
        "triangle_count": 0,
        "attributes": [
            attribute("position", 0, "vec3_float32", positions),
            attribute("normal", 0, "packed_octahedral_normal", [7, 8, 9][:len(positions)]),
            attribute("uv", 0, "vec2_half", [[0.1, 0.2]] * len(positions)),
        ],
        "skinning": {"status": "ok"},
    }


def proxy_geometry(mesh_info_index, transform_index, lod_index, mesh_id):
    positions = [[-0.5, -0.5, -0.5], [0.5, 0.5, 0.5]]
    return {
        "mesh_info_index": mesh_info_index,
        "mesh_id": mesh_id,
        "transform_index": transform_index,
        "lod_index": lod_index,
        "vertex_count": 2,
        "index_count": 0,
        "triangle_count": 0,
        "attributes": [
            attribute("position", 0, "vec3_float32", positions),
            attribute("normal", 0, "packed_octahedral_normal", [7, 8]),
            attribute("uv", 0, "vec2_half", [[0.1, 0.2], [0.3, 0.4]]),
        ],
        "skinning": {"status": "no_skin_attributes"},
    }


def unit_fixture(file_id, render_positions, proxy_positions=None, mesh_id_base=1000):
    transforms = [
        {"index": 0, "name_hash": 10, "parent_index_raw": 65535, "matrix_4x4_raw": identity_matrix()},
        {"index": 1, "name_hash": 11, "parent_index_raw": 0, "matrix_4x4_raw": identity_matrix()},
        {"index": 2, "name_hash": 12, "parent_index_raw": 0,
         "matrix_4x4_raw": translated_matrix(1.25)},
    ]
    geometry = [
        render_geometry(0, 1, 0, render_positions, mesh_id_base),
        proxy_geometry(1, 2, -1, mesh_id_base + 1),
    ]
    # Distinct MeshIDs per Unit: the shared-buffer check must not fire on a mesh
    # compared against itself.
    meshes = [
        {"index": 0, "mesh_id": mesh_id_base, "transform_index": 1, "lod_index": 0},
        {"index": 1, "mesh_id": mesh_id_base + 1, "transform_index": 2, "lod_index": -1},
    ]
    if proxy_positions is not None:
        geometry[1]["attributes"][0]["values"] = proxy_positions
    return {
        "unit": {"file_id": file_id, "geometry_source": "Unit GPU payload"},
        "package": {"name": f"package-{file_id}"},
        "transform_info": {
            "transforms": transforms,
            "resolved_skeleton": [
                {"transform_index": 0, "name": "root"},
                {"transform_index": 1, "name": "g_render"},
                {"transform_index": 2, "name": "hips_culling"},
            ],
            "hierarchy_matrix_validation": {"entries": []},
        },
        "mesh_info": {"records": meshes},
        "geometry": geometry,
    }


class HD2VertexPositionSpaceProbeTests(unittest.TestCase):
    def test_transform_point_uses_the_row_vector_layout(self):
        matrix = translated_matrix(-0.9734727144241333)
        result = transform_point([1.0, 2.0, 3.0], matrix)
        self.assertAlmostEqual(result[0], 1.0)
        self.assertAlmostEqual(result[1], 2.0)
        self.assertAlmostEqual(result[2], 3.0 - 0.9734727144241333)

        # Cyclic basis rows: translation lives in floats 12..15, not 3, 7 and 11.
        cyclic = [
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            1.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 0.0, 1.0,
        ]
        self.assertEqual(transform_point([1.0, 2.0, 3.0], cyclic), [3.0, 1.0, 2.0])

    def test_box_vs_union_measures_both_directions(self):
        box = {"min": [-0.5, -0.5, -0.5], "max": [0.5, 0.5, 0.5]}
        inside = _box_vs_union(box, [0.0, 0.0, 0.0], [0.1, 0.1, 0.2])
        self.assertEqual(inside["union_outside_max"], 0.0)
        self.assertGreater(inside["box_outside_max"], 0.0)

        # The union escapes upward on z; the metric must report that as outside.
        escaped = _box_vs_union(box, [0.0, 0.0, 1.0], [0.1, 0.1, 1.5])
        self.assertAlmostEqual(escaped["union_outside_per_axis"][2], 1.0)
        self.assertAlmostEqual(escaped["union_outside_max"], 1.0)

    def test_max_abs_point_diff_separates_noise_from_real_difference(self):
        left = [[0.0, -0.5, 1.0], [1.0, 2.0, 3.0]]
        noisy = [[-6.5168e-17, -0.5, 1.0], [1.0, 2.0, 3.0]]
        difference = _max_abs_point_diff(left, noisy)
        self.assertGreater(difference, 0.0)
        self.assertLessEqual(difference, POSITION_BUFFER_TOLERANCE)
        self.assertGreater(_max_abs_point_diff(left, [[0.2, -0.5, 1.0], [1.0, 2.0, 3.0]]),
                           POSITION_BUFFER_TOLERANCE)
        self.assertIsNone(_max_abs_point_diff(left, [[0.0, 0.0, 0.0]]))

    def load(self, fixture, label):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unit.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            return load_unit(path, label)

    def test_placement_is_decided_by_containment_not_surface_distance(self):
        first = self.load(unit_fixture("unit-a", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2], [0.1, 0.1, 1.5]]),
                          "unit_a")
        second = self.load(unit_fixture("unit-b", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2],
                                                   [0.1, 0.1, 1.5], [0.05, 0.05, 1.25]]),
                           "unit_b")
        report = build_report([first, second, self.load(unit_fixture(
            "unit-a", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2], [0.1, 0.1, 1.5]]), "unit_c")], 0)
        markdown = render_markdown(report)

        for unit in report["units"][:2]:
            self.assertEqual(unit["correspondence"], [])
            self.assertEqual(len(unit["volume"]), 1)
            test = unit["volume"][0]
            self.assertFalse(test["raw_contains_render"])
            self.assertTrue(test["mesh_world_contains_render"])
            self.assertEqual(test["mesh_world_vs_render_union"]["union_outside_max"], 0.0)
            self.assertAlmostEqual(test["raw_vs_render_union"]["union_outside_max"], 1.0)

        self.assertEqual(report["summary"]["volume_tests"], 3)
        self.assertIn("### Auxiliary volume containment", markdown)
        self.assertIn("True", markdown)

    def test_shared_proxy_buffers_are_detected_across_units(self):
        # A perturbation large enough to change the stored float but far below the
        # buffer tolerance: this is the branch real float32 noise lands in.
        noisy_positions = [[-0.5, -0.5 - 1.0e-9, -0.5], [0.5, 0.5, 0.5]]
        first = self.load(unit_fixture("unit-a", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2], [0.1, 0.1, 1.5]]),
                          "unit_a")
        second = self.load(unit_fixture("unit-b", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2],
                                                   [0.1, 0.1, 1.5], [0.05, 0.05, 1.25]],
                                        proxy_positions=noisy_positions, mesh_id_base=5000),
                           "unit_b")
        third = self.load(unit_fixture("unit-c", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2], [0.1, 0.1, 1.5]]),
                          "unit_c")
        report = build_report([first, second, third], 0)

        pairs = [pair for pair in report["shared_asset_pairs"]
                 if "hips_culling" in (pair["left"]["transform_name"], pair["right"]["transform_name"])]
        self.assertTrue(pairs, "expected the shared proxy pair to be reported")
        pair = min(pairs, key=lambda item: item["position_max_abs_diff"])
        self.assertIn("normal0", pair["bitwise_identical_buffers"])
        self.assertIn("uv0", pair["bitwise_identical_buffers"])
        self.assertIn("position0", pair["differing_buffers"])
        self.assertTrue(pair["position_within_tolerance"])
        self.assertLess(pair["position_max_abs_diff"], POSITION_BUFFER_TOLERANCE)
        self.assertIn("## Cross-Unit shared vertex buffers", render_markdown(report))

    def test_single_lod_unit_and_repeated_mesh_id_are_not_over_read(self):
        fixture = unit_fixture("unit-solo", [[0.0, 0.0, 1.0], [0.1, 0.0, 1.2], [0.1, 0.1, 1.5]])
        first = self.load(fixture, "solo_a")
        second = self.load(fixture, "solo_b")
        third = self.load(fixture, "solo_c")
        report = build_report([first, second, third], 0)
        markdown = render_markdown(report)

        self.assertIn("No correspondence test was possible", markdown)
        for unit in report["units"]:
            self.assertEqual(unit["correspondence"], [])
            self.assertEqual(len(unit["volume"]), 1)
            self.assertEqual(unit["volume"][0]["lod_index"], -1)
            self.assertEqual(unit["volume"][0]["skinning_status"], "no_skin_attributes")
        # Identical buffers under identical MeshIDs are the same mesh, not a shared asset.
        self.assertEqual(report["shared_asset_pairs"], [])


class HD2RestInvariantTests(unittest.TestCase):
    def load(self, fixture, label):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "unit.json"
            path.write_text(json.dumps(fixture), encoding="utf-8")
            return load_unit(path, label)

    def fixture(self, palette_matrix):
        return {
            "unit": {"file_id": "unit-rest", "geometry_source": "Unit GPU payload"},
            "package": {"name": "package-rest"},
            "transform_info": {
                "transforms": [
                    {"index": 0, "name_hash": 5, "parent_index_raw": 65535,
                     "matrix_4x4_raw": identity_matrix()},
                    {"index": 1, "name_hash": 6, "parent_index_raw": 0,
                     "matrix_4x4_raw": translated_matrix(1.25)},
                    {"index": 2, "name_hash": 7, "parent_index_raw": 0,
                     "matrix_4x4_raw": translated_matrix(0.5)},
                ],
                "resolved_skeleton": [
                    {"transform_index": 0, "name": "root"},
                    {"transform_index": 1, "name": "g_mesh"},
                    {"transform_index": 2, "name": "r_thigh"},
                ],
                "hierarchy_matrix_validation": {"entries": []},
            },
            "mesh_info": {"records": [
                {"index": 0, "mesh_id": 77, "transform_index": 1, "lod_index": 0},
            ]},
            "geometry": [{
                "mesh_info_index": 0,
                "mesh_id": 77,
                "transform_index": 1,
                "lod_index": 0,
                "vertex_count": 2,
                "attributes": [
                    attribute("position", 0, "vec3_float32", [[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]]),
                    attribute("bone_index", 0, "vec4_uint8", [[2, 2, 9, 9], [2, 2, 9, 9]]),
                ],
                "skinning": {
                    "status": "ok",
                    "palette": [{
                        "palette_slot": 2,
                        "transform_index": 2,
                        "bone_info_matrix_raw": palette_matrix,
                        "valid": True,
                    }],
                },
            }],
        }

    def test_rest_product_lands_on_the_placed_vertex_exactly_once(self):
        # palette = mesh_world x inverse(bone_world) = translation 1.25 - 0.5.
        rows = rest_invariant_tests(self.load(self.fixture(translated_matrix(0.75)), "rest"),
                                    0, REST_INVARIANT_TOLERANCE)
        self.assertEqual(len(rows), 1)
        row = rows[0]
        self.assertEqual(row["lanes_checked"], 4)
        self.assertEqual(row["lanes_unresolved"], 4)
        self.assertLessEqual(row["max_abs_error_vs_placed"], REST_INVARIANT_TOLERANCE)
        self.assertTrue(row["within_tolerance"])
        # The placed target differs from the raw vertex by the mesh-world offset.
        self.assertAlmostEqual(row["max_abs_error_vs_unplaced_vertex"], 1.25)

    def test_double_applied_mesh_world_is_caught(self):
        # mesh_world applied twice: 1.25 + 1.25 - 0.5 = 2.0.
        rows = rest_invariant_tests(self.load(self.fixture(translated_matrix(2.0)), "rest"),
                                    0, REST_INVARIANT_TOLERANCE)
        self.assertEqual(len(rows), 1)
        self.assertFalse(rows[0]["within_tolerance"])
        # The surplus equals exactly one extra application of the mesh-world offset.
        self.assertAlmostEqual(rows[0]["max_abs_error_vs_placed"], 1.25)


if __name__ == "__main__":
    unittest.main()
