import unittest

from hd2_bone_aliases import build_alias_candidates


class BoneAliasCandidateTests(unittest.TestCase):
    def test_candidates_are_advisory_and_parent_checked_when_source_parent_maps(self):
        report = {
            "source_unmapped": [
                {
                    "source_name": "ValveBiped.Bip01_Spine",
                    "source_node_id": 27,
                    "source_parent_name": "ValveBiped.Bip01_Pelvis",
                    "source_has_active_vertex_influence": True,
                },
                {
                    "source_name": "ValveBiped.Bip01_Pelvis",
                    "source_node_id": 0,
                    "source_parent_name": None,
                    "source_has_active_vertex_influence": True,
                },
            ],
            "target_only": [
                {
                    "target_name": "spine1",
                    "target_transform_index": 21,
                    "target_parent_name": "boss",
                    "target_has_active_vertex_influence": True,
                },
                {
                    "target_name": "hips",
                    "target_transform_index": 9,
                    "target_parent_name": "boss",
                    "target_has_active_vertex_influence": True,
                },
            ],
        }
        result = build_alias_candidates(report)
        rows = {row["source_name"]: row for row in result["candidates"]}
        self.assertEqual(result["candidate_count"], 2)
        self.assertEqual(rows["ValveBiped.Bip01_Spine"]["parent_status"], "mismatch")
        self.assertEqual(rows["ValveBiped.Bip01_Pelvis"]["parent_status"],
                         "not_checked_source_parent_has_no_candidate_alias")
        self.assertTrue(all(row["approval"] == "advisory_only"
                            for row in result["candidates"]))

    def test_missing_source_and_target_names_are_reported_not_guessed(self):
        result = build_alias_candidates({"source_unmapped": [], "target_only": []})
        self.assertEqual(result["candidate_count"], 0)
        self.assertEqual(len(result["source_names_absent_from_report"]), 22)
        self.assertEqual(len(result["target_names_absent_from_report"]), 0)


if __name__ == "__main__":
    unittest.main()
