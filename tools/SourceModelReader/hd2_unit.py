"""Experimental read-only HD2 Unit/geometry decoder for Source Model Reader 0.8.4-alpha.

The module parses Unit tables and direct or CompositeUnit GPU buffers into a
neutral JSON-friendly representation. It resolves material-scoped vertex
influences through BoneInfo palettes, validates the TransformInfo hierarchy
against stored matrices, and diagnoses actively used mesh-space skin matrices.
Raw game data remains unchanged; no retargeting or resource writing is done.
"""
from __future__ import annotations

import math
import struct
from pathlib import Path
from typing import Optional

from hd2_resources import (
    HD2ResourceError,
    TYPE_IDS,
    _read_package_file,
    _read_package_from_game_data,
    _check_range,
    parse_bones_resource,
    parse_toc_package,
    parse_unit_header,
    parse_unit_material_ids,
)

MAX_TRANSFORMS = 100_000
MAX_BONE_INFO = 4096
MAX_STREAMS = 4096
MAX_MESHES = 100_000
MAX_COMPONENTS = 16
MAX_VERTICES_PER_MESH = 5_000_000
MAX_INDICES_PER_MESH = 30_000_000

COMPONENT_TYPES = {
    0: "position", 1: "normal", 2: "tangent", 3: "bitangent",
    4: "uv", 5: "color", 6: "bone_index", 7: "bone_weight",
}

# (friendly name, byte size, struct format, value count)
# Format IDs below are the current Unit 10800438 layout. Unknown/old formats
# remain visible in the report and are not silently reinterpreted.
COMPONENT_FORMATS = {
    0: ("float32", 4, "f", 1),
    1: ("vec2_float32", 8, "2f", 2),
    2: ("vec3_float32", 12, "3f", 3),
    3: ("vec4_float32", 16, "4f", 4),
    4: ("rgba8", 4, "4B", 4),
    21: ("uint32", 4, "I", 1),
    22: ("vec2_uint32", 8, "2I", 2),
    23: ("vec3_uint32", 12, "3I", 3),
    24: ("vec4_uint32", 16, "4I", 4),
    25: ("uint8", 1, "B", 1),
    26: ("vec2_uint8", 2, "2B", 2),
    27: ("vec3_uint8", 3, "3B", 3),
    28: ("vec4_uint8", 4, "4B", 4),
    29: ("packed_10_10_10_2", 4, "I", 1),
    30: ("packed_octahedral_normal", 4, "I", 1),
    32: ("float16", 2, "e", 1),
    33: ("vec2_half", 4, "2e", 2),
    34: ("vec3_half", 6, "3e", 3),
    35: ("vec4_half", 8, "4e", 4),
}


def _u16(data: bytes, offset: int, label: str) -> int:
    _check_range(data, offset, 2, label)
    return struct.unpack_from("<H", data, offset)[0]


def _i32(data: bytes, offset: int, label: str) -> int:
    _check_range(data, offset, 4, label)
    return struct.unpack_from("<i", data, offset)[0]


def _u32(data: bytes, offset: int, label: str) -> int:
    _check_range(data, offset, 4, label)
    return struct.unpack_from("<I", data, offset)[0]


def _u64(data: bytes, offset: int, label: str) -> int:
    _check_range(data, offset, 8, label)
    return struct.unpack_from("<Q", data, offset)[0]


def decode_octahedral_normal(packed: int) -> list[float]:
    """Decode the Unit format-30 10+10-bit octahedral normal.

    The low ten bits encode X and the next ten bits encode Y. The upper
    twelve bits are not part of this normal representation and are ignored.
    """
    if isinstance(packed, bool) or not isinstance(packed, int):
        raise TypeError("Packed octahedral normal must be an integer")
    if packed < 0 or packed > 0xFFFFFFFF:
        raise ValueError("Packed octahedral normal must fit in uint32")

    qx = packed & 0x3FF
    qy = (packed >> 10) & 0x3FF
    x = qx * (2.0 / 1023.0) - 1.0
    y = qy * (2.0 / 1023.0) - 1.0
    z = 1.0 - abs(x) - abs(y)

    if z < 0.0:
        old_x, old_y = x, y
        x = (1.0 - abs(old_y)) * (1.0 if old_x >= 0.0 else -1.0)
        y = (1.0 - abs(old_x)) * (1.0 if old_y >= 0.0 else -1.0)

    length = math.sqrt(x * x + y * y + z * z)
    if length <= 1.0e-20:
        # Defensive fallback; valid quantized octahedral values should not
        # produce a zero vector, but never emit NaN if a malformed value does.
        return [0.0, 0.0, 1.0]
    return [x / length, y / length, z / length]


def decode_packed_weights_1010102(packed: int) -> list[float]:
    """Decode Format 29 when its component semantic is bone_weight.

    X/Y/Z are unsigned normalized ten-bit weights. AQ's Unit reader treats
    the top two bits as non-weight metadata and exposes the fourth influence
    as zero; callers retain the original packed uint32 separately.
    """
    if isinstance(packed, bool) or not isinstance(packed, int):
        raise TypeError("Packed 10:10:10:2 value must be an integer")
    if packed < 0 or packed > 0xFFFFFFFF:
        raise ValueError("Packed 10:10:10:2 value must fit in uint32")

    x = packed & 0x3FF
    y = (packed >> 10) & 0x3FF
    z = (packed >> 20) & 0x3FF
    return [x / 1023.0, y / 1023.0, z / 1023.0, 0.0]


def _positive_next(offset: int, candidates: list[int], total: int) -> int:
    valid = [item for item in candidates if item > offset]
    return min(valid) if valid else total


def parse_transform_info(data: bytes, offset: int, end_offset: Optional[int] = None) -> dict:
    """Decode Unit TransformInfo while preserving stored matrix/parent values."""
    if offset == 0:
        return {"count": 0, "reserved_hex": "", "transforms": [], "warnings": []}
    end = len(data) if end_offset is None else min(end_offset, len(data))
    if offset < 0 or offset + 16 > end:
        raise HD2ResourceError("TransformInfo header is outside its Unit TOC region")
    count = _u32(data, offset, "TransformInfo count")
    if count > MAX_TRANSFORMS:
        raise HD2ResourceError(f"TransformInfo count exceeds safety limit: {count}")
    total_size = 16 + count * 136
    _check_range(data[:end], offset, total_size, "TransformInfo arrays")
    reserved = data[offset + 4:offset + 16]
    local_start = offset + 16
    matrix_start = local_start + count * 64
    entry_start = matrix_start + count * 64
    hash_start = entry_start + count * 4
    transforms = []
    warnings = []
    for index in range(count):
        values = struct.unpack_from("<16f", data, local_start + index * 64)
        matrix = struct.unpack_from("<16f", data, matrix_start + index * 64)
        increment, parent = struct.unpack_from("<HH", data, entry_start + index * 4)
        name_hash = _u32(data, hash_start + index * 4, f"TransformInfo name hash[{index}]")
        if not all(math.isfinite(value) for value in values + matrix):
            warnings.append(f"Transform {index} contains non-finite transform values")
        if parent not in (0xFFFF,) and parent >= count:
            warnings.append(f"Transform {index} parent index {parent} is outside transform count {count}")
        transforms.append({
            "index": index,
            "rotation_3x3": list(values[0:9]),
            "position": list(values[9:12]),
            "scale": list(values[12:15]),
            "dummy": values[15],
            "matrix_4x4_raw": list(matrix),
            "increment": increment,
            "parent_index_raw": parent,
            "name_hash": name_hash,
        })
    return {
        "count": count,
        "reserved_hex": reserved.hex(),
        "byte_size": total_size,
        "transforms": transforms,
        "warnings": warnings,
    }


def _parse_bone_info_record(data: bytes, record_start: int, record_end: int, record_index: int) -> dict:
    _check_range(data[:record_end], record_start, 16, f"BoneInfo[{record_index}] header")
    num_bones, matrix_offset, real_offset, fake_offset = struct.unpack_from("<4I", data, record_start)
    if num_bones > MAX_TRANSFORMS:
        raise HD2ResourceError(f"BoneInfo[{record_index}] bone count exceeds safety limit: {num_bones}")
    matrices = []
    real_indices = []
    remaps = []
    warnings = []
    if num_bones:
        if matrix_offset < 16 or real_offset < 16:
            raise HD2ResourceError(f"BoneInfo[{record_index}] has an invalid matrix/RealIndices offset")
        matrix_abs = record_start + matrix_offset
        _check_range(data[:record_end], matrix_abs, num_bones * 64, f"BoneInfo[{record_index}] matrices")
        for i in range(num_bones):
            matrix = struct.unpack_from("<16f", data, matrix_abs + i * 64)
            if not all(math.isfinite(v) for v in matrix):
                warnings.append(f"BoneInfo[{record_index}] matrix {i} contains non-finite values")
            matrices.append(list(matrix))
        real_abs = record_start + real_offset
        _check_range(data[:record_end], real_abs, num_bones * 4, f"BoneInfo[{record_index}] RealIndices")
        real_indices = list(struct.unpack_from(f"<{num_bones}I", data, real_abs))
    if fake_offset:
        if fake_offset < 16:
            raise HD2ResourceError(f"BoneInfo[{record_index}] FakeIndices offset is invalid")
        fake_abs = record_start + fake_offset
        num_remaps = _u32(data[:record_end], fake_abs, f"BoneInfo[{record_index}] remap count")
        if num_remaps > 100_000:
            raise HD2ResourceError(f"BoneInfo[{record_index}] remap count exceeds safety limit")
        table_size = 4 + num_remaps * 8
        _check_range(data[:record_end], fake_abs, table_size, f"BoneInfo[{record_index}] remap offset table")
        offsets = []
        counts = []
        for i in range(num_remaps):
            rel, count = struct.unpack_from("<2I", data, fake_abs + 4 + i * 8)
            if count > MAX_TRANSFORMS:
                raise HD2ResourceError(f"BoneInfo[{record_index}] remap {i} exceeds safety limit")
            offsets.append(rel)
            counts.append(count)
        for i, (rel, count) in enumerate(zip(offsets, counts)):
            if count == 0:
                remaps.append([])
                continue
            if rel < table_size:
                raise HD2ResourceError(f"BoneInfo[{record_index}] remap {i} points into its header")
            remap_abs = fake_abs + rel
            _check_range(data[:record_end], remap_abs, count * 4, f"BoneInfo[{record_index}] remap[{i}]")
            remaps.append(list(struct.unpack_from(f"<{count}I", data, remap_abs)))
    if real_indices and any(index >= MAX_TRANSFORMS for index in real_indices):
        warnings.append(f"BoneInfo[{record_index}] contains an unusually large RealIndices value")
    return {
        "index": record_index,
        "num_bones": num_bones,
        "matrices": matrices,
        "real_indices": real_indices,
        "remaps": remaps,
        "relative_offsets": {
            "matrices": matrix_offset,
            "real_indices": real_offset,
            "fake_indices": fake_offset,
        },
        "warnings": warnings,
    }


def parse_bone_info(data: bytes, offset: int, end_offset: Optional[int] = None) -> dict:
    """Decode the Unit BoneInfo record table and retain every material remap."""
    if offset == 0:
        return {"count": 0, "records": [], "warnings": []}
    end = len(data) if end_offset is None else min(end_offset, len(data))
    if offset < 0 or offset + 4 > end:
        raise HD2ResourceError("BoneInfo table header is outside its Unit TOC region")
    count = _u32(data, offset, "BoneInfo table count")
    if count > MAX_BONE_INFO:
        raise HD2ResourceError(f"BoneInfo table count exceeds safety limit: {count}")
    _check_range(data[:end], offset + 4, count * 4, "BoneInfo record offset table")
    offsets = list(struct.unpack_from(f"<{count}I", data, offset + 4)) if count else []
    if any(value < 4 + count * 4 for value in offsets):
        raise HD2ResourceError("BoneInfo record offset points inside the offset table")
    if offsets != sorted(offsets) or len(set(offsets)) != len(offsets):
        raise HD2ResourceError("BoneInfo record offsets are not strictly increasing")
    records = []
    for index, relative in enumerate(offsets):
        start = offset + relative
        stop = offset + offsets[index + 1] if index + 1 < count else end
        if stop <= start:
            raise HD2ResourceError(f"BoneInfo[{index}] has an empty or reversed record range")
        records.append(_parse_bone_info_record(data, start, stop, index))
    return {"count": count, "records": records, "warnings": []}


def _component_size(format_id: int) -> Optional[int]:
    item = COMPONENT_FORMATS.get(format_id)
    return item[1] if item else None


def parse_stream_info(data: bytes, offset: int, end_offset: Optional[int] = None) -> dict:
    """Decode StreamInfo descriptors and component layouts (not GPU bytes)."""
    if offset in (0, 1):
        return {"count": 0, "records": [], "warnings": [], "external_sentinel": offset == 1}
    end = len(data) if end_offset is None else min(end_offset, len(data))
    if offset < 0 or offset + 8 > end:
        raise HD2ResourceError("StreamInfo table header is outside its Unit TOC region")
    count = _u32(data, offset, "StreamInfo count")
    if count > MAX_STREAMS:
        raise HD2ResourceError(f"StreamInfo count exceeds safety limit: {count}")
    table_size = 8 + count * 8
    _check_range(data[:end], offset, table_size, "StreamInfo offset tables")
    relative_offsets = list(struct.unpack_from(f"<{count}I", data, offset + 4)) if count else []
    unknowns = list(struct.unpack_from(f"<{count}I", data, offset + 4 + count * 4)) if count else []
    unknown_tail = _u32(data, offset + 4 + count * 8, "StreamInfo trailing word")
    records = []
    warnings = []
    for index, relative in enumerate(relative_offsets):
        if relative < table_size:
            raise HD2ResourceError(f"StreamInfo[{index}] offset points inside its table")
        start = offset + relative
        _check_range(data[:end], start, 448, f"StreamInfo[{index}] fixed record")
        component_info_id = _u64(data, start, f"StreamInfo[{index}] ComponentInfoID")
        num_components = _u64(data, start + 328, f"StreamInfo[{index}] component count")
        if num_components > MAX_COMPONENTS:
            raise HD2ResourceError(f"StreamInfo[{index}] has {num_components} components; maximum is {MAX_COMPONENTS}")
        components = []
        stride_sum = 0
        component_offsets_known = True
        for component_index in range(num_components):
            pos = start + 8 + component_index * 20
            comp_type, comp_format, comp_index = struct.unpack_from("<3I", data, pos)
            comp_unknown = _u64(data, pos + 12, f"StreamInfo[{index}] component unknown")
            size = _component_size(comp_format)
            if size is None:
                warnings.append(f"StreamInfo[{index}] component {component_index} uses unsupported format {comp_format}")
                byte_offset = None
                component_offsets_known = False
            else:
                byte_offset = stride_sum if component_offsets_known else None
                if component_offsets_known:
                    stride_sum += size
            components.append({
                "type": comp_type,
                "type_name": COMPONENT_TYPES.get(comp_type, f"unknown_{comp_type}"),
                "format": comp_format,
                "format_name": COMPONENT_FORMATS.get(comp_format, (f"unknown_{comp_format}",))[0],
                "index": comp_index,
                "unknown": comp_unknown,
                "byte_size": size,
                "byte_offset": byte_offset,
            })
        fields = struct.unpack_from("<QQIIQQQQIIQQIIII", data, start + 336)
        (vertex_buffer_id, vertex_unk1, num_vertices, vertex_stride, vertex_unk2,
         vertex_unk3, index_buffer_id, index_unk1, num_indices, index_type,
         index_unk2, index_unk3, vertex_buffer_offset, vertex_buffer_size,
         index_buffer_offset, index_buffer_size) = fields
        # The packed layout above is intentionally validated against explicit byte offsets.
        record = {
            "index": index,
            "relative_offset": relative,
            "component_info_id": component_info_id,
            "unknown_table_value": unknowns[index],
            "components": components,
            "num_vertices": num_vertices,
            "vertex_stride": vertex_stride,
            "vertex_buffer_id": vertex_buffer_id,
            "vertex_buffer_unknown": [vertex_unk1, vertex_unk2, vertex_unk3],
            "vertex_buffer_offset": vertex_buffer_offset,
            "vertex_buffer_size": vertex_buffer_size,
            "index_buffer_id": index_buffer_id,
            "index_buffer_unknown": [index_unk1, index_unk2, index_unk3],
            "num_indices": num_indices,
            "index_buffer_type": index_type,
            "index_buffer_offset": index_buffer_offset,
            "index_buffer_size": index_buffer_size,
            "component_stride_sum": stride_sum,
            "warnings": [],
        }
        if stride_sum and vertex_stride != stride_sum:
            message = f"StreamInfo[{index}] component sizes sum to {stride_sum}, VertexStride is {vertex_stride}"
            record["warnings"].append(message)
            warnings.append(message)
        if stride_sum > vertex_stride:
            raise HD2ResourceError(f"StreamInfo[{index}] components exceed VertexStride")
        if vertex_stride > 0 and num_vertices > MAX_VERTICES_PER_MESH:
            raise HD2ResourceError(f"StreamInfo[{index}] vertex count exceeds safety limit")
        if num_indices > MAX_INDICES_PER_MESH:
            raise HD2ResourceError(f"StreamInfo[{index}] index count exceeds safety limit")
        if index_type not in (0, 1):
            message = f"StreamInfo[{index}] uses unsupported IndexBuffer_Type {index_type}"
            record["warnings"].append(message)
            warnings.append(message)
        records.append(record)
    return {"count": count, "records": records, "unknown_tail": unknown_tail, "warnings": warnings, "external_sentinel": False}


def _parse_mesh_info_item(data: bytes, start: int, end: int, index: int) -> dict:
    _check_range(data[:end], start, 128, f"MeshInfo[{index}] fixed header")
    unk1 = _u64(data, start, f"MeshInfo[{index}] unknown1")
    unk2 = data[start + 8:start + 40]
    mesh_id, unk3, transform_index, unk4 = struct.unpack_from("<4I", data, start + 40)
    lod_index = _i32(data, start + 56, f"MeshInfo[{index}] LOD index")
    stream_index = _u32(data, start + 60, f"MeshInfo[{index}] stream index")
    unk6 = data[start + 64:start + 104]
    num_materials = _u32(data, start + 104, f"MeshInfo[{index}] material count")
    material_offset = _u32(data, start + 108, f"MeshInfo[{index}] material offset")
    unk8 = _u64(data, start + 112, f"MeshInfo[{index}] unknown8")
    num_sections = _u32(data, start + 120, f"MeshInfo[{index}] section count")
    sections_offset = _u32(data, start + 124, f"MeshInfo[{index}] section offset")
    if num_materials > 100_000 or num_sections > 100_000:
        raise HD2ResourceError(f"MeshInfo[{index}] material/section count exceeds safety limit")
    if num_materials:
        if material_offset < 128:
            raise HD2ResourceError(f"MeshInfo[{index}] material array offset is invalid")
        _check_range(data[:end], start + material_offset, num_materials * 4, f"MeshInfo[{index}] material IDs")
        material_ids = list(struct.unpack_from(f"<{num_materials}I", data, start + material_offset))
    else:
        material_ids = []
    sections = []
    if num_sections:
        if sections_offset < 128:
            raise HD2ResourceError(f"MeshInfo[{index}] sections offset is invalid")
        _check_range(data[:end], start + sections_offset, num_sections * 24, f"MeshInfo[{index}] sections")
        for section_index in range(num_sections):
            values = struct.unpack_from("<6I", data, start + sections_offset + section_index * 24)
            material_index, vertex_offset, num_vertices, index_offset, num_indices, group_index = values
            if material_index >= len(material_ids):
                raise HD2ResourceError(f"MeshInfo[{index}] section {section_index} material index is out of range")
            sections.append({
                "index": section_index,
                "material_index": material_index,
                "material_slot_id": material_ids[material_index],
                "vertex_offset": vertex_offset,
                "num_vertices": num_vertices,
                "index_offset": index_offset,
                "num_indices": num_indices,
                "group_index": group_index,
            })
    return {
        "index": index,
        "mesh_id": mesh_id,
        "unknown1": unk1,
        "unknown2_hex": unk2.hex(),
        "unknown3": unk3,
        "transform_index": transform_index,
        "unknown4": unk4,
        "lod_index": lod_index,
        "stream_index": stream_index,
        "unknown6_hex": unk6.hex(),
        "material_ids": material_ids,
        "unknown8": unk8,
        "sections": sections,
        "relative_offsets": {"materials": material_offset, "sections": sections_offset},
    }


def parse_mesh_info(data: bytes, offset: int, end_offset: Optional[int] = None) -> dict:
    if offset == 0:
        return {"count": 0, "records": [], "warnings": []}
    end = len(data) if end_offset is None else min(end_offset, len(data))
    if offset + 4 > end:
        raise HD2ResourceError("MeshInfo table header is outside its Unit TOC region")
    count = _u32(data, offset, "MeshInfo count")
    if count > MAX_MESHES:
        raise HD2ResourceError(f"MeshInfo count exceeds safety limit: {count}")
    _check_range(data[:end], offset + 4, count * 8, "MeshInfo offset tables")
    rel_offsets = list(struct.unpack_from(f"<{count}I", data, offset + 4)) if count else []
    unknowns = list(struct.unpack_from(f"<{count}I", data, offset + 4 + count * 4)) if count else []
    if any(value < 4 + count * 8 for value in rel_offsets):
        raise HD2ResourceError("MeshInfo record offset points inside its offset table")
    if rel_offsets != sorted(rel_offsets) or len(set(rel_offsets)) != len(rel_offsets):
        raise HD2ResourceError("MeshInfo record offsets are not strictly increasing")
    records = []
    for index, relative in enumerate(rel_offsets):
        start = offset + relative
        stop = offset + rel_offsets[index + 1] if index + 1 < count else end
        if stop <= start:
            raise HD2ResourceError(f"MeshInfo[{index}] has an empty or reversed record range")
        item = _parse_mesh_info_item(data, start, stop, index)
        item["unknown_table_value"] = unknowns[index]
        records.append(item)
    return {"count": count, "records": records, "warnings": []}


def parse_composite_unit(data: bytes, gpu_data: bytes, target_unit_id: int) -> dict:
    """Read one Unit's mesh-layout mapping and the CompositeUnit's shared streams."""
    if len(data) < 16:
        raise HD2ResourceError("CompositeUnit payload is shorter than its 16-byte header")
    num_units = _u32(data, 8, "CompositeUnit unit count")
    stream_offset = _u32(data, 12, "CompositeUnit StreamInfoOffset")
    if num_units > MAX_MESHES:
        raise HD2ResourceError("CompositeUnit unit count exceeds safety limit")
    rows_start = 16
    _check_range(data, rows_start, num_units * 16 + num_units * 4, "CompositeUnit unit and mesh-info tables")
    unit_rows = []
    for index in range(num_units):
        unit_type_hash, unit_hash = struct.unpack_from("<QQ", data, rows_start + index * 16)
        unit_rows.append({"unit_type_hash": unit_type_hash, "unit_hash": unit_hash})
    offsets_start = rows_start + num_units * 16
    mesh_info_offsets = list(struct.unpack_from(f"<{num_units}I", data, offsets_start)) if num_units else []
    target_index = next((i for i, row in enumerate(unit_rows) if row["unit_hash"] == target_unit_id), None)
    if target_index is None:
        raise HD2ResourceError(f"CompositeUnit does not contain Unit {target_unit_id:016x}")
    info_offset = mesh_info_offsets[target_index]
    if info_offset == 0:
        raise HD2ResourceError("CompositeUnit target Unit has no MeshInfo mapping")
    mesh_count = _u32(data, info_offset, "CompositeMeshInfo mesh count")
    if mesh_count > MAX_MESHES:
        raise HD2ResourceError("CompositeMeshInfo mesh count exceeds safety limit")
    _check_range(data, info_offset + 4, mesh_count * 8, "CompositeMeshInfo tables")
    mesh_ids = list(struct.unpack_from(f"<{mesh_count}I", data, info_offset + 4)) if mesh_count else []
    item_offsets = list(struct.unpack_from(f"<{mesh_count}I", data, info_offset + 4 + mesh_count * 4)) if mesh_count else []
    composite_meshes = {}
    for index, (mesh_id, relative) in enumerate(zip(mesh_ids, item_offsets)):
        if relative < 4 + mesh_count * 8:
            raise HD2ResourceError(f"CompositeMeshInfo item {index} offset points inside its table")
        start = info_offset + relative
        _check_range(data, start, 48, f"CompositeMeshInfo item {index} header")
        layout_index = _u32(data, start, f"CompositeMeshInfo item {index} MeshLayoutIdx")
        num_materials = _u32(data, start + 24, f"CompositeMeshInfo item {index} material count")
        materials_offset = _u32(data, start + 28, f"CompositeMeshInfo item {index} materials offset")
        num_groups = _u32(data, start + 40, f"CompositeMeshInfo item {index} group count")
        groups_offset = _u32(data, start + 44, f"CompositeMeshInfo item {index} groups offset")
        if num_materials > 100_000 or num_groups > 100_000:
            raise HD2ResourceError(f"CompositeMeshInfo item {index} count exceeds safety limit")
        if num_materials:
            if materials_offset < 48:
                raise HD2ResourceError(f"CompositeMeshInfo item {index} materials offset is invalid")
            _check_range(data, start + materials_offset, num_materials * 4, f"CompositeMeshInfo item {index} materials")
            materials = list(struct.unpack_from(f"<{num_materials}I", data, start + materials_offset))
        else:
            materials = []
        groups = []
        if num_groups:
            if groups_offset < 48:
                raise HD2ResourceError(f"CompositeMeshInfo item {index} groups offset is invalid")
            _check_range(data, start + groups_offset, num_groups * 24, f"CompositeMeshInfo item {index} groups")
            for group_index in range(num_groups):
                material_index, vertex_offset, num_vertices, index_offset, num_indices, group_id = struct.unpack_from(
                    "<6I", data, start + groups_offset + group_index * 24
                )
                if material_index >= len(materials):
                    raise HD2ResourceError(f"CompositeMeshInfo item {index} group {group_index} material index is out of range")
                groups.append({
                    "index": group_index,
                    "material_index": material_index,
                    "material_slot_id": materials[material_index],
                    "vertex_offset": vertex_offset,
                    "num_vertices": num_vertices,
                    "index_offset": index_offset,
                    "num_indices": num_indices,
                    "group_index": group_id,
                })
        composite_meshes[mesh_id] = {
            "mesh_id": mesh_id,
            "stream_index": layout_index,
            "material_ids": materials,
            "sections": groups,
        }
    streams = parse_stream_info(data, stream_offset) if stream_offset else {"count": 0, "records": [], "warnings": []}
    return {
        "num_units": num_units,
        "unit_rows": unit_rows,
        "target_unit_index": target_index,
        "stream_info_offset": stream_offset,
        "mesh_info": composite_meshes,
        "streams": streams,
        "gpu_bytes": len(gpu_data),
    }


def _decode_component_value(data: bytes, offset: int, format_id: int):
    item = COMPONENT_FORMATS.get(format_id)
    if item is None:
        return {"raw_hex": data[offset:offset + 1].hex()}
    _name, size, fmt, count = item
    raw = struct.unpack_from("<" + fmt, data, offset)
    if count == 1:
        return raw[0]
    return list(raw)


def _decode_mesh_geometry(mesh: dict, streams: dict, gpu_data: bytes) -> tuple[dict, list[str]]:
    warnings = []
    stream_index = mesh["stream_index"]
    if stream_index < 0 or stream_index >= len(streams["records"]):
        raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} references invalid StreamInfo index {stream_index}")
    stream = streams["records"][stream_index]
    sections = mesh["sections"]
    if not sections:
        raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} has no material sections")
    first = next((section for section in sections if section["num_vertices"]), sections[0])
    vertex_count = first["num_vertices"]
    vertex_start = first["vertex_offset"]
    if any(section["vertex_offset"] != vertex_start or section["num_vertices"] != vertex_count for section in sections):
        warnings.append(f"Mesh {mesh['mesh_id']:08x} sections disagree on vertex range; decoding the first section's vertex range")
    if vertex_count > MAX_VERTICES_PER_MESH:
        raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} vertex count exceeds safety limit")
    if vertex_start + vertex_count > stream["num_vertices"]:
        raise HD2ResourceError(
            f"Mesh {mesh['mesh_id']:08x} vertex range {vertex_start}+{vertex_count} exceeds stream vertices {stream['num_vertices']}"
        )
    if stream["vertex_stride"] <= 0 and vertex_count:
        raise HD2ResourceError(f"StreamInfo[{stream_index}] has vertices but zero VertexStride")
    _check_range(gpu_data, stream["vertex_buffer_offset"], stream["vertex_buffer_size"],
                 f"StreamInfo[{stream_index}] declared vertex buffer")
    _check_range(gpu_data, stream["index_buffer_offset"], stream["index_buffer_size"],
                 f"StreamInfo[{stream_index}] declared index buffer")
    vertex_base = stream["vertex_buffer_offset"] + vertex_start * stream["vertex_stride"]
    required_vertex_bytes = vertex_count * stream["vertex_stride"]
    if vertex_base + required_vertex_bytes > stream["vertex_buffer_offset"] + stream["vertex_buffer_size"]:
        raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} vertex range exceeds declared VertexBufferSize")
    _check_range(gpu_data, vertex_base, required_vertex_bytes, f"Mesh {mesh['mesh_id']:08x} vertex data")
    attributes = []
    for component in stream["components"]:
        fmt = component["format"]
        size = component["byte_size"]
        if size is None or component["byte_offset"] is None:
            warnings.append(f"Mesh {mesh['mesh_id']:08x} component format {fmt} not decoded because its byte offset is unknown")
            continue
        if component["byte_offset"] + size > stream["vertex_stride"]:
            raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} component exceeds VertexStride")
        values = []
        for vertex_index in range(vertex_count):
            pos = vertex_base + vertex_index * stream["vertex_stride"] + component["byte_offset"]
            values.append(_decode_component_value(gpu_data, pos, fmt))
        attribute = {
            "component_type": component["type"],
            "component_name": component["type_name"],
            "component_index": component["index"],
            "format": fmt,
            "format_name": component["format_name"],
            # Keep exact storage values in the existing field for compatibility.
            # Semantic interpretations are additive and never replace raw words.
            "values": values,
        }
        if fmt == 30 and component["type"] == 1:
            attribute["decoded_semantics"] = "octahedral_normal"
            attribute["decoded_values"] = [decode_octahedral_normal(value) for value in values]
        elif fmt == 29 and component["type"] == 7:
            attribute["decoded_semantics"] = "unorm10_10_10_2_bone_weights"
            attribute["decoded_values"] = [decode_packed_weights_1010102(value) for value in values]
        elif fmt == 30:
            warnings.append(
                f"Mesh {mesh['mesh_id']:08x} format 30 is used by component type "
                f"{component['type_name']}; retained as raw uint32 because only normal semantics are decoded"
            )
        elif fmt == 29:
            warnings.append(
                f"Mesh {mesh['mesh_id']:08x} format 29 is used by component type "
                f"{component['type_name']}; retained as raw uint32 because only bone_weight semantics are decoded"
            )
        attributes.append(attribute)
    index_type = stream["index_buffer_type"]
    if index_type == 0:
        index_fmt, index_size = "H", 2
    elif index_type == 1:
        index_fmt, index_size = "I", 4
    else:
        raise HD2ResourceError(f"StreamInfo[{stream_index}] uses unsupported index type {index_type}")
    all_sections = []
    total_indices = 0
    for section in sections:
        if section["num_indices"] > MAX_INDICES_PER_MESH:
            raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} section index count exceeds safety limit")
        if section["index_offset"] + section["num_indices"] > stream["num_indices"]:
            raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} section {section['index']} exceeds StreamInfo NumIndices")
        index_start = stream["index_buffer_offset"] + section["index_offset"] * index_size
        index_bytes = section["num_indices"] * index_size
        if index_start + index_bytes > stream["index_buffer_offset"] + stream["index_buffer_size"]:
            raise HD2ResourceError(f"Mesh {mesh['mesh_id']:08x} section {section['index']} exceeds declared IndexBufferSize")
        _check_range(gpu_data, index_start, index_bytes, f"Mesh {mesh['mesh_id']:08x} section {section['index']} index data")
        indices = list(struct.unpack_from(f"<{section['num_indices']}{index_fmt}", gpu_data, index_start)) if section["num_indices"] else []
        invalid = [value for value in indices if value >= vertex_count]
        if invalid:
            warnings.append(
                f"Mesh {mesh['mesh_id']:08x} section {section['index']} has {len(invalid)} index values outside local vertex range 0..{max(0, vertex_count - 1)}"
            )
        enriched = dict(section)
        enriched["indices"] = indices
        enriched["invalid_index_count"] = len(invalid)
        all_sections.append(enriched)
        total_indices += len(indices)
    return ({
        "mesh_id": mesh["mesh_id"],
        "mesh_info_index": mesh["index"],
        "transform_index": mesh["transform_index"],
        "lod_index": mesh["lod_index"],
        "stream_index": stream_index,
        "vertex_count": vertex_count,
        "vertex_offset": vertex_start,
        "vertex_stride": stream["vertex_stride"],
        "attributes": attributes,
        "sections": all_sections,
        "index_count": total_indices,
        "triangle_count": total_indices // 3,
    }, warnings)


def _matrix4_multiply(left: list[float], right: list[float]) -> list[float]:
    """Multiply row-major 4x4 matrices for row-vector transforms."""
    return [
        sum(left[row * 4 + k] * right[k * 4 + column] for k in range(4))
        for row in range(4)
        for column in range(4)
    ]


def _matrix4_inverse(values: list[float]) -> Optional[list[float]]:
    """Return a generic 4x4 inverse using pivoted Gauss-Jordan elimination."""
    if len(values) != 16:
        return None
    try:
        numeric_values = [float(value) for value in values]
    except (TypeError, ValueError):
        return None
    if not all(math.isfinite(value) for value in numeric_values):
        return None
    rows = [
        [numeric_values[row * 4 + column] for column in range(4)]
        + [1.0 if row == column else 0.0 for column in range(4)]
        for row in range(4)
    ]
    for column in range(4):
        pivot = max(range(column, 4), key=lambda row: abs(rows[row][column]))
        if abs(rows[pivot][column]) <= 1.0e-12:
            return None
        if pivot != column:
            rows[column], rows[pivot] = rows[pivot], rows[column]
        divisor = rows[column][column]
        rows[column] = [value / divisor for value in rows[column]]
        for row in range(4):
            if row == column:
                continue
            factor = rows[row][column]
            if factor:
                rows[row] = [
                    value - factor * pivot_value
                    for value, pivot_value in zip(rows[row], rows[column])
                ]
    inverse = [rows[row][4 + column] for row in range(4) for column in range(4)]
    return inverse if all(math.isfinite(value) for value in inverse) else None


def _has_unverified_nonuniform_scale_rotation(transform: dict) -> bool:
    """True when nonuniform scale and rotation make multiplication order observable."""
    rotation = transform.get("rotation_3x3")
    scale = transform.get("scale")
    if not isinstance(rotation, list) or len(rotation) != 9:
        return False
    if not isinstance(scale, list) or len(scale) != 3:
        return False
    try:
        rotation_values = [float(value) for value in rotation]
        scale_values = [float(value) for value in scale]
    except (TypeError, ValueError):
        return False
    if not all(math.isfinite(value) for value in rotation_values + scale_values):
        return False
    nonuniform_scale = max(scale_values) - min(scale_values) > 1.0e-6
    identity = [1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0]
    nonidentity_rotation = any(
        abs(value - expected) > 1.0e-6
        for value, expected in zip(rotation_values, identity)
    )
    return nonuniform_scale and nonidentity_rotation


def _local_transform_matrix(transform: dict) -> Optional[list[float]]:
    """Build the observed row-vector local matrix from Unit local fields."""
    rotation = transform.get("rotation_3x3")
    position = transform.get("position")
    scale = transform.get("scale")
    if not isinstance(rotation, list) or len(rotation) != 9:
        return None
    if not isinstance(position, list) or len(position) != 3:
        return None
    if not isinstance(scale, list) or len(scale) != 3:
        return None
    try:
        rotation = [float(value) for value in rotation]
        position = [float(value) for value in position]
        scale = [float(value) for value in scale]
    except (TypeError, ValueError):
        return None
    if not all(math.isfinite(value) for value in rotation + position + scale):
        return None

    matrix = [0.0] * 16
    for row in range(3):
        for column in range(3):
            matrix[row * 4 + column] = rotation[row * 3 + column] * scale[column]
    matrix[12:15] = position
    matrix[15] = 1.0
    return matrix


def _build_transform_world_matrices(
    transform_info: dict,
    tolerance: float = 1.0e-4,
) -> tuple[list[Optional[list[float]]], dict]:
    """Rebuild hierarchy matrices and compare them with stored TransformInfo matrices.

    The row-vector / local-times-parent-world convention is an observed
    candidate validated against the current real-game samples, not a claim
    that every Unit variant uses an identical convention.
    """
    transforms = transform_info.get("transforms", [])
    count = len(transforms)
    world_matrices: list[Optional[list[float]]] = [None] * count
    states = [0] * count
    hierarchy_status = [None] * count

    for start in range(count):
        if states[start] == 2:
            continue
        path = []
        current = start
        failure = None
        while True:
            if states[current] == 2:
                break
            if states[current] == 1:
                failure = "parent_cycle"
                break
            states[current] = 1
            path.append(current)
            raw_parent = transforms[current].get("parent_index_raw")
            if isinstance(raw_parent, bool) or not isinstance(raw_parent, int):
                failure = "invalid_parent_value"
                break
            if raw_parent == 0xFFFF or raw_parent == current:
                break
            if raw_parent < 0 or raw_parent >= count:
                failure = "parent_out_of_range"
                break
            current = raw_parent

        if failure:
            for index in path:
                hierarchy_status[index] = failure
                states[index] = 2
            continue

        for index in reversed(path):
            if _has_unverified_nonuniform_scale_rotation(transforms[index]):
                hierarchy_status[index] = "nonuniform_scale_rotation_order_unverified"
                world_matrices[index] = None
                states[index] = 2
                continue
            local = _local_transform_matrix(transforms[index])
            if local is None:
                hierarchy_status[index] = "invalid_local_transform"
                world_matrices[index] = None
                states[index] = 2
                continue
            raw_parent = transforms[index]["parent_index_raw"]
            if raw_parent == 0xFFFF or raw_parent == index:
                world_matrices[index] = local
                hierarchy_status[index] = "computed"
            elif world_matrices[raw_parent] is None:
                world_matrices[index] = None
                hierarchy_status[index] = "parent_unresolved"
            else:
                world_matrices[index] = _matrix4_multiply(local, world_matrices[raw_parent])
                hierarchy_status[index] = "computed"
            states[index] = 2

    entries = []
    matched = 0
    mismatched = 0
    unresolved = 0
    errors = []
    for index, transform in enumerate(transforms):
        computed = world_matrices[index]
        if computed is None:
            entry_status = hierarchy_status[index] or "unresolved"
            error = None
            unresolved += 1
        else:
            stored = transform.get("matrix_4x4_raw")
            if not isinstance(stored, list) or len(stored) != 16:
                entry_status = "missing_stored_matrix"
                error = None
                unresolved += 1
            else:
                try:
                    stored_values = [float(value) for value in stored]
                    if not all(math.isfinite(value) for value in stored_values):
                        raise ValueError("non-finite matrix")
                    error = max(abs(left - right) for left, right in zip(computed, stored_values))
                    errors.append(error)
                    if error <= tolerance:
                        entry_status = "matches"
                        matched += 1
                    else:
                        entry_status = "mismatch"
                        mismatched += 1
                except (TypeError, ValueError):
                    entry_status = "invalid_stored_matrix"
                    error = None
                    unresolved += 1
        entries.append({
            "transform_index": index,
            "parent_index_raw": transform.get("parent_index_raw"),
            "hierarchy_status": hierarchy_status[index] or "computed",
            "status": entry_status,
            "max_abs_error": error,
        })

    if count == 0:
        overall = "empty"
    elif mismatched:
        overall = "partial" if unresolved else "mismatch"
    elif unresolved:
        overall = "partial"
    else:
        overall = "matches"
    report = {
        "status": overall,
        "matrix_layout_candidate": "row_major_4x4_row_vector",
        "hierarchy_composition_candidate": "local_times_parent_world",
        "local_linear_composition_candidate": "rotation_3x3_times_diagonal_scale",
        "scale_rotation_order_validation": "not_independently_validated",
        "tolerance": tolerance,
        "transform_count": count,
        "matched_transform_count": matched,
        "mismatched_transform_count": mismatched,
        "unresolved_transform_count": unresolved,
        "max_abs_error": max(errors) if errors else None,
        "entries": entries,
    }
    return world_matrices, report


def _validate_mesh_bone_info_matrices(
    mesh: dict,
    record: dict,
    used_palette_slots: dict[int, int],
    world_matrices: list[Optional[list[float]]],
    tolerance: float = 1.0e-4,
) -> dict:
    """Compare active BoneInfo slots with MeshWorld * inverse(BoneWorld)."""
    palette_count = len(record.get("real_indices", []))
    unreferenced_count = max(0, palette_count - len(used_palette_slots))
    mesh_transform_index = mesh.get("transform_index")
    if not used_palette_slots:
        status = "no_active_palette_slots"
        checks = []
    elif (
        not isinstance(mesh_transform_index, int)
        or mesh_transform_index < 0
        or mesh_transform_index >= len(world_matrices)
        or world_matrices[mesh_transform_index] is None
    ):
        status = "unavailable"
        checks = [{
            "palette_slot": slot,
            "transform_index": transform_index,
            "status": "mesh_transform_unavailable",
            "max_abs_error": None,
        } for slot, transform_index in sorted(used_palette_slots.items())]
    else:
        mesh_world = world_matrices[mesh_transform_index]
        matrices = record.get("matrices", [])
        checks = []
        for slot, transform_index in sorted(used_palette_slots.items()):
            if (
                slot < 0 or slot >= len(matrices)
                or transform_index < 0 or transform_index >= len(world_matrices)
                or world_matrices[transform_index] is None
            ):
                checks.append({
                    "palette_slot": slot,
                    "transform_index": transform_index,
                    "status": "transform_or_palette_matrix_unavailable",
                    "max_abs_error": None,
                })
                continue
            inverse_bone_world = _matrix4_inverse(world_matrices[transform_index])
            stored = matrices[slot]
            if inverse_bone_world is None or not isinstance(stored, list) or len(stored) != 16:
                checks.append({
                    "palette_slot": slot,
                    "transform_index": transform_index,
                    "status": "matrix_unavailable",
                    "max_abs_error": None,
                })
                continue
            expected = _matrix4_multiply(mesh_world, inverse_bone_world)
            try:
                stored_values = [float(value) for value in stored]
                if not all(math.isfinite(value) for value in stored_values):
                    raise ValueError("non-finite matrix")
                error = max(abs(left - right) for left, right in zip(expected, stored_values))
            except (TypeError, ValueError):
                checks.append({
                    "palette_slot": slot,
                    "transform_index": transform_index,
                    "status": "invalid_matrix",
                    "max_abs_error": None,
                })
                continue
            checks.append({
                "palette_slot": slot,
                "transform_index": transform_index,
                "status": "matches" if error <= tolerance else "mismatch",
                "max_abs_error": error,
            })

        match_count = sum(item["status"] == "matches" for item in checks)
        mismatch_count = sum(item["status"] == "mismatch" for item in checks)
        unavailable_count = len(checks) - match_count - mismatch_count
        if mismatch_count:
            status = "partial" if unavailable_count else "mismatch"
        elif unavailable_count:
            status = "partial"
        else:
            status = "matches"

    match_count = sum(item["status"] == "matches" for item in checks)
    mismatch_count = sum(item["status"] == "mismatch" for item in checks)
    unavailable_count = len(checks) - match_count - mismatch_count
    errors = [
        item["max_abs_error"] for item in checks
        if item["max_abs_error"] is not None
    ]
    return {
        "status": status,
        "comparison_formula_candidate": "mesh_world_times_inverse_bone_world",
        "vector_convention_candidate": "row_vector",
        "mesh_transform_index": mesh_transform_index,
        "tolerance": tolerance,
        "active_palette_slot_count": len(used_palette_slots),
        "matched_active_palette_slot_count": match_count,
        "mismatched_active_palette_slot_count": mismatch_count,
        "unavailable_active_palette_slot_count": unavailable_count,
        "unreferenced_palette_slot_count": unreferenced_count,
        "max_abs_error": max(errors) if errors else None,
        "palette_slots": checks,
    }


def resolve_unit_skinning(
    transform_info: dict,
    bone_info: dict,
    bones_resource: Optional[dict],
    mesh_info: dict,
    geometry: list[dict],
) -> list[str]:
    """Resolve material-scoped vertex influences to named TransformInfo bones.

    Mapping order:
        vertex-local index -> Remaps[material_index] -> RealIndices
        -> TransformInfo index -> Bones name_hash.

    Each bone_index component index is kept as an independent index set. This
    matters for legacy/grouped HD2 layouts; sets are never merged or summed.
    BoneInfo matrices remain raw. A separate diagnostic reconstructs the
    TransformInfo hierarchy and compares actively used palette matrices with
    the observed MeshWorld * inverse(BoneWorld) relationship.
    """
    warnings = []
    transforms = transform_info.get("transforms", [])
    world_matrices, hierarchy_matrix_validation = _build_transform_world_matrices(transform_info)
    transform_info["hierarchy_matrix_validation"] = hierarchy_matrix_validation
    records = bone_info.get("records", [])
    names_by_hash = {}
    if bones_resource:
        for item in bones_resource.get("bones", []):
            names_by_hash.setdefault(item["hash"], []).append(item["name"])

    skeleton = []
    for item in transforms:
        candidates = names_by_hash.get(item["name_hash"], [])
        parent_raw = item["parent_index_raw"]
        parent_index = (
            None if parent_raw == 0xFFFF or parent_raw == item["index"]
            else parent_raw
        )
        parent_name = None
        if parent_index is not None and 0 <= parent_index < len(transforms):
            parent_hash = transforms[parent_index]["name_hash"]
            parent_candidates = names_by_hash.get(parent_hash, [])
            if len(parent_candidates) == 1:
                parent_name = parent_candidates[0]
        skeleton.append({
            "transform_index": item["index"],
            "name_hash": item["name_hash"],
            "name": candidates[0] if len(candidates) == 1 else None,
            "name_ambiguous": len(candidates) > 1,
            "parent_index_raw": parent_raw,
            "parent_transform_index": parent_index,
            "parent_name": parent_name,
            "increment": item["increment"],
        })
    transform_info["resolved_skeleton"] = skeleton
    names_by_transform = {item["transform_index"]: item["name"] for item in skeleton}
    active_influence_counts: dict[int, int] = {}

    # BoneInfo.Bones[i] and RealIndices[i] share the same palette slot.
    for record in records:
        resolved_palette = []
        for palette_slot, transform_index in enumerate(record["real_indices"]):
            if transform_index < 0 or transform_index >= len(transforms):
                resolved_palette.append({
                    "palette_slot": palette_slot,
                    "transform_index": transform_index,
                    "name_hash": None,
                    "name": None,
                    "bone_info_matrix_raw": (
                        record["matrices"][palette_slot]
                        if palette_slot < len(record["matrices"]) else None
                    ),
                    "valid": False,
                })
                continue
            transform = transforms[transform_index]
            candidates = names_by_hash.get(transform["name_hash"], [])
            resolved_palette.append({
                "palette_slot": palette_slot,
                "transform_index": transform_index,
                "name_hash": transform["name_hash"],
                "name": candidates[0] if len(candidates) == 1 else None,
                "name_ambiguous": len(candidates) > 1,
                "bone_info_matrix_raw": (
                    record["matrices"][palette_slot]
                    if palette_slot < len(record["matrices"]) else None
                ),
                "valid": True,
            })
        record["resolved_real_indices"] = resolved_palette
        record["resolved_remaps"] = [
            [
                resolved_palette[slot]["transform_index"]
                if slot < len(resolved_palette) and resolved_palette[slot]["valid"]
                else None
                for slot in remap
            ]
            for remap in record["remaps"]
        ]

    mesh_by_index = {item["index"]: item for item in mesh_info.get("records", [])}
    for mesh_geometry in geometry:
        mesh = mesh_by_index.get(mesh_geometry["mesh_info_index"])
        if mesh is None:
            warnings.append(
                f"Mesh geometry {mesh_geometry['mesh_id']:08x} has no matching MeshInfo record"
            )
            continue

        attributes = mesh_geometry.get("attributes", [])
        weight_attrs = [a for a in attributes if a["component_type"] == 7]
        index_attrs = sorted(
            (a for a in attributes if a["component_type"] == 6),
            key=lambda a: a["component_index"],
        )
        if not weight_attrs or not index_attrs:
            mesh_geometry["skinning"] = {
                "status": "no_skin_attributes",
                "lod_index": mesh_geometry["lod_index"],
                "section_skinning": [],
            }
            continue
        if len(weight_attrs) != 1:
            warnings.append(
                f"Mesh {mesh_geometry['mesh_id']:08x} has {len(weight_attrs)} bone-weight attributes; "
                "only a single shared weight attribute is currently supported"
            )
            mesh_geometry["skinning"] = {
                "status": "unsupported_weight_layout",
                "lod_index": mesh_geometry["lod_index"],
                "section_skinning": [],
            }
            continue

        lod_index = mesh_geometry["lod_index"]
        if lod_index >= 0:
            bone_info_index = lod_index
        elif lod_index == -1 and records:
            bone_info_index = len(records) - 1
        else:
            bone_info_index = None
        if bone_info_index is None or bone_info_index >= len(records):
            warnings.append(
                f"Mesh {mesh_geometry['mesh_id']:08x} LOD {lod_index} has no matching BoneInfo record"
            )
            mesh_geometry["skinning"] = {
                "status": "missing_bone_info",
                "lod_index": lod_index,
                "section_skinning": [],
            }
            continue

        record = records[bone_info_index]
        weight_attr = weight_attrs[0]
        weight_values = weight_attr.get("decoded_values")
        if weight_values is None:
            weight_values = weight_attr["values"]
            if weight_attr["format"] == 29:
                weight_values = [decode_packed_weights_1010102(v) for v in weight_values]
        if weight_values and not isinstance(weight_values[0], list):
            weight_values = [[value] for value in weight_values]

        section_results = []
        resolved_count = 0
        unresolved_count = 0
        used_palette_slots: dict[int, int] = {}

        def active_weight_lanes(vertex_index: int) -> int:
            if vertex_index < 0 or vertex_index >= len(weight_values):
                return 0
            return sum(1 for raw_weight in weight_values[vertex_index] if float(raw_weight) > 0.0)

        for section in mesh_geometry.get("sections", []):
            material_index = section["material_index"]
            vertex_indices = sorted(set(section.get("indices", [])))
            remaps = record["remaps"]
            remap = remaps[material_index] if 0 <= material_index < len(remaps) else None
            if remap is None:
                unresolved_section_lanes = sum(
                    active_weight_lanes(vertex_index) * len(index_attrs)
                    for vertex_index in vertex_indices
                )
                warnings.append(
                    f"Mesh {mesh_geometry['mesh_id']:08x} section {section['index']} material index "
                    f"{material_index} has no BoneInfo[{bone_info_index}] remap"
                )
                section_results.append({
                    "section_index": section["index"],
                    "material_index": material_index,
                    "material_slot_id": section.get("material_slot_id"),
                    "status": "missing_material_remap",
                    "vertex_indices": vertex_indices,
                    "bone_index_sets": [],
                    "resolved_active_influence_count": 0,
                    "unresolved_active_influence_count": unresolved_section_lanes,
                })
                unresolved_count += unresolved_section_lanes
                continue

            mapped_sets = []
            for index_attr in index_attrs:
                mapped_rows = []
                influence_rows = []
                set_unresolved = 0
                set_resolved = 0
                out_of_range_vertex_count = 0
                for vertex_index in vertex_indices:
                    if (
                        vertex_index < 0
                        or vertex_index >= len(index_attr["values"])
                        or vertex_index >= len(weight_values)
                    ):
                        mapped_rows.append([])
                        influence_rows.append({
                            "vertex_index": vertex_index,
                            "influences": [],
                            "status": "vertex_out_of_range",
                        })
                        set_unresolved += active_weight_lanes(vertex_index)
                        out_of_range_vertex_count += 1
                        continue
                    local_values = index_attr["values"][vertex_index]
                    if not isinstance(local_values, list):
                        local_values = [local_values]
                    vertex_weights = weight_values[vertex_index]
                    mapped_row = []
                    influences = []
                    for lane, raw_weight in enumerate(vertex_weights):
                        weight = float(raw_weight)
                        local_index = local_values[lane] if lane < len(local_values) else None
                        transform_index = None
                        palette_slot = None
                        if local_index is not None and 0 <= local_index < len(remap):
                            palette_slot = remap[local_index]
                            if 0 <= palette_slot < len(record["real_indices"]):
                                candidate = record["real_indices"][palette_slot]
                                if 0 <= candidate < len(transforms):
                                    transform_index = candidate
                        mapped_row.append(transform_index)
                        if weight > 0.0:
                            if transform_index is None:
                                set_unresolved += 1
                                influences.append({
                                    "lane": lane,
                                    "local_bone_index": local_index,
                                    "palette_slot": palette_slot,
                                    "transform_index": None,
                                    "bone_name": None,
                                    "weight": weight,
                                    "status": "unresolved",
                                })
                            else:
                                set_resolved += 1
                                used_palette_slots[palette_slot] = transform_index
                                active_influence_counts[transform_index] = (
                                    active_influence_counts.get(transform_index, 0) + 1
                                )
                                influences.append({
                                    "lane": lane,
                                    "local_bone_index": local_index,
                                    "palette_slot": palette_slot,
                                    "transform_index": transform_index,
                                    "bone_name": names_by_transform.get(transform_index),
                                    "weight": weight,
                                    "status": "ok",
                                })
                    mapped_rows.append(mapped_row)
                    influence_rows.append({
                        "vertex_index": vertex_index,
                        "influences": influences,
                        "status": "ok" if all(x["status"] == "ok" for x in influences) else "partial",
                    })
                resolved_count += set_resolved
                unresolved_count += set_unresolved
                mapped_sets.append({
                    "component_index": index_attr["component_index"],
                    "transform_indices": mapped_rows,
                    "vertex_influences": influence_rows,
                    "resolved_active_influence_count": set_resolved,
                    "unresolved_active_influence_count": set_unresolved,
                    "out_of_range_vertex_count": out_of_range_vertex_count,
                })
            section_results.append({
                "section_index": section["index"],
                "material_index": material_index,
                "material_slot_id": section.get("material_slot_id"),
                "status": "ok" if all(x["unresolved_active_influence_count"] == 0 for x in mapped_sets) else "partial",
                "vertex_indices": vertex_indices,
                "bone_index_sets": mapped_sets,
                "resolved_active_influence_count": sum(
                    item["resolved_active_influence_count"] for item in mapped_sets
                ),
                "unresolved_active_influence_count": sum(
                    item["unresolved_active_influence_count"] for item in mapped_sets
                ),
            })

        matrix_validation = _validate_mesh_bone_info_matrices(
            mesh, record, used_palette_slots, world_matrices
        )
        mesh_geometry["skinning"] = {
            "status": "ok" if unresolved_count == 0 else "partial",
            "matrix_validation": matrix_validation,
            "lod_index": lod_index,
            "bone_info_index": bone_info_index,
            "weight_attribute": {
                "component_index": weight_attr["component_index"],
                "format": weight_attr["format"],
                "format_name": weight_attr["format_name"],
            },
            "bone_index_set_count": len(index_attrs),
            "palette": record["resolved_real_indices"],
            "section_skinning": section_results,
            "resolved_active_influence_count": resolved_count,
            "unresolved_active_influence_count": unresolved_count,
        }
        if unresolved_count:
            warnings.append(
                f"Mesh {mesh_geometry['mesh_id']:08x} has {unresolved_count} unresolved active bone influences"
            )

    for item in skeleton:
        lane_count = active_influence_counts.get(item["transform_index"], 0)
        item["has_active_vertex_influence"] = lane_count > 0
        item["active_vertex_influence_lane_count"] = lane_count
    active_named = sum(
        1 for item in skeleton
        if item["transform_index"] in active_influence_counts and item["name"] is not None
    )
    active_unnamed = sum(
        1 for item in skeleton
        if item["transform_index"] in active_influence_counts and item["name"] is None
    )
    transform_info["weight_usage_summary"] = {
        "transform_count": len(skeleton),
        "named_transform_count": sum(item["name"] is not None for item in skeleton),
        "unmatched_or_ambiguous_transform_count": sum(item["name"] is None for item in skeleton),
        "actively_weighted_transform_count": len(active_influence_counts),
        "actively_weighted_named_transform_count": active_named,
        "actively_weighted_unnamed_transform_count": active_unnamed,
        "resolved_active_influence_lane_count": sum(active_influence_counts.values()),
        "unresolved_active_influence_lane_count": sum(
            mesh.get("skinning", {}).get("unresolved_active_influence_count", 0)
            for mesh in geometry
        ),
    }
    return warnings


def inspect_hd2_unit(input_path: str | Path, package_name: Optional[str], unit_id: str) -> dict:
    """Inspect one Unit and decode its direct or CompositeUnit-backed geometry."""
    path = Path(input_path)
    if path.is_dir():
        if not package_name:
            raise HD2ResourceError("Specify --package when input is an HD2 game data directory")
        toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(path, package_name)
        package_label = Path(package_name).name
    else:
        if package_name:
            raise HD2ResourceError("--package is only valid when input is a game data directory")
        toc_data, gpu_data, stream_data, source_kind = _read_package_file(path)
        package_label = path.name
    package = parse_toc_package(package_label, toc_data, gpu_data, stream_data, source_kind)
    raw_id = unit_id.lower().removeprefix("0x")
    try:
        unit_file_id = int(raw_id, 16)
    except ValueError as exc:
        raise HD2ResourceError(f"Invalid Unit ID (expected hexadecimal): {unit_id}") from exc
    entries = package.entry_map()
    unit_entry = entries.get((unit_file_id, TYPE_IDS["Unit"]))
    if unit_entry is None:
        raise HD2ResourceError(f"Unit {unit_file_id:016x} was not found in package {package_label}")
    unit_toc, unit_gpu, _unit_stream = package.payload(unit_entry)
    header = parse_unit_header(unit_toc)
    materials = parse_unit_material_ids(unit_toc, header.materials_offset)
    offsets = [
        header.transform_info_offset, header.light_list_offset, header.pre_light_offset,
        header.wwise_callback_offset, header.customization_offset, header.unknown_header_offset,
        header.connecting_bone_hash_offset, header.bone_info_offset,
        header.stream_info_offset if header.stream_info_offset > 1 else 0,
        header.mesh_info_offset, header.ending_offset, header.materials_offset,
    ]
    transform_end = _positive_next(header.transform_info_offset, offsets, len(unit_toc)) if header.transform_info_offset else len(unit_toc)
    transform_info = parse_transform_info(unit_toc, header.transform_info_offset, transform_end)
    bone_end = _positive_next(header.bone_info_offset, [header.stream_info_offset if header.stream_info_offset > 1 else 0, header.mesh_info_offset, header.ending_offset, header.materials_offset], len(unit_toc)) if header.bone_info_offset else len(unit_toc)
    bone_info = parse_bone_info(unit_toc, header.bone_info_offset, bone_end)
    mesh_info = parse_mesh_info(unit_toc, header.mesh_info_offset, header.materials_offset or header.ending_offset or len(unit_toc))
    composite_report = None
    geometry_gpu = unit_gpu
    if header.composite_ref:
        composite_entry = entries.get((header.composite_ref, TYPE_IDS["CompositeUnit"]))
        if composite_entry is None:
            raise HD2ResourceError(
                f"Unit references CompositeUnit {header.composite_ref:016x}, but that resource is not in this package"
            )
        composite_toc, composite_gpu, _composite_stream = package.payload(composite_entry)
        composite_report = parse_composite_unit(composite_toc, composite_gpu, unit_file_id)
        geometry_gpu = composite_gpu
        streams = composite_report["streams"]
        for mesh in mesh_info["records"]:
            composite_mesh = composite_report["mesh_info"].get(mesh["mesh_id"])
            if composite_mesh is None:
                raise HD2ResourceError(f"CompositeUnit has no layout for mesh {mesh['mesh_id']:08x}")
            mesh["stream_index"] = composite_mesh["stream_index"]
            mesh["material_ids"] = composite_mesh["material_ids"]
            mesh["sections"] = composite_mesh["sections"]
    else:
        stream_end = _positive_next(header.stream_info_offset, [header.mesh_info_offset, header.ending_offset, header.materials_offset], len(unit_toc)) if header.stream_info_offset > 1 else len(unit_toc)
        streams = parse_stream_info(unit_toc, header.stream_info_offset, stream_end)
    if header.stream_info_offset == 1 and not header.composite_ref:
        raise HD2ResourceError("Unit StreamInfoOffset is the external-stream sentinel but CompositeRef is zero")
    material_by_section = {item["section_id"]: f"{item['material_id']:016x}" for item in materials}
    for mesh in mesh_info["records"]:
        for section in mesh["sections"]:
            slot_id = section.get("material_slot_id")
            section["material_resource_id"] = material_by_section.get(slot_id)
    geometry = []
    geometry_warnings = []
    for mesh in mesh_info["records"]:
        decoded, warnings = _decode_mesh_geometry(mesh, streams, geometry_gpu)
        geometry.append(decoded)
        geometry_warnings.extend(warnings)
    bones_entry = entries.get((header.bones_ref, TYPE_IDS["Bones"])) if header.bones_ref else None
    bones_summary = None
    if bones_entry:
        bones_toc, _, _ = package.payload(bones_entry)
        bones_summary = parse_bones_resource(bones_toc)
    warnings = []
    warnings.extend(transform_info.get("warnings", []))
    warnings.extend(bone_info.get("warnings", []))
    warnings.extend(streams.get("warnings", []))
    warnings.extend(geometry_warnings)
    for mesh in mesh_info["records"]:
        if transform_info["count"] and mesh["transform_index"] >= transform_info["count"]:
            warnings.append(
                f"Mesh {mesh['mesh_id']:08x} TransformIndex {mesh['transform_index']} is outside TransformInfo count {transform_info['count']}"
            )
        if mesh["lod_index"] >= bone_info["count"]:
            warnings.append(
                f"Mesh {mesh['mesh_id']:08x} LOD index {mesh['lod_index']} is outside BoneInfo count {bone_info['count']}"
            )
        for section in mesh["sections"]:
            slot_id = section.get("material_slot_id")
            if slot_id is not None and section.get("material_resource_id") is None:
                warnings.append(
                    f"Mesh {mesh['mesh_id']:08x} section {section['index']} material slot {slot_id} is not in the Unit material table"
                )
    for bone_record in bone_info["records"]:
        for real_index in bone_record["real_indices"]:
            if transform_info["count"] and real_index >= transform_info["count"]:
                warnings.append(
                    f"BoneInfo[{bone_record['index']}] RealIndices contains {real_index}, outside TransformInfo count {transform_info['count']}"
                )
        for remap_index, remap in enumerate(bone_record["remaps"]):
            for palette_index in remap:
                if palette_index >= len(bone_record["real_indices"]):
                    warnings.append(
                        f"BoneInfo[{bone_record['index']}] remap {remap_index} references RealIndices slot {palette_index}, outside {len(bone_record['real_indices'])} entries"
                    )
    skinning_warnings = resolve_unit_skinning(
        transform_info, bone_info, bones_summary, mesh_info, geometry
    )
    warnings.extend(skinning_warnings)
    if not header.bones_ref:
        warnings.append("Unit has no BonesRef")
    if header.transform_info_offset == 0:
        warnings.append("Unit has no TransformInfo section")
    if header.composite_ref:
        geometry_source = "CompositeUnit"
    else:
        geometry_source = "Unit GPU payload"
    return {
        "schema": "SourceModelReader-HD2Unit1",
        "package": {"name": package_label, "container": source_kind},
        "unit": {
            "file_id": f"{unit_file_id:016x}",
            "resource_version": header.version,
            "references": {
                "bones": f"{header.bones_ref:016x}" if header.bones_ref else None,
                "composite_unit": f"{header.composite_ref:016x}" if header.composite_ref else None,
                "state_machine": f"{header.state_machine_ref:016x}" if header.state_machine_ref else None,
            },
            "payload_bytes": {"toc": len(unit_toc), "gpu": len(unit_gpu), "geometry_gpu": len(geometry_gpu)},
            "geometry_source": geometry_source,
        },
        "summary": {
            "status": "warning" if warnings else "ok",
            "transform_count": transform_info["count"],
            "bone_info_count": bone_info["count"],
            "stream_count": streams["count"],
            "mesh_count": mesh_info["count"],
            "decoded_mesh_count": len(geometry),
            "vertex_count": sum(mesh["vertex_count"] for mesh in geometry),
            "index_count": sum(mesh["index_count"] for mesh in geometry),
            "triangle_count": sum(mesh["triangle_count"] for mesh in geometry),
        },
        "materials": materials,
        "bones_resource": ({"file_id": f"{header.bones_ref:016x}", **bones_summary} if bones_summary else None),
        "transform_info": transform_info,
        "bone_info": bone_info,
        "stream_info": streams,
        "mesh_info": mesh_info,
        "composite_unit": composite_report,
        "geometry": geometry,
        "warnings": warnings,
        "limitations": [
            "Vertex-local bone indices are resolved through the active LOD BoneInfo palette and section material Remap into TransformInfo indices; raw bind-pose matrices remain uninterpreted.",
            "Format 29 is decoded as three normalized bone weights only when the component type is bone_weight; other Format 29 semantics remain raw.",
            "Transform matrices are emitted as raw stored floats; matrix convention and bind-pose semantics are not changed or inferred.",
        ],
    }


def format_hd2_unit_text(report: dict) -> str:
    unit = report["unit"]
    summary = report["summary"]
    lines = [
        "Source Model Reader 0.8.3-alpha - HD2 Unit inspection",
        f"Package: {report['package']['name']} | Unit: {unit['file_id']} | Container: {report['package']['container']}",
        f"Geometry source: {unit['geometry_source']} | Geometry GPU: {unit['payload_bytes']['geometry_gpu']:,} bytes",
        f"Status: {summary['status'].upper()} | Transforms: {summary['transform_count']} | BoneInfo: {summary['bone_info_count']} | Streams: {summary['stream_count']} | Meshes: {summary['mesh_count']}",
        f"Decoded: {summary['vertex_count']:,} vertices | {summary['index_count']:,} indices | {summary['triangle_count']:,} triangles",
    ]
    for mesh in report["geometry"]:
        lines.append(
            f"  Mesh {mesh['mesh_id']:08x} | LOD={mesh['lod_index']} | Stream={mesh['stream_index']} | "
            f"Vertices={mesh['vertex_count']:,} | Indices={mesh['index_count']:,} | Sections={len(mesh['sections'])}"
        )
    if report["warnings"]:
        lines.append("Warnings:")
        lines.extend(f"  - {warning}" for warning in report["warnings"])
    return "\n".join(lines)
