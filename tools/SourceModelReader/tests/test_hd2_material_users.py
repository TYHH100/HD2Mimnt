import struct
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from hd2_material_users import (
    _material_mesh_matches,
    scan_slim_material_users,
)
from hd2_resources import TOC_MAGIC, TYPE_IDS


class FakeSlimIndex:
    def __init__(self, package_bytes):
        self.packages = {"fixture": SimpleNamespace(size=len(package_bytes))}
        self.package_bytes = package_bytes

    def reconstruct_range(self, package_name, offset, size):
        if package_name != "fixture":
            raise KeyError(package_name)
        return self.package_bytes[offset:offset + size]


def make_unit_toc(material_id, section_id=0x101, mesh_id=0xAABBCCDD):
    mesh_offset = 160
    item_start = mesh_offset + 12
    materials_offset = 336
    data = bytearray(materials_offset + 16)

    # Unit header: version 10800438, MeshInfo offset and Materials offset.
    struct.pack_into("<I", data, 44, 10800438)
    struct.pack_into("<I", data, 100, mesh_offset)
    struct.pack_into("<I", data, 112, materials_offset)

    # One MeshInfo record, with one local material slot and one section.
    struct.pack_into("<I", data, mesh_offset, 1)
    struct.pack_into("<II", data, mesh_offset + 4, 12, 0)
    struct.pack_into("<4I", data, item_start + 40, mesh_id, 0, 0, 0)
    struct.pack_into("<iI", data, item_start + 56, 0, 0)
    struct.pack_into("<I", data, item_start + 104, 1)
    struct.pack_into("<I", data, item_start + 108, 128)
    struct.pack_into("<I", data, item_start + 120, 1)
    struct.pack_into("<I", data, item_start + 124, 132)
    struct.pack_into("<I", data, item_start + 128, section_id)
    struct.pack_into("<6I", data, item_start + 132, 0, 0, 3, 0, 3, 0)

    # Unit Materials table maps section_id to Material FileID.
    struct.pack_into("<I", data, materials_offset, 1)
    struct.pack_into("<I", data, materials_offset + 4, section_id)
    struct.pack_into("<Q", data, materials_offset + 8, material_id)
    return bytes(data)


def make_package_with_one_unit(unit_id, unit_toc):
    type_offset = 72
    entries_offset = type_offset + 32
    unit_offset = entries_offset + 80
    package = bytearray(unit_offset + len(unit_toc))
    struct.pack_into("<4I", package, 0, TOC_MAGIC, 1, 1, 0)
    struct.pack_into(
        "<QQQII", package, type_offset,
        0, TYPE_IDS["Unit"], 1, 0, 0,
    )
    struct.pack_into(
        "<QQQ", package, entries_offset,
        unit_id, TYPE_IDS["Unit"], unit_offset,
    )
    struct.pack_into("<I", package, entries_offset + 56, len(unit_toc))
    struct.pack_into("<I", package, entries_offset + 76, 0)
    package[unit_offset:unit_offset + len(unit_toc)] = unit_toc
    return bytes(package)


class MaterialUserMappingTests(unittest.TestCase):
    TARGET = 0x9423BB5F9A715C2E
    OTHER = 0x8B3FF5502AC52C25

    def test_direct_unit_mesh_sections_resolve_material_fileids(self):
        materials = [
            {"section_id": 0x101, "material_id": self.TARGET},
            {"section_id": 0x202, "material_id": self.OTHER},
        ]
        meshes = [
            {
                "index": 0,
                "mesh_id": 0xAABBCCDD,
                "lod_index": 0,
                "sections": [
                    {"index": 0, "material_slot_id": 0x101,
                     "vertex_offset": 0, "num_vertices": 100,
                     "index_offset": 0, "num_indices": 300, "group_index": 0},
                    {"index": 1, "material_slot_id": 0x202,
                     "vertex_offset": 0, "num_vertices": 100,
                     "index_offset": 300, "num_indices": 120, "group_index": 1},
                ],
            }
        ]

        matches = _material_mesh_matches(materials, meshes, {self.TARGET})

        self.assertEqual(len(matches), 1)
        self.assertEqual(matches[0]["material_file_id"], f"{self.TARGET:016x}")
        self.assertEqual(matches[0]["mesh_section_count"], 1)
        self.assertEqual(matches[0]["mesh_sections"][0]["mesh_id"], "aabbccdd")
        self.assertEqual(matches[0]["mesh_sections"][0]["section_index"], 0)
        self.assertEqual(matches[0]["mesh_sections"][0]["vertex_count"], 100)
        self.assertEqual(matches[0]["mesh_sections"][0]["index_count"], 300)

    def test_one_material_can_be_referenced_by_multiple_unit_sections(self):
        materials = [
            {"section_id": 0x11, "material_id": self.TARGET},
            {"section_id": 0x12, "material_id": self.TARGET},
        ]
        meshes = [
            {
                "index": 0, "mesh_id": 0x1001, "lod_index": 0,
                "sections": [
                    {"index": 0, "material_slot_id": 0x11,
                     "vertex_offset": 0, "num_vertices": 20,
                     "index_offset": 0, "num_indices": 60, "group_index": 0},
                    {"index": 1, "material_slot_id": 0x12,
                     "vertex_offset": 0, "num_vertices": 20,
                     "index_offset": 60, "num_indices": 30, "group_index": 1},
                ],
            }
        ]

        matches = _material_mesh_matches(materials, meshes, {self.TARGET})

        self.assertEqual(len(matches), 2)
        self.assertEqual([item["unit_material_section_id"] for item in matches], [0x11, 0x12])
        self.assertEqual([item["mesh_section_count"] for item in matches], [1, 1])

    def test_composite_unit_layout_supplies_actual_mesh_sections(self):
        materials = [
            {"section_id": 0x31, "material_id": self.TARGET},
            {"section_id": 0x32, "material_id": self.TARGET},
        ]
        unit_meshes = [
            {"index": 0, "mesh_id": 0xABCDEF01, "lod_index": 1, "sections": []},
        ]
        composite_meshes = {
            0xABCDEF01: {
                "sections": [
                    {"index": 0, "material_slot_id": 0x31,
                     "vertex_offset": 4, "num_vertices": 40,
                     "index_offset": 0, "num_indices": 120, "group_index": 0},
                    {"index": 1, "material_slot_id": 0x32,
                     "vertex_offset": 4, "num_vertices": 40,
                     "index_offset": 120, "num_indices": 60, "group_index": 1},
                ]
            }
        }

        matches = _material_mesh_matches(
            materials, unit_meshes, {self.TARGET}, composite_meshes
        )

        self.assertEqual(len(matches), 2)
        self.assertEqual(matches[0]["mesh_sections"][0]["lod_index"], 1)
        self.assertEqual(matches[0]["mesh_sections"][0]["vertex_offset"], 4)
        self.assertEqual(matches[1]["mesh_sections"][0]["index_count"], 60)

    def test_unresolved_composite_does_not_fall_back_to_unit_placeholder_sections(self):
        materials = [{"section_id": 0x41, "material_id": self.TARGET}]
        # Unit-side MeshInfo can carry placeholder-looking arrays for CompositeUnits.
        # The unresolved CompositeUnit must not make those look like actual sections.
        unit_meshes = [
            {
                "index": 0, "mesh_id": 0x1234, "lod_index": 0,
                "sections": [
                    {"index": 0, "material_slot_id": 0x41,
                     "vertex_offset": 0, "num_vertices": 10,
                     "index_offset": 0, "num_indices": 30, "group_index": 0},
                ],
            }
        ]

        matches = _material_mesh_matches(materials, unit_meshes, {self.TARGET}, {})

        self.assertEqual(len(matches), 1)
        self.assertEqual(matches[0]["mesh_section_count"], 0)
        self.assertEqual(matches[0]["mesh_sections"], [])

    def test_global_scan_maps_unit_material_to_mesh_section_without_gpu_reads(self):
        unit_id = 0x1122334455667788
        unit_toc = make_unit_toc(self.TARGET)
        package_bytes = make_package_with_one_unit(unit_id, unit_toc)
        fake_index = FakeSlimIndex(package_bytes)

        with tempfile.TemporaryDirectory() as temp:
            with patch("hd2_material_users.SlimBundleIndex", return_value=fake_index):
                report = scan_slim_material_users(temp, [self.TARGET])

        summary = report["summary"]
        self.assertTrue(summary["scan_complete"])
        self.assertEqual(summary["unit_count_scanned"], 1)
        self.assertEqual(summary["unit_parse_success_count"], 1)
        self.assertEqual(summary["unit_parse_error_count"], 0)
        self.assertEqual(summary["material_unit_reference_count"], 1)
        self.assertEqual(summary["mesh_section_match_count"], 1)
        reference = report["resources"][0]["references"][0]
        self.assertEqual(reference["unit_id"], f"{unit_id:016x}")
        self.assertEqual(reference["mesh_sections"][0]["mesh_id"], "aabbccdd")
        self.assertEqual(reference["mesh_sections"][0]["section_index"], 0)
        self.assertEqual(reference["mesh_sections"][0]["vertex_count"], 3)

    def test_cli_help_exposes_material_users_command(self):
        script = Path(__file__).resolve().parents[1] / "source_model_reader.py"
        result = subprocess.run(
            [sys.executable, str(script), "hd2-material-users", "--help"],
            capture_output=True, text=True, check=False,
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--material", result.stdout)
        self.assertIn("--max-packages", result.stdout)
        self.assertIn("--json", result.stdout)


if __name__ == "__main__":
    unittest.main(verbosity=2)
