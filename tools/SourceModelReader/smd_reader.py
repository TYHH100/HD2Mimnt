"""Text SMD parser and skeleton consistency diagnostics for Source 1.

This module intentionally treats a SMD's nodes/skeleton sections as the
skeleton definition and its triangles section as geometry data. A model slice
may carry a complete node/skeleton table even when only a subset of nodes is
referenced by its vertex weights.
"""
from __future__ import annotations

import math
import re
import shlex
from dataclasses import dataclass, field
from typing import Optional


class SMDParseError(ValueError):
    """Raised when an SMD text file cannot be parsed safely."""


@dataclass(frozen=True)
class SMDNode:
    node_id: int
    name: str
    parent_id: int


@dataclass(frozen=True)
class SMDTransform:
    position: tuple[float, float, float]
    rotation: tuple[float, float, float]


@dataclass
class SMDDocument:
    source: str
    version: Optional[int]
    nodes: list[SMDNode]
    frames: dict[int, dict[int, SMDTransform]]
    triangle_count: Optional[int]
    triangle_vertex_records: int
    materials: list[str]
    warnings: list[str] = field(default_factory=list)
    infos: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)

    @property
    def node_by_id(self) -> dict[int, SMDNode]:
        return {node.node_id: node for node in self.nodes}

    @property
    def node_by_name(self) -> dict[str, SMDNode]:
        return {node.name: node for node in self.nodes}

    @property
    def parent_names(self) -> dict[int, Optional[str]]:
        by_id = self.node_by_id
        return {node.node_id: (by_id[node.parent_id].name if node.parent_id in by_id else None)
                for node in self.nodes}

    @property
    def root_count(self) -> int:
        return sum(1 for node in self.nodes if node.parent_id == -1)

    @property
    def leaf_count(self) -> int:
        parents = {node.parent_id for node in self.nodes if node.parent_id >= 0}
        return sum(1 for node in self.nodes if node.node_id not in parents)

    @property
    def smd_type(self) -> str:
        return classify_smd_type(self.source, self.triangle_vertex_records, self.frames)

    @property
    def pose_comparable(self) -> bool:
        # Only ordinary model/reference slices are compared to a bind/reference pose.
        # Physics and animation SMDs have different semantic purposes.
        return self.smd_type == "model_slice"

    @property
    def reference_pose_time(self) -> Optional[int]:
        if not self.frames:
            return None
        return 0 if 0 in self.frames else min(self.frames)

    @property
    def reference_pose(self) -> dict[int, SMDTransform]:
        time = self.reference_pose_time
        return self.frames.get(time, {}) if time is not None else {}

    @property
    def frame_coverage(self) -> list[dict]:
        """Return explicit transform coverage for each authored frame.

        Missing transforms are deliberately not synthesized or inherited here.
        Their runtime interpretation depends on the animation/QC pipeline.
        """
        known_ids = set(self.node_by_id)
        total = len(known_ids)
        output = []
        for frame_time, transforms in sorted(self.frames.items()):
            explicit = len(set(transforms) & known_ids)
            omitted = max(0, total - explicit)
            output.append({
                "time": frame_time,
                "declared_node_count": total,
                "explicit_transform_count": explicit,
                "omitted_transform_count": omitted,
                "unknown_transform_count": len(set(transforms) - known_ids),
                "coverage_ratio": (explicit / total) if total else 0.0,
                "coverage_percent": (100.0 * explicit / total) if total else 0.0,
                "is_sparse": omitted > 0,
            })
        return output


def classify_smd_type(source: str, triangle_vertex_records: int = 0,
                      frames: Optional[dict[int, dict[int, SMDTransform]]] = None) -> str:
    """Classify common SMD roles conservatively using path/name and section shape.

    Classification is a hint for selecting valid pose comparisons, not a claim
    about the compiler's original QC intent. Unknown files remain unknown.
    """
    normalized = source.replace("\\", "/").casefold()
    parts = [part for part in normalized.split("/") if part]
    basename = parts[-1] if parts else normalized
    frame_count = len(frames or {})
    in_anim_dir = any(part in {"anim", "anims", "animation", "animations"} or
                      part.endswith("_anims") or part.endswith("_animations")
                      for part in parts[:-1])

    if "physics" in basename or "physics" in normalized or "_phys" in basename or basename.startswith("phys"):
        return "physics_geometry"
    if "proportions_corrective" in basename or ("corrective" in basename and "anim" in basename):
        return "corrective_animation"
    if "proportion" in basename:
        return "proportion_animation"
    if basename == "reference.smd" and in_anim_dir:
        return "animation_reference"
    if "ragdoll" in basename:
        return "ragdoll_pose"
    if in_anim_dir or "idle" in basename or "animation" in basename or basename.startswith("a_"):
        return "animation"
    if triangle_vertex_records > 0:
        return "model_slice"
    if frame_count > 1:
        return "animation"
    return "unknown"


SMD_TYPE_LABELS = {
    "model_slice": "model/reference geometry slice",
    "physics_geometry": "physics geometry",
    "animation": "animation",
    "proportion_animation": "proportion adjustment",
    "corrective_animation": "corrective animation",
    "animation_reference": "animation reference",
    "ragdoll_pose": "ragdoll pose",
    "unknown": "unknown / unclassified",
}


def _decode_smd(data: bytes) -> str:
    for encoding in ("utf-8-sig", "gbk"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            pass
    return data.decode("cp1252", "replace")


def _parse_int(token: str, label: str, line_no: int) -> int:
    try:
        return int(token, 10)
    except ValueError as exc:
        raise SMDParseError(f"line {line_no}: invalid {label} {token!r}") from exc


def _parse_float(token: str, label: str, line_no: int) -> float:
    try:
        value = float(token)
    except ValueError as exc:
        raise SMDParseError(f"line {line_no}: invalid {label} {token!r}") from exc
    if not math.isfinite(value):
        raise SMDParseError(f"line {line_no}: non-finite {label} {token!r}")
    return value


def _looks_like_vertex(tokens: list[str]) -> bool:
    if len(tokens) < 9:
        return False
    try:
        int(tokens[0], 10)
        for token in tokens[1:9]:
            float(token)
        return True
    except ValueError:
        return False


def parse_smd_bytes(data: bytes, source: str = "<memory>") -> SMDDocument:
    """Parse the nodes and skeleton sections, and lightly validate triangle rows."""
    if not data:
        raise SMDParseError(f"{source}: file is empty")
    text = _decode_smd(data)
    if "\x00" in text:
        raise SMDParseError(f"{source}: binary NUL bytes found; input does not look like a text SMD")

    version: Optional[int] = None
    section: Optional[str] = None
    nodes: list[SMDNode] = []
    frames: dict[int, dict[int, SMDTransform]] = {}
    current_time = 0
    triangle_vertex_records = 0
    materials: list[str] = []
    warnings: list[str] = []
    infos: list[str] = []
    known_sections = {"nodes", "skeleton", "triangles"}
    saw_triangles_section = False

    for line_no, raw_line in enumerate(text.splitlines(), start=1):
        line = raw_line.strip()
        if not line:
            continue
        if line.lower().startswith("version "):
            parts = line.split()
            if len(parts) == 2:
                version = _parse_int(parts[1], "SMD version", line_no)
                if version != 1:
                    warnings.append(f"line {line_no}: SMD version {version} is not the commonly used version 1")
            continue
        lower = line.lower()
        if lower in known_sections:
            section = lower
            if lower == "triangles":
                saw_triangles_section = True
            continue
        if lower == "end":
            section = None
            continue

        if section == "nodes":
            try:
                parts = shlex.split(line, posix=True)
            except ValueError as exc:
                raise SMDParseError(f"line {line_no}: malformed quoted node record: {exc}") from exc
            if len(parts) != 3:
                raise SMDParseError(f"line {line_no}: node record must be: ID \"name\" parentID")
            node_id = _parse_int(parts[0], "node ID", line_no)
            parent_id = _parse_int(parts[2], "parent ID", line_no)
            if node_id < 0:
                raise SMDParseError(f"line {line_no}: node ID must be non-negative")
            if parent_id < -1:
                warnings.append(f"line {line_no}: node {node_id} has unusual negative parent ID {parent_id}")
            nodes.append(SMDNode(node_id, parts[1], parent_id))
            continue

        if section == "skeleton":
            parts = line.split()
            if parts[0].lower() == "time":
                if len(parts) != 2:
                    raise SMDParseError(f"line {line_no}: time record must contain one frame index")
                current_time = _parse_int(parts[1], "skeleton frame index", line_no)
                frames.setdefault(current_time, {})
                continue
            if len(parts) != 7:
                raise SMDParseError(f"line {line_no}: skeleton transform must contain bone ID and six values")
            bone_id = _parse_int(parts[0], "skeleton bone ID", line_no)
            values = [_parse_float(token, "skeleton transform value", line_no) for token in parts[1:]]
            frame = frames.setdefault(current_time, {})
            if bone_id in frame:
                raise SMDParseError(f"line {line_no}: duplicate transform for bone {bone_id} at time {current_time}")
            frame[bone_id] = SMDTransform(tuple(values[:3]), tuple(values[3:]))  # type: ignore[arg-type]
            continue

        if section == "triangles":
            if _looks_like_vertex(line.split()):
                triangle_vertex_records += 1
            elif line not in materials:
                materials.append(line)
            continue

    if not nodes:
        raise SMDParseError(f"{source}: no nodes section or no valid node records were found")

    id_to_node: dict[int, SMDNode] = {}
    name_to_id: dict[str, int] = {}
    for node in nodes:
        if node.node_id in id_to_node:
            raise SMDParseError(f"{source}: duplicate node ID {node.node_id}")
        if node.name in name_to_id:
            raise SMDParseError(f"{source}: duplicate node name {node.name!r}")
        id_to_node[node.node_id] = node
        name_to_id[node.name] = node.node_id

    for node in nodes:
        if node.parent_id != -1 and node.parent_id not in id_to_node:
            warnings.append(f"node {node.name!r} ({node.node_id}) references missing parent ID {node.parent_id}")

    # Detect parent cycles without relying on node IDs being contiguous.
    for node in nodes:
        seen: set[int] = set()
        current = node
        while current.parent_id in id_to_node:
            if current.node_id in seen:
                warnings.append(f"bone hierarchy cycle detected starting at {node.name!r}")
                break
            seen.add(current.node_id)
            current = id_to_node[current.parent_id]

    if not frames:
        warnings.append("no skeleton time/transform records were found")

    known_ids = set(id_to_node)
    smd_type = classify_smd_type(source, triangle_vertex_records, frames)
    sparse_frame_types = {
        "animation", "proportion_animation", "corrective_animation",
        "animation_reference", "ragdoll_pose",
    }
    for frame_time, frame in frames.items():
        unknown = sorted(set(frame) - known_ids)
        if unknown:
            warnings.append(f"frame {frame_time} contains transforms for undefined node IDs: {unknown[:16]}")
        missing = sorted(known_ids - set(frame))
        if missing:
            if not frame:
                warnings.append(f"frame {frame_time} contains no transforms for {len(known_ids)} declared node(s)")
            elif smd_type in sparse_frame_types:
                infos.append(
                    f"frame {frame_time} has sparse transforms: {len(frame) - len(unknown)} / "
                    f"{len(known_ids)} declared nodes explicitly recorded; {len(missing)} omitted"
                )
            else:
                warnings.append(f"frame {frame_time} omits {len(missing)} node transform(s)")

    remainder = triangle_vertex_records % 3
    if remainder:
        warnings.append(f"triangles section has {triangle_vertex_records} recognized vertex rows; count is not divisible by 3")
    triangle_count = triangle_vertex_records // 3
    return SMDDocument(
        source=source,
        version=version,
        nodes=nodes,
        frames=frames,
        triangle_count=triangle_count if section is not None or triangle_vertex_records else triangle_count,
        triangle_vertex_records=triangle_vertex_records,
        materials=materials,
        warnings=warnings,
        infos=infos,
    )


def _angle_delta(a: float, b: float) -> float:
    """Smallest absolute angular delta in radians."""
    return abs((a - b + math.pi) % (2.0 * math.pi) - math.pi)


def compare_smd_skeletons(reference: SMDDocument, candidate: SMDDocument,
                          tolerance: float = 1e-4,
                          compare_pose: Optional[bool] = None) -> dict:
    """Compare node structure for every SMD and pose only when semantically valid.

    By default, reference-pose comparison is enabled only when both documents
    are classified as ordinary model slices. Callers may override this policy.
    """
    ref_nodes = reference.node_by_name
    cand_nodes = candidate.node_by_name
    ref_parents = reference.parent_names
    cand_parents = candidate.parent_names
    missing = sorted(set(ref_nodes) - set(cand_nodes))
    additional = sorted(set(cand_nodes) - set(ref_nodes))
    id_remaps: list[dict] = []
    parent_mismatches: list[dict] = []
    for name in sorted(set(ref_nodes) & set(cand_nodes)):
        rn, cn = ref_nodes[name], cand_nodes[name]
        if rn.node_id != cn.node_id:
            id_remaps.append({"name": name, "reference_id": rn.node_id, "candidate_id": cn.node_id})
        rp = ref_parents.get(rn.node_id)
        cp = cand_parents.get(cn.node_id)
        if rp != cp:
            parent_mismatches.append({"name": name, "reference_parent": rp, "candidate_parent": cp})

    if compare_pose is None:
        compare_pose = reference.pose_comparable and candidate.pose_comparable
    pose_mismatches: list[dict] = []
    missing_pose: list[str] = []
    max_position_delta = 0.0
    max_rotation_delta = 0.0
    ref_pose = reference.reference_pose
    cand_pose = candidate.reference_pose
    if compare_pose:
        for name in sorted(set(ref_nodes) & set(cand_nodes)):
            ri = ref_nodes[name].node_id
            ci = cand_nodes[name].node_id
            rt = ref_pose.get(ri)
            ct = cand_pose.get(ci)
            if rt is None or ct is None:
                missing_pose.append(name)
                continue
            position_delta = max(abs(a - b) for a, b in zip(rt.position, ct.position))
            rotation_delta = max(_angle_delta(a, b) for a, b in zip(rt.rotation, ct.rotation))
            max_position_delta = max(max_position_delta, position_delta)
            max_rotation_delta = max(max_rotation_delta, rotation_delta)
            if position_delta > tolerance or rotation_delta > tolerance:
                pose_mismatches.append({
                    "name": name,
                    "max_position_component_delta": position_delta,
                    "max_rotation_component_delta_radians": rotation_delta,
                })

    findings: list[dict] = []
    if missing:
        findings.append({"severity": "warning", "code": "missing_nodes", "names": missing})
    if additional:
        findings.append({"severity": "warning", "code": "additional_nodes", "names": additional})
    if parent_mismatches:
        findings.append({"severity": "warning", "code": "parent_mismatches", "items": parent_mismatches})
    if compare_pose and pose_mismatches:
        findings.append({"severity": "warning", "code": "reference_pose_mismatches", "items": pose_mismatches})
    if compare_pose and missing_pose:
        findings.append({"severity": "warning", "code": "missing_reference_pose_transforms", "names": missing_pose})
    if id_remaps:
        findings.append({"severity": "info", "code": "node_id_remaps", "items": id_remaps})
    if not compare_pose:
        findings.append({"severity": "info", "code": "reference_pose_comparison_skipped",
                         "reason": "SMD type is not an ordinary model slice; its first skeleton frame is not assumed to be a bind/reference pose"})

    structure_warning = bool(missing or additional or parent_mismatches)
    pose_warning = bool(compare_pose and (pose_mismatches or missing_pose))
    status = "warning" if structure_warning or pose_warning else "ok"
    return {
        "reference": reference.source,
        "candidate": candidate.source,
        "reference_smd_type": reference.smd_type,
        "candidate_smd_type": candidate.smd_type,
        "status": status,
        "structure_status": "warning" if structure_warning else "ok",
        "reference_pose_status": ("warning" if pose_warning else "ok") if compare_pose else "not_compared",
        "reference_pose_comparison": "compared" if compare_pose else "skipped_incompatible_smd_type",
        "tolerance": tolerance,
        "reference_node_count": len(reference.nodes),
        "candidate_node_count": len(candidate.nodes),
        "matched_node_count": len(set(ref_nodes) & set(cand_nodes)),
        "missing_node_count": len(missing),
        "additional_node_count": len(additional),
        "node_id_remap_count": len(id_remaps),
        "parent_mismatch_count": len(parent_mismatches),
        "reference_pose_mismatch_count": len(pose_mismatches),
        "missing_reference_pose_transform_count": len(missing_pose),
        "max_position_component_delta": max_position_delta,
        "max_rotation_component_delta_radians": max_rotation_delta,
        "reference_pose_time": reference.reference_pose_time if compare_pose else None,
        "candidate_reference_pose_time": candidate.reference_pose_time if compare_pose else None,
        "findings": findings,
    }

def compare_smd_to_mdl(document: SMDDocument, mdl_bones: list, tolerance: float = 1e-4,
                        compare_pose: Optional[bool] = None) -> dict:
    """Map SMD nodes to MDL definitions; pose checks are limited to model slices."""
    mdl_by_name = {bone.name: bone for bone in mdl_bones}
    smd_by_name = document.node_by_name
    missing_in_mdl = sorted(set(smd_by_name) - set(mdl_by_name))
    missing_in_smd = sorted(set(mdl_by_name) - set(smd_by_name))
    smd_parents = document.parent_names
    id_remaps: list[dict] = []
    parent_mismatches: list[dict] = []
    pose_mismatches: list[dict] = []
    max_pos = 0.0
    max_rot = 0.0
    pose = document.reference_pose
    if compare_pose is None:
        compare_pose = document.pose_comparable
    for name in sorted(set(smd_by_name) & set(mdl_by_name)):
        sn = smd_by_name[name]
        mb = mdl_by_name[name]
        if sn.node_id != mb.index:
            id_remaps.append({"name": name, "smd_id": sn.node_id, "mdl_index": mb.index})
        smd_parent = smd_parents.get(sn.node_id)
        mdl_parent = None
        if 0 <= mb.parent < len(mdl_bones):
            mdl_parent = mdl_bones[mb.parent].name
        if smd_parent != mdl_parent:
            parent_mismatches.append({"name": name, "smd_parent": smd_parent, "mdl_parent": mdl_parent})
        transform = pose.get(sn.node_id) if compare_pose else None
        if transform is not None:
            pos_delta = max(abs(a - b) for a, b in zip(transform.position, mb.pos))
            rot_delta = max(_angle_delta(a, b) for a, b in zip(transform.rotation, mb.rot))
            max_pos = max(max_pos, pos_delta)
            max_rot = max(max_rot, rot_delta)
            if pos_delta > tolerance or rot_delta > tolerance:
                pose_mismatches.append({"name": name,
                                        "max_position_component_delta": pos_delta,
                                        "max_rotation_component_delta_radians": rot_delta})

    findings: list[dict] = []
    if missing_in_mdl:
        findings.append({"severity": "warning", "code": "smd_nodes_missing_in_mdl", "names": missing_in_mdl})
    if missing_in_smd:
        findings.append({"severity": "warning", "code": "mdl_bones_missing_in_smd", "names": missing_in_smd})
    if parent_mismatches:
        findings.append({"severity": "warning", "code": "mdl_parent_mismatches", "items": parent_mismatches})
    if compare_pose and pose_mismatches:
        findings.append({"severity": "warning", "code": "mdl_reference_pose_mismatches", "items": pose_mismatches})
    if not compare_pose:
        findings.append({"severity": "info", "code": "mdl_pose_comparison_skipped",
                         "reason": "SMD type is not an ordinary model slice; first skeleton frame is not treated as bind pose"})
    if id_remaps:
        findings.append({"severity": "info", "code": "smd_to_mdl_index_mapping", "items": id_remaps})
    return {
        "smd": document.source,
        "smd_type": document.smd_type,
        "status": "warning" if any(x["severity"] == "warning" for x in findings) else "ok",
        "structure_status": "warning" if missing_in_mdl or missing_in_smd or parent_mismatches else "ok",
        "reference_pose_status": ("warning" if pose_mismatches else "ok") if compare_pose else "not_compared",
        "reference_pose_comparison": "compared" if compare_pose else "skipped_incompatible_smd_type",
        "mdl_bone_definition_count": len(mdl_bones),
        "smd_node_count": len(document.nodes),
        "matched_name_count": len(set(smd_by_name) & set(mdl_by_name)),
        "missing_in_mdl_count": len(missing_in_mdl),
        "missing_in_smd_count": len(missing_in_smd),
        "index_remap_count": len(id_remaps),
        "parent_mismatch_count": len(parent_mismatches),
        "reference_pose_mismatch_count": len(pose_mismatches),
        "max_position_component_delta": max_pos,
        "max_rotation_component_delta_radians": max_rot,
        "findings": findings,
    }
