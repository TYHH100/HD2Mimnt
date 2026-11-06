# HD2 Material Variable-Boundary Validation

- Packages considered: 12; Materials inspected: 813; failures: 0
- Materials per package cap: none; per-package counts shown below

## Hypothesis hits

| Hypothesis | Materials matching | Rate |
|---|---:|---:|

## Descriptor-bearing subset

- Materials carrying at least one descriptor: 202
- H1 (sum of elements x stride) on that subset: 0
- H2 (highest used value end) on that subset: 0
- H5 (highest offset + last size == VariableDataSize): 202
- H6 (highest offset + last size == VariableDataSize - 24): 0
- Offsets tile the region (sum of gaps == declared - last size): 136
- Reserved-word nonzero counts across all descriptors: [775, 0, 4655, 4519, 0]
| H1 sum(elements x stride) == VariableDataSize | 611 | 0.7515 |
| H1' same, with a component-size floor on stride | 686 | 0.8438 |
| H2 highest used value end == VariableDataSize | 611 | 0.7515 |
| H2' highest used value end <= VariableDataSize | 813 | 1.0 |
| H3 EndOffset == resource size | 0 | 0.0 |
| H4 EndOffset == payload start + VariableDataSize | 0 | 0.0 |
| H4' EndOffset == payload start + VariableDataSize + tail | 0 | 0.0 |
| VariableDataSize exactly fills the remaining bytes | 35 | 0.0431 |
| Declared size exceeds available bytes | 0 | 0.0 |
| Materials with a descriptor outside the region | 0 | 0.0 |
| Materials with stride below the class component size | 202 | 0.2485 |
| Materials with array descriptors (elements > 1) | 0 | 0.0 |
| Materials producing decoder warnings | 0 | 0.0 |

## Distributions

- Trailing bytes after the declared region: {4: 49, 12: 657, 0: 35, 8: 72}
- resource size minus EndOffset: {28: 49, 36: 657, 24: 35, 32: 72}
- EndOffset minus (payload start + declared): {-24: 813}
- Reserved-word nonzero counts (word0/1/4 are what the decoder calls class/elements/stride): [775, 0, 4655, 4519, 0]
- Offset gap sizes across all Materials: {'4': 3071, '8': 499, '12': 556, '16': 142, '20': 79, '24': 26, '28': 14, '32': 18, '36': 1, '40': 22, '44': 3, '48': 1, '52': 3, '56': 4, '60': 1, '72': 4, '80': 2, '88': 1, '92': 5, '168': 1}
- Last-parameter size (declared minus highest offset): {4: 150, 8: 6, 16: 28, 12: 18}
- Materials whose gaps plus last size fill the declared region: 136 of 202 with descriptors
- Descriptors total: 4655; array descriptors: 0; outside-region: 0; stride-below-component: 4655

## Per-package Material counts

| Package | Materials inspected |
|---|---:|
| `007e093ca718ca1a` | 660 |
| `009da023c64d178d` | 51 |
| `009cb913478ad077` | 41 |
| `0014a4ba255675bd` | 15 |
| `00cf0ef7df912a6e` | 11 |
| `000d250a449ec1e8` | 9 |
| `0118c2cfda4f68a5` | 9 |
| `0102bf171412f94d` | 8 |
| `0089bbe3e284fcaa` | 6 |
| `00a98a6c294e2c1e` | 1 |
| `00c928ec7e330f9e` | 1 |
| `0121fd0e8370a1dc` | 1 |

## Interpretation limits

- A hypothesis is a stored-data arithmetic relation, not a runtime claim; matching does not prove how the shader consumes the values.
- Materials whose payload could not be read or parsed are counted as failures and excluded from the hypothesis tallies.
- Class-to-component-size mapping is applied only for the four known classes; unknown classes contribute their stored stride alone.
- With a per-package cap the sample is the first N Materials of each package, not a random selection.

