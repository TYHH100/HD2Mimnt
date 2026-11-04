"""Read-only Helldivers 2 Material resource parser.

This module extracts the common binary fields observed in the public AQ and
Community SDK parsers. It preserves raw payloads and does not infer render mode.
"""
from __future__ import annotations

import json
import math
import struct
from pathlib import Path
from typing import Optional

from hd2_resources import (
    HD2ResourceError,
    ResourcePackage,
    TYPE_IDS,
    load_resource_package,
)

MATERIAL_HEADER_SIZE = 136
MATERIAL_TEXTURE_TYPE_SIZE = 4
MATERIAL_TEXTURE_ID_SIZE = 8
MATERIAL_VARIABLE_DESCRIPTOR_SIZE = 20
MAX_MATERIAL_BYTES = 64 * 1024 * 1024
MAX_MATERIAL_TEXTURES = 4096
MAX_MATERIAL_VARIABLES = 16384

VARIABLE_CLASS_NAMES = {
    0: "Scalar",
    1: "Vector2",
    2: "Vector3",
    3: "Vector4",
    12: "Other",
}


def _range(data: bytes, offset: int, size: int, label: str) -> None:
    if offset < 0 or size < 0 or offset > len(data) or size > len(data) - offset:
        raise HD2ResourceError(
            f"{label}: data range is out of bounds "
            f"(offset={offset}, size={size}, file={len(data)})"
        )


def _u32(data: bytes, offset: int, label: str) -> int:
    _range(data, offset, 4, label)
    return struct.unpack_from("<I", data, offset)[0]


def _u64(data: bytes, offset: int, label: str) -> int:
    _range(data, offset, 8, label)
    return struct.unpack_from("<Q", data, offset)[0]


def parse_material_resource(data: bytes) -> dict:
    """Parse one Material TOC payload without mutating or discarding source bytes.

    Shader-variable previews are intentionally limited to one scalar/vector
    read for classes 0..3, matching the conservative behavior documented in
    the inspected public parsers. Array/stride semantics remain raw metadata.
    """
    if not isinstance(data, bytes):
        data = bytes(data)
    if len(data) > MAX_MATERIAL_BYTES:
        raise HD2ResourceError(
            f"Material payload exceeds safety limit: {len(data)} > {MAX_MATERIAL_BYTES}"
        )
    _range(data, 0, MATERIAL_HEADER_SIZE, "Material fixed header")

    parent_id = _u64(data, 24, "ParentMaterialID")
    end_offset = _u32(data, 12, "EndOffset")
    num_textures = _u32(data, 64, "NumTextures")
    num_variables = _u32(data, 104, "NumVariables")
    variable_data_size = _u32(data, 120, "VariableDataSize")

    if num_textures > MAX_MATERIAL_TEXTURES:
        raise HD2ResourceError(
            f"Material texture count exceeds safety limit: {num_textures}"
        )
    if num_variables > MAX_MATERIAL_VARIABLES:
        raise HD2ResourceError(
            f"Material shader-variable count exceeds safety limit: {num_variables}"
        )

    texture_types_offset = MATERIAL_HEADER_SIZE
    texture_types_size = num_textures * MATERIAL_TEXTURE_TYPE_SIZE
    _range(data, texture_types_offset, texture_types_size, "Material texture type table")
    texture_ids_offset = texture_types_offset + texture_types_size
    texture_ids_size = num_textures * MATERIAL_TEXTURE_ID_SIZE
    _range(data, texture_ids_offset, texture_ids_size, "Material texture ID table")
    variables_offset = texture_ids_offset + texture_ids_size
    variables_size = num_variables * MATERIAL_VARIABLE_DESCRIPTOR_SIZE
    _range(data, variables_offset, variables_size, "Material shader-variable descriptors")
    value_payload_offset = variables_offset + variables_size
    value_payload = data[value_payload_offset:]

    texture_nodes = []
    for index in range(num_textures):
        type_offset = texture_types_offset + index * MATERIAL_TEXTURE_TYPE_SIZE
        id_offset = texture_ids_offset + index * MATERIAL_TEXTURE_ID_SIZE
        texture_type_id = _u32(data, type_offset, f"TextureTypes[{index}]")
        texture_id = _u64(data, id_offset, f"TextureIDs[{index}]")
        texture_nodes.append({
            "index": index,
            "texture_type_id": texture_type_id,
            "texture_type_id_hex": f"0x{texture_type_id:08x}",
            "texture_id": f"{texture_id:016x}",
            "texture_id_value": texture_id,
        })

    variables = []
    warnings = []
    for index in range(num_variables):
        offset = variables_offset + index * MATERIAL_VARIABLE_DESCRIPTOR_SIZE
        klass, elements, variable_id, value_offset, element_stride = struct.unpack_from(
            "<5I", data, offset
        )
        component_count = klass + 1 if klass in (0, 1, 2, 3) else None
        preview = None
        preview_status = "unsupported_class"
        if component_count is not None:
            preview_size = component_count * 4
            if value_offset > len(value_payload) or preview_size > len(value_payload) - value_offset:
                preview_status = "value_range_out_of_bounds"
            else:
                values = list(struct.unpack_from(
                    "<" + "f" * component_count,
                    value_payload,
                    value_offset,
                ))
                if all(math.isfinite(value) for value in values):
                    preview = values
                    preview_status = "first_vector_preview"
                else:
                    preview_status = "non_finite_float_preview"

        variables.append({
            "index": index,
            "klass": klass,
            "klass_name": VARIABLE_CLASS_NAMES.get(klass, "Unknown"),
            "elements": elements,
            "variable_id": variable_id,
            "variable_id_hex": f"0x{variable_id:08x}",
            "value_offset": value_offset,
            "element_stride": element_stride,
            "raw_descriptor_hex": data[offset:offset + MATERIAL_VARIABLE_DESCRIPTOR_SIZE].hex(),
            "preview_status": preview_status,
            "first_vector_preview": preview,
        })

    if variable_data_size != len(value_payload):
        warnings.append(
            "VariableDataSize does not equal the remaining bytes after the descriptor table "
            f"(declared={variable_data_size}, available={len(value_payload)}); "
            "the raw remaining payload is preserved and the field boundary is not assumed"
        )
    for variable in variables:
        if variable["preview_status"] == "value_range_out_of_bounds":
            warnings.append(
                f"Shader variable {variable['variable_id_hex']} preview offset is outside "
                "the available variable payload"
            )

    return {
        "schema": "SourceModelReader-HD2Material1",
        "resource_size": len(data),
        "fixed_header_size": MATERIAL_HEADER_SIZE,
        "header": {
            "end_offset": end_offset,
            "parent_material_id": f"{parent_id:016x}",
            "parent_material_id_value": parent_id,
            "num_textures": num_textures,
            "num_variables": num_variables,
            "declared_variable_data_size": variable_data_size,
            "unknown_0_12_hex": data[0:12].hex(),
            "unknown_16_24_hex": data[16:24].hex(),
            "unknown_32_64_hex": data[32:64].hex(),
            "unknown_68_104_hex": data[68:104].hex(),
            "unknown_108_120_hex": data[108:120].hex(),
            "unknown_124_136_hex": data[124:136].hex(),
        },
        "layout": {
            "texture_types_offset": texture_types_offset,
            "texture_ids_offset": texture_ids_offset,
            "variable_descriptors_offset": variables_offset,
            "variable_value_payload_offset": value_payload_offset,
            "available_variable_data_size": len(value_payload),
            "variable_data_size_matches_remaining_payload": variable_data_size == len(value_payload),
        },
        "texture_nodes": texture_nodes,
        "shader_variables": variables,
        "variable_value_payload_hex": value_payload.hex(),
        "render_mode": {
            "status": "unresolved",
            "reason": "Parent aliases and shader variable names are versioned metadata; raw IDs are preserved without inferring render behavior.",
        },
        "warnings": warnings,
    }


def inspect_hd2_material(
    input_path: str | Path,
    package_name: Optional[str],
    material_id: int,
) -> dict:
    """Inspect one Material resource and resolve Texture IDs in the same package."""
    package: ResourcePackage = load_resource_package(input_path, package_name)
    key = (material_id, TYPE_IDS["Material"])
    entry = package.entry_map().get(key)
    if entry is None:
        raise HD2ResourceError(
            f"Material resource {material_id:016x} is not present in package {package.name}"
        )
    material_payload, gpu_payload, stream_payload = package.payload(entry)
    report = parse_material_resource(material_payload)
    entries = package.entry_map()
    for node in report["texture_nodes"]:
        texture_entry = entries.get((node["texture_id_value"], TYPE_IDS["Texture"]))
        node["resolved_in_package"] = texture_entry is not None
        if texture_entry is not None:
            node["texture_resource"] = {
                "file_id": f"{texture_entry.file_id:016x}",
                "toc_bytes": texture_entry.toc_size,
                "gpu_bytes": texture_entry.gpu_size,
                "stream_bytes": texture_entry.stream_size,
            }
        else:
            node["texture_resource"] = None
    report["package"] = {
        "name": package.name,
        "container": package.source_kind,
    }
    report["resource"] = {
        "file_id": f"{entry.file_id:016x}",
        "type_id": f"{entry.type_id:016x}",
        "toc_bytes": len(material_payload),
        "gpu_bytes": len(gpu_payload),
        "stream_bytes": len(stream_payload),
    }
    report["summary"] = {
        "status": "warning" if report["warnings"] or any(
            not node["resolved_in_package"] for node in report["texture_nodes"]
        ) else "ok",
        "texture_count": len(report["texture_nodes"]),
        "texture_dependencies_found_in_package": sum(
            1 for node in report["texture_nodes"] if node["resolved_in_package"]
        ),
        "texture_dependencies_missing_from_package": sum(
            1 for node in report["texture_nodes"] if not node["resolved_in_package"]
        ),
        "shader_variable_count": len(report["shader_variables"]),
    }
    return report


def format_hd2_material_text(report: dict) -> str:
    package = report["package"]
    resource = report["resource"]
    header = report["header"]
    summary = report["summary"]
    lines = [
        "Source Model Reader - HD2 Material inspection",
        f"Package: {package['name']} | Container: {package['container']}",
        f"Material: {resource['file_id']} | TOC bytes: {resource['toc_bytes']}",
        f"ParentMaterialID: 0x{header['parent_material_id']} (raw; render family unresolved)",
        f"Textures: {summary['texture_count']} | Resolved in package: {summary['texture_dependencies_found_in_package']} | Missing from package: {summary['texture_dependencies_missing_from_package']}",
        f"Shader variables: {summary['shader_variable_count']}",
        "Texture nodes:",
    ]
    for node in report["texture_nodes"]:
        state = "found" if node["resolved_in_package"] else "not in this package"
        lines.append(
            f"  [{node['index']}] type={node['texture_type_id_hex']} "
            f"texture={node['texture_id']} ({state})"
        )
    lines.append("Shader-variable descriptors:")
    for variable in report["shader_variables"]:
        preview = variable["first_vector_preview"]
        preview_text = f" values={preview}" if preview is not None else ""
        lines.append(
            f"  [{variable['index']}] id={variable['variable_id_hex']} "
            f"class={variable['klass_name']} elements={variable['elements']} "
            f"offset={variable['value_offset']} stride={variable['element_stride']} "
            f"preview={variable['preview_status']}{preview_text}"
        )
    for warning in report["warnings"]:
        lines.append(f"WARNING: {warning}")
    lines.append(
        "Note: this command extracts raw material structure only; it does not infer "
        "alpha blending, cutout, culling or final shader behavior."
    )
    return "\n".join(lines)


def parse_resource_id(value: str) -> int:
    """Accept hexadecimal (with or without 0x) or decimal resource IDs."""
    text = value.strip().lower()
    if not text:
        raise ValueError("Resource ID cannot be empty")
    if text.startswith("0x"):
        return int(text, 16)
    if any(character in "abcdef" for character in text):
        return int(text, 16)
    return int(text, 10)
