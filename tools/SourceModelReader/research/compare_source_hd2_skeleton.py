#!/usr/bin/env python3
"""Compare a compiled Source MDL/VVD skeleton with a selected HD2 Unit skeleton."""
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
from hd2_bone_aliases import build_alias_candidates
from hd2_unit import inspect_hd2_unit
from smd_reader import SMDDocument, SMDNode
from source_model_reader import AssetBundle, SourceModel


def render_markdown(report: dict[str, Any]) -> str:
    summary = report["summary"]
    target = report["target"]
    source = report.get("source") or {}
    lines = [
        "# Source / HD2 Skeleton Comparison",
        "",
        "## Source Model",
        "",
        "- MDL: {}".format(source.get("mdl_display")),
        "- MDL / VVD / VTX versions: {} / {} / {}".format(
            source.get("mdl_version"), source.get("vvd_version"), source.get("vtx_version")
        ),
        "- VVD vertex pool: {}".format(source.get("vertex_pool_count")),
        "- Active weighted source bones: {}".format(len(source.get("active_weighted_bone_ids") or [])),
        "- Checksum: {}".format(source.get("checksum")),
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
        "| Source node | Source parent | Active Source influence | HD2 Transform | HD2 name | HD2 parent | Active target influence | Parent relation |",
        "|---|---|---|---:|---|---|---|---|",
    ])
    for row in report["matched"]:
        lines.append("| {} ({}) | {} | {} | {} | {} | {} | {} ({} lanes) | {} |".format(
            row["source_name"],
            row["source_node_id"],
            row["source_parent_name"] or "root / none",
            "yes" if row.get("source_has_active_vertex_influence") else "no",
            row["target_transform_index"],
            row["target_name"],
            row["target_parent_name"] or "root / none",
            "yes" if row["target_has_active_vertex_influence"] else "no",
            row["target_active_vertex_influence_lane_count"],
            row["parent_relation"],
        ))
    if not report["matched"]:
        lines.append("| — | — | — | — | — | — | — | — |")

    alias_report = report.get("alias_candidates") or {}
    lines.extend([
        "",
        "## Advisory humanoid alias candidates",
        "",
        "This section is hypothesis-only. It does not modify exact-name matches or approve a retarget.",
        "",
        "| Source bone | Source active | HD2 candidate | Target active | Parent check |",
        "|---|---|---|---|---|",
    ])
    for row in alias_report.get("candidates", []):
        lines.append("| {} | {} | {} | {} | {} |".format(
            row["source_name"],
            "yes" if row["source_active_vertex_influence"] else "no",
            row["target_name"],
            "yes" if row["target_active_vertex_influence"] else "no",
            row["parent_status"],
        ))
    if not alias_report.get("candidates"):
        lines.append("| — | — | — | — | no candidates |")
    lines.extend([
        "",
        "Candidate count: {}. Parent matches: {}. Parent mismatches: {}. Parent checks deferred: {}.".format(
            alias_report.get("candidate_count", 0),
            alias_report.get("parent_match_count", 0),
            alias_report.get("parent_mismatch_count", 0),
            alias_report.get("parent_not_checked_count", 0),
        ),
        "",
        "All candidates remain advisory-only; rest-pose, matrix-space, side and deformation validation is still required.",
    ])

    sections = [
        ("Source bones without exact HD2 name", report["source_unmapped"], "source_name", "source_node_id"),
        ("HD2-only named transforms", report["target_only"], "target_name", "target_transform_index"),
        ("Ambiguous source matches", report["ambiguous_source_matches"], "source_name", "source_node_id"),
    ]
    for title, rows, name_key, id_key in sections:
        lines.extend(["", "## " + title, "", "| Name | ID / Transform | Active Source influence | Details |", "|---|---:|---|---|"])
        for row in rows:
            details = row.get("reason") or row.get("target_parent_name") or ""
            lines.append("| {} | {} | {} | {} |".format(
                row.get(name_key), row.get(id_key),
                ("—" if row.get("source_has_active_vertex_influence") is None
                 else ("yes" if row.get("source_has_active_vertex_influence") else "no")),
                details,
            ))
        if not rows:
            lines.append("| — | — | — | none |")

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
        "- The Source skeleton comes from compiled MDL bone definitions. Active source bones are derived from positive-weight VVD lanes across the stored vertex pool.",
        "- The VVD active-bone set is a union across the stored model vertex pool; it is not a per-BodyGroup or per-LOD usage report. Non-zero VVD fixup tables remain a parser limitation.",
        "- Names are matched exactly after Unicode case-folding and trimming edge whitespace.",
        "- No punctuation stripping, prefix guessing, fuzzy matching, or automatic aliasing is performed.",
        "- Parent relationships are compared only when both parents resolve to names.",
        "- Rest-pose numeric values are not compared. Source MDL local bone transforms and poseToBone matrices require a validated coordinate, unit-scale, and matrix-space conversion before comparison with HD2 TransformInfo.",
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
        help="Source GMA, ZIP, model directory, or one MDL file",
    )
    parser.add_argument(
        "--model",
        default="models/lc_84/aes_yz_pm.mdl",
        help="Compiled MDL path/name inside the GMA, ZIP, or model directory",
    )
    parser.add_argument(
        "--vtx-extension",
        default=".dx90.vtx",
        help="Sibling VTX extension, default .dx90.vtx",
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

    bundle = None
    try:
        bundle = AssetBundle(args.source_container, model_hint=args.model)
        source_model = SourceModel(bundle, vtx_extension=args.vtx_extension)

        source_nodes = [
            SMDNode(bone.index, bone.name, bone.parent)
            for bone in source_model.bones
        ]
        source_doc = SMDDocument(
            source=bundle.mdl_display,
            version=None,
            nodes=source_nodes,
            frames={},
            triangle_count=None,
            triangle_vertex_records=0,
            materials=[],
            warnings=list(source_model.warnings),
            infos=[],
        )

        active_source_bones: dict[int, int] = {}
        for vertex in source_model.vertices:
            for lane in range(vertex.num_bones):
                weight = float(vertex.weights[lane])
                bone_index = int(vertex.bones[lane])
                if weight > 1e-8 and 0 <= bone_index < len(source_model.bones):
                    active_source_bones[bone_index] = active_source_bones.get(bone_index, 0) + 1

        target_report = inspect_hd2_unit(
            args.game_data, args.target_package, args.target_unit
        )
        report = compare_source_skeleton_to_hd2(
            source_doc,
            target_report,
            source_active_node_ids=set(active_source_bones),
            source_kind="compiled_mdl_v{}".format(source_model.mdl_version),
        )
        report["alias_candidates"] = build_alias_candidates(report)
    except Exception as exc:
        parser.error("Skeleton comparison failed: {}".format(exc))
    finally:
        if bundle is not None:
            bundle.close()

    report["source"] = {
        "container": str(args.source_container),
        "model": args.model,
        "mdl_display": source_doc.source,
        "mdl_version": source_model.mdl_version,
        "vvd_version": source_model.vvd_version,
        "vtx_version": source_model.vtx_version,
        "vtx_extension": args.vtx_extension,
        "checksum": source_model.checksum,
        "vertex_pool_count": len(source_model.vertices),
        "active_weighted_bone_ids": sorted(active_source_bones),
        "active_weighted_lane_counts": {
            str(key): value for key, value in sorted(active_source_bones.items())
        },
        "source_warnings": list(source_model.warnings),
    }
    report["summary"]["source_vertex_pool_count"] = len(source_model.vertices)
    report["summary"]["source_active_weighted_bone_count"] = len(active_source_bones)
    report["summary"]["target_unit_decode_status"] = target_report.get("summary", {}).get("status")
    report["summary"]["target_unit_mesh_count"] = target_report.get("summary", {}).get("decoded_mesh_count")
    report["summary"]["target_unit_vertex_count"] = target_report.get("summary", {}).get("vertex_count")

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    args.output_markdown.write_text(render_markdown(report), encoding="utf-8")

    print("Source: {} ({} bones, {} active weighted)".format(
        source_doc.source,
        len(source_model.bones),
        len(active_source_bones),
    ))
    print("Target Unit: {}".format(args.target_unit))
    print("Exact name matches: {}".format(report["summary"]["exact_name_match_count"]))
    print("Source unmapped: {}".format(report["summary"]["source_unmapped_count"]))
    print("Active source exact matches: {}".format(report["summary"]["source_active_weighted_exact_match_count"]))
    print("Active source unmapped: {}".format(report["summary"]["source_active_weighted_unmapped_count"]))
    print("Active source ambiguous: {}".format(report["summary"]["source_active_weighted_ambiguous_count"]))
    print("HD2-only: {}".format(report["summary"]["target_only_named_transform_count"]))
    print("Parent mismatches: {}".format(report["summary"]["parent_mismatch_count"]))
    print("Status: {}".format(report["summary"]["status"]))
    print("JSON: {}".format(args.output_json))
    print("Markdown: {}".format(args.output_markdown))
    # Exit 1 means the report was generated but contains mapping/structure warnings.
    return 0 if report["summary"]["status"] == "ok" else 1


if __name__ == "__main__":
    raise SystemExit(main())
