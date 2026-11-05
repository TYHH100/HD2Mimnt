"""Global read-only Unit -> Material -> Mesh Section reference scanner.

The scanner reads SLIM package TOC tables and Unit TOC payloads only. It does
not reconstruct GPU/Stream sidecars or decode mesh geometry. Its purpose is to
identify where selected Material FileIDs are referenced by Units and, when
available, map those references to direct or CompositeUnit-backed mesh sections.
"""
from __future__ import annotations

import struct
from pathlib import Path
from typing import Optional

from hd2_resources import (
    HD2ResourceError,
    MAX_TOC_ENTRIES,
    MAX_TOC_TYPES,
    SIDECAR_SUFFIXES,
    SlimBundleIndex,
    TOC_MAGIC,
    TYPE_IDS,
    parse_unit_header,
    parse_unit_material_ids,
)
from hd2_unit import parse_composite_unit, parse_mesh_info

TOC_HEADER_SIZE = 72
TOC_TYPE_ROW_SIZE = 32
TOC_ENTRY_ROW_SIZE = 80
ENTRY_CHUNK_ROWS = 2048
MAX_UNIT_TOC_BYTES = 64 * 1024 * 1024
MAX_COMPOSITE_TOC_BYTES = 64 * 1024 * 1024


def _read_package_entries(index: SlimBundleIndex, package_name: str) -> tuple[dict, dict, int, list[str]]:
    """Read one package's type table and collect Unit/CompositeUnit TOC entries."""
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

    type_table_size = num_types * TOC_TYPE_ROW_SIZE
    entries_offset = TOC_HEADER_SIZE + type_table_size
    entries_size = num_files * TOC_ENTRY_ROW_SIZE
    if entries_offset > package.size or entries_size > package.size - entries_offset:
        raise HD2ResourceError(f"{package_name}: TOC tables exceed declared package size")

    type_data = (
        index.reconstruct_range(package_name, TOC_HEADER_SIZE, type_table_size)
        if type_table_size else b""
    )
    declared_counts: dict[int, int] = {}
    for type_index in range(num_types):
        _unknown, type_id, count, _unknown2, _unknown3 = struct.unpack_from(
            "<QQQII", type_data, type_index * TOC_TYPE_ROW_SIZE
        )
        if type_id in declared_counts:
            raise HD2ResourceError(
                f"{package_name}: duplicate resource type {type_id:016x}"
            )
        declared_counts[type_id] = count

    unit_type_id = TYPE_IDS["Unit"]
    composite_type_id = TYPE_IDS["CompositeUnit"]
    unit_entries: dict[int, dict] = {}
    composite_entries: dict[int, dict] = {}
    actual_units = 0
    actual_composites = 0

    for first_row in range(0, num_files, ENTRY_CHUNK_ROWS):
        row_count = min(ENTRY_CHUNK_ROWS, num_files - first_row)
        chunk_offset = entries_offset + first_row * TOC_ENTRY_ROW_SIZE
        chunk = index.reconstruct_range(
            package_name, chunk_offset, row_count * TOC_ENTRY_ROW_SIZE
        )
        for local_row in range(row_count):
            row_offset = local_row * TOC_ENTRY_ROW_SIZE
            file_id, type_id, toc_offset = struct.unpack_from(
                "<3Q", chunk, row_offset
            )
            if type_id not in (unit_type_id, composite_type_id):
                continue
            toc_size = struct.unpack_from("<I", chunk, row_offset + 56)[0]
            entry = {
                "file_id": file_id,
                "toc_offset": toc_offset,
                "toc_size": toc_size,
                "entry_index": first_row + local_row,
            }
            if type_id == unit_type_id:
                actual_units += 1
                unit_entries[file_id] = entry
            else:
                actual_composites += 1
                composite_entries[file_id] = entry

    declared_units = declared_counts.get(unit_type_id, 0)
    declared_composites = declared_counts.get(composite_type_id, 0)
    warnings = []
    if declared_units != actual_units:
        warnings.append(
            f"Unit type table declares {declared_units} entries but "
            f"the TOC entry table contains {actual_units}"
        )
    if declared_composites != actual_composites:
        warnings.append(
            f"CompositeUnit type table declares {declared_composites} entries but "
            f"the TOC entry table contains {actual_composites}"
        )
    return unit_entries, composite_entries, num_files, warnings


def _read_entry_toc(
    index: SlimBundleIndex,
    package_name: str,
    entry: dict,
    label: str,
    minimum_size: int,
    maximum_size: int,
) -> bytes:
    size = entry["toc_size"]
    if size < minimum_size:
        raise HD2ResourceError(
            f"{label} {entry['file_id']:016x} has a short TOC payload ({size} bytes)"
        )
    if size > maximum_size:
        raise HD2ResourceError(
            f"{label} {entry['file_id']:016x} TOC payload exceeds safety limit "
            f"({size} > {maximum_size})"
        )
    return index.reconstruct_range(package_name, entry["toc_offset"], size)


def _material_mesh_matches(
    materials: list[dict],
    mesh_records: list[dict],
    target_ids: set[int],
    composite_mesh_info: Optional[dict] = None,
) -> list[dict]:
    """Join Unit Material section IDs to direct or CompositeUnit mesh sections."""
    by_section: dict[int, int] = {}
    for item in materials:
        by_section[item["section_id"]] = item["material_id"]

    results = []
    for item in materials:
        material_id = item["material_id"]
        if material_id not in target_ids:
            continue

        section_matches = []
        for mesh in mesh_records:
            if composite_mesh_info is None:
                layout = mesh
            else:
                layout = composite_mesh_info.get(mesh["mesh_id"])
                if layout is None:
                    continue

            for section in layout.get("sections", []):
                slot_id = section.get("material_slot_id")
                resolved_material_id = by_section.get(slot_id)
                if resolved_material_id != material_id:
                    continue
                section_matches.append({
                    "mesh_index": mesh.get("index"),
                    "mesh_id": f"{mesh['mesh_id']:08x}",
                    "lod_index": mesh.get("lod_index"),
                    "section_index": section.get("index"),
                    "material_slot_id": slot_id,
                    "vertex_offset": section.get("vertex_offset"),
                    "vertex_count": section.get("num_vertices"),
                    "index_offset": section.get("index_offset"),
                    "index_count": section.get("num_indices"),
                    "group_index": section.get("group_index"),
                })

        results.append({
            "unit_material_section_id": item["section_id"],
            "material_file_id": f"{material_id:016x}",
            "mesh_section_count": len(section_matches),
            "mesh_sections": section_matches,
        })
    return results


def scan_slim_material_users(
    data_dir: str | Path,
    material_ids: list[int],
    max_packages: Optional[int] = None,
) -> dict:
    """Find Unit -> Material -> Mesh Section references across SLIM packages.

    Only package TOC metadata and Unit/CompositeUnit TOC payloads are read.
    GPU and Stream sidecars are not reconstructed. A global scan is required
    because Unit references may point to Material resources in other packages.
    """
    requested = sorted(set(material_ids))
    if not requested:
        raise HD2ResourceError("At least one Material FileID is required")
    if len(requested) > 4096:
        raise HD2ResourceError("Material FileID query exceeds the 4096-ID safety limit")
    if any(
        isinstance(value, bool) or not isinstance(value, int)
        or value <= 0 or value > 0xFFFFFFFFFFFFFFFF
        for value in requested
    ):
        raise HD2ResourceError("Material FileIDs must be positive unsigned 64-bit integers")
    if max_packages is not None and max_packages < 1:
        raise HD2ResourceError("max_packages must be at least 1")

    root = Path(data_dir)
    index = SlimBundleIndex(root)
    all_names = [
        name for name in sorted(index.packages)
        if not name.endswith(SIDECAR_SUFFIXES)
    ]
    names = all_names if max_packages is None else all_names[:max_packages]
    target_set = set(requested)
    references: dict[int, list[dict]] = {file_id: [] for file_id in requested}
    package_errors = []
    unit_errors = []
    composite_warnings = []
    package_count_with_units = 0
    unit_count_scanned = 0
    unit_parse_success_count = 0
    unit_material_reference_count = 0
    mesh_section_match_count = 0

    composite_type_id = TYPE_IDS["CompositeUnit"]

    for package_name in names:
        try:
            unit_entries, composite_entries, _num_files, package_warnings = (
                _read_package_entries(index, package_name)
            )
            if not unit_entries and not package_warnings:
                continue
            package_count_with_units += 1
            for warning in package_warnings:
                package_errors.append({"package": package_name, "error": warning})

            for unit_id, unit_entry in sorted(unit_entries.items()):
                unit_count_scanned += 1
                try:
                    unit_toc = _read_entry_toc(
                        index, package_name, unit_entry, "Unit", 128,
                        MAX_UNIT_TOC_BYTES,
                    )
                    header = parse_unit_header(unit_toc)
                    materials = parse_unit_material_ids(
                        unit_toc, header.materials_offset
                    )
                    mesh_end = (
                        header.materials_offset
                        or header.ending_offset
                        or len(unit_toc)
                    )
                    mesh_report = parse_mesh_info(
                        unit_toc, header.mesh_info_offset, mesh_end
                    )
                    composite_mesh_info = None
                    geometry_source = "Unit"
                    composite_status = None

                    if header.composite_ref:
                        # A CompositeUnit owns the actual material section layout.
                        # If it cannot be resolved, an empty mapping prevents the
                        # Unit-side placeholder MeshInfo from being mistaken for
                        # direct mesh sections.
                        composite_mesh_info = {}
                        geometry_source = "CompositeUnit"
                        composite_entry = composite_entries.get(header.composite_ref)
                        if composite_entry is None:
                            composite_status = "not_in_same_package"
                            composite_warnings.append({
                                "package": package_name,
                                "unit_id": f"{unit_id:016x}",
                                "composite_unit_id": f"{header.composite_ref:016x}",
                                "status": composite_status,
                            })
                        else:
                            try:
                                composite_toc = _read_entry_toc(
                                    index, package_name, composite_entry,
                                    "CompositeUnit", 16, MAX_COMPOSITE_TOC_BYTES,
                                )
                                composite = parse_composite_unit(
                                    composite_toc, b"", unit_id
                                )
                                composite_mesh_info = composite["mesh_info"]
                                composite_status = "resolved"
                            except (HD2ResourceError, OSError, struct.error, ValueError, TypeError) as error:
                                composite_status = "parse_error"
                                composite_warnings.append({
                                    "package": package_name,
                                    "unit_id": f"{unit_id:016x}",
                                    "composite_unit_id": f"{header.composite_ref:016x}",
                                    "status": composite_status,
                                    "error": str(error),
                                })

                    matched = _material_mesh_matches(
                        materials, mesh_report["records"], target_set,
                        composite_mesh_info,
                    )
                    unit_parse_success_count += 1

                    for item in matched:
                        material_id = int(item["material_file_id"], 16)
                        if material_id not in references:
                            continue
                        item["package"] = package_name
                        item["unit_id"] = f"{unit_id:016x}"
                        item["resource_version"] = header.version
                        item["geometry_source"] = geometry_source
                        item["composite_unit_id"] = (
                            f"{header.composite_ref:016x}"
                            if header.composite_ref else None
                        )
                        item["composite_status"] = composite_status
                        item["mesh_count_in_unit_toc"] = mesh_report["count"]
                        references[material_id].append(item)
                        unit_material_reference_count += 1
                        mesh_section_match_count += item["mesh_section_count"]

                except (HD2ResourceError, OSError, struct.error, ValueError, TypeError, KeyError, OverflowError) as error:
                    unit_errors.append({
                        "package": package_name,
                        "unit_id": f"{unit_id:016x}",
                        "error": str(error),
                    })

        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError, KeyError, OverflowError) as error:
            package_errors.append({"package": package_name, "error": str(error)})

    resources = []
    for material_id in requested:
        found = sorted(
            references[material_id],
            key=lambda row: (row["package"], row["unit_id"], row["unit_material_section_id"]),
        )
        resources.append({
            "material_file_id": f"{material_id:016x}",
            "status": "referenced" if found else "not_referenced_by_scanned_units",
            "unit_reference_count": len(found),
            "mesh_section_match_count": sum(
                item["mesh_section_count"] for item in found
            ),
            "references": found,
        })

    truncated = len(names) < len(all_names)
    scan_complete = (
        not truncated
        and not package_errors
        and not unit_errors
    )
    status = "warning" if not scan_complete or composite_warnings else "ok"
    return {
        "schema": "SourceModelReader-HD2MaterialUsers1",
        "data_directory": str(root.resolve()),
        "container": "slim",
        "query": {
            "material_file_ids": [f"{value:016x}" for value in requested],
            "max_packages": max_packages,
        },
        "summary": {
            "status": status,
            "package_count_in_index": len(index.packages),
            "package_count_scannable": len(all_names),
            "package_count_scanned": len(names),
            "package_count_with_units": package_count_with_units,
            "unit_count_scanned": unit_count_scanned,
            "unit_parse_success_count": unit_parse_success_count,
            "unit_parse_error_count": len(unit_errors),
            "material_unit_reference_count": unit_material_reference_count,
            "mesh_section_match_count": mesh_section_match_count,
            "material_ids_referenced": sum(
                1 for item in resources if item["unit_reference_count"]
            ),
            "material_ids_not_referenced": sum(
                1 for item in resources if not item["unit_reference_count"]
            ),
            "package_count_errors": len(package_errors),
            "composite_warning_count": len(composite_warnings),
            "scan_complete": scan_complete,
        },
        "resources": resources,
        "package_errors": package_errors,
        "unit_errors": unit_errors,
        "composite_warnings": composite_warnings,
        "limitations": [
            "The scan reads package TOC tables and Unit/CompositeUnit TOC payloads only; it does not reconstruct GPU or Stream sidecars or decode mesh geometry.",
            "A complete scan with no references means only that the queried Material FileIDs were not found in scanned Unit material tables; it does not prove the material resources are unused by non-Unit resource types.",
            "A CompositeUnit reference not resolved in the same package is reported as a warning; Unit-level Material references are still reported, but mesh-section mapping may be incomplete.",
            "Mesh sections are linked by the existing Unit section_id/material_slot_id mapping. This does not infer render states or final shader behavior.",
        ],
    }


def format_material_users_text(report: dict) -> str:
    summary = report["summary"]
    lines = [
        "Source Model Reader - HD2 Material users",
        f"Packages: {summary['package_count_scanned']}/{summary['package_count_scannable']} scanned "
        f"({summary['package_count_with_units']} contain Units)",
        f"Units: {summary['unit_count_scanned']:,} scanned | "
        f"{summary['unit_parse_success_count']:,} parsed | "
        f"{summary['unit_parse_error_count']:,} errors",
        f"Material Unit references: {summary['material_unit_reference_count']:,} | "
        f"Mesh Section matches: {summary['mesh_section_match_count']:,}",
        f"Target Materials referenced: {summary['material_ids_referenced']}/"
        f"{len(report['resources'])} | Status: {summary['status'].upper()} | "
        f"Complete: {summary['scan_complete']}",
    ]
    for resource in report["resources"]:
        lines.append(
            f"Material {resource['material_file_id']}: "
            f"{resource['unit_reference_count']} Unit references, "
            f"{resource['mesh_section_match_count']} Mesh Section matches"
        )
        for reference in resource["references"][:20]:
            lines.append(
                f"  Package {reference['package']} | Unit {reference['unit_id']} | "
                f"Unit material section {reference['unit_material_section_id']} | "
                f"Mesh sections {reference['mesh_section_count']} | "
                f"Geometry {reference['geometry_source']}"
            )
            for section in reference["mesh_sections"][:20]:
                lines.append(
                    f"    Mesh {section['mesh_id']} LOD {section['lod_index']} "
                    f"section {section['section_index']} | "
                    f"vertices={section['vertex_count']} indices={section['index_count']}"
                )
            if len(reference["mesh_sections"]) > 20:
                lines.append(
                    f"    ... {len(reference['mesh_sections']) - 20} additional Mesh Sections in JSON"
                )
        if len(resource["references"]) > 20:
            lines.append(
                f"  ... {len(resource['references']) - 20} additional Unit references in JSON"
            )
    if report["package_errors"]:
        lines.append(f"Package scan errors: {len(report['package_errors'])}")
    if report["unit_errors"]:
        lines.append(f"Unit parse errors: {len(report['unit_errors'])}")
    if report["composite_warnings"]:
        lines.append(f"CompositeUnit resolution warnings: {len(report['composite_warnings'])}")
    if not summary["scan_complete"]:
        lines.append(
            "NOTE: references are incomplete because one or more packages/Units could not be scanned."
        )
    lines.append(
        "Note: this command inspects Unit material tables and MeshInfo references only; "
        "GPU/Stream geometry and render behavior are not decoded."
    )
    return "\n".join(lines)
