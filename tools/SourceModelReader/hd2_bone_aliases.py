"""Advisory Source ValveBiped -> HD2 Avatar bone-name hypotheses.

This module never changes exact-name matching and never approves a retarget.
Parent relationships are checked only when the Source parent also has an
explicit candidate alias. All other candidates remain advisory.
"""
from __future__ import annotations

from typing import Any

# Deliberately limited to the conventional humanoid core. Hair, ears, tail,
# ribbons, physics chains and accessory bones are not guessed.
SOURCE_TO_HD2_CANDIDATES = {
    "ValveBiped.Bip01_Pelvis": "hips",
    "ValveBiped.Bip01_Spine": "spine1",
    "ValveBiped.Bip01_Spine1": "spine2",
    "ValveBiped.Bip01_Spine2": "chest",
    "ValveBiped.Bip01_Neck1": "neck",
    "ValveBiped.Bip01_Head1": "head",
    "ValveBiped.Bip01_L_Thigh": "l_thigh",
    "ValveBiped.Bip01_L_Calf": "l_knee",
    "ValveBiped.Bip01_L_Foot": "l_foot",
    "ValveBiped.Bip01_L_Toe0": "l_toe",
    "ValveBiped.Bip01_L_Clavicle": "l_clavicle",
    "ValveBiped.Bip01_L_UpperArm": "l_shoulder",
    "ValveBiped.Bip01_L_Forearm": "l_elbow",
    "ValveBiped.Bip01_L_Hand": "l_hand",
    "ValveBiped.Bip01_R_Thigh": "r_thigh",
    "ValveBiped.Bip01_R_Calf": "r_knee",
    "ValveBiped.Bip01_R_Foot": "r_foot",
    "ValveBiped.Bip01_R_Toe0": "r_toe",
    "ValveBiped.Bip01_R_Clavicle": "r_clavicle",
    "ValveBiped.Bip01_R_UpperArm": "r_shoulder",
    "ValveBiped.Bip01_R_Forearm": "r_elbow",
    "ValveBiped.Bip01_R_Hand": "r_hand",
}


def build_alias_candidates(comparison: dict[str, Any]) -> dict[str, Any]:
    """Build advisory candidates from one exact-name comparison report."""
    source_rows = {
        row.get("source_name"): row
        for row in comparison.get("source_unmapped", [])
    }
    target_rows = {
        row.get("target_name"): row
        for row in comparison.get("target_only", [])
    }
    candidates = []
    absent_source = []
    absent_target = []

    for source_name, target_name in SOURCE_TO_HD2_CANDIDATES.items():
        source = source_rows.get(source_name)
        target = target_rows.get(target_name)
        if source is None:
            absent_source.append(source_name)
            continue
        if target is None:
            absent_target.append(target_name)
            continue

        source_parent = source.get("source_parent_name")
        expected_target_parent = SOURCE_TO_HD2_CANDIDATES.get(source_parent)
        actual_target_parent = target.get("target_parent_name")
        if expected_target_parent is not None:
            parent_status = (
                "match" if expected_target_parent.casefold() == str(actual_target_parent or "").casefold()
                else "mismatch"
            )
        else:
            parent_status = "not_checked_source_parent_has_no_candidate_alias"

        candidates.append({
            "source_name": source_name,
            "source_node_id": source.get("source_node_id"),
            "source_parent_name": source_parent,
            "source_active_vertex_influence": source.get("source_has_active_vertex_influence"),
            "target_name": target_name,
            "target_transform_index": target.get("target_transform_index"),
            "target_parent_name": actual_target_parent,
            "target_active_vertex_influence": target.get("target_has_active_vertex_influence"),
            "parent_status": parent_status,
            "approval": "advisory_only",
        })

    return {
        "policy": "explicit_humanoid_alias_candidates_only; no fuzzy matching or automatic retarget",
        "candidate_count": len(candidates),
        "parent_match_count": sum(x["parent_status"] == "match" for x in candidates),
        "parent_mismatch_count": sum(x["parent_status"] == "mismatch" for x in candidates),
        "parent_not_checked_count": sum(
            x["parent_status"] == "not_checked_source_parent_has_no_candidate_alias"
            for x in candidates
        ),
        "source_names_absent_from_report": absent_source,
        "target_names_absent_from_report": absent_target,
        "candidates": candidates,
    }
