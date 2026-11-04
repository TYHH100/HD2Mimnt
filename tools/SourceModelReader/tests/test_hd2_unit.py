import struct
import unittest

from hd2_unit import (
    HD2ResourceError,
    _decode_mesh_geometry,
    parse_bone_info,
    parse_composite_unit,
    parse_mesh_info,
    parse_stream_info,
    parse_transform_info,
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


if __name__ == "__main__":
    unittest.main(verbosity=2)
