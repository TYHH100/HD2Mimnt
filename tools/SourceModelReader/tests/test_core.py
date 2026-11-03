import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from source_model_reader import Binary, ReaderError, parse_bodygroup_args


class BinaryTests(unittest.TestCase):
    def test_reads_little_endian_int(self):
        b = Binary(b"\x34\x12\x00\x00", "fixture")
        self.assertEqual(b.i32(0), 0x1234)

    def test_bounds_check_raises(self):
        b = Binary(b"\x00", "fixture")
        with self.assertRaises(ReaderError):
            b.i32(0)


class ArgumentTests(unittest.TestCase):
    def test_bodygroup_args(self):
        self.assertEqual(parse_bodygroup_args(["18=1", "4=1"]), {18: 1, 4: 1})

    def test_duplicate_bodygroup_rejected(self):
        with self.assertRaises(ReaderError):
            parse_bodygroup_args(["18=1", "18=0"])


if __name__ == "__main__":
    unittest.main()
