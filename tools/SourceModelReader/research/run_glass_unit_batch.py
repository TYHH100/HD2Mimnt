#!/usr/bin/env python3
"""Run focused regression tests and a compact five-Unit Glass inspection batch.

The raw hd2-unit JSON can contain large per-vertex arrays. This runner parses
one Unit at a time, writes a compact structural/skinning report, then deletes
the temporary raw JSON. It never modifies game files.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[1]
DEFAULT_GAME_DATA = Path(
    r"G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data"
)
TEST_MODULES = (
    "test_hd2_texture.py",
    "test_hd2_material_users.py",
)
JOBS = (
    {
        "name": "glass_9423_direct",
        "package": "09985dc611a3a8b6",
        "unit": "12738f0c0c22b686",
        "material": "9423bb5f9a715c2e",
    },
    {
        "name": "glass_2f803_common",
        "package": "029a193f2a08167c",
        "unit": "246144231754fff4",
        "material": "2f80391371cce33e",
    },
    {
        "name": "glass_8b3ff_direct",
        "package": "01edaa2a7e84b840",
        "unit": "2d3bc1683a54298d",
        "material": "8b3ff5502ac52c25",
    },
    {
        "name": "glass_0f320_lod",
        "package": "1e33cc1600ff38f3",
        "unit": "4e401cc877093220",
        "material": "0f3201437c248dfc",
    },
    {
        "name": "glass_9423_composite",
        "package": "fc5b6bff0db90aab",
        "unit": "5367951cd4da72d9",
        "material": "9423bb5f9a715c2e",
    },
)


def compact_section(section: dict[str, Any]) -> dict[str, Any]:
    fields = (
        "index",
        "material_index",
        "material_slot_id",
        "material_resource_id",
        "vertex_offset",
        "num_vertices",
        "index_offset",
        "num_indices",
        "group_index",
        "invalid_index_count",
    )
    return {key: section.get(key) for key in fields}


def compact_skinning(skinning: dict[str, Any] | None) -> dict[str, Any] | None:
    if not skinning:
        return None

    palette = []
    for item in skinning.get("palette", []):
        palette.append({
            "palette_slot": item.get("palette_slot"),
            "transform_index": item.get("transform_index"),
            "name_hash": item.get("name_hash"),
            "name": item.get("name"),
            "valid": item.get("valid"),
        })

    section_rows = []
    for section in skinning.get("section_skinning", []):
        sets = []
        for item in section.get("bone_index_sets", []):
            sets.append({
                "component_index": item.get("component_index"),
                "resolved_active_influence_count": item.get(
                    "resolved_active_influence_count"
                ),
                "unresolved_active_influence_count": item.get(
                    "unresolved_active_influence_count"
                ),
                "out_of_range_vertex_count": item.get("out_of_range_vertex_count"),
            })
        section_rows.append({
            "section_index": section.get("section_index"),
            "material_index": section.get("material_index"),
            "material_slot_id": section.get("material_slot_id"),
            "status": section.get("status"),
            "resolved_active_influence_count": section.get(
                "resolved_active_influence_count"
            ),
            "unresolved_active_influence_count": section.get(
                "unresolved_active_influence_count"
            ),
            "out_of_range_vertex_count": section.get("out_of_range_vertex_count"),
            "bone_index_sets": sets,
        })

    return {
        "status": skinning.get("status"),
        "lod_index": skinning.get("lod_index"),
        "bone_info_index": skinning.get("bone_info_index"),
        "weight_attribute": skinning.get("weight_attribute"),
        "bone_index_set_count": skinning.get("bone_index_set_count"),
        "resolved_active_influence_count": skinning.get(
            "resolved_active_influence_count"
        ),
        "unresolved_active_influence_count": skinning.get(
            "unresolved_active_influence_count"
        ),
        "out_of_range_vertex_count": skinning.get("out_of_range_vertex_count"),
        "palette": palette,
        "section_skinning": section_rows,
        "matrix_validation": skinning.get("matrix_validation"),
    }


def compact_report(
    report: dict[str, Any], job: dict[str, str], exit_code: int
) -> dict[str, Any]:
    geometry = []
    for mesh in report.get("geometry", []):
        attributes = []
        for attribute in mesh.get("attributes", []):
            attributes.append({
                "component_type": attribute.get("component_type"),
                "component_name": attribute.get("component_name"),
                "component_index": attribute.get("component_index"),
                "format": attribute.get("format"),
                "format_name": attribute.get("format_name"),
                "decoded_semantics": attribute.get("decoded_semantics"),
            })

        geometry.append({
            "mesh_id": f"{int(mesh['mesh_id']):08x}",
            "mesh_info_index": mesh.get("mesh_info_index"),
            "transform_index": mesh.get("transform_index"),
            "lod_index": mesh.get("lod_index"),
            "stream_index": mesh.get("stream_index"),
            "vertex_count": mesh.get("vertex_count"),
            "vertex_offset": mesh.get("vertex_offset"),
            "vertex_stride": mesh.get("vertex_stride"),
            "index_count": mesh.get("index_count"),
            "triangle_count": mesh.get("triangle_count"),
            "attributes": attributes,
            "sections": [
                compact_section(item) for item in mesh.get("sections", [])
            ],
            "skinning": compact_skinning(mesh.get("skinning")),
        })

    stream_records = []
    for stream in report.get("stream_info", {}).get("records", []):
        components = []
        for component in stream.get("components", []):
            components.append({
                "type": component.get("type"),
                "type_name": component.get("type_name"),
                "format": component.get("format"),
                "format_name": component.get("format_name"),
                "index": component.get("index"),
                "byte_size": component.get("byte_size"),
                "byte_offset": component.get("byte_offset"),
            })
        stream_records.append({
            "index": stream.get("index"),
            "component_info_id": stream.get("component_info_id"),
            "components": components,
            "num_vertices": stream.get("num_vertices"),
            "vertex_stride": stream.get("vertex_stride"),
            "vertex_buffer_id": stream.get("vertex_buffer_id"),
            "vertex_buffer_offset": stream.get("vertex_buffer_offset"),
            "vertex_buffer_size": stream.get("vertex_buffer_size"),
            "index_buffer_id": stream.get("index_buffer_id"),
            "num_indices": stream.get("num_indices"),
            "index_buffer_type": stream.get("index_buffer_type"),
            "index_buffer_offset": stream.get("index_buffer_offset"),
            "index_buffer_size": stream.get("index_buffer_size"),
            "warnings": stream.get("warnings", []),
        })

    bone_records = []
    for record in report.get("bone_info", {}).get("records", []):
        palette = []
        for item in record.get("resolved_real_indices", []):
            palette.append({
                "palette_slot": item.get("palette_slot"),
                "transform_index": item.get("transform_index"),
                "name_hash": item.get("name_hash"),
                "name": item.get("name"),
                "valid": item.get("valid"),
            })
        bone_records.append({
            "index": record.get("index"),
            "num_bones": record.get("num_bones"),
            "real_indices": record.get("real_indices", []),
            "remaps": record.get("remaps", []),
            "resolved_real_indices": palette,
            "resolved_remaps": record.get("resolved_remaps", []),
            "warnings": record.get("warnings", []),
        })

    bones = report.get("bones_resource")
    bones_compact = None
    if bones:
        bones_compact = {
            "file_id": bones.get("file_id"),
            "bone_count": bones.get("bone_count", len(bones.get("bones", []))),
            "bones": [
                {"hash": item.get("hash"), "name": item.get("name")}
                for item in bones.get("bones", [])
            ],
        }

    transform_info = report.get("transform_info", {})
    stream_info = report.get("stream_info", {})
    bone_info = report.get("bone_info", {})

    return {
        "schema": "SourceModelReader-GlassUnitCompact1",
        "source_job": job["name"],
        "source_material_file_id": job["material"],
        "source_package": job["package"],
        "inspection_exit_code": exit_code,
        "package": report.get("package"),
        "unit": report.get("unit"),
        "summary": report.get("summary"),
        "materials": report.get("materials", []),
        "bones_resource": bones_compact,
        "transform_info": {
            "count": transform_info.get("count"),
            "resolved_skeleton": transform_info.get("resolved_skeleton", []),
            "weight_usage_summary": transform_info.get("weight_usage_summary"),
            "hierarchy_matrix_validation": transform_info.get(
                "hierarchy_matrix_validation"
            ),
            "warnings": transform_info.get("warnings", []),
        },
        "bone_info": {
            "count": bone_info.get("count"),
            "records": bone_records,
            "warnings": bone_info.get("warnings", []),
        },
        "stream_info": {
            "count": stream_info.get("count"),
            "records": stream_records,
            "warnings": stream_info.get("warnings", []),
        },
        "mesh_info_count": report.get("mesh_info", {}).get("count"),
        "geometry": geometry,
        "composite_unit": report.get("composite_unit"),
        "warnings": report.get("warnings", []),
        "limitations": report.get("limitations", []),
    }


def run_tests(output_dir: Path) -> list[dict[str, Any]]:
    results = []
    for module in TEST_MODULES:
        print(f"\n== Regression test: {module} ==", flush=True)
        command = [
            sys.executable,
            "-m",
            "unittest",
            "discover",
            "-s",
            "tests",
            "-p",
            module,
            "-v",
        ]
        completed = subprocess.run(
            command,
            cwd=ROOT,
            capture_output=True,
            text=True,
            errors="replace",
            check=False,
        )
        combined = completed.stdout + completed.stderr
        log_path = output_dir / f"{module}.log"
        log_path.write_text(combined, encoding="utf-8")
        if combined:
            print(combined, end="" if combined.endswith("\n") else "\n")
        results.append({
            "module": module,
            "exit_code": completed.returncode,
            "status": "passed" if completed.returncode == 0 else "failed",
            "log": str(log_path),
        })
    return results


def run_job(
    job: dict[str, str], game_data: Path, output_dir: Path
) -> dict[str, Any]:
    output_path = output_dir / f"{job['name']}.json"
    print(f"\n== Unit inspection: {job['name']} ==", flush=True)

    with tempfile.TemporaryDirectory(prefix="smr-glass-unit-") as temp_dir:
        raw_path = Path(temp_dir) / "unit-report.json"
        command = [
            sys.executable,
            "source_model_reader.py",
            "hd2-unit",
            str(game_data),
            "--package",
            job["package"],
            "--unit",
            job["unit"],
            "--json",
            str(raw_path),
        ]
        completed = subprocess.run(
            command,
            cwd=ROOT,
            capture_output=True,
            text=True,
            errors="replace",
            check=False,
        )
        if completed.stdout:
            print(completed.stdout, end="")
        if completed.stderr:
            print(completed.stderr, file=sys.stderr, end="")

        if completed.returncode >= 2 or not raw_path.is_file():
            raise RuntimeError(
                f"hd2-unit failed with exit code {completed.returncode}; "
                f"stdout={completed.stdout[-1500:]}; stderr={completed.stderr[-1500:]}"
            )

        with raw_path.open("r", encoding="utf-8") as handle:
            raw_report = json.load(handle)

        reduced = compact_report(raw_report, job, completed.returncode)
        output_path.write_text(
            json.dumps(reduced, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )

    summary = raw_report.get("summary", {})
    weight = (
        raw_report.get("transform_info", {}).get("weight_usage_summary") or {}
    )
    result = {
        "name": job["name"],
        "package": job["package"],
        "unit": job["unit"],
        "material": job["material"],
        "status": summary.get("status"),
        "exit_code": completed.returncode,
        "geometry_source": raw_report.get("unit", {}).get("geometry_source"),
        "transform_count": summary.get("transform_count"),
        "bone_info_count": summary.get("bone_info_count"),
        "stream_count": summary.get("stream_count"),
        "mesh_count": summary.get("mesh_count"),
        "decoded_mesh_count": summary.get("decoded_mesh_count"),
        "vertex_count": summary.get("vertex_count"),
        "index_count": summary.get("index_count"),
        "triangle_count": summary.get("triangle_count"),
        "actively_weighted_transform_count": weight.get(
            "actively_weighted_transform_count"
        ),
        "named_active_transform_count": weight.get(
            "actively_weighted_named_transform_count"
        ),
        "unresolved_active_influence_lane_count": weight.get(
            "unresolved_active_influence_lane_count"
        ),
        "warning_count": len(raw_report.get("warnings", [])),
        "compact_report": str(output_path),
    }
    print(
        f"Result: status={result['status']}, "
        f"meshes={result['decoded_mesh_count']}, "
        f"vertices={result['vertex_count']}, "
        f"transforms={result['transform_count']}, "
        f"weighted transforms={result['actively_weighted_transform_count']}, "
        f"warnings={result['warning_count']}"
    )
    return result


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Run focused tests and compact Glass Unit inspections."
    )
    parser.add_argument(
        "--game-data",
        type=Path,
        default=DEFAULT_GAME_DATA,
        help="Helldivers 2 data directory",
    )
    args = parser.parse_args()

    game_data = args.game_data.expanduser().resolve()
    if not game_data.is_dir():
        parser.error(f"Game data directory does not exist: {game_data}")

    output_dir = ROOT / "research" / "unit_inspections"
    output_dir.mkdir(parents=True, exist_ok=True)

    tests = run_tests(output_dir)
    results = []
    failures = []

    for job in JOBS:
        try:
            results.append(run_job(job, game_data, output_dir))
        except Exception as exc:  # keep the remaining candidates running
            failures.append({
                "name": job["name"],
                "package": job["package"],
                "unit": job["unit"],
                "message": str(exc),
            })
            print(f"FAILED: {job['name']}: {exc}", file=sys.stderr)

    summary = {
        "schema": "SourceModelReader-GlassUnitBatchSummary1",
        "game_data_path": str(game_data),
        "jobs_requested": len(JOBS),
        "jobs_succeeded": len(results),
        "jobs_failed": len(failures),
        "tests": tests,
        "results": results,
        "failures": failures,
    }
    summary_json = output_dir / "glass_unit_batch_summary.json"
    summary_json.write_text(
        json.dumps(summary, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )

    lines = [
        "# Glass Unit Batch Inspection",
        "",
        f"Jobs: {len(JOBS)} | Succeeded: {len(results)} | Failed: {len(failures)}",
        "",
        "## Focused tests",
        "",
        "| Module | Status |",
        "|---|---|",
    ]
    for item in tests:
        lines.append(f"| {item['module']} | {item['status']} |")
    lines.extend([
        "",
        "## Unit results",
        "",
        "| Candidate | Status | Geometry | Transforms | BoneInfo | Meshes | Vertices | Active weighted transforms | Unresolved lanes | Warnings |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|---:|",
    ])
    for item in results:
        lines.append(
            f"| {item['name']} | {item['status']} | {item['geometry_source']} | "
            f"{item['transform_count']} | {item['bone_info_count']} | "
            f"{item['decoded_mesh_count']} | {item['vertex_count']} | "
            f"{item['actively_weighted_transform_count']} | "
            f"{item['unresolved_active_influence_lane_count']} | "
            f"{item['warning_count']} |"
        )
    if failures:
        lines.extend(["", "## Failed candidates", ""])
        for item in failures:
            lines.append(f"- {item['name']}: {item['message']}")

    summary_md = output_dir / "glass_unit_batch_summary.md"
    summary_md.write_text("\n".join(lines) + "\n", encoding="utf-8")

    print("\n== Batch complete ==")
    print(f"Unit reports: {len(results)}/{len(JOBS)}")
    print(f"Tests passed: {sum(item['exit_code'] == 0 for item in tests)}/{len(tests)}")
    print(f"Summary: {summary_md}")
    print(f"Summary JSON: {summary_json}")

    if failures or any(item["exit_code"] != 0 for item in tests):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
