import struct
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from hd2_material_census import scan_slim_material_census
from hd2_resources import TOC_MAGIC, TYPE_IDS


def make_material(texture_nodes=None, variables=None, parent_id=0):
    texture_nodes = texture_nodes or []
    variables = variables or []
    value_size_by_class = {0: 4, 1: 8, 2: 12, 3: 16}
    required_value_size = max(
        (
            value_offset + value_size_by_class[klass]
            for klass, _elements, _variable_id, value_offset, _stride in variables
            if klass in value_size_by_class
        ),
        default=0,
    )
    value_payload = bytes(required_value_size)
    data = bytearray(136)
    struct.pack_into("<Q", data, 24, parent_id)
    struct.pack_into("<I", data, 64, len(texture_nodes))
    struct.pack_into("<I", data, 104, len(variables))
    struct.pack_into("<I", data, 120, len(value_payload))
    descriptors = bytearray()
    for klass, elements, variable_id, value_offset, stride in variables:
        descriptors.extend(struct.pack(
            "<5I", klass, elements, variable_id, value_offset, stride
        ))
    for type_id, _texture_id in texture_nodes:
        data.extend(struct.pack("<I", type_id))
    for _type_id, texture_id in texture_nodes:
        data.extend(struct.pack("<Q", texture_id))
    data.extend(descriptors)
    data.extend(value_payload)
    return bytes(data)


def make_package(resources):
    """Build a minimal in-memory TOC package with Material resources."""
    type_counts = {}
    for resource in resources:
        type_counts[resource["type_id"]] = type_counts.get(resource["type_id"], 0) + 1
    type_rows = b"".join(
        struct.pack("<QQQII", 0, type_id, count, 0, 0)
        for type_id, count in sorted(type_counts.items())
    )
    num_types = len(type_counts)
    num_files = len(resources)
    entries_offset = 72 + len(type_rows)
    entries_size = num_files * 80
    payload_offset = entries_offset + entries_size
    entries = bytearray()
    payloads = bytearray()
    for index, resource in enumerate(resources):
        payload = resource.get("payload", b"")
        toc_offset = payload_offset + len(payloads) if payload else 0
        entries.extend(struct.pack(
            "<7Q6I",
            resource["file_id"],
            resource["type_id"],
            toc_offset,
            0, 0, 0, 0,
            len(payload), 0, 0, 0, 0,
            index,
        ))
        payloads.extend(payload)
    header = struct.pack("<4I", TOC_MAGIC, num_types, num_files, 0) + bytes(56)
    return header + type_rows + bytes(entries) + bytes(payloads)


class FakeIndex:
    def __init__(self, _root):
        self.packages_data = {
            "a-package": make_package([
                {
                    "file_id": 0xA1,
                    "type_id": TYPE_IDS["Material"],
                    "payload": make_material(
                        texture_nodes=[(0x11111111, 0x2222)],
                        variables=[(0, 1, 0xAABBCCDD, 0, 4)],
                        parent_id=0xB2,
                    ),
                },
                {
                    "file_id": 0xB2,
                    "type_id": TYPE_IDS["Material"],
                    "payload": make_material(
                        texture_nodes=[(0x11111111, 0x3333)],
                        variables=[(1, 1, 0xAABBCCDD, 0, 8)],
                    ),
                },
            ]),
            "b-package": make_package([
                {
                    "file_id": 0xA1,
                    "type_id": TYPE_IDS["Material"],
                    "payload": make_material(
                        texture_nodes=[(0x11111111, 0x2222)],
                        variables=[(0, 1, 0xAABBCCDD, 0, 4)],
                        parent_id=0xB2,
                    ),
                },
                {
                    "file_id": 0xC3,
                    "type_id": TYPE_IDS["Texture"],
                    "payload": b"texture",
                },
            ]),
            "c-empty": make_package([
                {
                    "file_id": 0xD4,
                    "type_id": TYPE_IDS["Texture"],
                    "payload": b"texture",
                },
            ]),
        }
        self.packages = {
            name: SimpleNamespace(size=len(data))
            for name, data in self.packages_data.items()
        }
        self.bundle_count = 1

    def reconstruct_range(self, package_name, offset, size):
        data = self.packages_data[package_name]
        return data[offset:offset + size]


class MaterialCensusTests(unittest.TestCase):
    def scan(self, max_packages=None, include_descriptors=False, index_type=FakeIndex):
        with patch("hd2_material_census.SlimBundleIndex", index_type):
            return scan_slim_material_census(
                Path("unused-game-data"),
                max_packages=max_packages,
                include_descriptors=include_descriptors,
            )

    def test_parses_and_aggregates_materials_without_gpu_or_stream_reads(self):
        report = self.scan()
        summary = report["summary"]

        self.assertEqual(summary["status"], "ok")
        self.assertTrue(summary["scan_complete"])
        self.assertTrue(summary["package_scan_complete"])
        self.assertEqual(summary["package_count_scanned"], 3)
        self.assertEqual(summary["package_count_with_materials"], 2)
        self.assertEqual(summary["material_entry_count"], 3)
        self.assertEqual(summary["material_parse_success_count"], 3)
        self.assertEqual(summary["material_parse_error_count"], 0)
        self.assertEqual(summary["unique_material_file_id_count"], 2)
        self.assertEqual(summary["duplicate_material_file_id_count"], 1)
        self.assertFalse(report["options"]["gpu_sidecars_read"])
        self.assertFalse(report["options"]["stream_sidecars_read"])
        self.assertEqual(report["texture_type_frequencies"][0]["texture_type_id"], "0x11111111")
        self.assertEqual(report["shader_variable_frequencies"][0]["variable_id"], "0xaabbccdd")

    def test_detects_parent_fileid_match_and_payload_duplicates(self):
        report = self.scan()

        parent_b2 = next(
            row for row in report["parent_material_ids"]
            if row["parent_material_id"] == "00000000000000b2"
        )
        self.assertTrue(parent_b2["matches_material_file_id"])
        self.assertEqual(len(report["duplicate_file_ids"]), 1)
        self.assertEqual(report["duplicate_file_ids"][0]["file_id"], "00000000000000a1")
        self.assertEqual(report["duplicate_file_ids"][0]["unique_payload_hash_count"], 1)
        self.assertEqual(report["summary"]["duplicate_payload_hash_count"], 1)

    def test_optional_descriptors_and_partial_scan(self):
        report = self.scan(max_packages=1, include_descriptors=True)

        self.assertEqual(report["summary"]["status"], "warning")
        self.assertFalse(report["summary"]["scan_complete"])
        material = report["materials"][0]
        self.assertIn("texture_nodes", material)
        self.assertIn("shader_variables", material)
        self.assertEqual(material["texture_nodes"], [["0x11111111", "0000000000002222"]])
        self.assertEqual(material["shader_variables"][0][0], "0xaabbccdd")

    def test_malformed_material_is_isolated_and_reported(self):
        class BrokenMaterialIndex(FakeIndex):
            def __init__(self, root):
                super().__init__(root)
                self.packages_data["a-package"] = make_package([
                    {
                        "file_id": 0xEE,
                        "type_id": TYPE_IDS["Material"],
                        "payload": b"bad",
                    },
                ])
                self.packages["a-package"] = SimpleNamespace(
                    size=len(self.packages_data["a-package"])
                )

        report = self.scan(index_type=BrokenMaterialIndex)

        self.assertEqual(report["summary"]["material_parse_error_count"], 1)
        self.assertEqual(report["summary"]["material_parse_success_count"], 1)
        self.assertFalse(report["summary"]["scan_complete"])
        self.assertEqual(report["materials"][0]["parse_status"], "error")
        self.assertEqual(report["materials"][0]["file_id"], "00000000000000ee")
