import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from source_model_reader import (  # noqa: E402
    MDL_HEADER_BYTES,
    MDL_PROVEN_VERSION,
    SUPPORTED_MDL_VERSIONS,
    mdl_version_error,
    validate_mdl_header_layout,
)

SIZE = 4096


def build_header(version_name: str = "shoubi/AES_YZ_SB.mdl",
                 data_length: int = SIZE,
                 num_bones: int = 45, bone_index: int = 628,
                 num_textures: int = 3, texture_index: int = 3200,
                 num_cdtextures: int = 1, cdtexture_index: int = 3400,
                 num_skinrefs: int = 3, num_skin_families: int = 1,
                 skin_family_index: int = 3410,
                 num_bodyparts: int = 4, bodypart_index: int = 3000,
                 size: int = SIZE) -> bytes:
    """A header laid out exactly like the proven v48 field offsets."""
    raw = bytearray(size)
    raw[0:4] = b"IDST"
    name = version_name.encode("utf-8")
    raw[12:12 + len(name)] = name
    spots = {
        76: min(data_length, size) if data_length == SIZE else data_length,
        156: num_bones, 160: bone_index, 204: num_textures,
        208: texture_index, 212: num_cdtextures, 216: cdtexture_index,
        220: num_skinrefs, 224: num_skin_families, 228: skin_family_index,
        232: num_bodyparts, 236: bodypart_index,
    }
    for offset, value in spots.items():
        raw[offset:offset + 4] = int(value).to_bytes(4, "little", signed=True)
    return bytes(raw)


class MDLVersionGateTests(unittest.TestCase):
    def test_proven_and_structurally_supported_versions_pass(self):
        self.assertIsNone(mdl_version_error(MDL_PROVEN_VERSION))
        self.assertIsNone(mdl_version_error(49))
        self.assertEqual(SUPPORTED_MDL_VERSIONS, (48, 49))

    def test_other_versions_are_rejected_with_the_number(self):
        for version in (47, 50, 0, -1):
            message = mdl_version_error(version)
            self.assertIsNotNone(message)
            self.assertIn(f"found v{version}", message)


class MDLHeaderLayoutValidationTests(unittest.TestCase):
    def test_a_header_matching_the_proven_offsets_is_accepted(self):
        self.assertEqual(validate_mdl_header_layout(build_header()), [])

    def test_the_shipped_v49_field_values_validate(self):
        """The accepted values are copied from the real v49 member, not invented."""
        raw = build_header(data_length=23416, texture_index=15600,
                          cdtexture_index=15792, skin_family_index=15796,
                          bodypart_index=13500, size=23416)
        self.assertEqual(validate_mdl_header_layout(raw), [])

    def test_truncated_header_reports_one_problem(self):
        problems = validate_mdl_header_layout(b"IDST" + bytes(40))
        self.assertEqual(len(problems), 1)
        self.assertIn("needs", problems[0])

    def test_unprintable_or_missing_name_is_rejected(self):
        self.assertTrue(any("name" in problem
                            for problem in validate_mdl_header_layout(build_header(""))))
        self.assertTrue(any("name" in problem
                            for problem in validate_mdl_header_layout(
                                build_header("模型/坏名字.mdl"))))

    def test_bad_data_length_is_rejected(self):
        for length in (0, 200, SIZE + 1, -5):
            problems = validate_mdl_header_layout(build_header(data_length=length))
            self.assertTrue(any("dataLength" in problem for problem in problems), length)

    def test_out_of_range_counts_and_pointers_are_rejected(self):
        cases = {
            "num_bones": build_header(num_bones=99_999),
            "bone_index": build_header(bone_index=8),
            "texture_index": build_header(texture_index=SIZE + 4),
            "num_skin_families": build_header(num_skin_families=0),
            "bodypart_index": build_header(bodypart_index=SIZE + 9),
        }
        for label, raw in cases.items():
            problems = validate_mdl_header_layout(raw)
            self.assertTrue(problems, label)
            self.assertTrue(any(label in problem for problem in problems),
                            (label, problems))

    def test_validation_is_pure_and_repeatable(self):
        raw = build_header()
        self.assertEqual(validate_mdl_header_layout(raw),
                         validate_mdl_header_layout(raw))
        self.assertGreaterEqual(MDL_HEADER_BYTES, 240)


if __name__ == "__main__":
    unittest.main()
