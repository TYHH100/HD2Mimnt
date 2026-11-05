import unittest

from hd2_skeleton_compare import compare_source_skeleton_to_hd2
from smd_reader import parse_smd_bytes


def make_smd():
    return b'''version 1
nodes
0 "root" -1
1 "spine" 0
2 "source_only" 1
end
skeleton
time 0
0 0 0 0 0 0 0
1 0 1 0 0 0 0
2 0 2 0 0 0 0
end
triangles
body
0 0 0 0 0 0 1 0 0
1 1 0 0 0 0 1 1 0
2 0 1 0 0 0 1 0 1
end
'''


def target_report():
    return {
        "package": {"name": "avatar-package"},
        "unit": {"file_id": "0123456789abcdef", "geometry_source": "CompositeUnit"},
        "bones_resource": {"file_id": "fedcba9876543210"},
        "transform_info": {
            "resolved_skeleton": [
                {
                    "transform_index": 0, "name": "ROOT", "name_hash": 10,
                    "parent_transform_index": None, "parent_name": None,
                    "has_active_vertex_influence": False,
                    "active_vertex_influence_lane_count": 0,
                },
                {
                    "transform_index": 1, "name": "Spine", "name_hash": 11,
                    "parent_transform_index": 0, "parent_name": "ROOT",
                    "has_active_vertex_influence": True,
                    "active_vertex_influence_lane_count": 3,
                },
                {
                    "transform_index": 2, "name": "hd2_extra", "name_hash": 12,
                    "parent_transform_index": 1, "parent_name": "Spine",
                    "has_active_vertex_influence": True,
                    "active_vertex_influence_lane_count": 1,
                },
            ],
            "weight_usage_summary": {
                "named_transform_count": 3,
                "actively_weighted_transform_count": 2,
            },
        },
        "warnings": [],
    }


class SourceHD2SkeletonComparisonTests(unittest.TestCase):
    def test_matches_exact_names_case_insensitively_and_checks_parent(self):
        source = parse_smd_bytes(make_smd(), "AES_T.smd")
        result = compare_source_skeleton_to_hd2(source, target_report())
        self.assertEqual(result["summary"]["exact_name_match_count"], 2)
        self.assertEqual(result["summary"]["exact_name_match_active_target_count"], 1)
        self.assertEqual(result["summary"]["source_unmapped_count"], 1)
        self.assertEqual(result["summary"]["target_only_named_transform_count"], 1)
        self.assertEqual(result["summary"]["parent_mismatch_count"], 0)
        self.assertEqual(result["summary"]["status"], "warning")
        self.assertEqual(result["source_unmapped"][0]["source_name"], "source_only")
        self.assertEqual(result["target_only"][0]["target_name"], "hd2_extra")

    def test_reports_parent_mismatch_without_guessing_a_reparent(self):
        source = parse_smd_bytes(make_smd(), "AES_T.smd")
        target = target_report()
        target["transform_info"]["resolved_skeleton"][1]["parent_transform_index"] = 2
        target["transform_info"]["resolved_skeleton"][1]["parent_name"] = "hd2_extra"
        result = compare_source_skeleton_to_hd2(source, target)
        self.assertEqual(result["summary"]["parent_mismatch_count"], 1)
        self.assertEqual(result["parent_mismatches"][0]["source_name"], "spine")

    def test_reports_ambiguous_target_names(self):
        source = parse_smd_bytes(make_smd(), "AES_T.smd")
        target = target_report()
        extra = dict(target["transform_info"]["resolved_skeleton"][1])
        extra["transform_index"] = 9
        target["transform_info"]["resolved_skeleton"].append(extra)
        result = compare_source_skeleton_to_hd2(source, target)
        self.assertEqual(result["summary"]["ambiguous_source_match_count"], 1)
        self.assertEqual(result["ambiguous_source_matches"][0]["source_name"], "spine")

    def test_reports_unresolved_target_name_hashes(self):
        source = parse_smd_bytes(make_smd(), "AES_T.smd")
        target = target_report()
        target["transform_info"]["resolved_skeleton"].append({
            "transform_index": 10, "name": None, "name_hash": 999,
            "name_ambiguous": True, "parent_transform_index": None,
            "parent_name": None,
        })
        result = compare_source_skeleton_to_hd2(source, target)
        self.assertEqual(result["summary"]["ambiguous_target_name_count"], 1)
        self.assertEqual(len(result["ambiguous_target_names"]), 1)

    def test_reports_source_active_weighted_nodes_separately(self):
        source = parse_smd_bytes(make_smd(), "AES_T.smd")
        result = compare_source_skeleton_to_hd2(
            source, target_report(), source_active_node_ids={0, 2},
            source_kind="compiled_mdl_v48",
        )
        self.assertEqual(result["summary"]["source_active_weighted_node_count"], 2)
        self.assertEqual(result["summary"]["source_active_weighted_unmapped_count"], 1)
        matched = {row["source_name"]: row for row in result["matched"]}
        self.assertFalse(matched["root"]["source_has_active_vertex_influence"])
        self.assertEqual(result["summary"]["source_skeleton_type"], "compiled_mdl_v48")

    def test_does_not_claim_rest_pose_numeric_comparison(self):
        source = parse_smd_bytes(make_smd(), "AES_T.smd")
        result = compare_source_skeleton_to_hd2(source, target_report())
        self.assertEqual(
            result["matching_policy"]["rest_pose_numeric_comparison"],
            "not performed",
        )
        self.assertFalse(result["matching_policy"]["mapping_is_applied"])


if __name__ == "__main__":
    unittest.main()
