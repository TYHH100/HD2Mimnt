"""Read-only global Material census for Helldivers 2 SLIM packages.

The census reads package TOC tables and only the TOC byte ranges belonging to
Material resources. GPU and Stream sidecars are never reconstructed.
"""
from __future__ import annotations

import hashlib
import json
import struct
from collections import Counter, defaultdict
from pathlib import Path
from typing import Optional

from hd2_material import MAX_MATERIAL_BYTES, parse_material_resource
from hd2_resources import (
    HD2ResourceError,
    MAX_TOC_ENTRIES,
    MAX_TOC_TYPES,
    SIDECAR_SUFFIXES,
    SlimBundleIndex,
    TOC_MAGIC,
    TYPE_IDS,
)

TOC_HEADER_SIZE = 72
TOC_TYPE_ROW_SIZE = 32
TOC_ENTRY_ROW_SIZE = 80
ENTRY_CHUNK_ROWS = 2048
MATERIAL_BATCH_MAX_BYTES = 4 * 1024 * 1024
MATERIAL_BATCH_MAX_GAP = 64 * 1024


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _signature(value: object) -> str:
    encoded = json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode("ascii")
    return hashlib.sha256(encoded).hexdigest()[:16]


def _material_entry_rows(index: SlimBundleIndex, package_name: str):
    """Yield Material TOC entries while validating the package's TOC tables."""
    package = index.packages[package_name]
    if package.size < TOC_HEADER_SIZE:
        raise HD2ResourceError(f"{package_name}: package is shorter than the TOC header")

    header = index.reconstruct_range(package_name, 0, TOC_HEADER_SIZE)
    magic, num_types, num_files, _unknown = struct.unpack_from("<4I", header, 0)
    if magic != TOC_MAGIC:
        raise HD2ResourceError(
            f"{package_name}: TOC magic is {magic:#010x}, expected {TOC_MAGIC:#010x}"
        )
    if num_types > MAX_TOC_TYPES or num_files > MAX_TOC_ENTRIES:
        raise HD2ResourceError(
            f"{package_name}: TOC counts exceed safety limits "
            f"(types={num_types}, files={num_files})"
        )

    type_bytes = num_types * TOC_TYPE_ROW_SIZE
    entries_offset = TOC_HEADER_SIZE + type_bytes
    entries_bytes = num_files * TOC_ENTRY_ROW_SIZE
    if entries_offset > package.size or entries_bytes > package.size - entries_offset:
        raise HD2ResourceError(f"{package_name}: TOC tables exceed declared package size")

    raw_types = (
        index.reconstruct_range(package_name, TOC_HEADER_SIZE, type_bytes)
        if type_bytes else b""
    )
    declared_counts: dict[int, int] = {}
    for type_index in range(num_types):
        _unknown, type_id, count, _unknown2, _unknown3 = struct.unpack_from(
            "<QQQII", raw_types, type_index * TOC_TYPE_ROW_SIZE
        )
        if type_id in declared_counts:
            raise HD2ResourceError(
                f"{package_name}: duplicate resource type {type_id:016x}"
            )
        declared_counts[type_id] = count

    material_type_id = TYPE_IDS["Material"]
    actual_material_count = 0
    material_entries: list[dict] = []
    for first_row in range(0, num_files, ENTRY_CHUNK_ROWS):
        row_count = min(ENTRY_CHUNK_ROWS, num_files - first_row)
        chunk_offset = entries_offset + first_row * TOC_ENTRY_ROW_SIZE
        chunk = index.reconstruct_range(
            package_name, chunk_offset, row_count * TOC_ENTRY_ROW_SIZE
        )
        for row_index in range(row_count):
            row_offset = row_index * TOC_ENTRY_ROW_SIZE
            file_id, type_id, toc_offset = struct.unpack_from("<3Q", chunk, row_offset)
            if type_id != material_type_id:
                continue
            toc_size = struct.unpack_from("<I", chunk, row_offset + 56)[0]
            actual_material_count += 1
            material_entries.append({
                "file_id": file_id,
                "toc_offset": toc_offset,
                "toc_size": toc_size,
                "entry_index": first_row + row_index,
            })

    declared_material_count = declared_counts.get(material_type_id, 0)
    warnings = []
    if declared_material_count != actual_material_count:
        warnings.append(
            f"Material type table declares {declared_material_count} entries but "
            f"the TOC entry table contains {actual_material_count}"
        )
    return material_entries, declared_material_count, actual_material_count, num_files, warnings


def _group_material_ranges(entries: list[dict]):
    """Coalesce nearby Material payload slices to avoid repeatedly decoding DSAR chunks."""
    ordered = sorted(entries, key=lambda row: (row["toc_offset"], row["toc_size"], row["entry_index"]))
    groups: list[tuple[int, int, list[dict]]] = []
    for entry in ordered:
        start = entry["toc_offset"]
        size = entry["toc_size"]
        end = start + size
        if size <= 0:
            groups.append((start, start, [entry]))
            continue
        if groups:
            group_start, group_end, group_entries = groups[-1]
            merged_end = max(group_end, end)
            gap = max(0, start - group_end)
            if (
                gap <= MATERIAL_BATCH_MAX_GAP
                and merged_end - group_start <= MATERIAL_BATCH_MAX_BYTES
                and group_entries[0]["toc_size"] > 0
            ):
                group_entries.append(entry)
                groups[-1] = (group_start, merged_end, group_entries)
                continue
        groups.append((start, end, [entry]))
    return groups


def _read_material_payloads(index: SlimBundleIndex, package_name: str, entries: list[dict]):
    """Yield (entry, payload, error); fall back to isolated reads if a batch fails."""
    safe_entries = []
    for entry in entries:
        if entry["toc_size"] > MAX_MATERIAL_BYTES:
            yield (
                entry,
                b"",
                f"Material TOC payload exceeds parser safety limit ({entry['toc_size']} > {MAX_MATERIAL_BYTES})",
            )
        else:
            safe_entries.append(entry)
    for start, end, group in _group_material_ranges(safe_entries):
        if len(group) == 1 and group[0]["toc_size"] <= 0:
            yield group[0], b"", "Material entry has an empty TOC payload"
            continue
        try:
            block = index.reconstruct_range(package_name, start, end - start)
            for entry in group:
                relative = entry["toc_offset"] - start
                size = entry["toc_size"]
                if size <= 0:
                    yield entry, b"", "Material entry has an empty TOC payload"
                elif relative < 0 or relative > len(block) or size > len(block) - relative:
                    yield entry, b"", "Material payload slice is outside its coalesced TOC range"
                else:
                    yield entry, block[relative:relative + size], None
        except (HD2ResourceError, OSError, struct.error) as batch_error:
            # A malformed bundle segment should not hide the status of other entries.
            for entry in group:
                try:
                    if entry["toc_size"] <= 0:
                        raise HD2ResourceError("Material entry has an empty TOC payload")
                    payload = index.reconstruct_range(
                        package_name, entry["toc_offset"], entry["toc_size"]
                    )
                    yield entry, payload, None
                except (HD2ResourceError, OSError, struct.error) as error:
                    yield entry, b"", (
                        f"Batch read failed ({batch_error}); individual read failed ({error})"
                    )


def scan_slim_material_census(
    data_dir: str | Path,
    max_packages: Optional[int] = None,
    include_descriptors: bool = False,
) -> dict:
    """Parse every Material TOC payload in the indexed SLIM packages.

    Only package TOC metadata and Material TOC payload ranges are read. GPU and
    Stream sidecars are not reconstructed or inspected.
    """
    root = Path(data_dir)
    index = SlimBundleIndex(root)
    all_names = [
        name for name in sorted(index.packages)
        if not name.endswith(SIDECAR_SUFFIXES)
    ]
    if max_packages is not None:
        if max_packages < 1:
            raise HD2ResourceError("max_packages must be at least 1")
        names = all_names[:max_packages]
    else:
        names = all_names

    materials: list[dict] = []
    package_rows: list[dict] = []
    package_errors: list[dict] = []
    material_errors: list[dict] = []
    warning_samples: list[dict] = []
    material_occurrences: dict[str, list[dict]] = defaultdict(list)
    payload_occurrences: dict[str, list[dict]] = defaultdict(list)
    parent_counts: Counter[str] = Counter()
    texture_type_counts: Counter[str] = Counter()
    texture_type_material_counts: Counter[str] = Counter()
    texture_ids_by_type: dict[str, set[str]] = defaultdict(set)
    variable_counts: Counter[str] = Counter()
    variable_material_counts: Counter[str] = Counter()
    variable_classes: dict[str, Counter[str]] = defaultdict(Counter)
    variable_element_counts: dict[str, Counter[int]] = defaultdict(Counter)
    shape_counts: Counter[tuple[str, str, str]] = Counter()

    material_entry_count = 0
    material_parse_success_count = 0
    material_parse_error_count = 0
    material_parse_warning_count = 0
    package_warning_count = 0
    texture_node_count = 0
    zero_texture_id_count = 0
    shader_variable_descriptor_count = 0
    packages_with_materials = 0
    resource_entry_rows_scanned = 0

    for package_name in names:
        try:
            entries, declared_count, actual_count, rows_in_package, package_warnings = _material_entry_rows(
                index, package_name
            )
            resource_entry_rows_scanned += rows_in_package
            package_warning_count += len(package_warnings)
            packages_with_materials += 1 if actual_count else 0
            material_entry_count += actual_count
            parsed_in_package = 0
            errors_in_package = 0

            for entry, payload, read_error in _read_material_payloads(index, package_name, entries):
                occurrence = {
                    "package": package_name,
                    "entry_index": entry["entry_index"],
                }
                file_id = f'{entry["file_id"]:016x}'
                if read_error is not None:
                    material_parse_error_count += 1
                    errors_in_package += 1
                    material_occurrences[file_id].append({
                        **occurrence, "payload_sha256": None,
                    })
                    if len(material_errors) < 2000:
                        material_errors.append({
                            **occurrence,
                            "file_id": file_id,
                            "error": read_error,
                        })
                    materials.append({
                        **occurrence,
                        "file_id": file_id,
                        "toc_bytes": entry["toc_size"],
                        "parse_status": "error",
                        "error": read_error,
                    })
                    continue

                try:
                    parsed = parse_material_resource(payload)
                except (HD2ResourceError, OSError, struct.error, ValueError, TypeError, OverflowError) as error:
                    material_parse_error_count += 1
                    errors_in_package += 1
                    material_occurrences[file_id].append({
                        **occurrence, "payload_sha256": _sha256(payload),
                    })
                    if len(material_errors) < 2000:
                        material_errors.append({
                            **occurrence,
                            "file_id": file_id,
                            "error": str(error),
                        })
                    materials.append({
                        **occurrence,
                        "file_id": file_id,
                        "toc_bytes": len(payload),
                        "payload_sha256": _sha256(payload),
                        "parse_status": "error",
                        "error": str(error),
                    })
                    payload_occurrences[_sha256(payload)].append({
                        **occurrence, "file_id": file_id,
                    })
                    continue

                material_parse_success_count += 1
                parsed_in_package += 1
                material_hash = _sha256(payload)
                parent_id = parsed["header"]["parent_material_id"]
                parent_counts[parent_id] += 1
                texture_nodes = parsed["texture_nodes"]
                variables = parsed["shader_variables"]
                texture_types = [node["texture_type_id_hex"] for node in texture_nodes]
                variable_layout = [
                    [
                        variable["variable_id_hex"],
                        variable["klass"],
                        variable["elements"],
                        variable["value_offset"],
                        variable["element_stride"],
                    ]
                    for variable in variables
                ]
                texture_layout_signature = _signature(texture_types)
                shader_layout_signature = _signature(variable_layout)
                shape_counts[(parent_id, texture_layout_signature, shader_layout_signature)] += 1

                local_texture_types = set()
                for node in texture_nodes:
                    type_id = node["texture_type_id_hex"]
                    texture_id = node["texture_id"]
                    texture_type_counts[type_id] += 1
                    local_texture_types.add(type_id)
                    if int(texture_id, 16) == 0:
                        zero_texture_id_count += 1
                    else:
                        texture_ids_by_type[type_id].add(texture_id)
                for type_id in local_texture_types:
                    texture_type_material_counts[type_id] += 1
                texture_node_count += len(texture_nodes)

                local_variables = set()
                for variable in variables:
                    variable_id = variable["variable_id_hex"]
                    variable_counts[variable_id] += 1
                    variable_classes[variable_id][variable["klass_name"]] += 1
                    variable_element_counts[variable_id][variable["elements"]] += 1
                    local_variables.add(variable_id)
                for variable_id in local_variables:
                    variable_material_counts[variable_id] += 1
                shader_variable_descriptor_count += len(variables)

                row = {
                    **occurrence,
                    "file_id": file_id,
                    "toc_bytes": len(payload),
                    "parent_material_id": parent_id,
                    "payload_sha256": material_hash,
                    "texture_count": len(texture_nodes),
                    "texture_layout_signature": texture_layout_signature,
                    "shader_variable_count": len(variables),
                    "shader_layout_signature": shader_layout_signature,
                    "parse_status": "ok",
                }
                if include_descriptors:
                    row["texture_nodes"] = [
                        [node["texture_type_id_hex"], node["texture_id"]]
                        for node in texture_nodes
                    ]
                    row["shader_variables"] = [
                        [
                            variable["variable_id_hex"],
                            variable["klass"],
                            variable["elements"],
                            variable["value_offset"],
                            variable["element_stride"],
                            variable["first_vector_preview"],
                        ]
                        for variable in variables
                    ]
                materials.append(row)
                material_occurrences[file_id].append({
                    **occurrence,
                    "payload_sha256": material_hash,
                })
                payload_occurrences[material_hash].append({
                    **occurrence,
                    "file_id": file_id,
                })

                if parsed["warnings"]:
                    material_parse_warning_count += len(parsed["warnings"])
                    for warning in parsed["warnings"]:
                        if len(warning_samples) < 200:
                            warning_samples.append({
                                **occurrence,
                                "file_id": file_id,
                                "warning": warning,
                            })

            for warning in package_warnings:
                if len(warning_samples) < 200:
                    warning_samples.append({
                        "package": package_name,
                        "warning": warning,
                    })
            package_rows.append({
                "package": package_name,
                "declared_material_count": declared_count,
                "material_entry_count": actual_count,
                "parsed_material_count": parsed_in_package,
                "material_error_count": errors_in_package,
            })
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError, OverflowError) as error:
            package_errors.append({"package": package_name, "error": str(error)})
            package_rows.append({
                "package": package_name,
                "declared_material_count": None,
                "material_entry_count": None,
                "parsed_material_count": 0,
                "material_error_count": 0,
                "package_status": "error",
            })

    unique_material_file_ids = set(material_occurrences)
    duplicate_file_ids = []
    for file_id, occurrences in sorted(material_occurrences.items()):
        if len(occurrences) > 1:
            hashes = sorted({
                item["payload_sha256"] for item in occurrences
                if item["payload_sha256"] is not None
            })
            duplicate_file_ids.append({
                "file_id": file_id,
                "occurrence_count": len(occurrences),
                "unique_payload_hash_count": len(hashes),
                "payload_sha256s": hashes,
                "occurrences": occurrences,
            })

    duplicate_payloads = []
    for payload_hash, occurrences in sorted(payload_occurrences.items()):
        if len(occurrences) > 1:
            duplicate_payloads.append({
                "payload_sha256": payload_hash,
                "occurrence_count": len(occurrences),
                "unique_file_id_count": len({item["file_id"] for item in occurrences}),
                "occurrences": occurrences,
            })

    parent_materials = [
        {
            "parent_material_id": parent_id,
            "occurrence_count": count,
            "matches_material_file_id": parent_id in unique_material_file_ids,
        }
        for parent_id, count in sorted(parent_counts.items(), key=lambda item: (-item[1], item[0]))
    ]
    texture_type_frequencies = [
        {
            "texture_type_id": type_id,
            "node_occurrence_count": count,
            "material_occurrence_count": texture_type_material_counts[type_id],
            "unique_nonzero_texture_id_count": len(texture_ids_by_type[type_id]),
        }
        for type_id, count in sorted(texture_type_counts.items(), key=lambda item: (-item[1], item[0]))
    ]
    shader_variable_frequencies = [
        {
            "variable_id": variable_id,
            "descriptor_occurrence_count": count,
            "material_occurrence_count": variable_material_counts[variable_id],
            "klass_counts": dict(sorted(variable_classes[variable_id].items())),
            "element_counts": {
                str(key): value
                for key, value in sorted(variable_element_counts[variable_id].items())
            },
        }
        for variable_id, count in sorted(variable_counts.items(), key=lambda item: (-item[1], item[0]))
    ]
    material_shapes = [
        {
            "parent_material_id": parent_id,
            "texture_layout_signature": texture_signature,
            "shader_layout_signature": shader_signature,
            "occurrence_count": count,
        }
        for (parent_id, texture_signature, shader_signature), count
        in sorted(shape_counts.items(), key=lambda item: (-item[1], item[0]))
    ]

    package_count_scanned = len(names)
    package_scan_complete = package_count_scanned == len(all_names) and not package_errors
    scan_complete = package_scan_complete and material_parse_error_count == 0
    status = "warning" if (
        not scan_complete or package_warning_count or material_parse_warning_count
    ) else "ok"
    return {
        "schema": "SourceModelReader-HD2MaterialCensus1",
        "data_directory": str(root.resolve()),
        "container": "slim",
        "options": {
            "max_packages": max_packages,
            "include_descriptors": include_descriptors,
            "gpu_sidecars_read": False,
            "stream_sidecars_read": False,
            "material_payload_batch_max_bytes": MATERIAL_BATCH_MAX_BYTES,
            "material_payload_batch_max_gap": MATERIAL_BATCH_MAX_GAP,
        },
        "summary": {
            "status": status,
            "scan_complete": scan_complete,
            "package_scan_complete": package_scan_complete,
            "material_parse_complete": package_scan_complete and material_parse_error_count == 0,
            "package_count_in_index": len(index.packages),
            "package_count_scannable": len(all_names),
            "package_count_scanned": package_count_scanned,
            "package_count_with_materials": packages_with_materials,
            "package_count_errors": len(package_errors),
            "package_warning_count": package_warning_count,
            "resource_entry_rows_scanned": resource_entry_rows_scanned,
            "material_entry_count": material_entry_count,
            "material_parse_success_count": material_parse_success_count,
            "material_parse_error_count": material_parse_error_count,
            "unique_material_file_id_count": len(unique_material_file_ids),
            "duplicate_material_file_id_count": len(duplicate_file_ids),
            "duplicate_material_occurrence_count": sum(
                item["occurrence_count"] - 1 for item in duplicate_file_ids
            ),
            "unique_material_payload_hash_count": len(payload_occurrences),
            "duplicate_payload_hash_count": len(duplicate_payloads),
            "parent_material_id_unique_count": len(parent_counts),
            "parent_material_id_matching_file_id_count": sum(
                1 for item in parent_materials if item["matches_material_file_id"]
            ),
            "texture_node_count": texture_node_count,
            "zero_texture_id_reference_count": zero_texture_id_count,
            "texture_type_unique_count": len(texture_type_counts),
            "shader_variable_descriptor_count": shader_variable_descriptor_count,
            "shader_variable_unique_count": len(variable_counts),
            "material_parse_warning_count": material_parse_warning_count,
        },
        "materials": materials,
        "packages": package_rows,
        "parent_material_ids": parent_materials,
        "texture_type_frequencies": texture_type_frequencies,
        "shader_variable_frequencies": shader_variable_frequencies,
        "material_structure_frequencies": material_shapes,
        "duplicate_file_ids": duplicate_file_ids,
        "duplicate_payloads": duplicate_payloads,
        "package_errors": package_errors,
        "material_errors": material_errors,
        "material_errors_omitted": max(0, material_parse_error_count - len(material_errors)),
        "warning_samples": warning_samples,
        "limitations": [
            "The census reads SLIM package headers, TOC type tables, TOC entry tables, and Material TOC payload ranges only.",
            "GPU and Stream sidecars are not reconstructed or inspected.",
            "Texture IDs are recorded as Material references; this census does not globally resolve or decode Texture payloads.",
            "ParentMaterialID is preserved as a raw 64-bit identifier. A match to a Material FileID is reported as an observed identity match, not proof of inheritance semantics.",
            "Shader-variable IDs and texture-node type IDs remain raw values; versioned SDK dictionaries and verified render behavior are separate research steps.",
            "With --include-descriptors, each Material row also includes ordered texture-node pairs and shader-variable descriptors/previews.",
        ],
    }


def format_material_census_text(report: dict, top: int = 12) -> str:
    summary = report["summary"]
    lines = [
        "Source Model Reader - HD2 Material census",
        f"Container: {report['container']} | Status: {summary['status'].upper()}",
        (
            f"Packages: {summary['package_count_scanned']}/"
            f"{summary['package_count_scannable']} scanned | "
            f"with Material: {summary['package_count_with_materials']} | "
            f"errors: {summary['package_count_errors']}"
        ),
        (
            f"Material entries: {summary['material_entry_count']:,} | "
            f"parsed: {summary['material_parse_success_count']:,} | "
            f"parse errors: {summary['material_parse_error_count']:,}"
        ),
        (
            f"Unique FileIDs: {summary['unique_material_file_id_count']:,} | "
            f"duplicate FileIDs: {summary['duplicate_material_file_id_count']:,} | "
            f"duplicate payload hashes: {summary['duplicate_payload_hash_count']:,}"
        ),
        (
            f"Texture nodes: {summary['texture_node_count']:,} | "
            f"slot types: {summary['texture_type_unique_count']:,} | "
            f"zero Texture IDs: {summary['zero_texture_id_reference_count']:,}"
        ),
        (
            f"Shader-variable descriptors: {summary['shader_variable_descriptor_count']:,} | "
            f"unique variable IDs: {summary['shader_variable_unique_count']:,}"
        ),
        "Common ParentMaterialIDs:",
    ]
    for item in report["parent_material_ids"][:top]:
        state = "matches a Material FileID" if item["matches_material_file_id"] else "no FileID match"
        lines.append(
            f"  {item['parent_material_id']} | "
            f"{item['occurrence_count']:,} occurrences | {state}"
        )
    lines.append("Common texture-node type IDs:")
    for item in report["texture_type_frequencies"][:top]:
        lines.append(
            f"  {item['texture_type_id']} | nodes={item['node_occurrence_count']:,} "
            f"materials={item['material_occurrence_count']:,} "
            f"unique textures={item['unique_nonzero_texture_id_count']:,}"
        )
    lines.append("Common shader-variable IDs:")
    for item in report["shader_variable_frequencies"][:top]:
        classes = ", ".join(f"{name}:{count}" for name, count in item["klass_counts"].items())
        lines.append(
            f"  {item['variable_id']} | descriptors={item['descriptor_occurrence_count']:,} "
            f"materials={item['material_occurrence_count']:,} | {classes}"
        )
    if report["duplicate_file_ids"]:
        lines.append("Duplicate Material FileIDs:")
        for item in report["duplicate_file_ids"][:top]:
            lines.append(
                f"  {item['file_id']} | occurrences={item['occurrence_count']} "
                f"payload hashes={item['unique_payload_hash_count']}"
            )
    if report["package_errors"]:
        lines.append("Package errors (first 10):")
        for item in report["package_errors"][:10]:
            lines.append(f"  {item['package']}: {item['error']}")
    lines.append("Note: this scan does not read GPU/Stream sidecars or infer render modes.")
    return "\n".join(lines)
