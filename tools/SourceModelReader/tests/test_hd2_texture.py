import hashlib
import struct
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

from hd2_resources import HD2ResourceError, TYPE_IDS
from hd2_texture import (
    DDS_HEADER_SIZE,
    TEXTURE_TOC_SIZE,
    _read_slim_texture_payload,
    decode_bc7_preview,
    decode_texture_preview,
    inspect_hd2_texture,
    parse_texture_resource,
)


def make_texture_toc(
    *, width=256, height=128, mip_count=9, dxgi=98, array_size=1,
    populate_mips=True,
):
    data = bytearray(TEXTURE_TOC_SIZE)
    struct.pack_into("<3I", data, 0, 1, 2, 3)
    level_sizes = []
    current_width, current_height = width, height
    for _ in range(mip_count):
        blocks_x = max(1, (current_width + 3) // 4)
        blocks_y = max(1, (current_height + 3) // 4)
        block_bytes = 8 if dxgi in (70, 71, 72, 79, 80, 81) else 16
        level_sizes.append(blocks_x * blocks_y * block_bytes)
        current_width = max(1, current_width // 2)
        current_height = max(1, current_height // 2)
    total_size = sum(level_sizes)
    current_width, current_height = width, height
    offset = 0
    for index in range(min(mip_count, 15)):
        if populate_mips:
            struct.pack_into(
                "<IIHH", data, 12 + index * 12,
                offset, total_size - offset, current_height, current_width,
            )
        offset += level_sizes[index]
        current_width = max(1, current_width // 2)
        current_height = max(1, current_height // 2)
    dds = 12 + 15 * 12
    data[dds:dds + 4] = b"DDS "
    struct.pack_into("<I", data, dds + 4, 124)
    struct.pack_into("<I", data, dds + 8, 0x0002100F)
    struct.pack_into("<II", data, dds + 12, height, width)
    struct.pack_into("<I", data, dds + 28, mip_count)
    struct.pack_into("<I", data, dds + 76, 32)
    struct.pack_into("<I", data, dds + 80, 4)
    data[dds + 84:dds + 88] = b"DX10"
    struct.pack_into("<5I", data, dds + 128, dxgi, 3, 0, array_size, 0)
    return bytes(data)


class FakeSlimIndex:
    def __init__(self, _root, package_bytes, gpu_bytes=b"", stream_bytes=b""):
        self.packages = {
            "fixture": SimpleNamespace(size=len(package_bytes)),
            "fixture.gpu_resources": SimpleNamespace(size=len(gpu_bytes)),
            "fixture.stream": SimpleNamespace(size=len(stream_bytes)),
        }
        self.data = {
            "fixture": package_bytes,
            "fixture.gpu_resources": gpu_bytes,
            "fixture.stream": stream_bytes,
        }
        self.ranges = []

    def reconstruct_range(self, name, offset, size):
        self.ranges.append((name, offset, size))
        return self.data[name][offset:offset + size]


def make_texture_package(texture_id, texture_toc, gpu_bytes, stream_bytes):
    type_id = TYPE_IDS["Texture"]
    type_table_offset = 72
    entries_offset = type_table_offset + 32
    payload_offset = entries_offset + 80
    toc = bytearray(payload_offset + len(texture_toc))
    struct.pack_into("<4I", toc, 0, 0xF0000011, 1, 1, 0)
    struct.pack_into("<QQQII", toc, type_table_offset, 0, type_id, 1, 16, 64)
    struct.pack_into(
        "<7Q6I", toc, entries_offset,
        texture_id, type_id, payload_offset, 0, 0, 0, 0,
        len(texture_toc), len(stream_bytes), len(gpu_bytes), 0, 0, 0,
    )
    toc[payload_offset:] = texture_toc
    return bytes(toc)


class TextureParserTests(unittest.TestCase):
    def test_parses_texture_toc_dds_header_and_fifteen_mip_records(self):
        toc = make_texture_toc()
        report = parse_texture_resource(toc, b"gpu-data")

        self.assertEqual(len(toc), TEXTURE_TOC_SIZE)
        self.assertEqual(report["schema"], "SourceModelReader-HD2Texture1")
        self.assertEqual(len(report["mipmaps"]), 15)
        self.assertEqual(report["dds"]["width"], 256)
        self.assertEqual(report["dds"]["height"], 128)
        self.assertEqual(report["dds"]["dxgi_format"], 98)
        self.assertEqual(report["dds"]["dxgi_format_name"], "BC7_UNORM")
        self.assertEqual(report["dds"]["array_size"], 1)
        self.assertEqual(report["dds"]["alpha_mode_name"], "unknown")
        self.assertEqual(report["payloads"]["selected_source"], "gpu")
        self.assertEqual(report["payloads"]["selected"]["size"], len(b"gpu-data"))
        self.assertEqual(report["warnings"], [])


    def test_validates_bc7_full_mip_chain_for_gpu_only_texture(self):
        toc = make_texture_toc(width=512, height=512, mip_count=10)
        expected_bytes = 349552
        report = parse_texture_resource(toc, b"G" * expected_bytes)

        self.assertEqual(report["mip_chain"]["expected_full_chain_bytes"], expected_bytes)
        self.assertEqual(report["payload_layouts"]["gpu"]["layout"], "full_mip_chain")
        self.assertEqual(report["payload_layouts"]["gpu"]["matches_full_chain"], True)
        self.assertEqual(report["mipmap_validation"]["status"], "match")
        self.assertEqual(report["mipmap_validation"]["populated_records"], 10)
        self.assertEqual(report["mip_chain"]["levels"][6]["width"], 8)

    def test_detects_stream_full_chain_and_gpu_resident_mip_tail(self):
        toc = make_texture_toc(width=2048, height=2048, mip_count=12)
        stream = b"S" * 5592432
        gpu = b"G" * 1392
        report = parse_texture_resource(toc, gpu, stream)

        self.assertEqual(report["mip_chain"]["expected_full_chain_bytes"], 5592432)
        self.assertEqual(report["payload_layouts"]["stream"]["layout"], "full_mip_chain")
        self.assertEqual(report["payload_layouts"]["gpu"]["layout"], "mip_tail_from_level_6")
        self.assertEqual(report["payload_layouts"]["gpu"]["candidate_tail_start_width"], 32)
        self.assertEqual(report["payload_layouts"]["gpu"]["candidate_tail_start_height"], 32)
        self.assertEqual(report["mipmap_validation"]["status"], "match")

    def test_warns_when_populated_mipmap_metadata_disagrees_with_calculated_chain(self):
        toc = bytearray(make_texture_toc(width=512, height=512, mip_count=10))
        struct.pack_into("<I", toc, 12, 16)  # First mip should begin at byte zero.
        report = parse_texture_resource(bytes(toc), b"G" * 349552)

        self.assertEqual(report["mipmap_validation"]["status"], "mismatch")
        self.assertEqual(report["mipmap_validation"]["mismatches"][0]["index"], 0)
        self.assertTrue(any("disagree with the calculated" in warning for warning in report["warnings"]))

    def test_does_not_iterate_unbounded_dds_mip_count(self):
        toc = bytearray(make_texture_toc())
        dds = 12 + 15 * 12
        struct.pack_into("<I", toc, dds + 28, 0xFFFFFFFF)
        report = parse_texture_resource(bytes(toc), b"payload")

        self.assertIsNone(report["mip_chain"])
        self.assertEqual(report["mipmap_validation"]["status"], "not_checked")
        self.assertTrue(any("more than" in warning or "declares" in warning for warning in report["warnings"]))

    def test_calculates_chain_when_toc_mipmap_table_is_unpopulated(self):
        toc = make_texture_toc(
            width=512, height=512, mip_count=10, populate_mips=False
        )
        report = parse_texture_resource(toc, b"G" * 349552)

        self.assertEqual(report["mipmap_validation"]["status"], "not_populated")
        self.assertEqual(report["mipmap_validation"]["missing_records"], list(range(10)))
        self.assertEqual(report["mip_chain"]["expected_full_chain_bytes"], 349552)
        self.assertEqual(report["payload_layouts"]["gpu"]["layout"], "full_mip_chain")
        self.assertTrue(any("zero/unpopulated" in note for note in report["notes"]))


    def test_bc7_preview_writes_rgba_png_and_reports_alpha_histogram(self):
        import sys
        import types
        import zlib

        toc = make_texture_toc(width=4, height=4, mip_count=3)
        payload = b"B" * 48
        report = parse_texture_resource(toc, payload)
        bgra = bytearray()
        alpha_pattern = (0, 64, 128, 255)
        for index in range(16):
            bgra.extend((3, 2, 1, alpha_pattern[index % 4]))

        fake_decoder = types.ModuleType("texture2ddecoder")
        fake_decoder.__version__ = "test"
        calls = []
        def decode_bc7(data, width, height):
            calls.append((data, width, height))
            return bytes(bgra)
        fake_decoder.decode_bc7 = decode_bc7

        with patch.dict(sys.modules, {"texture2ddecoder": fake_decoder}):
            preview, png = decode_bc7_preview(report, payload)

        self.assertEqual(calls, [(payload[:16], 4, 4)])
        self.assertTrue(png.startswith(b"\x89PNG\r\n\x1a\n"))
        self.assertEqual(preview["width"], 4)
        self.assertEqual(preview["height"], 4)
        self.assertEqual(preview["decoded_input_bytes"], 16)
        alpha = preview["alpha"]
        self.assertEqual(alpha["pixel_count"], 16)
        self.assertEqual(alpha["minimum"], 0)
        self.assertEqual(alpha["maximum"], 255)
        self.assertEqual(alpha["unique_values"], 4)
        self.assertEqual(alpha["fully_transparent_pixels"], 4)
        self.assertEqual(alpha["partial_alpha_pixels"], 8)
        self.assertEqual(alpha["fully_opaque_pixels"], 4)
        self.assertEqual(alpha["alpha_histogram"][0], 4)
        self.assertEqual(alpha["alpha_histogram"][64], 4)
        self.assertEqual(alpha["alpha_histogram"][128], 4)
        self.assertEqual(alpha["alpha_histogram"][255], 4)

        offset = 8
        idat = bytearray()
        while offset < len(png):
            chunk_size = struct.unpack_from(">I", png, offset)[0]
            chunk_type = png[offset + 4:offset + 8]
            chunk_data = png[offset + 8:offset + 8 + chunk_size]
            if chunk_type == b"IDAT":
                idat.extend(chunk_data)
            offset += 12 + chunk_size
            if chunk_type == b"IEND":
                break
        raw_scanlines = zlib.decompress(bytes(idat))
        self.assertEqual(raw_scanlines[0], 0)  # PNG filter None
        self.assertEqual(raw_scanlines[1:5], bytes((1, 2, 3, 0)))  # BGRA -> RGBA

    def test_bc1_preview_reports_binary_alpha_and_rgba_output(self):
        import sys
        import types

        toc = make_texture_toc(width=4, height=4, mip_count=3, dxgi=71)
        payload = b"C" * 24
        report = parse_texture_resource(toc, payload)
        bgra = bytearray()
        for index in range(16):
            bgra.extend((30, 20, 10, 0 if index % 2 == 0 else 255))

        fake_decoder = types.ModuleType("texture2ddecoder")
        fake_decoder.__version__ = "test"
        calls = []

        def decode_bc1(data, width, height):
            calls.append((data, width, height))
            return bytes(bgra)

        fake_decoder.decode_bc1 = decode_bc1
        with patch.dict(sys.modules, {"texture2ddecoder": fake_decoder}):
            preview, png = decode_texture_preview(report, payload)

        self.assertEqual(calls, [(payload[:8], 4, 4)])
        self.assertEqual(preview["codec"], "BC1")
        self.assertEqual(preview["alpha"]["source_alpha_available"], True)
        self.assertEqual(preview["alpha"]["fully_transparent_pixels"], 8)
        self.assertEqual(preview["alpha"]["partial_alpha_pixels"], 0)
        self.assertEqual(preview["alpha"]["fully_opaque_pixels"], 8)
        self.assertEqual(preview["channels"]["red"]["minimum"], 10)
        self.assertEqual(preview["channels"]["green"]["minimum"], 20)
        self.assertEqual(preview["channels"]["blue"]["minimum"], 30)
        self.assertTrue(png.startswith(b"\x89PNG\r\n\x1a\n"))

    def test_bc5_preview_marks_alpha_as_decoder_fill_and_reports_rg(self):
        import sys
        import types

        toc = make_texture_toc(width=4, height=4, mip_count=3, dxgi=83)
        payload = b"N" * 48
        report = parse_texture_resource(toc, payload)
        bgra = bytearray()
        for index in range(16):
            # texture2ddecoder's BGRA output stores the two BC5 components in
            # R and G, sets B to 0, and fills A with 255.
            bgra.extend((0, 20 + index, 100 + index, 255))

        fake_decoder = types.ModuleType("texture2ddecoder")
        fake_decoder.__version__ = "test"
        calls = []

        def decode_bc5(data, width, height):
            calls.append((data, width, height))
            return bytes(bgra)

        fake_decoder.decode_bc5 = decode_bc5
        with patch.dict(sys.modules, {"texture2ddecoder": fake_decoder}):
            preview, png = decode_texture_preview(report, payload)

        self.assertEqual(calls, [(payload[:16], 4, 4)])
        self.assertEqual(preview["codec"], "BC5_UNORM")
        self.assertFalse(preview["alpha"]["source_alpha_available"])
        self.assertEqual(preview["alpha"]["minimum"], 255)
        self.assertEqual(preview["alpha"]["unique_values"], 1)
        self.assertEqual(preview["channels"]["red"]["minimum"], 100)
        self.assertEqual(preview["channels"]["red"]["maximum"], 115)
        self.assertEqual(preview["channels"]["green"]["minimum"], 20)
        self.assertEqual(preview["channels"]["green"]["maximum"], 35)
        self.assertEqual(preview["channels"]["blue"]["unique_values"], 1)
        self.assertIn("Not a source alpha channel", preview["alpha"]["interpretation"])
        self.assertTrue(png.startswith(b"\x89PNG\r\n\x1a\n"))

    def test_bc7_preview_reports_missing_optional_dependency(self):
        import sys

        toc = make_texture_toc(width=4, height=4, mip_count=3)
        payload = b"B" * 48
        report = parse_texture_resource(toc, payload)
        with patch.dict(sys.modules, {"texture2ddecoder": None}):
            with self.assertRaisesRegex(HD2ResourceError, "pip install texture2ddecoder"):
                decode_bc7_preview(report, payload)

    def test_bc7_preview_refuses_to_treat_gpu_mip_tail_as_full_resolution(self):
        toc = make_texture_toc(width=2048, height=2048, mip_count=12)
        gpu_tail = b"G" * 1392
        report = parse_texture_resource(toc, gpu_tail)

        self.assertEqual(report["payloads"]["selected_source"], "gpu")
        self.assertEqual(report["payload_layouts"]["gpu"]["layout"], "mip_tail_from_level_6")
        with self.assertRaisesRegex(HD2ResourceError, "resident mip tail cannot be decoded"):
            decode_bc7_preview(report, gpu_tail)


    def test_stream_is_selected_when_both_payloads_are_present_and_both_are_hashed(self):
        gpu = b"gpu-low-mips"
        stream = b"stream-high-mips"
        report = parse_texture_resource(make_texture_toc(), gpu, stream)

        self.assertEqual(report["payloads"]["selected_source"], "stream")
        self.assertEqual(report["payloads"]["gpu"]["sha256"], hashlib.sha256(gpu).hexdigest())
        self.assertEqual(report["payloads"]["stream"]["sha256"], hashlib.sha256(stream).hexdigest())
        self.assertEqual(report["payloads"]["selected"]["sha256"], hashlib.sha256(stream).hexdigest())
        self.assertTrue(any("Both GPU and Stream" in note for note in report["notes"]))

    def test_preserves_unknown_trailing_toc_bytes(self):
        toc = make_texture_toc() + b"\x10\x20\x30\x40"
        report = parse_texture_resource(toc)

        self.assertEqual(report["trailing_toc_size"], 4)
        self.assertEqual(report["trailing_toc_hex"], "10203040")

    def test_warns_on_invalid_dds_headers_without_guessing(self):
        toc = bytearray(make_texture_toc())
        dds = 12 + 15 * 12
        toc[dds:dds + 4] = b"BAD!"
        toc[dds + 84:dds + 88] = b"DX99"
        report = parse_texture_resource(bytes(toc))

        self.assertTrue(any("DDS magic" in warning for warning in report["warnings"]))
        self.assertTrue(any("FourCC" in warning for warning in report["warnings"]))

    def test_rejects_truncated_toc(self):
        with self.assertRaises(HD2ResourceError):
            parse_texture_resource(b"\x00" * (TEXTURE_TOC_SIZE - 1))

    def test_inspection_returns_sdk_style_dds_header_plus_selected_payload(self):
        toc = make_texture_toc()
        gpu = b"gpu-payload"
        stream = b"stream-payload"
        resource = {"file_id": "0000000000001234", "entry_index": 0}
        with patch(
            "hd2_texture._read_slim_texture_payload",
            return_value=(resource, toc, gpu, stream),
        ):
            report, dds, actual_gpu, actual_stream = inspect_hd2_texture("unused", "fixture", 0x1234)

        expected = toc[12 + 15 * 12:TEXTURE_TOC_SIZE] + stream
        self.assertEqual(dds, expected)
        self.assertEqual(report["summary"]["selected_payload_source"], "stream")
        self.assertEqual(report["dds_export_bytes"], DDS_HEADER_SIZE + len(stream))
        self.assertEqual(actual_gpu, gpu)
        self.assertEqual(actual_stream, stream)

    def test_range_loader_reads_one_texture_entry_and_only_its_payload_ranges(self):
        texture_id = 0x123456789ABCDEF0
        texture_toc = make_texture_toc()
        gpu = b"G" * 64
        stream = b"S" * 128
        package_bytes = make_texture_package(texture_id, texture_toc, gpu, stream)
        fake = FakeSlimIndex(None, package_bytes, gpu, stream)
        with tempfile.TemporaryDirectory() as temp:
            with patch("hd2_texture.SlimBundleIndex", return_value=fake):
                resource, actual_toc, actual_gpu, actual_stream = _read_slim_texture_payload(
                    temp, "fixture", texture_id
                )

        self.assertEqual(resource["entry_index"], 0)
        self.assertEqual(resource["toc_row_index"], 0)
        self.assertEqual(actual_toc, texture_toc)
        self.assertEqual(actual_gpu, gpu)
        self.assertEqual(actual_stream, stream)
        self.assertIn(("fixture.gpu_resources", 0, len(gpu)), fake.ranges)
        self.assertIn(("fixture.stream", 0, len(stream)), fake.ranges)
        self.assertFalse(any(name.endswith(".gpu_resources") and size > len(gpu) for name, _offset, size in fake.ranges))

    def test_cli_help_exposes_hd2_texture_command(self):
        import subprocess
        import sys

        script = Path(__file__).resolve().parents[1] / "source_model_reader.py"
        result = subprocess.run(
            [sys.executable, str(script), "hd2-texture", "--help"],
            capture_output=True, text=True, check=False,
        )

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--preview-png", result.stdout)
        self.assertIn("--output", result.stdout)

    def test_range_loader_rejects_wrong_stored_entry_index(self):
        texture_id = 0x123456789ABCDEF0
        texture_toc = make_texture_toc()
        package = bytearray(make_texture_package(texture_id, texture_toc, b"", b""))
        entry_offset = 72 + 32
        struct.pack_into("<I", package, entry_offset + 76, 9)
        fake = FakeSlimIndex(None, bytes(package))
        with tempfile.TemporaryDirectory() as temp:
            with patch("hd2_texture.SlimBundleIndex", return_value=fake):
                with self.assertRaises(HD2ResourceError):
                    _read_slim_texture_payload(temp, "fixture", texture_id)


if __name__ == "__main__":
    unittest.main(verbosity=2)
