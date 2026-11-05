import math
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from research.survey_hd2_matrix_composition import (  # noqa: E402
    BASIS_CLASSES,
    aggregate,
    classify_basis,
    exemplars,
    render_markdown,
    scaled_matrices,
)
from research.verify_hd2_retarget_deformation import (  # noqa: E402
    identity,
    multiply,
    rotate_x,
    rotate_z,
    translate,
)


def diagonal_scale(x: float, y: float, z: float) -> list[float]:
    matrix = identity()
    matrix[0], matrix[5], matrix[10] = x, y, z
    return matrix


def mirrored(x: float = -1.0, y: float = 1.0, z: float = 1.0) -> list[float]:
    return diagonal_scale(x, y, z)


class HD2MatrixClassificationTests(unittest.TestCase):
    def test_identity_basis(self):
        result = classify_basis(identity())
        self.assertEqual(result["basis_class"], "basis_identity")
        self.assertEqual(result["axis_scales"], [1.0, 1.0, 1.0])
        self.assertEqual(result["scale_ratio"], 1.0)
        self.assertFalse(result["mirrored"])
        self.assertAlmostEqual(result["determinant"], 1.0, places=9)

    def test_uniform_scale_is_not_read_as_identity(self):
        result = classify_basis(diagonal_scale(100.0, 100.0, 100.0))
        self.assertEqual(result["basis_class"], "basis_uniform_scale")
        self.assertEqual(result["scale_ratio"], 1.0)
        self.assertEqual(result["axis_scales"], [100.0, 100.0, 100.0])
        self.assertAlmostEqual(result["scale_product"], 1_000_000.0, places=3)

    def test_translation_never_counts_as_scale(self):
        result = classify_basis(translate(-0.28328, -0.04818, 1.43647))
        self.assertEqual(result["basis_class"], "basis_identity")
        self.assertEqual(result["scale_ratio"], 1.0)

    def test_anisotropic_diagonal_basis(self):
        result = classify_basis(diagonal_scale(1.0, 54.43227, 1.0))
        self.assertEqual(result["basis_class"], "basis_anisotropic_scale")
        self.assertAlmostEqual(result["scale_ratio"], 54.43227, places=4)
        self.assertFalse(result["mirrored"])
        self.assertLessEqual(result["off_diagonal"], 1.0e-9)

    def test_pure_rotation_basis(self):
        result = classify_basis(rotate_x(math.radians(30.0)))
        self.assertEqual(result["basis_class"], "basis_rotation")
        self.assertEqual(result["axis_scales"], [1.0, 1.0, 1.0])
        self.assertLessEqual(result["orthogonality_error"], 1.0e-9)
        self.assertGreater(result["off_diagonal"], 0.1)

    def test_axis_scale_then_rotation_keeps_orthogonal_rows(self):
        # p -> S then R: rows become scaled copies of the rotation rows, still orthogonal.
        matrix = multiply(diagonal_scale(1.0, 3.0, 1.0), rotate_z(math.radians(40.0)))
        result = classify_basis(matrix)
        self.assertEqual(result["basis_class"], "basis_rotation_with_non_uniform_scale")
        self.assertAlmostEqual(result["scale_ratio"], 3.0, places=6)
        self.assertLessEqual(result["orthogonality_error"], 1.0e-6)

    def test_rotation_then_axis_scale_shears_the_rows(self):
        matrix = multiply(rotate_z(math.radians(40.0)), diagonal_scale(1.0, 3.0, 1.0))
        result = classify_basis(matrix)
        self.assertEqual(result["basis_class"], "basis_non_orthogonal")
        self.assertGreater(result["orthogonality_error"], 1.0e-4)

    def test_reflection_takes_the_class_and_keeps_its_indicators(self):
        plain = classify_basis(mirrored())
        self.assertEqual(plain["basis_class"], "basis_reflection")
        self.assertTrue(plain["mirrored"])
        self.assertAlmostEqual(plain["determinant"], -1.0, places=9)

        combined = classify_basis(multiply(mirrored(), rotate_x(math.radians(35.0))))
        self.assertEqual(combined["basis_class"], "basis_reflection")
        self.assertTrue(combined["mirrored"])
        self.assertGreater(combined["off_diagonal"], 0.1)

    def test_degenerate_and_unparseable_bases(self):
        zero_row = [1.0, 0.0, 0.0, 0.0,
                    0.0, 0.0, 0.0, 0.0,
                    0.0, 0.0, 1.0, 0.0,
                    0.0, 0.0, 0.0, 1.0]
        self.assertEqual(classify_basis(zero_row)["basis_class"], "basis_degenerate")
        self.assertEqual(classify_basis(None)["basis_class"], "basis_unparseable")
        self.assertEqual(classify_basis([1.0] * 15)["basis_class"], "basis_unparseable")
        nan_matrix = identity()
        nan_matrix[0] = float("nan")
        self.assertEqual(classify_basis(nan_matrix)["basis_class"], "basis_unparseable")
        self.assertIsNone(classify_basis(zero_row)["scale_ratio"])


def _unit(unit_id: str, matrices: list[list[float]]) -> dict:
    rows = []
    for index, matrix in enumerate(matrices):
        rows.append({"kind": "transform", "index": index, "name_hash": 1000 + index,
                    "palette_slot": None, "real_index": None,
                    **classify_basis(matrix),
                    "translation": matrix[12:15], "bottom_row_deviation": 0.0})
    counts = {name: 0 for name in BASIS_CLASSES}
    for row in rows:
        counts[row["basis_class"]] += 1
    return {"unit_id": unit_id, "is_composite": False, "transform_table_count": len(rows),
            "bone_info_record_count": 0, "matrix_count": len(rows), "counts": counts,
            "mirrored_count": sum(1 for row in rows if row["mirrored"]), "rows": rows}


class HD2MatrixCensusAggregationTests(unittest.TestCase):
    def setUp(self):
        self.clean = [
            _unit("aa00", [identity(), rotate_x(math.radians(25.0)),
                           translate(0.1, 0.2, 0.3)]),
            _unit("bb00", [identity(), identity()]),
        ]
        self.unusual = [
            _unit("cc00", [multiply(rotate_z(math.radians(40.0)),
                                   diagonal_scale(1.0, 3.0, 1.0)),
                           mirrored(),
                           identity()]),
        ]

    def test_clean_census_reports_no_refusal_and_no_reflection(self):
        summary = aggregate(self.clean)
        self.assertFalse(summary["refused_composition_present"])
        self.assertFalse(summary["reflection_present"])
        self.assertEqual(summary["mirrored_matrices"], 0)
        self.assertEqual(summary["total_matrices"], 5)
        self.assertEqual(summary["units_with_unusual_basis"], [])
        self.assertEqual(summary["scaled_matrix_count"], 0)

    def test_refused_and_mirrored_cases_are_found_and_listed(self):
        summary = aggregate(self.unusual)
        self.assertTrue(summary["refused_composition_present"])
        self.assertEqual(summary["mirrored_matrices"], 1)
        self.assertEqual(summary["units_with_mirrored_basis"], ["cc00"])
        self.assertEqual(summary["units_with_unusual_basis"], ["cc00"])
        self.assertTrue(summary["counts"]["basis_non_orthogonal"] >= 1)
        sample = exemplars(self.unusual)
        self.assertTrue(any(row["basis_class"] == "basis_non_orthogonal" for row in sample))
        self.assertTrue(any(row["basis_class"] == "basis_reflection" for row in sample))

    def test_scaled_matrices_lists_only_non_unit_rows(self):
        units = [_unit("dd00", [identity(), diagonal_scale(100.0, 100.0, 100.0),
                                diagonal_scale(1.0, 54.0, 1.0)])]
        scaled = scaled_matrices(units)
        self.assertEqual(len(scaled), 2)
        self.assertEqual({row["scale_ratio"] for row in scaled}, {1.0, 54.0})
        self.assertTrue(all(row["kind"] == "transform" for row in scaled))

    def test_markdown_reports_counts_and_absence(self):
        units = self.clean
        summary = aggregate(units)
        markdown = render_markdown("pkg", 2, units, summary, exemplars(units),
                                   scaled_matrices(units), [])
        self.assertIn("# HD2 Matrix Composition Census", markdown)
        self.assertIn("## Counts by basis class", markdown)
        self.assertIn("None - every basis row has unit length.", markdown)
        self.assertIn("## Interpretation limits", markdown)
        for name in BASIS_CLASSES:
            self.assertIn(name, markdown)

    def test_markdown_lists_unusual_cases_and_failures(self):
        units = self.unusual
        summary = aggregate(units)
        markdown = render_markdown("pkg", 1, units, summary, exemplars(units),
                                   scaled_matrices(units),
                                   [{"unit_id": "ee00", "error": "broken table"}])
        self.assertIn("## Unusual cases", markdown)
        self.assertIn("basis_reflection", markdown)
        self.assertIn("broken table", markdown)
        self.assertIn("Refused composition", markdown)


if __name__ == "__main__":
    unittest.main()
