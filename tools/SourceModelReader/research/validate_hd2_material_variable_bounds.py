#!/usr/bin/env python3
"""Test the Material shader-variable boundary hypotheses against real game entries.

The bounded Material decoder reads a fixed header, then a texture-type table, a texture-ID
table, a shader-variable descriptor table, and finally a value region bounded by the
declared VariableDataSize. Two claims in the notes were based on a handful of inspected
files: that the declared region leaves a small constant tail, and that descriptor offsets
stay inside it. This pass tests those claims over every Material TOC payload it is pointed
at, by checking explicit arithmetic hypotheses instead of trusting one example:

  H1  sum(elements x element_stride over descriptors) == VariableDataSize
  H2  highest (value_offset + elements x element_stride) == VariableDataSize
  H3  EndOffset == resource size
  H4  EndOffset == variable-value payload offset + VariableDataSize

It also tallies how often each descriptor's own range falls outside the declared region
and what the trailing-byte count actually is, so a boundary rule can be adopted only when
the shipped data supports it. Payload bytes are read, never modified.
"""
from __future__ import annotations

import argparse
import json
import struct
import sys
from collections import Counter
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

COMPONENT_CLASSES = (0, 1, 2, 3)


def component_bytes(variable: dict[str, Any]) -> int | None:
    if variable["klass"] in COMPONENT_CLASSES:
        return (variable["klass"] + 1) * 4
    return None


def variable_region_hypotheses(header: dict[str, Any], variables: list[dict[str, Any]]) -> dict[str, Any]:
    """Evaluate the size hypotheses for one Material's declared value region."""
    declared = int(header["declared_variable_data_size"])
    sum_stride = 0
    sum_floor = 0
    highest_end = 0
    out_of_range = 0
    stride_below_component = 0
    arrays = 0
    for variable in variables:
        elements = int(variable["elements"])
        stride = int(variable["element_stride"])
        offset = int(variable["value_offset"])
        size = max(elements, 1) * stride
        sum_stride += size
        floor = stride
        component = component_bytes(variable)
        if component is not None and stride < component:
            stride_below_component += 1
            floor = component
        sum_floor += max(elements, 1) * floor
        end = offset + (size if elements else stride)
        highest_end = max(highest_end, end)
        if elements > 1:
            arrays += 1
        if end > declared:
            out_of_range += 1
    return {
        "declared_variable_data_size": declared,
        "descriptor_count": len(variables),
        "sum_elements_times_stride": sum_stride,
        "sum_with_component_floor": sum_floor,
        "h1_sum_equals_declared": sum_stride == declared,
        "h1_alt_sum_with_floor_equals_declared": sum_floor == declared,
        "h2_highest_end_equals_declared": highest_end == declared,
        "highest_used_end": highest_end,
        "h2_alt_highest_end_within_declared": highest_end <= declared,
        "descriptors_outside_declared_region": out_of_range,
        "descriptors_with_stride_below_component": stride_below_component,
        "array_descriptors": arrays,
    }


def end_offset_relations(header: dict[str, Any], layout: dict[str, Any],
                         resource_size: int) -> dict[str, Any]:
    """Locate EndOffset relative to the value region and to the payload size."""
    end = int(header["end_offset"])
    payload_start = int(layout["variable_value_payload_offset"])
    declared = int(header["declared_variable_data_size"])
    trailing = int(layout["trailing_payload_size"])
    return {
        "end_offset": end,
        "resource_size": resource_size,
        "h3_end_offset_equals_resource_size": end == resource_size,
        "h4_end_offset_equals_payload_start_plus_declared": end == payload_start + declared,
        "h4_alt_plus_tail": end == payload_start + declared + trailing,
        "resource_minus_end_offset": resource_size - end,
        "end_offset_minus_payload_start_minus_declared": end - payload_start - declared,
        "trailing_payload_size": trailing,
        "variable_data_size_equals_remaining_payload": layout["variable_data_size_matches_remaining_payload"],
        "declared_size_exceeds_available": not layout["variable_data_size_fits_available_payload"],
    }


def descriptor_slots(variable: dict[str, Any]) -> tuple[int, int, int, int, int]:
    """The five stored u32 words of one descriptor, read back from the raw hex."""
    raw = bytes.fromhex(variable["raw_descriptor_hex"])
    return tuple(struct.unpack_from("<5I", raw, 0))


def slot_statistics(variables: list[dict[str, Any]]) -> dict[str, Any]:
    """Per-word nonzero counts, so a reserved word cannot masquerade as a field."""
    nonzero = [0, 0, 0, 0, 0]
    distinct = [set(), set(), set(), set(), set()]
    for variable in variables:
        for index, value in enumerate(descriptor_slots(variable)):
            if value:
                nonzero[index] += 1
            distinct[index].add(value)
    return {
        "descriptor_count": len(variables),
        "nonzero_per_word": nonzero,
        "distinct_values_per_word": [len(values) for values in distinct],
        "word0_all_zero": nonzero[0] == 0,
        "word1_all_zero": nonzero[1] == 0,
        "word4_all_zero": nonzero[4] == 0,
    }


def gap_statistics(variables: list[dict[str, Any]], declared: int) -> dict[str, Any]:
    """Reveal per-parameter byte sizes from the stored offsets themselves.

    The descriptor words that the decoder calls class/elements/stride are reserved on the
    sampled data, so the only observable size signal is the distance between consecutive
    value offsets and the room left after the last one.
    """
    offsets = sorted(int(variable["value_offset"]) for variable in variables)
    gaps = [b - a for a, b in zip(offsets, offsets[1:])]
    last_size = declared - offsets[-1] if offsets and declared >= offsets[-1] else None
    return {
        "offset_count": len(offsets),
        "min_offset": offsets[0] if offsets else None,
        "max_offset": offsets[-1] if offsets else None,
        "gap_sizes": dict(Counter(gaps)),
        "last_parameter_size": last_size,
        "all_offsets_aligned_to_4": all(value % 4 == 0 for value in offsets),
        "all_offsets_within_declared": bool(offsets) and offsets[-1] < declared,
        "sum_gaps": sum(gaps),
    }


def inspect_payload(parsed: dict[str, Any]) -> dict[str, Any]:
    """Combine both hypothesis groups for one already-parsed Material."""
    header = parsed["header"]
    layout = parsed["layout"]
    variables = parsed["shader_variables"]
    return {
        "resource_size": parsed["resource_size"],
        "num_textures": header["num_textures"],
        "num_variables": header["num_variables"],
        "parent_material_id": header["parent_material_id"],
        **variable_region_hypotheses(header, variables),
        **end_offset_relations(header, layout, parsed["resource_size"]),
        "warning_count": len(parsed["warnings"]),
        "warnings": parsed["warnings"][:3],
        **slot_statistics(variables),
        **gap_statistics(variables, header["declared_variable_data_size"]),
    }


def tally(rows: list[dict[str, Any]]) -> dict[str, Any]:
    total = len(rows)
    if not total:
        return {"materials": 0}
    with_descriptors = [row for row in rows if row["descriptor_count"]]
    counts = {
        "h1_sum_equals_declared": sum(1 for row in rows if row["h1_sum_equals_declared"]),
        "h1_alt_sum_with_floor_equals_declared": sum(
            1 for row in rows if row["h1_alt_sum_with_floor_equals_declared"]),
        "h2_highest_end_equals_declared": sum(
            1 for row in rows if row["h2_highest_end_equals_declared"]),
        "h2_alt_within_declared": sum(1 for row in rows if row["h2_alt_highest_end_within_declared"]),
        "h3_end_offset_equals_resource_size": sum(
            1 for row in rows if row["h3_end_offset_equals_resource_size"]),
        "h4_end_offset_equals_payload_start_plus_declared": sum(
            1 for row in rows if row["h4_end_offset_equals_payload_start_plus_declared"]),
        "h4_alt_plus_tail": sum(1 for row in rows if row["h4_alt_plus_tail"]),
        "variable_data_size_equals_remaining_payload": sum(
            1 for row in rows if row["variable_data_size_equals_remaining_payload"]),
        "declared_size_exceeds_available": sum(1 for row in rows if row["declared_size_exceeds_available"]),
        "rows_with_out_of_range_descriptors": sum(
            1 for row in rows if row["descriptors_outside_declared_region"]),
        "rows_with_stride_below_component": sum(
            1 for row in rows if row["descriptors_with_stride_below_component"]),
        "rows_with_arrays": sum(1 for row in rows if row["array_descriptors"]),
        "rows_with_warnings": sum(1 for row in rows if row["warning_count"]),
    }
    return {
        "materials": total,
        "hypothesis_hits": counts,
        "hypothesis_rates": {key: round(value / total, 4) for key, value in counts.items()},
        "trailing_size_distribution": dict(Counter(row["trailing_payload_size"] for row in rows)),
        "resource_minus_end_offset_distribution": dict(
            Counter(row["resource_minus_end_offset"] for row in rows)),
        "end_offset_minus_region_distribution": dict(
            Counter(row["end_offset_minus_payload_start_minus_declared"] for row in rows).most_common(10)),
        "declared_size_total": sum(row["declared_variable_data_size"] for row in rows),
        "descriptor_stride_below_component_total": sum(
            row["descriptors_with_stride_below_component"] for row in rows),
        "out_of_range_descriptor_total": sum(
            row["descriptors_outside_declared_region"] for row in rows),
        "array_descriptor_total": sum(row["array_descriptors"] for row in rows),
        "descriptor_total": sum(row["descriptor_count"] for row in rows),
        "reserved_word_nonzero": [sum(row["nonzero_per_word"][i] for row in rows)
                                  for i in range(5)],
        "rows_with_any_descriptor": sum(1 for row in rows if row["descriptor_count"]),
        "rows_with_all_reserved_words_zero": sum(
            1 for row in rows if row["word0_all_zero"] and row["word1_all_zero"]
            and row["word4_all_zero"]),
        "offset_gap_distribution": _merge_gap_rows(rows),
        "last_parameter_size_distribution": dict(Counter(
            row["last_parameter_size"] for row in rows if row["offset_count"])),
        "rows_all_offsets_aligned_to_4": sum(1 for row in rows if row["all_offsets_aligned_to_4"]),
        "rows_offsets_within_declared": sum(
            1 for row in rows if row["all_offsets_within_declared"]),
        "rows_sum_gaps_equal_declared_minus_last": sum(
            1 for row in rows if row["offset_count"]
            and row["sum_gaps"] == row["declared_variable_data_size"]
            - (row["last_parameter_size"] or 0)),
        # H1 and H2 are vacuous when a Material declares no variables, so the same
        # relations are also scored only over rows that actually carry descriptors.
        "h1_sum_equals_declared_with_descriptors": sum(
            1 for row in with_descriptors if row["h1_sum_equals_declared"]),
        "h2_highest_end_equals_declared_with_descriptors": sum(
            1 for row in with_descriptors if row["h2_highest_end_equals_declared"]),
        "h5_used_extent_equals_declared": sum(
            1 for row in with_descriptors
            if row["max_offset"] is not None and row["last_parameter_size"] is not None
            and row["max_offset"] + row["last_parameter_size"]
            == row["declared_variable_data_size"]),
        "h6_used_extent_equals_declared_minus_24": sum(
            1 for row in with_descriptors
            if row["max_offset"] is not None and row["last_parameter_size"] is not None
            and row["max_offset"] + row["last_parameter_size"]
            == row["declared_variable_data_size"] - 24),
        "materials_with_descriptors": len(with_descriptors),
    }


def _merge_gap_rows(rows: list[dict[str, Any]]) -> dict[str, int]:
    merged: Counter[int] = Counter()
    for row in rows:
        for size, count in row["gap_sizes"].items():
            merged[int(size)] += count
    return {str(key): value for key, value in sorted(merged.items())}


def scan(data_dir: Path, package_filter: list[str], max_packages: int,
          max_materials_per_package: int) -> dict[str, Any]:
    from hd2_material import HD2ResourceError, parse_material_resource
    from hd2_material_census import (
        _material_entry_rows,
        _read_material_payloads,
        SIDECAR_SUFFIXES,
    )
    from hd2_resources import SlimBundleIndex

    index = SlimBundleIndex(data_dir)
    names = [name for name in sorted(index.packages) if not name.endswith(SIDECAR_SUFFIXES)]
    if package_filter:
        names = [name for name in names if any(name.startswith(prefix) or prefix in name
                                               for prefix in package_filter)]
    if max_packages:
        names = names[:max_packages]

    rows: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []
    per_package: dict[str, int] = {}
    for package_name in names:
        try:
            entries, _declared, actual, _rows_in_package, _warnings = _material_entry_rows(
                index, package_name)
        except (HD2ResourceError, OSError, ValueError) as exc:
            failures.append({"package": package_name, "stage": "entry_rows", "error": str(exc)})
            continue
        kept = 0
        for entry, payload, read_error in _read_material_payloads(index, package_name, entries):
            file_id = f'{entry["file_id"]:016x}'
            if read_error is not None:
                failures.append({"package": package_name, "file_id": file_id,
                                 "stage": "read", "error": read_error})
                continue
            try:
                parsed = parse_material_resource(payload)
            except (HD2ResourceError, ValueError, struct_error()) as exc:
                failures.append({"package": package_name, "file_id": file_id,
                                 "stage": "parse", "error": str(exc)})
                continue
            row = inspect_payload(parsed)
            row["package"] = package_name
            row["file_id"] = file_id
            rows.append(row)
            kept += 1
            if max_materials_per_package and kept >= max_materials_per_package:
                break
        per_package[package_name] = kept
    return {"packages_considered": len(names), "per_package": per_package,
            "rows": rows, "failures": failures}


def struct_error() -> type:
    import struct

    return struct.error


def render_markdown(report: dict[str, Any], summary: dict[str, Any]) -> str:
    lines = [
        "# HD2 Material Variable-Boundary Validation",
        "",
        "- Packages considered: {}; Materials inspected: {}; failures: {}".format(
            report["packages_considered"], summary["materials"], len(report["failures"])),
        "- Materials per package cap: {}; per-package counts shown below".format(
            report["limits"]["max_materials_per_package"] or "none"),
        "",
        "## Hypothesis hits",
        "",
        "| Hypothesis | Materials matching | Rate |",
        "|---|---:|---:|",
    ]
    labels = {
        "h1_sum_equals_declared": "H1 sum(elements x stride) == VariableDataSize",
        "h1_alt_sum_with_floor_equals_declared": "H1' same, with a component-size floor on stride",
        "h2_highest_end_equals_declared": "H2 highest used value end == VariableDataSize",
        "h2_alt_within_declared": "H2' highest used value end <= VariableDataSize",
        "h3_end_offset_equals_resource_size": "H3 EndOffset == resource size",
        "h4_end_offset_equals_payload_start_plus_declared": "H4 EndOffset == payload start + VariableDataSize",
        "h4_alt_plus_tail": "H4' EndOffset == payload start + VariableDataSize + tail",
        "variable_data_size_equals_remaining_payload": "VariableDataSize exactly fills the remaining bytes",
        "declared_size_exceeds_available": "Declared size exceeds available bytes",
        "rows_with_out_of_range_descriptors": "Materials with a descriptor outside the region",
        "rows_with_stride_below_component": "Materials with stride below the class component size",
        "rows_with_arrays": "Materials with array descriptors (elements > 1)",
        "rows_with_warnings": "Materials producing decoder warnings",
    }
    lines.append("")
    lines.append("## Descriptor-bearing subset")
    lines.append("")
    lines.append("- Materials carrying at least one descriptor: {}".format(
        summary["materials_with_descriptors"]))
    lines.append("- H1 (sum of elements x stride) on that subset: {}".format(
        summary["h1_sum_equals_declared_with_descriptors"]))
    lines.append("- H2 (highest used value end) on that subset: {}".format(
        summary["h2_highest_end_equals_declared_with_descriptors"]))
    lines.append("- H5 (highest offset + last size == VariableDataSize): {}".format(
        summary["h5_used_extent_equals_declared"]))
    lines.append("- H6 (highest offset + last size == VariableDataSize - 24): {}".format(
        summary["h6_used_extent_equals_declared_minus_24"]))
    lines.append("- Offsets tile the region (sum of gaps == declared - last size): {}".format(
        summary["rows_sum_gaps_equal_declared_minus_last"]))
    lines.append("- Reserved-word nonzero counts across all descriptors: {}".format(
        summary["reserved_word_nonzero"]))
    for key, label in labels.items():
        hits = summary["hypothesis_hits"][key]
        lines.append("| {} | {} | {} |".format(label, hits, summary["hypothesis_rates"][key]))
    lines.extend([
        "",
        "## Distributions",
        "",
        "- Trailing bytes after the declared region: {}".format(
            summary["trailing_size_distribution"]),
        "- resource size minus EndOffset: {}".format(summary["resource_minus_end_offset_distribution"]),
        "- EndOffset minus (payload start + declared): {}".format(
            summary["end_offset_minus_region_distribution"]),
        "- Reserved-word nonzero counts (word0/1/4 are what the decoder calls "
        "class/elements/stride): {}".format(summary["reserved_word_nonzero"]),
        "- Offset gap sizes across all Materials: {}".format(summary["offset_gap_distribution"]),
        "- Last-parameter size (declared minus highest offset): {}".format(
            summary["last_parameter_size_distribution"]),
        "- Materials whose gaps plus last size fill the declared region: {} of {} "
        "with descriptors".format(summary["rows_sum_gaps_equal_declared_minus_last"],
                                  summary["rows_with_any_descriptor"]),
        "- Descriptors total: {}; array descriptors: {}; outside-region: {}; "
        "stride-below-component: {}".format(summary["descriptor_total"],
                                            summary["array_descriptor_total"],
                                            summary["out_of_range_descriptor_total"],
                                            summary["descriptor_stride_below_component_total"]),
        "",
        "## Per-package Material counts",
        "",
        "| Package | Materials inspected |",
        "|---|---:|",
    ])
    for package_name, count in sorted(report["per_package"].items(), key=lambda kv: -kv[1]):
        lines.append("| `{}` | {} |".format(package_name, count))
    if report["failures"]:
        lines.extend(["", "## Failures", ""])
        seen: dict[str, int] = {}
        for row in report["failures"]:
            key = "{} / {}".format(row["stage"], row["error"][:90])
            seen[key] = seen.get(key, 0) + 1
        for key, count in sorted(seen.items(), key=lambda kv: -kv[1])[:20]:
            lines.append("- {} ({} occurrence(s))".format(key, count))
    lines.extend([
        "",
        "## Interpretation limits",
        "",
        "- A hypothesis is a stored-data arithmetic relation, not a runtime claim; matching does not prove how the shader consumes the values.",
        "- Materials whose payload could not be read or parsed are counted as failures and excluded from the hypothesis tallies.",
        "- Class-to-component-size mapping is applied only for the four known classes; unknown classes contribute their stored stride alone.",
        "- With a per-package cap the sample is the first N Materials of each package, not a random selection.",
        "",
    ])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_data", help="HD2 data directory containing bundles.nxa")
    parser.add_argument("--package", action="append", default=[],
                        help="Only packages whose name contains this value; repeat")
    parser.add_argument("--max-packages", type=int, default=0)
    parser.add_argument("--max-materials-per-package", type=int, default=0,
                        help="Cap Materials inspected per package; 0 means every Material")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    if args.max_packages < 0 or args.max_materials_per_package < 0:
        parser.error("--max-packages and --max-materials-per-package must be zero or positive")

    data_dir = Path(args.game_data)
    if not data_dir.is_dir():
        parser.error("Game data directory does not exist: {}".format(data_dir))

    report = scan(data_dir, args.package, args.max_packages, args.max_materials_per_package)
    report["limits"] = {"max_packages": args.max_packages,
                        "max_materials_per_package": args.max_materials_per_package,
                        "package_filter": args.package}
    summary = tally(report["rows"])
    payload = {"schema": "SourceModelReader-HD2MaterialVariableBoundary1",
               "report": report, "summary": summary}
    markdown = render_markdown(report, summary)

    print("Materials inspected: {} across {} package(s); failures: {}".format(
        summary.get("materials", 0), report["packages_considered"], len(report["failures"])))
    if summary.get("materials"):
        hits = summary["hypothesis_hits"]
        print("  H1 sum==declared: {}  H2 end==declared: {}  H3 end==size: {}  "
              "H4 end==start+declared: {}  out-of-range descriptors: {}".format(
                  hits["h1_sum_equals_declared"], hits["h2_highest_end_equals_declared"],
                  hits["h3_end_offset_equals_resource_size"],
                  hits["h4_end_offset_equals_payload_start_plus_declared"],
                  hits["rows_with_out_of_range_descriptors"]))
        print("  trailing bytes: {}".format(summary["trailing_size_distribution"]))
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
