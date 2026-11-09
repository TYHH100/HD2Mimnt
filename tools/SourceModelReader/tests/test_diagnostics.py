import sys
import unittest
from pathlib import Path
from types import SimpleNamespace

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from source_model_reader import Bone, BodyPartInfo, ModelInfo, SourceModel, VVDVertex


class SourceDiagnosticsTests(unittest.TestCase):
    def make_model(self):
        model = SourceModel.__new__(SourceModel)
        identity_3x4 = (1.0, 0.0, 0.0, 0.0,
                        0.0, 1.0, 0.0, 0.0,
                        0.0, 0.0, 1.0, 0.0)
        model.bones = [
            Bone(0, "root", -1, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), identity_3x4),
            Bone(1, "child", 0, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), identity_3x4),
        ]
        model.vertices = [
            VVDVertex((1.0, 0.0, 0.0), (0, 0, 0), 1, (0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0)),
            VVDVertex((1.0, 0.0, 0.0), (0, 0, 0), 1, (1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0)),
            VVDVertex((0.5, 0.5, 0.0), (0, 1, 0), 2, (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0)),
            VVDVertex((1.0, 0.0, 0.0), (0, 0, 0), 1, (99.0, 99.0, 99.0), (0.0, 0.0, 1.0), (0.5, 0.5)),
        ]
        model.vvd_vertex_count = 4
        model.bodyparts = [BodyPartInfo(0, "body", 1, [ModelInfo(0, "body0", 4, 0, [])])]
        model.bundle = SimpleNamespace(mdl_display="fixture.mdl", archive_info=None)
        model.warnings = []
        model.collect_geometry = lambda lod, skin, choices: (
            [("mat", 0, 1, 2)],
            [{"bodygroup": 0, "name": "body", "choice": 0, "model": "body0",
              "mdl_meshes": 1, "vtx_meshes": 1, "triangle_count": 1,
              "referenced_vertices": 3}],
        )
        return model

    def test_reports_selected_weights_and_unreferenced_vertices(self):
        report = self.make_model().diagnose()
        self.assertEqual(report["summary"]["status"], "ok")
        self.assertEqual(report["summary"]["selected_vertices"], 3)
        self.assertEqual(report["summary"]["triangles"], 1)
        self.assertEqual(report["vertices"]["not_referenced_by_selection"], 1)
        self.assertEqual(report["vertices"]["selected_geometry"]["unweighted_vertices"], 0)
        self.assertEqual(report["skeleton"]["weight_referenced_bone_count"], 2)
        self.assertEqual(len(report["skeleton"]["bone_table"]), 2)
        self.assertEqual(report["skeleton"]["definition_count"], 2)
        self.assertTrue(report["skeleton"]["bone_table"][0]["referenced_by_selected_geometry_weights"])
        self.assertEqual(report["geometry"]["bodygroup_catalog"][0]["options"][0]["name"], "body0")

    def test_flags_unweighted_active_vertex(self):
        model = self.make_model()
        model.vertices[0] = VVDVertex((0.0, 0.0, 0.0), (0, 0, 0), 1,
                                      (0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0))
        report = model.diagnose()
        self.assertEqual(report["summary"]["status"], "warning")
        codes = {item["code"] for item in report["findings"]}
        self.assertIn("unweighted_active_vertices", codes)

    def test_reports_bone_hierarchy_cycle(self):
        model = self.make_model()
        model.bones[0].parent = 1
        report = model.diagnose()
        self.assertEqual(report["summary"]["status"], "error")
        self.assertTrue(report["skeleton"]["hierarchy_cycles"])

    def test_bodygroup_bone_matrix_compares_each_option_to_default(self):
        model = self.make_model()
        model.bodyparts = [BodyPartInfo(0, "accessory", 1, [
            ModelInfo(0, "none", 4, 0, []),
            ModelInfo(1, "weighted_child", 4, 0, []),
            ModelInfo(2, "alternate_root", 4, 0, []),
        ])]
        model.vertices[2] = VVDVertex((1.0, 0.0, 0.0), (0, 0, 0), 1,
                                      (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0))
        model.vertices[3] = VVDVertex((1.0, 0.0, 0.0), (1, 0, 0), 1,
                                      (99.0, 99.0, 99.0), (0.0, 0.0, 1.0), (0.5, 0.5))
        model.vertices.append(VVDVertex((1.0, 0.0, 0.0), (1, 0, 0), 1,
                                        (99.0, 98.0, 99.0), (0.0, 0.0, 1.0), (0.7, 0.5)))

        def collect_geometry(lod, skin, choices):
            choice = choices.get(0, 0)
            if choice == 0:
                return [("mat", 0, 1, 2)], []
            if choice == 1:
                return [("mat", 1, 2, 3)], []
            return [("mat", 3, 4, 3)], []

        model.collect_geometry = collect_geometry
        report = model.bone_dependency_matrix()
        self.assertEqual(report["skeleton_definition"]["mdl_bone_definition_count"], 2)
        self.assertEqual(report["baseline"]["weight_references"]["weighted_bone_count"], 1)
        options = report["bodygroups"][0]["options"]
        self.assertEqual(options[0]["delta_vs_all_default"]["added_count"], 0)
        self.assertEqual(options[1]["delta_vs_all_default"]["added_count"], 1)
        self.assertEqual(options[1]["delta_vs_all_default"]["added"][0]["name"], "child")
        self.assertEqual(options[1]["delta_vs_all_default"]["removed_count"], 0)
        self.assertEqual(options[2]["delta_vs_all_default"]["added_count"], 1)
        self.assertEqual(options[2]["delta_vs_all_default"]["removed_count"], 1)


if __name__ == "__main__":
    unittest.main()
