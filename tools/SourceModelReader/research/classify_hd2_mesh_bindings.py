#!/usr/bin/env python3
"""Classify direct MeshInfo bindings from stored evidence, never from names.

Phase 2 of the roadmap asks for a schema that decides what a binding is from
LOD index, geometry counts, skin attributes, resolved influence lanes, the
mesh's own TransformInfo matrix and its hierarchy validation state - and that
keeps an explicit unknown fallback. Names such as culling, shadow or LOD1 are
reported for reference only and are never inputs to the decision.

Read-only over existing Unit JSON reports.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
IDENTITY_TOLERANCE = 1.0e-4
IDENTITY = [
    1.0, 0.0, 0.0, 0.0,
    0.0, 1.0, 0.0, 0.0,
    0.0, 0.0, 1.0, 0.0,
    0.0, 0.0, 0.0, 1.0,
]


def _identity_error(matrix: Any) -> float | None:
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
    return max(abs(a - b) for a, b in zip(values, IDENTITY))


def _lanes(skinning: dict[str, Any]) -> int | None:
    value = skinning.get("resolved_active_influence_count")
    if isinstance(value, bool) or not isinstance(value, int):
        return None
    return value


RECOGNIZED_SKINNING = ("ok", "partial", "no_skin_attributes", "geometry_unavailable")


def classify(evidence: dict[str, Any]) -> tuple[str, list[str]]:
    """Map evidence fields to one observed class plus the reasons that fired."""
    reasons: list[str] = []
    lod = evidence["lod_index"]
    skinning = evidence["skinning_status"]
    lanes = evidence["resolved_active_influence_count"]
    matrix = evidence["stored_matrix_identity_status"]
    hierarchy = evidence["hierarchy_validation_status"]

    # Unrecognized stored states must reach the fallback instead of being absorbed
    # by whichever rule happens to be written last.
    if skinning not in RECOGNIZED_SKINNING:
        return "unknown", [f"unrecognized skinning state {skinning!r}"]
    if lod is None:
        return "unknown", ["no LOD index on the binding"]

    if skinning == "geometry_unavailable" or evidence["vertex_count"] in (None, 0):
        return "undecoded_geometry", ["no decoded geometry rows for this binding"]
    if skinning == "no_skin_attributes":
        reasons.append("geometry declares no skin attributes")
        if lod == -1:
            reasons.append("LOD index is -1")
            return "auxiliary_unskinned", reasons
        return "unskinned_at_non_negative_lod", reasons
    if matrix == "unavailable":
        reasons.append("mesh's own TransformInfo matrix cannot be parsed")
        return "undecidable_placement", reasons
    if lanes and lanes > 0:
        reasons.append(f"{lanes} resolved influence lanes")
        if matrix == "non_identity":
            reasons.append("mesh's own TransformInfo matrix is not the identity")
            return "skinned_non_identity_placement", reasons
        if hierarchy != "matches":
            reasons.append(f"hierarchy validation is {hierarchy}")
            return "skinned_hierarchy_unverified", reasons
        return "skinned_render_lod", reasons
    if hierarchy != "matches":
        reasons.append(f"hierarchy validation is {hierarchy}")
        return "skinned_hierarchy_unverified", reasons
    reasons.append("skin attributes present but no resolved influence lanes")
    return "skinned_without_lanes", reasons


def collect(path: Path, label: str) -> dict[str, Any]:
    report = json.loads(path.read_text(encoding="utf-8"))
    transforms = {
        row["index"]: row
        for row in (report.get("transform_info") or {}).get("transforms") or []
        if isinstance(row, dict) and isinstance(row.get("index"), int)
    }
    names = {
        row["transform_index"]: row.get("name")
        for row in (report.get("transform_info") or {}).get("resolved_skeleton") or []
        if isinstance(row, dict) and isinstance(row.get("transform_index"), int)
    }
    hierarchy = {
        entry.get("transform_index"): entry
        for entry in ((report.get("transform_info") or {}).get("hierarchy_matrix_validation") or {}).get("entries") or []
        if isinstance(entry, dict)
    }
    geometry = {
        row.get("mesh_info_index"): row
        for row in report.get("geometry") or []
        if isinstance(row, dict)
    }

    rows: list[dict[str, Any]] = []
    for mesh in (report.get("mesh_info") or {}).get("records") or []:
        if not isinstance(mesh, dict):
            continue
        index = mesh.get("index")
        transform_index = mesh.get("transform_index")
        transform = transforms.get(transform_index) if isinstance(transform_index, int) else None
        geometry_row = geometry.get(index) if isinstance(index, int) else None
        skinning = (geometry_row or {}).get("skinning") or {}
        error = _identity_error(transform.get("matrix_4x4_raw") if transform else None)
        evidence = {
            "mesh_info_index": index,
            "mesh_id": mesh.get("mesh_id"),
            "lod_index": mesh.get("lod_index"),
            "transform_index": transform_index,
            "transform_name_reference_only": names.get(transform_index) if isinstance(transform_index, int) else None,
            "geometry_decoded": geometry_row is not None,
            "vertex_count": (geometry_row or {}).get("vertex_count"),
            "triangle_count": (geometry_row or {}).get("triangle_count"),
            "skinning_status": skinning.get("status", "geometry_unavailable"),
            "resolved_active_influence_count": _lanes(skinning),
            "unresolved_active_influence_count": skinning.get("unresolved_active_influence_count"),
            "bone_info_index": skinning.get("bone_info_index"),
            "stored_matrix_identity_status": (
                "unavailable" if error is None else
                "identity" if error <= IDENTITY_TOLERANCE else "non_identity"
            ),
            "stored_matrix_identity_error": error,
            "hierarchy_validation_status": (hierarchy.get(transform_index) or {}).get("status", "unavailable"),
        }
        observed_class, reasons = classify(evidence)
        evidence["observed_class"] = observed_class
        evidence["classification_reasons"] = reasons
        rows.append(evidence)

    unit = report.get("unit") or {}
    return {
        "label": label,
        "unit_id": unit.get("file_id"),
        "package_hint": (report.get("package") or {}).get("name"),
        "geometry_source": unit.get("geometry_source"),
        "binding_count": len(rows),
        "bindings": rows,
    }


def build_report(units: list[dict[str, Any]]) -> dict[str, Any]:
    tally: dict[str, int] = {}
    for unit in units:
        for row in unit["bindings"]:
            tally[row["observed_class"]] = tally.get(row["observed_class"], 0) + 1
    return {
        "schema": "SourceModelReader-HD2MeshBindingClassification1",
        "units": len(units),
        "bindings": sum(unit["binding_count"] for unit in units),
        "class_tally": dict(sorted(tally.items())),
        "class_definitions": [
            {"class": "skinned_render_lod", "requires": "decoded geometry, skin attributes, resolved lanes above zero, identity mesh matrix, matching hierarchy"},
            {"class": "skinned_non_identity_placement", "requires": "resolved lanes above zero and a non-identity mesh matrix; placement is applied through that matrix"},
            {"class": "auxiliary_unskinned", "requires": "no skin attributes and LOD -1"},
            {"class": "unskinned_at_non_negative_lod", "requires": "no skin attributes at LOD 0 or above; not seen in these samples"},
            {"class": "skinned_hierarchy_unverified", "requires": "skinned mesh whose hierarchy validation is not matching"},
            {"class": "undecidable_placement", "requires": "mesh's own TransformInfo matrix present but unparseable, so identity cannot be decided"},
            {"class": "skinned_without_lanes", "requires": "skin attributes present but no resolved lanes; not seen in these samples"},
            {"class": "undecoded_geometry", "requires": "no decoded geometry row for the binding"},
            {"class": "unknown", "requires": "fallback whenever no rule fires; never replaced by a name-based guess"},
        ],
        "units_detail": units,
        "interpretation_limits": [
            "Bone, mesh and node names are recorded for reference only and are never used to decide a class.",
            "Classes describe stored structure, not runtime use; only native or SDK-side observation can confirm a runtime role.",
            "The hierarchy and matrix states come from the parser's candidate conventions, which are internal-consistency results.",
            "A binding is reported undecoded_geometry when the Unit JSON has no geometry row; that is a decode scope fact, not a property of the mesh.",
        ],
    }


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# HD2 Mesh Binding Classification",
        "",
        f"- Units: {report['units']}",
        f"- Direct bindings: {report['bindings']}",
        "",
        "## Class tally",
        "",
        "| Class | Bindings |",
        "|---|---:|",
    ]
    for name, count in report["class_tally"].items():
        lines.append("| {} | {} |".format(name, count))
    lines.extend([
        "",
        "## Class definitions",
        "",
        "| Class | Requires |",
        "|---|---|",
    ])
    for definition in report["class_definitions"]:
        lines.append("| {} | {} |".format(definition["class"], definition["requires"]))
    lines.extend([
        "",
        "## Per-binding evidence",
        "",
    ])
    for unit in report["units_detail"]:
        lines.extend([
            f"### {unit['label']} (Unit {unit['unit_id']})",
            "",
            "| MeshInfo | Mesh | LOD | Skinning | Vertices | Lanes | Matrix | Hierarchy | Class | Name (reference only) |",
            "|---:|---:|---:|---|---:|---:|---|---|---|---|",
        ])
        for row in unit["bindings"]:
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                row["mesh_info_index"], row["mesh_id"], row["lod_index"],
                row["skinning_status"], row["vertex_count"],
                row["resolved_active_influence_count"], row["stored_matrix_identity_status"],
                row["hierarchy_validation_status"], row["observed_class"],
                row["transform_name_reference_only"] or "(unresolved)",
            ))
        lines.append("")
    lines.extend(["## Interpretation limits", ""])
    lines.extend("- " + item for item in report["interpretation_limits"])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unit", action="append", required=True, metavar="LABEL=PATH",
                        help="Unit JSON report to classify; repeat for every sample")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    units: list[dict[str, Any]] = []
    for item in args.unit:
        label, separator, path_text = item.partition("=")
        if not separator or not label or not path_text:
            parser.error("--unit expects LABEL=PATH")
        path = Path(path_text)
        if not path.is_file():
            parser.error(f"Unit JSON does not exist: {path}")
        try:
            units.append(collect(path, label))
        except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
            parser.error(f"Mesh binding classification failed for {path}: {exc}")

    report = build_report(units)
    markdown = render_markdown(report)
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Units {} bindings {} classes {}".format(
        report["units"], report["bindings"],
        ", ".join(f"{name}={count}" for name, count in report["class_tally"].items())))
    if not args.output_json and not args.output_markdown:
        print(markdown)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
