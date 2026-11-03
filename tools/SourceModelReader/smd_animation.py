"""SMD animation-to-glTF track preparation.

SMD skeleton transforms are treated as parent-local XYZ translations and XYZ
Euler rotations (radians). Missing records remain missing: no zero/identity
transforms are synthesized. The exporter writes one glTF track per bone using
only authored frame records.
"""
from __future__ import annotations

import math
from pathlib import Path
from typing import Any


def _normalize_quaternion(q: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    length = math.sqrt(sum(v * v for v in q))
    if not math.isfinite(length) or length < 1e-12:
        raise ValueError("SMD Euler angles produced an invalid quaternion")
    return tuple(v / length for v in q)  # type: ignore[return-value]


def _quat_mul(a: tuple[float, float, float, float],
              b: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def smd_euler_xyz_to_gltf_quaternion(rotation: tuple[float, float, float]) -> tuple[float, float, float, float]:
    """Convert SMD XYZ Euler radians to a glTF XYZW quaternion and change basis.

    Euler rotation is composed X then Y then Z. The Source-to-glTF basis maps
    vectors (x, y, z) -> (x, z, -y), i.e. a -90 degree rotation about X.
    Orientation is converted by conjugating with that basis rotation.
    """
    rx, ry, rz = rotation
    sx, sy, sz = math.sin(rx * 0.5), math.sin(ry * 0.5), math.sin(rz * 0.5)
    cx, cy, cz = math.cos(rx * 0.5), math.cos(ry * 0.5), math.cos(rz * 0.5)
    # q = qx * qy * qz (XYZ Euler composition).
    source_q = _normalize_quaternion((
        sx * cy * cz - cx * sy * sz,
        cx * sy * cz + sx * cy * sz,
        cx * cy * sz - sx * sy * cz,
        cx * cy * cz + sx * sy * sz,
    ))
    half = math.sqrt(0.5)
    basis = (-half, 0.0, 0.0, half)
    basis_inverse = (half, 0.0, 0.0, half)
    return _normalize_quaternion(_quat_mul(_quat_mul(basis, source_q), basis_inverse))


def _source_to_gltf_vec3(value: tuple[float, float, float]) -> tuple[float, float, float]:
    x, y, z = value
    return (float(x), float(z), -float(y))


def _continuous_quaternions(values: list[tuple[float, float, float, float]]) -> list[tuple[float, float, float, float]]:
    """Flip equivalent quaternion signs to avoid unnecessary long arcs."""
    result: list[tuple[float, float, float, float]] = []
    for current in values:
        q = current
        if result and sum(a * b for a, b in zip(result[-1], q)) < 0.0:
            q = tuple(-v for v in q)  # type: ignore[assignment]
        result.append(q)
    return result


def build_animation_clip(document: Any, scene_bones: list[Any], fps: float,
                         interpolation: str = "auto") -> dict:
    """Map parsed SMD animation frames to a GLB-friendly per-bone track table."""
    if not math.isfinite(fps) or fps <= 0.0 or fps > 1000.0:
        raise ValueError("FPS must be finite and between 0 (exclusive) and 1000")
    mode = interpolation.lower()
    if mode not in {"auto", "linear", "step"}:
        raise ValueError("Interpolation must be auto, linear, or step")
    if document.smd_type != "animation":
        raise ValueError(
            f"Animation export expects an SMD classified as 'animation', got {document.smd_type!r}. "
            "Proportion/corrective/ragdoll/reference files need their own semantics."
        )
    frame_indices = sorted(document.frames)
    if len(frame_indices) < 2:
        raise ValueError("Animation SMD must contain at least two distinct skeleton frame indices")
    if any(not document.frames[index] for index in frame_indices):
        empty = [index for index in frame_indices if not document.frames[index]]
        raise ValueError(f"Animation contains empty skeleton frame(s): {empty[:16]}")

    smd_nodes = document.node_by_id
    smd_name_map = document.node_by_name
    model_name_map = {bone.name: bone for bone in scene_bones}
    duplicate_model_names = len(model_name_map) != len(scene_bones)
    if duplicate_model_names:
        raise ValueError("Target model contains duplicate bone names; name-based animation mapping is ambiguous")

    missing_in_model = sorted(set(smd_name_map) - set(model_name_map))
    missing_in_animation = sorted(set(model_name_map) - set(smd_name_map))
    if missing_in_model or missing_in_animation:
        details = []
        if missing_in_model:
            details.append(f"SMD-only bones: {missing_in_model[:12]}")
        if missing_in_animation:
            details.append(f"model-only bones: {missing_in_animation[:12]}")
        raise ValueError("Animation skeleton does not match the target model by name; " + "; ".join(details))

    smd_parent_names = document.parent_names
    model_parent_names: dict[str, str | None] = {}
    for bone in scene_bones:
        parent = scene_bones[bone.parent].name if bone.parent >= 0 else None
        model_parent_names[bone.name] = parent
    parent_mismatches = [
        name for name, node in smd_name_map.items()
        if smd_parent_names.get(node.node_id) != model_parent_names.get(name)
    ]
    if parent_mismatches:
        raise ValueError(f"Animation skeleton hierarchy differs from target model at: {parent_mismatches[:16]}")

    known_ids = set(smd_nodes)
    unknown_by_frame = {
        time: sorted(set(frame) - known_ids)
        for time, frame in document.frames.items()
        if set(frame) - known_ids
    }
    if unknown_by_frame:
        raise ValueError(f"Animation has transforms for undefined SMD node IDs: {unknown_by_frame}")

    origin = frame_indices[0]
    frame_count = len(frame_indices)
    tracks: list[dict] = []
    static_tracks_elided = 0
    for node in document.nodes:
        records = [(frame_index, document.frames[frame_index][node.node_id])
                   for frame_index in frame_indices if node.node_id in document.frames[frame_index]]
        if not records:
            continue
        times = [(frame_index - origin) / fps for frame_index, _ in records]
        translations = [_source_to_gltf_vec3(record.position) for _, record in records]
        rotations = _continuous_quaternions([
            smd_euler_xyz_to_gltf_quaternion(record.rotation) for _, record in records
        ])
        is_sparse = len(records) < frame_count
        effective_interpolation = ("step" if is_sparse else "linear") if mode == "auto" else mode
        target_bone = model_name_map[node.name]
        # Omit a channel group only when every explicitly authored value equals
        # the existing GLB rest transform. This reduces redundant keys while
        # preserving constant animation offsets that differ from the rest pose.
        rest_translation = tuple(float(x) for x in target_bone.translation)
        rest_rotation = _normalize_quaternion(tuple(float(x) for x in target_bone.rotation))
        translation_is_rest = all(
            max(abs(a - b) for a, b in zip(value, rest_translation)) <= 1e-5
            for value in translations
        )
        rotation_is_rest = all(
            abs(sum(a * b for a, b in zip(value, rest_rotation))) >= 1.0 - 1e-8
            for value in rotations
        )
        if translation_is_rest and rotation_is_rest:
            static_tracks_elided += 1
            continue
        tracks.append({
            "bone_name": node.name,
            "smd_node_id": node.node_id,
            "target_bone_index": target_bone.index,
            "frame_indices": [frame_index for frame_index, _ in records],
            "times": times,
            "translations": [list(value) for value in translations],
            "rotations": [list(value) for value in rotations],
            "interpolation": effective_interpolation.upper(),
            "is_sparse": is_sparse,
            "explicit_key_count": len(records),
        })

    if not tracks:
        raise ValueError("Animation contains no transform changes or offsets relative to the target model rest pose")

    duration = max((index - origin) / fps for index in frame_indices)
    return {
        "name": Path(document.source).stem or "SourceAnimation",
        "source_smd": document.source,
        "source_smd_type": document.smd_type,
        "fps": float(fps),
        "time_origin_frame": origin,
        "source_frame_indices": frame_indices,
        "source_frame_count": frame_count,
        "duration_seconds": duration,
        "interpolation_requested": mode,
        "track_count": len(tracks),
        "animated_bone_count": len(tracks),
        "sparse_track_count": sum(1 for track in tracks if track["is_sparse"]),
        "static_tracks_elided_count": static_tracks_elided,
        "explicit_transform_count": sum(len(frame) for frame in document.frames.values()),
        "exported_transform_key_count": sum(track["explicit_key_count"] for track in tracks),
        "omitted_transform_count": sum(max(0, len(document.nodes) - len(frame)) for frame in document.frames.values()),
        "tracks": tracks,
        "transform_semantics": "Only explicitly authored SMD transforms are considered. Missing per-frame records are not synthesized. Tracks unchanged and equal to the GLB rest transform are elided. Sparse tracks default to STEP in auto mode; dense tracks default to LINEAR.",
        "coordinate_conversion": "Source Z-up (X,Y,Z) -> glTF Y-up (X,Z,-Y); local XYZ Euler radians -> XYZW quaternion",
    }
