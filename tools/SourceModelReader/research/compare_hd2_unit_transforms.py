#!/usr/bin/env python3
"""Compare named HD2 TransformInfo entries across selected Unit JSON reports.

This is a read-only diagnostic. It compares stored local fields and stored 4x4
TransformInfo matrices without converting coordinate systems or claiming that
the matrices are universal inverse-bind/rest-pose data.
"""
from __future__ import annotations

import argparse
import gc
import json
import math
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_INPUTS = {
    "fs05_lower_body": ROOT / "research" / "skinning_audits" / "fs05_lower_body.json",
    "fs05_torso_left_arm": ROOT / "research" / "skinning_audits" / "fs05_torso_left_arm.json",
    "avatar_composite": ROOT / "research" / "skinning_audits" / "avatar_composite.json",
}
DEFAULT_OUTPUT_JSON = ROOT / "research" / "skeleton_comparison" / "hd2_unit_transform_comparison.json"
DEFAULT_OUTPUT_MARKDOWN = ROOT / "research" / "skeleton_comparison" / "hd2_unit_transform_comparison.md"
ROOT_PARENT = 0xFFFF


def _finite_vector(value: Any, length: int) -> list[float] | None:
    if not isinstance(value, list) or len(value) != length:
        return None
    try:
        result = [float(item) for item in value]
    except (TypeError, ValueError, OverflowError):
        return None
    if not all(math.isfinite(item) for item in result):
        return None
    return result


def _max_abs_error(left: list[float] | None, right: list[float] | None) -> float | None:
    if left is None or right is None or len(left) != len(right):
        return None
    return max((abs(a - b) for a, b in zip(left, right)), default=0.0)


def _parent_data(
    transform: dict[str, Any],
    skeleton_by_index: dict[int, dict[str, Any]],
) -> tuple[str | None, bool]:
    index = transform.get("index")
    parent = transform.get("parent_index_raw")
    if isinstance(parent, int) and not isinstance(parent, bool) and (
        parent == ROOT_PARENT or parent == index
    ):
        return None, True
    if isinstance(parent, bool) or not isinstance(parent, int):
        return None, False
    parent_row = skeleton_by_index.get(parent)
    if parent_row is None or parent_row.get("name_ambiguous") or not parent_row.get("name"):
        return None, False
    return str(parent_row["name"]), True


def load_unit(path: Path, label: str) -> dict[str, Any]:
    """Load one large hd2-unit JSON and retain only compact TransformInfo data."""
    report = json.loads(path.read_text(encoding="utf-8"))
    transform_info = report.get("transform_info") or {}
    transforms = transform_info.get("transforms") or []
    skeleton = transform_info.get("resolved_skeleton") or []
    skeleton_by_index = {
        row.get("transform_index"): row
        for row in skeleton
        if isinstance(row, dict) and isinstance(row.get("transform_index"), int)
    }

    records = []
    for transform in transforms:
        if not isinstance(transform, dict):
            continue
        index = transform.get("index")
        if isinstance(index, bool) or not isinstance(index, int):
            continue
        resolved = skeleton_by_index.get(index, {})
        name = resolved.get("name")
        ambiguous = bool(resolved.get("name_ambiguous"))
        parent_name, parent_resolved = _parent_data(transform, skeleton_by_index)
        records.append({
            "index": index,
            "name": str(name) if isinstance(name, str) and name else None,
            "name_ambiguous": ambiguous,
            "name_hash": transform.get("name_hash"),
            "parent_name": parent_name,
            "parent_resolved": parent_resolved,
            "parent_index_raw": transform.get("parent_index_raw"),
            "active": bool(resolved.get("has_active_vertex_influence")),
            "active_lane_count": int(resolved.get("active_vertex_influence_lane_count") or 0),
            "position": _finite_vector(transform.get("position"), 3),
            "rotation_3x3": _finite_vector(transform.get("rotation_3x3"), 9),
            "scale": _finite_vector(transform.get("scale"), 3),
            "matrix_4x4_raw": _finite_vector(transform.get("matrix_4x4_raw"), 16),
        })

    # Release the large geometry/attribute payload before loading the next Unit.
    unit = report.get("unit") or {}
    package = report.get("package") or {}
    result = {
        "label": label,
        "input": str(path),
        "unit_id": unit.get("file_id"),
        "package_id": package.get("name"),
        "geometry_source": unit.get("geometry_source"),
        "summary": report.get("summary") or {},
        "records": records,
    }
    del report
    gc.collect()
    return result


def _unique_named(records: list[dict[str, Any]]) -> tuple[dict[str, dict[str, Any]], list[str]]:
    grouped: dict[str, list[dict[str, Any]]] = {}
    for row in records:
        name = row.get("name")
        if not name or row.get("name_ambiguous"):
            continue
        grouped.setdefault(name, []).append(row)
    unique = {name: rows[0] for name, rows in grouped.items() if len(rows) == 1}
    ambiguous = sorted(name for name, rows in grouped.items() if len(rows) > 1)
    return unique, ambiguous


def _status(error: float | None, tolerance: float) -> str:
    if error is None:
        return "unavailable"
    return "matches" if error <= tolerance else "mismatch"


def compare_pair(
    left: dict[str, Any],
    right: dict[str, Any],
    tolerance: float,
) -> dict[str, Any]:
    left_by_name, left_ambiguous = _unique_named(left["records"])
    right_by_name, right_ambiguous = _unique_named(right["records"])
    common = sorted(set(left_by_name) & set(right_by_name))
    rows = []

    for name in common:
        a = left_by_name[name]
        b = right_by_name[name]
        if a["parent_resolved"] and b["parent_resolved"]:
            parent_status = "matches" if a["parent_name"] == b["parent_name"] else "mismatch"
        else:
            parent_status = "unavailable"

        position_error = _max_abs_error(a["position"], b["position"])
        rotation_error = _max_abs_error(a["rotation_3x3"], b["rotation_3x3"])
        scale_error = _max_abs_error(a["scale"], b["scale"])
        matrix_error = _max_abs_error(a["matrix_4x4_raw"], b["matrix_4x4_raw"])
        matrix_a = a["matrix_4x4_raw"]
        matrix_b = b["matrix_4x4_raw"]
        translation_error = (
            _max_abs_error(matrix_a[12:15], matrix_b[12:15])
            if matrix_a is not None and matrix_b is not None else None
        )
        linear_error = (
            _max_abs_error(matrix_a[:12], matrix_b[:12])
            if matrix_a is not None and matrix_b is not None else None
        )

        rows.append({
            "name": name,
            "left_transform_index": a["index"],
            "right_transform_index": b["index"],
            "left_parent": a["parent_name"] if a["parent_resolved"] else "(unresolved)",
            "right_parent": b["parent_name"] if b["parent_resolved"] else "(unresolved)",
            "parent_status": parent_status,
            "left_active": a["active"],
            "right_active": b["active"],
            "left_active_lane_count": a["active_lane_count"],
            "right_active_lane_count": b["active_lane_count"],
            "name_hash_equal": a["name_hash"] == b["name_hash"],
            "position_max_abs_error": position_error,
            "position_status": _status(position_error, tolerance),
            "rotation_max_abs_error": rotation_error,
            "rotation_status": _status(rotation_error, tolerance),
            "scale_max_abs_error": scale_error,
            "scale_status": _status(scale_error, tolerance),
            "stored_matrix_max_abs_error": matrix_error,
            "stored_matrix_status": _status(matrix_error, tolerance),
            "stored_matrix_linear_max_abs_error": linear_error,
            "stored_matrix_translation_max_abs_error": translation_error,
            "active_status": "matches" if a["active"] == b["active"] else "differs",
        })

    def count_status(key: str, expected: str = "matches") -> int:
        return sum(row[key] == expected for row in rows)

    matrix_errors = [
        row["stored_matrix_max_abs_error"]
        for row in rows if row["stored_matrix_max_abs_error"] is not None
    ]
    position_errors = [
        row["position_max_abs_error"]
        for row in rows if row["position_max_abs_error"] is not None
    ]
    summary = {
        "left": left["label"],
        "right": right["label"],
        "left_unit_id": left["unit_id"],
        "right_unit_id": right["unit_id"],
        "shared_unique_named_transform_count": len(common),
        "left_unique_named_transform_count": len(left_by_name),
        "right_unique_named_transform_count": len(right_by_name),
        "left_ambiguous_name_count": len(left_ambiguous),
        "right_ambiguous_name_count": len(right_ambiguous),
        "parent_match_count": count_status("parent_status"),
        "parent_mismatch_count": count_status("parent_status", "mismatch"),
        "parent_unavailable_count": count_status("parent_status", "unavailable"),
        "position_match_count": count_status("position_status"),
        "position_mismatch_count": count_status("position_status", "mismatch"),
        "rotation_match_count": count_status("rotation_status"),
        "rotation_mismatch_count": count_status("rotation_status", "mismatch"),
        "scale_match_count": count_status("scale_status"),
        "scale_mismatch_count": count_status("scale_status", "mismatch"),
        "stored_matrix_match_count": count_status("stored_matrix_status"),
        "stored_matrix_mismatch_count": count_status("stored_matrix_status", "mismatch"),
        "stored_matrix_unavailable_count": count_status("stored_matrix_status", "unavailable"),
        "active_flag_difference_count": sum(row["active_status"] == "differs" for row in rows),
        "max_position_abs_error": max(position_errors) if position_errors else None,
        "max_stored_matrix_abs_error": max(matrix_errors) if matrix_errors else None,
        "comparison_status": "compared" if common else "no_shared_unique_names",
        "tolerance": tolerance,
    }
    return {
        "summary": summary,
        "ambiguous_names": {
            "left": left_ambiguous,
            "right": right_ambiguous,
        },
        "transforms": rows,
    }


def build_report(units: list[dict[str, Any]], tolerance: float) -> dict[str, Any]:
    if len(units) != 3:
        raise ValueError("Exactly three Unit reports are required")
    pairs = {}
    for left_index, right_index in ((0, 1), (0, 2), (1, 2)):
        left = units[left_index]
        right = units[right_index]
        key = left["label"] + "__vs__" + right["label"]
        pairs[key] = compare_pair(left, right, tolerance)

    unique_maps = [_unique_named(unit["records"])[0] for unit in units]
    common_all = sorted(set(unique_maps[0]) & set(unique_maps[1]) & set(unique_maps[2]))
    tri_rows = []
    for name in common_all:
        rows = [mapping[name] for mapping in unique_maps]
        parents = [
            row["parent_name"] if row["parent_resolved"] else "(unresolved)"
            for row in rows
        ]
        matrices = [row["matrix_4x4_raw"] for row in rows]
        position_vectors = [row["position"] for row in rows]
        matrix_errors = [
            _max_abs_error(matrices[0], matrices[index])
            for index in (1, 2)
        ]
        position_errors = [
            _max_abs_error(position_vectors[0], position_vectors[index])
            for index in (1, 2)
        ]
        tri_rows.append({
            "name": name,
            "transform_indices": [row["index"] for row in rows],
            "parent_names": parents,
            "parent_consistent": len(set(parents)) == 1 and "(unresolved)" not in parents,
            "active_by_unit": [row["active"] for row in rows],
            "active_lane_counts": [row["active_lane_count"] for row in rows],
            "max_position_error_vs_first": max(
                (value for value in position_errors if value is not None), default=None
            ),
            "max_stored_matrix_error_vs_first": max(
                (value for value in matrix_errors if value is not None), default=None
            ),
            "stored_matrix_all_match": (
                len(matrix_errors) == 2
                and all(value is not None and value <= tolerance for value in matrix_errors)
            ),
        })

    pair_matrix_mismatches = sum(
        pair["summary"]["stored_matrix_mismatch_count"] for pair in pairs.values()
    )
    pair_parent_mismatches = sum(
        pair["summary"]["parent_mismatch_count"] for pair in pairs.values()
    )
    pair_unavailable_matrices = sum(
        pair["summary"]["stored_matrix_unavailable_count"] for pair in pairs.values()
    )
    pair_no_overlap = sum(
        pair["summary"]["shared_unique_named_transform_count"] == 0
        for pair in pairs.values()
    )
    pair_local_transform_unavailable = sum(
        pair["summary"]["position_mismatch_count"] * 0
        + sum(row["position_status"] == "unavailable"
              or row["rotation_status"] == "unavailable"
              or row["scale_status"] == "unavailable"
              for row in pair["transforms"])
        for pair in pairs.values()
    )
    return {
        "schema": "SourceModelReader-HD2UnitTransformComparison1",
        "tolerance": tolerance,
        "units": [
            {key: value for key, value in unit.items() if key != "records"}
            | {"transform_record_count": len(unit["records"])}
            for unit in units
        ],
        "pairwise_comparisons": pairs,
        "common_to_all_three": {
            "count": len(common_all),
            "transforms": tri_rows,
        },
        "summary": {
            "pairwise_stored_matrix_mismatches": pair_matrix_mismatches,
            "pairwise_parent_mismatches": pair_parent_mismatches,
            "pairwise_stored_matrix_unavailable": pair_unavailable_matrices,
            "pairwise_no_overlap": pair_no_overlap,
            "pairwise_local_transform_unavailable": pair_local_transform_unavailable,
            "three_way_shared_unique_named_transform_count": len(common_all),
            "status": "warning" if (
                pair_matrix_mismatches or pair_parent_mismatches
                or pair_unavailable_matrices or pair_no_overlap
                or pair_local_transform_unavailable
            ) else "ok",
        },
        "interpretation_limits": [
            "Only unique, resolved TransformInfo names are compared. Duplicate or unresolved names are excluded from name-based matching.",
            "Position, rotation_3x3 and scale are compared as stored local fields; no axis, handedness, unit-scale or quaternion conversion is applied.",
            "matrix_4x4_raw is compared as the stored TransformInfo 4x4 value. The existing parser independently checks its relationship to reconstructed hierarchy matrices for each Unit, but this cross-Unit comparison does not establish universal world-space or inverse-bind semantics.",
            "A numerical match is evidence of similarity between these selected Unit samples only; it does not prove animated deformation, runtime behavior, or Source-to-HD2 retarget correctness.",
            "Active vertex influence flags are mesh-specific. A bone can exist in the shared skeleton but be unused by one selected mesh.",
        ],
    }


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# HD2 Cross-Unit Transform Comparison",
        "",
        f"- Tolerance: {report['tolerance']}",
        f"- Overall diagnostic status: **{report['summary']['status']}**",
        "",
        "## Input Units",
        "",
        "| Sample | Unit | Package | Geometry | Transform records |",
        "|---|---|---|---|---:|",
    ]
    for unit in report["units"]:
        lines.append("| {} | {} | {} | {} | {} |".format(
            unit["label"], unit.get("unit_id"), unit.get("package_id"),
            unit.get("geometry_source"), unit["transform_record_count"],
        ))

    lines.extend([
        "",
        "## Pairwise summary",
        "",
        "| Pair | Shared named bones | Parent matches / mismatches | Local position matches / mismatches | Local rotation matches / mismatches | Stored matrix matches / mismatches / unavailable |",
        "|---|---:|---:|---:|---:|---:|",
    ])
    for pair in report["pairwise_comparisons"].values():
        s = pair["summary"]
        lines.append("| {} vs {} | {} | {} / {} | {} / {} | {} / {} | {} / {} / {} |".format(
            s["left"], s["right"], s["shared_unique_named_transform_count"],
            s["parent_match_count"], s["parent_mismatch_count"],
            s["position_match_count"], s["position_mismatch_count"],
            s["rotation_match_count"], s["rotation_mismatch_count"],
            s["stored_matrix_match_count"], s["stored_matrix_mismatch_count"],
            s["stored_matrix_unavailable_count"],
        ))

    lines.extend([
        "",
        "## Pairwise transform details",
        "",
    ])
    for key, pair in report["pairwise_comparisons"].items():
        s = pair["summary"]
        lines.extend([
            f"### {s['left']} vs {s['right']}",
            "",
            f"- Shared unique names: {s['shared_unique_named_transform_count']}",
            f"- Maximum local-position component error: {s['max_position_abs_error']}",
            f"- Maximum stored-matrix component error: {s['max_stored_matrix_abs_error']}",
            "",
            "| Bone | Parent A | Parent B | Parent | Position Δ | Rotation Δ | Scale Δ | Stored matrix Δ | Matrix translation Δ | Active A/B |",
            "|---|---|---|---|---:|---:|---:|---:|---:|---|",
        ])
        for row in pair["transforms"]:
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {}/{} |".format(
                row["name"], row["left_parent"], row["right_parent"], row["parent_status"],
                row["position_max_abs_error"], row["rotation_max_abs_error"],
                row["scale_max_abs_error"], row["stored_matrix_max_abs_error"],
                row["stored_matrix_translation_max_abs_error"],
                "yes" if row["left_active"] else "no",
                "yes" if row["right_active"] else "no",
            ))
        if not pair["transforms"]:
            lines.append("| — | — | — | — | — | — | — | — | — | — |")
        if pair["ambiguous_names"]["left"] or pair["ambiguous_names"]["right"]:
            lines.extend([
                "",
                "- Ambiguous names in left sample: {}".format(
                    ", ".join(pair["ambiguous_names"]["left"]) or "none"
                ),
                "- Ambiguous names in right sample: {}".format(
                    ", ".join(pair["ambiguous_names"]["right"]) or "none"
                ),
            ])
        lines.append("")

    common = report["common_to_all_three"]
    lines.extend([
        "## Names shared by all three samples",
        "",
        f"- Unique resolved names shared by all three: {common['count']}",
        "",
        "| Bone | Transform indices (lower / torso / Avatar) | Parent names consistent | Position Δ vs lower-body | Stored matrix Δ vs lower-body | All stored matrices match |",
        "|---|---|---|---:|---:|---|",
    ])
    for row in common["transforms"]:
        lines.append("| {} | {} | {} | {} | {} | {} |".format(
            row["name"], " / ".join(str(value) for value in row["transform_indices"]),
            "yes" if row["parent_consistent"] else "no",
            row["max_position_error_vs_first"],
            row["max_stored_matrix_error_vs_first"],
            "yes" if row["stored_matrix_all_match"] else "no",
        ))
    if not common["transforms"]:
        lines.append("| — | — | — | — | — | — |")

    lines.extend(["", "## Interpretation limits", ""])
    lines.extend("- " + item for item in report["interpretation_limits"])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lower-body", type=Path, default=DEFAULT_INPUTS["fs05_lower_body"])
    parser.add_argument("--torso-left-arm", type=Path, default=DEFAULT_INPUTS["fs05_torso_left_arm"])
    parser.add_argument("--avatar", type=Path, default=DEFAULT_INPUTS["avatar_composite"])
    parser.add_argument("--tolerance", type=float, default=1.0e-4)
    parser.add_argument("--output-json", type=Path, default=DEFAULT_OUTPUT_JSON)
    parser.add_argument("--output-markdown", type=Path, default=DEFAULT_OUTPUT_MARKDOWN)
    args = parser.parse_args()

    if not math.isfinite(args.tolerance) or args.tolerance < 0:
        parser.error("--tolerance must be a finite non-negative number")

    inputs = [
        ("fs05_lower_body", args.lower_body),
        ("fs05_torso_left_arm", args.torso_left_arm),
        ("avatar_composite", args.avatar),
    ]
    for _label, path in inputs:
        if not path.is_file():
            parser.error("Input JSON does not exist: {}".format(path))

    try:
        units = [load_unit(path, label) for label, path in inputs]
        report = build_report(units, args.tolerance)
        markdown = render_markdown(report)
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        parser.error("Transform comparison failed: {}".format(exc))

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Samples: {}".format(len(units)))
    print("All-three shared unique names: {}".format(report["common_to_all_three"]["count"]))
    print("Pairwise matrix mismatches: {}".format(report["summary"]["pairwise_stored_matrix_mismatches"]))
    print("Pairwise parent mismatches: {}".format(report["summary"]["pairwise_parent_mismatches"]))
    print("Diagnostic status: {}".format(report["summary"]["status"]))
    print("Markdown: {}".format(args.output_markdown))
    print("JSON: {}".format(args.output_json))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
