import unittest

from research.summarize_hd2_skinning import build_summary, render_markdown


class Hd2SkinningAuditTests(unittest.TestCase):
    def test_preserves_boneinfo_remaps_and_resolved_vertex_influences(self):
        report = {
            "unit": {"file_id": "unit"},
            "package": {"name": "package"},
            "summary": {"status": "ok"},
            "bone_info": {
                "records": [{
                    "index": 0,
                    "num_bones": 2,
                    "real_indices": [9, 12],
                    "remaps": [[1, 0]],
                }]
            },
            "transform_info": {
                "resolved_skeleton": [{
                    "transform_index": 12,
                    "name": "hips",
                    "parent_name": "root",
                    "has_active_vertex_influence": True,
                    "active_vertex_influence_lane_count": 4,
                }]
            },
            "geometry": [{
                "mesh_id": 123,
                "vertex_count": 1,
                "index_count": 3,
                "skinning": {
                    "status": "ok",
                    "resolved_active_influence_count": 1,
                    "unresolved_active_influence_count": 0,
                    "palette": [],
                    "section_skinning": [{
                        "section_index": 0,
                        "material_index": 0,
                        "status": "ok",
                        "resolved_active_influence_count": 1,
                        "unresolved_active_influence_count": 0,
                        "bone_index_sets": [{
                            "component_index": 0,
                            "resolved_active_influence_count": 1,
                            "unresolved_active_influence_count": 0,
                            "vertex_influences": [{
                                "vertex_index": 0,
                                "influences": [{
                                    "lane": 0,
                                    "weight": 1.0,
                                    "local_bone_index": 0,
                                    "palette_slot": 1,
                                    "transform_index": 12,
                                    "bone_name": "hips",
                                    "status": "ok",
                                }],
                            }],
                        }],
                    }],
                },
            }],
            "warnings": [],
            "limitations": [],
        }
        result = build_summary(report)
        influence = result["meshes"][0]["sections"][0]["bone_index_sets"][0]["influences"][0]
        self.assertEqual(influence["local_bone_index"], 0)
        self.assertEqual(influence["palette_slot"], 1)
        self.assertEqual(influence["transform_index"], 12)
        self.assertEqual(result["bone_info_records"][0]["remaps"], [[1, 0]])
        self.assertEqual(result["active_transforms"][0]["name"], "hips")
        markdown = render_markdown(result)
        self.assertIn("palette=1", markdown)
        self.assertIn("transform=12", markdown)


if __name__ == "__main__":
    unittest.main()
