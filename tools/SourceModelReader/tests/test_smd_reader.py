import tempfile
import unittest
import zipfile
from pathlib import Path
from types import SimpleNamespace

from smd_reader import (SMDParseError, classify_smd_type, compare_smd_skeletons,
                        compare_smd_to_mdl, parse_smd_bytes)
from source_model_reader import build_smd_audit_report, collect_smd_sources


def make_smd(root_id=0, child_id=1, child_parent=None, child_x=1.0, include_triangles=True):
    if child_parent is None:
        child_parent = root_id
    triangles = ""
    if include_triangles:
        triangles = """triangles
body_material
0 0 0 0 0 0 1 0 0
1 1 0 0 0 0 1 1 0
1 0 1 0 0 0 1 0 1
end
"""
    return f'''version 1

nodes
{root_id} "root" -1
{child_id} "child bone" {child_parent}
end

skeleton
time 0
{root_id} 0 0 0 0 0 0
{child_id} {child_x} 0 0 0 0 0
time 1
{root_id} 0 0 0 0 0 0
{child_id} {child_x} 0 0 0.1 0 0
end

{triangles}'''.encode("utf-8")


class SMDParserTests(unittest.TestCase):
    def test_reads_nodes_reference_pose_frames_and_triangles(self):
        doc = parse_smd_bytes(make_smd(), "fixture.smd")
        self.assertEqual(len(doc.nodes), 2)
        self.assertEqual(doc.root_count, 1)
        self.assertEqual(doc.leaf_count, 1)
        self.assertEqual(sorted(doc.frames), [0, 1])
        self.assertEqual(doc.reference_pose_time, 0)
        self.assertEqual(doc.reference_pose[1].position, (1.0, 0.0, 0.0))
        self.assertEqual(doc.triangle_count, 1)
        self.assertEqual(doc.materials, ["body_material"])

    def test_accepts_non_contiguous_node_ids(self):
        doc = parse_smd_bytes(make_smd(root_id=12, child_id=40, child_parent=12), "sparse.smd")
        self.assertEqual(doc.node_by_id[40].parent_id, 12)
        self.assertEqual(doc.reference_pose[40].position[0], 1.0)

    def test_rejects_duplicate_node_ids(self):
        raw = b'version 1\nnodes\n0 "root" -1\n0 "duplicate" -1\nend\n'
        with self.assertRaises(SMDParseError):
            parse_smd_bytes(raw, "duplicate.smd")

    def test_rejects_non_finite_pose(self):
        raw = b'version 1\nnodes\n0 "root" -1\nend\nskeleton\ntime 0\n0 nan 0 0 0 0 0\nend\n'
        with self.assertRaises(SMDParseError):
            parse_smd_bytes(raw, "nan.smd")

    def test_cross_slice_comparison_matches_by_name_not_numeric_id(self):
        reference = parse_smd_bytes(make_smd(), "base.smd")
        candidate = parse_smd_bytes(make_smd(root_id=10, child_id=11, child_parent=10), "slice.smd")
        result = compare_smd_skeletons(reference, candidate)
        self.assertEqual(result["matched_node_count"], 2)
        self.assertEqual(result["node_id_remap_count"], 2)
        self.assertEqual(result["parent_mismatch_count"], 0)
        self.assertEqual(result["reference_pose_mismatch_count"], 0)
        self.assertEqual(result["status"], "ok")

    def test_cross_slice_comparison_detects_parent_and_pose_drift(self):
        reference = parse_smd_bytes(make_smd(), "base.smd")
        candidate = parse_smd_bytes(make_smd(child_parent=-1, child_x=2.0), "bad.smd")
        result = compare_smd_skeletons(reference, candidate)
        self.assertEqual(result["parent_mismatch_count"], 1)
        self.assertEqual(result["reference_pose_mismatch_count"], 1)
        self.assertEqual(result["status"], "warning")

    def test_maps_smd_nodes_to_mdl_by_name(self):
        doc = parse_smd_bytes(make_smd(), "base.smd")
        mdl_bones = [
            SimpleNamespace(index=0, name="root", parent=-1, pos=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0)),
            SimpleNamespace(index=1, name="child bone", parent=0, pos=(1.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0)),
        ]
        result = compare_smd_to_mdl(doc, mdl_bones)
        self.assertEqual(result["matched_name_count"], 2)
        self.assertEqual(result["parent_mismatch_count"], 0)
        self.assertEqual(result["reference_pose_mismatch_count"], 0)
        self.assertEqual(result["status"], "ok")

    def test_classifies_common_smd_roles(self):
        self.assertEqual(classify_smd_type("AES_T.smd", 3, {0: {}}), "model_slice")
        self.assertEqual(classify_smd_type("aes_yz_pm_physics.smd", 3, {0: {}}), "physics_geometry")
        self.assertEqual(classify_smd_type("aes_yz_pm_anims/idle_all_01.smd", 0, {0: {}, 1: {}}), "animation")
        self.assertEqual(classify_smd_type("aes_yz_pm_anims/a_proportions.smd", 0, {0: {}}), "proportion_animation")
        self.assertEqual(classify_smd_type("aes_yz_pm_anims/a_proportions_corrective_animation.smd", 0, {0: {}}), "corrective_animation")
        self.assertEqual(classify_smd_type("aes_yz_pm_anims/reference.smd", 0, {0: {}}), "animation_reference")
        self.assertEqual(classify_smd_type("aes_yz_pm_anims/ragdoll.smd", 0, {0: {}}), "ragdoll_pose")

    def test_animation_pose_is_not_compared_as_bind_pose(self):
        reference = parse_smd_bytes(make_smd(), "AES_T.smd")
        animation = parse_smd_bytes(make_smd(child_x=12.0), "aes_yz_pm_anims/idle_all_01.smd")
        result = compare_smd_skeletons(reference, animation)
        self.assertEqual(result["structure_status"], "ok")
        self.assertEqual(result["reference_pose_comparison"], "skipped_incompatible_smd_type")
        self.assertEqual(result["reference_pose_status"], "not_compared")
        self.assertEqual(result["reference_pose_mismatch_count"], 0)
        self.assertEqual(result["status"], "ok")

    def test_animation_structure_mismatch_is_still_reported(self):
        reference = parse_smd_bytes(make_smd(), "AES_T.smd")
        animation = parse_smd_bytes(make_smd(child_parent=-1, child_x=9.0), "anims/idle.smd")
        result = compare_smd_skeletons(reference, animation)
        self.assertEqual(result["reference_pose_comparison"], "skipped_incompatible_smd_type")
        self.assertEqual(result["reference_pose_mismatch_count"], 0)
        self.assertEqual(result["parent_mismatch_count"], 1)
        self.assertEqual(result["structure_status"], "warning")
        self.assertEqual(result["status"], "warning")


    def test_sparse_corrective_animation_frame_is_informational_with_coverage(self):
        node_lines = [
            '0 "root" -1',
            '1 "child" 0',
            '2 "helper" 1',
        ]
        raw = ("version 1\nnodes\n" + "\n".join(node_lines) +
               "\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\n").encode()
        doc = parse_smd_bytes(raw, "anims/a_proportions_corrective_animation.smd")
        self.assertEqual(doc.smd_type, "corrective_animation")
        self.assertEqual(doc.warnings, [])
        self.assertEqual(len(doc.infos), 1)
        self.assertIn("1 / 3", doc.infos[0])
        self.assertIn("2 omitted", doc.infos[0])
        self.assertEqual(doc.frame_coverage[0]["explicit_transform_count"], 1)
        self.assertEqual(doc.frame_coverage[0]["omitted_transform_count"], 2)
        self.assertAlmostEqual(doc.frame_coverage[0]["coverage_ratio"], 1 / 3)

    def test_sparse_model_slice_frame_remains_a_warning(self):
        raw = make_smd().decode("utf-8").replace("1 1.0 0 0 0 0 0\n", "")
        doc = parse_smd_bytes(raw.encode(), "AES_T.smd")
        self.assertEqual(doc.smd_type, "model_slice")
        self.assertTrue(any("omits 1 node transform" in warning for warning in doc.warnings))
        self.assertEqual(doc.infos, [])

    def test_empty_animation_frame_is_still_a_warning(self):
        raw = b'version 1\nnodes\n0 "root" -1\nend\nskeleton\ntime 0\nend\n'
        doc = parse_smd_bytes(raw, "anims/empty.smd")
        self.assertEqual(doc.smd_type, "animation")
        self.assertTrue(any("contains no transforms" in warning for warning in doc.warnings))

    def test_mdl_mapping_skips_pose_for_animation(self):
        animation = parse_smd_bytes(make_smd(child_x=12.0), "anims/idle.smd")
        mdl_bones = [
            SimpleNamespace(index=0, name="root", parent=-1, pos=(0.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0)),
            SimpleNamespace(index=1, name="child bone", parent=0, pos=(1.0, 0.0, 0.0), rot=(0.0, 0.0, 0.0)),
        ]
        result = compare_smd_to_mdl(animation, mdl_bones)
        self.assertEqual(result["status"], "ok")
        self.assertEqual(result["reference_pose_comparison"], "skipped_incompatible_smd_type")
        self.assertEqual(result["reference_pose_mismatch_count"], 0)


class SMDAuditInputTests(unittest.TestCase):
    def test_collects_smd_from_directory_and_zip(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "a.smd").write_bytes(make_smd())
            (root / "ignore.txt").write_text("not smd", encoding="utf-8")
            found, info = collect_smd_sources(root)
            self.assertEqual([name for name, _ in found], ["a.smd"])
            self.assertEqual(info["smd_count"], 1)
            archive = root / "fixture.zip"
            with zipfile.ZipFile(archive, "w") as zf:
                zf.writestr("models/a.smd", make_smd())
                zf.writestr("models/b.smd", make_smd(root_id=10, child_id=11, child_parent=10))
            found_zip, info_zip = collect_smd_sources(archive)
            self.assertEqual(len(found_zip), 2)
            self.assertEqual(info_zip["container_type"], "zip")

    def test_reports_no_raw_smd_in_compiled_model_package(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "model.mdl").write_bytes(b"compiled model placeholder")
            report = build_smd_audit_report(root)
            self.assertEqual(report["summary"]["status"], "not_found")
            self.assertEqual(report["summary"]["smd_files"], 0)


    def test_collects_smd_from_gma(self):
        import binascii
        import struct
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            archive = root / "smd.gma"
            payload = make_smd()
            with archive.open("wb") as f:
                f.write(b"GMAD")
                f.write(bytes([3]))
                f.write(struct.pack("<QQ", 76561198000000000, 1700000000))
                for value in ("", "SMD Test Addon", "", "Test Author"):
                    f.write(value.encode("utf-8") + b"\0")
                f.write(struct.pack("<i", 1))
                f.write(struct.pack("<I", 1))
                f.write(b"models/test/body.smd\0")
                f.write(struct.pack("<qI", len(payload), binascii.crc32(payload) & 0xFFFFFFFF))
                f.write(struct.pack("<I", 0))
                f.write(payload)
                f.write(struct.pack("<I", 0))
            found, info = collect_smd_sources(archive)
            self.assertEqual([name for name, _ in found], ["models/test/body.smd"])
            self.assertEqual(info["container_type"], "gma")
            self.assertEqual(info["smd_count"], 1)

    def test_audit_compares_multiple_smd_files(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "00_base.smd").write_bytes(make_smd())
            (root / "01_slice.smd").write_bytes(make_smd(root_id=10, child_id=11, child_parent=10))
            report = build_smd_audit_report(root)
            self.assertEqual(report["summary"]["status"], "ok")
            self.assertEqual(report["summary"]["comparisons"], 1)
            self.assertEqual(report["comparisons"][0]["node_id_remap_count"], 2)

    def test_31_file_role_fixture_has_clean_structure_and_pose_policy(self):
        fixture = Path(__file__).resolve().parents[1] / "examples" / "smd_role_fixture"
        report = build_smd_audit_report(fixture)
        summary = report["summary"]
        self.assertEqual(summary["smd_files"], 31)
        self.assertEqual(summary["parsed_files"], 31)
        self.assertEqual(summary["parse_errors"], 0)
        self.assertEqual(summary["reference_smd"], "AES_T.smd")
        self.assertEqual(summary["smd_type_counts"], {
            "model_slice": 25,
            "physics_geometry": 1,
            "proportion_animation": 1,
            "corrective_animation": 1,
            "animation": 1,
            "ragdoll_pose": 1,
            "animation_reference": 1,
        })
        self.assertEqual(summary["pose_comparisons"], 24)
        self.assertEqual(summary["pose_comparisons_skipped"], 6)
        self.assertEqual(summary["status"], "ok")
        self.assertTrue(all(c["parent_mismatch_count"] == 0 for c in report["comparisons"]))

    def test_sparse_corrective_animation_does_not_raise_overall_status(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "AES_T.smd").write_bytes(make_smd())
            anim_dir = root / "anims"
            anim_dir.mkdir()
            nodes = '0 "root" -1\n1 "child bone" 0\n'
            sparse = ("version 1\nnodes\n" + nodes +
                      "end\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\n").encode()
            (anim_dir / "a_proportions_corrective_animation.smd").write_bytes(sparse)
            report = build_smd_audit_report(root)
            self.assertEqual(report["summary"]["status"], "ok")
            self.assertEqual(report["summary"]["warning_groups"], 0)
            self.assertEqual(report["summary"]["info_groups"], 2)  # sparse-frame INFO + skipped-pose INFO
            self.assertEqual(report["summary"]["sparse_frame_count"], 1)
            self.assertEqual(report["summary"]["sparse_animation_frame_count"], 1)
            candidate = next(f for f in report["files"] if "corrective" in f["source"])
            self.assertEqual(candidate["status"], "ok")
            self.assertEqual(candidate["summary"]["frame_coverage_status"], "sparse")
            self.assertEqual(candidate["frame_coverage"][0]["explicit_transform_count"], 1)
            self.assertEqual(candidate["frame_coverage"][0]["omitted_transform_count"], 1)
            self.assertEqual(len(candidate["infos"]), 1)

    def test_explicit_reference_overrides_automatic_selection(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "AES_BS.smd").write_bytes(make_smd())
            (root / "AES_T.smd").write_bytes(make_smd())
            report = build_smd_audit_report(root, reference_name="AES_BS.smd")
            self.assertEqual(report["summary"]["reference_smd"], "AES_BS.smd")
            self.assertEqual(report["summary"]["reference_selection_method"], "explicit")

    def test_auto_reference_prefers_aes_t_and_skips_animation_pose_warnings(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "AES_BS.smd").write_bytes(make_smd())
            (root / "AES_T.smd").write_bytes(make_smd())
            anim_dir = root / "aes_yz_pm_anims"
            anim_dir.mkdir()
            (anim_dir / "idle_all_01.smd").write_bytes(make_smd(child_x=9.0))
            report = build_smd_audit_report(root)
            self.assertEqual(report["summary"]["status"], "ok")
            self.assertEqual(report["summary"]["reference_smd"], "AES_T.smd")
            self.assertEqual(report["summary"]["reference_selection_method"], "preferred_base_slice_aes_t")
            animation_comparison = next(c for c in report["comparisons"] if "idle_all_01" in c["candidate"])
            self.assertEqual(animation_comparison["structure_status"], "ok")
            self.assertEqual(animation_comparison["reference_pose_comparison"], "skipped_incompatible_smd_type")
            self.assertEqual(animation_comparison["status"], "ok")


if __name__ == "__main__":
    unittest.main()
