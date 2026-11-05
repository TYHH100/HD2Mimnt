# HD2 Transform Name Hash Resolution by Unit Union

- Units in batch: 8
- Distinct (name, hash) pairs in the union: 140
- Per-Unit pair counts: avatar_composite=90, fs05_lower_body=82, fs05_shoulder=82, fs05_torso_left_arm=82, fs05_ug_leg_l=82, fs05_ug_leg_r=82, fs05_ug_torso_f=82, fs05_ug_torso_m=82

| Status | Rows |
|---|---:|
| resolved_by_union | 4 |
| resolved_in_own_unit | 235 |
| still_unresolved | 19 |

## Newly resolved by the union

| Unit | Transform | Hash | Name |
|---|---:|---|---|
| avatar_composite | 3 | 975cebbd | skeleton |
| avatar_composite | 95 | a3d491d1 | collision |
| avatar_composite | 99 | 178e51ef | c_culling |
| avatar_composite | 100 | 315cd161 | game_mesh |

## Still unresolved in this batch

- 02b86507, 1a2a6ef2, 43997e6e, 4c3a8f75, 6d12ee05, 75275fac, 7c74e51c, 9da9fbc6, a420b088, a5c8115b, b7d14251, b9162207, c15d6a74, c89b0fd3, d68c4b31, f9963256

## Hash candidate functions tested against the stored pairs

| Candidate | Matched | Total |
|---|---:|---:|
| crc32_utf8 | 0 | 140 |
| djb2_utf8 | 0 | 140 |
| djb2_utf8_casefold | 0 | 140 |
| fnv1a_ascii | 0 | 140 |
| fnv1a_le_offset | 0 | 140 |
| fnv1a_nul_terminated | 0 | 140 |
| fnv1a_utf16le | 0 | 140 |
| fnv1a_utf16le_casefold | 0 | 140 |
| fnv1a_utf8 | 0 | 140 |
| fnv1a_utf8_casefold | 0 | 140 |
| fnv1a_with_length_prefix | 0 | 140 |
| jenkins_one_at_a_time | 0 | 140 |
| murmur2_32_seed0 | 0 | 140 |
| sdbm_utf8 | 0 | 140 |

## Interpretation limits

- Resolution only uses stored (name, hash) pairs; a hash is never assigned a name that does not produce it.
- The union is only as wide as the Units passed in, so still_unresolved means unknown to this batch, not unknown to the game.
- No hash algorithm is claimed here: every candidate listed under hash_functions_tested is a refutation record, not a conclusion.
- TransformInfo counts include duplicates across Units; tallies are per (Unit, transform) row, not per distinct hash.
