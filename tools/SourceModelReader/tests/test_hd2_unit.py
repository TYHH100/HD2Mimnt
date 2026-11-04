import struct
import unittest

from hd2_unit import (
    HD2ResourceError,
    _decode_mesh_geometry,
    _build_transform_world_matrices,
    _matrix4_inverse,
    _matrix4_multiply,
    _validate_mesh_bone_info_matrices,
    decode_octahedral_normal,
    decode_packed_weights_1010102,
    parse_bone_info,
    parse_composite_unit,
    parse_mesh_info,
    parse_stream_info,
    parse_transform_info,
    resolve_unit_skinning,
)


class HD2UnitTableTests(unittest.TestCase):
    def test_transform_info_preserves_local_and_matrix_data(self):
        offset = 16
        data = bytearray(offset + 16 + 136)
        struct.pack_into("<I", data, offset, 1)
        struct.pack_into("<16f", data, offset + 16, *([1.0] * 16))
        struct.pack_into("<16f", data, offset + 80, *([2.0] * 16))
        struct.pack_into("<HHI", data, offset + 144, 3, 0xFFFF, 0x12345678)

        report = parse_transform_info(bytes(data), offset)

        self.assertEqual(report["count"], 1)
        self.assertEqual(report["transforms"][0]["name_hash"], 0x12345678)
        self.assertEqual(report["transforms"][0]["parent_index_raw"], 0xFFFF)
        self.assertEqual(report["transforms"][0]["matrix_4x4_raw"], [2.0] * 16)

    def test_bone_info_preserves_real_indices_and_material_remaps(self):
        offset = 16
        data = bytearray(offset + 8 + 100)
        struct.pack_into("<II", data, offset, 1, 8)
        record = offset + 8
        struct.pack_into("<4I", data, record, 1, 16, 80, 84)
        struct.pack_into("<16f", data, record + 16, *([1.0] * 16))
        struct.pack_into("<I", data, record + 80, 5)
        struct.pack_into("<3I", data, record + 84, 1, 12, 1)
        struct.pack_into("<I", data, record + 96, 0)

        report = parse_bone_info(bytes(data), offset)

        self.assertEqual(report["count"], 1)
        self.assertEqual(report["records"][0]["real_indices"], [5])
        self.assertEqual(report["records"][0]["remaps"], [[0]])

    def test_stream_info_and_gpu_geometry_decode(self):
        stream_offset = 16
        record = 32
        toc = bytearray(record + 448)
        struct.pack_into("<I", toc, stream_offset, 1)
        struct.pack_into("<I", toc, stream_offset + 4, 16)
        struct.pack_into("<I", toc, stream_offset + 8, 99)
        struct.pack_into("<I", toc, stream_offset + 12, 0)
        components = [(0, 2, 0, 0), (6, 28, 0, 0), (7, 35, 0, 0)]
        for index, row in enumerate(components):
            struct.pack_into("<3IQ", toc, record + 8 + index * 20, *row)
        struct.pack_into("<Q", toc, record + 328, len(components))
        stream_fields = (1, 0, 3, 24, 0, 0, 2, 0, 3, 0, 0, 0, 0, 72, 72, 6)
        struct.pack_into("<QQIIQQQQIIQQIIII", toc, record + 336, *stream_fields)
        streams = parse_stream_info(bytes(toc), stream_offset)
        self.assertEqual(streams["count"], 1)
        self.assertEqual(streams["records"][0]["vertex_stride"], 24)

        mesh_offset = 16
        mesh_record = mesh_offset + 12
        mesh_toc = bytearray(mesh_record + 128 + 4 + 24)
        struct.pack_into("<I", mesh_toc, mesh_offset, 1)
        struct.pack_into("<II", mesh_toc, mesh_offset + 4, 12, 0)
        struct.pack_into("<4I", mesh_toc, mesh_record + 40, 0xABC, 0, 0, 0)
        struct.pack_into("<iI", mesh_toc, mesh_record + 56, 0, 0)
        struct.pack_into("<I", mesh_toc, mesh_record + 104, 1)
        struct.pack_into("<I", mesh_toc, mesh_record + 108, 128)
        struct.pack_into("<I", mesh_toc, mesh_record + 120, 1)
        struct.pack_into("<I", mesh_toc, mesh_record + 124, 132)
        struct.pack_into("<I", mesh_toc, mesh_record + 128, 0)
        struct.pack_into("<6I", mesh_toc, mesh_record + 132, 0, 0, 3, 0, 3, 0)
        meshes = parse_mesh_info(bytes(mesh_toc), mesh_offset)

        gpu = bytearray(78)
        for vertex in range(3):
            struct.pack_into(
                "<3f4B4e", gpu, vertex * 24,
                float(vertex), 0.0, 0.0, 0, 1, 2, 3, 1.0, 0.0, 0.0, 0.0,
            )
        struct.pack_into("<3H", gpu, 72, 0, 1, 2)
        decoded, warnings = _decode_mesh_geometry(meshes["records"][0], streams, bytes(gpu))

        self.assertEqual(decoded["vertex_count"], 3)
        self.assertEqual(decoded["sections"][0]["indices"], [0, 1, 2])
        self.assertEqual(decoded["attributes"][0]["values"][2], [2.0, 0.0, 0.0])
        self.assertEqual(warnings, [])

    def test_composite_unit_maps_mesh_ids_to_shared_streams(self):
        unit_id = 0x123456789ABCDEF0
        stream_offset = 112
        data = bytearray(stream_offset + 16 + 448)
        struct.pack_into("<QII", data, 0, 0, 1, stream_offset)
        struct.pack_into("<QQ", data, 16, 0x77, unit_id)
        struct.pack_into("<I", data, 32, 36)
        struct.pack_into("<I", data, 36, 1)
        struct.pack_into("<II", data, 40, 0xABC, 12)
        item = 48
        struct.pack_into("<I", data, item, 0)
        struct.pack_into("<I", data, item + 24, 1)
        struct.pack_into("<I", data, item + 28, 48)
        struct.pack_into("<I", data, item + 40, 1)
        struct.pack_into("<I", data, item + 44, 52)
        struct.pack_into("<I", data, item + 48, 0x55)
        struct.pack_into("<6I", data, item + 52, 0, 0, 3, 0, 3, 0)
        struct.pack_into("<IIII", data, stream_offset, 1, 16, 99, 0)
        stream_record = stream_offset + 16
        for index, row in enumerate([(0, 2, 0, 0), (6, 28, 0, 0), (7, 35, 0, 0)]):
            struct.pack_into("<3IQ", data, stream_record + 8 + index * 20, *row)
        struct.pack_into("<Q", data, stream_record + 328, 3)
        fields = (1, 0, 3, 24, 0, 0, 2, 0, 3, 0, 0, 0, 0, 72, 72, 6)
        struct.pack_into("<QQIIQQQQIIQQIIII", data, stream_record + 336, *fields)

        report = parse_composite_unit(bytes(data), bytes(78), unit_id)

        self.assertEqual(report["target_unit_index"], 0)
        self.assertEqual(report["mesh_info"][0xABC]["stream_index"], 0)
        self.assertEqual(report["mesh_info"][0xABC]["sections"][0]["num_vertices"], 3)
        self.assertEqual(report["streams"]["count"], 1)

    def test_rejects_out_of_range_transform_info(self):
        with self.assertRaises(HD2ResourceError):
            parse_transform_info(b"\0" * 24, 16)


class PackedVertexFormatTests(unittest.TestCase):
    @staticmethod
    def pack_xy10(x, y, upper=0):
        return (x & 0x3FF) | ((y & 0x3FF) << 10) | (upper << 20)

    def test_format_30_decodes_octahedral_cardinal_directions(self):
        vectors = {
            "positive_z": decode_octahedral_normal(self.pack_xy10(512, 512)),
            "positive_x": decode_octahedral_normal(self.pack_xy10(1023, 512)),
            "positive_y": decode_octahedral_normal(self.pack_xy10(512, 1023)),
            "negative_z": decode_octahedral_normal(self.pack_xy10(1023, 1023)),
        }
        self.assertGreater(vectors["positive_z"][2], 0.999)
        self.assertGreater(vectors["positive_x"][0], 0.999)
        self.assertGreater(vectors["positive_y"][1], 0.999)
        self.assertLess(vectors["negative_z"][2], -0.999)
        for vector in vectors.values():
            self.assertAlmostEqual(sum(value * value for value in vector), 1.0, places=6)

    def test_format_30_ignores_upper_twelve_bits_and_validates_uint32(self):
        packed = self.pack_xy10(700, 300)
        self.assertEqual(
            decode_octahedral_normal(packed),
            decode_octahedral_normal(packed | 0xFFF00000),
        )
        with self.assertRaises(ValueError):
            decode_octahedral_normal(-1)
        with self.assertRaises(ValueError):
            decode_octahedral_normal(0x1_0000_0000)

    def test_format_29_decodes_three_normalized_weights_and_zero_fourth(self):
        packed = 1023 | (512 << 10) | (256 << 20) | (3 << 30)
        weights = decode_packed_weights_1010102(packed)
        self.assertEqual(weights[0], 1.0)
        self.assertAlmostEqual(weights[1], 512 / 1023)
        self.assertAlmostEqual(weights[2], 256 / 1023)
        self.assertEqual(weights[3], 0.0)
        self.assertAlmostEqual(sum(weights), 1791 / 1023)

    def test_geometry_decode_preserves_raw_words_and_adds_semantic_values(self):
        packed_normal = self.pack_xy10(512, 512, upper=0xABC)
        packed_weights = 1023 | (512 << 10) | (256 << 20) | (3 << 30)
        gpu = bytearray()
        for _ in range(3):
            gpu.extend(struct.pack("<II", packed_normal, packed_weights))
        gpu.extend(struct.pack("<3H", 0, 1, 2))

        stream = {
            "components": [
                {
                    "type": 1, "type_name": "normal", "index": 0,
                    "format": 30, "format_name": "packed_octahedral_normal",
                    "byte_size": 4, "byte_offset": 0,
                },
                {
                    "type": 7, "type_name": "bone_weight", "index": 0,
                    "format": 29, "format_name": "packed_10_10_10_2",
                    "byte_size": 4, "byte_offset": 4,
                },
            ],
            "num_vertices": 3, "vertex_stride": 8,
            "vertex_buffer_offset": 0, "vertex_buffer_size": 24,
            "num_indices": 3, "index_buffer_type": 0,
            "index_buffer_offset": 24, "index_buffer_size": 6,
        }
        mesh = {
            "mesh_id": 0x12345678, "index": 0, "transform_index": 0,
            "lod_index": 0, "stream_index": 0,
            "sections": [{
                "index": 0, "vertex_offset": 0, "num_vertices": 3,
                "index_offset": 0, "num_indices": 3,
            }],
        }

        decoded, warnings = _decode_mesh_geometry(mesh, {"records": [stream]}, bytes(gpu))
        normal_attr, weight_attr = decoded["attributes"]

        self.assertEqual(normal_attr["values"], [packed_normal] * 3)
        self.assertEqual(normal_attr["decoded_semantics"], "octahedral_normal")
        self.assertEqual(len(normal_attr["decoded_values"]), 3)
        self.assertGreater(normal_attr["decoded_values"][0][2], 0.999)
        self.assertEqual(weight_attr["values"], [packed_weights] * 3)
        self.assertEqual(weight_attr["decoded_semantics"], "unorm10_10_10_2_bone_weights")
        self.assertEqual(weight_attr["decoded_values"][0][3], 0.0)
        self.assertEqual(warnings, [])

    def test_format_29_is_not_assumed_to_be_weights_for_other_components(self):
        packed = 1023 | (512 << 10) | (256 << 20) | (3 << 30)
        gpu = struct.pack("<3I3H", packed, packed, packed, 0, 1, 2)
        stream = {
            "components": [{
                "type": 2, "type_name": "tangent", "index": 0,
                "format": 29, "format_name": "packed_10_10_10_2",
                "byte_size": 4, "byte_offset": 0,
            }],
            "num_vertices": 3, "vertex_stride": 4,
            "vertex_buffer_offset": 0, "vertex_buffer_size": 12,
            "num_indices": 3, "index_buffer_type": 0,
            "index_buffer_offset": 12, "index_buffer_size": 6,
        }
        mesh = {
            "mesh_id": 0x87654321, "index": 0, "transform_index": 0,
            "lod_index": 0, "stream_index": 0,
            "sections": [{
                "index": 0, "vertex_offset": 0, "num_vertices": 3,
                "index_offset": 0, "num_indices": 3,
            }],
        }
        decoded, warnings = _decode_mesh_geometry(mesh, {"records": [stream]}, gpu)
        attr = decoded["attributes"][0]
        self.assertEqual(attr["values"], [packed] * 3)
        self.assertNotIn("decoded_values", attr)
        self.assertEqual(len(warnings), 1)
        self.assertIn("only bone_weight semantics are decoded", warnings[0])


class UnitSkinningResolverTests(unittest.TestCase):
    @staticmethod
    def fixture(index_values=None, lod_index=0):
        transforms = [
            {
                "index": 0, "name_hash": 100, "parent_index_raw": 0,
                "increment": 0, "matrix_4x4_raw": [1.0] * 16,
            },
            {
                "index": 1, "name_hash": 200, "parent_index_raw": 0,
                "increment": 1, "matrix_4x4_raw": [2.0] * 16,
            },
            {
                "index": 2, "name_hash": 300, "parent_index_raw": 1,
                "increment": 1, "matrix_4x4_raw": [3.0] * 16,
            },
        ]
        record = {
            "index": 0, "num_bones": 2,
            "real_indices": [1, 0],
            "matrices": [[10.0] * 16, [20.0] * 16],
            "remaps": [[1, 0], [0, 1]],
        }
        if index_values is None:
            index_values = [[0, 1, 0, 0], [1, 0, 0, 0]]
        geometry = [{
            "mesh_id": 0x1234, "mesh_info_index": 0, "lod_index": lod_index,
            "attributes": [
                {
                    "component_type": 7, "component_index": 0, "format": 35,
                    "format_name": "vec4_half",
                    "values": [[0.75, 0.25, 0.0, 0.0], [1.0, 0.0, 0.0, 0.0]],
                },
                {
                    "component_type": 6, "component_index": 0, "format": 28,
                    "values": index_values,
                },
                {
                    "component_type": 6, "component_index": 1, "format": 28,
                    "values": [[1, 0, 0, 0], [0, 1, 0, 0]],
                },
            ],
            "sections": [
                {"index": 0, "material_index": 0, "material_slot_id": 11, "indices": [0]},
                {"index": 1, "material_index": 1, "material_slot_id": 22, "indices": [0]},
            ],
        }]
        return (
            {"transforms": transforms},
            {"records": [record]},
            {"bones": [{"hash": 100, "name": "root"}, {"hash": 200, "name": "child"}]},
            {"records": [{"index": 0}]},
            geometry,
        )

    def test_resolves_material_remap_to_named_vertex_influences(self):
        transform_info, bone_info, bones, mesh_info, geometry = self.fixture()
        warnings = resolve_unit_skinning(transform_info, bone_info, bones, mesh_info, geometry)

        self.assertEqual(warnings, [])
        self.assertEqual(
            [item["name"] for item in transform_info["resolved_skeleton"]],
            ["root", "child", None],
        )
        self.assertEqual(transform_info["resolved_skeleton"][1]["parent_name"], "root")
        palette = bone_info["records"][0]["resolved_real_indices"]
        self.assertEqual([item["name"] for item in palette], ["child", "root"])
        self.assertEqual(palette[0]["bone_info_matrix_raw"], [10.0] * 16)
        self.assertEqual(palette[1]["bone_info_matrix_raw"], [20.0] * 16)

        skinning = geometry[0]["skinning"]
        self.assertEqual(skinning["status"], "ok")
        section0 = skinning["section_skinning"][0]
        first_set = section0["bone_index_sets"][0]["vertex_influences"][0]["influences"]
        self.assertEqual(
            [(item["bone_name"], item["weight"]) for item in first_set],
            [("root", 0.75), ("child", 0.25)],
        )
        second_set = section0["bone_index_sets"][1]["vertex_influences"][0]["influences"]
        self.assertEqual(
            [(item["bone_name"], item["weight"]) for item in second_set],
            [("child", 0.75), ("root", 0.25)],
        )

        # The same local vertex indices resolve differently for material slot 1.
        section1 = skinning["section_skinning"][1]
        material1 = section1["bone_index_sets"][0]["vertex_influences"][0]["influences"]
        self.assertEqual(
            [(item["bone_name"], item["weight"]) for item in material1],
            [("child", 0.75), ("root", 0.25)],
        )

    def test_unresolved_active_lane_marks_partial_without_rejecting_zero_weight_lanes(self):
        transform_info, bone_info, bones, mesh_info, geometry = self.fixture(
            index_values=[[99, 1, 99, 99], [1, 0, 0, 0]]
        )
        warnings = resolve_unit_skinning(transform_info, bone_info, bones, mesh_info, geometry)
        skinning = geometry[0]["skinning"]
        self.assertEqual(skinning["status"], "partial")
        self.assertGreater(skinning["unresolved_active_influence_count"], 0)
        self.assertTrue(any("unresolved active bone influences" in item for item in warnings))
        # Lane 0 is active and unresolved; lanes 2 and 3 have zero weights and
        # therefore do not inflate the unresolved active influence count.
        self.assertEqual(
            skinning["section_skinning"][0]["bone_index_sets"][0]["unresolved_active_influence_count"],
            1,
        )

    def test_lod_minus_one_uses_last_bone_info_record(self):
        transform_info, bone_info, bones, mesh_info, geometry = self.fixture(lod_index=-1)
        bone_info["records"].append({
            "index": 1, "num_bones": 1, "real_indices": [2],
            "matrices": [[30.0] * 16], "remaps": [[0], [0]],
        })
        # Change the index attributes to use local index zero and make the
        # selected final palette resolve to TransformInfo index 2 (unnamed).
        for attr in geometry[0]["attributes"]:
            if attr["component_type"] == 6:
                attr["values"] = [[0, 0, 0, 0], [0, 0, 0, 0]]
        warnings = resolve_unit_skinning(transform_info, bone_info, bones, mesh_info, geometry)
        self.assertEqual(geometry[0]["skinning"]["bone_info_index"], 1)
        self.assertEqual(geometry[0]["skinning"]["palette"][0]["transform_index"], 2)
        self.assertEqual(geometry[0]["skinning"]["palette"][0]["name"], None)
        self.assertEqual(warnings, [])



class MatrixValidationTests(unittest.TestCase):
    @staticmethod
    def transform(rotation, position, parent, matrix):
        return {
            "rotation_3x3": rotation,
            "position": position,
            "scale": [1.0, 1.0, 1.0],
            "parent_index_raw": parent,
            "matrix_4x4_raw": matrix,
        }

    def test_rebuilds_row_vector_world_matrix_from_local_times_parent(self):
        rotation_z_90 = [
            0.0, 1.0, 0.0,
            -1.0, 0.0, 0.0,
            0.0, 0.0, 1.0,
        ]
        identity_rotation = [
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 1.0,
        ]
        root_world = [
            0.0, 1.0, 0.0, 0.0,
            -1.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            10.0, 0.0, 0.0, 1.0,
        ]
        child_world = [
            0.0, 1.0, 0.0, 0.0,
            -1.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            10.0, 1.0, 0.0, 1.0,
        ]
        transform_info = {
            "transforms": [
                self.transform(rotation_z_90, [10.0, 0.0, 0.0], 0xFFFF, root_world),
                self.transform(identity_rotation, [1.0, 0.0, 0.0], 0, child_world),
            ]
        }

        world, report = _build_transform_world_matrices(transform_info)

        self.assertEqual(report["status"], "matches")
        self.assertEqual(report["matched_transform_count"], 2)
        self.assertEqual(report["mismatched_transform_count"], 0)
        self.assertEqual(report["entries"][1]["status"], "matches")
        self.assertEqual(world[1], child_world)

    def test_detects_parent_cycles_without_recursing(self):
        identity_rotation = [
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 1.0,
        ]
        identity = [
            1.0, 0.0, 0.0, 0.0,
            0.0, 1.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            0.0, 0.0, 0.0, 1.0,
        ]
        transform_info = {
            "transforms": [
                self.transform(identity_rotation, [0.0, 0.0, 0.0], 1, identity),
                self.transform(identity_rotation, [0.0, 0.0, 0.0], 0, identity),
            ]
        }

        world, report = _build_transform_world_matrices(transform_info)

        self.assertEqual(world, [None, None])
        self.assertEqual(report["status"], "partial")
        self.assertEqual(report["unresolved_transform_count"], 2)
        self.assertTrue(all(item["status"] == "parent_cycle" for item in report["entries"]))

    def test_checks_only_actively_used_palette_slots_against_mesh_space_inverse(self):
        rotation_z_90 = [
            0.0, 1.0, 0.0,
            -1.0, 0.0, 0.0,
            0.0, 0.0, 1.0,
        ]
        identity_rotation = [
            1.0, 0.0, 0.0,
            0.0, 1.0, 0.0,
            0.0, 0.0, 1.0,
        ]
        root = self.transform(rotation_z_90, [10.0, 0.0, 0.0], 0xFFFF, [
            0.0, 1.0, 0.0, 0.0,
            -1.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            10.0, 0.0, 0.0, 1.0,
        ])
        child = self.transform(identity_rotation, [1.0, 0.0, 0.0], 0, [
            0.0, 1.0, 0.0, 0.0,
            -1.0, 0.0, 0.0, 0.0,
            0.0, 0.0, 1.0, 0.0,
            10.0, 1.0, 0.0, 1.0,
        ])
        world, hierarchy = _build_transform_world_matrices({"transforms": [root, child]})
        self.assertEqual(hierarchy["status"], "matches")

        expected = _matrix4_multiply(world[0], _matrix4_inverse(world[1]))
        record = {
            "real_indices": [1, 0],
            "matrices": [expected, [99.0] * 16],
        }
        result = _validate_mesh_bone_info_matrices(
            {"transform_index": 0},
            record,
            {0: 1},
            world,
        )

        self.assertEqual(result["status"], "matches")
        self.assertEqual(result["active_palette_slot_count"], 1)
        self.assertEqual(result["matched_active_palette_slot_count"], 1)
        self.assertEqual(result["unreferenced_palette_slot_count"], 1)
        self.assertLess(result["max_abs_error"], 1.0e-9)

        bad_record = {
            "real_indices": [1, 0],
            "matrices": [[99.0] * 16, [88.0] * 16],
        }
        mismatch = _validate_mesh_bone_info_matrices(
            {"transform_index": 0},
            bad_record,
            {0: 1},
            world,
        )
        self.assertEqual(mismatch["status"], "mismatch")
        self.assertEqual(mismatch["mismatched_active_palette_slot_count"], 1)
        self.assertEqual(mismatch["unreferenced_palette_slot_count"], 1)


if __name__ == "__main__":
    unittest.main(verbosity=2)
