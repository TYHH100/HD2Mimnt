# Source Model Reader 0.8.2-alpha patch

This patch adds a read-only `hd2-unit` command to the existing Source Model Reader 0.8.1-alpha project.

## Install

1. Extract this archive into `tools/SourceModelReader` (the folder containing `source_model_reader.py`).
2. Open PowerShell in that folder.
3. Run:

```powershell
py -3 install_0.8.2.py
```

The Python installer creates backups of `source_model_reader.py` and `hd2_resources.py` before patching the CLI and report text. It keeps the existing files in place, installs the new `hd2_unit.py` module, and updates `VERSION`.

## Run the first FS-05 test

Start with one Unit from the FS-05 player archive:

```powershell
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package "8670598c1f4462dc" --unit "210fb2f60f8308ea" --json "fs05_unit_210fb2f60f8308ea.json"
```

Then test a second Unit with a different BoneInfo palette count:

```powershell
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package "8670598c1f4462dc" --unit "324fb206612ce4f7" --json "fs05_unit_324fb206612ce4f7.json"
```

After the direct Unit path is checked, test the Avatar's CompositeUnit-backed geometry:

```powershell
py -3 source_model_reader.py hd2-unit "G:\AppData\SteamLibrary\steamapps\common\Helldivers 2\data" --package "18235e0c9ec0e636" --unit "4d1c334d294dfa97" --json "hd2_avatar_unit.json"
```

## What 0.8.2-alpha reads

- Unit `TransformInfo`: local transform values, raw 4x4 matrices, parent indices and name hashes.
- Unit `BoneInfo`: per-LOD matrices, `RealIndices`, and the raw per-material remap arrays.
- `StreamInfo`: component descriptors, formats, strides and GPU buffer ranges.
- `MeshInfo`: mesh IDs, TransformIndex, LOD/stream selection, material slots and section ranges.
- Direct Unit GPU buffers and CompositeUnit-backed shared geometry.
- Supported scalar/vector float32, float16, uint8, uint32 and RGBA8 components; 16-bit and 32-bit index buffers.

Vertex attributes and section index lists are written to JSON as a neutral intermediate representation. The parser does not modify or write HD2 resources.

## Deliberate limitations

- `BoneInfo.RealIndices` and material remaps are preserved and range-checked, but are **not yet resolved into TransformInfo names or retargeted to the Avatar skeleton**. That remains the next palette-resolver milestone.
- Packed normal format 30 is preserved as its raw uint32 value; no guessed normal decoder is applied. Packed 10-10-10-2 values are also preserved raw.
- Transform matrices are emitted as stored floats. The parser does not assume a row/column-vector convention or reinterpret them as bind-pose matrices.
- This is an alpha read-only parser. The synthetic parser tests pass, but the real FS-05 and Avatar packages still need to be run on the user's HD2 installation. The resulting JSON reports are the next validation input.

## Tests

From this folder:

```powershell
py -3 -m unittest discover -s tests -v
```

The new tests cover TransformInfo, BoneInfo/remap layout, StreamInfo component decoding, MeshInfo/index data, and CompositeUnit mesh-to-stream mapping. Existing 0.8.1 tests should run unchanged.
