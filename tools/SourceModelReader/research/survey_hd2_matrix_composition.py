#!/usr/bin/env python3
"""Census the basis composition of every Unit matrix in one HD2 package.

The parser refuses to guess what a matrix that combines a non-unit, non-uniform scale
with a rotation means, and that refusal has only been justified from four Units so far.
Mirrored bases (a negative determinant) are the other arrangement a converter has to know
about before it maps a Source skeleton onto an HD2 rig. This pass reads only the Unit
tables on the TOC side - TransformInfo and BoneInfo - so it can cover a whole package
cheaply, and it answers three questions from stored data:

  * does any real matrix combine rotation with a non-uniform scale?
  * does any real basis carry a reflection?
  * which row norms and scale products actually appear, in node matrices and palettes?

This is a census of what the shipped files store, not a runtime observation.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

SCALE_RATIO_TOLERANCE = 1.0e-4
ORTHOGONAL_TOLERANCE = 1.0e-4
UNIT_SCALE_TOLERANCE = 1.0e-4
DEGENERATE_NORM = 1.0e-9
MAX_EXEMPLARS = 24

BASIS_CLASSES = (
    "basis_identity",
    "basis_uniform_scale",
    "basis_reflection",
    "basis_anisotropic_scale",
    "basis_rotation",
    "basis_rotation_with_non_uniform_scale",
    "basis_non_orthogonal",
    "basis_degenerate",
    "basis_unparseable",
)


def _matrix_rows(matrix: Any) -> list[list[float]] | None:
    """Return the three basis rows, or None when the matrix is not usable."""
    if not isinstance(matrix, list) or len(matrix) != 16:
        return None
    values: list[float] = []
    for item in matrix:
        if isinstance(item, bool) or not isinstance(item, (int, float)):
            return None
        number = float(item)
        if not math.isfinite(number):
            return None
        values.append(number)
    return [values[row * 4:row * 4 + 3] for row in range(3)]


def _det3(rows: list[list[float]]) -> float:
    a, b, c = rows[0]
    d, e, f = rows[1]
    g, h, i = rows[2]
    return a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)


def classify_basis(matrix: Any,
                   scale_tolerance: float = SCALE_RATIO_TOLERANCE,
                   orthogonal_tolerance: float = ORTHOGONAL_TOLERANCE,
                   unit_tolerance: float = UNIT_SCALE_TOLERANCE) -> dict[str, Any]:
    """Classify the 3x3 basis of a row-vector matrix, ignoring its translation.

    Row normalization separates scale from rotation. In the row-vector order an
    anisotropic scale applied before the rotation leaves orthogonal rows of unequal
    length, which is reported as rotation-with-non-uniform-scale; the rotation applied
    before the scale shears the rows, which is reported as non-orthogonal. Either way the
    combination is the composition the parser refuses to interpret.
    """
    rows = _matrix_rows(matrix)
    if rows is None:
        return {"basis_class": "basis_unparseable", "axis_scales": None, "scale_ratio": None,
                "orthogonality_error": None, "off_diagonal": None, "determinant": None,
                "scale_product": None, "mirrored": None}
    norms = [math.sqrt(sum(value * value for value in row)) for row in rows]
    determinant_raw = _det3(rows)
    if any(norm <= DEGENERATE_NORM for norm in norms):
        return {"basis_class": "basis_degenerate",
                "axis_scales": [round(value, 8) for value in norms],
                "scale_ratio": None, "orthogonality_error": None, "off_diagonal": None,
                "determinant": round(determinant_raw, 10),
                "scale_product": 0.0, "mirrored": determinant_raw < 0.0}
    normalized = [[value / norms[row] for value in rows[row]] for row in range(3)]
    error = 0.0
    off_diagonal = 0.0
    for row in range(3):
        for column in range(3):
            expected = 1.0 if row == column else 0.0
            dot = sum(normalized[row][k] * normalized[column][k] for k in range(3))
            error = max(error, abs(dot - expected))
            if row != column:
                off_diagonal = max(off_diagonal, abs(normalized[row][column]))
    ratio = max(norms) / min(norms)
    determinant = _det3(normalized)
    sheared = error > orthogonal_tolerance
    rotated = off_diagonal > orthogonal_tolerance
    non_uniform = ratio > 1.0 + scale_tolerance
    if determinant < 0.0:
        # A reflected basis is the fact a converter most needs, so it wins over the
        # structural label; axis scales, ratio, off-diagonal and shear are still reported
        # so a mirrored-and-anisotropic matrix stays visible in the fields.
        basis_class = "basis_reflection"
    elif sheared:
        basis_class = "basis_non_orthogonal"
    elif rotated and non_uniform:
        basis_class = "basis_rotation_with_non_uniform_scale"
    elif rotated:
        basis_class = "basis_rotation"
    elif non_uniform:
        basis_class = "basis_anisotropic_scale"
    elif abs(max(norms) - 1.0) <= unit_tolerance and abs(min(norms) - 1.0) <= unit_tolerance:
        basis_class = "basis_identity"
    else:
        basis_class = "basis_uniform_scale"
    return {"basis_class": basis_class,
            "axis_scales": [round(value, 8) for value in norms],
            "scale_ratio": round(ratio, 8),
            "orthogonality_error": round(error, 10),
            "off_diagonal": round(off_diagonal, 10),
            "determinant": round(determinant, 8),
            "scale_product": round(norms[0] * norms[1] * norms[2], 8),
            "mirrored": determinant < 0.0}


def _translation(matrix: Any) -> list[float] | None:
    if not isinstance(matrix, list) or len(matrix) != 16:
        return None
    return [round(float(matrix[12 + axis]), 6) for axis in range(3)]


def _bottom_row_deviation(matrix: Any) -> float | None:
    if not isinstance(matrix, list) or len(matrix) != 16:
        return None
    positions = (3, 7, 11, 15)
    expected = (0.0, 0.0, 0.0, 1.0)
    return max(abs(float(matrix[position]) - expected[index])
               for index, position in enumerate(positions))


def survey_unit_matrices(package: Any, entries: dict, unit_entry: Any) -> dict[str, Any]:
    """Classify every TransformInfo matrix and every BoneInfo palette matrix of one Unit."""
    from hd2_unit import _positive_next, parse_bone_info, parse_transform_info, parse_unit_header

    unit_toc, _gpu, _stream = package.payload(unit_entry)
    header = parse_unit_header(unit_toc)
    offsets = [
        header.transform_info_offset, header.light_list_offset, header.pre_light_offset,
        header.wwise_callback_offset, header.customization_offset,
        header.unknown_header_offset, header.connecting_bone_hash_offset,
        header.bone_info_offset,
        header.stream_info_offset if header.stream_info_offset > 1 else 0,
        header.mesh_info_offset, header.ending_offset, header.materials_offset,
    ]
    transforms = parse_transform_info(
        unit_toc, header.transform_info_offset,
        _positive_next(header.transform_info_offset, offsets, len(unit_toc))
        if header.transform_info_offset else len(unit_toc))
    bones = parse_bone_info(
        unit_toc, header.bone_info_offset,
        _positive_next(header.bone_info_offset, offsets, len(unit_toc))
        if header.bone_info_offset else len(unit_toc))

    rows: list[dict[str, Any]] = []
    for record in transforms.get("transforms") or []:
        if not isinstance(record, dict):
            continue
        matrix = record.get("matrix_4x4_raw")
        rows.append({"kind": "transform", "index": record.get("index"),
                     "parent_index_raw": record.get("parent_index_raw"),
                     "name_hash": record.get("name_hash"),
                     **classify_basis(matrix),
                     "translation": _translation(matrix),
                     "bottom_row_deviation": _bottom_row_deviation(matrix)})
    for bone_record in bones.get("records") or []:
        if not isinstance(bone_record, dict):
            continue
        real_indices = bone_record.get("real_indices") or []
        for slot, matrix in enumerate(bone_record.get("matrices") or []):
            rows.append({"kind": "bone_info", "index": bone_record.get("index"),
                         "palette_slot": slot,
                         "real_index": real_indices[slot] if slot < len(real_indices) else None,
                         **classify_basis(matrix),
                         "translation": _translation(matrix),
                         "bottom_row_deviation": _bottom_row_deviation(matrix)})
    counts = {name: 0 for name in BASIS_CLASSES}
    for row in rows:
        counts[row["basis_class"]] += 1
    return {
        "unit_id": f"{unit_entry.file_id:016x}",
        "is_composite": bool(header.composite_ref),
        "transform_table_count": transforms.get("count"),
        "bone_info_record_count": bones.get("count"),
        "matrix_count": len(rows),
        "counts": counts,
        "mirrored_count": sum(1 for row in rows if row.get("mirrored")),
        "rows": rows,
    }


def aggregate(units: list[dict[str, Any]]) -> dict[str, Any]:
    counts = {name: 0 for name in BASIS_CLASSES}
    by_kind: dict[str, dict[str, int]] = {}
    mirrored = 0
    scale_products: list[float] = []
    ratios: list[float] = []
    bottom_deviations: list[float] = []
    for unit in units:
        for row in unit["rows"]:
            counts[row["basis_class"]] += 1
            kind_row = by_kind.setdefault(row["kind"], {name: 0 for name in BASIS_CLASSES})
            kind_row[row["basis_class"]] += 1
            if row.get("mirrored"):
                mirrored += 1
            if row.get("scale_product"):
                scale_products.append(row["scale_product"])
            if row.get("scale_ratio"):
                ratios.append(row["scale_ratio"])
            if row.get("bottom_row_deviation") is not None:
                bottom_deviations.append(row["bottom_row_deviation"])
    unusual = ("basis_rotation_with_non_uniform_scale", "basis_non_orthogonal",
               "basis_reflection", "basis_degenerate", "basis_unparseable")
    # The scale list must be built before the per-matrix rows are dropped.
    scaled = scaled_matrices(units)
    return {
        "total_matrices": sum(counts.values()),
        "counts": counts,
        "counts_by_kind": by_kind,
        "mirrored_matrices": mirrored,
        "units_with_mirrored_basis": sorted(unit["unit_id"] for unit in units
                                            if unit["mirrored_count"]),
        "units_with_unusual_basis": sorted({
            unit["unit_id"] for unit in units
            if any(row["basis_class"] in unusual for row in unit["rows"])
        }),
        "scale_product_extremes": {
            "closest_to_zero": min(scale_products) if scale_products else None,
            "largest": max(scale_products) if scale_products else None,
        },
        "axis_scale_ratio_extremes": {
            "largest": max(ratios) if ratios else None,
            "count_above_one_plus_tolerance": sum(1 for value in ratios
                                                  if value > 1.0 + SCALE_RATIO_TOLERANCE),
        },
        "max_bottom_row_deviation": max(bottom_deviations) if bottom_deviations else None,
        "scaled_matrix_count": len(scaled),
        "refused_composition_present": any(counts[name] for name in unusual),
        "reflection_present": mirrored > 0,
    }


def scaled_matrices(units: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Every matrix whose basis rows are not unit length.

    The TOC-side pass has no name table, so transforms are identified by their stored
    name hash and palette entries by their BoneInfo record and real index.

    Attachment helpers are the usual carriers, and a palette matrix inherits scale from
    both of its factors, so the two kinds are listed together and separated by `kind`.
    """
    rows: list[dict[str, Any]] = []
    for unit in units:
        for row in unit["rows"]:
            norms = row.get("axis_scales")
            if not norms or row["basis_class"] in ("basis_degenerate", "basis_unparseable"):
                continue
            if all(abs(value - 1.0) <= UNIT_SCALE_TOLERANCE for value in norms):
                continue
            rows.append({"unit_id": unit["unit_id"], "kind": row["kind"],
                         "index": row.get("index"), "palette_slot": row.get("palette_slot"),
                         "name_hash": row.get("name_hash"),
                         "real_index": row.get("real_index"),
                         "basis_class": row["basis_class"], "axis_scales": norms,
                         "scale_ratio": row.get("scale_ratio"),
                         "scale_product": row.get("scale_product")})
    rows.sort(key=lambda item: (item["kind"], str(item.get("name_hash")), item["unit_id"],
                                item.get("index") or 0))
    return rows


def exemplars(units: list[dict[str, Any]]) -> list[dict[str, Any]]:
    unusual = ("basis_rotation_with_non_uniform_scale", "basis_non_orthogonal",
               "basis_degenerate", "basis_unparseable")
    rows: list[dict[str, Any]] = []
    # Mirrored bases are listed separately by flag, so they are not repeated here unless
    # they are also structurally unusual.
    for unit in units:
        for row in unit["rows"]:
            if row["basis_class"] in unusual or row.get("mirrored"):
                rows.append({"unit_id": unit["unit_id"], **row})
        if len(rows) >= MAX_EXEMPLARS:
            break
    return rows[:MAX_EXEMPLARS]


def render_markdown(package_name: str, unit_entry_count: int, units: list[dict[str, Any]],
                    summary: dict[str, Any], sample: list[dict[str, Any]],
                    scaled: list[dict[str, Any]],
                    failures: list[dict[str, Any]]) -> str:
    lines = [
        "# HD2 Matrix Composition Census",
        "",
        "- Package: `{}`".format(package_name),
        "- Unit entries: {}; Units censused: {}; failures: {}".format(
            unit_entry_count, len(units), len(failures)),
        "- Matrices classified: {}".format(summary["total_matrices"]),
        "- Refused composition (rotation with non-uniform scale, shear, degenerate, "
        "unparseable) present: **{}**".format(summary["refused_composition_present"]),
        "- Mirrored bases present: **{}** ({})".format(
            summary["reflection_present"], summary["mirrored_matrices"]),
        "",
        "## Counts by basis class",
        "",
        "| Basis class | All | TransformInfo | BoneInfo palette |",
        "|---|---:|---:|---:|",
    ]
    for name in BASIS_CLASSES:
        lines.append("| {} | {} | {} | {} |".format(
            name, summary["counts"][name],
            summary["counts_by_kind"].get("transform", {}).get(name, 0),
            summary["counts_by_kind"].get("bone_info", {}).get(name, 0)))
    lines.extend([
        "",
        "## Matrices carrying a non-unit scale",
        "",
    ])
    if not scaled:
        lines.append("None - every basis row has unit length.")
    else:
        lines.append("{} matrices; the first {} are listed.".format(
            len(scaled), min(len(scaled), 40)))
        lines.append("")
        lines.append("| Kind | Unit | Index | Slot | Name hash / real index | Class | "
                     "Axis scales | Ratio |")
        lines.append("|---|---|---:|---:|---|---|---|---:|")
        for row in scaled[:40]:
            label = (row.get("name_hash") if row.get("name_hash") is not None
                     else "real_index {}".format(row.get("real_index")))
            lines.append("| {} | `{}` | {} | {} | {} | {} | {} | {} |".format(
                row["kind"], row["unit_id"], row.get("index"),
                row.get("palette_slot") if row.get("palette_slot") is not None else "-",
                label, row["basis_class"], row["axis_scales"], row["scale_ratio"]))
    lines.extend([
        "",
        "## Scale spread",
        "",
        "- Scale product closest to zero: {}; largest: {}".format(
            summary["scale_product_extremes"]["closest_to_zero"],
            summary["scale_product_extremes"]["largest"]),
        "- Largest axis-scale ratio: {}; ratios above 1 + {}: {}".format(
            summary["axis_scale_ratio_extremes"]["largest"], SCALE_RATIO_TOLERANCE,
            summary["axis_scale_ratio_extremes"]["count_above_one_plus_tolerance"]),
        "- Max bottom-row deviation from `0 0 0 1`: {}".format(
            summary["max_bottom_row_deviation"]),
        "",
        "## Units carrying a mirrored basis",
        "",
    ])
    if summary["units_with_mirrored_basis"]:
        lines.extend("- `{}`".format(value) for value in summary["units_with_mirrored_basis"])
    else:
        lines.append("- None.")
    lines.extend(["", "## Unusual cases", ""])
    if not sample:
        lines.append("No mirrored, sheared, rotation-with-non-uniform-scale, degenerate or "
                     "unparseable basis found.")
    else:
        lines.append("| Unit | Kind | Index | Slot | Class | Axis scales | Ratio | "
                     "Ortho err | Det | Mirrored | Translation |")
        lines.append("|---|---|---:|---:|---|---|---:|---:|---:|---|---|")
        for row in sample:
            lines.append("| `{}` | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                row["unit_id"], row["kind"], row.get("index"), row.get("palette_slot", "-"),
                row["basis_class"], row.get("axis_scales"), row.get("scale_ratio"),
                row.get("orthogonality_error"), row.get("determinant"), row.get("mirrored"),
                row.get("translation")))
    lines.extend(["", "## Per-Unit totals", ""])
    if units:
        lines.append("| Unit | Composite | Transforms | BoneInfo records | Matrices | Identity | "
                     "Uniform scale | Rotation | Mirrored |")
        lines.append("|---|---|---:|---:|---:|---:|---:|---:|---:|")
        for unit in units:
            lines.append("| `{}` | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                unit["unit_id"], "yes" if unit["is_composite"] else "no",
                unit["transform_table_count"], unit["bone_info_record_count"],
                unit["matrix_count"], unit["counts"]["basis_identity"],
                unit["counts"]["basis_uniform_scale"], unit["counts"]["basis_rotation"],
                unit["mirrored_count"]))
    if failures:
        lines.extend(["", "## Failures", ""])
        for row in failures[:20]:
            lines.append("- `{}`: {}".format(row["unit_id"], row["error"]))
    lines.extend([
        "",
        "## Interpretation limits",
        "",
        "- A census of stored matrices; it says nothing about how the runtime composes them.",
        "- Only the 3x3 basis is classified, so a translation is never read as scale or rotation.",
        "- Row normalization separates `anisotropic scale then rotation` (orthogonal rows of "
        "unequal length) from `rotation then anisotropic scale` (sheared rows).",
        "- `basis_identity` means orthonormal unit rows; it does not assert the whole 4x4 matrix "
        "equals the identity, because the translation is excluded.",
        "- Mirroring is decided by the determinant of the normalized basis and takes the "
        "`basis_reflection` class; its scale and rotation indicators remain in the row.",
        "- Surveying one package says nothing about packages that were not read.",
        "",
    ])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_data", help="HD2 data directory containing bundles.nxa")
    parser.add_argument("--package", required=True, help="Package/archive ID to census")
    parser.add_argument("--limit", type=int, default=0, help="Stop after this many Units")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    if args.limit < 0:
        parser.error("--limit must be zero or positive")

    from hd2_resources import HD2ResourceError, TYPE_IDS, parse_toc_package
    from hd2_unit import _read_package_from_game_data

    try:
        toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(
            Path(args.game_data), args.package)
    except (HD2ResourceError, OSError, ValueError) as exc:
        parser.error("Could not read package {}: {}".format(args.package, exc))

    package = parse_toc_package(Path(args.package).name, toc_data, gpu_data, stream_data,
                                source_kind)
    entries = package.entry_map()
    unit_entries = [entry for entry in package.entries
                    if (entry.file_id, entry.type_id) in entries
                    and entry.type_id == TYPE_IDS["Unit"]]

    units: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    for entry in unit_entries:
        if args.limit and len(units) >= args.limit:
            break
        try:
            units.append(survey_unit_matrices(package, entries, entry))
        except (HD2ResourceError, OSError, ValueError, TypeError, KeyError) as exc:
            failures.append({"unit_id": f"{entry.file_id:016x}", "error": str(exc)})

    summary = aggregate(units)
    sample = exemplars(units)
    scaled = scaled_matrices(units)
    for unit in units:
        unit["rows"] = []
    markdown = render_markdown(package.name, len(unit_entries), units, summary, sample,
                               scaled, failures)
    payload = {
        "schema": "SourceModelReader-HD2MatrixComposition1",
        "package": package.name,
        "unit_entry_count": len(unit_entries),
        "censused_count": len(units),
        "failed_count": len(failures),
        "tolerances": {"scale_ratio": SCALE_RATIO_TOLERANCE,
                       "orthogonality": ORTHOGONAL_TOLERANCE,
                       "unit_scale": UNIT_SCALE_TOLERANCE,
                       "degenerate_norm": DEGENERATE_NORM},
        "summary": summary,
        "unusual_samples": sample,
        "scaled_matrices": scaled,
        "units": units,
        "failures": failures,
    }

    print("Package {}: {} Unit entries, {} censused, {} matrices, refused={}, mirrored={}".format(
        package.name, len(unit_entries), len(units), summary["total_matrices"],
        summary["refused_composition_present"], summary["mirrored_matrices"]))
    for name in BASIS_CLASSES:
        if summary["counts"][name]:
            print("  {}: {}".format(name, summary["counts"][name]))
    if failures:
        print("  failures: {}".format(len(failures)))
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
