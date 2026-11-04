"""Read-only Helldivers 2 Texture resource inspection for SLIM packages."""
from __future__ import annotations

import hashlib
import struct
from pathlib import Path
from typing import Optional

from hd2_resources import (
    HD2ResourceError,
    MAX_TOC_ENTRIES,
    MAX_TOC_TYPES,
    SlimBundleIndex,
    TOC_MAGIC,
    TYPE_IDS,
)

TEXTURE_FIXED_FIELDS_SIZE = 12
TEXTURE_MIPMAP_COUNT = 15
TEXTURE_MIPMAP_ENTRY_SIZE = 12
DDS_HEADER_SIZE = 148
TEXTURE_TOC_SIZE = TEXTURE_FIXED_FIELDS_SIZE + TEXTURE_MIPMAP_COUNT * TEXTURE_MIPMAP_ENTRY_SIZE + DDS_HEADER_SIZE
MAX_TEXTURE_TOC_BYTES = 4 * 1024 * 1024
MAX_TEXTURE_PAYLOAD_BYTES = 512 * 1024 * 1024
ENTRY_ROW_SIZE = 80

# DXGI values relevant to common game textures. Unknown values are preserved numerically.
DXGI_FORMAT_NAMES = {
    0: "UNKNOWN",
    27: "R8G8B8A8_TYPELESS",
    28: "R8G8B8A8_UNORM",
    29: "R8G8B8A8_UNORM_SRGB",
    30: "R8G8B8A8_UINT",
    31: "R8G8B8A8_SNORM",
    48: "R8G8_TYPELESS",
    49: "R8G8_UNORM",
    51: "R8G8_SNORM",
    61: "R8_UNORM",
    65: "A8_UNORM",
    70: "BC1_TYPELESS",
    71: "BC1_UNORM",
    72: "BC1_UNORM_SRGB",
    73: "BC2_TYPELESS",
    74: "BC2_UNORM",
    75: "BC2_UNORM_SRGB",
    76: "BC3_TYPELESS",
    77: "BC3_UNORM",
    78: "BC3_UNORM_SRGB",
    79: "BC4_TYPELESS",
    80: "BC4_UNORM",
    81: "BC4_SNORM",
    82: "BC5_TYPELESS",
    83: "BC5_UNORM",
    84: "BC5_SNORM",
    94: "BC6H_TYPELESS",
    95: "BC6H_UF16",
    96: "BC6H_SF16",
    97: "BC7_TYPELESS",
    98: "BC7_UNORM",
    99: "BC7_UNORM_SRGB",
}


def _check_range(data: bytes, offset: int, size: int, label: str) -> None:
    if offset < 0 or size < 0 or offset > len(data) or size > len(data) - offset:
        raise HD2ResourceError(
            f"{label}: data range is out of bounds "
            f"(offset={offset}, size={size}, available={len(data)})"
        )


def _read_slim_texture_payload(
    data_dir: str | Path,
    package_name: str,
    texture_id: int,
) -> tuple[dict, bytes, bytes, bytes]:
    """Read one Texture resource by range; never reconstructs whole package sidecars."""
    root = Path(data_dir)
    if not root.is_dir():
        raise HD2ResourceError("hd2-texture currently requires an HD2 SLIM data directory")
    if isinstance(texture_id, bool) or not isinstance(texture_id, int) or not 0 < texture_id <= 0xFFFFFFFFFFFFFFFF:
        raise HD2ResourceError("Texture FileID must be a positive unsigned 64-bit integer")

    package_name = Path(package_name).name
    index = SlimBundleIndex(root)
    if package_name not in index.packages:
        raise HD2ResourceError(f"SLIM package not found: {package_name}")
    package = index.packages[package_name]
    if package.size < 72:
        raise HD2ResourceError(f"{package_name}: package is shorter than the 72-byte TOC header")

    header = index.reconstruct_range(package_name, 0, 72)
    magic, num_types, num_files, _unknown = struct.unpack_from("<4I", header, 0)
    if magic != TOC_MAGIC:
        raise HD2ResourceError(
            f"{package_name}: TOC magic is {magic:#010x}, expected {TOC_MAGIC:#010x}"
        )
    if num_types > MAX_TOC_TYPES or num_files > MAX_TOC_ENTRIES:
        raise HD2ResourceError(f"{package_name}: TOC counts exceed safety limits")

    type_table_size = num_types * 32
    entries_offset = 72 + type_table_size
    entries_size = num_files * ENTRY_ROW_SIZE
    if entries_offset > package.size or entries_size > package.size - entries_offset:
        raise HD2ResourceError(f"{package_name}: TOC entry table exceeds declared package size")

    texture_type_id = TYPE_IDS["Texture"]
    found_row: Optional[tuple[int, bytes]] = None
    chunk_rows = 2048
    for first_row in range(0, num_files, chunk_rows):
        row_count = min(chunk_rows, num_files - first_row)
        offset = entries_offset + first_row * ENTRY_ROW_SIZE
        rows = index.reconstruct_range(package_name, offset, row_count * ENTRY_ROW_SIZE)
        for local_index in range(row_count):
            row_offset = local_index * ENTRY_ROW_SIZE
            file_id, type_id = struct.unpack_from("<QQ", rows, row_offset)
            if file_id == texture_id and type_id == texture_type_id:
                if found_row is not None:
                    raise HD2ResourceError(
                        f"{package_name}: duplicate Texture FileID {texture_id:016x}"
                    )
                absolute_index = first_row + local_index
                found_row = (absolute_index, rows[row_offset:row_offset + ENTRY_ROW_SIZE])

    if found_row is None:
        raise HD2ResourceError(
            f"Texture resource {texture_id:016x} is not present in package {package_name}"
        )

    entry_index, entry_row = found_row
    values = struct.unpack("<7Q6I", entry_row)
    file_id, type_id = values[0], values[1]
    toc_offset, stream_offset, gpu_offset = values[2], values[3], values[4]
    toc_size, stream_size, gpu_size = values[7], values[8], values[9]
    declared_entry_index = values[12]
    expected_entry_index = entry_index + 1
    if declared_entry_index != expected_entry_index:
        raise HD2ResourceError(
            f"{package_name}: Texture entry_index is not the expected 1-based TOC sequence "
            f"(row={entry_index}, expected={expected_entry_index}, stored={declared_entry_index})"
        )
    if toc_size < TEXTURE_TOC_SIZE or toc_size > MAX_TEXTURE_TOC_BYTES:
        raise HD2ResourceError(
            f"Texture TOC size is outside supported bounds: {toc_size}"
        )
    if gpu_size + stream_size > MAX_TEXTURE_PAYLOAD_BYTES:
        raise HD2ResourceError(
            "Combined Texture GPU/Stream payload exceeds the 512 MiB safety limit"
        )

    toc_data = index.reconstruct_range(package_name, toc_offset, toc_size)
    gpu_name = package_name + ".gpu_resources"
    stream_name = package_name + ".stream"

    if gpu_size and gpu_name not in index.packages:
        raise HD2ResourceError(
            f"{package_name}: Texture declares {gpu_size} GPU bytes but {gpu_name} is missing"
        )
    if stream_size and stream_name not in index.packages:
        raise HD2ResourceError(
            f"{package_name}: Texture declares {stream_size} Stream bytes but {stream_name} is missing"
        )

    gpu_data = (
        index.reconstruct_range(gpu_name, gpu_offset, gpu_size)
        if gpu_size else b""
    )
    stream_data = (
        index.reconstruct_range(stream_name, stream_offset, stream_size)
        if stream_size else b""
    )

    resource = {
        "file_id": f"{file_id:016x}",
        "type_id": f"{type_id:016x}",
        "entry_index": declared_entry_index,
        "toc_row_index": entry_index,
        "toc_offset": toc_offset,
        "stream_offset": stream_offset,
        "gpu_offset": gpu_offset,
        "toc_bytes": toc_size,
        "stream_bytes": stream_size,
        "gpu_bytes": gpu_size,
    }
    return resource, toc_data, gpu_data, stream_data


def parse_texture_resource(toc_data: bytes, gpu_data: bytes = b"", stream_data: bytes = b"") -> dict:
    """Parse the fixed Texture TOC/DDS metadata and preserve payload distinctions."""
    if not isinstance(toc_data, bytes):
        toc_data = bytes(toc_data)
    if not isinstance(gpu_data, bytes):
        gpu_data = bytes(gpu_data)
    if not isinstance(stream_data, bytes):
        stream_data = bytes(stream_data)

    if len(toc_data) > MAX_TEXTURE_TOC_BYTES:
        raise HD2ResourceError(
            f"Texture TOC payload exceeds safety limit: {len(toc_data)}"
        )
    if len(toc_data) < TEXTURE_TOC_SIZE:
        raise HD2ResourceError(
            f"Texture TOC payload is truncated: {len(toc_data)} bytes; "
            f"at least {TEXTURE_TOC_SIZE} required"
        )
    if len(gpu_data) + len(stream_data) > MAX_TEXTURE_PAYLOAD_BYTES:
        raise HD2ResourceError("Combined Texture GPU/Stream payload exceeds safety limit")

    unknown_id, unknown_1, unknown_2 = struct.unpack_from("<3I", toc_data, 0)
    mipmaps = []
    mipmap_offset = TEXTURE_FIXED_FIELDS_SIZE
    for index in range(TEXTURE_MIPMAP_COUNT):
        offset = mipmap_offset + index * TEXTURE_MIPMAP_ENTRY_SIZE
        start, bytes_left, height, width = struct.unpack_from("<IIHH", toc_data, offset)
        mipmaps.append({
            "index": index,
            "start": start,
            "bytes_left": bytes_left,
            "height": height,
            "width": width,
            "has_nonzero_fields": bool(start or bytes_left or height or width),
        })

    dds_offset = TEXTURE_FIXED_FIELDS_SIZE + TEXTURE_MIPMAP_COUNT * TEXTURE_MIPMAP_ENTRY_SIZE
    dds_header = toc_data[dds_offset:dds_offset + DDS_HEADER_SIZE]
    dds_magic = dds_header[0:4]
    dds_header_size = struct.unpack_from("<I", dds_header, 4)[0]
    dds_flags = struct.unpack_from("<I", dds_header, 8)[0]
    height = struct.unpack_from("<I", dds_header, 12)[0]
    width = struct.unpack_from("<I", dds_header, 16)[0]
    pitch_or_linear_size = struct.unpack_from("<I", dds_header, 20)[0]
    depth = struct.unpack_from("<I", dds_header, 24)[0]
    mip_count = struct.unpack_from("<I", dds_header, 28)[0]
    pixel_format_size = struct.unpack_from("<I", dds_header, 76)[0]
    four_cc = dds_header[84:88]
    dxgi_format = struct.unpack_from("<I", dds_header, 128)[0]
    resource_dimension = struct.unpack_from("<I", dds_header, 132)[0]
    misc_flag = struct.unpack_from("<I", dds_header, 136)[0]
    array_size = struct.unpack_from("<I", dds_header, 140)[0]
    misc_flags_2 = struct.unpack_from("<I", dds_header, 144)[0]

    warnings = []
    notes = []
    if dds_header_size != 124:
        warnings.append(f"DDS header size is {dds_header_size}, expected 124")
    if pixel_format_size != 32:
        warnings.append(f"DDS pixel-format structure size is {pixel_format_size}, expected 32")
    if dds_magic != b"DDS ":
        warnings.append(f"DDS magic is {dds_magic!r}, expected b'DDS '")
    if four_cc != b"DX10":
        warnings.append(
            f"DDS FourCC is {four_cc!r}, not DX10; public SDK parsers expect a DX10 header"
        )
    if not width or not height:
        warnings.append(f"DDS dimensions are zero or incomplete ({width}x{height})")
    if mip_count > TEXTURE_MIPMAP_COUNT:
        warnings.append(
            f"DDS declares {mip_count} mip levels but the Texture TOC has only "
            f"{TEXTURE_MIPMAP_COUNT} mipmap records"
        )
    if four_cc == b"DX10" and array_size == 0:
        warnings.append("DX10 ArraySize is zero")
    if len(gpu_data) and len(stream_data):
        notes.append(
            "Both GPU and Stream payloads are present. The public SDK read path selects "
            "Stream when non-empty; both byte sequences are preserved in this report."
        )

    if stream_data:
        selected_payload = stream_data
        payload_source = "stream"
    elif gpu_data:
        selected_payload = gpu_data
        payload_source = "gpu"
    else:
        selected_payload = b""
        payload_source = "none"
        warnings.append("Texture has no GPU or Stream payload bytes")

    def payload_info(data: bytes) -> dict:
        return {
            "size": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
        }

    format_name = DXGI_FORMAT_NAMES.get(dxgi_format, f"UNKNOWN_DXGI_FORMAT_{dxgi_format}")
    return {
        "schema": "SourceModelReader-HD2Texture1",
        "texture_toc_size": len(toc_data),
        "trailing_toc_size": len(toc_data) - TEXTURE_TOC_SIZE,
        "trailing_toc_hex": toc_data[TEXTURE_TOC_SIZE:].hex(),
        "mipmap_table_hex": toc_data[
            TEXTURE_FIXED_FIELDS_SIZE:
            TEXTURE_FIXED_FIELDS_SIZE + TEXTURE_MIPMAP_COUNT * TEXTURE_MIPMAP_ENTRY_SIZE
        ].hex(),
        "header": {
            "unknown_id": unknown_id,
            "unknown_1": unknown_1,
            "unknown_2": unknown_2,
        },
        "mipmaps": mipmaps,
        "dds": {
            "header_size_bytes": DDS_HEADER_SIZE,
            "magic": dds_magic.decode("ascii", "replace"),
            "dw_size": dds_header_size,
            "flags": dds_flags,
            "width": width,
            "height": height,
            "pitch_or_linear_size": pitch_or_linear_size,
            "depth": depth,
            "mip_count": mip_count,
            "pixel_format_size": pixel_format_size,
            "four_cc": four_cc.decode("ascii", "replace"),
            "dxgi_format": dxgi_format,
            "dxgi_format_name": format_name,
            "resource_dimension": resource_dimension,
            "misc_flag": misc_flag,
            "array_size": array_size,
            "misc_flags_2": misc_flags_2,
            "raw_header_hex": dds_header.hex(),
        },
        "payloads": {
            "gpu": payload_info(gpu_data),
            "stream": payload_info(stream_data),
            "selected_source": payload_source,
            "selected": payload_info(selected_payload),
        },
        "dds_export_bytes": DDS_HEADER_SIZE + len(selected_payload),
        "notes": notes,
        "warnings": warnings,
    }


def inspect_hd2_texture(
    data_dir: str | Path, package_name: str, texture_id: int
) -> tuple[dict, bytes, bytes, bytes]:
    """Inspect one Texture in one SLIM package and return report, DDS, GPU and Stream bytes."""
    resource, toc_data, gpu_data, stream_data = _read_slim_texture_payload(
        data_dir, package_name, texture_id
    )
    parsed = parse_texture_resource(toc_data, gpu_data, stream_data)
    selected = stream_data if stream_data else gpu_data
    dds_header = toc_data[
        TEXTURE_FIXED_FIELDS_SIZE + TEXTURE_MIPMAP_COUNT * TEXTURE_MIPMAP_ENTRY_SIZE:
        TEXTURE_TOC_SIZE
    ]
    dds_bytes = dds_header + selected
    package = {
        "name": Path(package_name).name,
        "container": "slim",
    }
    warnings = list(parsed["warnings"])
    status = "warning" if warnings else "ok"
    report = {
        **parsed,
        "package": package,
        "resource": resource,
        "summary": {
            "status": status,
            "gpu_bytes": len(gpu_data),
            "stream_bytes": len(stream_data),
            "selected_payload_source": parsed["payloads"]["selected_source"],
            "selected_payload_bytes": len(selected),
        },
    }
    return report, dds_bytes, gpu_data, stream_data


def format_hd2_texture_text(report: dict) -> str:
    package = report["package"]
    resource = report["resource"]
    dds = report["dds"]
    payloads = report["payloads"]
    summary = report["summary"]
    lines = [
        "Source Model Reader - HD2 Texture inspection",
        f"Package: {package['name']} | Container: {package['container']}",
        f"Texture: {resource['file_id']} | Entry: {resource['entry_index']}",
        f"TOC={resource['toc_bytes']} GPU={resource['gpu_bytes']} Stream={resource['stream_bytes']}",
        f"DDS: {dds['width']}x{dds['height']} | {dds['dxgi_format_name']} "
        f"(DXGI={dds['dxgi_format']}) | Mips={dds['mip_count']} | ArraySize={dds['array_size']}",
        f"DDS header: magic={dds['magic']!r} | FourCC={dds['four_cc']!r} | "
        f"DX10 dimension={dds['resource_dimension']}",
        f"Payload selected by SDK-compatible rule: {payloads['selected_source']} "
        f"({payloads['selected']['size']} bytes)",
        f"GPU SHA-256: {payloads['gpu']['sha256']}",
        f"Stream SHA-256: {payloads['stream']['sha256']}",
        f"Selected SHA-256: {payloads['selected']['sha256']}",
        f"Status: {summary['status'].upper()}",
        "Mipmap table:",
    ]
    for mip in report["mipmaps"]:
        if mip["has_nonzero_fields"]:
            lines.append(
                f"  [{mip['index']:02d}] {mip['width']}x{mip['height']} "
                f"start={mip['start']} bytes_left={mip['bytes_left']}"
            )
    for note in report["notes"]:
        lines.append(f"NOTE: {note}")
    for warning in report["warnings"]:
        lines.append(f"WARNING: {warning}")
    lines.append(
        "Note: raw GPU/Stream bytes are preserved separately in the report hashes; "
        "DDS export follows the SDK's Stream-first payload selection. Alpha/transparency "
        "semantics are not inferred from the DXGI format."
    )
    return "\n".join(lines)


__all__ = [
    "DDS_HEADER_SIZE",
    "TEXTURE_TOC_SIZE",
    "format_hd2_texture_text",
    "inspect_hd2_texture",
    "parse_texture_resource",
]
