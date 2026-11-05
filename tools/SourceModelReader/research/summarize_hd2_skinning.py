"""Create a compact, reviewable skinning audit from an hd2-unit JSON report.

The report aggregates per-vertex influences by section and local bone index.
Use --include-all-influences only when a full lane-by-lane dump is needed.
"""
from __future__ import annotations

import argparse
import json
from collections import Counter, defaultdict
from pathlib import Path
from statistics import mean
from typing import Any


def _safe_name(value: Any) -> str:
    return str(value) if value not in (None, "") else "(unresolved)"


def _inline(value: Any) -> str:
    return chr(96) + str(value) + chr(96)


def _summarize_vertex_rows(vertex_rows: list[dict[str, Any]]) -> dict[str, Any]:
    influence_counts: Counter[int] = Counter()
    weight_sums: list[float] = []
    mappings: dict[tuple[Any, ...], list[float]] = defaultdict(list)
    examples_by_count: dict[int, dict[str, Any]] = {}

    for vertex in vertex_rows:
        rows = [
            item for item in vertex.get("influences", [])
            if float(item.get("weight", 0.0)) > 0.0
        ]
        weights = [float(item.get("weight", 0.0)) for item in rows]
        influence_counts[len(rows)] += 1
        weight_sums.append(sum(weights))

        if rows and len(rows) not in examples_by_count:
            examples_by_count[len(rows)] = {
                "vertex_index": vertex.get("vertex_index"),
                "influences": rows,
            }

        for item in rows:
            key = (
                item.get("local_bone_index"),
                item.get("palette_slot"),
                item.get("transform_index"),
                item.get("bone_name"),
                item.get("status"),
            )
            mappings[key].append(float(item.get("weight", 0.0)))

    mapping_rows = []
    targets_by_local: dict[Any, set[tuple[Any, ...]]] = defaultdict(set)
    for key, values in mappings.items():
        local, palette, transform, bone_name, status = key
        targets_by_local[local].add((palette, transform, bone_name, status))
        mapping_rows.append({
            "local_bone_index": local,
            "palette_slot": palette,
            "transform_index": transform,
            "bone_name": bone_name,
            "status": status,
            "influence_count": len(values),
            "weight_min": min(values),
            "weight_max": max(values),
            "weight_mean": mean(values),
        })

    mapping_rows.sort(key=lambda row: (
        -1 if row["local_bone_index"] is None else row["local_bone_index"],
        -1 if row["palette_slot"] is None else row["palette_slot"],
        -1 if row["transform_index"] is None else row["transform_index"],
    ))
    conflicts = sorted(
        (local for local, targets in targets_by_local.items() if len(targets) > 1),
        key=lambda value: -1 if value is None else value,
    )

    weight_stats = None
    if weight_sums:
        weight_stats = {
            "min": min(weight_sums),
            "max": max(weight_sums),
            "mean": mean(weight_sums),
            "within_1e_4": sum(abs(value - 1.0) <= 1.0e-4 for value in weight_sums),
            "within_1e_3": sum(abs(value - 1.0) <= 1.0e-3 for value in weight_sums),
        }

    return {
        "vertex_count": len(vertex_rows),
        "active_influence_count": sum(influence_counts.values()),
        "influence_count_histogram": dict(sorted(influence_counts.items())),
        "weight_sum_stats": weight_stats,
        "mapping_rows": mapping_rows,
        "mapping_conflict_local_indices": conflicts,
        "examples": [examples_by_count[key] for key in sorted(examples_by_count)],
    }


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
                vertex_rows = []
                for vertex in index_set.get("vertex_influences", []):
                    positive = []
                    for influence in vertex.get("influences", []):
                        if float(influence.get("weight", 0.0)) <= 0.0:
                            continue
                        item = {
                            "vertex_index": vertex.get("vertex_index"),
                            "lane": influence.get("lane"),
                            "weight": influence.get("weight"),
                            "local_bone_index": influence.get("local_bone_index"),
                            "palette_slot": influence.get("palette_slot"),
                            "transform_index": influence.get("transform_index"),
                            "bone_name": influence.get("bone_name"),
                            "status": influence.get("status"),
                        }
                        influences.append(item)
                        positive.append(item)
                    vertex_rows.append({
                        "vertex_index": vertex.get("vertex_index"),
                        "influences": positive,
                    })
                stats = _summarize_vertex_rows(vertex_rows)
                sets.append({
                    "component_index": index_set.get("component_index"),
                    "resolved_active_influence_count": index_set.get("resolved_active_influence_count", 0),
                    "unresolved_active_influence_count": index_set.get("unresolved_active_influence_count", 0),
                    "influences": influences,
                    **stats,
                })
            sections.append({
                "section_index": section.get("section_index"),
                "material_index": section.get("material_index"),
                "material_slot_id": section.get("material_slot_id"),
                "status": section.get("status"),
                "resolved_active_influence_count": section.get("resolved_active_influence_count", 0),
                "unresolved_active_influence_count": section.get("unresolved_active_influence_count", 0),
                "vertex_count": len(section.get("vertex_indices", [])),
                "bone_index_sets": sets,
            })
        meshes.append({
            "mesh_index": mesh_index,
            "mesh_id": mesh.get("mesh_id"),
            "lod_index": mesh.get("lod_index"),
            "vertex_count": mesh.get("vertex_count"),
            "index_count": mesh.get("index_count"),
            "skinning_status": skinning.get("status"),
            "bone_info_index": skinning.get("bone_info_index"),
            "weight_attribute": skinning.get("weight_attribute"),
            "resolved_active_influence_count": skinning.get("resolved_active_influence_count", 0),
            "unresolved_active_influence_count": skinning.get("unresolved_active_influence_count", 0),
            "matrix_validation": skinning.get("matrix_validation"),
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
    active_transforms.sort(key=lambda item: item.get("transform_index", -1))

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


def render_markdown(
    summary: dict[str, Any],
    max_examples_per_section: int = 4,
    include_all_influences: bool = False,
) -> str:
    if max_examples_per_section < 0 or max_examples_per_section > 64:
        raise ValueError("max_examples_per_section must be between 0 and 64")

    unit = summary.get("unit") or {}
    package = summary.get("package") or {}
    overall = summary.get("summary") or {}
    lines = [
        "# HD2 Unit Skinning Audit",
        "",
        f"- Unit: {_inline(unit.get('file_id', 'unknown'))}",
        f"- Package: {_inline(package.get('name', 'unknown'))}",
        f"- Geometry source: {_inline(unit.get('geometry_source', 'unknown'))}",
        "",
        "## Unit summary",
        "",
        "| Metric | Value |",
        "|---|---:|",
    ]
    for label, key in (
        ("Transforms", "transform_count"),
        ("BoneInfo records", "bone_info_count"),
        ("Meshes", "mesh_count"),
        ("Decoded meshes", "decoded_mesh_count"),
        ("Vertices", "vertex_count"),
        ("Indices", "index_count"),
        ("Triangles", "triangle_count"),
    ):
        if key in overall:
            lines.append(f"| {label} | {overall.get(key)} |")

    lines.extend([
        "",
        "## BoneInfo palettes",
        "",
        "| Record | Bone entries | RealIndices entries | Material remaps |",
        "|---:|---:|---:|---:|",
    ])
    for record in summary.get("bone_info_records", []):
        remaps = record.get("remaps", [])
        lines.append("| {} | {} | {} | {} |".format(
            record.get("index"),
            record.get("num_bones"),
            len(record.get("real_indices", [])),
            len(remaps),
        ))
        for remap_index, remap in enumerate(remaps):
            unique_slots = len(set(remap))
            valid_slots = sum(0 <= value < len(record.get("real_indices", [])) for value in remap)
            slot_range = f"{min(remap)}..{max(remap)}" if remap else "(empty)"
            lines.append(
                f"- BoneInfo[{record.get('index')}] Remap[{remap_index}]: "
                f"{len(remap)} entries, {unique_slots} unique palette slots, "
                f"valid references {valid_slots}/{len(remap)}, range {slot_range}."
            )

    lines.extend([
        "",
        "## Mesh / section resolution",
        "",
        "| Mesh | LOD | BoneInfo | Section | Material | Status | Vertices | Resolved lanes | Unresolved lanes |",
        "|---|---:|---:|---:|---:|---|---:|---:|---:|",
    ])
    for mesh in summary.get("meshes", []):
        for section in mesh.get("sections", []):
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                _inline(mesh.get("mesh_id")),
                mesh.get("lod_index"),
                mesh.get("bone_info_index"),
                section.get("section_index"),
                section.get("material_index"),
                section.get("status") or "unknown",
                section.get("vertex_count", 0),
                section.get("resolved_active_influence_count", 0),
                section.get("unresolved_active_influence_count", 0),
            ))

    lines.extend([
        "",
        "## Active TransformInfo usage",
        "",
        "| Transform | Bone | Active lanes | Parent |",
        "|---:|---|---:|---|",
    ])
    for item in summary.get("active_transforms", []):
        lines.append("| {} | {} | {} | {} |".format(
            item.get("transform_index"),
            _safe_name(item.get("name")),
            item.get("active_vertex_influence_lane_count", 0),
            _safe_name(item.get("parent_name")),
        ))

    lines.extend(["", "## Detailed section mapping", ""])
    for mesh in summary.get("meshes", []):
        lines.extend([
            f"### Mesh {_inline(mesh.get('mesh_id'))} / LOD {mesh.get('lod_index')}",
            "",
            f"- Skinning status: {_inline(mesh.get('skinning_status') or 'unknown')}",
            f"- BoneInfo record: {_inline(mesh.get('bone_info_index'))}",
            f"- Total resolved active lanes: {mesh.get('resolved_active_influence_count', 0)}",
            f"- Total unresolved active lanes: {mesh.get('unresolved_active_influence_count', 0)}",
            "",
        ])
        for section in mesh.get("sections", []):
            lines.extend([
                f"#### Section {section.get('section_index')} / material {section.get('material_index')}",
                "",
                f"- Status: {_inline(section.get('status') or 'unknown')}",
                f"- Resolved / unresolved: {section.get('resolved_active_influence_count', 0)} / "
                f"{section.get('unresolved_active_influence_count', 0)}",
                "",
            ])
            for index_set in section.get("bone_index_sets", []):
                lines.extend([
                    f"**Bone-index component {index_set.get('component_index')}**",
                    "",
                    f"- Active lanes: {index_set.get('active_influence_count', 0)}",
                    f"- Vertices with weight data: {index_set.get('vertex_count', 0)}",
                ])
                histogram = index_set.get("influence_count_histogram", {})
                if histogram:
                    distribution = ", ".join(
                        f"{count} influence(s): {vertices} vertices"
                        for count, vertices in sorted(histogram.items(), key=lambda item: int(item[0]))
                    )
                    lines.append(f"- Per-vertex influence distribution: {distribution}")
                weight_stats = index_set.get("weight_sum_stats")
                if weight_stats:
                    lines.append(
                        "- Per-vertex weight sum: min {:.6f}, max {:.6f}, mean {:.6f}; "
                        "{}/{} within 1e-3 of 1.0.".format(
                            weight_stats["min"], weight_stats["max"], weight_stats["mean"],
                            weight_stats["within_1e_3"], index_set.get("vertex_count", 0),
                        )
                    )
                lines.extend([
                    "",
                    "| Local index | Palette slot | TransformInfo | Bone | Active lanes | Weight range | Status |",
                    "|---:|---:|---:|---|---:|---:|---|",
                ])
                for row in index_set.get("mapping_rows", []):
                    lines.append("| {} | {} | {} | {} | {} | {:.6f}–{:.6f} | {} |".format(
                        row.get("local_bone_index"),
                        row.get("palette_slot"),
                        row.get("transform_index"),
                        _safe_name(row.get("bone_name")),
                        row.get("influence_count", 0),
                        row.get("weight_min", 0.0),
                        row.get("weight_max", 0.0),
                        row.get("status") or "unknown",
                    ))
                conflicts = index_set.get("mapping_conflict_local_indices", [])
                if conflicts:
                    lines.append(f"- Mapping conflicts: {', '.join(str(value) for value in conflicts)}")
                else:
                    lines.append("- Mapping conflicts: none detected.")

                examples = index_set.get("examples", [])
                if include_all_influences:
                    sample_rows = index_set.get("influences", [])
                else:
                    sample_rows = []
                    for example in examples[:max_examples_per_section]:
                        sample_rows.extend(example.get("influences", []))
                if sample_rows:
                    lines.extend([
                        "",
                        "**Representative influence rows**",
                        "",
                    ])
                    for influence in sample_rows:
                        lines.append(
                            "  - vertex {vertex_index}, lane {lane}: weight={weight}, "
                            "local={local_bone_index}, palette={palette_slot}, "
                            "transform={transform_index}, bone={bone_name}, status={status}".format(**influence)
                        )
                lines.append("")

        matrix = mesh.get("matrix_validation")
        if matrix:
            lines.extend([
                "**BoneInfo matrix consistency check**",
                "",
                f"- Status: {_inline(matrix.get('status'))}",
                f"- Candidate formula: {_inline(matrix.get('comparison_formula_candidate'))}",
                f"- Vector convention candidate: {_inline(matrix.get('vector_convention_candidate'))}",
                f"- Active palette slots: {matrix.get('active_palette_slot_count', 0)}",
                f"- Matches / mismatches / unavailable: "
                f"{matrix.get('matched_active_palette_slot_count', 0)} / "
                f"{matrix.get('mismatched_active_palette_slot_count', 0)} / "
                f"{matrix.get('unavailable_active_palette_slot_count', 0)}",
                f"- Unreferenced palette slots: {matrix.get('unreferenced_palette_slot_count', 0)}",
                f"- Maximum absolute error: {matrix.get('max_abs_error')}",
                "",
            ])
            exceptional = [
                row for row in matrix.get("palette_slots", [])
                if row.get("status") not in ("matches",)
            ]
            if exceptional:
                lines.extend(["| Palette slot | Transform | Check status | Max error |", "|---:|---:|---|---:|"])
                for row in exceptional[:32]:
                    lines.append("| {} | {} | {} | {} |".format(
                        row.get("palette_slot"), row.get("transform_index"),
                        row.get("status"), row.get("max_abs_error"),
                    ))
                lines.append("")

    if summary.get("warnings"):
        lines.extend(["## Warnings", ""])
        lines.extend(f"- {item}" for item in summary["warnings"])
    if summary.get("limitations"):
        lines.extend(["", "## Decoder limitations", ""])
        lines.extend(f"- {item}" for item in summary["limitations"])
    lines.extend([
        "",
        "> This report aggregates influence data by section and local index. "
        "Use --include-all-influences only for a full per-lane dump.",
    ])
    return chr(10).join(lines) + chr(10)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input_json", type=Path, help="JSON produced by the hd2-unit command")
    parser.add_argument("--output", type=Path, default=Path("research/hd2_unit_skinning_audit.md"))
    parser.add_argument("--max-examples-per-section", type=int, default=4)
    parser.add_argument("--include-all-influences", action="store_true")
    args = parser.parse_args()

    report = json.loads(args.input_json.read_text(encoding="utf-8"))
    summary = build_summary(report)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        render_markdown(
            summary,
            max_examples_per_section=args.max_examples_per_section,
            include_all_influences=args.include_all_influences,
        ),
        encoding="utf-8",
    )
    print(
        f"Meshes: {len(summary['meshes'])} | "
        f"Active transforms: {len(summary['active_transforms'])}"
    )
    print(f"Skinning audit: {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
