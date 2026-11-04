"""Read-only Helldivers 2 resource package inspector.

This module is an independent, bounded parser for the public archive/TOC
structures documented by the community SDKs.  It deliberately does not import
Blender or reuse SDK implementation code.

Supported in 0.8.4-alpha:
- Legacy 0xF0000011 packages with optional .gpu_resources/.stream sidecars.
- DSAR chunk containers using raw LZ4 blocks.
- SLIM/bundled package reconstruction through bundles.nxa package metadata.
- Fast read-only catalog of every SLIM package using only TOC headers/type tables.
- TOC inventory, Unit dependency references, material IDs and Bones summaries.
- Per-Unit TransformInfo, BoneInfo, StreamInfo and MeshInfo decoding in hd2_unit.py.
- Direct Unit and CompositeUnit-backed GPU vertex/index geometry inspection.
- Bones hash resolution, material-scoped BoneInfo palette/remap resolution and per-vertex active skin influence reporting.

TransformInfo hierarchy and active BoneInfo mesh-space matrix diagnostics are implemented in hd2_unit.py. Source-to-HD2 retargeting, material/texture payload decoding and final animated deformation validation remain unimplemented.
"""
from __future__ import annotations

import json
import os
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

TOC_MAGIC = 0xF0000011
DSAR_MAGIC = b"DSAR"
MAX_TOC_TYPES = 100_000
MAX_TOC_ENTRIES = 2_000_000
MAX_PACKAGE_BYTES = 2 * 1024 * 1024 * 1024
MAX_DSAR_CHUNKS = 2_000_000

# Resource type IDs are 64-bit identifiers used by Helldivers 2.
TYPE_IDS = {
    "CompositeUnit": 14191111524867688662,
    "Unit": 16187218042980615487,
    "Texture": 14790446551990181426,
    "Material": 16915718763308572383,
    "Bones": 1792059921637536489,
    "Animation": 10600967118105529382,
    "StateMachine": 11855396184103720540,
    "Physics": 6877563742545042104,
}
TYPE_NAMES = {value: key for key, value in TYPE_IDS.items()}
# Additional public resource type IDs listed by the SDK. These names make the
# archive census useful without pretending that their payload formats are parsed.
TYPE_NAMES.update({
    0x18dead01056b72e9: "Bones",
    0x2a690fd348fe9ac5: "Level",
    0x5f7203c8f280dab8: "Physics",
    0x931e336d7646cc26: "Animation",
    0xa486d4045106165c: "StateMachine",
    0xa8193123526fad64: "Particles",
    0xab2f78e885f513c6: "Prefab",
    0xe0a48d0be9a7453f: "Unit",
    0x535a7bd3e650d799: "WwiseBank",
    0xaf32095c82f2b070: "WwiseDependency",
    0x504b55235d21440e: "WwiseStream",
    0x250e0a11ac8e26f8: "ShadingEnvironmentMapping",
    0x9831ca893b0d087d: "Entity",
    0xaa5965f03029fa18: "Bik",
    0x9efe0a916aae7880: "Font",
    0xd7014a50477953e0: "Cloth",
    0xfe73c7dcff8a7ca5: "ShadingEnvironment",
    0xa14e8dfa2cd117e2: "Lua",
    0xf7505933166d6755: "VectorField",
    0x92d3ee038eeb610d: "Flow",
    0x27862fe24795319c: "RenderConfig",
    0x0d972bab10b40fd3: "Strings",
    0x3b1fa9e8f6bac374: "NetworkConfig",
    0x82645835e6b73232: "Config",
    0xad9c6d9ed1e5e77a: "Package",
    0xd50a8b7e1c82b110: "WwiseMetadata",
    0xb277b11fe4a61d37: "MouseCursor",
    0x7910103158fc1de9: "Renderable",
    0x9e5c3cc74575aeb5: "ShaderLibraryGroup",
    0xe5ee32a477239a93: "ShaderLibrary",
    0x05106b81dcd58a13: "RuntimeFont",
    0x1d59bd6687db6b33: "RagdollProfile",
    0x2a0a70acfe476e1d: "AHBin",
    0x57a13425279979d7: "IKSkeleton",
    0x5fdd5fe391076f9f: "HavokPhysicsProperties",
    0x6592b918e67f082c: "HavokAIProperties",
    0x9199bb50b6896f02: "TextureAtlas",
    0xb8fd4d2cede20ed7: "Geleta",
    0xc4f0f4be7fb0c8d6: "CompositeUnit",
    0xe3f2851035957af5: "HashLookup",
    0xe985c5f61c169997: "SpeedTree",
    0xf7a09f8bb35a1d49: "HavokPhysicsProperties",
    0xfcaaf813b4d3cc1e: "CameraShake",
    0x5ee65304478f8db5: "Bik2",
})
DEFAULT_BASE_PACKAGE = "9ba626afa44a3aa3"
SIDECAR_SUFFIXES = (".gpu_resources", ".stream")


class HD2ResourceError(ValueError):
    """Malformed, truncated or unsupported HD2 resource input."""


def _check_range(data: bytes | bytearray | memoryview, offset: int, size: int, label: str) -> None:
    if offset < 0 or size < 0 or offset > len(data) or size > len(data) - offset:
        raise HD2ResourceError(
            f"{label}: data range is out of bounds (offset={offset}, size={size}, file={len(data)})"
        )


def _u32(data: bytes, offset: int, label: str = "uint32") -> int:
    _check_range(data, offset, 4, label)
    return struct.unpack_from("<I", data, offset)[0]


def _u64(data: bytes, offset: int, label: str = "uint64") -> int:
    _check_range(data, offset, 8, label)
    return struct.unpack_from("<Q", data, offset)[0]


def _lz4_block_decompress(block: bytes, expected_size: int) -> bytes:
    """Small pure-Python LZ4 block decoder (no frame wrapper)."""
    if expected_size < 0 or expected_size > MAX_PACKAGE_BYTES:
        raise HD2ResourceError(f"LZ4 output size is outside the safety limit: {expected_size}")
    src = 0
    out = bytearray()
    source_len = len(block)
    while src < source_len:
        token = block[src]
        src += 1
        literal_len = token >> 4
        if literal_len == 15:
            while True:
                if src >= source_len:
                    raise HD2ResourceError("Truncated LZ4 literal-length extension")
                value = block[src]
                src += 1
                literal_len += value
                if value != 255:
                    break
        if literal_len > source_len - src:
            raise HD2ResourceError("LZ4 literal run exceeds compressed input")
        if len(out) + literal_len > expected_size:
            raise HD2ResourceError("LZ4 literal run exceeds declared output size")
        out.extend(block[src:src + literal_len])
        src += literal_len
        if src == source_len:
            break
        if src + 2 > source_len:
            raise HD2ResourceError("Truncated LZ4 match offset")
        match_offset = block[src] | (block[src + 1] << 8)
        src += 2
        if match_offset == 0 or match_offset > len(out):
            raise HD2ResourceError(f"Invalid LZ4 match offset: {match_offset}")
        match_len = token & 0x0F
        if match_len == 15:
            while True:
                if src >= source_len:
                    raise HD2ResourceError("Truncated LZ4 match-length extension")
                value = block[src]
                src += 1
                match_len += value
                if value != 255:
                    break
        match_len += 4
        if len(out) + match_len > expected_size:
            raise HD2ResourceError("LZ4 match exceeds declared output size")
        for _ in range(match_len):
            out.append(out[-match_offset])
    if len(out) != expected_size:
        raise HD2ResourceError(
            f"LZ4 output length mismatch: declared {expected_size}, decoded {len(out)}"
        )
    return bytes(out)


@dataclass(frozen=True)
class DSARChunk:
    uncompressed_offset: int
    compressed_offset: int
    uncompressed_size: int
    compressed_size: int
    compression_type: int
    chunk_type: int


class DSARFile:
    """Indexed reader for DSAR containers, preserving random-access chunks."""
    def __init__(self, path: Path):
        self.path = Path(path)
        self.size = self.path.stat().st_size
        with self.path.open("rb") as stream:
            header = stream.read(12)
            if len(header) != 12 or header[:4] != DSAR_MAGIC:
                raise HD2ResourceError(f"Not a DSAR container: {self.path}")
            self.version = struct.unpack_from("<I", header, 4)[0]
            self.chunk_count = struct.unpack_from("<I", header, 8)[0]
            if self.chunk_count > MAX_DSAR_CHUNKS:
                raise HD2ResourceError(f"DSAR chunk count exceeds safety limit: {self.chunk_count}")
            table_end = 0x20 + self.chunk_count * 0x20
            if table_end > self.size:
                raise HD2ResourceError("DSAR chunk table is truncated")
            stream.seek(0x20)
            table = stream.read(self.chunk_count * 0x20)
        self.chunks: list[DSARChunk] = []
        self.offset_to_index: dict[int, int] = {}
        for index in range(self.chunk_count):
            fields = struct.unpack_from("<QQIIBB6x", table, index * 0x20)
            chunk = DSARChunk(*fields)
            if chunk.uncompressed_offset in self.offset_to_index:
                raise HD2ResourceError("DSAR has duplicate uncompressed chunk offsets")
            if chunk.compressed_offset < table_end or chunk.compressed_size > self.size - chunk.compressed_offset:
                raise HD2ResourceError(f"DSAR chunk {index} compressed range is invalid")
            if chunk.uncompressed_size > MAX_PACKAGE_BYTES:
                raise HD2ResourceError(f"DSAR chunk {index} exceeds decompressed-size safety limit")
            if chunk.compression_type not in (0, 3):
                raise HD2ResourceError(
                    f"Unsupported DSAR compression type {chunk.compression_type} in chunk {index}"
                )
            self.offset_to_index[chunk.uncompressed_offset] = index
            self.chunks.append(chunk)

    def read_chunk(self, index: int) -> bytes:
        if not 0 <= index < len(self.chunks):
            raise HD2ResourceError(f"DSAR chunk index out of range: {index}")
        chunk = self.chunks[index]
        with self.path.open("rb") as stream:
            stream.seek(chunk.compressed_offset)
            raw = stream.read(chunk.compressed_size)
        if len(raw) != chunk.compressed_size:
            raise HD2ResourceError(f"DSAR chunk {index} payload is truncated")
        if chunk.compression_type == 0:
            if len(raw) != chunk.uncompressed_size:
                raise HD2ResourceError(f"DSAR chunk {index} uncompressed size mismatch")
            return raw
        return _lz4_block_decompress(raw, chunk.uncompressed_size)

    def decompress_all(self) -> bytes:
        total = sum(chunk.uncompressed_size for chunk in self.chunks)
        if total > MAX_PACKAGE_BYTES:
            raise HD2ResourceError(f"DSAR decompressed content exceeds {MAX_PACKAGE_BYTES} bytes")
        output = bytearray()
        for index in range(len(self.chunks)):
            output.extend(self.read_chunk(index))
        return bytes(output)

    def read_resource_at(self, uncompressed_offset: int) -> bytes:
        """Read a chunk-start resource through the next START-marked chunk."""
        try:
            index = self.offset_to_index[uncompressed_offset]
        except KeyError as error:
            raise HD2ResourceError(
                f"Bundle offset {uncompressed_offset:#x} is not a DSAR chunk boundary"
            ) from error
        chunks: list[bytes] = []
        for current in range(index, len(self.chunks)):
            if current > index and self.chunks[current].chunk_type & 0x02:
                break
            chunks.append(self.read_chunk(current))
        return b"".join(chunks)

    def read_range_at(self, uncompressed_offset: int, size: int) -> bytes:
        if size < 0 or size > MAX_PACKAGE_BYTES:
            raise HD2ResourceError(f"Requested bundle range size is invalid: {size}")
        result = bytearray()
        current_offset = uncompressed_offset
        while len(result) < size:
            try:
                chunk_index = self.offset_to_index[current_offset]
            except KeyError as error:
                raise HD2ResourceError(
                    f"Bundle offset {current_offset:#x} is not a DSAR chunk boundary"
                ) from error
            resource = self.read_resource_at(current_offset)
            if not resource:
                raise HD2ResourceError(f"Bundle resource at {current_offset:#x} is empty")
            remaining = size - len(result)
            result.extend(resource[:remaining])
            if len(resource) >= remaining:
                return bytes(result)
            current_offset += len(resource)
        return bytes(result)


@dataclass(frozen=True)
class BundlePiece:
    archive_offset: int
    bundle_offset: int
    bundle_index: int


@dataclass(frozen=True)
class BundlePackage:
    name: str
    size: int
    pieces: tuple[BundlePiece, ...]


class SlimBundleIndex:
    """Read package-to-bundle mappings from bundles.nxa."""
    def __init__(self, data_dir: Path):
        self.data_dir = Path(data_dir)
        metadata_path = self.data_dir / "bundles.nxa"
        if not metadata_path.is_file():
            raise HD2ResourceError(f"Missing SLIM bundle metadata: {metadata_path}")
        metadata = DSARFile(metadata_path).decompress_all()
        _check_range(metadata, 0, 0x18, "bundles.nxa header")
        self.bundle_count = _u32(metadata, 0x0C, "bundle count")
        package_count = _u32(metadata, 0x10, "package count")
        if self.bundle_count > MAX_TOC_ENTRIES:
            raise HD2ResourceError(f"SLIM bundle count exceeds safety limit: {self.bundle_count}")
        if package_count > MAX_TOC_ENTRIES:
            raise HD2ResourceError(f"SLIM package count exceeds safety limit: {package_count}")
        table_start = 0x18
        table_end = table_start + package_count * 0x18
        _check_range(metadata, table_start, package_count * 0x18, "SLIM package table")
        packages: dict[str, BundlePackage] = {}
        for package_index in range(package_count):
            row = table_start + package_index * 0x18
            package_size = _u64(metadata, row, "package size")
            name_offset = _u32(metadata, row + 8, "package name offset")
            item_count = _u32(metadata, row + 12, "package piece count")
            items_offset = _u32(metadata, row + 16, "package pieces offset")
            if package_size > MAX_PACKAGE_BYTES or item_count > MAX_TOC_ENTRIES:
                raise HD2ResourceError(f"SLIM package {package_index} exceeds safety limits")
            if not 0 <= name_offset < len(metadata):
                raise HD2ResourceError(f"SLIM package {package_index} name offset is invalid")
            name_end = metadata.find(b"\0", name_offset, min(len(metadata), name_offset + 4096))
            if name_end < 0:
                raise HD2ResourceError(f"SLIM package {package_index} name is unterminated")
            name = metadata[name_offset:name_end].decode("utf-8", "replace")
            _check_range(metadata, items_offset, item_count * 0x10, f"{name} piece table")
            pieces: list[BundlePiece] = []
            previous_archive_offset = -1
            for item_index in range(item_count):
                item = items_offset + item_index * 0x10
                archive_offset = _u64(metadata, item, "package archive offset")
                bundle_offset = _u32(metadata, item + 8, "bundle uncompressed offset")
                bundle_index = metadata[item + 15]
                if archive_offset <= previous_archive_offset or archive_offset >= package_size:
                    raise HD2ResourceError(f"{name}: package piece offsets are invalid or unordered")
                if bundle_index >= self.bundle_count:
                    raise HD2ResourceError(f"{name}: bundle index {bundle_index} is out of range")
                previous_archive_offset = archive_offset
                pieces.append(BundlePiece(archive_offset, bundle_offset, bundle_index))
            if not name or name in packages:
                raise HD2ResourceError(f"SLIM package name is empty or duplicated: {name!r}")
            if package_size and (not pieces or pieces[0].archive_offset != 0):
                raise HD2ResourceError(f"{name}: first package piece must begin at archive offset zero")
            packages[name] = BundlePackage(name, package_size, tuple(pieces))
        self.packages = packages
        self._bundle_files: dict[int, DSARFile] = {}

    def _bundle(self, index: int) -> DSARFile:
        if index not in self._bundle_files:
            path = self.data_dir / f"bundles.{index:02d}.nxa"
            if not path.is_file():
                raise HD2ResourceError(f"Missing bundle data file: {path.name}")
            self._bundle_files[index] = DSARFile(path)
        return self._bundle_files[index]

    def reconstruct(self, package_name: str) -> bytes:
        try:
            package = self.packages[package_name]
        except KeyError as error:
            choices = ", ".join(sorted(self.packages)[:12])
            raise HD2ResourceError(
                f"SLIM package not found: {package_name!r}. Available examples: {choices}"
            ) from error
        output = bytearray(package.size)
        for index, piece in enumerate(package.pieces):
            end = package.pieces[index + 1].archive_offset if index + 1 < len(package.pieces) else package.size
            required = end - piece.archive_offset
            bundle = self._bundle(piece.bundle_index)
            data = bundle.read_range_at(piece.bundle_offset, required)
            output[piece.archive_offset:end] = data
        return bytes(output)

    def reconstruct_range(self, package_name: str, offset: int, size: int) -> bytes:
        """Read a logical package range without rebuilding its entire payload."""
        try:
            package = self.packages[package_name]
        except KeyError as error:
            raise HD2ResourceError(f"SLIM package not found: {package_name!r}") from error
        if offset < 0 or size < 0 or offset > package.size or size > package.size - offset:
            raise HD2ResourceError(
                f"{package_name}: requested range is outside package bounds "
                f"(offset={offset}, size={size}, package={package.size})"
            )
        output = bytearray()
        request_end = offset + size
        for index, piece in enumerate(package.pieces):
            piece_end = (
                package.pieces[index + 1].archive_offset
                if index + 1 < len(package.pieces) else package.size
            )
            take_start = max(offset, piece.archive_offset)
            take_end = min(request_end, piece_end)
            if take_start >= take_end:
                continue
            local_skip = take_start - piece.archive_offset
            take_size = take_end - take_start
            bundle = self._bundle(piece.bundle_index)
            raw = bundle.read_range_at(piece.bundle_offset, local_skip + take_size)
            segment = raw[local_skip:local_skip + take_size]
            if len(segment) != take_size:
                raise HD2ResourceError(
                    f"{package_name}: short SLIM range at package offset {take_start:#x}"
                )
            output.extend(segment)
            if len(output) == size:
                return bytes(output)
        if len(output) != size:
            raise HD2ResourceError(
                f"{package_name}: requested {size} bytes at {offset:#x}, read {len(output)}"
            )
        return bytes(output)


def _read_package_file(path: Path) -> tuple[bytes, bytes, bytes, str]:
    """Load a standalone legacy/DSAR package and its GPU/stream companions."""
    path = Path(path)
    if not path.is_file():
        raise HD2ResourceError(f"Package file does not exist: {path}")
    file_size = path.stat().st_size
    if file_size < 4:
        raise HD2ResourceError(f"Package file is too short: {path}")
    if file_size > MAX_PACKAGE_BYTES:
        raise HD2ResourceError(f"Package file exceeds the {MAX_PACKAGE_BYTES}-byte safety limit")
    with path.open("rb") as stream:
        magic = stream.read(4)
    gpu_path = Path(str(path) + ".gpu_resources")
    stream_path = Path(str(path) + ".stream")
    if magic == DSAR_MAGIC:
        toc_data = DSARFile(path).decompress_all()
        gpu_data = DSARFile(gpu_path).decompress_all() if gpu_path.is_file() else b""
        stream_data = DSARFile(stream_path).decompress_all() if stream_path.is_file() else b""
        return toc_data, gpu_data, stream_data, "dsar"
    if len(magic) == 4 and struct.unpack("<I", magic)[0] == TOC_MAGIC:
        toc_data = path.read_bytes()
        gpu_data = gpu_path.read_bytes() if gpu_path.is_file() else b""
        stream_data = stream_path.read_bytes() if stream_path.is_file() else b""
        if len(gpu_data) > MAX_PACKAGE_BYTES or len(stream_data) > MAX_PACKAGE_BYTES:
            raise HD2ResourceError("GPU or stream sidecar exceeds the package safety limit")
        return toc_data, gpu_data, stream_data, "legacy"
    raise HD2ResourceError(
        f"Unsupported package magic in {path.name}: {magic.hex(' ')}"
    )



def _read_package_from_game_data(data_dir: Path, package_name: str) -> tuple[bytes, bytes, bytes, str]:
    data_dir = Path(data_dir)
    package_name = Path(package_name).name
    direct_path = data_dir / package_name
    if direct_path.is_file():
        return _read_package_file(direct_path)
    index = SlimBundleIndex(data_dir)
    toc_data = index.reconstruct(package_name)
    gpu_name = package_name + ".gpu_resources"
    stream_name = package_name + ".stream"
    gpu_data = index.reconstruct(gpu_name) if gpu_name in index.packages else b""
    stream_data = index.reconstruct(stream_name) if stream_name in index.packages else b""
    return toc_data, gpu_data, stream_data, "slim"


@dataclass(frozen=True)
class ResourceEntry:
    file_id: int
    type_id: int
    toc_offset: int
    stream_offset: int
    gpu_offset: int
    unknown1: int
    unknown2: int
    toc_size: int
    stream_size: int
    gpu_size: int
    unknown3: int
    unknown4: int
    entry_index: int

    @property
    def type_name(self) -> str:
        return TYPE_NAMES.get(self.type_id, f"Unknown({self.type_id:016x})")


@dataclass
class ResourcePackage:
    name: str
    source_kind: str
    toc_data: bytes
    gpu_data: bytes
    stream_data: bytes
    type_counts: dict[int, int]
    entries: list[ResourceEntry]
    warnings: list[str]

    def entry_map(self) -> dict[tuple[int, int], ResourceEntry]:
        return {(entry.file_id, entry.type_id): entry for entry in self.entries}

    def payload(self, entry: ResourceEntry) -> tuple[bytes, bytes, bytes]:
        if entry.toc_size:
            _check_range(self.toc_data, entry.toc_offset, entry.toc_size,
                         f"resource {entry.file_id:016x} TOC payload")
        if entry.gpu_size:
            _check_range(self.gpu_data, entry.gpu_offset, entry.gpu_size,
                         f"resource {entry.file_id:016x} GPU payload")
        if entry.stream_size:
            _check_range(self.stream_data, entry.stream_offset, entry.stream_size,
                         f"resource {entry.file_id:016x} stream payload")
        return (
            self.toc_data[entry.toc_offset:entry.toc_offset + entry.toc_size] if entry.toc_size else b"",
            self.gpu_data[entry.gpu_offset:entry.gpu_offset + entry.gpu_size] if entry.gpu_size else b"",
            self.stream_data[entry.stream_offset:entry.stream_offset + entry.stream_size] if entry.stream_size else b"",
        )



def parse_toc_package(name: str, toc_data: bytes, gpu_data: bytes = b"", stream_data: bytes = b"", source_kind: str = "legacy") -> ResourcePackage:
    if len(toc_data) < 72:
        raise HD2ResourceError("TOC package is shorter than its 72-byte header")
    magic, num_types, num_files, unknown = struct.unpack_from("<4I", toc_data, 0)
    if magic != TOC_MAGIC:
        raise HD2ResourceError(f"Invalid TOC magic: {magic:#010x}; expected {TOC_MAGIC:#010x}")
    if num_types > MAX_TOC_TYPES or num_files > MAX_TOC_ENTRIES:
        raise HD2ResourceError(f"TOC counts exceed safety limits (types={num_types}, files={num_files})")
    type_table_offset = 72
    type_table_size = num_types * 32
    entries_offset = type_table_offset + type_table_size
    entries_size = num_files * 80
    _check_range(toc_data, type_table_offset, type_table_size, "TOC type table")
    _check_range(toc_data, entries_offset, entries_size, "TOC entry table")

    declared_types: dict[int, int] = {}
    for index in range(num_types):
        row = type_table_offset + index * 32
        _unk, type_id, count, unk2, unk3 = struct.unpack_from("<QQQII", toc_data, row)
        if type_id in declared_types:
            raise HD2ResourceError(f"Duplicate TOC type definition: {type_id:016x}")
        declared_types[type_id] = count

    entries: list[ResourceEntry] = []
    seen_ids: set[tuple[int, int]] = set()
    actual_counts: dict[int, int] = {}
    warnings: list[str] = []
    for index in range(num_files):
        row = entries_offset + index * 80
        values = struct.unpack_from("<7Q6I", toc_data, row)
        entry = ResourceEntry(
            file_id=values[0], type_id=values[1], toc_offset=values[2],
            stream_offset=values[3], gpu_offset=values[4], unknown1=values[5],
            unknown2=values[6], toc_size=values[7], stream_size=values[8],
            gpu_size=values[9], unknown3=values[10], unknown4=values[11],
            entry_index=values[12],
        )
        key = (entry.file_id, entry.type_id)
        if key in seen_ids:
            raise HD2ResourceError(
                f"Duplicate resource identity: {entry.file_id:016x}/{entry.type_id:016x}"
            )
        seen_ids.add(key)
        actual_counts[entry.type_id] = actual_counts.get(entry.type_id, 0) + 1
        # Validate every payload slice immediately so bad offsets are reported
        # at inventory time rather than later during Unit decoding.
        if entry.toc_size:
            _check_range(toc_data, entry.toc_offset, entry.toc_size,
                         f"TOC payload for {entry.file_id:016x}")
        if entry.gpu_size:
            _check_range(gpu_data, entry.gpu_offset, entry.gpu_size,
                         f"GPU payload for {entry.file_id:016x}")
        if entry.stream_size:
            _check_range(stream_data, entry.stream_offset, entry.stream_size,
                         f"stream payload for {entry.file_id:016x}")
        entries.append(entry)
    for type_id, declared_count in declared_types.items():
        actual = actual_counts.get(type_id, 0)
        if declared_count != actual:
            warnings.append(
                f"Type {type_id:016x} declares {declared_count} resources but TOC has {actual}"
            )
    for type_id, actual in actual_counts.items():
        if type_id not in declared_types:
            warnings.append(f"Resource type {type_id:016x} has {actual} entries but no type-table row")
    return ResourcePackage(
        name=name, source_kind=source_kind, toc_data=toc_data,
        gpu_data=gpu_data, stream_data=stream_data,
        type_counts=actual_counts, entries=entries, warnings=warnings,
    )


@dataclass(frozen=True)
class UnitHeader:
    unknown_ref_1: int
    bones_ref: int
    composite_ref: int
    unknown_ref_2: int
    state_machine_ref: int
    header_data_1: int
    version: int
    lod_group_offset: int
    transform_info_offset: int
    light_list_offset: int
    pre_light_offset: int
    wwise_callback_offset: int
    customization_offset: int
    unknown_header_offset: int
    connecting_bone_hash_offset: int
    bone_info_offset: int
    stream_info_offset: int
    ending_offset: int
    mesh_info_offset: int
    materials_offset: int


def parse_unit_header(data: bytes) -> UnitHeader:
    if len(data) < 128:
        raise HD2ResourceError(f"Unit TOC payload is shorter than 128-byte header: {len(data)}")
    refs = struct.unpack_from("<5Q", data, 0)
    header_data_1, version = struct.unpack_from("<2I", data, 40)
    lod_group, transform, light, pre_light, wwise = struct.unpack_from("<5I", data, 48)
    customization, unknown_header, connecting, bone_info, stream_info, ending, mesh_info = struct.unpack_from("<7I", data, 76)
    materials = _u32(data, 112, "Unit MaterialsOffset")
    offsets = {
        "LOD group": lod_group, "TransformInfo": transform, "LightList": light,
        "pre-light": pre_light, "Wwise callback": wwise,
        "CustomizationInfo": customization, "unknown header": unknown_header,
        "ConnectingBoneHash": connecting, "BoneInfo": bone_info,
        "StreamInfo": stream_info, "ending": ending, "MeshInfo": mesh_info,
        "Materials": materials,
    }
    for label, offset in offsets.items():
        if offset and offset >= len(data):
            raise HD2ResourceError(f"Unit {label} offset {offset:#x} is outside its TOC payload ({len(data)} bytes)")
    return UnitHeader(
        unknown_ref_1=refs[0], bones_ref=refs[1], composite_ref=refs[2],
        unknown_ref_2=refs[3], state_machine_ref=refs[4],
        header_data_1=header_data_1, version=version,
        lod_group_offset=lod_group, transform_info_offset=transform,
        light_list_offset=light, pre_light_offset=pre_light,
        wwise_callback_offset=wwise, customization_offset=customization,
        unknown_header_offset=unknown_header, connecting_bone_hash_offset=connecting,
        bone_info_offset=bone_info, stream_info_offset=stream_info,
        ending_offset=ending, mesh_info_offset=mesh_info, materials_offset=materials,
    )


def parse_unit_material_ids(data: bytes, materials_offset: int) -> list[dict]:
    if materials_offset == 0:
        return []
    count = _u32(data, materials_offset, "Unit material count")
    if count > 100_000:
        raise HD2ResourceError(f"Unit material count exceeds safety limit: {count}")
    start_sections = materials_offset + 4
    _check_range(data, start_sections, count * 4, "Unit material section IDs")
    sections = list(struct.unpack_from(f"<{count}I", data, start_sections)) if count else []
    material_start = start_sections + count * 4
    _check_range(data, material_start, count * 8, "Unit material resource IDs")
    ids = list(struct.unpack_from(f"<{count}Q", data, material_start)) if count else []
    return [{"section_id": sections[i], "material_id": ids[i]} for i in range(count)]


def parse_unit_table_counts(data: bytes, header: UnitHeader) -> dict:
    result = {}
    for label, offset in (("bone_info_count", header.bone_info_offset),
                          ("stream_count", header.stream_info_offset),
                          ("mesh_count", header.mesh_info_offset)):
        # A StreamInfoOffset of 1 is the SDK's CompositeUnit external-stream sentinel,
        # not a byte offset into this Unit's TOC buffer.
        result[label] = _u32(data, offset, f"Unit {label}") if offset > (1 if label == "stream_count" else 0) else 0
    return result


def parse_bones_resource(data: bytes) -> dict:
    if len(data) < 8:
        raise HD2ResourceError("Bones resource is shorter than its 8-byte header")
    num_names, num_lods = struct.unpack_from("<2I", data, 0)
    if num_names > 100_000 or num_lods > 1024:
        raise HD2ResourceError("Bones resource count exceeds safety limits")
    offset = 8
    _check_range(data, offset, num_lods * 4, "Bones LOD float array")
    offset += num_lods * 4
    _check_range(data, offset, num_names * 4, "Bones hash array")
    hashes = list(struct.unpack_from(f"<{num_names}I", data, offset)) if num_names else []
    offset += num_names * 4
    _check_range(data, offset, num_lods * 4, "Bones LOD levels")
    lod_levels = list(struct.unpack_from(f"<{num_lods}I", data, offset)) if num_lods else []
    offset += num_lods * 4
    remaining = data[offset:]
    names_raw = remaining.split(b"\0") if remaining else []
    if names_raw and names_raw[-1] == b"":
        names_raw.pop()
    names = [value.decode("utf-8", "replace") for value in names_raw]
    warnings = []
    if len(names) != num_names:
        warnings.append(f"Bones resource declares {num_names} names but contains {len(names)} strings")
    if len(hashes) != num_names:
        warnings.append(f"Bones resource declares {num_names} names but contains {len(hashes)} hashes")
    pairs = [
        {"hash": hashes[i], "name": names[i] if i < len(names) else None}
        for i in range(num_names)
    ]
    return {
        "name_count": num_names,
        "lod_count": num_lods,
        "lod_levels": lod_levels,
        "bones": pairs,
        "warnings": warnings,
    }


def build_resource_report(package: ResourcePackage) -> dict:
    entries_by_key = package.entry_map()
    resources = []
    units = []
    bones_resources = []
    report_warnings = list(package.warnings)
    for entry in package.entries:
        resources.append({
            "file_id": f"{entry.file_id:016x}",
            "type_id": f"{entry.type_id:016x}",
            "type": entry.type_name,
            "toc_size": entry.toc_size,
            "gpu_size": entry.gpu_size,
            "stream_size": entry.stream_size,
            "entry_index": entry.entry_index,
        })
        toc_payload, _gpu_payload, _stream_payload = package.payload(entry)
        if entry.type_id == TYPE_IDS["Unit"]:
            header = parse_unit_header(toc_payload)
            materials = parse_unit_material_ids(toc_payload, header.materials_offset)
            counts = parse_unit_table_counts(toc_payload, header)
            dependencies = []
            if header.bones_ref:
                dependencies.append({
                    "role": "bones", "file_id": f"{header.bones_ref:016x}",
                    "type": "Bones",
                    "resolved_in_package": (header.bones_ref, TYPE_IDS["Bones"]) in entries_by_key,
                })
            if header.composite_ref:
                dependencies.append({"role": "composite_unit", "file_id": f"{header.composite_ref:016x}",
                                     "type": "CompositeUnit", "resolved_in_package": (header.composite_ref, TYPE_IDS["CompositeUnit"]) in entries_by_key})
            if header.state_machine_ref:
                dependencies.append({"role": "state_machine", "file_id": f"{header.state_machine_ref:016x}",
                                     "type": "StateMachine", "resolved_in_package": (header.state_machine_ref, TYPE_IDS["StateMachine"]) in entries_by_key})
            for index, item in enumerate(materials):
                dependencies.append({"role": f"material[{index}]", "file_id": f"{item['material_id']:016x}",
                                     "type": "Material", "resolved_in_package": (item["material_id"], TYPE_IDS["Material"]) in entries_by_key})
            units.append({
                "file_id": f"{entry.file_id:016x}",
                "resource_version": header.version,
                "references": {
                    "bones": f"{header.bones_ref:016x}" if header.bones_ref else None,
                    "composite_unit": f"{header.composite_ref:016x}" if header.composite_ref else None,
                    "state_machine": f"{header.state_machine_ref:016x}" if header.state_machine_ref else None,
                    "unknown_ref_1": f"{header.unknown_ref_1:016x}" if header.unknown_ref_1 else None,
                    "unknown_ref_2": f"{header.unknown_ref_2:016x}" if header.unknown_ref_2 else None,
                },
                "table_counts": counts,
                "offsets": {
                    "transform_info": header.transform_info_offset,
                    "bone_info": header.bone_info_offset,
                    "stream_info": header.stream_info_offset,
                    "mesh_info": header.mesh_info_offset,
                    "materials": header.materials_offset,
                },
                "material_sections": materials,
                "dependencies": dependencies,
                "gpu_bytes": entry.gpu_size,
                "stream_bytes": entry.stream_size,
            })
        elif entry.type_id == TYPE_IDS["Bones"]:
            summary = parse_bones_resource(toc_payload)
            for warning in summary["warnings"]:
                report_warnings.append(f"Bones {entry.file_id:016x}: {warning}")
            bones_resources.append({"file_id": f"{entry.file_id:016x}", **summary})
    missing_deps = [
        {"unit_id": unit["file_id"], **dependency}
        for unit in units for dependency in unit["dependencies"]
        if not dependency["resolved_in_package"]
    ]
    return {
        "schema": "SourceModelReader-HD2ResourceInventory1",
        "package": {
            "name": package.name,
            "container": package.source_kind,
            "toc_bytes": len(package.toc_data),
            "gpu_bytes": len(package.gpu_data),
            "stream_bytes": len(package.stream_data),
        },
        "summary": {
            "status": "warning" if report_warnings else "ok",
            "resource_count": len(package.entries),
            "type_count": len(package.type_counts),
            "unit_count": len(units),
            "bones_resource_count": len(bones_resources),
            "missing_in_package_dependency_count": len(missing_deps),
        },
        "resource_type_counts": [
            {"type_id": f"{type_id:016x}", "type": TYPE_NAMES.get(type_id, "Unknown"), "count": count}
            for type_id, count in sorted(package.type_counts.items(), key=lambda item: (-item[1], item[0]))
        ],
        "units": units,
        "bones_resources": bones_resources,
        "resources": resources,
        "dependencies_not_in_this_package": missing_deps,
        "warnings": report_warnings,
        "limitations": [
            "This milestone inventories TOC resources and reads Unit/Bones metadata only.",
            "A missing dependency in this package may be stored in a different game archive.",
            "GPU vertex streams, BoneInfo remaps, material payloads and mesh reconstruction are not yet decoded.",
        ],
    }


def _catalog_one_slim_package(index: SlimBundleIndex, name: str) -> dict:
    package = index.packages[name]
    if package.size < 72:
        raise HD2ResourceError(f"{name}: package is shorter than the TOC header")
    header = index.reconstruct_range(name, 0, 72)
    magic, num_types, num_files, _unknown = struct.unpack_from("<4I", header, 0)
    if magic != TOC_MAGIC:
        raise HD2ResourceError(f"{name}: TOC magic is {magic:#010x}, expected {TOC_MAGIC:#010x}")
    if num_types > MAX_TOC_TYPES or num_files > MAX_TOC_ENTRIES:
        raise HD2ResourceError(f"{name}: TOC counts exceed safety limits")
    type_bytes = num_types * 32
    entry_table_end = 72 + type_bytes + num_files * 80
    if entry_table_end > package.size:
        raise HD2ResourceError(f"{name}: TOC tables exceed declared package size")
    raw_types = index.reconstruct_range(name, 72, type_bytes) if type_bytes else b""
    counts = {}
    for item_index in range(num_types):
        _unknown, type_id, count, _unk2, _unk3 = struct.unpack_from(
            "<QQQII", raw_types, item_index * 32
        )
        if type_id in counts:
            raise HD2ResourceError(f"{name}: duplicate resource type {type_id:016x}")
        counts[type_id] = count
    return {
        "package": name,
        "package_bytes": package.size,
        "resource_count": num_files,
        "type_count": num_types,
        "resource_types": [
            {"type_id": f"{type_id:016x}", "type": TYPE_NAMES.get(type_id, "Unknown"), "count": count}
            for type_id, count in sorted(counts.items(), key=lambda row: (-row[1], row[0]))
        ],
        "counts_by_type_id": counts,
    }


def scan_slim_package_catalog(data_dir: str | Path, max_packages: Optional[int] = None) -> dict:
    """Census SLIM package TOC headers/type tables, without loading GPU streams."""
    root = Path(data_dir)
    index = SlimBundleIndex(root)
    names = [
        name for name in sorted(index.packages)
        if not name.endswith(SIDECAR_SUFFIXES)
    ]
    if max_packages is not None:
        if max_packages < 1:
            raise HD2ResourceError("max_packages must be at least 1")
        names = names[:max_packages]
    packages = []
    errors = []
    aggregate: dict[int, int] = {}
    for name in names:
        try:
            item = _catalog_one_slim_package(index, name)
            packages.append(item)
            for type_id, count in item["counts_by_type_id"].items():
                aggregate[type_id] = aggregate.get(type_id, 0) + count
        except (HD2ResourceError, OSError, struct.error) as error:
            errors.append({"package": name, "error": str(error)})

    def package_count(type_name: str) -> int:
        type_id = TYPE_IDS[type_name]
        return sum(1 for row in packages if row["counts_by_type_id"].get(type_id, 0) > 0)

    unit_id = TYPE_IDS["Unit"]
    bones_id = TYPE_IDS["Bones"]
    composite_id = TYPE_IDS["CompositeUnit"]
    relevant = []
    for row in packages:
        counts = row["counts_by_type_id"]
        unit_count = counts.get(unit_id, 0)
        bones_count = counts.get(bones_id, 0)
        composite_count = counts.get(composite_id, 0)
        if unit_count or bones_count or composite_count:
            relevant.append({
                "package": row["package"],
                "package_bytes": row["package_bytes"],
                "resource_count": row["resource_count"],
                "unit_count": unit_count,
                "bones_count": bones_count,
                "composite_unit_count": composite_count,
                "material_count": counts.get(TYPE_IDS["Material"], 0),
                "texture_count": counts.get(TYPE_IDS["Texture"], 0),
            })
    return {
        "schema": "SourceModelReader-HD2PackageCatalog1",
        "data_directory": str(root.resolve()),
        "container": "slim",
        "summary": {
            "status": "warning" if errors else "ok",
            "package_count_in_index": len(index.packages),
            "package_count_scanned": len(names),
            "package_count_parsed": len(packages),
            "package_count_errors": len(errors),
            "packages_with_units": package_count("Unit"),
            "packages_with_bones": package_count("Bones"),
            "packages_with_composite_units": package_count("CompositeUnit"),
            "resource_type_count": len(aggregate),
        },
        "aggregate_resource_types": [
            {"type_id": f"{type_id:016x}", "type": TYPE_NAMES.get(type_id, "Unknown"), "count": count}
            for type_id, count in sorted(aggregate.items(), key=lambda row: (-row[1], row[0]))
        ],
        "model_resource_packages": sorted(
            relevant,
            key=lambda row: (-row["unit_count"], -row["bones_count"], row["package"]),
        ),
        "errors": errors,
        "limitations": [
            "This census reads only the package index, TOC headers and resource type tables.",
            "GPU and stream sidecars are not reconstructed during catalog scanning.",
            "A Unit package may reference Bones, CompositeUnit or other resources stored in a different package.",
            "Unit payloads are not decoded by this catalog command; inspect candidate packages individually with hd2-inspect.",
        ],
    }


def format_catalog_text(report: dict) -> str:
    summary = report["summary"]
    lines = [
        "Source Model Reader 0.8.1-alpha - HD2 package catalog",
        f"Data: {report['data_directory']} | Container: {report['container']}",
        (f"Status: {summary['status'].upper()} | Packages: {summary['package_count_parsed']}/"
         f"{summary['package_count_in_index']} | Errors: {summary['package_count_errors']}"),
        (f"Packages with Unit: {summary['packages_with_units']} | Bones: "
         f"{summary['packages_with_bones']} | CompositeUnit: {summary['packages_with_composite_units']}"),
        "Candidate packages (Unit / Bones / CompositeUnit):",
    ]
    for row in report["model_resource_packages"]:
        lines.append(
            f"  {row['package']} | units={row['unit_count']} bones={row['bones_count']} "
            f"composites={row['composite_unit_count']} materials={row['material_count']} "
            f"textures={row['texture_count']} bytes={row['package_bytes']:,}"
        )
    if not report["model_resource_packages"]:
        lines.append("  (none found in scanned packages)")
    for item in report["errors"][:30]:
        lines.append(f"ERROR: {item['package']}: {item['error']}")
    if len(report["errors"]) > 30:
        lines.append(f"... {len(report['errors']) - 30} additional package errors in JSON")
    lines.append("Note: catalog scans TOC headers/type tables only; it does not read GPU vertex data.")
    return "\n".join(lines)


def inspect_hd2_package(input_path: str | Path, package_name: Optional[str] = None) -> dict:
    path = Path(input_path)
    if path.is_dir():
        if package_name:
            toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(path, package_name)
            name = Path(package_name).name
        else:
            direct_candidates = sorted(
                child.name for child in path.iterdir()
                if child.is_file() and not child.name.endswith((".gpu_resources", ".stream", ".patch"))
                and child.name != "bundle_database.data"
            )
            if (path / DEFAULT_BASE_PACKAGE).is_file() or (path / "bundles.nxa").is_file():
                name = DEFAULT_BASE_PACKAGE
            else:
                candidates = [name for name in direct_candidates if name != "bundles.nxa"]
                if len(candidates) != 1:
                    raise HD2ResourceError(
                        "Input is a game data directory. Specify --package ARCHIVE_ID; "
                        f"found {len(candidates)} direct package candidates."
                    )
                name = candidates[0]
            toc_data, gpu_data, stream_data, source_kind = _read_package_from_game_data(path, name)
    else:
        if package_name:
            raise HD2ResourceError("--package is only valid when input is a game data directory")
        toc_data, gpu_data, stream_data, source_kind = _read_package_file(path)
        name = path.name
    package = parse_toc_package(name, toc_data, gpu_data, stream_data, source_kind)
    return build_resource_report(package)


def format_report_text(report: dict) -> str:
    package = report["package"]
    summary = report["summary"]
    lines = [
        f"Source Model Reader 0.8.2-alpha - HD2 resource inspection",
        f"Package: {package['name']} | Container: {package['container']}",
        f"TOC: {package['toc_bytes']:,} bytes | GPU: {package['gpu_bytes']:,} bytes | Stream: {package['stream_bytes']:,} bytes",
        f"Status: {summary['status'].upper()} | Resources: {summary['resource_count']} | Types: {summary['type_count']} | Units: {summary['unit_count']} | Bones resources: {summary['bones_resource_count']}",
        "Resource types:",
    ]
    for item in report["resource_type_counts"]:
        lines.append(f"  {item['type']:<18} {item['count']:>7}  ({item['type_id']})")
    if report["units"]:
        lines.append("Units:")
        for unit in report["units"]:
            refs = unit["references"]
            lines.append(
                f"  {unit['file_id']} | v{unit['resource_version']} | meshes={unit['table_counts']['mesh_count']} "
                f"streams={unit['table_counts']['stream_count']} bone-palettes={unit['table_counts']['bone_info_count']} "
                f"materials={len(unit['material_sections'])}"
            )
            lines.append(
                f"    Bones={refs['bones'] or '-'} Composite={refs['composite_unit'] or '-'} "
                f"StateMachine={refs['state_machine'] or '-'}"
            )
    if report["bones_resources"]:
        lines.append("Bones resources:")
        for bones in report["bones_resources"]:
            lines.append(
                f"  {bones['file_id']} | names={bones['name_count']} | LOD levels={bones['lod_count']}"
            )
            for warning in bones["warnings"]:
                lines.append(f"    WARNING: {warning}")
    if report["dependencies_not_in_this_package"]:
        lines.append(
            f"Dependencies not present in this package: {len(report['dependencies_not_in_this_package'])} "
            "(they may be stored in another archive)"
        )
    for warning in report["warnings"]:
        lines.append(f"WARNING: {warning}")
    lines.append("Note: use hd2-unit to inspect per-Unit transforms, BoneInfo, stream layouts and GPU geometry. Palette/name resolution and retargeting remain later milestones.")
    return "\n".join(lines)


__all__ = [
    "DSARFile", "HD2ResourceError", "ResourcePackage", "SlimBundleIndex",
    "TYPE_IDS", "build_resource_report", "format_report_text", "format_catalog_text",
    "inspect_hd2_package", "scan_slim_package_catalog",
    "parse_bones_resource", "parse_toc_package", "parse_unit_header",
]
