#!/usr/bin/env python3
"""Dump the raw structure of one HD2 Bones resource.

`parse_bones_resource` reads the header, the hash array and the name strings,
and skips a small float array without exposing it. This probe reads the same
payload bytes and reports every region, including whatever is left after the
names, so the roadmap can decide whether the Bones resource is an independent
second source for bind transforms and LOD distances, or only a name table.

Read-only: nothing here changes the parser, and the payload is never modified.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from hd2_resources import TYPE_IDS, parse_toc_package  # noqa: E402
from hd2_unit import _read_package_from_game_data, parse_unit_header  # noqa: E402

MAX_STRINGS = 100_000
SKIP_VALUES = (0.0, 1.0)


def _read_float_array(data: bytes, offset: int, count: int) -> tuple[list[float], int]:
    if count <= 0 or offset + count * 4 > len(data):
        return [], offset
    return list(struct.unpack_from(f"<{count}f", data, offset)), offset + 4 * count


def _read_uint_array(data: bytes, offset: int, count: int) -> tuple[list[int], int]:
    if count <= 0 or offset + count * 4 > len(data):
        return [], offset
    return list(struct.unpack_from(f"<{count}I", data, offset)), offset + 4 * count


def _read_strings(data: bytes, offset: int, limit: int) -> tuple[list[str], int]:
    names: list[str] = []
    cursor = offset
    while cursor < len(data) and len(names) < limit:
        end = data.find(b"\0", cursor)
        if end < 0:
            break
        chunk = data[cursor:end]
        if not chunk:
            break
        names.append(chunk.decode("utf-8", "replace"))
        cursor = end + 1
    return names, cursor


def _ascii_window(data: bytes, limit: int = 96) -> str:
    return "".join(chr(byte) if 32 <= byte < 127 else "." for byte in data[:limit])


def _signature_hits(data: bytes, signatures: dict[bytes, str]) -> list[dict[str, Any]]:
    """Report where known float32 values from the Unit reappear inside Bones."""
    hits: list[dict[str, Any]] = []
    for needle, label in signatures.items():
        offsets: list[int] = []
        start = 0
        while True:
            index = data.find(needle, start)
            if index < 0:
                break
            offsets.append(index)
            start = index + 1
            if len(offsets) >= 8:
                break
        if offsets:
            hits.append({"value_label": label, "bytes": needle.hex(), "offsets": offsets})
    return hits


def build_signatures(unit_json: Path | None, limit: int = 24) -> dict[bytes, str]:
    """Collect float32 values from the Unit as a search dictionary."""
    if unit_json is None or not unit_json.is_file():
        return {}
    report = json.loads(unit_json.read_text(encoding="utf-8"))
    matrices: list[tuple[str, list[Any]]] = []
    for row in (report.get("transform_info") or {}).get("transforms") or []:
        if row.get("matrix_4x4_raw"):
            matrices.append((f"transform[{row.get('index')}]", row["matrix_4x4_raw"]))
    for row in (report.get("bone_info") or {}).get("records") or []:
        for key, value in row.items():
            if isinstance(value, list) and len(value) in (12, 16) and key.endswith("matrix"):
                matrices.append((f"bone_info[{row.get('index')}].{key}", value))
    signatures: dict[bytes, str] = {}
    for label, values in matrices:
        for position, value in enumerate(values):
            if len(signatures) >= limit:
                return signatures
            if not isinstance(value, (int, float)) or isinstance(value, bool):
                continue
            if float(value) in SKIP_VALUES:
                continue
            try:
                packed = struct.pack("<f", float(value))
            except (OverflowError, ValueError):
                continue
            signatures.setdefault(packed, f"{label}[{position}]={float(value):.9g}")
    return signatures


def dump_structure(data: bytes, signatures: dict[bytes, str]) -> dict[str, Any]:
    if len(data) < 8:
        raise ValueError("Bones payload is shorter than its 8-byte header")
    declared_names, declared_lods = struct.unpack_from("<2I", data, 0)
    if declared_names > 1_000_000 or declared_lods > 1024:
        raise ValueError("Bones header counts exceed safety limits")

    regions: list[dict[str, Any]] = [{
        "region": "header", "offset": 0, "size": 8,
        "layout": "uint32 name_count, uint32 lod_count",
    }]
    offset = 8
    lod_floats, offset = _read_float_array(data, offset, declared_lods)
    regions.append({
        "region": "lod_float_array", "offset": 8, "size": offset - 8,
        "layout": f"{declared_lods} float32 (skipped by the current parser)",
    })
    hashes, hash_end = _read_uint_array(data, offset, declared_names)
    regions.append({
        "region": "hash_array", "offset": offset, "size": hash_end - offset,
        "layout": f"{declared_names} uint32",
    })
    offset = hash_end
    lod_levels, offset = _read_uint_array(data, offset, declared_lods)
    regions.append({
        "region": "lod_levels", "offset": hash_end, "size": offset - hash_end,
        "layout": f"{declared_lods} uint32",
    })
    names, names_end = _read_strings(data, offset, MAX_STRINGS)
    regions.append({
        "region": "names", "offset": offset, "size": names_end - offset,
        "layout": "NUL-terminated strings",
    })
    tail = data[names_end:]
    return {
        "payload_bytes": len(data),
        "declared_name_count": declared_names,
        "declared_lod_count": declared_lods,
        "regions": regions,
        "lod_float_array": lod_floats,
        "hash_count": len(hashes),
        "lod_levels": lod_levels,
        "name_count_read": len(names),
        "first_names": names[:12],
        "bytes_after_names": len(tail),
        "tail_alignment_mod4": len(tail) % 4,
        "tail_hex": tail[:128].hex(),
        "tail_ascii": _ascii_window(tail),
        "unit_float_signature_hits": _signature_hits(data, signatures),
    }


def render_markdown(report: dict[str, Any]) -> str:
    dump = report["dump"]
    lines = [
        "# HD2 Bones Resource Raw Structure",
        "",
        f"- Package: {report.get('package_id')}",
        f"- Unit: {report.get('unit_id')}",
        f"- Bones resource: {report.get('bones_id')}",
        f"- TOC payload bytes: {dump['payload_bytes']}",
        f"- GPU payload bytes: {report.get('gpu_bytes')}",
        f"- Stream payload bytes: {report.get('stream_bytes')}",
        f"- Declared names: {dump['declared_name_count']} / read: {dump['name_count_read']}",
        f"- Declared LOD count: {dump['declared_lod_count']}",
        f"- Bytes left after the name block: {dump['bytes_after_names']}",
        "",
        "## Regions, in the order the current parser assumes",
        "",
        "| Region | Offset | Size | Layout |",
        "|---|---:|---:|---|",
    ]
    for region in dump["regions"]:
        lines.append("| {} | {} | {} | {} |".format(
            region["region"], region["offset"], region["size"], region["layout"]))
    lines.extend([
        "",
        f"- Skipped float array: {', '.join(f'{value:.6g}' for value in dump['lod_float_array']) or '(empty)'}",
        f"- LOD levels: {', '.join(str(value) for value in dump['lod_levels']) or '(empty)'}",
        f"- First names: {', '.join(dump['first_names']) or '(none)'}",
        "",
        "## Tail after the name block",
        "",
        f"- Size: {dump['bytes_after_names']} (size mod 4 = {dump['tail_alignment_mod4']})",
        f"- Hex: `{dump['tail_hex'] or '(empty)'}`",
        f"- ASCII: `{dump['tail_ascii'] or '(empty)'}`",
        "",
        "## Unit float values found inside the Bones payload",
        "",
        "A hit means a float32 stored in the Unit also appears in this resource at the",
        "reported offset; it is a starting point for locating a second copy of transforms,",
        "not proof that the Bones resource stores them.",
        "",
    ])
    hits = dump["unit_float_signature_hits"]
    if hits:
        lines.extend([
            "| Unit value | Bytes | Offsets |",
            "|---|---|---|",
        ])
        for hit in hits:
            lines.append("| {} | {} | {} |".format(
                hit["value_label"], hit["bytes"],
                ", ".join(str(offset) for offset in hit["offsets"])))
    else:
        lines.append("| (no Unit float32 value was found in the Bones payload) | - | - |")
    lines.extend([
        "",
        "## Interpretation limits",
        "",
        "- Region boundaries follow the layout assumed by `parse_bones_resource`; a tail of",
        "  zero bytes means no further region was found after the names.",
        "- Hits are individual float32 matches. Repeated values such as a shared bone offset",
        "  can coincide without proving a matrix layout.",
        "- The payload is read without modification; nothing here rewrites or re-encodes data.",
        "",
    ])
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help="HD2 game data directory")
    parser.add_argument("--package", required=True, help="Package/archive ID")
    parser.add_argument("--unit", help="Unit resource FileID (hexadecimal)")
    parser.add_argument("--bones", help="Bones resource FileID (hexadecimal), instead of --unit")
    parser.add_argument("--unit-json", type=Path,
                        help="Unit JSON used to build the float32 search dictionary")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    if not args.unit and not args.bones:
        parser.error("either --unit or --bones is required")

    try:
        toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(
            Path(args.input), args.package)
        package = parse_toc_package(Path(args.package).name, toc_data, gpu_data,
                                    stream_data, source_kind)
        entries = package.entry_map()
        unit_id = None
        bones_id = None
        if args.bones:
            bones_id = int(args.bones.lower().removeprefix("0x"), 16)
        else:
            unit_id = int(args.unit.lower().removeprefix("0x"), 16)
            unit_entry = entries.get((unit_id, TYPE_IDS["Unit"]))
            if unit_entry is None:
                parser.error(f"Unit {unit_id:016x} not found in package {args.package}")
            unit_toc, _unit_gpu, _unit_stream = package.payload(unit_entry)
            bones_id = parse_unit_header(unit_toc).bones_ref
            if not bones_id:
                parser.error(f"Unit {unit_id:016x} has no Bones reference")
        bones_entry = entries.get((bones_id, TYPE_IDS["Bones"]))
        if bones_entry is None:
            parser.error(f"Bones resource {bones_id:016x} not found in package {args.package}")
        bones_toc, bones_gpu, bones_stream = package.payload(bones_entry)
        dump = dump_structure(bones_toc, build_signatures(args.unit_json))
    except (OSError, ValueError, TypeError, KeyError, struct.error) as exc:
        parser.error(f"Bones structure dump failed: {exc}")

    report = {
        "schema": "SourceModelReader-HD2BonesStructure1",
        "package_id": package.name,
        "unit_id": f"{unit_id:016x}" if unit_id else None,
        "bones_id": f"{bones_id:016x}",
        "gpu_bytes": len(bones_gpu),
        "stream_bytes": len(bones_stream),
        "dump": dump,
    }
    markdown = render_markdown(report)

    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")

    print(f"Bones {bones_id:016x}: {dump['payload_bytes']} TOC bytes, "
          f"{len(bones_gpu)} GPU bytes, {len(bones_stream)} stream bytes, "
          f"tail after names {dump['bytes_after_names']}, "
          f"float hits {len(dump['unit_float_signature_hits'])}")
    if not args.output_json and not args.output_markdown:
        print(markdown)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
