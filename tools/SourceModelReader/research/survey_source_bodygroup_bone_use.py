#!/usr/bin/env python3
"""Split compiled-Source bone use by BodyGroup choice instead of one union.

The existing reports derive the active Source bone set from the whole stored VVD
vertex pool, which is a union across every BodyGroup choice. A retarget table
built on that union would carry bones no single worn combination ever touches.
This survey reuses the existing reconstruction APIs and measures bone use per
BodyGroup choice, then derives the shared core and the per-choice remainder.

Read-only over the compiled model: no vertex, weight or resource bytes are
modified, and the stored pool is never rewritten.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from source_model_reader import ReaderError, open_model  # noqa: E402


def usage_by_choice(model, lod: int, skin: int) -> list[dict[str, Any]]:
    """Collect bone IDs for every BodyGroup choice against an all-default baseline.

    Triangles returned by the reader carry only their material, so a single-group
    call cannot isolate one choice's geometry. Each row therefore records the bone
    set of the whole combination with that choice selected, and the caller reads
    the gain and loss against the default combination.
    """
    default = {group.index: 0 for group in model.bodyparts}
    baseline = bone_set(model, default, lod, skin)
    rows: list[dict[str, Any]] = []
    for group in model.bodyparts:
        for choice, part in enumerate(group.models):
            choices = dict(default)
            choices[group.index] = choice
            used = bone_set(model, choices, lod, skin)
            rows.append({
                "bodygroup": group.index,
                "name": group.name,
                "choice": choice,
                "model_name": part.name,
                "is_default": choice == 0,
                "bone_indices": sorted(used),
                "bone_count": len(used),
                "added_over_default": sorted(used - baseline),
                "removed_vs_default": sorted(baseline - used),
            })
    return rows


def bone_set(model, choices: dict[int, int], lod: int, skin: int) -> set[int]:
    triangles, _included = model.collect_geometry(lod, skin, choices)
    usage = model._weighted_bone_usage_for_triangles(triangles)
    indices = usage.get("weight_referenced_bone_indices") or []
    return {value for value in indices if isinstance(value, int) and not isinstance(value, bool)}


def derive_core(rows: list[dict[str, Any]]) -> dict[str, Any]:
    """Derive the shared core and the per-choice remainder from combination use sets."""
    populated = [row for row in rows if row["bone_indices"]]
    if not populated:
        return {"choice_count": 0, "core": [], "core_size": 0, "union": [], "union_size": 0,
                "per_choice_min": 0, "per_choice_median": 0, "per_choice_max": 0,
                "varying_choices": 0, "choices": []}
    sets = [set(row["bone_indices"]) for row in populated]
    core = sorted(set.intersection(*sets))
    union = sorted(set.union(*sets))
    sizes = sorted(len(value) for value in sets)
    return {
        "choice_count": len(populated),
        # Every row is a whole combination, so this intersection is the part that
        # no BodyGroup choice can remove; it is not a per-choice isolated set.
        "core": core,
        "core_size": len(core),
        "union": union,
        "union_size": len(union),
        "per_choice_min": sizes[0],
        "per_choice_max": sizes[-1],
        "per_choice_median": sizes[len(sizes) // 2],
        "varying_choices": sum(
            1 for row in populated if row["added_over_default"] or row["removed_vs_default"]
        ),
        "choices": [
            {
                "bodygroup": row["bodygroup"], "name": row["name"], "choice": row["choice"],
                "model_name": row["model_name"], "is_default": row["is_default"],
                "bone_count": row["bone_count"],
                "added_over_default_count": len(row["added_over_default"]),
                "removed_vs_default_count": len(row["removed_vs_default"]),
                "added_over_default": row["added_over_default"],
                "removed_vs_default": row["removed_vs_default"],
            }
            for row in populated
        ],
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help=".mdl file, model folder, ZIP archive or uncompressed .gma")
    parser.add_argument("--model", help="MDL path inside a folder, ZIP or GMA")
    parser.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    parser.add_argument("--lod", type=int, default=0, help="LOD index to reconstruct per choice")
    parser.add_argument("--skin", type=int, default=0, help="Skin family index")
    parser.add_argument("--output-json", type=Path)
    args = parser.parse_args()

    if args.lod < 0 or args.skin < 0:
        parser.error("--lod and --skin must be zero or positive")

    try:
        model = open_model(args.input, args.model, args.backend)
        rows = usage_by_choice(model, args.lod, args.skin)
        core = derive_core(rows)
    except (ReaderError, OSError, ValueError, TypeError, KeyError) as exc:
        parser.error(f"BodyGroup bone-use survey failed: {exc}")

    report = {
        "schema": "SourceModelReader-SourceBodygroupBoneUse1",
        "model": str(args.model or args.input),
        "lod": args.lod,
        "skin": args.skin,
        "declared_bone_count": len(model.bones),
        "stored_pool_union_note": (
            "The existing reports derive active bones from the whole stored VVD pool; this "
            "survey derives them per BodyGroup choice from reconstructed geometry."
        ),
        "vmd_fixup_note": (
            "VVD fixup records are still not applied by this survey, so choice sets are read "
            "from the stored pool offsets the reconstruction already uses."
        ),
        "summary": core,
    }
    print("BodyGroup bone-use survey: {} combinations, invariant core {} bones, union {} bones, per-combination min/median/max {}/{}/{}; {} combinations differ from the default".format(
        core["choice_count"], core["core_size"], core["union_size"],
        core.get("per_choice_min", 0), core.get("per_choice_median", 0), core.get("per_choice_max", 0),
        core.get("varying_choices", 0)))
    for choice in core["choices"][:40]:
        if not choice["added_over_default_count"] and not choice["removed_vs_default_count"]:
            continue
        print("  group {} choice {} [{}]: {} bones, +{} -{} vs default".format(
            choice["bodygroup"], choice["choice"], choice["model_name"],
            choice["bone_count"], choice["added_over_default_count"],
            choice["removed_vs_default_count"]))
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"JSON: {args.output_json}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
