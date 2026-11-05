import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

from research.survey_source_model_lod_coverage import (  # noqa: E402
    _count,
    discover_models,
    lod_deltas,
    render_markdown,
    skeleton_comparison,
    summarize,
)


def profile(member: str, bones: list[str], vvd_lods: int, weighted: list[list[int]],
            triangles: int = 100, fixups: int = 0) -> dict:
    rows = [{"lod_index": lod, "triangles": triangles, "triangles_included": None,
             "vertices": 10 + lod, "weighted_bone_indices": sorted(indices),
             "weighted_bone_count": len(indices),
             "weighted_bone_names": [bones[i] for i in sorted(indices)],
             "unweighted_vertices": 0, "invalid_bone_references": 0}
            for lod, indices in enumerate(weighted)]
    return {
        "member": member, "mdl_name": member, "mdl_version": 48,
        "bone_count": len(bones), "bodygroup_count": 1, "bodygroup_options": 2,
        "texture_count": 3, "skin_family_count": 1, "vvd_version": 4,
        "vvd_lod_count": vvd_lods, "vvd_lod0_vertices": 1000, "vvd_fixups": fixups,
        "vtx_version": 7, "vtx_lod_count": vvd_lods, "multi_lod": vvd_lods > 1,
        "lod_usage": rows, "lod_deltas_vs_lod0": lod_deltas(rows),
        "skeleton_signature": sorted(name.lower() for name in bones),
        "warnings": [],
    }


class HD2SourceLodDiscoveryTests(unittest.TestCase):
    def test_count_accepts_lists_and_integers(self):
        self.assertEqual(_count([1, 2, 3]), 3)
        self.assertEqual(_count({"a": 1}), 1)
        self.assertEqual(_count(7), 7)
        self.assertEqual(_count(None), 0)
        self.assertEqual(_count(True), 0)

    def test_discover_models_on_a_directory(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "models" / "a").mkdir(parents=True)
            (root / "models" / "b").mkdir(parents=True)
            (root / "models" / "a" / "first.mdl").write_bytes(b"x")
            (root / "models" / "b" / "second.mdl").write_bytes(b"x")
            (root / "models" / "b" / "ignored.vvd").write_bytes(b"x")
            self.assertEqual(discover_models(root),
                             ["models/a/first.mdl", "models/b/second.mdl"])

    def test_discover_models_on_a_zip(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "addon.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("models/x/one.mdl", b"x")
                archive.writestr("models/x/one.vvd", b"x")
                archive.writestr("models/Y/two.MDL", b"x")
            self.assertEqual(discover_models(path), ["models/Y/two.MDL", "models/x/one.mdl"])

    def test_single_mdl_file_yields_itself(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "solo.mdl"
            path.write_bytes(b"x")
            self.assertEqual(discover_models(path), ["solo.mdl"])


class HD2SourceLodAnalysisTests(unittest.TestCase):
    def test_lod_deltas_find_added_and_removed_bones(self):
        rows = [
            {"lod_index": 0, "weighted_bone_indices": [0, 1, 2]},
            {"lod_index": 1, "weighted_bone_indices": [0, 1, 5]},
            {"lod_index": 2, "weighted_bone_indices": [0, 1, 2]},
        ]
        deltas = lod_deltas(rows)
        self.assertTrue(deltas[0]["same_as_lod0"])
        self.assertEqual(deltas[1]["added_vs_lod0"], [5])
        self.assertEqual(deltas[1]["removed_vs_lod0"], [2])
        self.assertFalse(deltas[1]["same_as_lod0"])
        self.assertTrue(deltas[2]["same_as_lod0"])
        self.assertEqual(lod_deltas([]), [])

    def test_skeleton_groups_share_only_identical_name_sets(self):
        profiles = [
            profile("a.mdl", ["Root", "Bip01"], 1, [[1]]),
            profile("b.mdl", ["bip01", "root"], 1, [[1]]),
            profile("c.mdl", ["Root", "Bip01", "Tail"], 1, [[1, 2]]),
        ]
        comparison = skeleton_comparison(profiles)
        self.assertEqual(comparison["distinct_skeletons"], 2)
        self.assertEqual(comparison["groups"][0]["members"], ["a.mdl", "b.mdl"])
        self.assertIn("tail", comparison["shared_bone_name_union"])

    def test_summarize_counts_multi_lod_and_delta_rows(self):
        profiles = [
            profile("single.mdl", ["A", "B"], 1, [[0]], fixups=0),
            profile("multi.mdl", ["A", "B", "C"], 3, [[0, 1], [0, 1], [0]], fixups=4),
        ]
        summary = summarize(profiles, [{"member": "bad.mdl", "error": "v49"}])
        self.assertEqual(summary["models_profiled"], 2)
        self.assertEqual(summary["models_failed"], 1)
        self.assertEqual(summary["models_with_multiple_vvd_lods"], 1)
        self.assertEqual(summary["max_vvd_lod_count"], 3)
        self.assertEqual(summary["models_with_fixups"], 1)
        self.assertEqual(summary["lod_rows_with_bone_delta"], 1)

    def test_markdown_states_the_single_lod_gap(self):
        profiles = [profile("single.mdl", ["A", "B"], 1, [[0]]),
                    profile("multi.mdl", ["A", "B", "C"], 2, [[0, 1], [0]])]
        failures = [{"member": "bad.mdl", "error": "supports MDL v48 only; found v49"}]
        markdown = render_markdown(Path("addon.gma"), profiles, failures,
                                  summarize(profiles, failures),
                                  skeleton_comparison(profiles))
        self.assertIn("# Source Model LOD Coverage Survey", markdown)
        self.assertIn("single VVD LOD, so per-LOD bone use cannot be measured", markdown)
        self.assertIn("added / 1 removed vs LOD 0", markdown)
        self.assertIn("supports MDL v48 only; found v49", markdown)
        self.assertIn("## Interpretation limits", markdown)
        self.assertIn("name-level comparison", markdown)

if __name__ == "__main__":
    unittest.main()
