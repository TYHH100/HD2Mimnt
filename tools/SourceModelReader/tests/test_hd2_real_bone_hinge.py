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

from research.verify_hd2_bone_hinge_on_real_matrices import (  # noqa: E402
    DEFAULT_ANGLE_DEGREES,
    REST_INVARIANT_TOLERANCE,
    _attr_indices,
    _stride_indices,
    build_report,
    hinge_tests,
    main,
    render_markdown,
    summarize,
)
from research.verify_hd2_retarget_deformation import (  # noqa: E402
    invert_affine,
    multiply,
    rotate_x,
    rotate_z,
    translate,
)

MESH_WORLD = multiply(rotate_z(math.radians(15.0)), translate(-0.28328, -0.04818, 1.43647))
JOINT_A = [-0.086986, -0.018955, 0.941668]
JOINT_B = [-0.210121, -0.068876, 1.47449]
WORLD_A = multiply(rotate_x(math.radians(20.0)), translate(*JOINT_A))
WORLD_B = translate(*JOINT_B)
POINTS = [
    [0.05, 0.02, -0.20],
    [0.00, 0.01, 0.30],
    [-0.04, 0.03, 1.10],
    [0.02, -0.02, 0.70],
]
# Lane plan per vertex: two real bones, plus an optional third bone index whose palette
# slot is deliberately unusable.
LANES = [[0, 1], [0, 1], [1, 0], [0, 1]]
LANES_WITH_BAD = [[0, 1], [2, 2], [1, 0], [0, 1]]
WEIGHTS = [[0.7, 0.3], [1.0, 0.0], [0.25, 0.75], [0.5, 0.5]]


def _palette(mesh_world: list[float], bone_world: list[float]) -> list[float]:
    return multiply(mesh_world, invert_affine(bone_world))


def build_unit(mesh_world: list[float] = MESH_WORLD, swap_palette_a: bool = False,
               bad_lane: bool = False, second_attribute: bool = False,
               ambiguous_pairing: bool = False) -> dict:
    """A Unit dict shaped exactly like the position-space probe's loader output."""
    palette_a = _palette(mesh_world, WORLD_A)
    if swap_palette_a:
        palette_a = multiply(invert_affine(WORLD_A), mesh_world)
    slots = [
        {"palette_slot": 0, "valid": True, "bone_info_matrix_raw": palette_a,
         "transform_index": 4},
        {"palette_slot": 1, "valid": True, "bone_info_matrix_raw": _palette(mesh_world, WORLD_B),
         "transform_index": 5},
        {"palette_slot": 2, "valid": False, "bone_info_matrix_raw": [], "transform_index": 6},
    ]
    index_buffer = list(LANES_WITH_BAD if bad_lane else LANES)
    buffers = [index_buffer]
    if second_attribute:
        # A second bone-index attribute with weights that do not extend to it, which the
        # chosen pairing does not consult, so its lanes must be counted as unexamined.
        buffers.append(list(index_buffer))
    mesh = {
        "mesh_info_index": 0,
        "mesh_id": 1000,
        "lod_index": 0,
        "transform_index": 1,
        "transform_name": "mesh_node",
        "stored_matrix_4x4": mesh_world,
        "stored_matrix_identity_status": ("identity" if mesh_world == translate(0, 0, 0)
                                         else "non_identity"),
        "skinning_status": "ok",
        "position_attribute_present": True,
        "palette": slots,
        "bone_index_buffers": buffers,
        "weight_rows": [list(row) for row in WEIGHTS],
        "weight_format": "vec2_float32",
        "_points": [list(point) for point in POINTS],
    }
    unit = {
        "label": "fixture",
        "input": "fixture.json",
        "unit_id": "unit-fixture",
        "package_id": "package-fixture",
        "geometry_source": "Unit GPU payload",
        "transform_matrices": {1: mesh_world, 4: WORLD_A, 5: WORLD_B, 6: translate(0.0, 0.0, 1.0)},
        "transform_names": {1: "mesh_node", 4: "bone_a", 5: "bone_b", 6: "bone_c"},
        "meshes": [mesh],
        "hinge": {"rows": [], "skipped_meshes": []},
    }
    if ambiguous_pairing and second_attribute:
        mesh["weight_rows"] = [[0.5, 0.5], [0.5, 0.5], [0.5, 0.5], [0.5, 0.5]]
        mesh["bone_index_buffers"] = [list(LANES), list(LANES)]
    return unit


class HD2RealBoneHingeSamplingTests(unittest.TestCase):
    def test_stride_indices_spreads_and_respects_the_limit(self):
        self.assertEqual(_stride_indices(10, 3), [0, 4, 8])
        self.assertEqual(_stride_indices(10, 0), list(range(10)))
        self.assertEqual(_stride_indices(10, 25), list(range(10)))
        self.assertEqual(_stride_indices(0, 5), [])

    def test_attr_indices_parses_both_pairing_models(self):
        self.assertEqual(_attr_indices("single_index_attr_1", 3), [1])
        self.assertEqual(_attr_indices("every_index_attr_shares_weights", 3), [0, 1, 2])
        self.assertEqual(_attr_indices("single_index_attr_9", 3), [])
        self.assertEqual(_attr_indices("single_index_attr_x", 3), [])
        self.assertEqual(_attr_indices("unknown_model", 3), [])


class HD2RealBoneHingeChecksTests(unittest.TestCase):
    def test_faithful_fixture_passes_every_gate(self):
        unit = build_unit()
        result = hinge_tests(unit, 0, 0, DEFAULT_ANGLE_DEGREES, REST_INVARIANT_TOLERANCE)
        rows = [row for row in result["rows"] if row["naive_verdict"] != "degenerate_axis"]
        self.assertTrue(rows, result["skipped_meshes"])
        self.assertEqual(result["skipped_meshes"], [])
        for row in rows:
            self.assertLessEqual(row["hinge_max_error"], REST_INVARIANT_TOLERANCE)
            self.assertLessEqual(row["rigid_max_error"], REST_INVARIANT_TOLERANCE)
            self.assertLessEqual(row["blend_max_error"], REST_INVARIANT_TOLERANCE)
            self.assertEqual(row["joint_drift"], 0.0)
            self.assertTrue(row["hinge_within_tolerance"], row["axis_label"])
        self.assertNotIn("joint_stays_fixed", rows[0])
        self.assertTrue(rows[0]["joint_fixity_holds_by_construction"])

    def test_both_constructions_are_separated_on_real_joints(self):
        rows = hinge_tests(build_unit(), 0, 0, DEFAULT_ANGLE_DEGREES,
                           REST_INVARIANT_TOLERANCE)["rows"]
        self.assertTrue(all(row["naive_verdict"] == "separated" for row in rows))
        # Some hinge axes sit nearly on top of a joint, so the drift that matters is the
        # vertex-level one; assert the smallest drift is still far outside tolerance.
        self.assertGreater(min(row["naive_joint_drift"] for row in rows), 1.0e-3)
        self.assertGreater(min(row["naive_max_error"] for row in rows), REST_INVARIANT_TOLERANCE)

    def test_dropped_placement_error_is_reported_and_rotation_invariant(self):
        rows = hinge_tests(build_unit(), 0, 0, DEFAULT_ANGLE_DEGREES,
                           REST_INVARIANT_TOLERANCE)["rows"]
        for row in rows:
            self.assertEqual(row["dropped_placement_verdict"], "separated")
            self.assertGreater(row["dropped_placement_max_error"], 0.1)
        identity_rows = hinge_tests(build_unit(mesh_world=[1.0, 0.0, 0.0, 0.0,
                                                          0.0, 1.0, 0.0, 0.0,
                                                          0.0, 0.0, 1.0, 0.0,
                                                          0.0, 0.0, 0.0, 1.0]),
                                    0, 0, DEFAULT_ANGLE_DEGREES,
                                    REST_INVARIANT_TOLERANCE)["rows"]
        self.assertTrue(identity_rows)
        for row in identity_rows:
            self.assertEqual(row["dropped_placement_verdict"], "identical")
            self.assertEqual(row["dropped_placement_max_error"], 0.0)

    def test_swapped_palette_is_refused_before_any_pass_is_reported(self):
        unit = build_unit(swap_palette_a=True)
        result = hinge_tests(unit, 0, 0, DEFAULT_ANGLE_DEGREES, REST_INVARIANT_TOLERANCE)
        self.assertEqual(result["rows"], [])
        self.assertEqual([row["reason"] for row in result["skipped_meshes"]],
                         ["no_passing_weight_pairing"])

    def test_unusable_slot_lanes_are_counted_instead_of_guessed(self):
        unit = build_unit(bad_lane=True)
        result = hinge_tests(unit, 0, 0, DEFAULT_ANGLE_DEGREES, REST_INVARIANT_TOLERANCE)
        self.assertTrue(result["rows"], result["skipped_meshes"])
        self.assertTrue(all(row["lanes_unresolved"] >= 1 for row in result["rows"]))

    def test_lanes_of_an_unconsulted_attribute_are_counted_separately(self):
        single = hinge_tests(build_unit(), 0, 0, DEFAULT_ANGLE_DEGREES,
                             REST_INVARIANT_TOLERANCE)["rows"]
        two_attributes = hinge_tests(build_unit(second_attribute=True), 0, 0,
                                     DEFAULT_ANGLE_DEGREES, REST_INVARIANT_TOLERANCE)["rows"]
        self.assertTrue(all(row["lanes_in_unexamined_attributes"] == 0 for row in single))
        self.assertTrue(two_attributes)
        for row in two_attributes:
            self.assertEqual(row["lanes_in_unexamined_attributes"], len(POINTS) * 2)
            self.assertEqual(row["bone_index_attr_count"], 2)
            self.assertEqual(row["attr_indices_used"], [0])

    def test_ambiguous_pairings_are_flagged_not_silently_chosen(self):
        rows = hinge_tests(build_unit(second_attribute=True, ambiguous_pairing=True),
                           0, 0, DEFAULT_ANGLE_DEGREES, REST_INVARIANT_TOLERANCE)["rows"]
        self.assertTrue(rows)
        self.assertTrue(all(row["pairing_model"] for row in rows))
        self.assertTrue(any(row["pairing_ambiguous"] for row in rows))


class HD2RealBoneHingeReportTests(unittest.TestCase):
    def test_summary_counts_and_gates(self):
        units = [build_unit()]
        report = build_report(units, 0, 0, DEFAULT_ANGLE_DEGREES, REST_INVARIANT_TOLERANCE)
        summary = report["summary"]
        self.assertEqual(summary["units"], 1)
        self.assertEqual(summary["meshes_checked"], 1)
        self.assertGreater(summary["hinge_rows"], 0)
        self.assertEqual(summary["rows_failing"], 0)
        self.assertEqual(summary["max_joint_drift"], 0.0)
        self.assertGreaterEqual(summary["dropped_separated"], 1)
        self.assertTrue(report["gates"]["every_row_within_tolerance"])
        self.assertTrue(report["gates"]["no_control_left_unseparated"])
        self.assertEqual(summary["hinge_rows"], summary["rows_checked"]
                         + summary["degenerate_axis_rows"])

    def test_summary_tallies_across_units(self):
        first = build_unit()
        second = build_unit()
        first["label"] = "a"
        second["label"] = "b"
        for unit in (first, second):
            unit["hinge"] = hinge_tests(unit, 0, 0, DEFAULT_ANGLE_DEGREES,
                                        REST_INVARIANT_TOLERANCE)
        single = summarize([first])
        both = summarize([first, second])
        self.assertEqual(both["units"], 2)
        self.assertEqual(both["hinge_rows"], 2 * single["hinge_rows"])
        self.assertEqual(both["meshes_checked"], 2 * single["meshes_checked"])

    def test_markdown_lists_units_verdicts_and_limits(self):
        report = build_report([build_unit()], 0, 0, DEFAULT_ANGLE_DEGREES,
                              REST_INVARIANT_TOLERANCE)
        markdown = render_markdown(report)
        self.assertIn("# HD2 Real-Bone Hinge Checks", markdown)
        self.assertIn("## fixture", markdown)
        self.assertIn("Sampled / total", markdown)
        self.assertIn("algebraic identity of the construction", markdown)
        self.assertIn("re-expression of that contract", markdown)
        self.assertNotIn("FAIL", markdown)

    def test_cli_on_a_written_unit_json_returns_zero(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            unit_path = directory / "unit.json"
            unit_path.write_text(json.dumps(unit_report_payload()), encoding="utf-8")
            json_out = directory / "hinge.json"
            markdown_out = directory / "hinge.md"
            code, stdout = _run_main(["x", "--unit", "cli={}".format(unit_path),
                                      "--output-json", str(json_out),
                                      "--output-markdown", str(markdown_out)])
            self.assertEqual(code, 0, stdout)
            self.assertIn("Real-bone hinge checks: rows=", stdout)
            payload = json.loads(json_out.read_text(encoding="utf-8"))
            self.assertEqual(payload["schema"], "SourceModelReader-HD2RealBoneHingeChecks1")
            self.assertTrue(payload["summary"]["hinge_rows"] > 0)
            self.assertTrue(payload["gates"]["every_row_within_tolerance"])
            markdown = markdown_out.read_text(encoding="utf-8")
            self.assertIn("## cli", markdown)
            self.assertIn("pass", markdown)

    def test_cli_exits_one_when_a_gate_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            directory = Path(tmp)
            unit_path = directory / "unit.json"
            unit_path.write_text(json.dumps(unit_report_payload(perturb=1.0e-9)),
                                 encoding="utf-8")
            json_out = directory / "hinge.json"
            markdown_out = directory / "hinge.md"
            code, stdout = _run_main(["x", "--unit", "cli={}".format(unit_path),
                                      "--tolerance", "1e-12",
                                      "--output-json", str(json_out),
                                      "--output-markdown", str(markdown_out)])
            self.assertEqual(code, 1, stdout)
            payload = json.loads(json_out.read_text(encoding="utf-8"))
            self.assertFalse(payload["gates"]["every_row_within_tolerance"])
            self.assertGreater(payload["summary"]["rows_failing"], 0)
            self.assertIn("FAIL", markdown_out.read_text(encoding="utf-8"))


def unit_report_payload(perturb: float = 0.0) -> dict:
    """A Unit report shaped like source_model_reader's hd2-unit JSON output.

    perturb shifts one palette entry just enough that a very tight tolerance rejects it
    while the weight-pairing gate, which is fixed at 1e-5, still accepts the mesh.
    """
    palette_a = _palette(MESH_WORLD, WORLD_A)
    palette_a[12] += perturb
    return {
        "unit": {"file_id": "unit-cli", "geometry_source": "Unit GPU payload"},
        "package": {"name": "package-cli"},
        "transform_info": {
            "transforms": [
                {"index": 1, "name_hash": 11, "parent_index_raw": 65535,
                 "matrix_4x4_raw": MESH_WORLD},
                {"index": 4, "name_hash": 14, "parent_index_raw": 1,
                 "matrix_4x4_raw": WORLD_A},
                {"index": 5, "name_hash": 15, "parent_index_raw": 1,
                 "matrix_4x4_raw": WORLD_B},
            ],
            "resolved_skeleton": [
                {"transform_index": 1, "name": "mesh_node"},
                {"transform_index": 4, "name": "bone_a"},
                {"transform_index": 5, "name": "bone_b"},
            ],
        },
        "mesh_info": {"records": [{"index": 0, "mesh_id": 1000, "transform_index": 1,
                                   "lod_index": 0}]},
        "geometry": [{
            "index": 0,
            "mesh_id": 1000,
            "mesh_info_index": 0,
            "transform_index": 1,
            "lod_index": 0,
            "skinning": {
                "status": "ok",
                "palette": [
                    {"palette_slot": 0, "valid": True, "transform_index": 4,
                     "bone_info_matrix_raw": palette_a},
                    {"palette_slot": 1, "valid": True, "transform_index": 5,
                     "bone_info_matrix_raw": _palette(MESH_WORLD, WORLD_B)},
                ],
            },
            "attributes": [
                {"component_name": "position", "format_name": "vec3_float32",
                 "values": [list(point) for point in POINTS]},
                {"component_name": "bone_index", "format_name": "vec2_uint16",
                 "values": [list(row) for row in LANES]},
                {"component_name": "bone_weight", "format_name": "vec2_float32",
                 "values": [list(row) for row in WEIGHTS]},
            ],
            "sections": [],
        }],
    }


def _run_main(argv: list[str]) -> tuple[int, str]:
    previous = sys.argv
    sys.argv = argv
    try:
        with contextlib.redirect_stdout(io.StringIO()) as sink:
            code = main()
    finally:
        sys.argv = previous
    return code, sink.getvalue()


if __name__ == "__main__":
    unittest.main()
