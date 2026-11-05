import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from research.classify_hd2_mesh_bindings import build_report, classify, collect, render_markdown  # noqa: E402


IDENTITY = [
    1.0, 0.0, 0.0, 0.0,
    0.0, 1.0, 0.0, 0.0,
    0.0, 0.0, 1.0, 0.0,
    0.0, 0.0, 0.0, 1.0,
]


def identity_matrix():
    return list(IDENTITY)


def translated_matrix(offset):
    return [
        1.0, 0.0, 0.0, 0.0,
        0.0, 1.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, offset, 1.0,
    ]


def matrix_with_scaled_rotation():
    return [
        0.0, 2.0, 0.0, 0.0,
        1.0, 0.0, 0.0, 0.0,
        0.0, 0.0, 1.0, 0.0,
        0.0, 0.0, 0.0, 1.0,
    ]


def matrix_missing_translation_row():
    return [1.0] * 15


def base_binding(index=0, mesh_id=700, lod_index=0, transform_index=1, vertex_count=100,
                 skinning=None, slot_ids=(7000,)):
    return {
        "mesh_info_index": index, "mesh_id": mesh_id, "transform_index": transform_index,
        "lod_index": lod_index, "vertex_count": vertex_count, "triangle_count": vertex_count,
        "sections": [{"index": n, "material_slot_id": slot} for n, slot in enumerate(slot_ids)],
        "skinning": skinning or {"status": "ok", "resolved_active_influence_count": vertex_count * 2,
                                 "unresolved_active_influence_count": 0},
    }


def base_transform(index=1, matrix=None):
    return {"index": index, "name_hash": 200 + index, "parent_index_raw": 0,
            "matrix_4x4_raw": matrix if matrix is not None else identity_matrix()}


def fixture(binding, transform, hierarchy_status="matches"):
    return {
        "unit": {"file_id": "unit-fixture", "geometry_source": "Unit GPU payload"},
        "package": {"name": "package-fixture"},
        "transform_info": {
            "transforms": [base_transform(index=0), transform],
            "resolved_skeleton": [
                {"transform_index": 0, "name": "root"},
                {"transform_index": transform["index"], "name": "g_fixture"},
            ],
            "hierarchy_matrix_validation": {"entries": [
                {"transform_index": transform["index"], "status": hierarchy_status,
                 "max_abs_error": 0.0}
            ]},
        },
        "mesh_info": {"records": [
            {"index": binding["mesh_info_index"], "mesh_id": binding["mesh_id"],
             "transform_index": binding["transform_index"], "lod_index": binding["lod_index"]}
        ]},
        "geometry": [binding],
    }


def collect_fixture(data, label="fixture"):
    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "unit.json"
        path.write_text(json.dumps(data), encoding="utf-8")
        return collect(path, label)


class HD2MeshBindingClassificationTests(unittest.TestCase):
    def one(self, binding, transform, hierarchy_status="matches"):
        unit = collect_fixture(fixture(binding, transform, hierarchy_status))
        return unit["bindings"][0]

    def test_skinned_identity_hierarchy_matching_is_render_lod(self):
        row = self.one(base_binding(), base_transform())
        self.assertEqual(row["observed_class"], "skinned_render_lod")
        self.assertEqual(row["stored_matrix_identity_status"], "identity")
        self.assertIn("200 resolved influence lanes", row["classification_reasons"])

    def test_skinned_non_identity_mesh_matrix_is_placement_case(self):
        row = self.one(base_binding(), base_transform(matrix=translated_matrix(1.43)))
        self.assertEqual(row["observed_class"], "skinned_non_identity_placement")
        self.assertEqual(row["stored_matrix_identity_status"], "non_identity")
        self.assertIn("mesh's own TransformInfo matrix is not the identity", row["classification_reasons"])

    def test_skinned_mesh_with_unverified_hierarchy_is_separate_case(self):
        row = self.one(base_binding(), base_transform(), hierarchy_status="unresolved")
        self.assertEqual(row["observed_class"], "skinned_hierarchy_unverified")

    def test_no_skin_attributes_at_negative_lod_is_auxiliary_volume(self):
        row = self.one(
            base_binding(lod_index=-1, skinning={"status": "no_skin_attributes"}),
            base_transform(matrix=translated_matrix(1.43)),
        )
        self.assertEqual(row["observed_class"], "auxiliary_unskinned")
        self.assertIn("geometry declares no skin attributes", row["classification_reasons"])
        self.assertIn("LOD index is -1", row["classification_reasons"])

    def test_no_skin_attributes_at_positive_lod_is_not_merged_with_auxiliary(self):
        row = self.one(
            base_binding(lod_index=2, skinning={"status": "no_skin_attributes"}),
            base_transform(),
        )
        self.assertEqual(row["observed_class"], "unskinned_at_non_negative_lod")

    def test_skinned_attributes_without_lanes_are_not_render_lods(self):
        row = self.one(
            base_binding(skinning={"status": "partial", "resolved_active_influence_count": 0}),
            base_transform(),
        )
        self.assertEqual(row["observed_class"], "skinned_without_lanes")

    def test_binding_without_geometry_row_is_undecoded(self):
        data = fixture(base_binding(vertex_count=0), base_transform())
        data["geometry"] = []
        row = collect_fixture(data)["bindings"][0]
        self.assertEqual(row["observed_class"], "undecoded_geometry")

    def test_unparseable_matrix_is_unavailable_not_identity(self):
        row = self.one(base_binding(), base_transform(matrix=matrix_missing_translation_row()))
        self.assertEqual(row["stored_matrix_identity_status"], "unavailable")
        self.assertEqual(row["stored_matrix_identity_error"], None)
        self.assertEqual(row["observed_class"], "undecidable_placement")

    def test_unknown_fallback_is_reachable_from_a_stored_state(self):
        row = self.one(
            base_binding(skinning={"status": "brand_new_state", "resolved_active_influence_count": 5}),
            base_transform(),
        )
        self.assertEqual(row["observed_class"], "unknown")
        self.assertEqual(row["classification_reasons"], ["unrecognized skinning state 'brand_new_state'"])

    def test_unknown_fallback_is_reachable_without_a_stored_binding(self):
        observed_class, reasons = classify({
            "lod_index": 0, "skinning_status": "brand_new_state",
            "resolved_active_influence_count": 5, "stored_matrix_identity_status": "identity",
            "hierarchy_validation_status": "matches", "vertex_count": 10,
        })
        self.assertEqual(observed_class, "unknown")
        self.assertEqual(reasons, ["unrecognized skinning state 'brand_new_state'"])

    def test_names_are_reference_only_and_never_decide_class(self):
        culling_like = self.one(
            base_binding(lod_index=4, skinning={"status": "ok", "resolved_active_influence_count": 880}),
            base_transform(matrix=translated_matrix(1.43)),
        )
        ordinary = self.one(
            base_binding(lod_index=0, skinning={"status": "ok", "resolved_active_influence_count": 880}),
            base_transform(matrix=translated_matrix(1.43)),
        )
        # The stored structure is identical; only the reference name differs, so the
        # decision must be identical instead of being renamed by a word like culling.
        self.assertEqual(culling_like["observed_class"], ordinary["observed_class"])
        self.assertEqual(culling_like["observed_class"], "skinned_non_identity_placement")

    def test_report_tally_and_definitions_cover_every_class_found(self):
        units = [collect_fixture(fixture(base_binding(), base_transform()), "a"),
                 collect_fixture(fixture(base_binding(mesh_id=701, transform_index=1),
                                         base_transform(matrix=matrix_with_scaled_rotation())), "b"),
                 collect_fixture(fixture(base_binding(lod_index=-1, mesh_id=702,
                                                     skinning={"status": "no_skin_attributes"}),
                                         base_transform()), "c")]
        report = build_report(units)
        self.assertEqual(report["units"], 3)
        self.assertEqual(report["bindings"], 3)
        self.assertEqual(report["class_tally"], {
            "auxiliary_unskinned": 1,
            "skinned_non_identity_placement": 1,
            "skinned_render_lod": 1,
        })
        defined = {definition["class"] for definition in report["class_definitions"]}
        self.assertTrue(set(report["class_tally"]).issubset(defined))
        self.assertIn("unknown", defined)
        markdown = render_markdown(report)
        for name in report["class_tally"]:
            self.assertIn(name, markdown)

    def multi(self, bindings, label, hierarchy_status="matches"):
        transforms = [base_transform(index=0)]
        for index, binding in enumerate(bindings, start=1):
            transforms.append(base_transform(index=binding["transform_index"]))
        records = [
            {"index": binding["mesh_info_index"], "mesh_id": binding["mesh_id"],
             "transform_index": binding["transform_index"], "lod_index": binding["lod_index"]}
            for binding in bindings
        ]
        skeleton = [{"transform_index": 0, "name": "root"}] + [
            {"transform_index": binding["transform_index"], "name": f"g_fixture_{binding['lod_index']}"}
            for binding in bindings
        ]
        validation = {"entries": [
            {"transform_index": binding["transform_index"], "status": hierarchy_status,
             "max_abs_error": 0.0} for binding in bindings
        ]}
        data = {
            "unit": {"file_id": f"unit-{label}", "geometry_source": "Unit GPU payload"},
            "package": {"name": f"package-{label}"},
            "transform_info": {
                "transforms": transforms, "resolved_skeleton": skeleton,
                "hierarchy_matrix_validation": validation,
            },
            "mesh_info": {"records": records},
            "geometry": bindings,
        }
        return collect_fixture(data, label)

    def test_reference_used_only_above_base_level_is_split_from_garment_lods(self):
        garment = [
            base_binding(index=0, mesh_id=700, lod_index=0, transform_index=1, slot_ids=(7000,)),
            base_binding(index=1, mesh_id=701, lod_index=1, transform_index=1, slot_ids=(7000,)),
            base_binding(index=2, mesh_id=702, lod_index=3, transform_index=1, slot_ids=(8000,)),
        ]
        report = build_report([self.multi(garment, "a")])
        rows = report["units_detail"][0]["bindings"]
        self.assertEqual([row["material_reference_profile"] for row in rows],
                         ["base_lod_bound", "base_lod_bound", "family_no_base_lod_upper_lod"])
        census = {entry["material_slot_id"]: entry for entry in report["slot_census"]}
        self.assertTrue(census[7000]["appears_at_base_lod"])
        self.assertFalse(census[8000]["appears_at_base_lod"])
        self.assertEqual(census[8000]["lod_indices"], [3])

    def test_reuse_across_units_alone_is_not_a_family(self):
        # Twins of one garment share the garment reference at their base LOD, so
        # reuse has to be the wrong answer or the split collapses back onto names.
        bindings = [
            base_binding(lod_index=0, slot_ids=(7000,)),
            base_binding(index=1, mesh_id=702, lod_index=3, slot_ids=(7000,)),
        ]
        report = build_report([self.multi(bindings, "female"), self.multi(bindings, "male")])
        entry = report["slot_census"][0]
        self.assertEqual(entry["material_slot_id"], 7000)
        self.assertEqual(entry["unit_count"], 2)
        self.assertEqual(entry["reference_profile"], "base_lod_reference")
        for unit in report["units_detail"]:
            self.assertEqual([row["material_reference_profile"] for row in unit["bindings"]],
                             ["base_lod_bound", "base_lod_bound"])

    def test_reference_used_at_negative_level_is_split_from_garment_lods(self):
        garment = base_binding(lod_index=0, slot_ids=(7000,))
        volume = base_binding(index=1, mesh_id=702, lod_index=-1, slot_ids=(9000,),
                              skinning={"status": "no_skin_attributes"})
        report = build_report([self.multi([garment, volume], "a")])
        rows = report["units_detail"][0]["bindings"]
        self.assertEqual([row["material_reference_profile"] for row in rows],
                         ["base_lod_bound", "family_no_base_lod_negative_lod"])
        self.assertEqual([row["observed_class"] for row in rows],
                         ["skinned_render_lod", "auxiliary_unskinned"])

    def test_binding_mixing_both_kinds_of_reference_is_left_unsplit(self):
        garment = base_binding(lod_index=0, slot_ids=(7000,))
        mixed = base_binding(index=1, mesh_id=702, lod_index=2, slot_ids=(7000, 8000))
        report = build_report([self.multi([garment, mixed], "a")])
        rows = report["units_detail"][0]["bindings"]
        self.assertEqual([row["material_reference_profile"] for row in rows],
                         ["base_lod_bound", "mixed_references"])

    def test_binding_without_sections_has_no_references(self):
        garment = base_binding(lod_index=0, slot_ids=(7000,))
        bare = base_binding(index=1, mesh_id=702, lod_index=1)
        bare.pop("sections")
        report = build_report([self.multi([garment, bare], "a")])
        rows = report["units_detail"][0]["bindings"]
        self.assertEqual(rows[1]["section_material_slot_ids"], [])
        self.assertEqual(rows[1]["material_reference_profile"], "no_section_references")

    def test_report_reports_reference_profiles_and_census_together(self):
        garment = [
            base_binding(lod_index=0, slot_ids=(7000,)),
            base_binding(index=1, mesh_id=702, lod_index=3, slot_ids=(8000,)),
        ]
        report = build_report([self.multi(garment, "a")])
        self.assertEqual(report["reference_profile_tally"],
                         {"base_lod_bound": 1, "family_no_base_lod_upper_lod": 1})
        self.assertEqual(report["profile_by_class"]["family_no_base_lod_upper_lod"],
                         {"skinned_render_lod": 1})
        markdown = render_markdown(report)
        self.assertIn("## Section material reference census", markdown)
        self.assertIn("## Reference profile by structure class", markdown)
        self.assertIn("family_no_base_lod_upper_lod", markdown)


if __name__ == "__main__":
    unittest.main()
