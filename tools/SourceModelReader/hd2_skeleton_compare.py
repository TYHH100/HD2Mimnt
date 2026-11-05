"""Exact-name Source SMD to HD2 skeleton comparison.

This is a diagnostic only: no fuzzy aliases, reparenting, coordinate conversion,
or rest-pose numeric comparison is performed.
"""
from __future__ import annotations
from collections import defaultdict
from typing import Any
from smd_reader import SMDDocument


def _key(name: str) -> str:
    return name.strip().casefold()


def compare_source_skeleton_to_hd2(source: SMDDocument, target_report: dict[str, Any]) -> dict[str, Any]:
    source_by_key: dict[str, list[Any]] = defaultdict(list)
    for node in source.nodes:
        source_by_key[_key(node.name)].append(node)

    transform_info = target_report.get("transform_info") or {}
    skeleton = transform_info.get("resolved_skeleton") or []
    target_by_key: dict[str, list[dict[str, Any]]] = defaultdict(list)
    target_unresolved = []
    target_ambiguous = []
    for item in skeleton:
        name = item.get("name")
        if not name:
            row = {
                "transform_index": item.get("transform_index"),
                "name_hash": item.get("name_hash"),
                "name_ambiguous": bool(item.get("name_ambiguous")),
                "parent_transform_index": item.get("parent_transform_index"),
            }
            (target_ambiguous if item.get("name_ambiguous") else target_unresolved).append(row)
        else:
            target_by_key[_key(str(name))].append(item)

    source_collisions = [
        {"normalized_name": key, "names": [n.name for n in nodes],
         "source_ids": [n.node_id for n in nodes]}
        for key, nodes in sorted(source_by_key.items()) if len(nodes) > 1
    ]
    target_collisions = [
        {"normalized_name": key, "names": [x.get("name") for x in items],
         "transform_indices": [x.get("transform_index") for x in items]}
        for key, items in sorted(target_by_key.items()) if len(items) > 1
    ]

    source_by_id = source.node_by_id

    def source_parent(node: Any) -> tuple[str | None, str]:
        if node.parent_id == -1:
            return None, "root"
        parent = source_by_id.get(node.parent_id)
        return (parent.name, "resolved") if parent else (None, "unresolved")

    def target_parent(item: dict[str, Any]) -> tuple[str | None, str]:
        if item.get("parent_name"):
            return str(item["parent_name"]), "resolved"
        if item.get("parent_transform_index") is None:
            return None, "root"
        return None, "unresolved"

    matched, source_unmapped, ambiguous = [], [], []
    parent_mismatches, parent_unresolved = [], []
    for node in sorted(source.nodes, key=lambda n: (n.name.casefold(), n.node_id)):
        key = _key(node.name)
        sp, ss = source_parent(node)
        base = {
            "source_node_id": node.node_id,
            "source_name": node.name,
            "source_parent_id": node.parent_id,
            "source_parent_name": sp,
        }
        candidates = target_by_key.get(key, [])
        if len(source_by_key[key]) > 1:
            ambiguous.append({**base, "reason": "source_casefold_collision"})
            continue
        if len(candidates) > 1:
            ambiguous.append({
                **base, "reason": "multiple_target_transforms_share_name",
                "candidate_target_transform_indices": [x.get("transform_index") for x in candidates],
            })
            continue
        if not candidates:
            source_unmapped.append(base)
            continue
        target = candidates[0]
        tp, ts = target_parent(target)
        if ss == "root" and ts == "root":
            relation = "match"
        elif ss == "resolved" and ts == "resolved":
            relation = "match" if _key(sp or "") == _key(tp or "") else "mismatch"
        elif ss == "unresolved" or ts == "unresolved":
            relation = "unresolved"
        else:
            relation = "mismatch"
        row = {
            **base,
            "target_transform_index": target.get("transform_index"),
            "target_name": target.get("name"),
            "target_name_hash": target.get("name_hash"),
            "target_parent_transform_index": target.get("parent_transform_index"),
            "target_parent_name": tp,
            "target_has_active_vertex_influence": bool(target.get("has_active_vertex_influence")),
            "target_active_vertex_influence_lane_count": int(target.get("active_vertex_influence_lane_count") or 0),
            "parent_relation": relation,
        }
        matched.append(row)
        if relation == "mismatch":
            parent_mismatches.append(row)
        elif relation == "unresolved":
            parent_unresolved.append(row)

    matched_keys = {_key(x["target_name"]) for x in matched}
    target_only = []
    for key, items in sorted(target_by_key.items()):
        if len(items) != 1 or key in matched_keys:
            continue
        item = items[0]
        target_only.append({
            "target_transform_index": item.get("transform_index"),
            "target_name": item.get("name"),
            "target_name_hash": item.get("name_hash"),
            "target_parent_transform_index": item.get("parent_transform_index"),
            "target_parent_name": item.get("parent_name"),
            "has_active_vertex_influence": bool(item.get("has_active_vertex_influence")),
            "active_vertex_influence_lane_count": int(item.get("active_vertex_influence_lane_count") or 0),
        })

    weight_summary = transform_info.get("weight_usage_summary") or {}
    active_matches = [x for x in matched if x["target_has_active_vertex_influence"]]
    warnings = []
    if source_unmapped:
        warnings.append("source_bones_without_exact_target_name")
    if ambiguous or target_ambiguous or target_collisions or source_collisions:
        warnings.append("ambiguous_bone_names")
    if parent_mismatches:
        warnings.append("parent_hierarchy_mismatches")
    if parent_unresolved:
        warnings.append("parent_hierarchy_unresolved")
    if target_unresolved:
        warnings.append("target_transforms_without_resolved_bone_names")

    unit = target_report.get("unit") or {}
    return {
        "schema": "SourceModelReader-SourceHD2SkeletonComparison1",
        "target": {
            "unit_file_id": unit.get("file_id"),
            "geometry_source": unit.get("geometry_source"),
            "package": (target_report.get("package") or {}).get("name"),
            "bones_resource_file_id": (target_report.get("bones_resource") or {}).get("file_id"),
        },
        "summary": {
            "status": "warning" if warnings else "ok",
            "source_smd_type": source.smd_type,
            "source_node_count": len(source.nodes),
            "target_transform_count": len(skeleton),
            "target_named_transform_count": int(weight_summary.get("named_transform_count") or len(target_by_key)),
            "target_active_weighted_transform_count": int(weight_summary.get("actively_weighted_transform_count") or 0),
            "exact_name_match_count": len(matched),
            "exact_name_match_active_target_count": len(active_matches),
            "source_unmapped_count": len(source_unmapped),
            "target_only_named_transform_count": len(target_only),
            "ambiguous_source_match_count": len(ambiguous),
            "ambiguous_target_name_count": len(target_ambiguous) + len(target_collisions),
            "unresolved_target_name_count": len(target_unresolved),
            "parent_mismatch_count": len(parent_mismatches),
            "parent_unresolved_count": len(parent_unresolved),
        },
        "matching_policy": {
            "name_matching": "exact Unicode casefold after trimming edge whitespace",
            "source_nodes_are_declared_smd_nodes": True,
            "source_active_vertex_influence_filter": "not available from SMDDocument; includes declared helper/unweighted nodes",
            "punctuation_or_alias_guessing": False,
            "parent_comparison": "compare resolved parent names only",
            "rest_pose_numeric_comparison": "not performed",
            "reason_pose_not_compared": (
                "Source SMD local Euler transforms and HD2 TransformInfo matrices "
                "need a validated coordinate, unit-scale and matrix-space conversion."
            ),
            "mapping_is_applied": False,
        },
        "matched": matched,
        "source_unmapped": source_unmapped,
        "target_only": target_only,
        "ambiguous_source_matches": ambiguous,
        "ambiguous_target_names": target_ambiguous,
        "target_unresolved_names": target_unresolved,
        "source_casefold_collisions": source_collisions,
        "target_casefold_collisions": target_collisions,
        "parent_mismatches": parent_mismatches,
        "parent_unresolved": parent_unresolved,
        "source_warnings": list(source.warnings),
        "source_infos": list(source.infos),
        "target_warnings": list(target_report.get("warnings") or []),
    }
