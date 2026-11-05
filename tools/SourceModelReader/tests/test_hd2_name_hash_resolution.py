import json
import sys
import tempfile
import unittest
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
for candidate in (ROOT, ROOT / "research"):
    if str(candidate) not in sys.path:
        sys.path.insert(0, str(candidate))

import resolve_hd2_transform_names as R  # noqa: E402


def unit_document(file_id, bones, transforms):
    skeleton = [
        {"transform_index": index, "name": name}
        for index, (name, _hash) in transforms.items() if name
    ]
    return {
        "unit": {"file_id": file_id},
        "bones_resource": {"bones": [{"hash": value, "name": name} for name, value in bones.items()]},
        "transform_info": {
            "transforms": [
                {"index": index, "name_hash": pair[1] if isinstance(pair, tuple) else pair}
                for index, pair in transforms.items()
            ],
            "resolved_skeleton": skeleton,
        },
    }


class HD2NameHashResolutionTests(unittest.TestCase):
    def write_and_load(self, documents):
        rows = []
        pairs = {}
        with tempfile.TemporaryDirectory() as directory:
            for label, document in documents:
                path = Path(directory) / f"{label}.json"
                path.write_text(json.dumps(document), encoding="utf-8")
                unit_pairs, transforms = R.load_name_pairs(path, label)
                pairs.update(unit_pairs)
                rows.extend(transforms)
        return pairs, rows

    def test_load_reads_pairs_and_names_only_where_the_unit_resolves_them(self):
        document = unit_document(
            "unit-a", {"root": 11, "hips": 12},
            {0: ("root", 11), 1: (None, 12), 2: (None, 99)},
        )
        pairs, rows = self.write_and_load([("a", document)])
        self.assertEqual(pairs, {"root": 11, "hips": 12})
        self.assertEqual([row["resolved_in_own_unit"] for row in rows], [True, False, False])
        self.assertEqual(rows[0]["own_unit_name"], "root")

    def test_union_resolves_a_hash_the_own_unit_table_lacks(self):
        first = unit_document("unit-a", {"root": 11},
                              {0: ("root", 11), 1: (None, 12), 2: (None, 99)})
        second = unit_document("unit-b", {"hips": 12}, {0: ("hips", 12)})
        pairs, rows = self.write_and_load([("a", first), ("b", second)])
        tally = R.classify_transforms(rows, R.build_hash_index(pairs))
        self.assertEqual(tally, {"resolved_in_own_unit": 2, "resolved_by_union": 1,
                               "still_unresolved": 1})
        resolved = next(row for row in rows if row["union_status"] == "resolved_by_union")
        self.assertEqual(resolved["union_name"], "hips")
        self.assertEqual(resolved["name_hash"], 12)
        unresolved = next(row for row in rows if row["union_status"] == "still_unresolved")
        self.assertEqual(unresolved["name_hash"], 99)

    def test_ambiguous_union_hashes_are_never_assigned_a_single_name(self):
        rows = [
            {"unit_label": "a", "transform_index": 5, "name_hash": 77,
             "resolved_in_own_unit": False},
            {"unit_label": "a", "transform_index": 6, "name_hash": 78,
             "resolved_in_own_unit": False},
        ]
        index = R.build_hash_index({"l_thigh": 77, "r_thigh": 77, "knee": 78})
        tally = R.classify_transforms(rows, index)
        self.assertEqual(tally, {"ambiguous_by_union": 1, "resolved_by_union": 1})
        ambiguous = rows[0]
        self.assertNotIn("union_name", ambiguous)
        self.assertEqual(ambiguous["union_candidates"], ["l_thigh", "r_thigh"])

    def test_rows_already_named_in_their_own_unit_win_over_the_union(self):
        rows = [{"unit_label": "a", "transform_index": 0, "name_hash": 11,
                 "resolved_in_own_unit": True}]
        tally = R.classify_transforms(rows, R.build_hash_index({"root": 11, "other": 11}))
        self.assertEqual(tally, {"resolved_in_own_unit": 1})

    def test_candidate_table_can_validate_a_hash_that_reproduces_the_pairs(self):
        # A fixture whose stored hashes come from one known candidate proves the
        # verification path works, which is how an SDK hash gets checked later.
        pairs = {name: R.fnv1a(name.encode("utf-8"))
                 for name in ("StingrayEntityRoot", "hips", "l_shoulder_twist", "game_mesh")}
        rows = R.check_hash_candidates(pairs)
        # Several candidates agree on ASCII-only names, so the assertion is that a
        # correct implementation shows a full match rather than being ranked first.
        self.assertEqual(rows[0]["matched"], len(pairs))
        full_matches = {row["candidate"] for row in rows if row["matched"] == len(pairs)}
        self.assertIn("fnv1a_utf8", full_matches)
        self.assertIn("fnv1a_ascii", full_matches)
        self.assertTrue(all(row["total"] == len(pairs) for row in rows))

    def test_candidate_table_reports_zero_matches_for_unrelated_hashes(self):
        pairs = {"hips": 0x12345678, "game_mesh": 0x9ABCDEF0}
        rows = R.check_hash_candidates(pairs)
        self.assertTrue(all(row["matched"] == 0 for row in rows))
        self.assertEqual({row["total"] for row in rows}, {2})
        self.assertEqual(len(rows), len(R.HASH_CANDIDATES))

    def test_hash_helpers_are_deterministic_and_distinct(self):
        self.assertEqual(R.fnv1a(b"abc"), R.fnv1a(b"abc"))
        self.assertNotEqual(R.fnv1a(b"abc"), R.fnv1a(b"abd"))
        self.assertEqual(R.djb2(b"abc"), R.djb2(b"abc"))
        self.assertEqual(R.sdbm(b""), 0)
        self.assertEqual(R.murmur2_32(b"", 0), 0)
        self.assertEqual(R.jenkins_one_at_a_time(b"abc"), R.jenkins_one_at_a_time(b"abc"))

    def test_crc32_helper_matches_zlib_directly(self):
        self.assertEqual(R.crc32(b"abc"), zlib.crc32(b"abc"))


if __name__ == "__main__":
    unittest.main()
