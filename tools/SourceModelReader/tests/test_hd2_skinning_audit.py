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


    def test_compact_report_aggregates_influences_and_keeps_matrix_validation(self):
        base = {
            "vertex_index": 0,
            "influences": [
                {"lane": 0, "weight": 0.25, "local_bone_index": 0,
                 "palette_slot": 1, "transform_index": 12, "bone_name": "hips", "status": "ok"},
                {"lane": 1, "weight": 0.75, "local_bone_index": 1,
                 "palette_slot": 0, "transform_index": 9, "bone_name": "root", "status": "ok"},
            ],
        }
        report = {
            "unit": {"file_id": "unit"},
            "package": {"name": "package"},
            "summary": {"transform_count": 2, "bone_info_count": 1, "mesh_count": 1,
                        "decoded_mesh_count": 1, "vertex_count": 2, "index_count": 3,
                        "triangle_count": 1},
            "bone_info": {"records": [{"index": 0, "num_bones": 2,
                                       "real_indices": [9, 12], "remaps": [[1, 0]]}]},
            "transform_info": {"resolved_skeleton": [
                {"transform_index": 9, "name": "root", "parent_name": None,
                 "has_active_vertex_influence": True, "active_vertex_influence_lane_count": 1},
                {"transform_index": 12, "name": "hips", "parent_name": "root",
                 "has_active_vertex_influence": True, "active_vertex_influence_lane_count": 1},
            ]},
            "geometry": [{
                "mesh_id": 123, "lod_index": 0, "vertex_count": 2, "index_count": 3,
                "skinning": {
                    "status": "ok", "bone_info_index": 0,
                    "weight_attribute": {"component_index": 0, "format": 29},
                    "resolved_active_influence_count": 2,
                    "unresolved_active_influence_count": 0,
                    "matrix_validation": {
                        "status": "matches",
                        "comparison_formula_candidate": "mesh_world_times_inverse_bone_world",
                        "vector_convention_candidate": "row_vector",
                        "active_palette_slot_count": 2,
                        "matched_active_palette_slot_count": 2,
                        "mismatched_active_palette_slot_count": 0,
                        "unavailable_active_palette_slot_count": 0,
                        "unreferenced_palette_slot_count": 0,
                        "max_abs_error": 1.0e-6,
                        "palette_slots": [],
                    },
                    "section_skinning": [{
                        "section_index": 0, "material_index": 0, "material_slot_id": 0,
                        "status": "ok", "vertex_indices": [0],
                        "resolved_active_influence_count": 2,
                        "unresolved_active_influence_count": 0,
                        "bone_index_sets": [{
                            "component_index": 0,
                            "resolved_active_influence_count": 2,
                            "unresolved_active_influence_count": 0,
                            "vertex_influences": [base],
                        }],
                    }],
                },
            }],
            "warnings": [],
            "limitations": [],
        }
        summary = build_summary(report)
        index_set = summary["meshes"][0]["sections"][0]["bone_index_sets"][0]
        self.assertEqual(index_set["vertex_count"], 1)
        self.assertEqual(index_set["active_influence_count"], 2)
        self.assertEqual(index_set["influence_count_histogram"], {2: 1})
        self.assertEqual(index_set["weight_sum_stats"]["within_1e_3"], 1)
        self.assertEqual(len(index_set["mapping_rows"]), 2)
        self.assertEqual(index_set["mapping_conflict_local_indices"], [])

        markdown = render_markdown(summary)
        self.assertIn("BoneInfo matrix consistency check", markdown)
        self.assertIn("Local index", markdown)
        self.assertNotIn("all per-lane dump", markdown.split("## Detailed section mapping")[0])

    def test_compact_report_flags_one_local_index_with_conflicting_destinations(self):
        report = {
            "unit": {"file_id": "unit"},
            "package": {"name": "package"},
            "bone_info": {"records": []},
            "transform_info": {},
            "geometry": [{
                "mesh_id": 456,
                "skinning": {
                    "section_skinning": [{
                        "section_index": 0, "material_index": 0, "status": "ok",
                        "bone_index_sets": [{
                            "component_index": 0,
                            "vertex_influences": [
                                {"vertex_index": 0, "influences": [
                                    {"lane": 0, "weight": 1.0, "local_bone_index": 3,
                                     "palette_slot": 2, "transform_index": 9, "bone_name": "root", "status": "ok"}
                                ]},
                                {"vertex_index": 1, "influences": [
                                    {"lane": 0, "weight": 1.0, "local_bone_index": 3,
                                     "palette_slot": 4, "transform_index": 12, "bone_name": "hips", "status": "ok"}
                                ]},
                            ],
                        }],
                    }],
                },
            }],
        }
        summary = build_summary(report)
        index_set = summary["meshes"][0]["sections"][0]["bone_index_sets"][0]
        self.assertEqual(index_set["mapping_conflict_local_indices"], [3])
        markdown = render_markdown(summary)
        self.assertIn("Mapping conflicts: 3", markdown)


if __name__ == "__main__":
    unittest.main()
