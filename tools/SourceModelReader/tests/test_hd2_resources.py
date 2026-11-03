import struct
import tempfile
import unittest
from pathlib import Path

from hd2_resources import (
    DSARFile,
    HD2ResourceError,
    SlimBundleIndex,
    TYPE_IDS,
    _lz4_block_decompress,
    inspect_hd2_package,
    parse_bones_resource,
    parse_toc_package,
)


def make_dsar(payload: bytes, compressed: bool = False) -> bytes:
    if compressed:
        length = len(payload)
        token = min(length, 15) << 4
        encoded = bytearray([token])
        if length >= 15:
            remaining = length - 15
            while remaining >= 255:
                encoded.append(255)
                remaining -= 255
            encoded.append(remaining)
        encoded.extend(payload)
        raw = bytes(encoded)
        compression = 3
    else:
        raw = payload
        compression = 0
    data_offset = 0x40
    header = bytearray(0x40)
    header[:4] = b"DSAR"
    struct.pack_into("<I", header, 4, 1)
    struct.pack_into("<I", header, 8, 1)
    struct.pack_into(
        "<QQIIBB6x", header, 0x20,
        0, data_offset, len(payload), len(raw), compression, 0x02,
    )
    return bytes(header) + raw


def make_dsar_chunks(chunks: list[tuple[int, bytes]]) -> bytes:
    table_start = 0x20
    data_start = table_start + len(chunks) * 0x20
    header = bytearray(data_start)
    header[:4] = b"DSAR"
    struct.pack_into("<I", header, 4, 1)
    struct.pack_into("<I", header, 8, len(chunks))
    payload = bytearray()
    uncompressed_offset = 0
    compressed_offset = data_start
    for index, (chunk_type, chunk_data) in enumerate(chunks):
        struct.pack_into(
            "<QQIIBB6x", header, table_start + index * 0x20,
            uncompressed_offset, compressed_offset, len(chunk_data), len(chunk_data), 0, chunk_type,
        )
        payload.extend(chunk_data)
        uncompressed_offset += len(chunk_data)
        compressed_offset += len(chunk_data)
    return bytes(header) + bytes(payload)


def make_bones_resource() -> bytes:
    # 2 names, 1 LOD float, 2 hashes, 1 LOD level, then a NUL string table.
    return (
        struct.pack("<2I", 2, 1)
        + struct.pack("<f", 0.0)
        + struct.pack("<2I", 0x11111111, 0x22222222)
        + struct.pack("<I", 2)
        + b"root\0spine\0"
    )


def make_unit_resource(bones_id: int = 0xB0, material_id: int | None = None) -> bytes:
    data = bytearray(128)
    struct.pack_into("<5Q", data, 0, 0, bones_id, 0, 0, 0)
    struct.pack_into("<2I", data, 40, 0, 10800438)
    materials_offset = 128
    if material_id is not None:
        # A zeroed offset table isn't valid here; append a one-row materials table.
        struct.pack_into("<I", data, 112, materials_offset)
        data.extend(struct.pack("<I", 1))
        data.extend(struct.pack("<I", 0xABCDEF01))
        data.extend(struct.pack("<Q", material_id))
    return bytes(data)


def make_legacy_package() -> bytes:
    unit_id = 0xA0
    bones_id = 0xB0
    material_id = 0xC0
    unit = make_unit_resource(bones_id, material_id)
    bones = make_bones_resource()
    num_types = 3
    num_files = 2
    type_offset = 72
    entries_offset = type_offset + num_types * 32
    payload_offset = entries_offset + num_files * 80
    unit_offset = payload_offset
    bones_offset = unit_offset + len(unit)
    toc = bytearray(bones_offset + len(bones))
    struct.pack_into("<4I", toc, 0, 0xF0000011, num_types, num_files, 0)
    type_rows = [
        (TYPE_IDS["Unit"], 1),
        (TYPE_IDS["Bones"], 1),
        (TYPE_IDS["Material"], 0),
    ]
    for index, (type_id, count) in enumerate(type_rows):
        struct.pack_into("<QQQII", toc, type_offset + index * 32, 0, type_id, count, 16, 64)
    entries = [
        (unit_id, TYPE_IDS["Unit"], unit_offset, len(unit)),
        (bones_id, TYPE_IDS["Bones"], bones_offset, len(bones)),
    ]
    for index, (file_id, type_id, offset, size) in enumerate(entries, 1):
        struct.pack_into(
            "<7Q6I", toc, entries_offset + (index - 1) * 80,
            file_id, type_id, offset, 0, 0, 0, 0,
            size, 0, 0, 16, 64, index,
        )
    toc[unit_offset:unit_offset + len(unit)] = unit
    toc[bones_offset:bones_offset + len(bones)] = bones
    return bytes(toc)


class LZ4Tests(unittest.TestCase):
    def test_decodes_literal_only_block(self):
        self.assertEqual(_lz4_block_decompress(b"\x50hello", 5), b"hello")

    def test_decodes_overlapping_match(self):
        self.assertEqual(_lz4_block_decompress(b"\x10a\x01\x00", 5), b"aaaaa")

    def test_rejects_invalid_offset(self):
        with self.assertRaises(HD2ResourceError):
            _lz4_block_decompress(b"\x00\x01\x00", 4)


class DSARTests(unittest.TestCase):
    def test_reads_uncompressed_chunk(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "sample.nxa"
            path.write_bytes(make_dsar(b"payload"))
            dsar = DSARFile(path)
            self.assertEqual(dsar.decompress_all(), b"payload")
            self.assertEqual(dsar.read_range_at(0, 4), b"payl")

    def test_reads_compressed_chunk(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "sample.nxa"
            path.write_bytes(make_dsar(b"compressed payload", compressed=True))
            self.assertEqual(DSARFile(path).decompress_all(), b"compressed payload")

    def test_reads_split_resource_and_next_resource_boundary(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "split.nxa"
            path.write_bytes(make_dsar_chunks([(0x02, b"abc"), (0x04, b"def"), (0x02, b"ghi")]))
            dsar = DSARFile(path)
            self.assertEqual(dsar.read_resource_at(0), b"abcdef")
            self.assertEqual(dsar.read_range_at(0, 9), b"abcdefghi")

    def test_rejects_truncated_chunk_table(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "bad.nxa"
            path.write_bytes(b"DSAR" + bytes(20))
            with self.assertRaises(HD2ResourceError):
                DSARFile(path)


class ResourceTOCTests(unittest.TestCase):
    def test_inspects_standalone_dsar_package(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "sample.nxa"
            path.write_bytes(make_dsar(make_legacy_package()))
            report = inspect_hd2_package(path)
            self.assertEqual(report["summary"]["unit_count"], 1)
            self.assertEqual(report["package"]["container"], "dsar")

    def test_parses_legacy_toc_and_unit_bones_dependencies(self):
        package = parse_toc_package("fixture", make_legacy_package())
        report = inspect_hd2_package_from_package(package)
        self.assertEqual(report["summary"]["unit_count"], 1)
        self.assertEqual(report["summary"]["bones_resource_count"], 1)
        unit = report["units"][0]
        self.assertEqual(unit["table_counts"]["mesh_count"], 0)
        self.assertEqual(unit["references"]["bones"], "00000000000000b0")
        self.assertEqual(unit["material_sections"][0]["material_id"], 0xC0)
        bones = report["bones_resources"][0]
        self.assertEqual(bones["name_count"], 2)
        self.assertEqual([row["name"] for row in bones["bones"]], ["root", "spine"])

    def test_rejects_truncated_toc_entry_table(self):
        with self.assertRaises(HD2ResourceError):
            parse_toc_package("bad", struct.pack("<4I", 0xF0000011, 0, 1, 0) + bytes(64))

    def test_reports_unknown_type_without_losing_entry(self):
        raw = bytearray(make_legacy_package())
        entries_offset = 72 + 3 * 32
        struct.pack_into("<Q", raw, entries_offset + 80 + 8, 0x123456789ABCDEF0)
        package = parse_toc_package("fixture", bytes(raw))
        self.assertTrue(any(entry.type_id == 0x123456789ABCDEF0 for entry in package.entries))

    def test_bones_parser_reports_name_count_mismatch(self):
        bad = struct.pack("<2I", 2, 0) + struct.pack("<2I", 1, 2) + b"only_one\0"
        report = parse_bones_resource(bad)
        self.assertTrue(report["warnings"])


def inspect_hd2_package_from_package(package):
    from hd2_resources import build_resource_report
    return build_resource_report(package)


class SlimTests(unittest.TestCase):
    def test_reconstructs_selected_package_from_bundles(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            package_bytes = make_legacy_package()
            package_name = "fixture_archive"

            metadata = bytearray(0x18 + 0x18 + len(package_name) + 1 + 0x10)
            struct.pack_into("<I", metadata, 0x0C, 1)  # bundles
            struct.pack_into("<I", metadata, 0x10, 1)  # packages
            row = 0x18
            name_offset = 0x18 + 0x18
            items_offset = name_offset + len(package_name) + 1
            struct.pack_into("<QIII", metadata, row, len(package_bytes), name_offset, 1, items_offset)
            metadata[name_offset:name_offset + len(package_name)] = package_name.encode()
            item = items_offset
            struct.pack_into("<QI", metadata, item, 0, 0)
            metadata[item + 15] = 0

            (root / "bundles.nxa").write_bytes(make_dsar(bytes(metadata)))
            (root / "bundles.00.nxa").write_bytes(make_dsar(package_bytes))
            (root / "bundle_database.data").write_bytes(b"not needed by the package reader")

            report = inspect_hd2_package(root, package_name)
            self.assertEqual(report["summary"]["unit_count"], 1)
            self.assertEqual(report["package"]["container"], "slim")

    def test_reconstructs_package_from_multiple_bundle_pieces(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            package_bytes = make_legacy_package()
            split_at = len(package_bytes) // 2
            package_name = "two_piece_archive"

            metadata = bytearray(0x18 + 0x18 + len(package_name) + 1 + 0x20)
            struct.pack_into("<I", metadata, 0x0C, 1)
            struct.pack_into("<I", metadata, 0x10, 1)
            row = 0x18
            name_offset = 0x18 + 0x18
            items_offset = name_offset + len(package_name) + 1
            struct.pack_into("<QIII", metadata, row, len(package_bytes), name_offset, 2, items_offset)
            metadata[name_offset:name_offset + len(package_name)] = package_name.encode()
            struct.pack_into("<QI", metadata, items_offset, 0, 0)
            metadata[items_offset + 15] = 0
            struct.pack_into("<QI", metadata, items_offset + 0x10, split_at, split_at)
            metadata[items_offset + 0x10 + 15] = 0

            (root / "bundles.nxa").write_bytes(make_dsar(bytes(metadata)))
            (root / "bundles.00.nxa").write_bytes(make_dsar_chunks([
                (0x02, package_bytes[:split_at]),
                (0x02, package_bytes[split_at:]),
            ]))

            report = inspect_hd2_package(root, package_name)
            self.assertEqual(report["summary"]["unit_count"], 1)
            self.assertEqual(report["package"]["container"], "slim")


if __name__ == "__main__":
    unittest.main()

class HD2CLITests(unittest.TestCase):
    def test_hd2_inspect_command_writes_report(self):
        import json
        import subprocess
        import sys

        with tempfile.TemporaryDirectory() as temp:
            package_path = Path(temp) / "fixture.archive"
            json_path = Path(temp) / "inventory.json"
            package_path.write_bytes(make_legacy_package())
            result = subprocess.run(
                [sys.executable, str(Path(__file__).resolve().parents[1] / "source_model_reader.py"),
                 "hd2-inspect", str(package_path), "--json", str(json_path)],
                capture_output=True, text=True, check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("HD2 resource inspection", result.stdout)
            report = json.loads(json_path.read_text(encoding="utf-8"))
            self.assertEqual(report["summary"]["unit_count"], 1)

class HD2CatalogTests(unittest.TestCase):
    @staticmethod
    def make_catalog_toc(type_counts):
        type_rows = list(type_counts.items())
        num_types = len(type_rows)
        num_files = sum(type_counts.values())
        table_end = 72 + num_types * 32
        data = bytearray(table_end + num_files * 80)
        struct.pack_into("<4I", data, 0, 0xF0000011, num_types, num_files, 0)
        for index, (type_id, count) in enumerate(type_rows):
            struct.pack_into("<QQQII", data, 72 + index * 32, 0, type_id, count, 16, 64)
        return bytes(data)

    @staticmethod
    def make_slim_fixture(root, package_payloads, bundle_splits=None):
        names = list(package_payloads)
        table_start = 0x18
        package_table_end = table_start + len(names) * 0x18
        strings_start = package_table_end
        strings = bytearray()
        name_offsets = {}
        for name in names:
            name_offsets[name] = strings_start + len(strings)
            strings.extend(name.encode("utf-8") + b"\0")

        items_start = strings_start + len(strings)
        item_tables = bytearray()
        package_rows = []
        current_bundle_offset = 0
        bundle_chunks = []
        bundle_splits = bundle_splits or {}
        for name in names:
            payload = package_payloads[name]
            split = bundle_splits.get(name)
            if split is not None:
                chunks = [payload[:split], payload[split:]]
            else:
                chunks = [payload]
            item_count = 1
            items_offset = items_start + len(item_tables)
            # One package segment begins at the first DSAR resource for this package.
            piece = bytearray(0x10)
            struct.pack_into("<QI", piece, 0, 0, current_bundle_offset)
            piece[15] = 0
            item_tables.extend(piece)
            package_rows.append((len(payload), name_offsets[name], item_count, items_offset))
            bundle_chunks.extend((0x02, chunk) for chunk in chunks)
            current_bundle_offset += len(payload)

        metadata = bytearray(items_start + len(item_tables))
        struct.pack_into("<I", metadata, 0x0C, 1)
        struct.pack_into("<I", metadata, 0x10, len(names))
        for index, row in enumerate(package_rows):
            struct.pack_into("<QIII", metadata, table_start + index * 0x18, *row)
        metadata[strings_start:items_start] = strings
        metadata[items_start:] = item_tables
        (root / "bundles.nxa").write_bytes(make_dsar(bytes(metadata)))
        (root / "bundles.00.nxa").write_bytes(make_dsar_chunks(bundle_chunks))

    def test_catalog_finds_unit_and_bones_packages_without_rebuilding_payloads(self):
        from hd2_resources import scan_slim_package_catalog
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_slim_fixture(root, {
                "base_archive": self.make_catalog_toc({TYPE_IDS["Material"]: 5}),
                "character_archive": self.make_catalog_toc({
                    TYPE_IDS["Unit"]: 4, TYPE_IDS["Bones"]: 1,
                    TYPE_IDS["Material"]: 3, TYPE_IDS["Texture"]: 8,
                }),
                "character_archive.gpu_resources": b"GPU sidecar sentinel is not a TOC",
                "character_archive.stream": b"stream sidecar sentinel is not a TOC",
                "static_archive": self.make_catalog_toc({TYPE_IDS["Unit"]: 2}),
            })
            report = scan_slim_package_catalog(root)
            self.assertEqual(report["summary"]["package_count_in_index"], 5)
            self.assertEqual(report["summary"]["package_count_scanned"], 3)
            self.assertEqual(report["summary"]["packages_with_units"], 2)
            self.assertEqual(report["summary"]["packages_with_bones"], 1)
            candidates = {row["package"]: row for row in report["model_resource_packages"]}
            self.assertIn("character_archive", candidates)
            self.assertEqual(candidates["character_archive"]["unit_count"], 4)
            self.assertEqual(candidates["character_archive"]["bones_count"], 1)
            self.assertEqual(candidates["static_archive"]["bones_count"], 0)

    def test_catalog_range_read_crosses_bundle_resource_chunks(self):
        from hd2_resources import SlimBundleIndex
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            package = self.make_catalog_toc({TYPE_IDS["Unit"]: 1, TYPE_IDS["Bones"]: 1})
            split = 93
            self.make_slim_fixture(root, {"split_archive": package}, bundle_splits={"split_archive": split})
            index = SlimBundleIndex(root)
            self.assertEqual(index.reconstruct_range("split_archive", 0, 72), package[:72])
            self.assertEqual(index.reconstruct_range("split_archive", split - 10, 30), package[split - 10:split + 20])

    def test_catalog_honors_max_packages(self):
        from hd2_resources import scan_slim_package_catalog
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_slim_fixture(root, {
                "a_archive": self.make_catalog_toc({TYPE_IDS["Unit"]: 1}),
                "b_archive": self.make_catalog_toc({TYPE_IDS["Bones"]: 1}),
            })
            report = scan_slim_package_catalog(root, max_packages=1)
            self.assertEqual(report["summary"]["package_count_scanned"], 1)

    def test_catalog_cli_writes_json(self):
        import json
        import subprocess
        import sys
        from hd2_resources import TYPE_IDS
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            self.make_slim_fixture(root, {
                "armor_archive": self.make_catalog_toc({TYPE_IDS["Unit"]: 2, TYPE_IDS["Bones"]: 1}),
            })
            out = root / "catalog.json"
            result = subprocess.run(
                [sys.executable, str(Path(__file__).resolve().parents[1] / "source_model_reader.py"),
                 "hd2-catalog", str(root), "--json", str(out)],
                capture_output=True, text=True, check=False,
            )
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("HD2 package catalog", result.stdout)
            report = json.loads(out.read_text(encoding="utf-8"))
            self.assertEqual(report["model_resource_packages"][0]["package"], "armor_archive")
