#!/usr/bin/env python3
"""Compare Source and HD2 rest joints and test the axis rule numerically.

Phase 3 asks for rest-joint evidence per candidate mapping, and Phase 1 asks
whether the Source-to-HD2 axis relation really is a 180 degree rotation about +Z.
Both need the same measurement, so this script derives the scale from paired bone
lengths among the candidate joints, then fits each candidate axis hypothesis by
aligning the root joint and reports the residual joint error per hypothesis.

Read-only and advisory. It maps nothing into a converter, approves no alias and
rewrites no resource byte: every row keeps the approval field from the alias
layer, and ambiguous rows stay explicitly unresolved.
"""
from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from dump_source_bone_rest import build_world_positions  # noqa: E402
from source_model_reader import ReaderError, open_model  # noqa: E402

# Each hypothesis maps Source (x, y, z) onto HD2 axes with signs, before scale.
AXIS_HYPOTHESES = {
    "rot180_z": (-1.0, -1.0, 1.0),
    "scale_only": (1.0, 1.0, 1.0),
    "mirror_x": (-1.0, 1.0, 1.0),
    "mirror_y": (1.0, -1.0, 1.0),
}
ROOT_PAIR = ("ValveBiped.Bip01_Pelvis", "hips")


def paired_lengths(source_positions: dict[str, list[float]],
                   target_positions: dict[str, list[float]],
                   candidates: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Measure segment lengths for candidate pairs whose parent is also mapped."""
    by_source = {row["source_name"]: row for row in candidates}
    rows: list[dict[str, Any]] = []
    for row in candidates:
        parent_name = row.get("source_parent_name")
        parent_row = by_source.get(parent_name) if parent_name else None
        if not parent_row:
            continue
        child_source = source_positions.get(row["source_name"])
        parent_source = source_positions.get(parent_name)
        child_target = target_positions.get(row["target_name"])
        parent_target = target_positions.get(parent_row["target_name"])
        if not all((child_source, parent_source, child_target, parent_target)):
            continue
        source_length = _distance(child_source, parent_source)
        target_length = _distance(child_target, parent_target)
        if source_length <= 1.0e-9 or target_length <= 1.0e-9:
            continue
        rows.append({
            "segment": f"{parent_name} -> {row['source_name']}",
            "target_segment": f"{parent_row['target_name']} -> {row['target_name']}",
            "source_length": source_length,
            "target_length": target_length,
            "hd2_meters_per_source_unit": target_length / source_length,
        })
    return rows


def _distance(left: list[float], right: list[float]) -> float:
    return math.sqrt(sum((left[k] - right[k]) ** 2 for k in range(3)))


def hypothesis_residuals(source_positions: dict[str, list[float]],
                         target_positions: dict[str, list[float]],
                         candidates: list[dict[str, Any]], scale: float) -> dict[str, dict[str, Any]]:
    """Align each hypothesis at the root joint, then measure joint residuals."""
    out: dict[str, dict[str, Any]] = {}
    pairs = [
        (source_positions.get(row["source_name"]), target_positions.get(row["target_name"]), row)
        for row in candidates
    ]
    pairs = [entry for entry in pairs if entry[0] and entry[1]]
    if not pairs:
        return {}
    for name, signs in AXIS_HYPOTHESES.items():
        mapped = [[signs[axis] * scale * value[axis] for axis in range(3)] for value, _, _ in pairs]
        root = next(((index, row) for index, row in enumerate(pairs)
                     if row[2]["source_name"] == ROOT_PAIR[0]), None)
        if root is None:
            root = (0, pairs[0])
        offset = [mapped[root[0]][axis] - root[1][1][axis] for axis in range(3)]
        errors = [
            _distance([mapped[index][axis] - offset[axis] for axis in range(3)], pair[1])
            for index, pair in enumerate(pairs)
        ]
        joints = [
            {
                "source": pair[2]["source_name"], "target": pair[2]["target_name"],
                "residual_meters": round(errors[index], 4),
            }
            for index, pair in enumerate(pairs)
        ]
        joints.sort(key=lambda row: -row["residual_meters"])
        out[name] = {
            "root_alignment_source": root[1][2]["source_name"],
            "root_alignment_target": root[1][2]["target_name"],
            "root_offset": [round(value, 4) for value in offset],
            "joint_count": len(errors),
            "mean_residual": statistics.mean(errors),
            "median_residual": statistics.median(errors),
            "max_residual": max(errors),
            "residual_by_source": {
                pair[2]["source_name"]: round(errors[index], 4)
                for index, pair in enumerate(pairs)
            },
            "worst_joints": joints[:5],
        }
    return out


def disposition(candidate: dict[str, Any], residual: float | None) -> str:
    """State a review disposition without approving anything."""
    if candidate.get("approval") != "advisory_only":
        return "needs review"
    if str(candidate.get("parent_status", "")).startswith("mismatch"):
        return "candidate with structural mismatch"
    if residual is None:
        return "candidate without rest evidence"
    if residual <= 0.15:
        return "candidate with close rest joint"
    if residual <= 0.45:
        return "candidate with offset rest joint"
    return "candidate with far rest joint"


def build_rows(source_positions: dict[str, list[float]], target_positions: dict[str, list[float]],
               candidates: list[dict[str, Any]], scale: float,
               residuals: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    per_joint: dict[str, dict[str, float]] = {}
    for hypothesis, block in residuals.items():
        for source_name, value in block.get("residual_by_source", {}).items():
            per_joint.setdefault(source_name, {})[hypothesis] = value
    rows: list[dict[str, Any]] = []
    for candidate in candidates:
        name = candidate["source_name"]
        source = source_positions.get(name)
        target = target_positions.get(candidate["target_name"])
        hypothesis_errors = {
            key: value
            for key, value in per_joint.get(name, {}).items()
        }
        rows.append({
            "source_name": name,
            "target_name": candidate["target_name"],
            "source_parent_name": candidate.get("source_parent_name"),
            "target_parent_name": candidate.get("target_parent_name"),
            "parent_status": candidate.get("parent_status"),
            "source_active_vertex_influence": candidate.get("source_active_vertex_influence"),
            "target_active_influence_lanes": candidate.get("target_active_vertex_influence_lane_count"),
            "source_rest": [round(value, 4) for value in source] if source else None,
            "target_rest": [round(value, 4) for value in target] if target else None,
            "worst_hypothesis_residuals": hypothesis_errors,
            "disposition": disposition(candidate, min(hypothesis_errors.values()) if hypothesis_errors else None),
            "approval": candidate.get("approval", "advisory_only"),
        })
    return rows


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# Source / HD2 Rest Joint Comparison",
        "",
        f"- Source model: {report['source_model']}",
        f"- HD2 target Unit: {report['target_unit']}",
        f"- Candidate mappings: {report['candidate_count']}",
        f"- Scale estimate (HD2 meters per Source unit): {report['scale_estimate']:.6f} "
        f"(median of {report['segment_count']} paired bone lengths, range "
        f"{report['scale_min']:.6f} to {report['scale_max']:.6f})",
        "",
        "## Axis hypothesis residuals after root alignment",
        "",
        "| Hypothesis | Joints | Mean error | Median error | Max error | Worst joints |",
        "|---|---:|---:|---:|---:|---|",
    ]
    for name, block in report["hypotheses"].items():
        lines.append("| {} | {} | {:.4f} | {:.4f} | {:.4f} | {} |".format(
            name, block["joint_count"], block["mean_residual"], block["median_residual"],
            block["max_residual"],
            ", ".join(f"{row['source'].replace('ValveBiped.', '')}@{row['residual_meters']}"
                      for row in block["worst_joints"][:3]),
        ))
    lines.extend([
        "",
        "## Paired bone lengths behind the scale estimate",
        "",
        "| Segment | Source length | Target meters | Meters per Source unit |",
        "|---|---:|---:|---:|",
    ])
    for row in report["segments"]:
        lines.append("| {} | {:.4f} | {:.4f} | {:.6f} |".format(
            row["segment"].replace("ValveBiped.", ""), row["source_length"],
            row["target_length"], row["hd2_meters_per_source_unit"]))
    fit = report["least_squares_fit"]
    if fit:
        lines.extend([
            "",
            "## Least-squares orientation test (threshold-free)",
            "",
            "Center both joint clouds, fit A from the cross-covariance and read sign(det(A)).",
            "Positive means the best fit preserves orientation, negative means a mirror.",
            "",
            "- Joint pairs: {}".format(fit["joint_pairs"]),
            "- Determinant: {:+.6e}; normalized by column norms: {}".format(
                fit["determinant"],
                "unavailable" if fit["normalized_determinant"] is None
                else "{:+.6f}".format(fit["normalized_determinant"])),
            "- Orientation: **{}**".format(fit["orientation"]),
            "- Fit RMS: {:.4f} m; worst joint {:.4f} m".format(
                fit["fit_rms_meters"], fit["fit_max_meters"]),
            "- Column norms: {}".format(
                ", ".join("{:.6f}".format(value) for value in fit["column_norms"])),
            "- Fitted matrix (row-major): {}".format(
                ", ".join("{:.6f}".format(value) for row in fit["matrix"] for value in row)),
        ])
    lines.extend([
        "",
        "## Frame chirality: per-triple sign survey",
        "",
        "Sign of det(left, right, up) about the root joint, per rig. Same sign in both rigs",
        "means an even number of axis negations, which is a rotation; opposite means a mirror.",
        "",
        "| Rig | Triple | Signed volume | Volume / |L||R||U| | Usable |",
        "|---|---|---:|---:|---|",
    ])
    for rig, key in (("Source", "source_frames"), ("HD2", "target_frames")):
        for frame in report["chirality"][key]:
            lines.append("| {} | {} / {} / {} about {} | {:+.6f} | {:.4f} | {} |".format(
                rig, frame["left"], frame["right"], frame["up"], frame["origin"],
                frame["signed_volume"], frame["volume_scale_ratio"],
                "no" if frame["degenerate"] else "yes",
            ))
    chirality = report["chirality"]
    lines.extend([
        "",
        "- Usable pairs: {}; same-sign {}; opposite-sign {}".format(
            chirality["usable_pair_count"], chirality["same_sign_pairs"],
            chirality["opposite_sign_pairs"]),
        "- Required negation parity: **{}**".format(chirality["required_negation_parity"]),
        "",
        "## Candidate mapping review table",
        "",
        "| Source | Target | Source parent | Target parent | Parent status | Active (src/tgt) | Disposition | Approval |",
        "|---|---|---|---|---|---|---|---|",
    ])
    for row in report["rows"]:
        lines.append("| {} | {} | {} | {} | {} | {} | {} | {} |".format(
            row["source_name"], row["target_name"],
            row["source_parent_name"] or "(none)", row["target_parent_name"] or "(none)",
            row["parent_status"],
            f"{row['source_active_vertex_influence']}/{row['target_active_influence_lanes']}",
            row["disposition"], row["approval"],
        ))
    lines.extend(["", "## Interpretation limits", ""])
    lines.extend("- " + item for item in report["interpretation_limits"])
    return "\n".join(lines) + "\n"


CHIRALITY_TRIPLES = (
    ("ValveBiped.Bip01_L_UpperArm", "ValveBiped.Bip01_R_UpperArm", "ValveBiped.Bip01_Head1",
     "ValveBiped.Bip01_Pelvis"),
    ("ValveBiped.Bip01_L_Forearm", "ValveBiped.Bip01_R_Forearm", "ValveBiped.Bip01_Head1",
     "ValveBiped.Bip01_Pelvis"),
    ("ValveBiped.Bip01_L_Thigh", "ValveBiped.Bip01_R_Thigh", "ValveBiped.Bip01_Head1",
     "ValveBiped.Bip01_Pelvis"),
)
HD2_CHIRALITY_TRIPLES = (
    ("l_shoulder", "r_shoulder", "head", "hips"),
    ("l_elbow", "r_elbow", "head", "hips"),
    ("l_thigh", "r_thigh", "head", "hips"),
)
DEGENERACY_RATIO = 0.02


def _determinant(left: list[float], right: list[float], up: list[float]) -> float:
    return (left[0] * (right[1] * up[2] - right[2] * up[1])
            - left[1] * (right[0] * up[2] - right[2] * up[0])
            + left[2] * (right[0] * up[1] - right[1] * up[0]))


def _relative_triple(names: tuple[str, str, str, str],
                     positions: dict[str, list[float]]) -> dict[str, Any] | None:
    """Signed volume of (left, right, up) about the origin joint, with a degeneracy gate."""
    left, right, up, origin = (positions.get(name) for name in names)
    if not all((left, right, up, origin)):
        return None
    vectors = [
        [left[k] - origin[k] for k in range(3)],
        [right[k] - origin[k] for k in range(3)],
        [up[k] - origin[k] for k in range(3)],
    ]
    product = 1.0
    for vector in vectors:
        product *= math.sqrt(sum(component * component for component in vector))
    volume = _determinant(*vectors)
    if product <= 1.0e-12:
        return None
    return {
        "left": names[0], "right": names[1], "up": names[2], "origin": names[3],
        "signed_volume": volume,
        "volume_scale_ratio": abs(volume) / product,
        "degenerate": abs(volume) / product < DEGENERACY_RATIO,
    }


def chirality_comparison(source_positions: dict[str, list[float]],
                         target_positions: dict[str, list[float]],
                         source_triples: tuple[tuple[str, str, str, str], ...] = CHIRALITY_TRIPLES,
                         target_triples: tuple[tuple[str, str, str, str], ...] = HD2_CHIRALITY_TRIPLES
                         ) -> dict[str, Any]:
    """Decide whether an axis flip count must be even or odd from both rigs' frames.

    The sign of det(left, right, up) says the chirality of each rig's own stored
    frame. Same sign in both rigs means the mapping needs an even number of axis
    negations, which is a rotation; opposite signs mean a mirror. Volume is
    scale-dependent, so only the sign is compared, and near-coplanar joint triples
    are gated out because their sign carries no information.
    """
    source_rows: list[dict[str, Any] | None] = [
        _relative_triple(names, source_positions) for names in source_triples
    ]
    target_rows: list[dict[str, Any] | None] = [
        _relative_triple(names, target_positions) for names in target_triples
    ]
    # Pair by position, then drop pairs whose triples are missing or near-coplanar:
    # zipping filtered lists could line a shoulder triple against a thigh triple.
    usable = [
        (source_row, target_row)
        for source_row, target_row in zip(source_rows, target_rows)
        if source_row and target_row
        and not source_row["degenerate"] and not target_row["degenerate"]
    ]
    signs = [(source_row["signed_volume"] > 0) == (target_row["signed_volume"] > 0)
             for source_row, target_row in usable]
    return {
        "source_frames": [row for row in source_rows if row],
        "target_frames": [row for row in target_rows if row],
        "usable_pair_count": len(usable),
        "same_sign_pairs": sum(signs),
        "opposite_sign_pairs": len(signs) - sum(signs),
        "chirality_agrees": bool(signs) and all(signs),
        "chirality_disagrees": bool(signs) and not any(signs),
        "required_negation_parity": (
            "even (rotation)" if signs and all(signs)
            else "odd (mirror)" if signs and not any(signs) else "undetermined"
        ),
    }


def _invert3(matrix: list[list[float]]) -> list[list[float]] | None:
    determinant = (matrix[0][0] * (matrix[1][1] * matrix[2][2] - matrix[1][2] * matrix[2][1])
                   - matrix[0][1] * (matrix[1][0] * matrix[2][2] - matrix[1][2] * matrix[2][0])
                   + matrix[0][2] * (matrix[1][0] * matrix[2][1] - matrix[1][1] * matrix[2][0]))
    if abs(determinant) <= 1.0e-18:
        return None
    def cofactor(row: int, column: int) -> float:
        minor = [[matrix[r][c] for c in range(3) if c != column] for r in range(3) if r != row]
        sign = -1.0 if (row + column) % 2 else 1.0
        return sign * (minor[0][0] * minor[1][1] - minor[0][1] * minor[1][0])
    return [[cofactor(j, i) / determinant for j in range(3)] for i in range(3)]


def least_squares_map(source_positions: dict[str, list[float]],
                      target_positions: dict[str, list[float]],
                      candidates: list[dict[str, Any]]) -> dict[str, Any] | None:
    """Fit the best linear map from Source joints to HD2 joints and read its sign.

    Centering both clouds and solving A from the cross-covariance needs no chosen
    axis hypothesis and no degeneracy threshold: sign(det(A)) says whether the
    best-fitting transform preserves orientation (rotation plus scale) or reverses
    it (mirror). A near-zero determinant would mean the fit cannot tell, and that
    value is reported instead of being hidden behind a threshold.
    """
    pairs = [
        (source_positions.get(row["source_name"]), target_positions.get(row["target_name"]))
        for row in candidates
    ]
    pairs = [pair for pair in pairs if pair[0] and pair[1]]
    if len(pairs) < 6:
        return None
    source_mean = [sum(pair[0][k] for pair in pairs) / len(pairs) for k in range(3)]
    target_mean = [sum(pair[1][k] for pair in pairs) / len(pairs) for k in range(3)]
    covariance = [[0.0] * 3 for _ in range(3)]
    spread = [[0.0] * 3 for _ in range(3)]
    for source, target in pairs:
        left = [source[k] - source_mean[k] for k in range(3)]
        right = [target[k] - target_mean[k] for k in range(3)]
        for row in range(3):
            for column in range(3):
                covariance[row][column] += right[row] * left[column]
                spread[row][column] += left[row] * left[column]
    inverse = _invert3(spread)
    if inverse is None:
        return None
    matrix = [[sum(covariance[row][k] * inverse[k][column] for k in range(3))
               for column in range(3)] for row in range(3)]
    determinant = (matrix[0][0] * (matrix[1][1] * matrix[2][2] - matrix[1][2] * matrix[2][1])
                   - matrix[0][1] * (matrix[1][0] * matrix[2][2] - matrix[1][2] * matrix[2][0])
                   + matrix[0][2] * (matrix[1][0] * matrix[2][1] - matrix[1][1] * matrix[2][0]))
    column_norms = [math.sqrt(sum(matrix[row][column] ** 2 for row in range(3)))
                    for column in range(3)]
    product = 1.0
    for norm in column_norms:
        product *= norm
    residuals = []
    for source, target in pairs:
        left = [source[k] - source_mean[k] for k in range(3)]
        fitted = [sum(matrix[row][k] * left[k] for k in range(3)) for row in range(3)]
        right = [target[k] - target_mean[k] for k in range(3)]
        residuals.append(math.sqrt(sum((fitted[row] - right[row]) ** 2 for row in range(3))))
    return {
        "joint_pairs": len(pairs),
        "matrix": matrix,
        "column_norms": column_norms,
        "determinant": determinant,
        "normalized_determinant": determinant / product if product > 1.0e-18 else None,
        "orientation": "preserved (rotation plus scale)" if determinant > 0
        else "reversed (mirror)" if determinant < 0 else "undetermined",
        "fit_rms_meters": math.sqrt(sum(value * value for value in residuals) / len(residuals)),
        "fit_max_meters": max(residuals),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help=".mdl, model folder, ZIP or uncompressed .gma")
    parser.add_argument("--model", help="MDL path inside the archive when several exist")
    parser.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    parser.add_argument("--skeleton-json", type=Path, required=True,
                        help="Source/HD2 skeleton comparison JSON providing the alias candidates")
    parser.add_argument("--hd2-json", type=Path, required=True, help="HD2 Unit JSON for the target skeleton")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    try:
        skeleton = json.loads(args.skeleton_json.read_text(encoding="utf-8"))
        hd2 = json.loads(args.hd2_json.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        parser.error(f"Cannot read inputs: {exc}")

    candidates = (skeleton.get("alias_candidates") or {}).get("candidates") or []
    if not candidates:
        parser.error("skeleton JSON carries no advisory alias candidates to review")

    target_matrices = {
        row["index"]: (row["matrix_4x4_raw"][12:15]
                       if isinstance(row.get("matrix_4x4_raw"), list) and len(row["matrix_4x4_raw"]) == 16
                       else None)
        for row in (hd2.get("transform_info") or {}).get("transforms") or []
        if isinstance(row, dict) and isinstance(row.get("index"), int)
    }
    names = {
        row["transform_index"]: row.get("name")
        for row in (hd2.get("transform_info") or {}).get("resolved_skeleton") or []
        if isinstance(row, dict) and isinstance(row.get("transform_index"), int)
    }
    target_by_name = {
        name: target_matrices[index]
        for index, name in names.items()
        if name and target_matrices.get(index)
    }

    try:
        model = open_model(args.input, args.model, args.backend)
        source_positions = build_world_positions([
            {"index": bone.index, "name": bone.name, "parent": bone.parent,
             "pos": [float(value) for value in bone.pos],
             "quat": [float(value) for value in bone.quat]}
            for bone in model.bones
        ])
    except (ReaderError, OSError, ValueError, TypeError, KeyError) as exc:
        parser.error(f"Rest joint comparison failed: {exc}")

    segments = paired_lengths(source_positions, target_by_name, candidates)
    if not segments:
        parser.error("no candidate pair has both endpoints mapped, so no scale can be estimated")
    ratios = sorted(row["hd2_meters_per_source_unit"] for row in segments)
    scale = statistics.median(ratios)
    residuals = hypothesis_residuals(source_positions, target_by_name, candidates, scale)
    chirality = chirality_comparison(source_positions, target_by_name)
    fitted = least_squares_map(source_positions, target_by_name, candidates)
    rows = build_rows(source_positions, target_by_name, candidates, scale, residuals)

    report = {
        "schema": "SourceModelReader-SourceHD2RestJoints1",
        "source_model": args.model or str(args.input),
        "target_unit": (hd2.get("unit") or {}).get("file_id"),
        "candidate_count": len(candidates),
        "segments": segments,
        "segment_count": len(segments),
        "scale_estimate": scale,
        "scale_min": ratios[0],
        "scale_max": ratios[-1],
        "hypotheses": residuals,
        "chirality": chirality,
        "least_squares_fit": fitted,
        "rows": rows,
        "interpretation_limits": [
            "Scale is estimated from paired bone lengths among the candidate joints themselves, so it inherits their mapping errors; it is an estimate, not a calibration.",
            "Each axis hypothesis is fitted by aligning one root pair, which absorbs translation but not rotation or mirror choices.",
            "Joint residuals cannot separate a 180 degree rotation from a single-axis mirror on a near-symmetric body, so the frame chirality comparison decides the parity of axis negations instead.",
            "Chirality compares only the sign of det(left, right, up); the volume itself scales with units and body size, and near-coplanar triples are gated out rather than counted.",
            "Residuals compare rest joint positions only. No animation, deformation or in-game behavior is measured here.",
            "Every row keeps the advisory approval from the alias layer; this script approves no mapping and applies none.",
            "Rows whose source or target joint is missing stay without rest evidence rather than being dropped from the table.",
        ],
    }
    markdown = render_markdown(report)
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Scale estimate {} HD2 m per Source unit from {} segments; hypotheses: {}".format(
        round(scale, 6), len(segments),
        ", ".join(f"{name}=mean {block['mean_residual']:.4f}m" for name, block in residuals.items())))
    if not args.output_json and not args.output_markdown:
        print(markdown)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
