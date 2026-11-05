#!/usr/bin/env python3
"""Resolve unresolved HD2 TransformInfo name hashes through a Unit union.

Each Unit JSON only names the hashes present in its own Bones resource, so a
transform name hash that looks unresolved in one Unit may already be known in a
sibling Unit of the same package. This script unions the name tables of every
Unit given to it, re-resolves the hashes, and reports what the union buys.

It also records which hash implementations were tested against the observed
(name, hash) pairs, so a candidate SDK hash can be checked in one command
instead of being guessed at. Nothing is guessed here: a hash is only ever
assigned the name whose stored pair produces it.
"""
from __future__ import annotations

import argparse
import functools
import json
import struct
import sys
import zlib
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

MASK32 = 0xFFFFFFFF


def fnv1a(bytes_data: bytes, offset: int = 2166136261, prime: int = 16777619) -> int:
    digest = offset
    for byte in bytes_data:
        digest = ((digest ^ byte) * prime) & MASK32
    return digest


def djb2(bytes_data: bytes) -> int:
    digest = 5381
    for byte in bytes_data:
        digest = ((digest * 33) + byte) & MASK32
    return digest


def sdbm(bytes_data: bytes) -> int:
    digest = 0
    for byte in bytes_data:
        digest = (byte + (digest << 6) + (digest << 16) - digest) & MASK32
    return digest


def jenkins_one_at_a_time(bytes_data: bytes) -> int:
    digest = 0
    for byte in bytes_data:
        digest = (digest + byte) & MASK32
        digest = (digest + (digest << 10)) & MASK32
        digest ^= digest >> 6
    digest = (digest + (digest << 3)) & MASK32
    digest ^= digest >> 11
    digest = (digest + (digest << 15)) & MASK32
    return digest


def murmur2_32(bytes_data: bytes, seed: int = 0) -> int:
    length = len(bytes_data)
    digest = seed ^ length
    index = 0
    remaining = length
    multiplier = 0x5BD1E995
    while remaining >= 4:
        k = int.from_bytes(bytes_data[index:index + 4], "little")
        k = (k * multiplier) & MASK32
        k ^= k >> 24
        k = (k * multiplier) & MASK32
        digest = (digest * multiplier) & MASK32
        digest ^= k
        index += 4
        remaining -= 4
    if remaining == 3:
        digest ^= bytes_data[index + 2] << 16
    if remaining >= 2:
        digest ^= bytes_data[index + 1] << 8
    if remaining >= 1:
        digest ^= bytes_data[index]
        digest = (digest * multiplier) & MASK32
    digest ^= digest >> 13
    digest = (digest * multiplier) & MASK32
    digest ^= digest >> 15
    return digest


def crc32(bytes_data: bytes) -> int:
    return zlib.crc32(bytes_data)


def fnv1a_casefold(name: str) -> int:
    return fnv1a(name.casefold().encode("utf-8"))


# Candidate string hashes for the observed name pairs. Each entry takes a name and
# returns a 32-bit value; encodings that need bytes are wrapped here so the table
# stays uniform.
HASH_CANDIDATES: dict[str, Callable[[str], int]] = {
    "fnv1a_utf8": lambda name: fnv1a(name.encode("utf-8")),
    "fnv1a_ascii": lambda name: fnv1a(name.encode("ascii", "replace")),
    "fnv1a_utf8_casefold": fnv1a_casefold,
    "fnv1a_utf16le": lambda name: fnv1a(name.encode("utf-16-le")),
    "fnv1a_utf16le_casefold": lambda name: fnv1a(name.casefold().encode("utf-16-le")),
    "fnv1a_nul_terminated": lambda name: fnv1a(name.encode("utf-8") + b"\0"),
    "fnv1a_with_length_prefix": lambda name: fnv1a(
        struct.pack("<I", len(name)) + name.encode("utf-8")),
    "fnv1a_le_offset": lambda name: functools.reduce(
        lambda acc, byte: ((acc ^ byte) * 16777619) & MASK32,
        name.encode("utf-8"), 0x85EBCA6B),
    "djb2_utf8": lambda name: djb2(name.encode("utf-8")),
    "djb2_utf8_casefold": lambda name: djb2(name.casefold().encode("utf-8")),
    "sdbm_utf8": lambda name: sdbm(name.encode("utf-8")),
    "jenkins_one_at_a_time": lambda name: jenkins_one_at_a_time(name.encode("utf-8")),
    "murmur2_32_seed0": lambda name: murmur2_32(name.encode("utf-8"), 0),
    "crc32_utf8": lambda name: crc32(name.encode("utf-8")),
}


def load_name_pairs(path: Path, label: str) -> tuple[dict[str, int], list[dict[str, Any]]]:
    """Read one Unit JSON: its (name, hash) pairs plus its transform hash list."""
    report = json.loads(path.read_text(encoding="utf-8"))
    pairs: dict[str, int] = {}
    for row in (report.get("bones_resource") or {}).get("bones") or []:
        if not isinstance(row, dict):
            continue
        name = row.get("name")
        value = row.get("hash")
        if isinstance(name, str) and isinstance(value, int) and not isinstance(value, bool):
            pairs[name] = value
    transforms = []
    skeleton = {
        row["transform_index"]: row.get("name")
        for row in (report.get("transform_info") or {}).get("resolved_skeleton") or []
        if isinstance(row, dict) and isinstance(row.get("transform_index"), int)
    }
    for row in (report.get("transform_info") or {}).get("transforms") or []:
        if not isinstance(row, dict):
            continue
        index = row.get("index")
        value = row.get("name_hash")
        if not isinstance(index, int) or not isinstance(value, int) or isinstance(value, bool):
            continue
        transforms.append({
            "unit_label": label,
            "unit_id": (report.get("unit") or {}).get("file_id"),
            "transform_index": index,
            "name_hash": value,
            "resolved_in_own_unit": skeleton.get(index) is not None,
            "own_unit_name": skeleton.get(index),
        })
    del report
    return pairs, transforms


def build_hash_index(pairs: dict[str, int]) -> dict[int, set[str]]:
    index: dict[int, set[str]] = {}
    for name, value in pairs.items():
        index.setdefault(value, set()).add(name)
    return index


def classify_transforms(transforms: list[dict[str, Any]],
                        hash_to_names: dict[int, set[str]]) -> dict[str, int]:
    """Assign each transform row a resolution status from stored pairs only."""
    tally: dict[str, int] = {}
    for row in transforms:
        names = sorted(hash_to_names.get(row["name_hash"], set()))
        if row["resolved_in_own_unit"]:
            row["union_status"] = "resolved_in_own_unit"
        elif len(names) == 1:
            row["union_status"] = "resolved_by_union"
            row["union_name"] = names[0]
        elif len(names) > 1:
            row["union_status"] = "ambiguous_by_union"
            row["union_candidates"] = names
        else:
            row["union_status"] = "still_unresolved"
        tally[row["union_status"]] = tally.get(row["union_status"], 0) + 1
    return dict(sorted(tally.items()))


def check_hash_candidates(pairs: dict[str, int]) -> list[dict[str, Any]]:
    rows = []
    for name, function in HASH_CANDIDATES.items():
        matched = 0
        for text, value in pairs.items():
            try:
                if function(text) == value:
                    matched += 1
            except (UnicodeError, TypeError, ValueError):
                pass
        rows.append({"candidate": name, "matched": matched, "total": len(pairs)})
    return sorted(rows, key=lambda row: (-row["matched"], row["candidate"]))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unit", action="append", required=True, metavar="LABEL=PATH",
                        help="Unit JSON report; repeat for every Unit whose names may help")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    all_pairs: dict[str, int] = {}
    transforms: list[dict[str, Any]] = []
    per_unit_pairs: dict[str, int] = {}
    for item in args.unit:
        label, separator, path_text = item.partition("=")
        if not separator or not label or not path_text:
            parser.error("--unit expects LABEL=PATH")
        path = Path(path_text)
        if not path.is_file():
            parser.error(f"Unit JSON does not exist: {path}")
        try:
            pairs, rows = load_name_pairs(path, label)
        except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
            parser.error(f"Name resolution failed for {path}: {exc}")
        per_unit_pairs[label] = len(pairs)
        for name, value in pairs.items():
            all_pairs.setdefault(name, value)
        for row in rows:
            transforms.append(row)
    hash_to_names = build_hash_index(all_pairs)
    tally = classify_transforms(transforms, hash_to_names)
    report = {
        "schema": "SourceModelReader-HD2NameHashResolution1",
        "units": len(args.unit),
        "name_pair_count_union": len(all_pairs),
        "per_unit_pair_counts": dict(sorted(per_unit_pairs.items())),
        "hash_functions_tested": check_hash_candidates(all_pairs),
        "tally": dict(sorted(tally.items())),
        "newly_resolved": [
            {"transform_index": row["transform_index"], "name_hash": f"{row['name_hash']:08x}",
             "unit_label": row["unit_label"], "name": row.get("union_name")}
            for row in transforms if row["union_status"] == "resolved_by_union"
        ],
        "still_unresolved": sorted({
            f"{row['name_hash']:08x}" for row in transforms if row["union_status"] == "still_unresolved"
        }),
        "ambiguous": [
            {"name_hash": f"{row['name_hash']:08x}", "candidates": row.get("union_candidates", []),
             "unit_label": row["unit_label"], "transform_index": row["transform_index"]}
            for row in transforms if row["union_status"] == "ambiguous_by_union"
        ],
        "interpretation_limits": [
            "Resolution only uses stored (name, hash) pairs; a hash is never assigned a name that does not produce it.",
            "The union is only as wide as the Units passed in, so still_unresolved means unknown to this batch, not unknown to the game.",
            "No hash algorithm is claimed here: every candidate listed under hash_functions_tested is a refutation record, not a conclusion.",
            "TransformInfo counts include duplicates across Units; tallies are per (Unit, transform) row, not per distinct hash.",
        ],
    }

    lines = [
        "# HD2 Transform Name Hash Resolution by Unit Union",
        "",
        f"- Units in batch: {report['units']}",
        f"- Distinct (name, hash) pairs in the union: {report['name_pair_count_union']}",
        f"- Per-Unit pair counts: {', '.join(f'{k}={v}' for k, v in report['per_unit_pair_counts'].items())}",
        "",
        "| Status | Rows |",
        "|---|---:|",
    ]
    for name, count in report["tally"].items():
        lines.append(f"| {name} | {count} |")
    lines.extend([
        "",
        "## Newly resolved by the union",
        "",
    ])
    if report["newly_resolved"]:
        lines.extend(["| Unit | Transform | Hash | Name |", "|---|---:|---|---|"])
        for row in report["newly_resolved"]:
            lines.append("| {} | {} | {} | {} |".format(
                row["unit_label"], row["transform_index"], row["name_hash"], row["name"]))
    else:
        lines.append("- Nothing new: every hash resolvable in this batch was already known in its own Unit.")
    lines.extend(["", "## Still unresolved in this batch", ""])
    lines.append("- " + (", ".join(report["still_unresolved"]) or "none"))
    lines.extend(["", "## Hash candidate functions tested against the stored pairs", "",
                  "| Candidate | Matched | Total |", "|---|---:|---:|"])
    for row in report["hash_functions_tested"]:
        lines.append("| {} | {} | {} |".format(row["candidate"], row["matched"], row["total"]))
    lines.extend(["", "## Interpretation limits", ""])
    lines.extend("- " + item for item in report["interpretation_limits"])
    markdown = "\n".join(lines) + "\n"

    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")
    print("Union pairs {} tally {} best hash candidate {}".format(
        report["name_pair_count_union"], report["tally"],
        (report["hash_functions_tested"][0]["candidate"] + " "
         + f"{report['hash_functions_tested'][0]['matched']}/{report['hash_functions_tested'][0]['total']}")
        if report["hash_functions_tested"] else "none"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
