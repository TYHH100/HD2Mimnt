"""Batch-run compact skinning audits for explicitly selected HD2 Units.

Manifest format:
[
  {"name":"sample_a","package":"0123456789abcdef",
   "unit":"fedcba9876543210","geometry_source":"direct"}
]

Raw Unit JSON is held in a temporary directory and removed after each run.
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

from research.summarize_hd2_skinning import build_summary, render_markdown


def _run_one(root: Path, game_data: str, target: dict[str, Any], work: Path, python: str) -> dict[str, Any]:
    name = str(target["name"])
    package = str(target["package"])
    unit = str(target["unit"])
    source = str(target.get("geometry_source", "unspecified"))
    json_path = work / (name + ".json")
    command = [python, str(root / "source_model_reader.py"), "hd2-unit", game_data,
               "--package", package, "--unit", unit, "--json", str(json_path)]
    result = subprocess.run(command, cwd=root, text=True, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, check=False)
    row: dict[str, Any] = {
        "name": name, "package": package, "unit": unit,
        "geometry_source": source, "exit_code": result.returncode,
        "status": "failed", "stdout": result.stdout,
    }
    if result.returncode >= 2 or not json_path.is_file():
        row["failure"] = "hd2-unit failed or did not produce JSON"
        return row
    try:
        report = json.loads(json_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        row["failure"] = "Could not read Unit JSON: " + str(exc)
        return row
    compact = build_summary(report)
    row["status"] = (report.get("summary") or {}).get("status", "unknown")
    row["mesh_count"] = len(compact.get("meshes", []))
    sections = [section for mesh in compact.get("meshes", [])
                for section in mesh.get("sections", [])]
    sets = [item for section in sections for item in section.get("bone_index_sets", [])]
    row["section_count"] = len(sections)
    row["resolved_lanes"] = sum(int(s.get("resolved_active_influence_count", 0)) for s in sections)
    row["unresolved_lanes"] = sum(int(s.get("unresolved_active_influence_count", 0)) for s in sections)
    row["active_transforms"] = len(compact.get("active_transforms", []))
    row["mapping_conflicts"] = sum(len(s.get("mapping_conflict_local_indices", [])) for s in sets)
    row["audit_path"] = str((args_output_dir / (name + "_skinning_audit.md")).as_posix())
    Path(row["audit_path"]).write_text(render_markdown(compact), encoding="utf-8")
    return row


def main() -> int:
    global args_output_dir
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("game_data", help="HD2 data directory containing bundles.nxa")
    parser.add_argument("--manifest", type=Path, required=True, help="JSON list of package/unit targets")
    parser.add_argument("--output-dir", type=Path, default=Path("research/skinning_audits"))
    parser.add_argument("--python", default=sys.executable)
    args = parser.parse_args()

    root = Path(__file__).resolve().parents[1]
    args_output_dir = args.output_dir
    if not args_output_dir.is_absolute():
        args_output_dir = root / args_output_dir
    args_output_dir.mkdir(parents=True, exist_ok=True)
    manifest_path = args.manifest.resolve()
    targets = json.loads(manifest_path.read_text(encoding="utf-8"))
    if not isinstance(targets, list) or not targets:
        parser.error("manifest must be a non-empty JSON array")
    required = {"name", "package", "unit"}
    for index, target in enumerate(targets):
        if not isinstance(target, dict) or not required.issubset(target):
            parser.error("manifest item {} must contain name, package and unit".format(index))

    rows = []
    with tempfile.TemporaryDirectory(prefix="hd2_skinning_batch_") as temporary:
        work = Path(temporary)
        for target in targets:
            print("\nInspecting {} ...".format(target["name"]), flush=True)
            row = _run_one(root, args.game_data, target, work, args.python)
            print(row.pop("stdout", ""), end="", flush=True)
            rows.append(row)
            print("Status: {}".format(row["status"]), flush=True)

    lines = [
        "# HD2 Skinning Batch Summary", "",
        "| Sample | Geometry source | Status | Meshes | Sections | Resolved lanes | Unresolved lanes | Active transforms | Mapping conflicts |",
        "|---|---|---|---:|---:|---:|---:|---:|---:|",
    ]
    for row in rows:
        lines.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
            row["name"], row["geometry_source"], row["status"], row.get("mesh_count", 0),
            row.get("section_count", 0), row.get("resolved_lanes", 0),
            row.get("unresolved_lanes", 0), row.get("active_transforms", 0),
            row.get("mapping_conflicts", 0)))
    lines.extend(["", "## Per-sample reports", ""])
    for row in rows:
        lines.append("- {}: {}".format(row["name"], row.get("audit_path", row.get("failure", "failed"))))
    summary_path = args_output_dir / "skinning_batch_summary.md"
    summary_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\nBatch summary: {}".format(summary_path))
    return 2 if any(row["status"] == "failed" for row in rows) else 0


if __name__ == "__main__":
    raise SystemExit(main())
