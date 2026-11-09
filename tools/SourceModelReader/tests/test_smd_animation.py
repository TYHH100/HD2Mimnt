import json
import math
import struct
import tempfile
import unittest
from pathlib import Path

from smd_reader import parse_smd_bytes
from smd_animation import build_animation_clip, smd_euler_xyz_to_gltf_quaternion
from source_model_reader import (
    ModelScene, SceneBone, ScenePrimitive, SceneVertex,
    validate_glb_file, write_glb_file,
)


def make_animation_smd() -> bytes:
    return b'''version 1
nodes
0 "root" -1
1 "arm" 0
2 "hand" 1
end
skeleton
time 0
0 0 0 0 0 0 0
1 1 0 0 0 0 0
2 2 0 0 0 0 0
time 2
0 0 0 0 0 0 0
1 1 0 0 0 0 1.57079632679
2 2.5 0 0 0 0 0
time 4
0 0 0 0 0 0 0
1 1 0 0 0 0 3.14159265359
end
'''


def make_scene() -> ModelScene:
    identity = (1.0, 0.0, 0.0, 0.0,
                0.0, 1.0, 0.0, 0.0,
                0.0, 0.0, 1.0, 0.0,
                0.0, 0.0, 0.0, 1.0)
    bones = [
        SceneBone(0, "root", -1, (0.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), identity),
        SceneBone(1, "arm", 0, (1.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), identity),
        SceneBone(2, "hand", 1, (2.0, 0.0, 0.0), (0.0, 0.0, 0.0, 1.0), identity),
    ]
    vertices = [
        SceneVertex((0.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0), (0, 0, 0, 0), (1.0, 0.0, 0.0, 0.0)),
        SceneVertex((1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (1.0, 0.0), (0, 0, 0, 0), (1.0, 0.0, 0.0, 0.0)),
        SceneVertex((0.0, 0.0, 1.0), (0.0, 1.0, 0.0), (0.0, 1.0), (0, 0, 0, 0), (1.0, 0.0, 0.0, 0.0)),
    ]
    return ModelScene("Fixture", bones, vertices, [ScenePrimitive("mat", [0, 1, 2])],
                      [0, 1, 2], [], "fixture.mdl", None, 0, 0, [])


class SMDAnimationTests(unittest.TestCase):
    def test_included_sample_model_animation_fixture(self):
        fixture = Path(__file__).resolve().parents[1] / "examples" / "animation_fixture" / "synthetic_arm_pose.smd"
        if not fixture.is_file():
            self.skipTest("Optional 255-bone sample fixture is not included in the repository")
        document = parse_smd_bytes(fixture.read_bytes(), str(fixture))
        self.assertEqual(len(document.nodes), 255)
        self.assertEqual(sorted(document.frames), [0, 30])
        self.assertEqual(document.smd_type, "animation")
        self.assertEqual(len(document.frames[0]), 255)
        self.assertEqual(len(document.frames[30]), 255)

    def test_euler_identity_and_normalization(self):
        q = smd_euler_xyz_to_gltf_quaternion((0.0, 0.0, 0.0))
        self.assertAlmostEqual(q[0], 0.0, places=7)
        self.assertAlmostEqual(q[1], 0.0, places=7)
        self.assertAlmostEqual(q[2], 0.0, places=7)
        self.assertAlmostEqual(abs(q[3]), 1.0, places=7)
        q2 = smd_euler_xyz_to_gltf_quaternion((0.2, -0.4, 1.1))
        self.assertAlmostEqual(math.sqrt(sum(x * x for x in q2)), 1.0, places=7)

    def test_builds_dense_and_sparse_tracks_without_fabricating_keys(self):
        document = parse_smd_bytes(make_animation_smd(), "anims/walk.smd")
        clip = build_animation_clip(document, make_scene().bones, 30.0, "auto")
        by_name = {track["bone_name"]: track for track in clip["tracks"]}
        self.assertEqual(clip["source_frame_indices"], [0, 2, 4])
        self.assertAlmostEqual(clip["duration_seconds"], 4 / 30)
        self.assertEqual(by_name["arm"]["interpolation"], "LINEAR")
        self.assertEqual(by_name["hand"]["interpolation"], "STEP")
        self.assertEqual(by_name["hand"]["frame_indices"], [0, 2])
        self.assertEqual(by_name["hand"]["explicit_key_count"], 2)
        self.assertEqual(clip["static_tracks_elided_count"], 1)

    def test_exports_and_validates_animated_glb(self):
        document = parse_smd_bytes(make_animation_smd(), "anims/walk.smd")
        scene = make_scene()
        clip = build_animation_clip(document, scene.bones, 30.0, "auto")
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "walk.glb"
            report = write_glb_file(scene, output, clip)
            self.assertEqual(report["animation"]["animated_bone_count"], 2)
            self.assertEqual(report["animation"]["static_tracks_elided_count"], 1)
            validated = validate_glb_file(output)
            self.assertEqual(validated["summary"]["status"], "ok")
            self.assertEqual(validated["summary"]["animations"], 1)
            self.assertEqual(validated["summary"]["animation_channels"], 4)
            self.assertEqual(validated["summary"]["animated_nodes"], 2)

    def test_validator_rejects_duplicate_animation_key_times(self):
        document = parse_smd_bytes(make_animation_smd(), "anims/walk.smd")
        scene = make_scene()
        clip = build_animation_clip(document, scene.bones, 30.0, "auto")
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "walk.glb"
            write_glb_file(scene, output, clip)
            raw = bytearray(output.read_bytes())
            cursor = 12
            json_length, json_type = struct.unpack_from("<I4s", raw, cursor)
            self.assertEqual(json_type, b"JSON")
            cursor += 8
            gltf = json.loads(raw[cursor:cursor + json_length].decode("utf-8").rstrip(" "))
            bin_header = cursor + json_length
            bin_length, bin_type = struct.unpack_from("<I4s", raw, bin_header)
            self.assertEqual(bin_type, b"BIN\0")
            binary_start = bin_header + 8
            sampler = gltf["animations"][0]["samplers"][0]
            accessor = gltf["accessors"][sampler["input"]]
            view = gltf["bufferViews"][accessor["bufferView"]]
            start = binary_start + int(view.get("byteOffset", 0)) + int(accessor.get("byteOffset", 0))
            first = struct.unpack_from("<f", raw, start)[0]
            struct.pack_into("<f", raw, start + 4, first)
            broken = Path(temp) / "walk-bad-time.glb"
            broken.write_bytes(raw)
            report = validate_glb_file(broken)
            self.assertEqual(report["summary"]["status"], "error")
            self.assertTrue(any("not strictly increasing" in item["message"] for item in report["findings"]))

    def test_rejects_non_animation_smd(self):
        raw = make_animation_smd().replace(b"anims/walk", b"body/walk")
        # A multi-frame file outside an animation path is still conservatively
        # classified as animation by frame count.
        doc = parse_smd_bytes(raw, "body/walk.smd")
        clip = build_animation_clip(doc, make_scene().bones, 24.0)
        self.assertGreater(clip["duration_seconds"], 0.0)
        one_frame = b'version 1\nnodes\n0 "root" -1\nend\nskeleton\ntime 0\n0 0 0 0 0 0 0\nend\n'
        pose = parse_smd_bytes(one_frame, "body/pose.smd")
        with self.assertRaises(ValueError):
            build_animation_clip(pose, make_scene().bones, 24.0)

    def test_rejects_hierarchy_mismatch(self):
        document = parse_smd_bytes(make_animation_smd(), "anims/walk.smd")
        bones = make_scene().bones
        bones[2].parent = 0
        with self.assertRaisesRegex(ValueError, "hierarchy differs"):
            build_animation_clip(document, bones, 30.0)


if __name__ == "__main__":
    unittest.main()
