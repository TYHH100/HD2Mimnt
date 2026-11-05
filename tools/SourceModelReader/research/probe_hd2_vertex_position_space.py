#!/usr/bin/env python3
"""Probe which coordinate space stored HD2 vertex positions use.

Read-only. For every decoded mesh it reports the raw position extent and the
extent after the candidate mesh-world application, then measures nearest-vertex
distance against the Unit's reference render mesh under both hypotheses.

The row-vector layout is structural, not assumed: TransformInfo and BoneInfo
matrices store the 3x3 basis in floats 0..11 and the translation in floats
12..15, so a point is p' = p x M.
"""
from __future__ import annotations

import argparse
import hashlib
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
DEFAULT_OUTPUT_JSON = ROOT / "research" / "skeleton_comparison" / "hd2_vertex_position_space.json"
DEFAULT_OUTPUT_MARKDOWN = ROOT / "research" / "skeleton_comparison" / "hd2_vertex_position_space.md"
IDENTITY_TOLERANCE = 1.0e-4
POSITION_BUFFER_TOLERANCE = 1.0e-6
REST_INVARIANT_TOLERANCE = 1.0e-5


def _identity_error(matrix: list[float]) -> float:
    identity = [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0,
    ]
    return max(abs(a - b) for a, b in zip(matrix, identity))


def transform_point(point: list[float], matrix: list[float]) -> list[float]:
    """Apply a row-major, row-vector 4x4 matrix to (x, y, z)."""
    x, y, z = point[0], point[1], point[2]
    return [
        x * matrix[0] + y * matrix[4] + z * matrix[8] + matrix[12],
        x * matrix[1] + y * matrix[5] + z * matrix[9] + matrix[13],
        x * matrix[2] + y * matrix[6] + z * matrix[10] + matrix[14],
    ]


def _positions(geometry_row: dict[str, Any]) -> list[list[float]] | None:
    for attribute in geometry_row.get("attributes") or []:
        if not isinstance(attribute, dict):
            continue
        if attribute.get("component_name") != "position":
            continue
        values = attribute.get("values")
        if isinstance(values, list) and values and all(isinstance(v, list) for v in values):
            return [v[:3] for v in values]
        return None
    return None


def _extent(points: list[list[float]]) -> dict[str, Any]:
    count = len(points)
    mins = [min(p[k] for p in points) for k in range(3)]
    maxs = [max(p[k] for p in points) for k in range(3)]
    centroid = [sum(p[k] for p in points) / count for k in range(3)]
    return {
        "vertex_count": count,
        "min": mins,
        "max": maxs,
        "size": [maxs[k] - mins[k] for k in range(3)],
        "centroid": centroid,
        "diagonal": math.sqrt(sum((maxs[k] - mins[k]) ** 2 for k in range(3))),
    }


def _attribute_profile(geometry_row: dict[str, Any]) -> dict[str, Any]:
    """Record each attribute's identity, format and buffer hash.

    Hashing the raw decoded buffers lets the report prove two meshes share an
    asset without comparing millions of floats by hand.
    """
    components: dict[str, str] = {}
    hashes: dict[str, str] = {}
    for attribute in geometry_row.get("attributes") or []:
        if not isinstance(attribute, dict):
            continue
        key = f"{attribute.get('component_name')}{attribute.get('component_index')}"
        components[key] = str(attribute.get("format_name"))
        values = attribute.get("values")
        if isinstance(values, list):
            payload = json.dumps(values, separators=(",", ":"), sort_keys=True).encode("utf-8")
            hashes[key] = hashlib.sha256(payload).hexdigest()
    return {"attribute_components": components, "attribute_buffer_hashes": hashes}


def _max_abs_point_diff(left: list[list[float]], right: list[list[float]]) -> float | None:
    if len(left) != len(right) or not left:
        return None
    return max(
        abs(a - b)
        for la, ra in zip(left, right)
        for a, b in zip(la[:3], ra[:3])
    )


def _nearest_stats(source: list[list[float]], target: list[list[float]]) -> dict[str, float]:
    """Distance from every source vertex to its nearest target vertex."""
    tx = [p[0] for p in target]
    ty = [p[1] for p in target]
    tz = [p[2] for p in target]
    distances: list[float] = []
    for px, py, pz in ((p[0], p[1], p[2]) for p in source):
        best = math.inf
        for cx, cy, cz in zip(tx, ty, tz):
            dx = px - cx
            dy = py - cy
            dz = pz - cz
            squared = dx * dx + dy * dy + dz * dz
            if squared < best:
                best = squared
        distances.append(math.sqrt(best))
    distances.sort()
    count = len(distances)
    return {
        "mean": sum(distances) / count,
        "median": distances[count // 2],
        "p95": distances[min(count - 1, int(count * 0.95))],
        "max": distances[-1],
    }


def load_unit(path: Path, label: str) -> dict[str, Any]:
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

    meshes: list[dict[str, Any]] = []
    for geometry_row in report.get("geometry") or []:
        if not isinstance(geometry_row, dict):
            continue
        transform_index = geometry_row.get("transform_index")
        transform = transforms.get(transform_index) if isinstance(transform_index, int) else None
        matrix = transform.get("matrix_4x4_raw") if transform else None
        has_matrix = isinstance(matrix, list) and len(matrix) == 16
        identity_error = _identity_error(matrix) if has_matrix else None
        points = _positions(geometry_row)

        row = {
            "mesh_info_index": geometry_row.get("mesh_info_index"),
            "mesh_id": geometry_row.get("mesh_id"),
            "lod_index": geometry_row.get("lod_index"),
            "transform_index": transform_index,
            "transform_name": names.get(transform_index) if isinstance(transform_index, int) else None,
            "transform_name_hash": transform.get("name_hash") if transform else None,
            "skinning_status": (geometry_row.get("skinning") or {}).get("status"),
            "position_attribute_present": points is not None,
            "stored_matrix_4x4": matrix if has_matrix else None,
            "stored_matrix_identity_error": identity_error,
            "stored_matrix_identity_status": (
                "unavailable" if identity_error is None
                else "identity" if identity_error <= IDENTITY_TOLERANCE
                else "non_identity"
            ),
            "raw_extent": None,
            "mesh_world_extent": None,
            "raw_centroid_mesh_world": None,
            "palette": (geometry_row.get("skinning") or {}).get("palette") or [],
            "bone_index_buffers": [
                attribute.get("values")
                for attribute in geometry_row.get("attributes") or []
                if isinstance(attribute, dict)
                and attribute.get("component_name") == "bone_index"
                and isinstance(attribute.get("values"), list)
            ],
            **_attribute_profile(geometry_row),
        }
        if points:
            row["raw_extent"] = _extent(points)
            if has_matrix and identity_error is not None and identity_error > IDENTITY_TOLERANCE:
                moved = [transform_point(p, matrix) for p in points]
                row["mesh_world_extent"] = _extent(moved)
                row["raw_centroid_mesh_world"] = row["raw_extent"]["centroid"]
            # Keep the working set only for the meshes the correspondence test needs.
            row["_points"] = points
        meshes.append(row)

    unit = report.get("unit") or {}
    package = report.get("package") or {}
    transform_matrices = {
        index: row.get("matrix_4x4_raw")
        for index, row in transforms.items()
        if isinstance(row.get("matrix_4x4_raw"), list) and len(row["matrix_4x4_raw"]) == 16
    }
    del report
    return {
        "label": label,
        "input": str(path),
        "unit_id": unit.get("file_id"),
        "package_id": package.get("name"),
        "geometry_source": unit.get("geometry_source"),
        "transform_matrices": transform_matrices,
        "meshes": meshes,
    }


def rest_invariant_tests(unit: dict[str, Any], sample_limit: int, tolerance: float) -> list[dict[str, Any]]:
    """Check the stored rest transform per vertex lane, not just per matrix.

    For every influence lane the parser can resolve, compare the two-step
    row-vector product p x palette x bone_world against p x mesh_world. The
    product has to land on the same placed position, otherwise mesh_world is
    being carried twice or dropped.
    """
    results: list[dict[str, Any]] = []
    transforms = unit["transform_matrices"]
    for mesh in unit["meshes"]:
        points = mesh.get("_points")
        palette_rows = mesh.get("palette") or []
        lane_groups = mesh.get("bone_index_buffers") or []
        if not points or not palette_rows or not lane_groups:
            continue
        palette = {}
        for slot in palette_rows:
            if not isinstance(slot, dict):
                continue
            index = slot.get("palette_slot")
            if isinstance(index, int) and not isinstance(index, bool):
                palette[index] = slot
        mesh_world = mesh["stored_matrix_4x4"] or []
        considered = points[:sample_limit] if sample_limit else points
        max_error = 0.0
        total_error = 0.0
        lanes = 0
        missing = 0
        unplaced_error = 0.0
        for vertex_index, point in enumerate(considered):
            target = transform_point(point, mesh_world) if mesh_world else point
            for buffer in lane_groups:
                if vertex_index >= len(buffer):
                    continue
                lanes_per_vertex = buffer[vertex_index]
                if not isinstance(lanes_per_vertex, list):
                    continue
                for bone_index in lanes_per_vertex:
                    if not isinstance(bone_index, int) or isinstance(bone_index, bool):
                        continue
                    slot = palette.get(bone_index)
                    if not slot or not slot.get("valid") or not slot.get("bone_info_matrix_raw"):
                        missing += 1
                        continue
                    bone_world = transforms.get(slot.get("transform_index"))
                    if not bone_world:
                        missing += 1
                        continue
                    posed = transform_point(
                        transform_point(point, slot["bone_info_matrix_raw"]), bone_world
                    )
                    error = max(abs(posed[k] - target[k]) for k in range(3))
                    max_error = max(max_error, error)
                    total_error += error
                    lanes += 1
                    if mesh_world:
                        unplaced_error = max(
                            unplaced_error,
                            max(abs(posed[k] - point[k]) for k in range(3)),
                        )
        if not lanes:
            continue
        results.append({
            "mesh_info_index": mesh["mesh_info_index"],
            "mesh_id": mesh["mesh_id"],
            "lod_index": mesh["lod_index"],
            "transform_name": mesh["transform_name"],
            "stored_matrix_identity_status": mesh["stored_matrix_identity_status"],
            "vertices_considered": len(considered),
            "lanes_checked": lanes,
            "lanes_unresolved": missing,
            "placed_target": "mesh_world" if mesh_world else "identity",
            "max_abs_error_vs_placed": max_error,
            "mean_abs_error_vs_placed": total_error / lanes,
            "max_abs_error_vs_unplaced_vertex": unplaced_error,
            "within_tolerance": max_error <= tolerance,
        })
    return results


def correspondence_tests(unit: dict[str, Any], sample_limit: int) -> list[dict[str, Any]]:
    """Compare every mesh against the Unit's largest ordinary render mesh.

    The ordinary LOD pair is the control: both have identity mesh-world
    matrices, so a near-zero raw distance proves the metric itself works before
    any conclusion is drawn from a non-identity mesh.
    """
    candidates = [
        mesh for mesh in unit["meshes"]
        if mesh["position_attribute_present"]
        and mesh["skinning_status"] == "ok"
        and mesh["stored_matrix_identity_status"] == "identity"
        and isinstance(mesh["lod_index"], int) and mesh["lod_index"] >= 0
    ]
    if len(candidates) < 2:
        return []
    reference = max(candidates, key=lambda mesh: mesh["raw_extent"]["vertex_count"])
    controls = [
        mesh for mesh in candidates
        if mesh is not reference and mesh["lod_index"] != reference["lod_index"]
    ][:1]
    control_ids = {id(mesh) for mesh in controls}
    # Auxiliary here means "not already covered by the control": a mesh whose stored
    # mesh-world matrix is non-identity, or that carries no skin attributes. The
    # non-identity case is the discriminating one, so it is kept even when skinned.
    others = [
        mesh for mesh in unit["meshes"]
        if mesh["position_attribute_present"]
        and id(mesh) not in control_ids
        and mesh is not reference
        and (
            mesh["stored_matrix_identity_status"] == "non_identity"
            or mesh["skinning_status"] != "ok"
        )
    ]
    results: list[dict[str, Any]] = []
    target_points = reference["_points"]
    for mesh in controls + others:
        source_points = mesh["_points"][:sample_limit] if sample_limit else mesh["_points"]
        matrix = mesh["stored_matrix_4x4"]
        raw = _nearest_stats(source_points, target_points)
        transformed = None
        if matrix is not None and mesh["stored_matrix_identity_status"] == "non_identity":
            moved = [transform_point(p, matrix) for p in source_points]
            transformed = _nearest_stats(moved, target_points)
        results.append({
            "role": "control" if id(mesh) in control_ids else "auxiliary",
            "mesh_info_index": mesh["mesh_info_index"],
            "mesh_id": mesh["mesh_id"],
            "lod_index": mesh["lod_index"],
            "transform_index": mesh["transform_index"],
            "transform_name": mesh["transform_name"],
            "skinning_status": mesh["skinning_status"],
            "stored_matrix_identity_status": mesh["stored_matrix_identity_status"],
            "reference_mesh_info_index": reference["mesh_info_index"],
            "reference_lod_index": reference["lod_index"],
            "source_sample_count": len(source_points),
            "nearest_distance_raw_space": raw,
            "nearest_distance_after_mesh_world": transformed,
        })
    return results


def _box_vs_union(box: dict[str, Any], low: list[float], high: list[float]) -> dict[str, Any]:
    """Compare a candidate volume with the render union, in both directions.

    outside_union answers "is the proxy too small"; outside_box answers "is it
    larger than the geometry it is supposed to wrap".
    """
    union_outside = [
        max(box["min"][k] - low[k], high[k] - box["max"][k], 0.0) for k in range(3)
    ]
    box_outside = [
        max(low[k] - box["min"][k], box["max"][k] - high[k], 0.0) for k in range(3)
    ]
    return {
        "union_outside_per_axis": union_outside,
        "union_outside_max": max(union_outside),
        "box_outside_per_axis": box_outside,
        "box_outside_max": max(box_outside),
    }


def volume_tests(unit: dict[str, Any]) -> list[dict[str, Any]]:
    """Test which placement puts an auxiliary volume around the render geometry.

    A nearest-vertex surface test is meaningless for a proxy that is not a
    decimation of the same surface, so containment is used instead: the volume
    that actually surrounds the Unit's render meshes is the stored space.
    """
    render = [
        mesh for mesh in unit["meshes"]
        if mesh["raw_extent"]
        and mesh["skinning_status"] == "ok"
        and mesh["stored_matrix_identity_status"] == "identity"
    ]
    if not render:
        return []
    low = [min(mesh["raw_extent"]["min"][k] for mesh in render) for k in range(3)]
    high = [max(mesh["raw_extent"]["max"][k] for mesh in render) for k in range(3)]
    results = []
    for mesh in unit["meshes"]:
        moved = mesh["mesh_world_extent"]
        if mesh["stored_matrix_identity_status"] != "non_identity" or moved is None:
            continue
        raw = mesh["raw_extent"]
        raw_compare = _box_vs_union(raw, low, high)
        moved_compare = _box_vs_union(moved, low, high)
        results.append({
            "mesh_info_index": mesh["mesh_info_index"],
            "mesh_id": mesh["mesh_id"],
            "lod_index": mesh["lod_index"],
            "transform_index": mesh["transform_index"],
            "transform_name": mesh["transform_name"],
            "skinning_status": mesh["skinning_status"],
            "render_mesh_count": len(render),
            "render_union_min": low,
            "render_union_max": high,
            "raw_vs_render_union": raw_compare,
            "mesh_world_vs_render_union": moved_compare,
            "raw_contains_render": raw_compare["union_outside_max"] == 0.0,
            "mesh_world_contains_render": moved_compare["union_outside_max"] == 0.0,
        })
    return results


def shared_asset_checks(units: list[dict[str, Any]], tolerance: float) -> list[dict[str, Any]]:
    """Find meshes in different Units that store the same vertex buffers."""
    rows = [
        (unit, mesh)
        for unit in units
        for mesh in unit["meshes"]
        if mesh["raw_extent"]
    ]
    results = []
    for i, (left_unit, left) in enumerate(rows):
        for right_unit, right in rows[i + 1:]:
            if left_unit is right_unit or left["mesh_id"] == right["mesh_id"]:
                continue
            if left["raw_extent"]["vertex_count"] != right["raw_extent"]["vertex_count"]:
                continue
            shared = set(left["attribute_buffer_hashes"]) & set(right["attribute_buffer_hashes"])
            if not shared:
                continue
            identical = sorted(
                name for name in shared
                if left["attribute_buffer_hashes"][name] == right["attribute_buffer_hashes"][name]
            )
            if not identical:
                continue
            position_diff = None
            if "_points" in left and "_points" in right:
                position_diff = _max_abs_point_diff(left["_points"], right["_points"])
            results.append({
                "left": {
                    "unit_label": left_unit["label"], "mesh_info_index": left["mesh_info_index"],
                    "mesh_id": left["mesh_id"], "lod_index": left["lod_index"],
                    "transform_name": left["transform_name"],
                    "stored_matrix_identity_status": left["stored_matrix_identity_status"],
                },
                "right": {
                    "unit_label": right_unit["label"], "mesh_info_index": right["mesh_info_index"],
                    "mesh_id": right["mesh_id"], "lod_index": right["lod_index"],
                    "transform_name": right["transform_name"],
                    "stored_matrix_identity_status": right["stored_matrix_identity_status"],
                },
                "vertex_count": left["raw_extent"]["vertex_count"],
                "bitwise_identical_buffers": identical,
                "differing_buffers": sorted(set(shared) - set(identical)),
                "position_max_abs_diff": position_diff,
                "position_within_tolerance": (
                    position_diff is not None and position_diff <= tolerance
                ),
            })
    return results


def build_report(units: list[dict[str, Any]], sample_limit: int) -> dict[str, Any]:
    for unit in units:
        unit["correspondence"] = correspondence_tests(unit, sample_limit)
        unit["volume"] = volume_tests(unit)
        unit["rest_invariant"] = rest_invariant_tests(
            unit, sample_limit, REST_INVARIANT_TOLERANCE
        )
    shared = shared_asset_checks(units, POSITION_BUFFER_TOLERANCE)
    rest_rows = [row for unit in units for row in unit["rest_invariant"]]
    summary = {
        "units": len(units),
        "meshes_with_positions": sum(
            1 for unit in units for mesh in unit["meshes"] if mesh["position_attribute_present"]
        ),
        "non_identity_mesh_world": sum(
            1 for unit in units for mesh in unit["meshes"]
            if mesh["stored_matrix_identity_status"] == "non_identity"
        ),
        "auxiliary_tests": sum(
            1 for unit in units for test in unit["correspondence"] if test["role"] == "auxiliary"
        ),
        "volume_tests": sum(len(unit["volume"]) for unit in units),
        "shared_asset_pairs": len(shared),
        "rest_invariant_meshes": len(rest_rows),
        "rest_lanes_checked": sum(row["lanes_checked"] for row in rest_rows),
        "rest_lanes_unresolved": sum(row["lanes_unresolved"] for row in rest_rows),
        "rest_invariant_within_tolerance": sum(1 for row in rest_rows if row["within_tolerance"]),
        "rest_invariant_exceeding_tolerance": sum(1 for row in rest_rows if not row["within_tolerance"]),
    }
    for unit in units:
        for mesh in unit["meshes"]:
            mesh.pop("_points", None)
    return {
        "schema": "SourceModelReader-HD2VertexPositionSpace1",
        "matrix_convention_candidate": "row_major_4x4_row_vector",
        "identity_tolerance": IDENTITY_TOLERANCE,
        "position_buffer_tolerance": POSITION_BUFFER_TOLERANCE,
        "rest_invariant_tolerance": REST_INVARIANT_TOLERANCE,
        "vertex_sample_limit": sample_limit,
        "summary": summary,
        "shared_asset_pairs": shared,
        "units": units,
        "interpretation_limits": [
            "Nearest-vertex distance measures which space a stored position already lives in; it does not prove the runtime shader order.",
            "The candidate mesh-world matrix comes from the parser's own hierarchy reconstruction, which is validated for internal consistency only.",
            "Vertices are read, never modified; no weight transfer or conversion is performed or implied by this probe.",
            "A control pair with two identity mesh-world matrices must show a near-zero raw distance before any auxiliary result is read.",
            "Buffer identity is decided by hashing decoded attribute values; a float32 rounding difference such as negative zero is reported as a difference, not silently ignored.",
        ],
    }


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# HD2 Vertex Position-Space Probe",
        "",
        f"- Matrix convention candidate: {report['matrix_convention_candidate']}",
        f"- Identity tolerance: {report['identity_tolerance']}",
        f"- Position buffer tolerance: {report['position_buffer_tolerance']}",
        f"- Vertex sample limit per mesh: {report['vertex_sample_limit'] or 'all'}",
        "",
        "Question: are stored vertex positions already in the space the render LODs share, "
        "or must the non-identity mesh-world matrix be applied to them? Two independent tests "
        "answer it: nearest-vertex distance for surfaces that should coincide, and volume "
        "containment for auxiliary geometry that is not a copy of the render surface.",
        "",
    ]
    for unit in report["units"]:
        lines.extend([
            f"## {unit['label']}",
            "",
            f"- Unit: {unit.get('unit_id')}",
            f"- Package: {unit.get('package_id')}",
            f"- Geometry source: {unit.get('geometry_source')}",
            "",
            "| MeshInfo | LOD | Transform | Name / hash | Skinning | Matrix | Vertices | Raw centroid | Raw diagonal | After mesh-world diagonal | Attribute formats |",
            "|---:|---:|---:|---|---|---|---:|---|---:|---:|---|",
        ])
        for mesh in unit["meshes"]:
            raw = mesh["raw_extent"]
            moved = mesh["mesh_world_extent"]
            centroid = "(no positions)" if raw is None else ", ".join(f"{v:.5f}" for v in raw["centroid"])
            formats = ", ".join(
                f"{name}:{fmt}" for name, fmt in sorted(mesh["attribute_components"].items())
            )
            lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                mesh["mesh_info_index"], mesh["lod_index"], mesh["transform_index"],
                f"{mesh['transform_name'] or '(unresolved)'} / {mesh['transform_name_hash']}",
                mesh["skinning_status"], mesh["stored_matrix_identity_status"],
                "-" if raw is None else raw["vertex_count"],
                centroid,
                "-" if raw is None else f"{raw['diagonal']:.5f}",
                "-" if moved is None else f"{moved['diagonal']:.5f}",
                formats,
            ))
        lines.append("")
        if unit["correspondence"]:
            lines.extend([
                "### Nearest-vertex distance to the reference render mesh",
                "",
                "| Role | MeshInfo | LOD | Matrix | Reference | Sampled | Raw mean | Raw median | Raw p95 | Raw max | Mesh-world mean | Mesh-world median | Mesh-world p95 |",
                "|---|---:|---:|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|",
            ])
            for test in unit["correspondence"]:
                raw = test["nearest_distance_raw_space"]
                moved = test["nearest_distance_after_mesh_world"]
                lines.append("| {} | {} | {} | {} | {} | {} | {:.6f} | {:.6f} | {:.6f} | {:.6f} | {} | {} | {} |".format(
                    test["role"], test["mesh_info_index"], test["lod_index"],
                    test["stored_matrix_identity_status"],
                    f"mesh {test['reference_mesh_info_index']} (LOD {test['reference_lod_index']})",
                    test["source_sample_count"],
                    raw["mean"], raw["median"], raw["p95"], raw["max"],
                    "-" if moved is None else f"{moved['mean']:.6f}",
                    "-" if moved is None else f"{moved['median']:.6f}",
                    "-" if moved is None else f"{moved['p95']:.6f}",
                ))
            lines.append("")
        else:
            lines.extend([
                "No correspondence test was possible: this Unit lacks a second ordinary "
                "identity-matrix render mesh to compare against.",
                "",
            ])
        if unit["volume"]:
            lines.extend([
                "### Auxiliary volume containment of the render union",
                "",
                "union-outside is the largest distance the render meshes escape a candidate "
                "volume; box-slack is how much the candidate volume exceeds the render union. "
                "Zero union-outside means the volume fully wraps the render geometry.",
                "",
                "| MeshInfo | LOD | Transform | Skinning | Render union min | Render union max | Union outside raw | Union outside mesh-world | Contains raw | Contains mesh-world | Box slack raw | Box slack mesh-world |",
                "|---:|---:|---|---|---|---|---:|---:|---|---|---:|---:|",
            ])
            for test in unit["volume"]:
                raw_compare = test["raw_vs_render_union"]
                moved_compare = test["mesh_world_vs_render_union"]
                lines.append("| {} | {} | {} | {} | {} | {} | {:.5f} | {:.5f} | {} | {} | {:.5f} | {:.5f} |".format(
                    test["mesh_info_index"], test["lod_index"],
                    test["transform_name"] or "(unresolved)", test["skinning_status"],
                    ", ".join(f"{v:.5f}" for v in test["render_union_min"]),
                    ", ".join(f"{v:.5f}" for v in test["render_union_max"]),
                    raw_compare["union_outside_max"], moved_compare["union_outside_max"],
                    test["raw_contains_render"], test["mesh_world_contains_render"],
                    raw_compare["box_outside_max"], moved_compare["box_outside_max"],
                ))
            lines.append("")

        if unit["rest_invariant"]:
            lines.extend([
                "### Rest-pose vertex invariant",
                "",
                "For every resolved influence lane, (p x palette) x bone_world must equal "
                "p x mesh_world. A non-zero error means the placement transform is applied "
                "twice or not at all in the stored representation.",
                "",
                "| MeshInfo | LOD | Transform | Matrix | Vertices | Lanes checked | Unresolved lanes | Max error | Mean error | Max error vs unplaced p | Within tolerance |",
                "|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---|",
            ])
            for row in unit["rest_invariant"]:
                lines.append("| {} | {} | {} | {} | {} | {} | {} | {:.3e} | {:.3e} | {:.3e} | {} |".format(
                    row["mesh_info_index"], row["lod_index"],
                    row["transform_name"] or "(unresolved)", row["stored_matrix_identity_status"],
                    row["vertices_considered"], row["lanes_checked"], row["lanes_unresolved"],
                    row["max_abs_error_vs_placed"], row["mean_abs_error_vs_placed"],
                    row["max_abs_error_vs_unplaced_vertex"], row["within_tolerance"],
                ))
            lines.append("")

    lines.extend([
        "## Cross-Unit shared vertex buffers",
        "",
        "| Left | Right | Vertices | Bitwise identical buffers | Differing buffers | Position max abs diff | Within tolerance |",
        "|---|---|---:|---|---|---:|---|",
    ])
    if report["shared_asset_pairs"]:
        for pair in report["shared_asset_pairs"]:
            left = pair["left"]
            right = pair["right"]
            lines.append("| {} mesh {} ({} / LOD {} / {}) | {} mesh {} ({} / LOD {} / {}) | {} | {} | {} | {} | {} |".format(
                left["unit_label"], left["mesh_info_index"], left["transform_name"] or "(unresolved)",
                left["lod_index"], left["stored_matrix_identity_status"],
                right["unit_label"], right["mesh_info_index"], right["transform_name"] or "(unresolved)",
                right["lod_index"], right["stored_matrix_identity_status"],
                pair["vertex_count"],
                ", ".join(pair["bitwise_identical_buffers"]) or "-",
                ", ".join(pair["differing_buffers"]) or "-",
                "-" if pair["position_max_abs_diff"] is None else f"{pair['position_max_abs_diff']:.3e}",
                pair["position_within_tolerance"],
            ))
    else:
        lines.append("| - | - | - | none found | - | - | - |")
    lines.append("")

    lines.extend([
        "## Interpretation limits",
        "",
    ])
    lines.extend("- " + item for item in report["interpretation_limits"])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lower-body", type=Path, default=DEFAULT_INPUTS["fs05_lower_body"])
    parser.add_argument("--torso-left-arm", type=Path, default=DEFAULT_INPUTS["fs05_torso_left_arm"])
    parser.add_argument("--avatar", type=Path, default=DEFAULT_INPUTS["avatar_composite"])
    parser.add_argument(
        "--sample-limit", type=int, default=0,
        help="Cap vertices per source mesh in the nearest-distance test; 0 uses all vertices.",
    )
    parser.add_argument("--output-json", type=Path, default=DEFAULT_OUTPUT_JSON)
    parser.add_argument("--output-markdown", type=Path, default=DEFAULT_OUTPUT_MARKDOWN)
    args = parser.parse_args()

    if args.sample_limit < 0:
        parser.error("--sample-limit must be zero or positive")

    inputs = [
        ("fs05_lower_body", args.lower_body),
        ("fs05_torso_left_arm", args.torso_left_arm),
        ("avatar_composite", args.avatar),
    ]
    for _label, path in inputs:
        if not path.is_file():
            parser.error(f"Input JSON does not exist: {path}")

    try:
        units = [load_unit(path, label) for label, path in inputs]
        report = build_report(units, args.sample_limit)
        for unit in units:
            unit["summary"] = {
                "mesh_count": len(unit["meshes"]),
                "meshes_with_positions": sum(
                    1 for mesh in unit["meshes"] if mesh["position_attribute_present"]
                ),
                "non_identity_stored_matrix_count": sum(
                    1 for mesh in unit["meshes"]
                    if mesh["stored_matrix_identity_status"] == "non_identity"
                ),
            }
        markdown = render_markdown(report)
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        parser.error(f"Vertex position-space probe failed: {exc}")

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    args.output_markdown.write_text(markdown, encoding="utf-8")

    for unit in units:
        print("{}: {} meshes with positions, {} non-identity matrices, {} volume tests".format(
            unit["label"], unit["summary"]["meshes_with_positions"],
            unit["summary"]["non_identity_stored_matrix_count"], len(unit["volume"]),
        ))
    print("Shared asset pairs: {}".format(report["summary"]["shared_asset_pairs"]))
    print(f"Markdown: {args.output_markdown}")
    print(f"JSON: {args.output_json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
