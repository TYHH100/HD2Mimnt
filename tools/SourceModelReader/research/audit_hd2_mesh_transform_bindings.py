#!/usr/bin/env python3
"""Audit direct MeshInfo bindings, stored TransformInfo matrices and skinning.

This is a read-only report over existing hd2-unit JSON outputs. It does not
reinterpret vertex positions, apply transforms, or infer that a mesh is a
culling proxy from its name.
"""
from __future__ import annotations

import argparse
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
DEFAULT_OUTPUT_JSON = ROOT / "research" / "skeleton_comparison" / "hd2_mesh_transform_audit.json"
DEFAULT_OUTPUT_MARKDOWN = ROOT / "research" / "skeleton_comparison" / "hd2_mesh_transform_audit.md"
ROOT_PARENT = 0xFFFF


def _finite_vector(value: Any, length: int) -> list[float] | None:
    if not isinstance(value, list) or len(value) != length:
        return None
    try:
        result = [float(item) for item in value]
    except (TypeError, ValueError, OverflowError):
        return None
    return result if all(math.isfinite(item) for item in result) else None


def _identity_error(matrix: list[float] | None) -> float | None:
    if matrix is None:
        return None
    identity = [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0,
    ]
    return max(abs(a - b) for a, b in zip(matrix, identity))


def _index_map(rows: Any, key: str) -> dict[int, dict[str, Any]]:
    result: dict[int, dict[str, Any]] = {}
    if not isinstance(rows, list):
        return result
    for row in rows:
        if not isinstance(row, dict):
            continue
        index = row.get(key)
        if isinstance(index, int) and not isinstance(index, bool):
            result[index] = row
    return result


def _parent_chain(
    transform_index: int,
    transforms: dict[int, dict[str, Any]],
    skeleton: dict[int, dict[str, Any]],
) -> tuple[list[dict[str, Any]], str]:
    chain: list[dict[str, Any]] = []
    visited: set[int] = set()
    current = transform_index
    status = "root_reached"

    while current in transforms:
        if current in visited:
            status = "cycle_detected"
            break
        visited.add(current)
        transform = transforms[current]
        resolved = skeleton.get(current, {})
        parent = transform.get("parent_index_raw")
        chain.append({
            "transform_index": current,
            "name": resolved.get("name"),
            "name_hash": transform.get("name_hash"),
            "parent_index_raw": parent,
        })
        if (
            isinstance(parent, bool)
            or not isinstance(parent, int)
            or parent == ROOT_PARENT
            or parent == current
        ):
            status = "root_reached"
            break
        if parent not in transforms:
            status = "parent_missing"
            break
        current = parent
    else:
        status = "transform_missing"

    return chain, status


def load_unit(path: Path, label: str, tolerance: float) -> dict[str, Any]:
    report = json.loads(path.read_text(encoding="utf-8"))
    transform_info = report.get("transform_info") or {}
    mesh_info = report.get("mesh_info") or {}
    geometry_rows = report.get("geometry") or []

    transforms = _index_map(transform_info.get("transforms"), "index")
    skeleton = _index_map(transform_info.get("resolved_skeleton"), "transform_index")
    hierarchy = _index_map(
        (transform_info.get("hierarchy_matrix_validation") or {}).get("entries"),
        "transform_index",
    )
    geometry = _index_map(geometry_rows, "mesh_info_index")

    mesh_rows = mesh_info.get("records")
    if not isinstance(mesh_rows, list):
        mesh_rows = []

    meshes: list[dict[str, Any]] = []
    for mesh in mesh_rows:
        if not isinstance(mesh, dict):
            continue
        mesh_index = mesh.get("index")
        transform_index = mesh.get("transform_index")
        if (
            isinstance(mesh_index, bool)
            or not isinstance(mesh_index, int)
            or isinstance(transform_index, bool)
            or not isinstance(transform_index, int)
        ):
            continue

        transform = transforms.get(transform_index)
        resolved = skeleton.get(transform_index, {})
        geometry_row = geometry.get(mesh_index, {})
        skinning = geometry_row.get("skinning") or {}
        matrix_validation = skinning.get("matrix_validation") or {}
        hierarchy_validation = hierarchy.get(transform_index, {})
        matrix = _finite_vector(
            transform.get("matrix_4x4_raw") if transform else None, 16
        )
        identity_error = _identity_error(matrix)
        identity_status = (
            "unavailable" if identity_error is None
            else "identity" if identity_error <= tolerance
            else "non_identity"
        )
        parent_index = transform.get("parent_index_raw") if transform else None
        parent_row = transforms.get(parent_index) if isinstance(parent_index, int) else None
        parent_resolved = skeleton.get(parent_index, {}) if isinstance(parent_index, int) else {}

        chain, chain_status = _parent_chain(transform_index, transforms, skeleton)
        lane_count = skinning.get("resolved_active_influence_count")
        if isinstance(lane_count, bool) or not isinstance(lane_count, int):
            lane_count = None

        meshes.append({
            "mesh_info_index": mesh_index,
            "mesh_id": mesh.get("mesh_id"),
            "transform_index": transform_index,
            "transform_name": resolved.get("name"),
            "transform_name_hash": transform.get("name_hash") if transform else None,
            "lod_index": mesh.get("lod_index"),
            "vertex_count": geometry_row.get("vertex_count"),
            "index_count": geometry_row.get("index_count"),
            "triangle_count": geometry_row.get("triangle_count"),
            "skinning_status": skinning.get("status", "geometry_unavailable"),
            "resolved_active_influence_count": lane_count,
            "unresolved_active_influence_count": skinning.get(
                "unresolved_active_influence_count"
            ),
            "matrix_validation_status": matrix_validation.get(
                "status", "not_applicable"
            ),
            "matrix_validation_formula_candidate": matrix_validation.get(
                "comparison_formula_candidate"
            ),
            "matrix_validation_matched_slots": matrix_validation.get(
                "matched_active_palette_slot_count"
            ),
            "matrix_validation_mismatched_slots": matrix_validation.get(
                "mismatched_active_palette_slot_count"
            ),
            "matrix_validation_unavailable_slots": matrix_validation.get(
                "unavailable_active_palette_slot_count"
            ),
            "matrix_validation_max_abs_error": matrix_validation.get("max_abs_error"),
            "hierarchy_validation_status": hierarchy_validation.get("status", "unavailable"),
            "hierarchy_validation_max_abs_error": hierarchy_validation.get("max_abs_error"),
            "parent_transform_index": parent_index,
            "parent_transform_name": parent_resolved.get("name"),
            "parent_transform_name_hash": parent_row.get("name_hash") if parent_row else None,
            "parent_chain_status": chain_status,
            "parent_chain": chain,
            "stored_matrix_identity_status": identity_status,
            "stored_matrix_identity_max_abs_error": identity_error,
            "stored_matrix_4x4": matrix,
        })

    unit = report.get("unit") or {}
    package = report.get("package") or {}
    del report

    skinned = sum(
        1 for row in meshes
        if row["skinning_status"] == "ok"
        and (row["resolved_active_influence_count"] or 0) > 0
    )
    no_skin = sum(row["skinning_status"] == "no_skin_attributes" for row in meshes)
    matrix_matches = sum(row["matrix_validation_status"] == "matches" for row in meshes)
    matrix_mismatches = sum(row["matrix_validation_status"] == "mismatch" for row in meshes)
    hierarchy_matches = sum(row["hierarchy_validation_status"] == "matches" for row in meshes)

    return {
        "label": label,
        "input": str(path),
        "unit_id": unit.get("file_id"),
        "package_id": package.get("name"),
        "geometry_source": unit.get("geometry_source"),
        "mesh_count": len(meshes),
        "summary": {
            "direct_mesh_count": len(meshes),
            "identity_stored_matrix_count": sum(
                row["stored_matrix_identity_status"] == "identity" for row in meshes
            ),
            "non_identity_stored_matrix_count": sum(
                row["stored_matrix_identity_status"] == "non_identity" for row in meshes
            ),
            "skinned_mesh_count": skinned,
            "no_skin_attribute_mesh_count": no_skin,
            "bone_matrix_validation_matches": matrix_matches,
            "bone_matrix_validation_mismatches": matrix_mismatches,
            "hierarchy_validation_matches": hierarchy_matches,
        },
        "meshes": meshes,
    }


def build_report(units: list[dict[str, Any]], tolerance: float) -> dict[str, Any]:
    if len(units) != 3:
        raise ValueError("Exactly three Unit reports are required")
    return {
        "schema": "SourceModelReader-HD2MeshTransformAudit1",
        "tolerance": tolerance,
        "units": units,
        "interpretation_limits": [
            "The stored 4x4 matrix is reported without rewriting its values. The parser validates it against a reconstructed hierarchy matrix using the row-major row-vector and local-times-parent-world candidates; these conventions are candidates, not universal runtime proof.",
            "MeshInfo direct binding establishes a structural association, not that every bound mesh is a visible render mesh or a disposable culling proxy.",
            "A mesh with no skin attributes is not treated as a skinned mesh. A mesh with valid skin attributes and matching palette checks remains a skinning participant even if its name contains words such as culling.",
            "A matching hierarchy or BoneInfo matrix check validates internal consistency of the parsed representation; it does not prove in-game rendering, animation, or Source-to-HD2 vertex conversion.",
            "Vertex positions are not transformed or modified by this audit.",
        ],
    }


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# HD2 Mesh Transform Binding Audit",
        "",
        f"- Tolerance: {report['tolerance']}",
        "",
        "This report preserves direct MeshInfo bindings, geometry/LOD metadata, skinning status, hierarchy validation and the full stored 4x4 matrix.",
        "",
    ]
    for unit in report["units"]:
        summary = unit["summary"]
        lines.extend([
            f"## {unit['label']}",
            "",
            f"- Unit: {unit.get('unit_id')}",
            f"- Package: {unit.get('package_id')}",
            f"- Geometry source: {unit.get('geometry_source')}",
            f"- Meshes: {summary['direct_mesh_count']}",
            f"- Stored matrices: {summary['identity_stored_matrix_count']} identity / {summary['non_identity_stored_matrix_count']} non-identity",
            f"- Skinned meshes: {summary['skinned_mesh_count']}; no-skin-attribute meshes: {summary['no_skin_attribute_mesh_count']}",
            f"- Bone matrix validation: {summary['bone_matrix_validation_matches']} matches / {summary['bone_matrix_validation_mismatches']} mismatches",
            "",
            "| MeshInfo | Mesh ID | Transform | Name / hash | LOD | Vertices | Triangles | Skinning | Active lanes | Bone matrix check | Hierarchy check | Stored matrix |",
            "|---:|---:|---:|---|---:|---:|---:|---|---:|---|---|---|",
        ])
        for row in unit["meshes"]:
            name = row["transform_name"] or "(unresolved)"
            name_hash = row["transform_name_hash"]
            name_text = f"{name} / {name_hash}"
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} ({}) | {} ({}) | {} |".format(
                row["mesh_info_index"], row["mesh_id"], row["transform_index"],
                name_text, row["lod_index"], row["vertex_count"], row["triangle_count"],
                row["skinning_status"], row["resolved_active_influence_count"],
                row["matrix_validation_status"], row["matrix_validation_max_abs_error"],
                row["hierarchy_validation_status"], row["hierarchy_validation_max_abs_error"],
                row["stored_matrix_identity_status"],
            ))
        lines.append("")
        for row in unit["meshes"]:
            lines.extend([
                f"### MeshInfo {row['mesh_info_index']} / Mesh {row['mesh_id']}",
                "",
                f"- Transform: {row['transform_index']} — {row['transform_name'] or '(unresolved)'}",
                f"- LOD: {row['lod_index']}; vertices: {row['vertex_count']}; triangles: {row['triangle_count']}",
                f"- Skinning: {row['skinning_status']}; active lanes: {row['resolved_active_influence_count']}; unresolved lanes: {row['unresolved_active_influence_count']}",
                f"- Bone matrix validation: {row['matrix_validation_status']}; matched/mismatched/unavailable slots: {row['matrix_validation_matched_slots']}/{row['matrix_validation_mismatched_slots']}/{row['matrix_validation_unavailable_slots']}; max error: {row['matrix_validation_max_abs_error']}",
                f"- Hierarchy validation: {row['hierarchy_validation_status']}; max error: {row['hierarchy_validation_max_abs_error']}",
                f"- Stored matrix identity classification: {row['stored_matrix_identity_status']}; identity error: {row['stored_matrix_identity_max_abs_error']}",
                f"- Parent chain status: {row['parent_chain_status']}",
                "",
                "~~~text",
            ])
            for depth, node in enumerate(row["parent_chain"]):
                label = node["name"] or "(unresolved)"
                lines.append(
                    "  " * depth
                    + f"[{node['transform_index']}] {label} "
                    + f"(hash={node['name_hash']}, parent={node['parent_index_raw']})"
                )
            lines.extend(["~~~", "", "Stored 4x4 matrix (row-major):", "", "~~~text"])
            matrix = row["stored_matrix_4x4"]
            if matrix is None:
                lines.append("(unavailable)")
            else:
                for index in range(4):
                    values = matrix[index * 4:index * 4 + 4]
                    lines.append(", ".join(f"{value:.9g}" for value in values))
            lines.extend(["~~~", ""])

    lines.extend(["## Interpretation limits", ""])
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
            parser.error(f"Input JSON does not exist: {path}")

    try:
        units = [load_unit(path, label, args.tolerance) for label, path in inputs]
        report = build_report(units, args.tolerance)
        markdown = render_markdown(report)
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        parser.error(f"Mesh transform audit failed: {exc}")

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Samples: {}".format(len(units)))
    for unit in units:
        print("{}: {} meshes, {} skinned, {} without skin attributes, {} non-identity matrices".format(
            unit["label"], unit["summary"]["direct_mesh_count"],
            unit["summary"]["skinned_mesh_count"],
            unit["summary"]["no_skin_attribute_mesh_count"],
            unit["summary"]["non_identity_stored_matrix_count"],
        ))
    print(f"Markdown: {args.output_markdown}")
    print(f"JSON: {args.output_json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
