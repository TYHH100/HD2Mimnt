#!/usr/bin/env python3
"""Profile every model inside one Source asset bundle for LOD coverage and skeleton spread.

Two open roadmap lines need more than one compiled model: the per-LOD bone-use question,
because the checked-in sample carries a single VVD LOD and no fixups, and the 1.0.0
requirement to test an independent model rather than one file. This pass enumerates the
MDL entries of a GMA, ZIP or directory, then records for each model what the stored
headers say (VVD and VTX LOD counts, fixups, bone and bodygroup counts) and which bones
are positively weighted at each LOD.

Read-only: models are parsed, never rewritten, and geometry is collected per LOD from the
stored pools without modifying them.
"""
from __future__ import annotations

import argparse
import json
import sys
import zipfile
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from source_model_reader import GMAArchive, ReaderError, open_model  # noqa: E402


def _count(value: Any) -> int:
    """Accept either a stored list or an already-counted integer."""
    if isinstance(value, (list, tuple, dict, set)):
        return len(value)
    if isinstance(value, bool):
        return 0
    if isinstance(value, int):
        return value
    return 0


def discover_models(input_path: Path) -> list[str]:
    """List MDL member paths for a GMA, ZIP or directory; a single file yields itself."""
    if input_path.is_file() and input_path.suffix.lower() == ".mdl":
        return [input_path.name]
    if input_path.is_dir():
        return sorted(str(path.relative_to(input_path)).replace("\\", "/")
                      for path in input_path.rglob("*.mdl"))
    if input_path.is_file() and input_path.suffix.lower() == ".gma":
        archive = GMAArchive(input_path)
        return sorted(entry.path for entry in archive.entries
                      if entry.path.lower().endswith(".mdl"))
    if input_path.is_file() and input_path.suffix.lower() in (".zip", ".pk3"):
        with zipfile.ZipFile(input_path) as archive:
            return sorted(name for name in archive.namelist()
                          if name.lower().endswith(".mdl"))
    raise ReaderError(f"Unsupported input for model discovery: {input_path}")


def lod_bone_usage(model, lod: int, skin: int, choices: dict[int, int]) -> dict[str, Any]:
    """Collect the positively weighted bone set of one LOD for a bodygroup combination."""
    triangles, included = model.collect_geometry(lod, skin, choices)
    usage = model._weighted_bone_usage_for_triangles(triangles)
    names = [usage["weight_referenced_bones"][index]["name"]
             for index in range(len(usage["weight_referenced_bones"]))]
    return {
        "lod_index": lod,
        "triangles": len(triangles),
        "triangles_included": len(included) if isinstance(included, (list, tuple)) else None,
        "vertices": usage["vertex_count"],
        "weighted_bone_indices": list(usage["weight_referenced_bone_indices"]),
        "weighted_bone_count": usage["weighted_bone_count"],
        "weighted_bone_names": names,
        "unweighted_vertices": usage["unweighted_vertices"],
        "invalid_bone_references": usage["invalid_bone_references"],
    }


def lod_deltas(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Compare every LOD's weighted bone set against LOD 0."""
    if not rows:
        return []
    base = set(rows[0]["weighted_bone_indices"])
    deltas = []
    for row in rows:
        current = set(row["weighted_bone_indices"])
        deltas.append({
            "lod_index": row["lod_index"],
            "added_vs_lod0": sorted(current - base),
            "removed_vs_lod0": sorted(base - current),
            "same_as_lod0": current == base,
        })
    return deltas


def profile_model(input_path: Path, member: str, backend: str) -> dict[str, Any]:
    """Read one model's header counts and per-LOD weighted bone sets."""
    model = open_model(str(input_path), member, backend)
    try:
        info = model.inspect_dict()
        vvd = info.get("vvd") or {}
        vtx = info.get("vtx") or {}
        mdl = info.get("mdl") or {}
        vvd_lods = int(vvd.get("lod_count") or 0)
        vtx_lods = int(vtx.get("lod_count") or 0)
        default_choices = {group.index: 0 for group in model.bodyparts}
        rows = [lod_bone_usage(model, lod, 0, default_choices)
                for lod in range(max(1, vvd_lods))]
        bone_names = [bone.name for bone in model.bones]
        return {
            "member": member,
            "mdl_name": mdl.get("name"),
            "mdl_version": mdl.get("version"),
            "bone_count": len(model.bones),
            "bodygroup_count": len(model.bodyparts),
            "bodygroup_options": sum(_count(group.models) for group in model.bodyparts),
            "texture_count": _count(mdl.get("textures")),
            "skin_family_count": _count(mdl.get("skin_families")),
            "vvd_version": vvd.get("version"),
            "vvd_lod_count": vvd_lods,
            "vvd_lod0_vertices": vvd.get("lod0_vertices"),
            "vvd_fixups": (vvd.get("fixups") or {}).get("count")
            if isinstance(vvd.get("fixups"), dict) else vvd.get("fixups"),
            "vtx_version": vtx.get("version"),
            "vtx_lod_count": vtx_lods,
            "multi_lod": vvd_lods > 1,
            "lod_usage": rows,
            "lod_deltas_vs_lod0": lod_deltas(rows),
            "skeleton_signature": sorted(name.lower() for name in bone_names),
            "warnings": info.get("warnings") or [],
        }
    finally:
        bundle = getattr(model, "bundle", None)
        if bundle is not None and hasattr(bundle, "close"):
            bundle.close()


def skeleton_comparison(profiles: list[dict[str, Any]]) -> dict[str, Any]:
    """Group models by skeleton so an independent model is visible, not assumed."""
    by_signature: dict[tuple[str, ...], list[str]] = {}
    for profile in profiles:
        key = tuple(profile["skeleton_signature"])
        by_signature.setdefault(key, []).append(profile["member"])
    groups = [{"bone_count": len(signature), "members": members}
              for signature, members in by_signature.items()]
    groups.sort(key=lambda group: (-len(group["members"]), -group["bone_count"]))
    return {
        "distinct_skeletons": len(groups),
        "groups": groups,
        "shared_bone_name_union": sorted(set().union(
            *(set(profile["skeleton_signature"]) for profile in profiles))
            if profiles else set()),
    }


def summarize(profiles: list[dict[str, Any]], failures: list[dict[str, str]]) -> dict[str, Any]:
    multi = [profile for profile in profiles if profile["multi_lod"]]
    return {
        "models_profiled": len(profiles),
        "models_failed": len(failures),
        "models_with_multiple_vvd_lods": len(multi),
        "max_vvd_lod_count": max((profile["vvd_lod_count"] for profile in profiles), default=0),
        "max_vtx_lod_count": max((profile["vtx_lod_count"] for profile in profiles), default=0),
        "models_with_fixups": sum(1 for profile in profiles
                                  if isinstance(profile["vvd_fixups"], int)
                                  and profile["vvd_fixups"] > 0),
        "lod_rows_with_bone_delta": sum(
            1 for profile in profiles for delta in profile["lod_deltas_vs_lod0"]
            if not delta["same_as_lod0"]),
    }


def render_markdown(input_path: Path, profiles: list[dict[str, Any]],
                    failures: list[dict[str, str]], summary: dict[str, Any],
                    comparison: dict[str, Any]) -> str:
    lines = [
        "# Source Model LOD Coverage Survey",
        "",
        "- Input: `{}`".format(input_path),
        "- Models profiled: {}; failures: {}".format(summary["models_profiled"],
                                                     summary["models_failed"]),
        "- Models with more than one VVD LOD: {}".format(summary["models_with_multiple_vvd_lods"]),
        "- Highest VVD LOD count: {}; highest VTX LOD count: {}".format(
            summary["max_vvd_lod_count"], summary["max_vtx_lod_count"]),
        "- Models carrying VVD fixups: {}".format(summary["models_with_fixups"]),
        "- LOD rows whose weighted bone set differs from their own LOD 0: {}".format(
            summary["lod_rows_with_bone_delta"]),
        "- Distinct skeletons: {}".format(comparison["distinct_skeletons"]),
        "",
        "| Model | Bones | Bodygroups | Options | Textures | VVD lods | VTX lods | Fixups | LOD 0 bones | All LOD bones |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---|",
    ]
    for profile in profiles:
        rows = profile["lod_usage"]
        first = rows[0]["weighted_bone_count"] if rows else 0
        every = sorted({count for row in rows for count in [row["weighted_bone_count"]]})
        lines.append("| `{}` | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
            profile["member"], profile["bone_count"], profile["bodygroup_count"],
            profile["bodygroup_options"], profile["texture_count"], profile["vvd_lod_count"],
            profile["vtx_lod_count"], profile["vvd_fixups"], first,
            ", ".join(str(value) for value in every)))
    lines.extend(["", "## Skeleton groups", ""])
    for group in comparison["groups"]:
        lines.append("- {} bones: {}".format(group["bone_count"], ", ".join(
            "`{}`".format(member) for member in group["members"])))
    lines.extend(["", "## Per-LOD bone sets", ""])
    for profile in profiles:
        if profile["vvd_lod_count"] <= 1:
            lines.append("- `{}`: single VVD LOD, so per-LOD bone use cannot be measured "
                         "on this model.".format(profile["member"]))
            continue
        lines.append("- `{}`: {} VVD LODs".format(profile["member"], profile["vvd_lod_count"]))
        for row, delta in zip(profile["lod_usage"], profile["lod_deltas_vs_lod0"]):
            lines.append("    - LOD {}: {} vertices, {} triangles, {} weighted bones, "
                         "{} added / {} removed vs LOD 0".format(
                             row["lod_index"], row["vertices"], row["triangles"],
                             row["weighted_bone_count"], len(delta["added_vs_lod0"]),
                             len(delta["removed_vs_lod0"])))
    if failures:
        lines.extend(["", "## Failures", ""])
        for row in failures:
            lines.append("- `{}`: {}".format(row["member"], row["error"]))
    lines.extend([
        "",
        "## Interpretation limits",
        "",
        "- Counts come from the compiled headers and the stored vertex pools; nothing was rendered or converted.",
        "- Per-LOD bone sets are measured with every BodyGroup at its default choice, so a bodygroup-only bone can still look shared.",
        "- A model with one VVD LOD cannot contribute to the per-LOD question at all; it is listed rather than skipped so the coverage gap stays visible.",
        "- Skeleton groups compare lowercased bone-name sets, which is a name-level comparison, not a semantic mapping.",
        "",
    ])
    return "\n".join(lines) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", help=".gma, .zip/.pk3, model directory or one .mdl file")
    parser.add_argument("--backend", choices=("auto", "dx90", "dx80"), default="auto")
    parser.add_argument("--model", action="append", default=[],
                        help="Profile only these MDL member paths; repeat")
    parser.add_argument("--limit", type=int, default=0, help="Stop after this many models")
    parser.add_argument("--output-json", type=Path)
    parser.add_argument("--output-markdown", type=Path)
    args = parser.parse_args()

    if args.limit < 0:
        parser.error("--limit must be zero or positive")

    input_path = Path(args.input)
    if not input_path.exists():
        parser.error("Input does not exist: {}".format(input_path))

    try:
        members = args.model or discover_models(input_path)
    except (ReaderError, OSError, zipfile.BadZipFile) as exc:
        parser.error("Could not list models in {}: {}".format(input_path, exc))
    if not members:
        parser.error("No MDL members found in {}".format(input_path))

    profiles: list[dict[str, Any]] = []
    failures: list[dict[str, str]] = []
    for member in members:
        if args.limit and len(profiles) >= args.limit:
            break
        try:
            profiles.append(profile_model(input_path, member, args.backend))
        except (ReaderError, OSError, ValueError, KeyError) as exc:
            failures.append({"member": member, "error": str(exc)})
        print("profiled {}: {} bones, vvd_lods={}, vtx_lods={}".format(
            member, profiles[-1]["bone_count"] if profiles else "-",
            profiles[-1]["vvd_lod_count"] if profiles else "failed",
            profiles[-1]["vtx_lod_count"] if profiles else "failed"))

    summary = summarize(profiles, failures)
    comparison = skeleton_comparison(profiles)
    payload = {
        "schema": "SourceModelReader-SourceModelLodCoverage1",
        "input": str(input_path),
        "discovered_models": len(members),
        "summary": summary,
        "skeleton_comparison": comparison,
        "models": profiles,
        "failures": failures,
    }
    markdown = render_markdown(input_path, profiles, failures, summary, comparison)

    print("Models: {} profiled, {} failed, {} with multiple VVD LODs, {} distinct skeletons".format(
        summary["models_profiled"], summary["models_failed"],
        summary["models_with_multiple_vvd_lods"], comparison["distinct_skeletons"]))
    if args.output_json:
        args.output_json.parent.mkdir(parents=True, exist_ok=True)
        args.output_json.write_text(json.dumps(payload, ensure_ascii=False, indent=2),
                                    encoding="utf-8")
    if args.output_markdown:
        args.output_markdown.parent.mkdir(parents=True, exist_ok=True)
        args.output_markdown.write_text(markdown, encoding="utf-8")
    return 0 if profiles else 1


if __name__ == "__main__":
    raise SystemExit(main())
