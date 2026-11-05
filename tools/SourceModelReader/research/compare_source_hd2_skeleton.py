#!/usr/bin/env python3
"""Compare one Source SMD skeleton with a selected HD2 Unit skeleton."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from hd2_skeleton_compare import compare_source_skeleton_to_hd2
from hd2_unit import inspect_hd2_unit
from smd_reader import SMDParseError, parse_smd_bytes
from source_model_reader import collect_smd_sources


def render_markdown(report: dict[str, Any]) -> str:
    summary = report["summary"]
    target = report["target"]
    lines = [
        "# Source / HD2 Skeleton Comparison",
        "",
        "## Target Unit",
        "",
        "- Unit: {}".format(target.get("unit_file_id")),
        "- Package: {}".format(target.get("package")),
        "- Geometry source: {}".format(target.get("geometry_source")),
        "- Bones resource: {}".format(target.get("bones_resource_file_id")),
        "",
        "## Summary",
        "",
        "| Metric | Value |",
        "|---|---:|",
    ]
    for key, value in summary.items():
        lines.append("| {} | {} |".format(key.replace("_", " "), value))

    lines.extend([
        "",
        "## Exact name matches",
        "",
        "| Source node | Source parent | HD2 Transform | HD2 name | HD2 parent | Active target influence | Parent relation |",
        "|---|---|---:|---|---|---|---|",
    ])
    for row in report["matched"]:
        lines.append("| {} ({}) | {} | {} | {} | {} | {} ({} lanes) | {} |".format(
            row["source_name"],
            row["source_node_id"],
            row["source_parent_name"] or "root / none",
            row["target_transform_index"],
            row["target_name"],
            row["target_parent_name"] or "root / none",
            "yes" if row["target_has_active_vertex_influence"] else "no",
            row["target_active_vertex_influence_lane_count"],
            row["parent_relation"],
        ))
    if not report["matched"]:
        lines.append("| — | — | — | — | — | — | — |")

    sections = [
        ("Source bones without exact HD2 name", report["source_unmapped"], "source_name", "source_node_id"),
        ("HD2-only named transforms", report["target_only"], "target_name", "target_transform_index"),
        ("Ambiguous source matches", report["ambiguous_source_matches"], "source_name", "source_node_id"),
    ]
    for title, rows, name_key, id_key in sections:
        lines.extend(["", "## " + title, "", "| Name | ID / Transform | Details |", "|---|---:|---|"])
        for row in rows:
            details = row.get("reason") or row.get("target_parent_name") or ""
            lines.append("| {} | {} | {} |".format(row.get(name_key), row.get(id_key), details))
        if not rows:
            lines.append("| — | — | none |")

    lines.extend([
        "",
        "## Ambiguous / unresolved HD2 names",
        "",
        "| Transform | Name hash | Ambiguous |",
        "|---:|---:|---|",
    ])
    unresolved = report["ambiguous_target_names"] + report["target_unresolved_names"]
    for row in unresolved:
        lines.append("| {} | {} | {} |".format(
            row.get("transform_index"), row.get("name_hash"), row.get("name_ambiguous")
        ))
    if not unresolved:
        lines.append("| — | — | none |")

    lines.extend([
        "",
        "## Parent hierarchy mismatches",
        "",
        "| Bone | Source parent | HD2 parent |",
        "|---|---|---|",
    ])
    for row in report["parent_mismatches"]:
        lines.append("| {} | {} | {} |".format(
            row["source_name"],
            row["source_parent_name"] or "root / none",
            row["target_parent_name"] or "root / none",
        ))
    if not report["parent_mismatches"]:
        lines.append("| — | — | none |")

    lines.extend([
        "",
        "## Matching policy and limitations",
        "",
        "- Names are matched exactly after Unicode case-folding and trimming edge whitespace.",
        "- No punctuation stripping, prefix guessing, fuzzy matching, or automatic aliasing is performed.",
        "- Parent relationships are compared only when both parents resolve to names.",
        "- Rest-pose numeric values are not compared. Source SMD local Euler transforms and HD2 TransformInfo matrices need a validated coordinate, unit-scale, and matrix-space conversion.",
        "- No retargeting or bone mapping is applied. This is a diagnostic report only.",
        "",
        "## Source warnings",
        "",
    ])
    lines.extend("- " + str(item) for item in report["source_warnings"])
    if not report["source_warnings"]:
        lines.append("None.")
    lines.extend(["", "## Target Unit warnings", ""])
    lines.extend("- " + str(item) for item in report["target_warnings"])
    if not report["target_warnings"]:
        lines.append("None.")
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source-container",
        required=True,
        type=Path,
        help="Source GMA, ZIP, SMD directory, or one SMD file",
    )
    parser.add_argument(
        "--source-entry",
        help="SMD path in the container; basename accepted only if unique",
    )
    parser.add_argument(
        "--game-data",
        required=True,
        type=Path,
        help="Helldivers 2 data directory",
    )
    parser.add_argument("--target-package", default="18235e0c9ec0e636")
    parser.add_argument("--target-unit", default="4d1c334d294dfa97")
    parser.add_argument(
        "--output-json",
        type=Path,
        default=ROOT / "research" / "skeleton_comparison" / "source_hd2_skeleton.json",
    )
    parser.add_argument(
        "--output-markdown",
        type=Path,
        default=ROOT / "research" / "skeleton_comparison" / "source_hd2_skeleton.md",
    )
    args = parser.parse_args()

    if not args.source_container.exists():
        parser.error("Source container does not exist: {}".format(args.source_container))
    if not args.game_data.is_dir():
        parser.error("HD2 game data directory does not exist: {}".format(args.game_data))

    try:
        sources, source_metadata = collect_smd_sources(args.source_container)
    except Exception as exc:
        parser.error("Could not read Source container: {}".format(exc))
    if not sources:
        parser.error("No SMD files were found in the Source container")

    if args.source_entry:
        query = args.source_entry.replace("\\", "/").casefold()
        matches = [
            (name, data) for name, data in sources
            if name.replace("\\", "/").casefold() == query
            or Path(name.replace("\\", "/")).name.casefold() == query
        ]
        if len(matches) != 1:
            choices = "\n".join("- " + name for name, _ in sources[:80])
            parser.error(
                "Source entry did not resolve to exactly one SMD. Available entries:\n" + choices
            )
        source_name, source_bytes = matches[0]
    elif len(sources) == 1:
        source_name, source_bytes = sources[0]
    else:
        choices = "\n".join("- " + name for name, _ in sources[:80])
        parser.error(
            "The source container has {} SMD files. Specify --source-entry.\n{}".format(
                len(sources), choices
            )
        )

    try:
        source_doc = parse_smd_bytes(source_bytes, source_name)
        target_report = inspect_hd2_unit(
            args.game_data, args.target_package, args.target_unit
        )
        report = compare_source_skeleton_to_hd2(source_doc, target_report)
    except (SMDParseError, Exception) as exc:
        parser.error("Skeleton comparison failed: {}".format(exc))

    report["source"] = {
        "container": str(args.source_container),
        "container_metadata": source_metadata,
        "entry": source_name,
    }
    report["summary"]["target_unit_decode_status"] = target_report.get("summary", {}).get("status")
    report["summary"]["target_unit_mesh_count"] = target_report.get("summary", {}).get("decoded_mesh_count")
    report["summary"]["target_unit_vertex_count"] = target_report.get("summary", {}).get("vertex_count")

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    args.output_markdown.write_text(render_markdown(report), encoding="utf-8")

    print("Source: {} ({})".format(source_name, source_doc.smd_type))
    print("Target Unit: {}".format(args.target_unit))
    print("Exact name matches: {}".format(report["summary"]["exact_name_match_count"]))
    print("Source unmapped: {}".format(report["summary"]["source_unmapped_count"]))
    print("HD2-only: {}".format(report["summary"]["target_only_named_transform_count"]))
    print("Parent mismatches: {}".format(report["summary"]["parent_mismatch_count"]))
    print("Status: {}".format(report["summary"]["status"]))
    print("JSON: {}".format(args.output_json))
    print("Markdown: {}".format(args.output_markdown))
    return 0 if report["summary"]["status"] == "ok" else 1


if __name__ == "__main__":
    raise SystemExit(main())
