#!/usr/bin/env python3
"""Survey every Unit in one HD2 package for position-space test material.

Phase 1 needs a Unit where a *skinned* mesh carries a non-identity TransformInfo
matrix and the Unit also has several LOD meshes: that is the only arrangement
where applying or not applying the mesh transform can be told apart on the same
surface. The three checked-in samples do not contain such a Unit, so this survey
finds candidates in one package by reading only the Unit tables on the TOC side —
no GPU geometry is decoded, which keeps the pass over a package cheap.
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
IDENTITY = [
    1.0, 0.0, 0.0, 0.0,
    0.0, 1.0, 0.0, 0.0,
    0.0, 0.0, 1.0, 0.0,
    0.0, 0.0, 0.0, 1.0,
]
IDENTITY_TOLERANCE = 1.0e-4


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


def survey_unit(package: Any, entries: dict, unit_entry: Any) -> dict[str, Any]:
    """Read the Unit's TOC-side tables and classify its mesh bindings."""
    from hd2_unit import (
        HD2ResourceError,
        _positive_next,
        parse_mesh_info,
        parse_transform_info,
    )
    from hd2_resources import TYPE_IDS, parse_unit_header

    unit_toc, _unit_gpu, _unit_stream = package.payload(unit_entry)
    header = parse_unit_header(unit_toc)
    offsets = [
        header.transform_info_offset, header.light_list_offset, header.pre_light_offset,
        header.wwise_callback_offset, header.customization_offset,
        header.unknown_header_offset, header.connecting_bone_hash_offset,
        header.bone_info_offset,
        header.stream_info_offset if header.stream_info_offset > 1 else 0,
        header.mesh_info_offset, header.ending_offset, header.materials_offset,
    ]
    transform_end = (_positive_next(header.transform_info_offset, offsets, len(unit_toc))
                     if header.transform_info_offset else len(unit_toc))
    transforms = parse_transform_info(
        unit_toc, header.transform_info_offset, transform_end)
    mesh_info = parse_mesh_info(
        unit_toc, header.mesh_info_offset,
        header.materials_offset or header.ending_offset or len(unit_toc))

    by_index: dict[int, dict[str, Any]] = {}
    for row in transforms.get("transforms") or []:
        if isinstance(row, dict) and isinstance(row.get("index"), int):
            by_index[row["index"]] = row
    names = {
        row["transform_index"]: row.get("name")
        for row in transforms.get("resolved_skeleton") or []
        if isinstance(row, dict) and isinstance(row.get("transform_index"), int)
    }

    bindings: list[dict[str, Any]] = []
    for mesh in mesh_info.get("records") or []:
        if not isinstance(mesh, dict):
            continue
        transform_index = mesh.get("transform_index")
        row = by_index.get(transform_index) if isinstance(transform_index, int) else None
        error = _identity_error(row.get("matrix_4x4_raw") if row else None)
        bindings.append({
            "index": mesh.get("index"),
            "mesh_id": mesh.get("mesh_id"),
            "lod_index": mesh.get("lod_index"),
            "transform_index": transform_index,
            "transform_name": names.get(transform_index) if isinstance(transform_index, int) else None,
            "transform_present": row is not None,
            "identity_error": error,
            "matrix_status": ("unavailable" if error is None
                              else "identity" if error <= IDENTITY_TOLERANCE
                              else "non_identity"),
        })

    lod_values = sorted({
        item["lod_index"] for item in bindings if isinstance(item["lod_index"], int)
    })
    non_identity = [item for item in bindings if item["matrix_status"] == "non_identity"]
    return {
        "unit_id": f"{unit_entry.file_id:016x}",
        "is_composite": bool(header.composite_ref),
        "composite_ref": f"{header.composite_ref:016x}" if header.composite_ref else None,
        "bones_ref": f"{header.bones_ref:016x}" if header.bones_ref else None,
        "transform_count": transforms.get("count"),
        "mesh_count": len(bindings),
        "lod_values": lod_values,
        "distinct_lod_count": len(lod_values),
        "non_identity_binding_count": len(non_identity),
        "non_identity_bindings": non_identity,
        "bindings": bindings,
        # A Unit is discriminating when several LODs exist and at least one direct
        # binding carries a non-identity matrix: the only way to separate the two
        # position-space hypotheses on one mesh set.
        "discriminating": bool(len(lod_values) >= 2 and non_identity),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_data", help="HD2 data directory containing bundles.nxa")
    parser.add_argument("--package", required=True, help="Package/archive ID to survey")
    parser.add_argument("--limit", type=int, default=0, help="Stop after this many Units")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    if args.limit < 0:
        parser.error("--limit must be zero or positive")

    from hd2_resources import HD2ResourceError, TYPE_IDS, parse_toc_package
    from hd2_unit import _read_package_from_game_data

    toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(
        Path(args.game_data), args.package)
    package = parse_toc_package(Path(args.package).name, toc_data, gpu_data,
                                stream_data, source_kind)
    entries = package.entry_map()
    unit_entries = [
        entry for entry in package.entries
        if (entry.file_id, entry.type_id) in entries and entry.type_id == TYPE_IDS["Unit"]
    ]

    rows: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []
    for entry in unit_entries:
        if args.limit and len(rows) >= args.limit:
            break
        try:
            rows.append(survey_unit(package, entries, entry))
        except (HD2ResourceError, OSError, ValueError, TypeError, KeyError) as exc:
            failures.append({"unit_id": f"{entry.file_id:016x}", "error": str(exc)})

    rows.sort(key=lambda row: (not row["discriminating"], -row["distinct_lod_count"],
                               -row["non_identity_binding_count"], row["unit_id"]))
    report = {
        "schema": "SourceModelReader-HD2UnitMeshMatrixSurvey1",
        "package": package.name,
        "unit_entry_count": len(unit_entries),
        "surveyed_count": len(rows),
        "failed_count": len(failures),
        "discriminating_count": sum(1 for row in rows if row["discriminating"]),
        "units": rows,
        "failures": failures,
        "interpretation_limits": [
            "Only Unit, TransformInfo and MeshInfo tables on the TOC side are read; geometry, "
            "skin attributes and BoneInfo palette validation are not evaluated here.",
            "A non-identity direct binding is a candidate discriminator, not proof: the mesh may "
            "be unskinned auxiliary geometry, which the full Unit JSON decode has to confirm.",
            "LOD values are taken from MeshInfo records as stored, including negative entries.",
            "Surveying one package says nothing about packages that were not read.",
        ],
    }

    lines = [
        "# HD2 Unit Mesh-Matrix Survey",
        "",
        f"- Package: {package.name}",
        f"- Unit entries in package: {len(unit_entries)}",
        f"- Surveyed: {len(rows)} (failed {len(failures)})",
        f"- Discriminating Units: {report['discriminating_count']}",
        "",
        "Discriminating means at least two distinct LOD values and at least one direct",
        "MeshInfo binding whose TransformInfo matrix is not the identity.",
        "",
        "| Unit | Composite | Transforms | Meshes | LOD values | Non-identity bindings | Discriminating |",
        "|---|---|---:|---:|---|---:|---|",
    ]
    for row in rows[:60]:
        lines.append("| {} | {} | {} | {} | {} | {} | {} |".format(
            row["unit_id"], "yes" if row["is_composite"] else "no",
            row["transform_count"], row["mesh_count"],
            ", ".join(str(value) for value in row["lod_values"]) or "-",
            row["non_identity_binding_count"], "YES" if row["discriminating"] else "no"))
    if len(rows) > 60:
        lines.append("")
        lines.append(f"(table truncated at 60 of {len(rows)} surveyed Units)")
    lines.extend(["", "## Non-identity direct bindings", ""])
    shown = 0
    for row in rows:
        for binding in row["non_identity_bindings"]:
            lines.append("- Unit {} mesh {} LOD {} transform {} name {} identity error {}".format(
                row["unit_id"], binding["mesh_id"], binding["lod_index"],
                binding["transform_index"], binding["transform_name"] or "(unresolved)",
                "unavailable" if binding["identity_error"] is None
                else format(binding["identity_error"], ".6g")))
            shown += 1
            if shown >= 40:
                break
        if shown >= 40:
            break
    if not shown:
        lines.append("- None found in this package.")
    lines.extend(["", "## Failures", ""])
    if failures:
        for item in failures[:20]:
            lines.append("- Unit {}: {}".format(item["unit_id"], item["error"]))
    else:
        lines.append("- None.")
    lines.extend(["", "## Interpretation limits", ""])
    lines.extend("- " + item for item in report["interpretation_limits"])
    markdown = "\n".join(lines) + "\n"

    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Package {}: {} Unit entries, {} surveyed, {} discriminating, {} failed".format(
        package.name, len(unit_entries), len(rows), report["discriminating_count"],
        len(failures)))
    for row in rows[:8]:
        print("  {} lods={} non_identity={} discriminating={}".format(
            row["unit_id"], row["distinct_lod_count"], row["non_identity_binding_count"],
            row["discriminating"]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
