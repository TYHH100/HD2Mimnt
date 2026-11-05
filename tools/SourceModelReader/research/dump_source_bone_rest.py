#!/usr/bin/env python3
"""Measure Source bone rest positions and segment lengths.

Bounding boxes cannot decide a unit convention, because two different models
have two different bodies. Bone segment lengths can: a human thigh is about half
a meter in any authored scale, so the stored magnitude says whether the model is
authored in meters, centimeters or at a custom scale.

Read-only. Uses the existing MDL reader and rewrites nothing.
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

from source_model_reader import ReaderError, open_model  # noqa: E402

# Anthropometric reference lengths in meters, used only as a scale sanity check.
# Values are deliberately loose adult ranges, not a rig specification.
# ValveBiped naming differs between toolkits, so each end of a chain lists the
# candidate spellings actually present in compiled models.
LANDMARK_CHAINS = {
    "pelvis_to_head": (["ValveBiped.Bip01_Pelvis"], ["ValveBiped.Bip01_Head1"]),
    "pelvis_to_spine": (["ValveBiped.Bip01_Pelvis"], ["ValveBiped.Bip01_Spine"]),
    "spine_chain": (["ValveBiped.Bip01_Spine"], ["ValveBiped.Bip01_Spine4"]),
    "thigh_left": (["ValveBiped.Bip01_L_Thigh", "ValveBiped.Bip01_Thigh_L"],
                   ["ValveBiped.Bip01_L_Calf", "ValveBiped.Bip01_LowerThigh_L"]),
    "calf_left": (["ValveBiped.Bip01_L_Calf", "ValveBiped.Bip01_LowerThigh_L"],
                  ["ValveBiped.Bip01_L_Foot", "ValveBiped.Bip01_LowerLeg_L"]),
    "thigh_right": (["ValveBiped.Bip01_R_Thigh", "ValveBiped.Bip01_UpperThigh_R"],
                    ["ValveBiped.Bip01_R_Calf", "ValveBiped.Bip01_LowerThigh_R"]),
    "upper_arm_left": (["ValveBiped.Bip01_L_UpperArm"], ["ValveBiped.Bip01_L_Forearm"]),
    "forearm_left": (["ValveBiped.Bip01_L_Forearm"], ["ValveBiped.Bip01_L_Hand"]),
    "upper_arm_right": (["ValveBiped.Bip01_R_UpperArm"], ["ValveBiped.Bip01_R_Forearm"]),
}
REFERENCE_METERS = {
    "pelvis_to_head": 0.60,
    "pelvis_to_spine": 0.12,
    "spine_chain": 0.42,
    "thigh_left": 0.45,
    "calf_left": 0.42,
    "thigh_right": 0.45,
    "upper_arm_left": 0.30,
    "forearm_left": 0.26,
    "upper_arm_right": 0.30,
}


def _first_present(candidates: list[str], positions: dict[str, list[float]]) -> str | None:
    for name in candidates:
        if name in positions:
            return name
    return None


def _rotation_from_quaternion(quat: tuple[float, float, float, float]) -> list[list[float]]:
    x, y, z, w = quat
    length = math.sqrt(x * x + y * y + z * z + w * w)
    if length <= 0.0 or not math.isfinite(length):
        return [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]]
    x, y, z, w = x / length, y / length, z / length, w / length
    return [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ]


def _multiply(left: list[list[float]], right: list[list[float]]) -> list[list[float]]:
    """Multiply two column-major affine matrices stored as rows of 3 plus a translation."""
    result = [[0.0, 0.0, 0.0] for _ in range(3)]
    for row in range(3):
        for column in range(3):
            result[row][column] = sum(left[row][k] * right[k][column] for k in range(3))
    return result


def build_world_positions(bones: list[dict[str, Any]]) -> dict[str, list[float]]:
    """Compose parent-relative translations and rotations into rest positions."""
    world: dict[int, dict[str, Any]] = {}
    by_index = {bone["index"]: bone for bone in bones}

    def resolve(index: int, depth: int = 0) -> dict[str, Any]:
        if index in world:
            return world[index]
        bone = by_index.get(index)
        if bone is None or depth > len(bones):
            entry = {"matrix": [[1.0, 0.0, 0.0], [0.0, 1.0, 0.0], [0.0, 0.0, 1.0]],
                     "translation": [0.0, 0.0, 0.0]}
            world[index] = entry
            return entry
        rotation = _rotation_from_quaternion(bone["quat"])
        local = {"matrix": rotation, "translation": list(bone["pos"])}
        parent = bone["parent"]
        if parent is None or parent == index or parent not in by_index:
            world[index] = local
            return local
        above = resolve(parent, depth + 1)
        combined = _multiply(above["matrix"], local["matrix"])
        shifted = [above["translation"][k] + sum(
            above["matrix"][k][c] * local["translation"][c] for c in range(3)
        ) for k in range(3)]
        entry = {"matrix": combined, "translation": shifted}
        world[index] = entry
        return entry

    for bone in bones:
        resolve(bone["index"])
    return {
        str(bone["name"]): world[bone["index"]]["translation"]
        for bone in bones
        if bone["index"] in world
    }


def chain_lengths(positions: dict[str, list[float]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for label, (start_candidates, end_candidates) in LANDMARK_CHAINS.items():
        start_name = _first_present(start_candidates, positions)
        end_name = _first_present(end_candidates, positions)
        if start_name is None or end_name is None:
            rows.append({"chain": label, "from": "|".join(start_candidates),
                         "to": "|".join(end_candidates), "status": "bone_not_found"})
            continue
        start, end = positions[start_name], positions[end_name]
        distance = math.sqrt(sum((end[k] - start[k]) ** 2 for k in range(3)))
        reference = REFERENCE_METERS[label]
        rows.append({
            "chain": label,
            "from": start_name,
            "to": end_name,
            "status": "measured",
            "stored_units": distance,
            "reference_meters": reference,
            "stored_units_per_meter": distance / reference if reference else None,
        })
    return rows


FRONT_MARKERS = ("frount", "front", "bang", "eye", "jaw", "nose", "mouth", "tongue", "visor")
BACK_MARKERS = ("back", "ponytail", "cape", "tail")


def facing_evidence(positions: dict[str, list[float]]) -> dict[str, Any]:
    """Infer which way the model faces from front and back marker bones.

    Hair and accessory bones are authored relative to the body, so their lateral
    Y positions separate the face side from the back side without needing a
    camera view. The author misspelling "Frount" is matched deliberately.
    """
    def collect(markers: tuple[str, ...]) -> list[dict[str, Any]]:
        rows = [
            {"name": name, "y": position[1]}
            for name, position in positions.items()
            if any(marker in str(name).lower() for marker in markers)
        ]
        rows.sort(key=lambda row: abs(row["y"]), reverse=True)
        return rows[:12]

    front = collect(FRONT_MARKERS)
    back = collect(BACK_MARKERS)
    front_mean = sum(row["y"] for row in front) / len(front) if front else None
    back_mean = sum(row["y"] for row in back) / len(back) if back else None
    inferred = None
    if front_mean is not None and back_mean is not None:
        inferred = "-Y" if front_mean < back_mean else "+Y"
    return {
        "front_marker_bones": front,
        "front_mean_y": front_mean,
        "back_marker_bones": back,
        "back_mean_y": back_mean,
        "inferred_facing_axis": inferred,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help=".mdl, model folder, ZIP or uncompressed .gma")
    parser.add_argument("--model", help="MDL path inside the archive when several exist")
    parser.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    try:
        model = open_model(args.input, args.model, args.backend)
        bones = [
            {
                "index": bone.index,
                "name": bone.name,
                "parent": bone.parent,
                "pos": [float(value) for value in bone.pos],
                "quat": [float(value) for value in bone.quat],
            }
            for bone in model.bones
        ]
    except (ReaderError, OSError, ValueError, TypeError, KeyError) as exc:
        parser.error(f"Source bone rest dump failed: {exc}")

    positions = build_world_positions(bones)
    rows = chain_lengths(positions)
    facing = facing_evidence(positions)
    measured = [row for row in rows if row["status"] == "measured"]
    ratios = [row["stored_units_per_meter"] for row in measured
              if row["stored_units_per_meter"] not in (None, 0.0)]
    extents = {}
    if positions:
        axes = ["x", "y", "z"]
        for axis in range(3):
            values = [item[axis] for item in positions.values()]
            extents[axes[axis]] = {"min": min(values), "max": max(values),
                                   "size": max(values) - min(values)}
    report = {
        "schema": "SourceModelReader-SourceBoneRest1",
        "bone_count": len(bones),
        "named_positions": len(positions),
        "chains": rows,
        "facing": facing,
        "ratio_median": sorted(ratios)[len(ratios) // 2] if ratios else None,
        "ratio_min": min(ratios) if ratios else None,
        "ratio_max": max(ratios) if ratios else None,
        "bone_extent_from_root": extents,
        "interpretation_limits": [
            "Segment lengths are compared against loose adult reference ranges, so the derived ratio is a scale indication, not a calibrated unit proof.",
            "Rest positions come from composing parent-relative pos and quat; the reader does not apply axis conversion flags beyond what the MDL already stores.",
            "Nothing here converts, rescales or rewrites Source geometry; it only measures it.",
            "Facing markers are selected by the model author's bone names, but the evidence is the sign of the measured Y values, not the wording of a name.",
        ],
    }
    lines = [
        "# Source Bone Rest Measurements",
        "",
        f"- Model: {args.model or Path(args.input).name}",
        f"- Bone definitions: {len(bones)}",
        f"- Chain ratio median (stored units per meter): {report['ratio_median'] if report['ratio_median'] is None else format(report['ratio_median'], '.4f')}",
        f"- Ratio range: {report['ratio_min'] if report['ratio_min'] is None else format(report['ratio_min'], '.4f')}"
        f" to {report['ratio_max'] if report['ratio_max'] is None else format(report['ratio_max'], '.4f')}",
        "",
        "| Chain | From | To | Stored length | Reference meters | Units per meter |",
        "|---|---|---|---:|---:|---:|",
    ]
    for row in rows:
        if row["status"] != "measured":
            lines.append("| {} | {} | {} | {} | - | - |".format(
                row["chain"], row["from"], row["to"], row["status"]))
            continue
        lines.append("| {} | {} | {} | {:.4f} | {:.2f} | {:.4f} |".format(
            row["chain"], row["from"], row["to"], row["stored_units"],
            row["reference_meters"], row["stored_units_per_meter"]))
    lines.extend(["", "## Rest position extent (relative to the skeleton root)", ""])
    for axis in ("x", "y", "z"):
        row = extents.get(axis)
        if row:
            lines.append("- {}: min {:.4f}, max {:.4f}, size {:.4f}".format(
                axis.upper(), row["min"], row["max"], row["size"]))
    lines.extend(["", "## Facing evidence (rest position Y)", ""])
    lines.append("- Front marker bones ({}), mean Y: {}".format(
        len(facing["front_marker_bones"]),
        "(none)" if facing["front_mean_y"] is None else format(facing["front_mean_y"], ".4f")))
    for row in facing["front_marker_bones"]:
        lines.append("  - {} Y {:.4f}".format(row["name"], row["y"]))
    lines.append("- Back marker bones ({}), mean Y: {}".format(
        len(facing["back_marker_bones"]),
        "(none)" if facing["back_mean_y"] is None else format(facing["back_mean_y"], ".4f")))
    for row in facing["back_marker_bones"]:
        lines.append("  - {} Y {:.4f}".format(row["name"], row["y"]))
    lines.append("- Inferred facing: {}".format(facing["inferred_facing_axis"] or "undetermined"))
    lines.extend(["", "## Interpretation limits", ""])
    lines.extend("- " + item for item in report["interpretation_limits"])
    markdown = "\n".join(lines) + "\n"

    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")
    print(markdown)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
