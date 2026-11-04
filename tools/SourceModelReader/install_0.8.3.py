#!/usr/bin/env python3
"""Install Source Model Reader 0.8.3-alpha over an existing 0.8.2.x folder."""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

SUPPORTED_OLD_VERSIONS = {"0.8.2-alpha", "0.8.2.1-alpha"}
NEW_VERSION = "0.8.3-alpha"


def backup(path: Path) -> None:
    target = path.with_name(path.name + ".pre-0.8.3.bak")
    if path.exists() and not target.exists():
        shutil.copy2(path, target)


def install(root: Path, patch_dir: Path) -> None:
    root = root.resolve()
    patch_dir = patch_dir.resolve()
    files = {
        patch_dir / "source_model_reader.py": root / "source_model_reader.py",
        patch_dir / "hd2_unit.py": root / "hd2_unit.py",
        patch_dir / "tests" / "test_hd2_unit.py": root / "tests" / "test_hd2_unit.py",
        patch_dir / "VERSION": root / "VERSION",
    }
    for source in files:
        if not source.is_file():
            raise FileNotFoundError(f"Patch file not found: {source}")

    version_path = root / "VERSION"
    if not version_path.is_file():
        raise FileNotFoundError(f"Target VERSION file not found: {version_path}")
    old_version = version_path.read_text(encoding="utf-8-sig").strip()
    if old_version not in SUPPORTED_OLD_VERSIONS and old_version != NEW_VERSION:
        raise RuntimeError(
            f"Target version is {old_version!r}; expected one of "
            f"{sorted(SUPPORTED_OLD_VERSIONS)}. Refusing to overwrite an unknown version."
        )

    for target in files.values():
        backup(target)
    (root / "tests").mkdir(parents=True, exist_ok=True)
    for source, target in files.items():
        shutil.copy2(source, target)

    print(f"Source Model Reader {NEW_VERSION} installed.")
    print("Backups: *.pre-0.8.3.bak (created only when absent)")
    print("No Helldivers 2 game files were modified.")
    print("Next:")
    print("  py -3 -m unittest discover -s tests -v")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        required=True,
        help="Existing SourceModelReader 0.8.2.x project folder",
    )
    args = parser.parse_args()
    try:
        install(args.root, Path(__file__).resolve().parent)
        return 0
    except (OSError, RuntimeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
