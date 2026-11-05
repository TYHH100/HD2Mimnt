#!/usr/bin/env python3
"""Census the third bone-index attribute across every Unit in one HD2 package.

The skinning resolver folds all three bone-index attributes into palette
resolution, which is where the unresolved influence lanes come from. This census
answers two narrow questions with real data instead of interpretation: does any
mesh carry more than one distinct non-zero value in that attribute, and where do
the marked vertices sit compared with the rest of the mesh.

Read-only. Nothing here changes the resolver or rewrites resource bytes.
"""
from __future__ import annotations

import argparse
import json
import statistics
import sys
from collections import Counter
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from hd2_resources import TYPE_IDS, parse_toc_package  # noqa: E402
from hd2_unit import HD2ResourceError, _read_package_from_game_data, inspect_hd2_unit  # noqa: E402


def attribute_rows(report: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for geometry in report.get("geometry") or []:
        for attribute in geometry.get("attributes") or []:
            if (
                attribute.get("component_name") != "bone_index"
                or attribute.get("format_name") != "vec4_uint32"
            ):
                continue
            values = attribute.get("values") or []
            counts: Counter = Counter(
                value
                for row in values
                for value in (row if isinstance(row, list) else [row])
                if isinstance(value, int) and not isinstance(value, bool) and value != 0
            )
            if not counts:
                continue
            positions = next(
                (a.get("values") or [] for a in geometry.get("attributes") or []
                 if a.get("component_name") == "position"),
                [],
            )
            marked = [
                index
                for index, row in enumerate(values)
                if any(isinstance(value, int) and value for value in (row if isinstance(row, list) else [row]))
            ]
            rows.append({
                "unit_id": (report.get("unit") or {}).get("file_id"),
                "mesh_info_index": geometry.get("mesh_info_index"),
                "lod_index": geometry.get("lod_index"),
                "skinning_status": (geometry.get("skinning") or {}).get("status"),
                "unresolved_active_influence_count": (geometry.get("skinning") or {}).get(
                    "unresolved_active_influence_count"
                ),
                "vertex_count": len(positions),
                "marked_vertex_count": len(marked),
                "distinct_nonzero_values": len(counts),
                "value_counts": {str(key): int(value) for key, value in sorted(counts.items())[:6]},
                "marked_mean_z": (
                    round(statistics.mean([positions[i][2] for i in marked]), 4) if marked else None
                ),
                "unmarked_mean_z": (
                    round(statistics.mean(
                        [positions[i][2] for i in range(len(positions)) if i not in set(marked)]
                    ), 4)
                    if marked and len(marked) < len(positions) else None
                ),
                "mesh_min_z": round(min(p[2] for p in positions), 4) if positions else None,
                "mesh_max_z": round(max(p[2] for p in positions), 4) if positions else None,
            })
    return rows


def render_markdown(report: dict[str, Any]) -> str:
    summary = report["summary"]
    lines = [
        "# HD2 Auxiliary Bone-Index Census",
        "",
        f"- Package: {report.get('package')}",
        f"- Units decoded: {summary['units_decoded']} (failed {summary['units_failed']})",
        f"- Meshes with a non-zero third bone-index attribute: {summary['meshes_with_values']}",
        f"- Meshes with more than one distinct value: {summary['meshes_multi_value']}",
        f"- Total unresolved influence lanes: {summary['unresolved_lanes_total']}",
        "",
        "| Unit | Mesh | LOD | Skinning | Vertices | Marked | Distinct values | Values | Marked mean z | Unmarked mean z | Mesh z range |",
        "|---|---:|---:|---|---:|---:|---:|---|---:|---:|---|",
    ]
    for row in report["rows"]:
        lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
            row["unit_id"], row["mesh_info_index"], row["lod_index"], row["skinning_status"],
            row["vertex_count"], row["marked_vertex_count"], row["distinct_nonzero_values"],
            ", ".join(f"{k}:{v}" for k, v in row["value_counts"].items()),
            row["marked_mean_z"], row["unmarked_mean_z"] if row["unmarked_mean_z"] is not None else "-",
            f"{row['mesh_min_z']}..{row['mesh_max_z']}",
        ))
    lines.extend([
        "",
        "## Interpretation limits",
        "",
        "- Decoding every Unit in one package is a census of that package only; other packages may differ.",
        "- A single distinct value per mesh does not identify what it indexes; it only rules out a per-vertex weight lane.",
        "- Marked and unmarked mean z are reported so a bone-name reading can be tested spatially rather than assumed.",
        "- The resolver is not modified by this census and no resource bytes are rewritten.",
        "",
    ])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_data", help="HD2 data directory")
    parser.add_argument("--package", required=True, help="Package/archive ID")
    parser.add_argument("--survey-json", type=Path, help="Unit list from the mesh-matrix survey; defaults to every Unit entry")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    game_data = Path(args.game_data)
    if not game_data.is_dir():
        parser.error("input must be the HD2 game data directory")

    toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(game_data, args.package)
    package = parse_toc_package(Path(args.package).name, toc_data, gpu_data, stream_data, source_kind)
    if args.survey_json and args.survey_json.is_file():
        survey = json.loads(args.survey_json.read_text(encoding="utf-8"))
        unit_ids = [row["unit_id"] for row in survey.get("units") or []]
    else:
        unit_ids = [f"{entry.file_id:016x}" for entry in package.entries
                    if entry.type_id == TYPE_IDS["Unit"]]

    rows: list[dict[str, Any]] = []
    failures = 0
    for unit_id in unit_ids:
        try:
            report = inspect_hd2_unit(game_data, args.package, unit_id)
        except (HD2ResourceError, OSError, ValueError, TypeError, KeyError):
            failures += 1
            continue
        rows.extend(attribute_rows(report))
        del report

    summary = {
        "units_decoded": len(unit_ids) - failures,
        "units_failed": failures,
        "meshes_with_values": len(rows),
        "meshes_multi_value": sum(1 for row in rows if row["distinct_nonzero_values"] > 1),
        "unresolved_lanes_total": sum(
            row["unresolved_active_influence_count"] or 0 for row in rows
        ),
    }
    report = {
        "schema": "SourceModelReader-HD2AuxBoneIndexCensus1",
        "package": package.name,
        "summary": summary,
        "rows": rows,
    }
    markdown = render_markdown(report)
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Units decoded {} (failed {}), meshes with values {}, multi-value meshes {}, unresolved lanes {}".format(
        summary["units_decoded"], summary["units_failed"], summary["meshes_with_values"],
        summary["meshes_multi_value"], summary["unresolved_lanes_total"]))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
