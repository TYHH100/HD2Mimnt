import struct
import unittest

from hd2_material import (
    MATERIAL_HEADER_SIZE,
    MAX_MATERIAL_TEXTURES,
    parse_material_resource,
    parse_resource_id,
)
from hd2_resources import HD2ResourceError


def make_material(
    *,
    texture_types=None,
    texture_ids=None,
    variables=None,
    value_payload=b"",
    declared_value_size=None,
):
    texture_types = texture_types or []
    texture_ids = texture_ids or []
    variables = variables or []
    if len(texture_types) != len(texture_ids):
        raise ValueError("texture type and ID lists must have equal length")
    data = bytearray(MATERIAL_HEADER_SIZE)
    struct.pack_into("<I", data, 12, 0x12345678)
    struct.pack_into("<Q", data, 24, 0x1122334455667788)
    struct.pack_into("<I", data, 64, len(texture_types))
    struct.pack_into("<I", data, 104, len(variables))
    struct.pack_into(
        "<I", data, 120,
        len(value_payload) if declared_value_size is None else declared_value_size,
    )
    for value in texture_types:
        data.extend(struct.pack("<I", value))
    for value in texture_ids:
        data.extend(struct.pack("<Q", value))
    for klass, elements, variable_id, offset, stride in variables:
        data.extend(struct.pack("<5I", klass, elements, variable_id, offset, stride))
    data.extend(value_payload)
    return bytes(data)


class MaterialResourceParserTests(unittest.TestCase):
    def test_reads_common_header_texture_nodes_and_vector_preview(self):
        payload = struct.pack("<2f", 0.25, -0.5)
        material = make_material(
            texture_types=[0xAABBCCDD],
            texture_ids=[0x123456789ABCDEF0],
            variables=[(1, 1, 0x01020304, 0, 8)],
            value_payload=payload,
        )

        report = parse_material_resource(material)

        self.assertEqual(report["schema"], "SourceModelReader-HD2Material1")
        self.assertEqual(report["header"]["parent_material_id"], "1122334455667788")
        self.assertEqual(report["header"]["num_textures"], 1)
        self.assertEqual(report["header"]["num_variables"], 1)
        self.assertEqual(report["texture_nodes"][0]["texture_type_id"], 0xAABBCCDD)
        self.assertEqual(report["texture_nodes"][0]["texture_id"], "123456789abcdef0")
        variable = report["shader_variables"][0]
        self.assertEqual(variable["klass_name"], "Vector2")
        self.assertEqual(variable["variable_id_hex"], "0x01020304")
        self.assertEqual(variable["first_vector_preview"], [0.25, -0.5])
        self.assertEqual(variable["preview_status"], "first_vector_preview")
        self.assertEqual(report["render_mode"]["status"], "unresolved")
        self.assertEqual(report["warnings"], [])

    def test_preserves_unknown_header_and_raw_value_payload(self):
        material = bytearray(make_material(value_payload=b"\x00\x7f\x80\xff"))
        material[0:12] = bytes(range(12))
        material[32:64] = bytes(range(32))
        report = parse_material_resource(bytes(material))

        self.assertEqual(report["header"]["unknown_0_12_hex"], bytes(range(12)).hex())
        self.assertEqual(report["header"]["unknown_32_64_hex"], bytes(range(32)).hex())
        self.assertEqual(report["variable_value_payload_hex"], "007f80ff")
        self.assertEqual(report["layout"]["available_variable_data_size"], 4)

    def test_keeps_unsupported_variable_class_as_raw_descriptor(self):
        material = make_material(
            variables=[(12, 1, 0x55667788, 0, 16)],
            value_payload=bytes(range(16)),
        )
        report = parse_material_resource(material)
        variable = report["shader_variables"][0]

        self.assertEqual(variable["klass_name"], "Other")
        self.assertIsNone(variable["first_vector_preview"])
        self.assertEqual(variable["preview_status"], "unsupported_class")
        self.assertEqual(len(variable["raw_descriptor_hex"]), 40)

    def test_reports_out_of_range_shader_variable_preview_without_losing_payload(self):
        material = make_material(
            variables=[(3, 1, 0xAABBCCDD, 12, 16)],
            value_payload=struct.pack("<3f", 1.0, 2.0, 3.0),
        )
        report = parse_material_resource(material)

        self.assertEqual(
            report["shader_variables"][0]["preview_status"],
            "value_range_out_of_bounds",
        )
        self.assertIsNone(report["shader_variables"][0]["first_vector_preview"])
        self.assertEqual(len(report["variable_value_payload_hex"]), 24)
        self.assertTrue(any("outside the available variable payload" in item for item in report["warnings"]))

    def test_variable_data_size_mismatch_is_warning_and_payload_is_preserved(self):
        material = make_material(value_payload=b"\x01\x02\x03\x04", declared_value_size=12)
        report = parse_material_resource(material)

        self.assertFalse(report["layout"]["variable_data_size_matches_remaining_payload"])
        self.assertEqual(report["layout"]["available_variable_data_size"], 4)
        self.assertEqual(report["variable_value_payload_hex"], "01020304")
        self.assertTrue(any("VariableDataSize does not equal" in item for item in report["warnings"]))

    def test_rejects_truncated_fixed_header(self):
        with self.assertRaises(HD2ResourceError):
            parse_material_resource(b"\x00" * (MATERIAL_HEADER_SIZE - 1))

    def test_rejects_texture_count_beyond_safety_limit(self):
        material = bytearray(MATERIAL_HEADER_SIZE)
        struct.pack_into("<I", material, 64, MAX_MATERIAL_TEXTURES + 1)
        with self.assertRaises(HD2ResourceError):
            parse_material_resource(bytes(material))

    def test_rejects_truncated_texture_id_table(self):
        material = bytearray(MATERIAL_HEADER_SIZE)
        struct.pack_into("<I", material, 64, 1)
        with self.assertRaises(HD2ResourceError):
            parse_material_resource(bytes(material))

    def test_accepts_hex_and_decimal_resource_ids(self):
        self.assertEqual(parse_resource_id("0x10"), 16)
        self.assertEqual(parse_resource_id("ff"), 255)
        self.assertEqual(parse_resource_id("255"), 255)
        self.assertEqual(parse_resource_id(""), 0) if False else None
        with self.assertRaises(ValueError):
            parse_resource_id("")


if __name__ == "__main__":
    unittest.main(verbosity=2)
