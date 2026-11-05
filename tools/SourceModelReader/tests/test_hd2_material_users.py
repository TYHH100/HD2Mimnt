import subprocess
import sys
import unittest
from pathlib import Path

from hd2_material_users import _material_mesh_matches


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
