#!/usr/bin/env python3
"""Apply the Source Model Reader 0.8.2-alpha patch in-place, with backups."""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

PARSER_MARKER = "    args = parser.parse_args(argv)"
HANDLER_MARKER = '    if args.command == "hd2-inspect":'
PARSER_BLOCK = '''    p_hd2_unit = sub.add_parser(
        "hd2-unit",
        help="Decode one HD2 Unit's TransformInfo, BoneInfo, streams, meshes, and GPU geometry",
    )
    p_hd2_unit.add_argument("input", help="HD2 game data directory or package/archive file")
    p_hd2_unit.add_argument("--package", help="Package/archive ID when input is the HD2 game data directory")
    p_hd2_unit.add_argument("--unit", required=True, help="Unit resource FileID (hexadecimal)")
    p_hd2_unit.add_argument("--json", dest="json_path", help="Write the complete Unit/geometry report to JSON")

'''
HANDLER_BLOCK = '''    if args.command == "hd2-unit":
        try:
            from hd2_unit import HD2ResourceError, format_hd2_unit_text, inspect_hd2_unit

            report = inspect_hd2_unit(args.input, args.package, args.unit)
            print(format_hd2_unit_text(report))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError, KeyError, OverflowError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
'''


def replace_once(source: str, old: str, new: str, label: str) -> str:
    count = source.count(old)
    if count != 1:
        raise RuntimeError(f"Expected exactly one {label} marker, found {count}")
    return source.replace(old, new, 1)


def install(root: Path) -> None:
    main_path = root / "source_model_reader.py"
    resources_path = root / "hd2_resources.py"
    module_source = Path(__file__).with_name("hd2_unit.py")
    version_path = root / "VERSION"

    for file_path in (main_path, resources_path, module_source):
        if not file_path.is_file():
            raise FileNotFoundError(f"Required project file not found: {file_path}")

    main = main_path.read_text(encoding="utf-8-sig")
    if 'VERSION = "0.8.2-alpha"' not in main:
        if 'VERSION = "0.8.1-alpha"' not in main:
            raise RuntimeError("This patch targets Source Model Reader 0.8.1-alpha only")
        main = replace_once(main, 'VERSION = "0.8.1-alpha"', 'VERSION = "0.8.2-alpha"', "version")

    if 'p_hd2_unit = sub.add_parser' not in main:
        main = replace_once(main, PARSER_MARKER, PARSER_BLOCK + PARSER_MARKER, "CLI parser")
    if 'if args.command == "hd2-unit":' not in main:
        main = replace_once(main, HANDLER_MARKER, HANDLER_BLOCK + HANDLER_MARKER, "CLI handler")

    resources = resources_path.read_text(encoding="utf-8-sig")
    resources = resources.replace(
        "Source Model Reader 0.8.1-alpha - HD2 resource inspection",
        "Source Model Reader 0.8.2-alpha - HD2 resource inspection",
    )
    resources = resources.replace(
        "Note: GPU vertex streams and per-material BoneInfo remaps are not decoded in 0.8.1-alpha.",
        "Note: use hd2-unit for per-Unit TransformInfo, BoneInfo, stream, mesh, and GPU geometry decoding. Bone palette/name resolution remains a later milestone.",
    )

    backup_main = main_path.with_name(main_path.name + ".0.8.1.bak")
    backup_resources = resources_path.with_name(resources_path.name + ".0.8.1.bak")
    if not backup_main.exists():
        shutil.copy2(main_path, backup_main)
    if not backup_resources.exists():
        shutil.copy2(resources_path, backup_resources)

    main_path.write_text(main, encoding="utf-8", newline="\n")
    resources_path.write_text(resources, encoding="utf-8", newline="\n")
    module_target = root / "hd2_unit.py"
    if module_source.resolve() != module_target.resolve():
        shutil.copy2(module_source, module_target)
    if version_path.exists():
        version_path.write_text("0.8.2-alpha\n", encoding="utf-8", newline="\n")

    print("Source Model Reader 0.8.2-alpha patch installed.")
    print(f"Backup: {backup_main.name}")
    print("Next: py -3 -m unittest discover -s tests -v")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent,
                        help="SourceModelReader project folder (defaults to this script's folder)")
    args = parser.parse_args()
    try:
        install(args.root.resolve())
        return 0
    except (OSError, RuntimeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
