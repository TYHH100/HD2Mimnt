"""Create a compact, reviewable skinning audit from an hd2-unit JSON report.

The input report is produced by:
  source_model_reader.py hd2-unit ... --json unit.json

This script does not decode game resources itself and does not infer a new
palette convention. It summarizes the decoder's already-resolved evidence.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any


def _safe_name(value: Any) -> str:
    return str(value) if value not in (None, "") else "(unresolved)"


def build_summary(report: dict[str, Any]) -> dict[str, Any]:
    transform_info = report.get("transform_info") or {}
    bone_info = report.get("bone_info") or {}
    skeleton = transform_info.get("resolved_skeleton") or transform_info.get("skeleton") or []
    records = bone_info.get("records") or []
    geometry = report.get("geometry") or []

    meshes = []
    for mesh_index, mesh in enumerate(geometry):
        skinning = mesh.get("skinning") or {}
        sections = []
        for section in skinning.get("section_skinning", []):
            sets = []
            for index_set in section.get("bone_index_sets", []):
                influences = []
                for vertex in index_set.get("vertex_influences", []):
                    for influence in vertex.get("influences", []):
                        if influence.get("weight", 0) > 0:
                            influences.append({
                                "vertex_index": vertex.get("vertex_index"),
                                "lane": influence.get("lane"),
                                "weight": influence.get("weight"),
                                "local_bone_index": influence.get("local_bone_index"),
                                "palette_slot": influence.get("palette_slot"),
                                "transform_index": influence.get("transform_index"),
                                "bone_name": influence.get("bone_name"),
                                "status": influence.get("status"),
                            })
                sets.append({
                    "component_index": index_set.get("component_index"),
                    "resolved_active_influence_count": index_set.get("resolved_active_influence_count", 0),
                    "unresolved_active_influence_count": index_set.get("unresolved_active_influence_count", 0),
                    "influences": influences,
                })
            sections.append({
                "section_index": section.get("section_index"),
                "material_index": section.get("material_index"),
                "material_slot_id": section.get("material_slot_id"),
                "status": section.get("status"),
                "resolved_active_influence_count": section.get("resolved_active_influence_count", 0),
                "unresolved_active_influence_count": section.get("unresolved_active_influence_count", 0),
                "bone_index_sets": sets,
            })
        meshes.append({
            "mesh_index": mesh_index,
            "mesh_id": mesh.get("mesh_id"),
            "vertex_count": mesh.get("vertex_count"),
            "index_count": mesh.get("index_count"),
            "skinning_status": skinning.get("status"),
            "resolved_active_influence_count": skinning.get("resolved_active_influence_count", 0),
            "unresolved_active_influence_count": skinning.get("unresolved_active_influence_count", 0),
            "palette": skinning.get("palette", []),
            "sections": sections,
        })

    active_transforms = []
    for item in skeleton:
        if item.get("has_active_vertex_influence"):
            active_transforms.append({
                "transform_index": item.get("transform_index"),
                "name": item.get("name"),
                "active_vertex_influence_lane_count": item.get("active_vertex_influence_lane_count", 0),
                "parent_name": item.get("parent_name"),
            })

    return {
        "unit": report.get("unit"),
        "package": report.get("package"),
        "summary": report.get("summary"),
        "bone_info_records": [{
            "index": row.get("index"),
            "num_bones": row.get("num_bones"),
            "real_indices": row.get("real_indices", []),
            "remaps": row.get("remaps", []),
        } for row in records],
        "active_transforms": active_transforms,
        "meshes": meshes,
        "warnings": report.get("warnings", []),
        "limitations": report.get("limitations", []),
    }


def render_markdown(summary: dict[str, Any]) -> str:
    unit = summary.get("unit") or {}
    package = summary.get("package") or {}
    lines = [
        "# HD2 Unit Skinning Audit",
        "",
        f"- Unit: `{unit.get('file_id', 'unknown')}`",
        f"- Package: `{package.get('name', 'unknown')}`",
        "",
        "## BoneInfo records",
        "",
        "| Record | Bone count | RealIndices | Material remap count |",
        "|---:|---:|---|---:|",
    ]
    for record in summary.get("bone_info_records", []):
        lines.append("| {} | {} | {} | {} |".format(
            record.get("index"), record.get("num_bones"),
            ", ".join(str(x) for x in record.get("real_indices", [])),
            len(record.get("remaps", [])),
        ))
        for index, remap in enumerate(record.get("remaps", [])):
            lines.append(f"<!-- BoneInfo[{record.get('index')}] remap[{index}]: {remap} -->")
    lines.extend([
        "",
        "## Mesh / section influence summary",
        "",
        "| Mesh | Section | Material | Status | Resolved lanes | Unresolved lanes |",
        "|---|---:|---:|---|---:|---:|",
    ])
    for mesh in summary.get("meshes", []):
        for section in mesh.get("sections", []):
            lines.append("| {} | {} | {} | {} | {} | {} |".format(
                mesh.get("mesh_id"), section.get("section_index"),
                section.get("material_index"), section.get("status") or "ok",
                section.get("resolved_active_influence_count", 0),
                section.get("unresolved_active_influence_count", 0),
            ))
    lines.extend([
        "",
        "## Active transform usage",
        "",
        "| Transform | Name | Active influence lanes | Parent |",
        "|---:|---|---:|---|",
    ])
    for item in summary.get("active_transforms", []):
        lines.append("| {} | {} | {} | {} |".format(
            item.get("transform_index"), _safe_name(item.get("name")),
            item.get("active_vertex_influence_lane_count", 0),
            _safe_name(item.get("parent_name")),
        ))
    lines.extend(["", "## Palette resolution evidence", ""])
    for mesh in summary.get("meshes", []):
        lines.extend([
            f"### Mesh {mesh.get('mesh_id')} (index {mesh.get('mesh_index')})",
            "",
            f"- Skinning status: {mesh.get('skinning_status')}",
            f"- Resolved active lanes: {mesh.get('resolved_active_influence_count')}",
            f"- Unresolved active lanes: {mesh.get('unresolved_active_influence_count')}",
            "",
        ])
        for section in mesh.get("sections", []):
            lines.append(f"#### Section {section.get('section_index')} / material {section.get('material_index')}")
            for index_set in section.get("bone_index_sets", []):
                lines.append(
                    f"- Index component {index_set.get('component_index')}: "
                    f"{index_set.get('resolved_active_influence_count')} resolved / "
                    f"{index_set.get('unresolved_active_influence_count')} unresolved"
                )
                for influence in index_set.get("influences", []):
                    lines.append(
                        "  - vertex {vertex_index}, lane {lane}: weight={weight}, "
                        "local={local_bone_index}, palette={palette_slot}, "
                        "transform={transform_index}, bone={bone_name}, status={status}".format(**influence)
                    )
            lines.append("")
    if summary.get("warnings"):
        lines.extend(["## Warnings", ""])
        lines.extend(f"- {item}" for item in summary["warnings"])
    if summary.get("limitations"):
        lines.extend(["", "## Decoder limitations", ""])
        lines.extend(f"- {item}" for item in summary["limitations"])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_json", type=Path, help="JSON produced by the hd2-unit command")
    parser.add_argument("--output", type=Path, default=Path("research/hd2_unit_skinning_audit.md"))
    args = parser.parse_args()
    report = json.loads(args.input_json.read_text(encoding="utf-8"))
    summary = build_summary(report)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(render_markdown(summary), encoding="utf-8")
    print(f"Meshes: {len(summary['meshes'])} | Active transforms: {len(summary['active_transforms'])}")
    print(f"Skinning audit: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
