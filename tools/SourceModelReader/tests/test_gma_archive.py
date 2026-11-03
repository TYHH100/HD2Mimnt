import binascii
import struct
import tempfile
import unittest
from pathlib import Path

from source_model_reader import GMAArchive, ReaderError


def build_gma(path: Path, entries: list[tuple[str, bytes]], *, version: int = 3) -> None:
    with path.open("wb") as f:
        f.write(b"GMAD")
        f.write(bytes([version]))
        f.write(struct.pack("<QQ", 76561198000000000, 1700000000))
        for value in ("", "Unit Test Addon", '{"type":"model"}', "Test Author"):
            f.write(value.encode("utf-8") + b"\0")
        f.write(struct.pack("<i", 1))
        for index, (name, data) in enumerate(entries, start=1):
            f.write(struct.pack("<I", index))
            f.write(name.encode("utf-8") + b"\0")
            f.write(struct.pack("<qI", len(data), binascii.crc32(data) & 0xFFFFFFFF))
        f.write(struct.pack("<I", 0))
        for _, data in entries:
            f.write(data)
        f.write(struct.pack("<I", 0))


class GMAArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.gma_path = self.root / "fixture.gma"
        build_gma(self.gma_path, [("models/test/model.mdl", b"MDL-DATA"), ("models/test/model.vvd", b"VVD-DATA")])

    def tearDown(self):
        self.temp.cleanup()

    def test_reads_metadata_and_members(self):
        archive = GMAArchive(self.gma_path)
        try:
            self.assertEqual(archive.version, 3)
            self.assertEqual(archive.name, "Unit Test Addon")
            self.assertEqual(archive.author, "Test Author")
            self.assertEqual(len(archive.entries), 2)
            self.assertEqual(archive.read("models/test/model.mdl"), b"MDL-DATA")
            self.assertEqual(archive.read("MODELS/TEST/MODEL.VVD"), b"VVD-DATA")
        finally:
            archive.close()

    def test_rejects_bad_crc(self):
        raw = bytearray(self.gma_path.read_bytes())
        raw[-5] ^= 0x01  # Corrupt the last payload byte, before the 4-byte trailer.
        self.gma_path.write_bytes(raw)
        archive = GMAArchive(self.gma_path)
        try:
            with self.assertRaisesRegex(ReaderError, "CRC32 mismatch"):
                archive.read("models/test/model.vvd")
        finally:
            archive.close()

    def test_rejects_path_traversal(self):
        bad = self.root / "unsafe.gma"
        build_gma(bad, [("../outside.txt", b"bad")])
        with self.assertRaisesRegex(ReaderError, "unsafe member path"):
            GMAArchive(bad)

    def test_rejects_truncated_payload(self):
        raw = self.gma_path.read_bytes()[:-8]
        self.gma_path.write_bytes(raw)
        with self.assertRaisesRegex(ReaderError, "truncated"):
            GMAArchive(self.gma_path)


if __name__ == "__main__":
    unittest.main()
