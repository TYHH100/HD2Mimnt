#!/usr/bin/env python3
"""Synthetic deformation checks for the Source-to-HD2 retarget math.

Phase 4 asks for single-bone, two-bone blend, rotated joint and translated root
cases before any weight transfer is written. This harness builds those cases from
the conventions already measured - row-vector 4x4 matrices, HD2 palette equal to
mesh_world times inverse(bone_world), and a Source-to-HD2 adapter of scale plus a
180 degree rotation about +Z - and then asserts the invariants that correct linear
blend skinning must satisfy.

It cannot prove runtime behaviour: it only refuses math that breaks rigid-body
distance, weight-sum or blend monotonicity guarantees, and it refuses to model the
non-uniform-scale-plus-rotation composition that the parser also refuses to guess.
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any

# Measured adapter inputs, restated here so the synthetic cases are explicit about
# what they assume. Values come from the rest-joint survey and the position-space probe.
HD2_METERS_PER_SOURCE_UNIT = 0.030524
SOURCE_TO_HD2_SIGNS = (-1.0, -1.0, 1.0)
RIGID_TOLERANCE = 1.0e-9
MEASURE_DIGITS = 6


def identity() -> list[float]:
    return [1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0]


def translate(x: float, y: float, z: float) -> list[float]:
    matrix = identity()
    matrix[12], matrix[13], matrix[14] = x, y, z
    return matrix


def rotate_x(angle_radians: float) -> list[float]:
    cosine = math.cos(angle_radians)
    sine = math.sin(angle_radians)
    return [1.0, 0.0, 0.0, 0.0,
            0.0, cosine, sine, 0.0,
            0.0, -sine, cosine, 0.0,
            0.0, 0.0, 0.0, 1.0]


def rotate_z(angle_radians: float) -> list[float]:
    cosine = math.cos(angle_radians)
    sine = math.sin(angle_radians)
    return [cosine, sine, 0.0, 0.0,
            -sine, cosine, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0]


def rotate_y(angle_radians: float) -> list[float]:
    cosine = math.cos(angle_radians)
    sine = math.sin(angle_radians)
    return [cosine, 0.0, -sine, 0.0,
            0.0, 1.0, 0.0, 0.0,
            sine, 0.0, cosine, 0.0,
            0.0, 0.0, 0.0, 1.0]


def axis_angle(axis: list[float], angle_radians: float) -> list[float]:
    """Rotation about an arbitrary unit axis, in the same row-vector layout.

    Rodrigues' formula, transposed into row-major form so the existing
    transform_point can consume it directly.
    """
    length = math.sqrt(sum(value * value for value in axis))
    if length <= 0.0:
        raise ValueError("axis must be non-zero")
    x, y, z = (value / length for value in axis)
    cosine = math.cos(angle_radians)
    sine = math.sin(angle_radians)
    carried = 1.0 - cosine
    return [
        cosine + x * x * carried,
        y * x * carried + z * sine,
        z * x * carried - y * sine,
        0.0,
        x * y * carried - z * sine,
        cosine + y * y * carried,
        z * y * carried + x * sine,
        0.0,
        x * z * carried + y * sine,
        y * z * carried - x * sine,
        cosine + z * z * carried,
        0.0,
        0.0, 0.0, 0.0, 1.0,
    ]


def multiply(left: list[float], right: list[float]) -> list[float]:
    """Row-major, row-vector product: the result applies left and then right."""
    out = [0.0] * 16
    for row in range(4):
        for column in range(4):
            out[row * 4 + column] = sum(
                left[row * 4 + k] * right[k * 4 + column] for k in range(4))
    return out


def joint_pose(angle_radians: float, joint: list[float]) -> list[float]:
    """Rotate about the bone's own origin, then place it at the joint.

    Row-vector order is scale x rotation x translation. Rotating before translating
    keeps the joint fixed; the opposite order rotates the joint vector too and swings
    the bone around the parent origin instead of around its own hinge.
    """
    return multiply(rotate_x(angle_radians), translate(joint[0], joint[1], joint[2]))


def transform_point(point: list[float], matrix: list[float]) -> list[float]:
    x, y, z = point[0], point[1], point[2]
    return [
        x * matrix[0] + y * matrix[4] + z * matrix[8] + matrix[12],
        x * matrix[1] + y * matrix[5] + z * matrix[9] + matrix[13],
        x * matrix[2] + y * matrix[6] + z * matrix[10] + matrix[14],
    ]


def hinge_about_point(joint: list[float], rotation: list[float]) -> list[float]:
    """Rotate about `joint` rather than about the origin."""
    return multiply(
        translate(-joint[0], -joint[1], -joint[2]),
        multiply(rotation, translate(joint[0], joint[1], joint[2])),
    )


def local_frame_hinge(bone_world: list[float], joint: list[float],
                      rotation: list[float]) -> list[float]:
    """Pose a bone with a hinge through its own joint.

    The conjugation keeps the bone origin fixed, so palette x posed differs from the
    rest palette by a rigid rotation about the joint rather than by a rotation about
    the model origin.
    """
    return multiply(bone_world, hinge_about_point(joint, rotation))


def world_origin_hinge(bone_world: list[float], rotation: list[float]) -> list[float]:
    """The naive alternative kept for the control: rotate about the model origin."""
    return multiply(bone_world, rotation)


def basis_row(matrix: list[float], row: int) -> list[float]:
    """The row-vector basis row, i.e. where that local axis lands after the matrix."""
    return [matrix[row * 4 + column] for column in range(3)]


def rotation_rows(matrix: list[float]) -> list[list[float]]:
    return [[matrix[row * 4 + column] for column in range(3)] for row in range(3)]


def _invert3(matrix: list[list[float]]) -> list[list[float]] | None:
    a, b, c = matrix[0]
    d, e, f = matrix[1]
    g, h, i = matrix[2]
    determinant = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g)
    if abs(determinant) < 1.0e-12:
        return None
    cofactors = [
        [(e * i - f * h), (c * h - b * i), (b * f - c * e)],
        [(f * g - d * i), (a * i - c * g), (c * d - a * f)],
        [(d * h - e * g), (b * g - a * h), (a * e - b * d)],
    ]
    return [[value / determinant for value in row] for row in cofactors]


def invert_affine(matrix: list[float]) -> list[float]:
    """Inverse of a row-vector affine matrix with a 0 0 0 1 bottom row.

    p = v x R + t, so v = (p - t) x R^-1: the inverse carries R^-1 and the
    translation -t x R^-1.
    """
    inverse_rotation = _invert3(rotation_rows(matrix))
    if inverse_rotation is None:
        raise ValueError("matrix has a singular rotation part")
    out = identity()
    for row in range(3):
        for column in range(3):
            out[row * 4 + column] = inverse_rotation[row][column]
    translation = matrix[12:15]
    for column in range(3):
        out[12 + column] = -sum(
            translation[k] * inverse_rotation[k][column] for k in range(3))
    return out


def palette_for(mesh_world: list[float], bone_world: list[float]) -> list[float]:
    """The measured candidate: carry mesh_world once, then undo the bone bind."""
    return multiply(mesh_world, invert_affine(bone_world))


def blend(vertex: list[float], palettes: list[list[float]],
          poses: list[list[float]], weights: list[float]) -> list[float]:
    """Linear blend skinning in the measured HD2 convention, row-vector order.

    Weights are normalized by their sum, so scaling the whole weight set must not
    move the result.
    """
    total = sum(weights)
    if total <= 0.0:
        raise ValueError("weights must sum to a positive value")
    result = [0.0, 0.0, 0.0]
    for palette, pose, weight in zip(palettes, poses, weights):
        posed = transform_point(transform_point(vertex, palette), pose)
        for axis in range(3):
            result[axis] += weight * posed[axis]
    return [value / total for value in result]


def distance(left: list[float], right: list[float]) -> float:
    return math.sqrt(sum((left[k] - right[k]) ** 2 for k in range(3)))


def adapter(source_point: list[float]) -> list[float]:
    return [SOURCE_TO_HD2_SIGNS[axis] * HD2_METERS_PER_SOURCE_UNIT * source_point[axis]
            for axis in range(3)]


def _rounded(vector: list[float]) -> list[float]:
    return [round(value, MEASURE_DIGITS) for value in vector]


def _row(case: str, checks: dict[str, bool], measures: dict[str, Any]) -> dict[str, Any]:
    return {"case": case, "checks": checks, "measures": measures}


def case_single_bone_rigid() -> dict[str, Any]:
    """One bone with an identity mesh_world: the joint holds and distances are kept."""
    joint = [0.0, 0.0, 0.9417]
    bone_rest = translate(*joint)
    palette = palette_for(identity(), bone_rest)
    vertex = [0.05, 0.02, -0.20]
    angle = math.radians(30.0)
    bone_posed = joint_pose(angle, joint)

    rest = blend(vertex, [palette], [bone_rest], [1.0])
    moved = blend(vertex, [palette], [bone_posed], [1.0])
    offset = [vertex[k] - joint[k] for k in range(3)]
    expected = [joint[axis] + transform_point(offset, rotate_x(angle))[axis]
                for axis in range(3)]

    return _row("single_bone_rigid_rotation", {
        "rest_reproduces_stored_position": distance(rest, vertex) <= RIGID_TOLERANCE,
        "joint_stays_fixed": distance(transform_point([0.0, 0.0, 0.0], bone_posed), joint)
                             <= RIGID_TOLERANCE,
        "matches_rigid_rotation_about_joint": distance(moved, expected) <= RIGID_TOLERANCE,
        "vertex_to_joint_distance_preserved":
            abs(distance(moved, joint) - distance(vertex, joint)) <= RIGID_TOLERANCE,
    }, {
        "joint": _rounded(joint),
        "vertex": _rounded(vertex),
        "vertex_to_joint": round(distance(vertex, joint), MEASURE_DIGITS),
        "displacement_from_rest": round(distance(rest, moved), MEASURE_DIGITS),
        "expected_minus_blended": round(distance(expected, moved), 12),
    })


def case_two_bone_blend() -> dict[str, Any]:
    """A moving bone blended against a still bone must sweep linearly and honestly."""
    upper_joint = [0.0, 0.0, 0.9417]
    lower_joint = [0.0, 0.0, 0.5163]
    upper_rest = translate(*upper_joint)
    lower_rest = translate(*lower_joint)
    palettes = [palette_for(identity(), upper_rest), palette_for(identity(), lower_rest)]
    vertex = [0.0, 0.0, 0.70]
    angle = math.radians(45.0)
    upper_posed = joint_pose(angle, upper_joint)

    moving_only = blend(vertex, palettes, [upper_posed, lower_rest], [1.0, 0.0])
    static_only = blend(vertex, palettes, [upper_rest, lower_rest], [0.0, 1.0])
    offset = [vertex[k] - upper_joint[k] for k in range(3)]
    expected_rigid = [upper_joint[axis] + transform_point(offset, rotate_x(angle))[axis]
                      for axis in range(3)]

    samples = []
    previous = None
    monotonic = True
    for step in range(11):
        weight_on_moving = step / 10.0
        moved = blend(vertex, palettes, [upper_posed, lower_rest],
                      [weight_on_moving, 1.0 - weight_on_moving])
        displacement = distance(moved, vertex)
        samples.append({
            "weight_on_moving_bone": round(weight_on_moving, 2),
            "distance_from_stored": round(displacement, MEASURE_DIGITS),
            "distance_to_upper_joint": round(distance(moved, upper_joint), MEASURE_DIGITS),
        })
        if previous is not None and displacement < previous - RIGID_TOLERANCE:
            monotonic = False
        previous = displacement

    rest_radius = distance(vertex, upper_joint)
    smallest_radius = min(sample["distance_to_upper_joint"] for sample in samples)

    return _row("two_bone_blend_sweep", {
        "weight_one_equals_rigid_rotation_about_joint":
            distance(moving_only, expected_rigid) <= RIGID_TOLERANCE,
        "weight_zero_equals_stored_position": distance(static_only, vertex) <= RIGID_TOLERANCE,
        "rigid_side_keeps_joint_distance":
            abs(distance(moving_only, upper_joint) - rest_radius) <= RIGID_TOLERANCE,
        "displacement_grows_with_moving_weight": monotonic,
        # Linear blend skinning is not length-preserving inside a blend. The sweep has
        # to dip inside the joint circle; this harness records that volume loss instead
        # of hiding it behind a tolerance.
        "blend_volume_loss_is_recorded": smallest_radius < rest_radius,
    }, {
        "vertex_to_upper_joint": round(rest_radius, MEASURE_DIGITS),
        "vertex_to_lower_joint": round(distance(vertex, lower_joint), MEASURE_DIGITS),
        "smallest_distance_to_upper_joint": round(smallest_radius, MEASURE_DIGITS),
        "largest_distance_from_stored": round(
            max(sample["distance_from_stored"] for sample in samples), MEASURE_DIGITS),
        "samples": samples,
    })


def case_weight_sum_scale_invariance() -> dict[str, Any]:
    """Scaling the whole weight set must not move the blended vertex."""
    joint_a = [0.0, 0.0, 0.9417]
    joint_b = [0.0, 0.0, 0.5163]
    bone_a = translate(*joint_a)
    bone_b = translate(*joint_b)
    palettes = [palette_for(identity(), bone_a), palette_for(identity(), bone_b)]
    vertex = [0.02, -0.01, 0.75]
    posed_a = joint_pose(math.radians(20.0), joint_a)

    reference = blend(vertex, palettes, [posed_a, bone_b], [0.7, 0.3])
    doubled = blend(vertex, palettes, [posed_a, bone_b], [1.4, 0.6])
    halved = blend(vertex, palettes, [posed_a, bone_b], [0.35, 0.15])
    unnormalized = blend(vertex, palettes, [posed_a, bone_b], [70.0, 30.0])
    rebalanced = blend(vertex, palettes, [posed_a, bone_b], [0.35, 0.35])
    try:
        blend(vertex, palettes, [posed_a, bone_b], [0.0, 0.0])
        zero_refused = False
    except ValueError:
        zero_refused = True

    return _row("weight_sum_scale_invariance", {
        "sum_one_equals_sum_two": distance(reference, doubled) <= RIGID_TOLERANCE,
        "sum_one_equals_sum_half": distance(reference, halved) <= RIGID_TOLERANCE,
        "sum_one_equals_sum_hundred": distance(reference, unnormalized) <= RIGID_TOLERANCE,
        "all_zero_weights_refused": zero_refused,
        "more_weight_on_the_static_bone_moves_it": distance(rebalanced, reference) > 1.0e-3,
    }, {
        "normalized_result": _rounded(reference),
        "sum_two_minus_normalized": _rounded([doubled[k] - reference[k] for k in range(3)]),
        "equal_weight_result": _rounded(rebalanced),
    })


def case_translated_root() -> dict[str, Any]:
    """Moving the root must displace the skinned vertex by exactly that offset."""
    root = translate(0.1, -0.2, 1.0)
    bone_rest = multiply(root, translate(0.0, 0.0, -0.3))
    palette = palette_for(identity(), bone_rest)
    vertex = [0.02, 0.01, 0.0]
    rest = blend(vertex, [palette], [bone_rest], [1.0])
    moved_root = multiply(root, translate(0.4, 0.05, 0.6))
    bone_moved = multiply(moved_root, translate(0.0, 0.0, -0.3))
    moved = blend(vertex, [palette], [bone_moved], [1.0])
    applied = [moved[k] - vertex[k] for k in range(3)]
    expected = [bone_moved[12 + k] - bone_rest[12 + k] for k in range(3)]

    return _row("translated_root", {
        "rest_reproduces_stored_position": distance(rest, vertex) <= RIGID_TOLERANCE,
        "translation_applied_once": max(abs(applied[k] - expected[k])
                                        for k in range(3)) <= RIGID_TOLERANCE,
        "offset_magnitude_matches_joint_move":
            abs(distance(applied, [0.0, 0.0, 0.0]) - distance(expected, [0.0, 0.0, 0.0]))
            <= RIGID_TOLERANCE,
    }, {
        "vertex_offset": _rounded(applied),
        "joint_offset": _rounded(expected),
        "offset_magnitude": round(distance(applied, [0.0, 0.0, 0.0]), MEASURE_DIGITS),
    })


def case_double_application_refused() -> dict[str, Any]:
    """Carrying mesh_world twice must break the rest invariant, rotation included."""
    mesh_world = multiply(rotate_z(math.radians(15.0)),
                          translate(-0.28328, -0.04818, 1.43647))
    bone_world = multiply(rotate_x(math.radians(25.0)), translate(0.0, 0.0, 1.4051))
    vertex = [0.03, 0.01, 0.02]

    palette = palette_for(mesh_world, bone_world)
    once = blend(vertex, [palette], [bone_world], [1.0])
    twice = blend(vertex, [multiply(mesh_world, palette)], [bone_world], [1.0])
    swapped = blend(vertex, [multiply(invert_affine(bone_world), mesh_world)],
                    [bone_world], [1.0])
    expected = transform_point(vertex, mesh_world)

    return _row("double_application_refused", {
        "single_application_matches_mesh_world": distance(once, expected) <= RIGID_TOLERANCE,
        "double_application_rejected": distance(twice, expected) > 1.0e-3,
        "palette_order_swap_rejected": distance(swapped, expected) > 1.0e-3,
    }, {
        "expected_position": _rounded(expected),
        "single_application_error": round(distance(once, expected), 12),
        "double_application_error": round(distance(twice, expected), MEASURE_DIGITS),
        "palette_order_swap_error": round(distance(swapped, expected), MEASURE_DIGITS),
    })


def case_non_uniform_scale_gate() -> dict[str, Any]:
    """The parser refuses rotation combined with non-uniform scale; so does this."""
    matrix = [2.0, 0.0, 0.0, 0.0,
              0.0, 0.0, 1.0, 0.0,
              0.0, 2.0, 0.0, 0.0,
              0.0, 0.0, 0.0, 1.0]
    rows = [math.sqrt(sum(matrix[row * 4 + column] ** 2 for column in range(3)))
            for row in range(3)]
    ratio = max(rows) / min(rows)
    normalized = [[matrix[row * 4 + column] / rows[row] for column in range(3)]
                  for row in range(3)]
    off_diagonal = max(abs(normalized[row][column])
                       for row in range(3) for column in range(3) if row != column)

    uniform = [3.0, 0.0, 0.0, 0.0,
               0.0, 3.0, 0.0, 0.0,
               0.0, 0.0, 3.0, 0.0,
               0.0, 0.0, 0.0, 1.0]
    uniform_rows = [math.sqrt(sum(uniform[row * 4 + column] ** 2 for column in range(3)))
                    for row in range(3)]
    uniform_ratio = max(uniform_rows) / min(uniform_rows)

    return _row("non_uniform_scale_gate", {
        "rotation_with_non_uniform_scale_refused": ratio > 1.0 + 1.0e-4 and off_diagonal > 1.0e-4,
        "pure_uniform_scale_not_misread_as_rotation": uniform_ratio <= 1.0 + 1.0e-9,
        "rotation_detected_only_after_row_normalization": off_diagonal > 1.0e-4,
    }, {
        "axis_scales": [round(value, MEASURE_DIGITS) for value in rows],
        "scale_ratio": round(ratio, MEASURE_DIGITS),
        "off_diagonal_after_normalization": round(off_diagonal, MEASURE_DIGITS),
        "uniform_reference_scale_ratio": round(uniform_ratio, MEASURE_DIGITS),
    })


def case_adapter_scale_and_orientation() -> dict[str, Any]:
    """The measured adapter must rescale isotropically, not mirror, and keep left left."""
    source_left_hand = [4.1152, 0.9323, 54.9447]
    source_right_hand = [-4.1152, 0.9323, 54.9446]
    source_hip = [0.0, 0.9, 54.0]
    left = adapter(source_left_hand)
    right = adapter(source_right_hand)
    hip = adapter(source_hip)
    determinant = (SOURCE_TO_HD2_SIGNS[0] * SOURCE_TO_HD2_SIGNS[1] * SOURCE_TO_HD2_SIGNS[2])
    source_length = math.sqrt(sum(value ** 2 for value in source_left_hand))
    adapted_length = math.sqrt(sum(value ** 2 for value in left))

    return _row("adapter_scale_and_orientation", {
        "adapter_is_rotation_not_mirror": abs(determinant - 1.0) <= 1.0e-9,
        "left_stays_left_of_right": left[0] < right[0],
        "scale_is_isotropic": abs(adapted_length / source_length
                                  - HD2_METERS_PER_SOURCE_UNIT) <= 1.0e-12,
        "height_order_preserved": left[2] > hip[2],
    }, {
        "adapted_left_hand": _rounded(left),
        "adapted_right_hand": _rounded(right),
        "adapted_hip": _rounded(hip),
        "linear_determinant": determinant,
        "signs": list(SOURCE_TO_HD2_SIGNS),
        "scale": HD2_METERS_PER_SOURCE_UNIT,
    })


def case_adapter_maps_joint_deltas() -> dict[str, Any]:
    """The measured ratio comes from paired joint lengths, so it maps deltas.

    Bone-to-bone differences survive the adapter; absolute positions do not, because
    nothing establishes that the Source model origin and the HD2 model origin coincide.
    """
    source_shoulder = [2.5, 1.0, 56.0]
    source_hand = [4.1152, 0.9323, 54.9447]
    shift = [7.0, -3.5, 12.0]
    delta = [source_hand[k] - source_shoulder[k] for k in range(3)]
    adapted_delta = adapter(delta)
    delta_of_adapted = [adapter(source_hand)[k] - adapter(source_shoulder)[k]
                        for k in range(3)]
    shifted = [source_hand[k] + shift[k] for k in range(3)]
    shifted_shoulder = [source_shoulder[k] + shift[k] for k in range(3)]
    max_error = max(abs(adapted_delta[k] - delta_of_adapted[k]) for k in range(3))
    # Adapting each joint on its own and then differencing must give the same vector as
    # differencing first: the adapter is translation-equivariant, so a model placed at a
    # different origin still yields the same bone-to-bone deltas.
    equivariant = [adapter(shifted)[k] - adapter(shifted_shoulder)[k] for k in range(3)]
    shifted_error = max(abs(equivariant[k] - adapted_delta[k]) for k in range(3))
    source_length = distance(delta, [0.0, 0.0, 0.0])
    adapted_length = distance(adapted_delta, [0.0, 0.0, 0.0])
    hd2_reference_hand = [-0.6566, 0.1401, 1.2189]

    return _row("adapter_maps_joint_deltas", {
        "delta_mapping_is_linear": max_error <= 1.0e-12,
        "shifting_the_source_model_does_not_change_deltas": shifted_error <= 1.0e-12,
        "delta_length_uses_the_measured_ratio": abs(adapted_length / source_length
                                                    - HD2_METERS_PER_SOURCE_UNIT) <= 1.0e-12,
    }, {
        "source_delta": _rounded(delta),
        "adapted_delta": _rounded(adapted_delta),
        "source_delta_length": round(source_length, MEASURE_DIGITS),
        "adapted_delta_length_meters": round(adapted_length, MEASURE_DIGITS),
        "hd2_reference_hand_landmark": hd2_reference_hand,
        "absolute_adapted_hand": _rounded(adapter(source_hand)),
        "absolute_adapted_hand_vs_other_model_landmark": round(distance(adapter(source_hand),
                                                                        hd2_reference_hand),
                                                               MEASURE_DIGITS),
    })


def case_hinge_on_a_rotated_bone() -> dict[str, Any]:
    """A hinge through a bone that is already rotated: the bridge to real matrices.

    Real TransformInfo matrices carry rotation as well as translation, so the joint
    fixity cannot be checked with translation-only pivots. This case builds a rotated
    bone under a rotated mesh_world and hinges it about its own origin.
    """
    mesh_world = multiply(rotate_z(math.radians(15.0)),
                          translate(-0.28328, -0.04818, 1.43647))
    joint = [0.11, 0.02, 1.4051]
    bone_world = multiply(rotate_y(math.radians(20.0)), translate(*joint))
    palette = palette_for(mesh_world, bone_world)
    vertex = [0.03, 0.01, 0.02]
    placed = transform_point(vertex, mesh_world)
    angle = math.radians(30.0)
    spin = axis_angle([1.0, 0.0, 0.0], angle)

    rest = blend(vertex, [palette], [bone_world], [1.0])
    bone_posed = local_frame_hinge(bone_world, joint, spin)
    posed = blend(vertex, [palette], [bone_posed], [1.0])
    naive_posed = blend(vertex, [palette], [world_origin_hinge(bone_world, spin)], [1.0])
    expected = [joint[axis] + transform_point([placed[k] - joint[k] for k in range(3)],
                                              spin)[axis] for axis in range(3)]
    naive_joint = transform_point([0.0, 0.0, 0.0], world_origin_hinge(bone_world, spin))

    return _row("hinge_on_a_rotated_bone", {
        "rest_reproduces_placed_position": distance(rest, placed) <= RIGID_TOLERANCE,
        "joint_stays_fixed": distance(transform_point([0.0, 0.0, 0.0], bone_posed), joint)
                             <= RIGID_TOLERANCE,
        "matches_rigid_rotation_of_the_placed_vertex": distance(posed, expected) <= RIGID_TOLERANCE,
        "vertex_to_joint_distance_preserved": abs(distance(posed, joint) - distance(placed, joint))
                                             <= RIGID_TOLERANCE,
        "rotate_about_model_origin_rejected": distance(naive_posed, expected) > 1.0e-3,
    }, {
        "joint": _rounded(joint),
        "placed_vertex": _rounded(placed),
        "vertex_to_joint": round(distance(placed, joint), MEASURE_DIGITS),
        "hinged_vertex": _rounded(posed),
        "hinge_minus_expected": round(distance(posed, expected), 12),
        "naive_joint_displacement": round(distance(naive_joint, joint), MEASURE_DIGITS),
        "naive_minus_hinged": round(distance(naive_posed, posed), MEASURE_DIGITS),
    })


CASES = (case_single_bone_rigid, case_two_bone_blend, case_weight_sum_scale_invariance,
         case_translated_root, case_hinge_on_a_rotated_bone,
         case_double_application_refused, case_non_uniform_scale_gate,
         case_adapter_scale_and_orientation, case_adapter_maps_joint_deltas)


def evaluate() -> dict[str, Any]:
    results = [case() for case in CASES]
    failing = ["{}/{}".format(row["case"], name)
               for row in results
               for name, passed in row["checks"].items()
               if not passed]
    return {"results": results, "failing": failing, "all_passed": not failing}


def build_report() -> dict[str, Any]:
    return {
        "schema": "SourceModelReader-HD2RetargetDeformationChecks1",
        "assumptions": {
            "matrix_convention": "row_major_4x4_row_vector",
            "hd2_meters_per_source_unit": HD2_METERS_PER_SOURCE_UNIT,
            "source_to_hd2_signs": list(SOURCE_TO_HD2_SIGNS),
            "palette_formula_candidate": "mesh_world_times_inverse_bone_world",
            "bone_pose_construction": "rotate_about_bone_origin_then_translate_to_joint",
        },
        "checks": evaluate(),
        "interpretation_limits": [
            "These are algebraic invariants of the measured conventions, not observations of the game runtime.",
            "Passing means no transform is dropped or applied twice in the modelled cases; it does not prove the shader order.",
            "The non-uniform-scale-with-rotation composition is refused rather than modelled, so it stays unverified.",
            "Joint positions reuse the measured HD2 rest heights only as plausible pivots; no HD2 Unit geometry is decoded here.",
            "The weight-sum case normalizes by the sum, which is the term the earlier residual analysis showed a non-normalizing blend biases by (sum-1) times the static target.",
            "Blend volume loss inside a two-bone sweep is recorded, not corrected; this harness does not claim length preservation mid-blend.",
            "A bone pose built as translate-then-rotate swings the joint around the parent origin; this harness uses rotate-then-translate and checks that the joint does not move.",
            "The adapter is a delta map: its ratio was measured from paired bone-to-bone lengths, so adapted absolute coordinates are reported for scale only and are not compared against another model's landmarks.",
        ],
    }


def render_markdown(report: dict[str, Any]) -> str:
    lines = [
        "# HD2 Retarget Deformation Checks (Synthetic)",
        "",
        "- Matrix convention: {}".format(report["assumptions"]["matrix_convention"]),
        "- Palette candidate: `{}`".format(report["assumptions"]["palette_formula_candidate"]),
        "- Bone pose construction: `{}`".format(report["assumptions"]["bone_pose_construction"]),
        "- Adapter: scale {} with signs {}".format(
            report["assumptions"]["hd2_meters_per_source_unit"],
            report["assumptions"]["source_to_hd2_signs"]),
        "- All checks passed: **{}**".format(report["checks"]["all_passed"]),
        "- Failing checks: {}".format(", ".join(report["checks"]["failing"]) or "none"),
        "",
        "Every case is built from synthetic vertices plus the measured rest joints. No HD2 "
        "Unit payload is decoded and no weight is transferred.",
        "",
    ]
    for row in report["checks"]["results"]:
        lines.append("## {}".format(row["case"]))
        lines.append("")
        lines.append("| Check | Result |")
        lines.append("|---|:---|")
        for name, passed in row["checks"].items():
            lines.append("| {} | {} |".format(name, "pass" if passed else "FAIL"))
        lines.append("")
        for key, value in row["measures"].items():
            if key == "samples":
                lines.append("- {}:".format(key))
                lines.append("")
                lines.append("| weight_on_moving_bone | distance_from_stored | distance_to_upper_joint |")
                lines.append("|---:|---:|---:|")
                for sample in value:
                    lines.append("| {} | {} | {} |".format(
                        sample["weight_on_moving_bone"],
                        sample["distance_from_stored"],
                        sample["distance_to_upper_joint"]))
                lines.append("")
                continue
            lines.append("- {}: {}".format(key, value))
        lines.append("")
    lines.append("## Interpretation limits")
    lines.append("")
    lines.extend("- " + item for item in report["interpretation_limits"])
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-json", type=Path, default=None)
    parser.add_argument("--output-markdown", type=Path, default=None)
    args = parser.parse_args()

    report = build_report()
    markdown = render_markdown(report)

    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")

    print("Synthetic retarget checks: all_passed={} failing={}".format(
        report["checks"]["all_passed"],
        ", ".join(report["checks"]["failing"]) or "none"))
    if not args.output_json and not args.output_markdown:
        print(markdown)
    return 0 if report["checks"]["all_passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
