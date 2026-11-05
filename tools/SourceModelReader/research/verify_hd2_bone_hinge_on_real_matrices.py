#!/usr/bin/env python3
"""Hinge real HD2 bones and check the skinning invariants on stored matrices.

Phase 4 has to hold on the matrices the game actually stores, not only on synthetic
pivots. This tool reads decoded Unit JSON, takes the real BoneInfo palette matrices and
the real TransformInfo world matrices, and asks what a hinge through a real joint must
produce:

    palette x posed_bone  ==  mesh_world x hinge(joint, rotation)

so the bone origin does not move, every influence lane turns as a rigid body about that
joint, and a weighted blend displaces a vertex by exactly its own weight fraction. The
naive alternative - post-multiplying the world matrix by the rotation - is carried as a
control, so the two constructions are separated by measurement instead of by assumption.

Reading the identity the other way round also tests the stored palette: if BoneInfo did
not really hold mesh_world x inverse(bone_world), the hinge product above would not
match. Vertices and matrices are read, hinged in memory and dropped; nothing is written
back and no weight is transferred.
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

from research.probe_hd2_vertex_position_space import (  # noqa: E402
    DEFAULT_INPUTS,
    REST_INVARIANT_TOLERANCE,
    load_unit,
    transform_point,
    weight_lane_tests,
)
from research.verify_hd2_retarget_deformation import (  # noqa: E402
    axis_angle,
    basis_row,
    distance,
    hinge_about_point,
    local_frame_hinge,
    world_origin_hinge,
)

DEFAULT_SAMPLE_LIMIT = 0
DEFAULT_MAX_BONES = 0
DEFAULT_ANGLE_DEGREES = 30.0
DEFAULT_OUTPUT_JSON = ROOT / "research" / "skeleton_comparison" / "hd2_real_bone_hinge.json"
DEFAULT_OUTPUT_MARKDOWN = ROOT / "research" / "skeleton_comparison" / "hd2_real_bone_hinge.md"
DIGITS = 6

# placed_* axes live in the space the mesh is placed into; bone_* axes are the stored
# world matrix's own basis rows, i.e. where that bone's local axis actually points.
AXES = (
    ("placed_x", (1.0, 0.0, 0.0)),
    ("placed_y", (0.0, 1.0, 0.0)),
    ("placed_z", (0.0, 0.0, 1.0)),
    ("bone_x", "basis_0"),
    ("bone_z", "basis_2"),
)


def _palette_slots(mesh: dict[str, Any]) -> dict[int, dict[str, Any]]:
    slots: dict[int, dict[str, Any]] = {}
    for row in mesh.get("palette") or []:
        slot = row.get("palette_slot") if isinstance(row, dict) else None
        if isinstance(slot, int) and not isinstance(slot, bool):
            slots[slot] = row
    return slots


def _attr_indices(model: str, count: int) -> list[int]:
    prefix = "single_index_attr_"
    if model.startswith(prefix):
        try:
            index = int(model[len(prefix):])
        except ValueError:
            return []
        return [index] if 0 <= index < count else []
    if model == "every_index_attr_shares_weights":
        return list(range(count))
    return []


def _pairing_choices(unit: dict[str, Any], sample_limit: int) -> dict[Any, dict[str, Any]]:
    """Pick one weight/bone-index pairing per mesh using the probe's own scorer.

    The pairing was settled in the position-space probe by the per-vertex weight sum;
    reusing that scorer here stops the two tools from drifting apart. Where several
    models pass, the report keeps their names and whether the alternatives actually hold
    different bone indices, because at rest every lane blends onto the same placed point
    and no rest quantity can separate them.
    """
    passing: dict[Any, list[dict[str, Any]]] = {}
    chosen: dict[Any, dict[str, Any]] = {}
    for row in weight_lane_tests(unit, sample_limit, REST_INVARIANT_TOLERANCE):
        key = row["mesh_info_index"]
        if not row["passes"]:
            continue
        passing.setdefault(key, []).append(row)
        current = chosen.get(key)
        if current is None or row["vertices_checked"] > current["vertices_checked"]:
            chosen[key] = row
    for key, row in chosen.items():
        group = passing[key]
        best_checked = max(item["vertices_checked"] for item in group)
        # A model that resolves almost no vertex can pass vacuously: with every lane
        # unusable, nothing is summed and nothing is blended. Only models covering at
        # least half as many vertices as the best one compete for the pairing.
        strong = [item for item in group
                  if item["vertices_checked"] >= 0.5 * best_checked]
        names = sorted(str(item["pairing_model"]) for item in strong)
        row["passing_models"] = names
        row["passing_model_count"] = len(names)
        row["vacuous_models"] = sorted(
            str(item["pairing_model"]) for item in group if item not in strong)
        row["pairing_ambiguous"] = len(names) > 1
        mesh = next((candidate for candidate in unit["meshes"]
                     if candidate["mesh_info_index"] == key), None)
        buffers = (mesh or {}).get("bone_index_buffers") or []
        chosen_indices = _attr_indices(str(row["pairing_model"]), len(buffers))
        row["alternative_passing_models_differ"] = any(
            _attr_indices(name, len(buffers)) != chosen_indices
            and _buffer_sets_differ(buffers, _attr_indices(name, len(buffers)),
                                    chosen_indices)
            for name in names if name != row["pairing_model"]
        ) if len(buffers) > 1 else False
    return chosen


def _buffer_sets_differ(buffers: list[Any], first: list[int], second: list[int]) -> bool:
    """True when the two attribute groups hold different bone index rows."""
    for index in first:
        for other in second:
            if index == other:
                continue
            if index < len(buffers) and other < len(buffers) \
                    and buffers[index] != buffers[other]:
                return True
    return False


def _vertex_lanes(mesh: dict[str, Any], vertex_index: int,
                  attr_indices: list[int]) -> dict[int, float]:
    weight_rows = mesh.get("weight_rows") or []
    if vertex_index >= len(weight_rows):
        return {}
    weights = weight_rows[vertex_index]
    if not isinstance(weights, list):
        return {}
    lanes: dict[int, float] = {}
    for attr_index in attr_indices:
        buffers = mesh.get("bone_index_buffers") or []
        if attr_index >= len(buffers):
            continue
        buffer = buffers[attr_index]
        if vertex_index >= len(buffer) or not isinstance(buffer[vertex_index], list):
            continue
        for lane, bone_index in enumerate(buffer[vertex_index]):
            if lane >= len(weights):
                continue
            weight = weights[lane]
            if isinstance(weight, bool) or not isinstance(weight, (int, float)):
                continue
            if weight <= 0.0 or not isinstance(bone_index, int) or isinstance(bone_index, bool):
                continue
            lanes[bone_index] = lanes.get(bone_index, 0.0) + float(weight)
    return lanes


def _bone_matrices(slots: dict[int, dict[str, Any]],
                   transforms: dict[int, list[float]],
                   bone_indices: list[int]) -> dict[int, tuple[list[float], list[float], int]]:
    """Resolve palette matrix, bone world matrix and transform index per bone index."""
    resolved: dict[int, tuple[list[float], list[float], int]] = {}
    for bone_index in bone_indices:
        slot = slots.get(bone_index)
        if not slot or not slot.get("valid"):
            continue
        palette = slot.get("bone_info_matrix_raw")
        transform_index = slot.get("transform_index")
        bone_world = transforms.get(transform_index) if isinstance(transform_index, int) else None
        if not isinstance(palette, list) or len(palette) != 16:
            continue
        if not isinstance(bone_world, list) or len(bone_world) != 16:
            continue
        resolved[bone_index] = (palette, bone_world, transform_index)
    return resolved


def _unit_axis(bone_world: list[float], marker: str) -> list[float] | None:
    index = int(marker.split("_")[1])
    return _normalized(basis_row(bone_world, index))


def _normalized(vector: list[float]) -> list[float] | None:
    length = math.sqrt(sum(value * value for value in vector))
    if length <= 1.0e-9:
        return None
    return [value / length for value in vector]


def _stride_indices(count: int, sample_limit: int) -> list[int]:
    """Spread a bounded sample over the whole vertex range.

    Composite meshes are ordered by section, so the first N vertices would measure one
    region only; a strided sample reaches every section at the same cost.
    """
    if not sample_limit or sample_limit >= count:
        return list(range(count))
    step = -(-count // sample_limit)
    return list(range(0, count, step))


def _mesh_rows(mesh: dict[str, Any], slots: dict[int, dict[str, Any]],
               transforms: dict[int, list[float]], attr_indices: list[int],
               sample_limit: int) -> dict[str, Any]:
    """Sample vertices once, resolve their lanes and measure the rest error."""
    points = mesh["_points"]
    mesh_world = mesh.get("stored_matrix_4x4")
    indices = _stride_indices(len(points), sample_limit)
    rows: list[tuple[list[float], list[float], dict[int, float], float]] = []
    matrices: dict[int, tuple[list[float], list[float], int]] = {}
    buffers = mesh.get("bone_index_buffers") or []
    other_buffers = [buffer for index, buffer in enumerate(buffers) if index not in attr_indices]
    unresolved_lanes = 0
    unexamined_lanes = 0
    rest_error = 0.0
    usage: dict[int, int] = {}
    for vertex_index in indices:
        point = points[vertex_index]
        placed = transform_point(point, mesh_world) if mesh_world else list(point)
        for buffer in other_buffers:
            if vertex_index < len(buffer) and isinstance(buffer[vertex_index], list):
                unexamined_lanes += len(buffer[vertex_index])
        lanes = _vertex_lanes(mesh, vertex_index, attr_indices)
        usable: dict[int, float] = {}
        for bone_index, weight in lanes.items():
            if bone_index not in matrices:
                matrices.update(_bone_matrices(slots, transforms, [bone_index]))
            if bone_index not in matrices:
                unresolved_lanes += 1
                continue
            usable[bone_index] = weight
        if not usable:
            continue
        for bone_index, weight in usable.items():
            palette, bone_world, _index = matrices[bone_index]
            rest = transform_point(transform_point(point, palette), bone_world)
            rest_error = max(rest_error, max(abs(rest[k] - placed[k]) for k in range(3)))
            usage[bone_index] = usage.get(bone_index, 0) + 1
        rows.append((point, placed, usable, sum(usable.values())))
    return {
        "rows": rows,
        "matrices": matrices,
        "unresolved_lanes": unresolved_lanes,
        "lanes_in_unexamined_attributes": unexamined_lanes,
        "bone_index_attr_count": len(buffers),
        "attr_indices_used": list(attr_indices),
        "rest_max_error": rest_error,
        "usage": usage,
        "vertices_sampled": len(indices),
        "vertices_total": len(points),
        "vertices_with_lanes": len(rows),
    }


def _hinge_metrics(sampled: dict[str, Any], bone_index: int, axis: list[float],
                   angle_degrees: float, tolerance: float) -> dict[str, Any]:
    """Hinge one real bone about its own stored joint and measure every lane."""
    matrices = sampled["matrices"]
    palette, bone_world, transform_index = matrices[bone_index]
    joint = bone_world[12:15]
    spin = axis_angle(axis, math.radians(angle_degrees))
    posed = local_frame_hinge(bone_world, joint, spin)
    naive = world_origin_hinge(bone_world, spin)
    hinge_m = hinge_about_point(joint, spin)
    origin = [0.0, 0.0, 0.0]

    hinge_error = 0.0
    rigid_error = 0.0
    naive_error = 0.0
    dropped_error = 0.0
    blend_error = 0.0
    blend_vertices = 0
    single_lane_vertices = 0
    for point, placed, lanes, total in sampled["rows"]:
        if bone_index not in lanes or total <= 0.0:
            continue
        expected = transform_point(placed, hinge_m)
        hinged = transform_point(transform_point(point, palette), posed)
        hinge_error = max(hinge_error, max(abs(hinged[k] - expected[k]) for k in range(3)))
        rigid_error = max(rigid_error,
                          abs(distance(hinged, joint) - distance(placed, joint)))
        # A converter that dropped mesh_world would skin from the unplaced vertex. The
        # gap is measured here too, and a rigid hinge cannot change it, which is why the
        # rest check already settled that question.
        dropped_error = max(dropped_error, distance(point, placed))
        naively = transform_point(transform_point(point, palette), naive)
        naive_error = max(naive_error, max(abs(naively[k] - expected[k]) for k in range(3)))
        if len(lanes) == 1:
            single_lane_vertices += 1
            continue
        blended = [0.0, 0.0, 0.0]
        for other_index, weight in lanes.items():
            other_palette, other_world, _index = matrices[other_index]
            other_point = hinged if other_index == bone_index else transform_point(
                transform_point(point, other_palette), other_world)
            for axis_index in range(3):
                blended[axis_index] += weight * other_point[axis_index]
        blended = [value / total for value in blended]
        fraction = lanes[bone_index] / total
        analytic = [placed[axis_index] + fraction * (hinged[axis_index] - placed[axis_index])
                    for axis_index in range(3)]
        blend_error = max(blend_error, max(abs(blended[k] - analytic[k]) for k in range(3)))
        blend_vertices += 1

    joint_drift = distance(transform_point(origin, posed), joint)
    naive_drift = distance(transform_point(origin, naive), joint)
    if naive_drift <= tolerance:
        naive_verdict = "identical"
    elif naive_error > tolerance:
        naive_verdict = "separated"
    else:
        naive_verdict = "not_separated"

    return {
        "palette_slot": bone_index,
        "transform_index": transform_index,
        "axis": [round(value, DIGITS) for value in axis],
        "angle_degrees": round(angle_degrees, 3),
        "joint": [round(value, DIGITS) for value in joint],
        "joint_drift": round(joint_drift, 12),
        "naive_joint_drift": round(naive_drift, DIGITS),
        "hinge_max_error": round(hinge_error, 12),
        "rigid_max_error": round(rigid_error, 12),
        "blend_max_error": round(blend_error, 12),
        "blend_vertices": blend_vertices,
        "single_lane_vertices": single_lane_vertices,
        "naive_max_error": round(naive_error, DIGITS),
        "naive_verdict": naive_verdict,
        "dropped_placement_max_error": round(dropped_error, DIGITS),
        "dropped_placement_verdict": "separated" if dropped_error > tolerance else "identical",
        "hinge_within_tolerance": hinge_error <= tolerance,
        "rigid_within_tolerance": rigid_error <= tolerance,
        # Kept as a construction audit, not as evidence: the conjugation is anchored to
        # B's own translation row, so origin x posed equals that row by algebra and can
        # never drift. The real discriminator is hinge_max_error, which compares two
        # independent paths onto the same point.
        "joint_fixity_holds_by_construction": joint_drift <= tolerance,
        "blend_within_tolerance": blend_vertices == 0 or blend_error <= tolerance,
        "rest_within_tolerance": sampled["rest_max_error"] <= tolerance,
    }


def hinge_tests(unit: dict[str, Any], sample_limit: int, max_bones: int,
                angle_degrees: float, tolerance: float) -> dict[str, Any]:
    transforms = unit["transform_matrices"]
    names = unit.get("transform_names") or {}
    pairings = _pairing_choices(unit, sample_limit)
    rows: list[dict[str, Any]] = []
    skipped: list[dict[str, Any]] = []
    for mesh in unit["meshes"]:
        mesh_info_index = mesh["mesh_info_index"]
        slots = _palette_slots(mesh)
        if not mesh.get("_points") or not slots or not mesh.get("bone_index_buffers"):
            skipped.append({"mesh_info_index": mesh_info_index,
                            "reason": "no_positions_no_palette_or_no_index_attributes"})
            continue
        choice = pairings.get(mesh_info_index)
        if not choice:
            skipped.append({"mesh_info_index": mesh_info_index,
                            "reason": "no_passing_weight_pairing"})
            continue
        attr_indices = _attr_indices(str(choice["pairing_model"]),
                                     len(mesh["bone_index_buffers"]))
        if not attr_indices:
            skipped.append({"mesh_info_index": mesh_info_index,
                            "reason": "unrecognized_pairing_model"})
            continue
        sampled = _mesh_rows(mesh, slots, transforms, attr_indices, sample_limit)
        if not sampled["rows"]:
            skipped.append({"mesh_info_index": mesh_info_index,
                            "reason": "no_resolved_lanes_in_sample"})
            continue
        ranked = sorted(sampled["usage"].items(), key=lambda item: (-item[1], item[0]))
        bones = [bone_index for bone_index, _count in ranked[:max_bones]] if max_bones else \
            [bone_index for bone_index, _count in ranked]
        for bone_index in bones:
            _palette, bone_world, transform_index = sampled["matrices"][bone_index]
            for label, marker in AXES:
                axis = list(marker) if not isinstance(marker, str) else _unit_axis(bone_world, marker)
                if axis is None:
                    rows.append({
                        "mesh_info_index": mesh_info_index,
                        "lod_index": mesh["lod_index"],
                        "mesh_transform_name": mesh["transform_name"],
                        "palette_slot": bone_index,
                        "transform_index": transform_index,
                        "bone_name": names.get(transform_index),
                        "axis_label": label,
                        "axis": None,
                        "naive_verdict": "degenerate_axis",
                        "hinge_within_tolerance": True,
                        "rigid_within_tolerance": True,
                        "joint_fixity_holds_by_construction": True,
                        "blend_within_tolerance": True,
                        "rest_within_tolerance": sampled["rest_max_error"] <= tolerance,
                        "note": "basis row too short to define an axis",
                    })
                    continue
                metrics = _hinge_metrics(sampled, bone_index, axis, angle_degrees, tolerance)
                rows.append({
                    "mesh_info_index": mesh_info_index,
                    "lod_index": mesh["lod_index"],
                    "mesh_transform_name": mesh["transform_name"],
                    "stored_matrix_identity_status": mesh["stored_matrix_identity_status"],
                    "pairing_model": choice["pairing_model"],
                    "pairing_ambiguous": choice["pairing_ambiguous"],
                    "passing_models": choice["passing_models"],
                    "vacuous_models": choice["vacuous_models"],
                    "alternative_passing_models_differ":
                        choice["alternative_passing_models_differ"],
                    "vertices_sampled": sampled["vertices_sampled"],
                    "vertices_total": sampled["vertices_total"],
                    "vertices_with_lanes": sampled["vertices_with_lanes"],
                    "lanes_unresolved": sampled["unresolved_lanes"],
                    "lanes_in_unexamined_attributes": sampled["lanes_in_unexamined_attributes"],
                    "bone_index_attr_count": sampled["bone_index_attr_count"],
                    "attr_indices_used": sampled["attr_indices_used"],
                    "rest_max_error": round(sampled["rest_max_error"], 12),
                    "bone_name": names.get(transform_index),
                    "axis_label": label,
                    **metrics,
                })
    return {"rows": rows, "skipped_meshes": skipped}


def _passed(row: dict[str, Any]) -> bool:
    return bool(row.get("hinge_within_tolerance") and row.get("rigid_within_tolerance")
                and row.get("blend_within_tolerance") and row.get("rest_within_tolerance"))


def _per_mesh_count(rows: list[dict[str, Any]]) -> dict[tuple[Any, Any], int]:
    """Collapse the per-(bone, axis) rows back to one count per mesh."""
    counts: dict[tuple[Any, Any], int] = {}
    for row in rows:
        counts[(row.get("mesh_info_index"), row.get("bone_index_attr_count"))] = max(
            counts.get((row.get("mesh_info_index"), row.get("bone_index_attr_count")), 0),
            int(row.get("lanes_in_unexamined_attributes", 0)))
    return counts


def summarize(units: list[dict[str, Any]]) -> dict[str, Any]:
    rows = [row for unit in units for row in unit["hinge"]["rows"]]
    live = [row for row in rows if row.get("naive_verdict") != "degenerate_axis"]
    # Counts are per mesh, and mesh_info_index repeats across Units, so collapse inside
    # each Unit before adding them together.
    per_mesh_lanes = [value for unit in units for value in _per_mesh_count(
        [row for row in unit["hinge"]["rows"]
         if row.get("naive_verdict") != "degenerate_axis"]).values()]
    return {
        "units": len(units),
        "meshes_checked": len({(unit["label"], row["mesh_info_index"])
                               for unit in units for row in unit["hinge"]["rows"]}),
        "hinge_rows": len(rows),
        "degenerate_axis_rows": len(rows) - len(live),
        "rows_checked": len(live),
        "rows_all_checks_within_tolerance": sum(1 for row in live if _passed(row)),
        "rows_failing": sum(1 for row in live if not _passed(row)),
        "bones_hinged": len({(unit["label"], row["mesh_info_index"], row["palette_slot"])
                             for unit in units for row in unit["hinge"]["rows"]}),
        # Per-mesh maxima: rows repeat a mesh's count once per hinged bone and axis, so
        # summing them would multiply the same vertices by the number of rows.
        "max_lanes_unresolved_per_mesh": max((row.get("lanes_unresolved", 0)
                                              for row in live), default=0),
        "max_lanes_in_unexamined_attributes_per_mesh": max(
            (row.get("lanes_in_unexamined_attributes", 0) for row in live), default=0),
        "total_lanes_in_unexamined_attributes": sum(per_mesh_lanes),
        "meshes_with_unexamined_lanes": sum(1 for value in per_mesh_lanes if value > 0),
        "pairing_ambiguous_rows": sum(1 for row in live if row.get("pairing_ambiguous")),
        "rows_with_differing_alternatives": sum(
            1 for row in live if row.get("alternative_passing_models_differ")),
        "rows_with_vacuous_models_excluded": sum(
            1 for row in live if row.get("vacuous_models")),
        "max_hinge_error": max((row.get("hinge_max_error", 0.0) for row in live), default=0.0),
        "max_rigid_error": max((row.get("rigid_max_error", 0.0) for row in live), default=0.0),
        "max_blend_error": max((row.get("blend_max_error", 0.0) for row in live), default=0.0),
        "max_joint_drift": max((row.get("joint_drift", 0.0) for row in live), default=0.0),
        "naive_separated": sum(1 for row in live if row.get("naive_verdict") == "separated"),
        "naive_identical": sum(1 for row in live if row.get("naive_verdict") == "identical"),
        "naive_not_separated": sum(1 for row in live
                                   if row.get("naive_verdict") == "not_separated"),
        "max_dropped_placement_error": max((row.get("dropped_placement_max_error", 0.0)
                                            for row in live), default=0.0),
        "dropped_separated": sum(1 for row in live
                                 if row.get("dropped_placement_verdict") == "separated"),
        "dropped_identical": sum(1 for row in live
                                 if row.get("dropped_placement_verdict") == "identical"),
        "blend_rows": sum(1 for row in live if row.get("blend_vertices", 0) > 0),
    }


def build_report(units: list[dict[str, Any]], sample_limit: int, max_bones: int,
                 angle_degrees: float, tolerance: float) -> dict[str, Any]:
    for unit in units:
        unit["hinge"] = hinge_tests(unit, sample_limit, max_bones, angle_degrees, tolerance)
    summary = summarize(units)
    report = {
        "schema": "SourceModelReader-HD2RealBoneHingeChecks1",
        "assumptions": {
            "matrix_convention": "row_major_4x4_row_vector",
            "palette_formula_candidate": "mesh_world_times_inverse_bone_world",
            "hinge_construction": "bone_world_times_translate_minus_joint_times_rotation_times_translate_joint",
            "control_construction": "bone_world_times_rotation",
            "angle_degrees": angle_degrees,
            "tolerance": tolerance,
            "sample_limit": sample_limit,
            "max_bones_per_mesh": max_bones,
            "axes": [label for label, _marker in AXES],
        },
        "summary": summary,
        "gates": {
            "every_row_within_tolerance": summary["rows_failing"] == 0,
            "no_control_left_unseparated": summary["naive_not_separated"] == 0,
        },
        "units": [{
            "label": unit["label"],
            "unit_id": unit["unit_id"],
            "package_id": unit["package_id"],
            "geometry_source": unit["geometry_source"],
            "skipped_meshes": unit["hinge"]["skipped_meshes"],
            "rows": unit["hinge"]["rows"],
        } for unit in units],
        "interpretation_limits": [
            "The posed bone matrices are synthesized here; HD2 animation data is not decoded, so this is a test of the stored rest matrices under a hypothetical hinge.",
            "With the default flags every vertex and every positively weighted bone is covered; a run narrowed by --sample-limit or --max-bones measures only its slice, and the report states both counts.",
            "A hinge whose joint sits at the space origin cannot separate the two pose constructions; those rows are labelled identical instead of counted as evidence.",
            "Unresolved lanes are counted and skipped, not guessed; they are the partial-skinning lanes already documented for the vec4_uint32 attribute.",
            "Lanes carried by bone-index attributes that the chosen pairing does not consult are counted separately as unexamined, so a zero unresolved count is not a claim that every index attribute behaves.",
            "Where a mesh offers several passing pairings, the alternative attribute can hold different bone indices while producing the same placed point; that is a rest-level degeneracy, so the tool keeps one chosen pairing and reports the alternatives instead of pretending the question is closed.",
            "Pairing models that resolve fewer than half the vertices of the best model are listed as vacuous rather than counted as ambiguity, because a model whose lanes are all unusable passes by checking nothing.",
            "Joint fixity of the conjugated hinge is an algebraic identity of the construction, reported as a construction audit rather than as evidence; the measured discriminators are the hinge identity, the rigid distance and the blend linearity.",
            "The hinge identity reduces to the same matrix equation as the rest invariant, so it is a re-expression of that contract through a rotation and a real joint rather than a new independent constraint.",
            "The dropped-placement gap is unchanged by the hinge because a rigid rotation preserves distance, which is why the rest check already decided it.",
            "Passing means the stored palette behaves as mesh_world times inverse(bone_world) under a rigid hinge; it is not a runtime shader trace.",
        ],
    }
    for unit in units:
        unit["hinge"]["rows"] = []
    return report


def render_markdown(report: dict[str, Any]) -> str:
    summary = report["summary"]
    lines = [
        "# HD2 Real-Bone Hinge Checks",
        "",
        "- Hinge construction: `{}`".format(report["assumptions"]["hinge_construction"]),
        "- Control construction: `{}`".format(report["assumptions"]["control_construction"]),
        "- Angle: {} degrees; axes: {}".format(
            report["assumptions"]["angle_degrees"], ", ".join(report["assumptions"]["axes"])),
        "- Sample limit per mesh: {}; bones per mesh: {}".format(
            report["assumptions"]["sample_limit"], report["assumptions"]["max_bones_per_mesh"]),
        "- Tolerance: {}".format(report["assumptions"]["tolerance"]),
        "",
        "## Summary",
        "",
        "| Metric | Value |",
        "|---|---:|",
        "| Units | {} |".format(summary["units"]),
        "| Meshes checked | {} |".format(summary["meshes_checked"]),
        "| Bones hinged | {} |".format(summary["bones_hinged"]),
        "| Hinge rows | {} |".format(summary["hinge_rows"]),
        "| Rows checked | {} |".format(summary["rows_checked"]),
        "| Degenerate axis rows | {} |".format(summary["degenerate_axis_rows"]),
        "| Rows within tolerance | {} |".format(summary["rows_all_checks_within_tolerance"]),
        "| Rows failing | {} |".format(summary["rows_failing"]),
        "| Max hinge error | {} |".format(summary["max_hinge_error"]),
        "| Max rigid distance error | {} |".format(summary["max_rigid_error"]),
        "| Max blend linearity error | {} |".format(summary["max_blend_error"]),
        "| Max joint drift | {} |".format(summary["max_joint_drift"]),
        "| Control separated | {} |".format(summary["naive_separated"]),
        "| Control identical (undecidable) | {} |".format(summary["naive_identical"]),
        "| Control not separated | {} |".format(summary["naive_not_separated"]),
        "| Max dropped-placement error | {} |".format(summary["max_dropped_placement_error"]),
        "| Rows where dropping mesh_world changes the answer | {} / {} |".format(
            summary["dropped_separated"], summary["dropped_identical"]),
        "| Rows with blended vertices | {} |".format(summary["blend_rows"]),
        "| Max unresolved lanes per mesh | {} |".format(summary["max_lanes_unresolved_per_mesh"]),
        "| Max lanes in unexamined index attributes per mesh | {} |".format(
            summary["max_lanes_in_unexamined_attributes_per_mesh"]),
        "| Unexamined lane slots (all meshes) | {} on {} meshes |".format(
            summary["total_lanes_in_unexamined_attributes"],
            summary["meshes_with_unexamined_lanes"]),
        "| Rows from ambiguous pairings | {} |".format(summary["pairing_ambiguous_rows"]),
        "| Rows whose alternative pairing holds other indices | {} |".format(
            summary["rows_with_differing_alternatives"]),
        "| Rows with vacuous models excluded from the tally | {} |".format(
            summary["rows_with_vacuous_models_excluded"]),
        "",
        "Gates: every row within tolerance = **{}**; no control left unseparated = **{}**".format(
            report["gates"]["every_row_within_tolerance"],
            report["gates"]["no_control_left_unseparated"]),
        "",
    ]
    for unit in report["units"]:
        lines.append("## {} (`{}`)".format(unit["label"], unit["unit_id"]))
        lines.append("")
        lines.append("- Package: {}".format(unit["package_id"]))
        lines.append("- Geometry source: {}".format(unit["geometry_source"]))
        if unit["skipped_meshes"]:
            reasons: dict[str, int] = {}
            for row in unit["skipped_meshes"]:
                reasons[row["reason"]] = reasons.get(row["reason"], 0) + 1
            lines.append("- Meshes skipped: {}".format(
                ", ".join("{} = {}".format(reason, count) for reason, count in sorted(reasons.items()))))
        lines.append("")
        lines.append("| Mesh | LOD | Bone | Transform | Axis | Joint | Hinge err | Rigid err | "
                     "Blend err | Sampled / total | Blend verts | Unresolved | Naive joint drift | Verdict |")
        lines.append("|---:|---:|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|---|")
        for row in unit["rows"]:
            verdict = "pass" if _passed(row) else "FAIL"
            if row.get("naive_verdict") == "degenerate_axis":
                verdict = "degenerate axis"
            lines.append("| {} | {} | {} | {} ({}) | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
                row["mesh_info_index"], row["lod_index"], row["palette_slot"],
                row.get("bone_name") or "unresolved", row.get("transform_index"),
                row["axis_label"], row.get("joint"),
                row.get("hinge_max_error"), row.get("rigid_max_error"),
                row.get("blend_max_error"),
                "{} / {}".format(row.get("vertices_sampled", "-"), row.get("vertices_total", "-")),
                row.get("blend_vertices", "-"), row.get("lanes_unresolved", "-"),
                row.get("naive_joint_drift"), verdict))
        lines.append("")
    lines.append("## Interpretation limits")
    lines.append("")
    lines.extend("- " + item for item in report["interpretation_limits"])
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unit", action="append", default=[], metavar="LABEL=PATH",
                        help="Hinge bones in these Unit JSON files; repeat. Defaults to the "
                             "three checked-in samples.")
    parser.add_argument("--sample-limit", type=int, default=DEFAULT_SAMPLE_LIMIT,
                        help="Vertices sampled per mesh, strided across the range; "
                             "0 (default) uses every vertex.")
    parser.add_argument("--max-bones", type=int, default=DEFAULT_MAX_BONES,
                        help="Most-used bones hinged per mesh; 0 (default) hinges every "
                             "positively used bone.")
    parser.add_argument("--angle-degrees", type=float, default=DEFAULT_ANGLE_DEGREES)
    parser.add_argument("--tolerance", type=float, default=REST_INVARIANT_TOLERANCE)
    parser.add_argument("--output-json", type=Path, default=DEFAULT_OUTPUT_JSON)
    parser.add_argument("--output-markdown", type=Path, default=DEFAULT_OUTPUT_MARKDOWN)
    args = parser.parse_args()

    if args.sample_limit < 0:
        parser.error("--sample-limit must be zero or positive")
    if args.max_bones < 0:
        parser.error("--max-bones must be zero or positive")
    if args.tolerance <= 0.0:
        parser.error("--tolerance must be positive")

    if args.unit:
        inputs: list[tuple[str, Path]] = []
        for item in args.unit:
            label, separator, path_text = item.partition("=")
            if not separator or not label or not path_text:
                parser.error("--unit expects LABEL=PATH")
            inputs.append((label, Path(path_text)))
    else:
        inputs = [(label, path) for label, path in DEFAULT_INPUTS.items()]
    for _label, path in inputs:
        if not path.is_file():
            parser.error("Input JSON does not exist: {}".format(path))

    try:
        units = [load_unit(path, label) for label, path in inputs]
        report = build_report(units, args.sample_limit, args.max_bones,
                              args.angle_degrees, args.tolerance)
        markdown = render_markdown(report)
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        parser.error("Real-bone hinge check failed: {}".format(exc))

    args.output_json.parent.mkdir(parents=True, exist_ok=True)
    args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
    args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    args.output_markdown.write_text(markdown, encoding="utf-8")

    summary = report["summary"]
    print("Real-bone hinge checks: rows={} within_tolerance={} failing={} "
          "max_hinge_error={} controls_separated={}".format(
              summary["hinge_rows"], summary["rows_all_checks_within_tolerance"],
              summary["rows_failing"], summary["max_hinge_error"], summary["naive_separated"]))
    print("Markdown: {}".format(args.output_markdown))
    print("JSON: {}".format(args.output_json))
    passed = report["gates"]["every_row_within_tolerance"] and report["gates"]["no_control_left_unseparated"]
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
