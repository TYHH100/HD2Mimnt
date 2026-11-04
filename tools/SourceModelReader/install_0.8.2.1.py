#!/usr/bin/env python3
"""Install the Source Model Reader 0.8.2.1-alpha HD2 packed-format patch.

Keep this script next to the updated hd2_unit.py file. The target project
folder may be supplied with --root when the patch files are in a separate
download folder.
"""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

OLD_VERSION = "0.8.2-alpha"
NEW_VERSION = "0.8.2.1-alpha"


def backup(path: Path, suffix: str) -> None:
    target = path.with_name(path.name + suffix)
    if path.exists() and not target.exists():
        shutil.copy2(path, target)


def install(root: Path, module_source: Path, tests_source: Path) -> None:
    root = root.resolve()
    module_source = module_source.resolve()
    tests_source = tests_source.resolve()
    main_path = root / "source_model_reader.py"
    module_target = root / "hd2_unit.py"
    tests_target = root / "tests" / "test_hd2_unit.py"
    version_path = root / "VERSION"

    for path in (main_path, module_source, tests_source):
        if not path.is_file():
            raise FileNotFoundError(f"Required patch/project file not found: {path}")

    source = main_path.read_text(encoding="utf-8-sig")
    old_marker = f'VERSION = "{OLD_VERSION}"'
    new_marker = f'VERSION = "{NEW_VERSION}"'
    if new_marker not in source:
        if source.count(old_marker) != 1:
            raise RuntimeError(
                f"Expected source_model_reader.py VERSION {OLD_VERSION}; "
                "refusing to patch an unknown project version."
            )
        source = source.replace(old_marker, new_marker, 1)

    backup(main_path, ".0.8.2.bak")
    backup(module_target, ".0.8.2.bak")
    backup(version_path, ".0.8.2.bak")

    if module_source != module_target.resolve():
        shutil.copy2(module_source, module_target)
    main_path.write_text(source, encoding="utf-8", newline="\n")
    version_path.write_text(NEW_VERSION + "\n", encoding="utf-8", newline="\n")

    print(f"Source Model Reader {NEW_VERSION} patch installed.")
    print("Backups: *.0.8.2.bak (created only when absent)")
    print("Next: py -3 -m unittest discover -s tests -v")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        required=True,
        help="Target SourceModelReader project folder containing the existing 0.8.2-alpha installation",
    )
    parser.add_argument(
        "--module",
        type=Path,
        default=Path(__file__).resolve().with_name("hd2_unit.py"),
        help="Updated hd2_unit.py supplied with this installer",
    )
    parser.add_argument(
        "--tests",
        type=Path,
        default=Path(__file__).resolve().with_name("tests") / "test_hd2_unit.py",
        help="Updated tests/test_hd2_unit.py supplied with this installer",
    )
    args = parser.parse_args()
    try:
        install(args.root, args.module, args.tests)
        return 0
    except (OSError, RuntimeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
