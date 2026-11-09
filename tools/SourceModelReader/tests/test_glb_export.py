import json
import struct
import tempfile
import unittest
from pathlib import Path

from source_model_reader import (
    ModelScene,
    SceneBone,
    ScenePrimitive,
    SceneVertex,
    _source_to_gltf_vec3,
    validate_glb_file,
    write_glb_file,
)


class CoordinateConversionTests(unittest.TestCase):
    def test_source_z_up_to_gltf_y_up(self):
        self.assertEqual(_source_to_gltf_vec3((1.0, 2.0, 3.0)), (1.0, 3.0, -2.0))


class GLBExportTests(unittest.TestCase):
    def make_scene(self):
        identity_column_major = (
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0,
        )
        bone = SceneBone(
            index=0,
            name="root",
            parent=-1,
            translation=(0.0, 0.0, 0.0),
            rotation=(0.0, 0.0, 0.0, 1.0),
            inverse_bind_matrix=identity_column_major,
        )
        vertices = [
            SceneVertex((0.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, 0.0), (0, 0, 0, 0), (1.0, 0.0, 0.0, 0.0)),
            SceneVertex((1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (1.0, 0.0), (0, 0, 0, 0), (1.0, 0.0, 0.0, 0.0)),
            SceneVertex((0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.0, 1.0), (0, 0, 0, 0), (1.0, 0.0, 0.0, 0.0)),
        ]
        primitive = ScenePrimitive("test/material", [0, 1, 2])
        return ModelScene(
            name="fixture",
            bones=[bone],
            vertices=vertices,
            primitives=[primitive],
            source_vertex_indices=[10, 11, 12],
            bodygroups=[{"bodygroup": 0, "choice": 0}],
            source_model="fixture.mdl",
            source_archive=None,
            lod=0,
            skin=0,
            warnings=[],
        )

    def test_writes_valid_glb_container_and_skin(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "fixture.glb"
            report = write_glb_file(self.make_scene(), output)
            raw = output.read_bytes()
            magic, version, total_length = struct.unpack_from("<4sII", raw, 0)
            self.assertEqual(magic, b"glTF")
            self.assertEqual(version, 2)
            self.assertEqual(total_length, len(raw))

            json_length, json_type = struct.unpack_from("<I4s", raw, 12)
            self.assertEqual(json_type, b"JSON")
            gltf = json.loads(raw[20:20 + json_length].decode("utf-8"))
            bin_header = 20 + json_length
            bin_length, bin_type = struct.unpack_from("<I4s", raw, bin_header)
            self.assertEqual(bin_type, b"BIN\0")
            self.assertEqual(bin_header + 8 + bin_length, len(raw))
            self.assertEqual(gltf["asset"]["version"], "2.0")
            self.assertEqual(len(gltf["skins"][0]["joints"]), 1)
            self.assertEqual(gltf["accessors"][gltf["skins"][0]["inverseBindMatrices"]]["count"], 1)
            self.assertEqual(len(gltf["meshes"][0]["primitives"]), 1)
            self.assertEqual(report["unified_vertex_count"], 3)
            self.assertEqual(report["triangle_count"], 1)
            self.assertEqual(report["primitive_count"], 1)
            validation = validate_glb_file(output)
            self.assertEqual(validation["summary"]["status"], "ok")
            self.assertEqual(validation["summary"]["triangles"], 1)
            self.assertEqual(validation["summary"]["skin_joints"], 1)

    def test_validator_rejects_non_glb_file(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "bad.glb"
            output.write_bytes(b"not a glb file")
            with self.assertRaisesRegex(Exception, "too small|magic"):
                validate_glb_file(output)

    def test_validator_detects_bad_skin_weight_sum(self):
        with tempfile.TemporaryDirectory() as temp:
            output = Path(temp) / "fixture.glb"
            write_glb_file(self.make_scene(), output)
            raw = bytearray(output.read_bytes())
            json_length, _ = struct.unpack_from("<I4s", raw, 12)
            gltf = json.loads(raw[20:20 + json_length].decode("utf-8"))
            weight_accessor_index = gltf["meshes"][0]["primitives"][0]["attributes"]["WEIGHTS_0"]
            accessor = gltf["accessors"][weight_accessor_index]
            view = gltf["bufferViews"][accessor["bufferView"]]
            bin_chunk_header = 20 + json_length
            bin_length, _ = struct.unpack_from("<I4s", raw, bin_chunk_header)
            first_weight_offset = bin_chunk_header + 8 + int(view.get("byteOffset", 0)) + int(accessor.get("byteOffset", 0))
            struct.pack_into("<f", raw, first_weight_offset, 0.0)
            output.write_bytes(raw)
            validation = validate_glb_file(output)
            self.assertEqual(validation["summary"]["status"], "warning")
            self.assertEqual(validation["summary"]["bad_weight_sums"], 1)


if __name__ == "__main__":
    unittest.main()
