#!/usr/bin/env python3
"""Export a human-readable report from five compact Glass Unit JSON files.

This tool does not reread game packages or rerun hd2-unit.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
REPORT_DIR = ROOT / "research" / "unit_inspections"
INPUTS = (
    "glass_9423_direct.json",
    "glass_2f803_common.json",
    "glass_8b3ff_direct.json",
    "glass_0f320_lod.json",
    "glass_9423_composite.json",
)


def ident(value: Any, width: int = 8) -> str:
    if value is None:
        return "—"
    if isinstance(value, str):
        return value.lower().removeprefix("0x")
    if isinstance(value, int) and not isinstance(value, bool):
        return format(value, "0" + str(width) + "x")
    return str(value)


def material_map(items: list[dict[str, Any]]) -> dict[int, str]:
    result: dict[int, str] = {}
    for item in items:
        slot = item.get("section_id")
        mid = item.get("material_id")
        if isinstance(slot, str):
            try:
                slot = int(slot, 0)
            except ValueError:
                try:
                    slot = int(slot, 16)
                except ValueError:
                    slot = None
        if isinstance(mid, str):
            mid = mid.lower().removeprefix("0x")
        elif isinstance(mid, int):
            mid = format(mid, "016x")
        if isinstance(slot, int) and mid is not None:
            result[slot] = str(mid)
    return result


def attribute_text(items: list[dict[str, Any]]) -> str:
    if not items:
        return "none"
    return ", ".join(
        "{}[{}]={}".format(
            item.get("component_name", "type" + str(item.get("component_type"))),
            item.get("component_index", 0),
            item.get("format_name", "format" + str(item.get("format"))),
        )
        for item in items
    )


def render_report(report: dict[str, Any]) -> list[str]:
    job = report.get("source_job", "unknown")
    unit = report.get("unit", {})
    summary = report.get("summary", {})
    transform = report.get("transform_info", {})
    weight = transform.get("weight_usage_summary") or {}
    materials = report.get("materials", [])
    mat_by_slot = material_map(materials)
    active = [
        item for item in transform.get("resolved_skeleton", [])
        if item.get("has_active_vertex_influence")
    ]
    lines = [
        "## " + job,
        "",
        "- Package: " + str(report.get("source_package", "—")),
        "- Unit: " + str(unit.get("file_id", "—")),
        "- Queried Material: " + str(report.get("source_material_file_id", "—")),
        "- Geometry source: " + str(unit.get("geometry_source", "—")),
        "- Status: **{}** (exit code {})".format(
            summary.get("status", "unknown"),
            report.get("inspection_exit_code", "—"),
        ),
        "",
        "| Metric | Value |",
        "|---|---:|",
        "| Resource version | {} |".format(unit.get("resource_version", "—")),
        "| Transforms | {} |".format(summary.get("transform_count", "—")),
        "| BoneInfo records | {} |".format(summary.get("bone_info_count", "—")),
        "| StreamInfo records | {} |".format(summary.get("stream_count", "—")),
        "| MeshInfo records | {} |".format(summary.get("mesh_count", "—")),
        "| Decoded meshes | {} |".format(summary.get("decoded_mesh_count", "—")),
        "| Vertices summed across mesh/LOD records | {} |".format(summary.get("vertex_count", "—")),
        "| Indices | {} |".format(summary.get("index_count", "—")),
        "| Triangles | {} |".format(summary.get("triangle_count", "—")),
        "| Active weighted transforms | {} |".format(weight.get("actively_weighted_transform_count", "—")),
        "| Named active transforms | {} |".format(weight.get("actively_weighted_named_transform_count", "—")),
        "| Unresolved active influence lanes | {} |".format(weight.get("unresolved_active_influence_lane_count", "—")),
        "",
        "### Unit material table",
        "",
        "| Section/Slot ID | Material FileID | Queried target? |",
        "|---:|---|---|",
    ]
    for item in materials:
        slot = item.get("section_id")
        mid = item.get("material_id")
        mid_text = ident(mid, 16)
        target = mid_text == str(report.get("source_material_file_id", "")).lower()
        lines.append("| {} | {} | {} |".format(
            ident(slot), mid_text, "yes" if target else "no"
        ))
    if not materials:
        lines.append("| — | — | — |")

    lines.extend([
        "",
        "### Active skeleton transforms",
        "",
        "| Transform | Bone name | Name hash | Parent | Active influence lanes |",
        "|---:|---|---:|---|---:|",
    ])
    for item in active:
        parent = item.get("parent_name")
        if not parent and item.get("parent_transform_index") is not None:
            parent = "transform " + str(item.get("parent_transform_index"))
        if not parent:
            parent = "root / none"
        lines.append("| {} | {} | {} | {} | {} |".format(
            item.get("transform_index"),
            item.get("name") or "unresolved name",
            ident(item.get("name_hash")),
            parent,
            item.get("active_vertex_influence_lane_count", 0),
        ))
    if not active:
        lines.append("| — | no active weighted transforms | — | — | — |")

    lines.extend([
        "",
        "### Mesh and Section layout",
        "",
        "| Mesh ID | LOD | Vertices | Stride | Vertex components | Skinning | Sections |",
        "|---|---:|---:|---:|---|---|---|",
    ])
    for mesh in report.get("geometry", []):
        skinning = mesh.get("skinning") or {}
        by_section = {
            item.get("section_index"): item
            for item in skinning.get("section_skinning", [])
        }
        sections = []
        for section in mesh.get("sections", []):
            slot = section.get("material_slot_id")
            mid = mat_by_slot.get(slot, section.get("material_resource_id"))
            skin = by_section.get(section.get("index"), {})
            sections.append(
                "S{}: material={}, slot={}, vertices={}, indices={}, skin={}, resolved={}, unresolved={}".format(
                    section.get("index"),
                    ident(mid, 16) if mid is not None else "unmapped",
                    ident(slot),
                    section.get("num_vertices"),
                    section.get("num_indices"),
                    skin.get("status", "not evaluated"),
                    skin.get("resolved_active_influence_count", 0),
                    skin.get("unresolved_active_influence_count", 0),
                )
            )
        lines.append("| {} | {} | {} | {} | {} | {} | {} |".format(
            ident(mesh.get("mesh_id")),
            mesh.get("lod_index"),
            mesh.get("vertex_count"),
            mesh.get("vertex_stride"),
            attribute_text(mesh.get("attributes", [])),
            skinning.get("status", "not evaluated"),
            "<br>".join(sections) if sections else "no sections",
        ))

    lines.extend([
        "",
        "### Stream layouts",
        "",
        "| Stream | Vertices | Stride | Indices | Index type | Components |",
        "|---:|---:|---:|---:|---:|---|",
    ])
    records = report.get("stream_info", {}).get("records", [])
    for stream in records:
        lines.append("| {} | {} | {} | {} | {} | {} |".format(
            stream.get("index"),
            stream.get("num_vertices"),
            stream.get("vertex_stride"),
            stream.get("num_indices"),
            stream.get("index_buffer_type"),
            attribute_text(stream.get("components", [])),
        ))
    if not records:
        lines.append("| — | — | — | — | — | no StreamInfo records |")

    lines.extend([
        "",
        "### BoneInfo palettes and remaps",
        "",
        "| BoneInfo | Bones | Remap sizes | Unique resolved Transform indices |",
        "|---:|---:|---|---:|",
    ])
    records = report.get("bone_info", {}).get("records", [])
    for record in records:
        sizes = ", ".join(str(len(item)) for item in record.get("remaps", []))
        unique = {
            value
            for row in record.get("resolved_remaps", [])
            for value in row
            if value is not None
        }
        lines.append("| {} | {} | {} | {} |".format(
            record.get("index"),
            record.get("num_bones"),
            sizes or "none",
            len(unique),
        ))
    if not records:
        lines.append("| — | 0 | none | 0 |")

    lines.extend(["", "### Warnings", ""])
    warnings = report.get("warnings", [])
    if warnings:
        lines.extend("- " + str(item) for item in warnings)
    else:
        lines.append("None.")

    validation = transform.get("hierarchy_matrix_validation")
    if validation is not None:
        lines.extend([
            "",
            "### Transform hierarchy / matrix validation",
            "",
            "Matrix validation details:",
            json.dumps(validation, ensure_ascii=False, indent=2),
        ])
    return lines


def main() -> int:
    reports = []
    missing = []
    for name in INPUTS:
        path = REPORT_DIR / name
        if not path.is_file():
            missing.append(str(path))
            continue
        try:
            report = json.loads(path.read_text(encoding="utf-8-sig"))
        except (OSError, json.JSONDecodeError) as exc:
            missing.append("{}: {}".format(path, exc))
            continue
        if report.get("schema") != "SourceModelReader-GlassUnitCompact1":
            missing.append("{}: unsupported schema {}".format(path, report.get("schema")))
            continue
        reports.append(report)

    if missing:
        print("Missing or invalid reports:")
        for item in missing:
            print("- " + item)
        return 2

    lines = [
        "# Glass Unit Detailed Inspection",
        "",
        "Generated from the five existing compact Unit JSON reports.",
        "No game packages were reread and no hd2-unit inspection was rerun.",
        "",
        "Vertex totals are sums across decoded mesh/LOD records, not unique source-model vertex counts. Asset roles must not be inferred from counts alone.",
        "",
        "## Batch overview",
        "",
        "| Candidate | Unit | Material | Status | Geometry | Transforms | BoneInfo | Meshes | Vertices | Active weighted transforms | Unresolved lanes | Warnings |",
        "|---|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|",
    ]
    for report in reports:
        unit = report.get("unit", {})
        summary = report.get("summary", {})
        weight = report.get("transform_info", {}).get("weight_usage_summary") or {}
        lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
            report.get("source_job"),
            unit.get("file_id"),
            report.get("source_material_file_id"),
            summary.get("status"),
            unit.get("geometry_source"),
            summary.get("transform_count"),
            summary.get("bone_info_count"),
            summary.get("decoded_mesh_count"),
            summary.get("vertex_count"),
            weight.get("actively_weighted_transform_count"),
            weight.get("unresolved_active_influence_lane_count"),
            len(report.get("warnings", [])),
        ))
    lines.append("")
    for report in reports:
        lines.extend(render_report(report))
        lines.append("")

    output = REPORT_DIR / "glass_unit_detailed_inspection.md"
    output.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
    print("Loaded {} compact Unit reports.".format(len(reports)))
    print("Wrote: {}".format(output))
    print("Share this Markdown report; no LFS JSON payloads are needed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
