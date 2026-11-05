#!/usr/bin/env python3
"""Source Model Reader - inspect Source 1 resources and export unified GLB or SMD.

Initial target: Source studio model v48, VVD v4, VTX v7 (DX90/DX80).
No third-party Python dependencies are required.
"""
from __future__ import annotations

import argparse
import binascii
import json
import math
import os
import struct
import sys
import zipfile
from dataclasses import dataclass, asdict
from pathlib import Path, PurePosixPath
from typing import Iterable, Optional

VERSION = "0.8.4-alpha"
VERTEX_SIZE = 48
BONE_SIZE = 216
MODEL_SIZE = 148
MESH_SIZE = 116


class ReaderError(Exception):
    pass


class Binary:
    def __init__(self, data: bytes, label: str):
        self.data = data
        self.label = label

    def check(self, offset: int, size: int, what: str = "data") -> None:
        if offset < 0 or size < 0 or offset + size > len(self.data):
            raise ReaderError(
                f"{self.label}: {what} is out of bounds (offset={offset}, size={size}, file={len(self.data)})"
            )

    def unpack(self, fmt: str, offset: int, what: str = "data"):
        size = struct.calcsize(fmt)
        self.check(offset, size, what)
        return struct.unpack_from(fmt, self.data, offset)

    def i32(self, offset: int, what: str = "int32") -> int:
        return self.unpack("<i", offset, what)[0]

    def u32(self, offset: int, what: str = "uint32") -> int:
        return self.unpack("<I", offset, what)[0]

    def u16(self, offset: int, what: str = "uint16") -> int:
        return self.unpack("<H", offset, what)[0]

    def u8(self, offset: int, what: str = "uint8") -> int:
        self.check(offset, 1, what)
        return self.data[offset]

    def cstring(self, offset: int, limit: int = 4096) -> str:
        if offset < 0 or offset >= len(self.data):
            return ""
        end = self.data.find(b"\0", offset, min(len(self.data), offset + limit))
        if end < 0:
            end = min(len(self.data), offset + limit)
        return self.data[offset:end].decode("utf-8", "replace")


@dataclass
class GMAEntry:
    path: str
    size: int
    crc32: int
    offset: int


class GMAArchive:
    """Random-access reader for uncompressed GMAD/GMA addon archives (versions 1-3)."""
    MAX_ENTRIES = 200_000
    MAX_STRING_BYTES = 1_048_576

    def __init__(self, path: Path):
        self.path = path
        self.file = path.open("rb")
        self.entries: list[GMAEntry] = []
        self.entry_map: dict[str, GMAEntry] = {}
        self.warnings: list[str] = []
        try:
            self._parse()
        except Exception:
            self.file.close()
            raise

    def _read_exact(self, count: int, label: str) -> bytes:
        data = self.file.read(count)
        if len(data) != count:
            raise ReaderError(f"GMA: truncated {label} (wanted {count} bytes, got {len(data)})")
        return data

    def _read_cstring(self, label: str) -> str:
        data = bytearray()
        while len(data) <= self.MAX_STRING_BYTES:
            chunk = self.file.read(1)
            if not chunk:
                raise ReaderError(f"GMA: unexpected end of file while reading {label}")
            if chunk == b"\0":
                return data.decode("utf-8", "replace")
            data.extend(chunk)
        raise ReaderError(f"GMA: {label} exceeds the {self.MAX_STRING_BYTES}-byte safety limit")

    @staticmethod
    def _validate_member_path(raw: str) -> str:
        # GMA paths are logical addon paths, not host filesystem paths.
        normalized = raw.replace("\\", "/")
        path = PurePosixPath(normalized)
        if not normalized or path.is_absolute() or any(part in ("", ".", "..") for part in normalized.split("/")):
            raise ReaderError(f"GMA contains an unsafe member path: {raw!r}")
        if path.parts and ":" in path.parts[0]:
            raise ReaderError(f"GMA contains a drive-qualified member path: {raw!r}")
        return path.as_posix()

    def _parse(self) -> None:
        file_size = self.path.stat().st_size
        if self._read_exact(4, "signature") != b"GMAD":
            raise ReaderError("Input has no GMAD signature; this may be a compressed Workshop .bin rather than a plain .gma")
        self.version = self._read_exact(1, "format version")[0]
        if self.version not in (1, 2, 3):
            raise ReaderError(f"Unsupported GMA format version: {self.version} (supported: 1, 2, 3)")
        self.steam_id, self.timestamp = struct.unpack("<QQ", self._read_exact(16, "SteamID/timestamp"))
        self.required_content = self._read_cstring("required-content field")
        self.name = self._read_cstring("addon name")
        self.description = self._read_cstring("addon description")
        self.author = self._read_cstring("addon author")
        self.addon_version = struct.unpack("<i", self._read_exact(4, "addon version"))[0]

        total_payload = 0
        previous_index = 0
        while True:
            raw_index = self._read_exact(4, "file index")
            index = struct.unpack("<I", raw_index)[0]
            if index == 0:
                break
            if len(self.entries) >= self.MAX_ENTRIES:
                raise ReaderError(f"GMA contains more than {self.MAX_ENTRIES} file entries")
            if index != previous_index + 1:
                self.warnings.append(f"GMA file indices are not sequential near entry {index}")
            previous_index = index
            raw_path = self._read_cstring(f"file path for entry {index}")
            member_path = self._validate_member_path(raw_path)
            size = struct.unpack("<q", self._read_exact(8, f"file size for {member_path}"))[0]
            crc32 = struct.unpack("<I", self._read_exact(4, f"CRC32 for {member_path}"))[0]
            if size < 0:
                raise ReaderError(f"GMA has a negative file size for {member_path}")
            key = member_path.casefold()
            if key in self.entry_map:
                raise ReaderError(f"GMA has duplicate member paths (case-insensitive): {member_path}")
            self.entries.append(GMAEntry(member_path, size, crc32, 0))
            self.entry_map[key] = self.entries[-1]
            total_payload += size
            if total_payload > file_size:
                raise ReaderError("GMA member sizes exceed the archive size")

        self.data_offset = self.file.tell()
        offset = self.data_offset
        for entry in self.entries:
            entry.offset = offset
            offset += entry.size
        if offset > file_size:
            raise ReaderError(f"GMA file data is truncated (expected end {offset}, file size {file_size})")
        trailing = file_size - offset
        if trailing == 4:
            self.file.seek(offset)
            trailer = self._read_exact(4, "archive trailer")
            if trailer != b"\0\0\0\0":
                raise ReaderError("GMA has an unexpected non-zero 4-byte trailer")
        elif trailing != 0:
            raise ReaderError(f"GMA has {trailing} unexpected trailing bytes after file data")
        self.file.seek(self.data_offset)

    @property
    def metadata(self) -> dict:
        return {
            "format_version": self.version,
            "steam_id": self.steam_id,
            "timestamp": self.timestamp,
            "required_content": self.required_content,
            "name": self.name,
            "description": self.description,
            "author": self.author,
            "addon_version": self.addon_version,
            "entry_count": len(self.entries),
            "warnings": list(self.warnings),
        }

    def read(self, member_path: str) -> bytes:
        key = self._validate_member_path(member_path).casefold()
        entry = self.entry_map.get(key)
        if entry is None:
            raise ReaderError(f"Missing sibling resource in GMA: {member_path}")
        self.file.seek(entry.offset)
        data = self._read_exact(entry.size, f"member {entry.path}")
        # A zero checksum is used by GMAD's -nocrc mode; it means no checksum was stored.
        if entry.crc32:
            actual = binascii.crc32(data) & 0xFFFFFFFF
            if actual != entry.crc32:
                raise ReaderError(
                    f"GMA CRC32 mismatch for {entry.path}: stored {entry.crc32:08x}, calculated {actual:08x}"
                )
        else:
            self.warnings.append(f"No CRC32 supplied for GMA member: {entry.path}")
        return data

    def close(self) -> None:
        self.file.close()


class AssetBundle:
    """Reads a model and sibling resources from a folder, MDL path, ZIP, or GMA."""
    def __init__(self, input_path: Path, model_hint: Optional[str] = None):
        self.input_path = input_path
        self.zf: Optional[zipfile.ZipFile] = None
        self.gma: Optional[GMAArchive] = None
        self.root: Optional[Path] = None
        self.archive_info: Optional[dict] = None
        self.mdl_rel: str
        self.mdl_display: str
        if input_path.is_file() and input_path.suffix.lower() == ".gma":
            self.gma = GMAArchive(input_path)
            try:
                names = [entry.path for entry in self.gma.entries if entry.path.lower().endswith(".mdl")]
                if not names:
                    raise ReaderError("GMA archive contains no .mdl files")
                self.mdl_rel = self._matching_candidates(names, model_hint)[0]
                self.mdl_display = f"{input_path.name}::{self.mdl_rel}"
                self.archive_info = self.gma.metadata
            except Exception:
                self.gma.close()
                self.gma = None
                raise
        elif input_path.is_file() and zipfile.is_zipfile(input_path):
            self.zf = zipfile.ZipFile(input_path, "r")
            names = [n for n in self.zf.namelist() if n.lower().endswith(".mdl") and not n.endswith("/")]
            if not names:
                raise ReaderError("ZIP archive contains no .mdl files")
            candidates = self._matching_candidates(names, model_hint)
            self.mdl_rel = candidates[0]
            self.mdl_display = self.mdl_rel
        elif input_path.is_dir():
            self.root = input_path
            paths = [p for p in input_path.rglob("*") if p.is_file() and p.suffix.lower() == ".mdl"]
            if not paths:
                raise ReaderError(f"No .mdl files found under: {input_path}")
            names = [p.relative_to(input_path).as_posix() for p in paths]
            self.mdl_rel = self._matching_candidates(names, model_hint)[0]
            self.mdl_display = str(input_path / Path(self.mdl_rel))
        elif input_path.is_file() and input_path.suffix.lower() == ".mdl":
            self.root = input_path.parent
            self.mdl_rel = input_path.name
            self.mdl_display = str(input_path)
        else:
            raise ReaderError("Input must be a .mdl file, a folder, a ZIP archive, or an uncompressed .gma archive")

    @staticmethod
    def _matching_candidates(names: list[str], hint: Optional[str]) -> list[str]:
        norm = sorted(names, key=lambda x: x.lower())
        if hint:
            h = hint.replace("\\", "/").lower()
            matches = [n for n in norm if n.lower() == h or n.lower().endswith("/" + h) or PurePosixPath(n).name.lower() == PurePosixPath(h).name.lower()]
            if not matches:
                raise ReaderError(f"No .mdl entry matches --model {hint!r}")
            return matches
        def score(name: str) -> tuple[int, str]:
            low = name.lower()
            # Prefer the familiar player-model suffix, then a model with matching VVD/VTX siblings.
            suffix_score = 0 if "_pm.mdl" in low else 1
            return suffix_score, low
        return sorted(norm, key=score)

    def read(self, extension: str) -> bytes:
        target = str(PurePosixPath(self.mdl_rel).with_suffix(extension))
        if self.gma:
            return self.gma.read(target)
        if self.zf:
            try:
                return self.zf.read(target)
            except KeyError:
                # Some archives contain mixed path casing; resolve case-insensitively.
                lowmap = {n.lower(): n for n in self.zf.namelist()}
                key = lowmap.get(target.lower())
                if key is None:
                    raise ReaderError(f"Missing sibling resource in ZIP: {target}")
                return self.zf.read(key)
        assert self.root is not None
        p = self.root / Path(self.mdl_rel)
        sibling = p.with_suffix(extension)
        if not sibling.is_file():
            raise ReaderError(f"Missing sibling resource: {sibling}")
        return sibling.read_bytes()

    def close(self) -> None:
        if self.zf:
            self.zf.close()
        if self.gma:
            self.gma.close()


@dataclass
class Bone:
    index: int
    name: str
    parent: int
    pos: tuple[float, float, float]
    rot: tuple[float, float, float]
    quat: tuple[float, float, float, float]
    pose_to_bone: tuple[float, ...]


@dataclass
class SceneBone:
    index: int
    name: str
    parent: int
    translation: tuple[float, float, float]
    rotation: tuple[float, float, float, float]
    inverse_bind_matrix: tuple[float, ...]  # glTF column-major MAT4


@dataclass
class SceneVertex:
    position: tuple[float, float, float]
    normal: tuple[float, float, float]
    uv: tuple[float, float]
    joints: tuple[int, int, int, int]
    weights: tuple[float, float, float, float]


@dataclass
class ScenePrimitive:
    material: str
    indices: list[int]


@dataclass
class ModelScene:
    name: str
    bones: list[SceneBone]
    vertices: list[SceneVertex]
    primitives: list[ScenePrimitive]
    source_vertex_indices: list[int]
    bodygroups: list[dict]
    source_model: str
    source_archive: Optional[dict]
    lod: int
    skin: int
    warnings: list[str]


@dataclass
class MeshInfo:
    index: int
    skin_ref: int
    material_name: str
    vertex_count: int
    vertex_offset: int


@dataclass
class ModelInfo:
    index: int
    name: str
    vertex_count: int
    vertex_byte_offset: int
    meshes: list[MeshInfo]


@dataclass
class BodyPartInfo:
    index: int
    name: str
    base: int
    models: list[ModelInfo]


@dataclass
class VVDVertex:
    weights: tuple[float, float, float]
    bones: tuple[int, int, int]
    num_bones: int
    pos: tuple[float, float, float]
    normal: tuple[float, float, float]
    uv: tuple[float, float]


class SourceModel:
    def __init__(self, bundle: AssetBundle, vtx_extension: str = ".dx90.vtx"):
        self.bundle = bundle
        self.mdl_bytes = bundle.read(".mdl")
        self.vvd_bytes = bundle.read(".vvd")
        self.vtx_bytes = bundle.read(vtx_extension)
        self.mdl = Binary(self.mdl_bytes, "MDL")
        self.vvd = Binary(self.vvd_bytes, "VVD")
        self.vtx = Binary(self.vtx_bytes, "VTX")
        self.warnings: list[str] = []
        self._parse_headers()
        self.texture_names, self.cdtexture_dirs, self.skin_families = self._parse_materials()
        self.bones = self._parse_bones()
        self.bodyparts = self._parse_bodyparts()
        self.vertices = self._parse_vvd_vertices()

    def _parse_headers(self) -> None:
        if self.mdl_bytes[:4] != b"IDST":
            raise ReaderError("MDL signature is not IDST")
        self.mdl_version = self.mdl.i32(4, "MDL version")
        if self.mdl_version != 48:
            raise ReaderError(f"This alpha supports MDL v48 only; found v{self.mdl_version}")
        self.checksum = self.mdl.u32(8, "MDL checksum")
        self.mdl_name = self.mdl.cstring(12, 64)
        self.mdl_length = self.mdl.i32(76, "MDL length")
        if self.mdl_length > len(self.mdl_bytes) or self.mdl_length < 256:
            self.warnings.append(f"MDL header length field ({self.mdl_length}) differs from file size ({len(self.mdl_bytes)})")
        self.num_bones = self.mdl.i32(156)
        self.bone_index = self.mdl.i32(160)
        self.num_textures = self.mdl.i32(204)
        self.texture_index = self.mdl.i32(208)
        self.num_cdtextures = self.mdl.i32(212)
        self.cdtexture_index = self.mdl.i32(216)
        self.num_skinrefs = self.mdl.i32(220)
        self.num_skin_families = self.mdl.i32(224)
        self.skin_index = self.mdl.i32(228)
        self.num_bodyparts = self.mdl.i32(232)
        self.bodypart_index = self.mdl.i32(236)
        if not (0 <= self.num_bones <= 4096):
            raise ReaderError(f"Implausible bone count: {self.num_bones}")
        if not (0 <= self.num_bodyparts <= 1024):
            raise ReaderError(f"Implausible bodypart count: {self.num_bodyparts}")

        if self.vvd_bytes[:4] != b"IDSV":
            raise ReaderError("VVD signature is not IDSV")
        self.vvd_version = self.vvd.i32(4)
        self.vvd_checksum = self.vvd.u32(8)
        self.vvd_num_lods = self.vvd.i32(12)
        self.vvd_lod_counts = self.vvd.unpack("<8i", 16, "VVD LOD counts")
        self.vvd_num_fixups = self.vvd.i32(48)
        self.vvd_fixup_start = self.vvd.i32(52)
        self.vvd_vertex_start = self.vvd.i32(56)
        self.vvd_tangent_start = self.vvd.i32(60)
        if self.vvd_version != 4:
            raise ReaderError(f"This alpha supports VVD v4 only; found v{self.vvd_version}")
        if self.vvd_num_lods < 1 or self.vvd_num_lods > 8:
            raise ReaderError(f"Invalid VVD LOD count: {self.vvd_num_lods}")
        self.vvd_vertex_count = self.vvd_lod_counts[0]
        if self.vvd_vertex_count < 0 or self.vvd_vertex_count > 5_000_000:
            raise ReaderError(f"Implausible VVD vertex count: {self.vvd_vertex_count}")
        if self.vvd_num_fixups:
            self.warnings.append(
                f"VVD contains {self.vvd_num_fixups} fixup records. This alpha currently reads the stored vertex pool directly; non-zero-fixup LOD remapping is not yet applied."
            )

        if len(self.vtx_bytes) < 36:
            raise ReaderError("VTX header is truncated")
        self.vtx_version = self.vtx.i32(0)
        self.vtx_checksum = self.vtx.u32(16)
        self.vtx_num_lods = self.vtx.i32(20)
        self.vtx_num_bodyparts = self.vtx.i32(28)
        self.vtx_bodypart_offset = self.vtx.i32(32)
        if self.vtx_version != 7:
            raise ReaderError(f"This alpha supports VTX v7 only; found v{self.vtx_version}")
        if self.vtx_num_lods < 1 or self.vtx_num_lods > 8:
            raise ReaderError(f"Invalid VTX LOD count: {self.vtx_num_lods}")
        if self.checksum != self.vvd_checksum or self.checksum != self.vtx_checksum:
            raise ReaderError(
                f"Checksum mismatch: MDL={self.checksum}, VVD={self.vvd_checksum}, VTX={self.vtx_checksum}"
            )
        if self.num_bodyparts != self.vtx_num_bodyparts:
            self.warnings.append(f"MDL bodyparts={self.num_bodyparts}, VTX bodyparts={self.vtx_num_bodyparts}")

    def _parse_bones(self) -> list[Bone]:
        result = []
        for i in range(self.num_bones):
            o = self.bone_index + i * BONE_SIZE
            name_rel = self.mdl.i32(o, f"bone[{i}].name offset")
            name = self.mdl.cstring(o + name_rel) if name_rel else f"bone_{i}"
            parent = self.mdl.i32(o + 4)
            pos = self.mdl.unpack("<3f", o + 32, f"bone[{i}].position")
            quat = self.mdl.unpack("<4f", o + 44, f"bone[{i}].quaternion")
            rot = self.mdl.unpack("<3f", o + 60, f"bone[{i}].rotation")
            pose_to_bone = self.mdl.unpack("<12f", o + 96, f"bone[{i}].poseToBone")
            if parent < -1 or parent >= self.num_bones or parent == i:
                self.warnings.append(f"Bone {i} ({name}) has invalid parent {parent}; exporting it as a root")
                parent = -1
            result.append(Bone(i, name or f"bone_{i}", parent, tuple(pos), tuple(rot), tuple(quat), tuple(pose_to_bone)))
        return result

    def _parse_materials(self):
        textures: list[str] = []
        for i in range(self.num_textures):
            o = self.texture_index + i * 64
            rel = self.mdl.i32(o, f"texture[{i}].name offset")
            textures.append(self.mdl.cstring(o + rel) if rel else f"texture_{i}")
        cd_dirs = []
        for i in range(self.num_cdtextures):
            # studiohdr stores absolute file offsets in this table in the supported v48 layout.
            absolute = self.mdl.i32(self.cdtexture_index + i * 4, f"cdtexture[{i}]")
            cd_dirs.append(self.mdl.cstring(absolute) if 0 <= absolute < len(self.mdl_bytes) else "")
        if self.num_skinrefs > 0 and self.num_skin_families > 0:
            self.mdl.check(self.skin_index, self.num_skinrefs * self.num_skin_families * 2, "skin reference table")
            skins = [
                self.mdl.unpack("<" + "h" * self.num_skinrefs, self.skin_index + f * self.num_skinrefs * 2, f"skin family {f}")
                for f in range(self.num_skin_families)
            ]
        else:
            skins = []
        return textures, cd_dirs, skins

    def _material_for_skinref(self, skin_ref: int, skin: int) -> str:
        tex_index = skin_ref
        if self.skin_families:
            if not 0 <= skin < len(self.skin_families):
                raise ReaderError(f"Skin family {skin} is out of range (0..{len(self.skin_families)-1})")
            row = self.skin_families[skin]
            if 0 <= skin_ref < len(row):
                tex_index = row[skin_ref]
        tex = self.texture_names[tex_index] if 0 <= tex_index < len(self.texture_names) else f"material_{skin_ref}"
        if self.cdtexture_dirs and self.cdtexture_dirs[0]:
            prefix = self.cdtexture_dirs[0].replace("\\", "/").strip("/")
            return f"{prefix}/{tex}".replace("//", "/")
        return tex

    def _parse_bodyparts(self) -> list[BodyPartInfo]:
        result: list[BodyPartInfo] = []
        for bi in range(self.num_bodyparts):
            bo = self.bodypart_index + bi * 16
            name_rel, nmodels, base, model_rel = self.mdl.unpack("<4i", bo, f"bodypart[{bi}]")
            bodypart_name = self.mdl.cstring(bo + name_rel) if name_rel else f"bodypart_{bi}"
            if nmodels < 0 or nmodels > 4096:
                raise ReaderError(f"Bodypart {bi} has implausible model count {nmodels}")
            models: list[ModelInfo] = []
            for mi in range(nmodels):
                mo = bo + model_rel + mi * MODEL_SIZE
                model_name = self.mdl.cstring(mo, 64)  # model name is an inline 64-byte buffer
                model_name = model_name or f"model_{mi}"
                nmeshes = self.mdl.i32(mo + 72)
                mesh_rel = self.mdl.i32(mo + 76)
                numvertices = self.mdl.i32(mo + 80)
                vertex_byte_offset = self.mdl.i32(mo + 84)
                if nmeshes < 0 or nmeshes > 100_000:
                    raise ReaderError(f"Model {name} has implausible mesh count {nmeshes}")
                meshes: list[MeshInfo] = []
                for mj in range(nmeshes):
                    me = mo + mesh_rel + mj * MESH_SIZE
                    skin_ref = self.mdl.i32(me)
                    mesh_numverts = self.mdl.i32(me + 8)
                    vertex_offset = self.mdl.i32(me + 12)
                    mat_name = self._material_for_skinref(skin_ref, 0)
                    meshes.append(MeshInfo(mj, skin_ref, mat_name, mesh_numverts, vertex_offset))
                models.append(ModelInfo(mi, model_name, numvertices, vertex_byte_offset, meshes))
            result.append(BodyPartInfo(bi, bodypart_name, base, models))
        return result

    def _parse_vvd_vertices(self) -> list[VVDVertex]:
        self.vvd.check(self.vvd_vertex_start, self.vvd_vertex_count * VERTEX_SIZE, "VVD vertex buffer")
        out: list[VVDVertex] = []
        for i in range(self.vvd_vertex_count):
            o = self.vvd_vertex_start + i * VERTEX_SIZE
            weights = self.vvd.unpack("<3f", o, f"VVD vertex[{i}] weights")
            bones = self.vvd.unpack("<3B", o + 12, f"VVD vertex[{i}] bone ids")
            num_bones = self.vvd.u8(o + 15)
            pos = self.vvd.unpack("<3f", o + 16, f"VVD vertex[{i}] position")
            normal = self.vvd.unpack("<3f", o + 28, f"VVD vertex[{i}] normal")
            uv = self.vvd.unpack("<2f", o + 40, f"VVD vertex[{i}] uv")
            if num_bones > 3:
                raise ReaderError(f"VVD vertex {i} has {num_bones} influences; Source v4 stores at most 3")
            out.append(VVDVertex(tuple(weights), tuple(bones), num_bones, tuple(pos), tuple(normal), tuple(uv)))
        return out

    def _vtx_bodypart(self, index: int) -> tuple[int, int]:
        o = self.vtx_bodypart_offset + index * 8
        return self.vtx.unpack("<2i", o, f"VTX bodypart[{index}]")

    def _vtx_model(self, bodypart_offset: int, model_index: int) -> tuple[int, int]:
        o = bodypart_offset + model_index * 8
        return self.vtx.unpack("<2i", o, "VTX model header")

    def _iter_triangles_for_mesh(self, mesh_header: int, model_base_vertex: int,
                                 mesh_vertex_offset: int, mesh_numverts: int,
                                 lod: int) -> Iterable[tuple[int, int, int]]:
        num_groups, groups_rel = self.vtx.unpack("<2i", mesh_header, "VTX mesh header")
        for gi in range(num_groups):
            sg = mesh_header + groups_rel + gi * 25
            num_verts, vert_rel, num_indices, index_rel, num_strips, strip_rel = self.vtx.unpack(
                "<6i", sg, "VTX stripgroup"
            )
            self.vtx.check(sg + vert_rel, num_verts * 9, "VTX stripgroup vertices")
            self.vtx.check(sg + index_rel, num_indices * 2, "VTX stripgroup indices")
            for si in range(num_strips):
                st = sg + strip_rel + si * 27
                strip_indices, strip_index_rel, strip_verts, _strip_vert_rel = self.vtx.unpack("<4i", st, "VTX strip")
                strip_flags = self.vtx.u8(st + 18)
                if strip_indices < 0 or strip_indices > num_indices:
                    raise ReaderError("VTX strip index count exceeds its stripgroup")
                if strip_index_rel < 0 or strip_index_rel + strip_indices > num_indices:
                    raise ReaderError("VTX strip index range exceeds its stripgroup")
                raw_indices = [self.vtx.u16(sg + index_rel + (strip_index_rel + k) * 2) for k in range(strip_indices)]
                mapped: list[int] = []
                for local_index in raw_indices:
                    if local_index >= num_verts:
                        raise ReaderError(f"VTX index {local_index} exceeds stripgroup vertex count {num_verts}")
                    vtx_vertex = sg + vert_rel + local_index * 9
                    orig_mesh_vert = self.vtx.u16(vtx_vertex + 4)
                    if orig_mesh_vert >= mesh_numverts:
                        # Some LODs may carry a reduced mesh vertex set; do not silently corrupt the model.
                        raise ReaderError(
                            f"VTX original mesh vertex {orig_mesh_vert} exceeds MDL mesh vertex count {mesh_numverts}"
                        )
                    global_index = model_base_vertex + mesh_vertex_offset + orig_mesh_vert
                    if global_index < 0 or global_index >= len(self.vertices):
                        raise ReaderError(f"Mapped VVD vertex index {global_index} is out of range")
                    mapped.append(global_index)
                if strip_flags & 0x01:  # STRIP_IS_TRILIST
                    usable = len(mapped) - (len(mapped) % 3)
                    for k in range(0, usable, 3):
                        a, b, c = mapped[k:k + 3]
                        if a != b and b != c and a != c:
                            yield (a, b, c)
                elif strip_flags & 0x02:  # STRIP_IS_TRISTRIP
                    for k in range(len(mapped) - 2):
                        a, b, c = mapped[k:k + 3]
                        if k & 1:
                            a, b = b, a
                        if a != b and b != c and a != c:
                            yield (a, b, c)
                else:
                    self.warnings.append(f"Skipped strip with unknown primitive flags 0x{strip_flags:02x}")

    def collect_geometry(self, lod: int = 0, skin: int = 0,
                         bodygroup_choices: Optional[dict[int, int]] = None):
        if lod < 0 or lod >= min(self.vtx_num_lods, self.vvd_num_lods):
            raise ReaderError(f"LOD {lod} is unavailable")
        choices = bodygroup_choices or {}
        triangles: list[tuple[str, int, int, int]] = []
        included: list[dict] = []
        for bp in self.bodyparts:
            choice = choices.get(bp.index, 0)
            if choice < 0 or choice >= len(bp.models):
                raise ReaderError(f"BodyGroup {bp.index} ({bp.name}) choice {choice} is out of range")
            if not bp.models:
                continue
            model = bp.models[choice]
            vbp_num_models, vbp_model_rel = self._vtx_bodypart(bp.index)
            if choice >= vbp_num_models:
                raise ReaderError(f"VTX has no model {choice} for BodyGroup {bp.index} ({bp.name})")
            vmodel = self.vtx_bodypart_offset + bp.index * 8 + vbp_model_rel + choice * 8
            num_lods, lod_rel = self.vtx.unpack("<2i", vmodel, "VTX model")
            if lod >= num_lods:
                self.warnings.append(f"BodyGroup {bp.index} model {choice} has no LOD {lod}; skipped")
                continue
            v_lod = vmodel + lod_rel + lod * 12
            num_vtx_meshes, mesh_rel = self.vtx.unpack("<2i", v_lod, "VTX LOD")
            group_report = {"bodygroup": bp.index, "name": bp.name, "choice": choice,
                            "model": model.name, "mdl_meshes": len(model.meshes),
                            "vtx_meshes": num_vtx_meshes,
                            "triangle_count": 0, "referenced_vertices": 0}
            group_vertices: set[int] = set()
            model_base = model.vertex_byte_offset // VERTEX_SIZE
            if model.vertex_byte_offset % VERTEX_SIZE:
                raise ReaderError(f"Model {model.name} vertex byte offset is not aligned to {VERTEX_SIZE}")
            if len(model.meshes) != num_vtx_meshes:
                # A zero-triangle VTX mesh can still be present for an empty LOD; process matching entries only.
                if not (len(model.meshes) == 0 and num_vtx_meshes > 0):
                    self.warnings.append(
                        f"BodyGroup {bp.index} model {choice}: MDL meshes={len(model.meshes)}, VTX meshes={num_vtx_meshes}"
                    )
            for mj, mesh in enumerate(model.meshes):
                if mj >= num_vtx_meshes:
                    self.warnings.append(f"No corresponding VTX mesh for {bp.name}/{model.name}/mesh{mj}")
                    continue
                v_mesh_header = v_lod + mesh_rel + mj * 9
                material = self._material_for_skinref(mesh.skin_ref, skin)
                for tri in self._iter_triangles_for_mesh(v_mesh_header, model_base,
                                                         mesh.vertex_offset, mesh.vertex_count, lod):
                    triangles.append((material, tri[0], tri[1], tri[2]))
                    group_report["triangle_count"] += 1
                    group_vertices.update(tri)
            group_report["referenced_vertices"] = len(group_vertices)
            included.append(group_report)
        return triangles, included

    def build_scene(self, lod: int = 0, skin: int = 0,
                    bodygroup_choices: Optional[dict[int, int]] = None) -> ModelScene:
        """Build one logical character scene from the selected BodyGroup model variants.

        The scene shares one skeleton and one vertex pool. Material boundaries remain
        separate glTF primitives; the result is still a single skinned character mesh.
        """
        triangles, included = self.collect_geometry(lod, skin, bodygroup_choices)
        if not triangles:
            raise ReaderError("No triangles were reconstructed. Check the selected BodyGroups and LOD.")

        # Preserve first-use order so the exported vertex buffer is deterministic.
        source_to_local: dict[int, int] = {}
        source_indices: list[int] = []
        primitive_map: dict[str, list[int]] = {}
        for material, a, b, c in triangles:
            out_indices = primitive_map.setdefault(material, [])
            for source_index in (a, b, c):
                local_index = source_to_local.get(source_index)
                if local_index is None:
                    local_index = len(source_indices)
                    source_to_local[source_index] = local_index
                    source_indices.append(source_index)
                out_indices.append(local_index)

        scene_vertices: list[SceneVertex] = []
        for source_index in source_indices:
            vertex = self.vertices[source_index]
            pos = _source_to_gltf_vec3(vertex.pos)
            normal = _source_to_gltf_vec3(vertex.normal)
            joints: list[int] = []
            weights: list[float] = []
            for influence in range(vertex.num_bones):
                bone_id = int(vertex.bones[influence])
                weight = float(vertex.weights[influence])
                if weight <= 1e-8:
                    continue
                if bone_id < 0 or bone_id >= len(self.bones):
                    raise ReaderError(f"VVD vertex {source_index} references invalid bone {bone_id}")
                joints.append(bone_id)
                weights.append(weight)
            total = sum(weights)
            if not weights or total <= 1e-8:
                joints, weights = [0], [1.0]
            else:
                weights = [w / total for w in weights]
            while len(joints) < 4:
                joints.append(0)
                weights.append(0.0)
            if not all(math.isfinite(x) for x in (*pos, *normal, *vertex.uv, *weights)):
                raise ReaderError(f"VVD vertex {source_index} contains a non-finite value")
            scene_vertices.append(SceneVertex(pos, normal, tuple(vertex.uv), tuple(joints[:4]), tuple(weights[:4])))

        scene_bones: list[SceneBone] = []
        for bone in self.bones:
            q = _source_to_gltf_quat(bone.quat)
            translation = _source_to_gltf_vec3(bone.pos)
            inv_bind = _source_to_gltf_matrix(_source_3x4_to_mat4(bone.pose_to_bone))
            if not all(math.isfinite(x) for x in (*q, *translation, *inv_bind)):
                raise ReaderError(f"Bone {bone.index} ({bone.name}) contains a non-finite transform")
            scene_bones.append(SceneBone(bone.index, bone.name, bone.parent, translation, q, inv_bind))

        _validate_bone_hierarchy(scene_bones, self.warnings)
        _validate_bind_pose(scene_bones, self.warnings)
        primitives = [ScenePrimitive(material, indices) for material, indices in primitive_map.items()]
        return ModelScene(
            name=Path(self.mdl_name).stem or "SourceModel",
            bones=scene_bones,
            vertices=scene_vertices,
            primitives=primitives,
            source_vertex_indices=source_indices,
            bodygroups=included,
            source_model=self.bundle.mdl_display,
            source_archive=self.bundle.archive_info,
            lod=lod,
            skin=skin,
            warnings=list(dict.fromkeys(self.warnings)),
        )

    def write_glb(self, output: Path, lod: int = 0, skin: int = 0,
                  bodygroup_choices: Optional[dict[int, int]] = None) -> dict:
        scene = self.build_scene(lod, skin, bodygroup_choices)
        report = write_glb_file(scene, output)
        report["warnings"] = list(dict.fromkeys(scene.warnings + report.get("warnings", [])))
        output.with_suffix(".report.json").write_text(
            json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
        )
        return report

    def inspect_dict(self) -> dict:
        return {
            "tool_version": VERSION,
            "model": self.bundle.mdl_display,
            "archive": self.bundle.archive_info,
            "mdl": {"version": self.mdl_version, "name": self.mdl_name, "checksum": self.checksum,
                    "bytes": len(self.mdl_bytes), "bones": self.num_bones, "bodyparts": self.num_bodyparts,
                    "textures": self.num_textures, "skin_families": self.num_skin_families},
            "vvd": {"version": self.vvd_version, "checksum": self.vvd_checksum,
                    "lod_count": self.vvd_num_lods, "lod0_vertices": self.vvd_vertex_count,
                    "fixups": self.vvd_num_fixups},
            "vtx": {"version": self.vtx_version, "checksum": self.vtx_checksum,
                    "lod_count": self.vtx_num_lods, "bodyparts": self.vtx_num_bodyparts},
            "bodygroups": [
                {"index": bp.index, "name": bp.name, "base": bp.base,
                 "models": [{"index": m.index, "name": m.name, "vertices": m.vertex_count,
                             "vertex_byte_offset": m.vertex_byte_offset,
                             "meshes": [{"index": x.index, "skin_ref": x.skin_ref,
                                         "material": x.material_name, "vertices": x.vertex_count,
                                         "vertex_offset": x.vertex_offset} for x in m.meshes]}
                            for m in bp.models]}
                for bp in self.bodyparts
            ],
            "warnings": list(dict.fromkeys(self.warnings)),
        }

    def _weighted_bone_usage_for_triangles(self, triangles: list[tuple[str, int, int, int]]) -> dict:
        """Summarize bone IDs directly referenced by unique vertices in a geometry selection."""
        vertex_ids = sorted({i for _, a, b, c in triangles for i in (a, b, c)})
        vertices_by_bone: dict[int, int] = {}
        weight_by_bone: dict[int, float] = {}
        invalid_references = 0
        unweighted_vertices = 0
        for source_index in vertex_ids:
            vertex = self.vertices[source_index]
            influences: list[tuple[int, float]] = []
            for slot in range(max(0, min(int(vertex.num_bones), len(vertex.bones)))):
                bone_id = int(vertex.bones[slot])
                weight = float(vertex.weights[slot])
                if not math.isfinite(weight) or weight <= 1e-8:
                    continue
                if bone_id < 0 or bone_id >= len(self.bones):
                    invalid_references += 1
                    continue
                influences.append((bone_id, weight))
            if not influences:
                unweighted_vertices += 1
                continue
            for bone_id, weight in influences:
                vertices_by_bone[bone_id] = vertices_by_bone.get(bone_id, 0) + 1
                weight_by_bone[bone_id] = weight_by_bone.get(bone_id, 0.0) + weight
        entries = [
            {"index": bone_id, "name": self.bones[bone_id].name,
             "influencing_vertices": vertices_by_bone[bone_id],
             "accumulated_weight": round(weight_by_bone[bone_id], 6)}
            for bone_id in sorted(vertices_by_bone)
        ]
        return {
            "vertex_count": len(vertex_ids),
            "triangle_count": len(triangles),
            "weighted_bone_count": len(vertices_by_bone),
            "weight_referenced_bone_indices": sorted(vertices_by_bone),
            "weight_referenced_bones": entries,
            "unweighted_vertices": unweighted_vertices,
            "invalid_bone_references": invalid_references,
        }

    def bone_dependency_matrix(self, lod: int = 0, skin: int = 0) -> dict:
        """Compare each BodyGroup option against the all-default selection.

        For each option, all other BodyGroups remain at choice 0. The reported bone
        dependencies are strictly vertex-weight references in the selected geometry;
        they are not claims about animation, procedural, physics, or runtime needs.
        """
        baseline_choices: dict[int, int] = {}
        baseline_triangles, baseline_included = self.collect_geometry(lod, skin, baseline_choices)
        if not baseline_triangles:
            raise ReaderError("The all-default BodyGroup selection contains no triangles")
        baseline_usage = self._weighted_bone_usage_for_triangles(baseline_triangles)
        baseline_bones = set(baseline_usage["weight_referenced_bone_indices"])

        bodygroup_rows: list[dict] = []
        total_options = 0
        for bp in self.bodyparts:
            options: list[dict] = []
            for option in bp.models:
                total_options += 1
                choices = {bp.index: option.index}
                triangles, included = self.collect_geometry(lod, skin, choices)
                usage = self._weighted_bone_usage_for_triangles(triangles)
                current_bones = set(usage["weight_referenced_bone_indices"])
                added_ids = sorted(current_bones - baseline_bones)
                removed_ids = sorted(baseline_bones - current_bones)
                added = [{"index": i, "name": self.bones[i].name} for i in added_ids]
                removed = [{"index": i, "name": self.bones[i].name} for i in removed_ids]
                options.append({
                    "option_index": option.index,
                    "option_name": option.name,
                    "is_default_option": option.index == 0,
                    "geometry": {
                        "triangles": usage["triangle_count"],
                        "referenced_vertices": usage["vertex_count"],
                    },
                    "weight_references": {
                        "weighted_bone_count": usage["weighted_bone_count"],
                        "unweighted_vertices": usage["unweighted_vertices"],
                        "invalid_bone_references": usage["invalid_bone_references"],
                        "bones": usage["weight_referenced_bones"],
                    },
                    "delta_vs_all_default": {
                        "added_count": len(added), "removed_count": len(removed),
                        "added": added, "removed": removed,
                    },
                    "selection_coverage": included,
                })
            bodygroup_rows.append({
                "bodygroup_index": bp.index,
                "bodygroup_name": bp.name,
                "default_option_index": 0,
                "option_count": len(options),
                "options": options,
            })

        return {
            "tool_version": VERSION,
            "source_model": self.bundle.mdl_display,
            "source_archive": self.bundle.archive_info,
            "comparison_basis": {
                "lod": lod,
                "skin_family": skin,
                "baseline": "all BodyGroups at option 0",
                "per_option": "one BodyGroup option selected; all other BodyGroups remain at option 0",
                "meaning": "bone dependencies count unique MDL bone indices directly referenced by positive vertex weights in the selected geometry",
                "not_inferred": ["animation dependencies", "procedural/jiggle-bone dependencies", "physics dependencies", "runtime engine usage"],
            },
            "skeleton_definition": {
                "mdl_bone_definition_count": len(self.bones),
                "root_count": sum(1 for b in self.bones if b.parent == -1),
                "bone_definitions": [{
                    "index": b.index, "name": b.name, "parent_index": b.parent,
                    "parent_name": self.bones[b.parent].name if 0 <= b.parent < len(self.bones) else None,
                } for b in self.bones],
            },
            "baseline": {
                "geometry": {"triangles": baseline_usage["triangle_count"],
                             "referenced_vertices": baseline_usage["vertex_count"]},
                "weight_references": baseline_usage,
            },
            "summary": {
                "bodygroup_count": len(self.bodyparts),
                "option_count": total_options,
                "mdl_bone_definition_count": len(self.bones),
                "baseline_weight_referenced_bone_count": len(baseline_bones),
            },
            "bodygroups": bodygroup_rows,
        }

    def diagnose(self, lod: int = 0, skin: int = 0,
                 bodygroup_choices: Optional[dict[int, int]] = None) -> dict:
        """Run structural, skeleton, geometry, and skin-weight diagnostics.

        Diagnostics are performed against the selected BodyGroup/LOD geometry while
        also reporting how much of the stored VVD vertex pool is not referenced by
        that selection. Bone records are reported as MDL definitions; weight references
        are counted separately and are not used to infer a bone's semantic/runtime role.
        """
        triangles, included = self.collect_geometry(lod, skin, bodygroup_choices)
        if not triangles:
            raise ReaderError("No triangles were reconstructed; diagnostics cannot inspect an empty selection")
        selected_indices = sorted({i for _, a, b, c in triangles for i in (a, b, c)})
        selected_set = set(selected_indices)

        def vertex_stats(indices: Iterable[int]) -> dict:
            population = list(indices)
            influence_histogram = {str(i): 0 for i in range(4)}
            weight_sums: list[float] = []
            bone_use_vertices: dict[int, int] = {}
            bone_use_weight: dict[int, float] = {}
            invalid_bone_refs = 0
            zero_or_negative_weights = 0
            unweighted_vertices = 0
            non_finite_positions = 0
            non_finite_normals = 0
            non_finite_uvs = 0
            negative_weights = 0
            out_of_range_weights = 0
            for source_index in population:
                v = self.vertices[source_index]
                effective: list[tuple[int, float]] = []
                for slot in range(v.num_bones):
                    bone_id = int(v.bones[slot])
                    weight = float(v.weights[slot])
                    if not math.isfinite(weight):
                        zero_or_negative_weights += 1
                        continue
                    if weight < 0.0:
                        negative_weights += 1
                    if weight > 1.0 + 1e-5:
                        out_of_range_weights += 1
                    if weight <= 1e-8:
                        zero_or_negative_weights += 1
                        continue
                    if bone_id < 0 or bone_id >= len(self.bones):
                        invalid_bone_refs += 1
                        continue
                    effective.append((bone_id, weight))
                influence_histogram[str(min(len(effective), 3))] += 1
                total = sum(w for _, w in effective)
                if not effective or total <= 1e-8:
                    unweighted_vertices += 1
                else:
                    weight_sums.append(total)
                    for bone_id, weight in effective:
                        bone_use_vertices[bone_id] = bone_use_vertices.get(bone_id, 0) + 1
                        bone_use_weight[bone_id] = bone_use_weight.get(bone_id, 0.0) + weight
                if not all(math.isfinite(float(x)) for x in v.pos):
                    non_finite_positions += 1
                if not all(math.isfinite(float(x)) for x in v.normal):
                    non_finite_normals += 1
                if not all(math.isfinite(float(x)) for x in v.uv):
                    non_finite_uvs += 1
            deviations = [abs(x - 1.0) for x in weight_sums]
            bones_by_weight = sorted(
                ({"index": index, "name": self.bones[index].name,
                  "vertices": bone_use_vertices.get(index, 0),
                  "accumulated_weight": round(bone_use_weight.get(index, 0.0), 6)}
                 for index in bone_use_vertices),
                key=lambda x: (-x["vertices"], x["index"])
            )
            return {
                "vertex_count": len(population),
                "effective_influence_histogram": influence_histogram,
                "unweighted_vertices": unweighted_vertices,
                "invalid_bone_references": invalid_bone_refs,
                "zero_or_negative_weight_slots": zero_or_negative_weights,
                "negative_weight_slots": negative_weights,
                "weights_above_one_slots": out_of_range_weights,
                "weight_sum": {
                    "sample_count": len(weight_sums),
                    "min": min(weight_sums) if weight_sums else None,
                    "max": max(weight_sums) if weight_sums else None,
                    "mean": (sum(weight_sums) / len(weight_sums)) if weight_sums else None,
                    "outside_1_plus_minus_0_001": sum(1 for d in deviations if d > 1e-3),
                    "max_abs_deviation_from_one": max(deviations) if deviations else None,
                },
                "non_finite_positions": non_finite_positions,
                "non_finite_normals": non_finite_normals,
                "non_finite_uvs": non_finite_uvs,
                "bone_usage": bones_by_weight,
            }

        # Inspect all stored vertices and the subset actually referenced by selected triangles.
        all_vertex_stats = vertex_stats(range(len(self.vertices)))
        active_vertex_stats = vertex_stats(selected_indices)

        # Validate hierarchy cycles and collect parent/child structure.
        child_counts = [0] * len(self.bones)
        child_indices: list[list[int]] = [[] for _ in self.bones]
        invalid_parents = []
        duplicate_names: dict[str, list[int]] = {}
        for bone in self.bones:
            duplicate_names.setdefault(bone.name, []).append(bone.index)
            if bone.parent < -1 or bone.parent >= len(self.bones) or bone.parent == bone.index:
                invalid_parents.append({"bone": bone.index, "name": bone.name, "parent": bone.parent})
            elif bone.parent >= 0:
                child_counts[bone.parent] += 1
                child_indices[bone.parent].append(bone.index)
        cycles: list[list[int]] = []
        globally_done: set[int] = set()
        for start in range(len(self.bones)):
            path: list[int] = []
            local_pos: dict[int, int] = {}
            current = start
            while current >= 0 and current < len(self.bones) and current not in globally_done:
                if current in local_pos:
                    cycles.append(path[local_pos[current]:])
                    break
                local_pos[current] = len(path)
                path.append(current)
                current = self.bones[current].parent
            globally_done.update(path)

        used_bone_ids = {x["index"] for x in active_vertex_stats["bone_usage"]}
        bones_not_weight_referenced = [
            {"index": b.index, "name": b.name, "parent": b.parent, "children": child_counts[b.index]}
            for b in self.bones if b.index not in used_bone_ids
        ]
        duplicate_bone_names = [
            {"name": name, "indices": ids}
            for name, ids in duplicate_names.items() if len(ids) > 1
        ]

        non_finite_bone_transforms = 0
        quaternion_norms: list[float] = []
        scene_bones_for_validation: list[SceneBone] = []
        for bone in self.bones:
            values = (*bone.pos, *bone.rot, *bone.quat, *bone.pose_to_bone)
            if not all(math.isfinite(float(x)) for x in values):
                non_finite_bone_transforms += 1
                continue
            quaternion_norms.append(math.sqrt(sum(float(x) * float(x) for x in bone.quat)))
            scene_bones_for_validation.append(SceneBone(
                bone.index, bone.name, bone.parent,
                _source_to_gltf_vec3(bone.pos), _source_to_gltf_quat(bone.quat),
                _source_to_gltf_matrix(_source_3x4_to_mat4(bone.pose_to_bone)),
            ))
        bind_pose_max_error: Optional[float] = None
        bind_pose_validation_warnings: list[str] = []
        if len(scene_bones_for_validation) == len(self.bones):
            _validate_bone_hierarchy(scene_bones_for_validation, bind_pose_validation_warnings)
            bind_pose_max_error = _validate_bind_pose(scene_bones_for_validation, bind_pose_validation_warnings)

        bone_use_map = {x["index"]: x for x in active_vertex_stats["bone_usage"]}
        bone_depths: list[Optional[int]] = [None] * len(self.bones)
        for bone in self.bones:
            if bone_depths[bone.index] is not None:
                continue
            chain: list[int] = []
            current = bone.index
            seen: set[int] = set()
            while current >= 0 and current < len(self.bones) and bone_depths[current] is None and current not in seen:
                seen.add(current)
                chain.append(current)
                current = self.bones[current].parent
            base_depth = bone_depths[current] + 1 if 0 <= current < len(self.bones) and bone_depths[current] is not None else 0
            for index in reversed(chain):
                bone_depths[index] = base_depth
                base_depth += 1
        bone_table = []
        for bone in self.bones:
            usage = bone_use_map.get(bone.index, {})
            parent_name = self.bones[bone.parent].name if 0 <= bone.parent < len(self.bones) else None
            bone_table.append({
                "index": bone.index, "name": bone.name,
                "parent_index": bone.parent, "parent_name": parent_name,
                "children": child_indices[bone.index], "depth": bone_depths[bone.index],
                "influencing_vertices": usage.get("vertices", 0),
                "accumulated_weight": usage.get("accumulated_weight", 0.0),
                "referenced_by_selected_geometry_weights": bone.index in used_bone_ids,
            })

        selected_by_index = {int(item["bodygroup"]): int(item["choice"]) for item in included}
        bodygroup_catalog = []
        for bp in self.bodyparts:
            options = []
            for model_option in bp.models:
                options.append({
                    "index": model_option.index,
                    "name": model_option.name,
                    "vertex_count": model_option.vertex_count,
                    "mesh_count": len(model_option.meshes),
                    "meshes": [{
                        "index": mesh.index, "material": mesh.material_name,
                        "vertex_count": mesh.vertex_count, "vertex_offset": mesh.vertex_offset,
                    } for mesh in model_option.meshes],
                })
            bodygroup_catalog.append({
                "index": bp.index, "name": bp.name, "base": bp.base,
                "selected_choice": selected_by_index.get(bp.index, 0),
                "option_count": len(options), "options": options,
            })

        # Geometry sanity: bounds and zero-area faces in Source-space coordinates.
        pos_values = [self.vertices[i].pos for i in selected_indices]
        bounds = {
            "min": [min(p[k] for p in pos_values) for k in range(3)],
            "max": [max(p[k] for p in pos_values) for k in range(3)],
        }
        degenerate_triangles = 0
        for _, a, b, c in triangles:
            pa, pb, pc = self.vertices[a].pos, self.vertices[b].pos, self.vertices[c].pos
            ab = (pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2])
            ac = (pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2])
            cross = (ab[1] * ac[2] - ab[2] * ac[1],
                     ab[2] * ac[0] - ab[0] * ac[2],
                     ab[0] * ac[1] - ab[1] * ac[0])
            if sum(x * x for x in cross) <= 1e-20:
                degenerate_triangles += 1

        warnings = list(dict.fromkeys(self.warnings))
        errors: list[dict] = []
        findings: list[dict] = []
        def add_finding(severity: str, code: str, message: str) -> None:
            findings.append({"severity": severity, "code": code, "message": message})
        if invalid_parents:
            errors.append({"code": "invalid_bone_parent", "count": len(invalid_parents), "items": invalid_parents})
            add_finding("error", "invalid_bone_parent", f"{len(invalid_parents)} bone(s) have invalid parent indices")
        if cycles:
            errors.append({"code": "bone_hierarchy_cycle", "count": len(cycles), "cycles": cycles})
            add_finding("error", "bone_hierarchy_cycle", f"{len(cycles)} cycle(s) exist in the bone hierarchy")
        if active_vertex_stats["invalid_bone_references"]:
            add_finding("error", "invalid_active_bone_reference",
                        f"{active_vertex_stats['invalid_bone_references']} active weight slot(s) reference invalid bones")
        if active_vertex_stats["unweighted_vertices"]:
            add_finding("warning", "unweighted_active_vertices",
                        f"{active_vertex_stats['unweighted_vertices']} active vertex/vertices have no valid positive influence")
        if active_vertex_stats["weight_sum"]["outside_1_plus_minus_0_001"]:
            add_finding("warning", "weight_sum_deviation",
                        f"{active_vertex_stats['weight_sum']['outside_1_plus_minus_0_001']} active vertex/vertices have raw weights outside 1 ± 0.001")
        if active_vertex_stats["negative_weight_slots"] or active_vertex_stats["weights_above_one_slots"]:
            add_finding("warning", "unusual_weight_values", "Active vertices contain negative or greater-than-one weight slots")
        if (active_vertex_stats["non_finite_positions"] or active_vertex_stats["non_finite_normals"] or
                active_vertex_stats["non_finite_uvs"]):
            add_finding("error", "non_finite_vertex_attributes", "Active geometry contains non-finite position, normal, or UV values")
        if duplicate_bone_names:
            add_finding("warning", "duplicate_bone_names", f"{len(duplicate_bone_names)} bone name(s) are duplicated")
        if non_finite_bone_transforms:
            add_finding("error", "non_finite_bone_transforms",
                        f"{non_finite_bone_transforms} bone(s) contain non-finite transform values")
        non_unit_quaternions = sum(1 for value in quaternion_norms if abs(value - 1.0) > 1e-3)
        if non_unit_quaternions:
            add_finding("warning", "non_unit_bone_quaternions",
                        f"{non_unit_quaternions} bone quaternion(s) differ from unit length by more than 0.001")
        if bind_pose_max_error is not None and bind_pose_max_error > 1e-3:
            add_finding("warning", "bind_pose_matrix_mismatch",
                        f"Bind-pose matrix consistency maximum error is {bind_pose_max_error:.6g}")
        for warning in bind_pose_validation_warnings:
            if "bind-pose consistency check" not in warning and not (cycles and "cycle detected" in warning):
                add_finding("warning", "skeleton_validation_warning", warning)
        if bones_not_weight_referenced:
            add_finding(
                "info", "unreferenced_bone_definitions",
                f"{len(bones_not_weight_referenced)} MDL bone definition(s) are not directly referenced by the selected geometry's vertex weights; this does not classify their runtime role"
            )
        if degenerate_triangles:
            add_finding("warning", "degenerate_triangles", f"{degenerate_triangles} triangle(s) have near-zero geometric area")
        if len(selected_indices) < self.vvd_vertex_count:
            add_finding("info", "unreferenced_vvd_vertices",
                        f"{self.vvd_vertex_count - len(selected_indices)} stored VVD vertex/vertices are not referenced by this BodyGroup selection")
        for warning in warnings:
            add_finding("warning", "parser_warning", warning)

        bone_report = {
            "count": len(self.bones),
            "root_count": sum(1 for b in self.bones if b.parent == -1),
            "leaf_count": sum(1 for c in child_counts if c == 0),
            "invalid_parents": invalid_parents,
            "hierarchy_cycles": cycles,
            "duplicate_names": duplicate_bone_names,
            "non_finite_transforms": non_finite_bone_transforms,
            "quaternion_norm": {
                "min": min(quaternion_norms) if quaternion_norms else None,
                "max": max(quaternion_norms) if quaternion_norms else None,
                "outside_1_plus_minus_0_001": non_unit_quaternions,
            },
            "bind_pose_max_identity_error": bind_pose_max_error,
            "definition_count": len(self.bones),
            "weight_referenced_bone_count": len(used_bone_ids),
            "defined_but_not_weight_referenced_count": len(bones_not_weight_referenced),
            "defined_but_not_weight_referenced_sample": bones_not_weight_referenced[:100],
            "weight_referenced_bone_indices": sorted(used_bone_ids),
            "usage_top_20": active_vertex_stats["bone_usage"][:20],
            "bone_table": bone_table,
        }
        return {
            "tool_version": VERSION,
            "source_model": self.bundle.mdl_display,
            "source_archive": self.bundle.archive_info,
            "selection": {"lod": lod, "skin_family": skin, "bodygroups": included},
            "summary": {
                "status": "error" if any(x["severity"] == "error" for x in findings) else
                          "warning" if any(x["severity"] == "warning" for x in findings) else "ok",
                "bone_definitions": len(self.bones),
                "triangles": len(triangles),
                "selected_vertices": len(selected_indices),
                "material_count": len({m for m, *_ in triangles}),
                "finding_counts": {severity: sum(1 for x in findings if x["severity"] == severity)
                                   for severity in ("error", "warning", "info")},
            },
            "skeleton": bone_report,
            "vertices": {
                "vvd_stored_vertices": len(self.vertices),
                "selected_referenced_vertices": len(selected_indices),
                "not_referenced_by_selection": len(self.vertices) - len(selected_indices),
                "all_vvd": all_vertex_stats,
                "selected_geometry": active_vertex_stats,
            },
            "geometry": {"triangle_count": len(triangles), "degenerate_triangles": degenerate_triangles,
                         "bounds_source_space": bounds,
                         "bodygroup_coverage": included,
                         "bodygroup_catalog": bodygroup_catalog},
            "findings": findings,
            "parser_warnings": warnings,
        }

    def write_smd(self, output: Path, lod: int = 0, skin: int = 0,
                  bodygroup_choices: Optional[dict[int, int]] = None) -> dict:
        triangles, included = self.collect_geometry(lod, skin, bodygroup_choices)
        if not triangles:
            raise ReaderError("No triangles were reconstructed. Check the chosen BodyGroups and LOD.")
        output.parent.mkdir(parents=True, exist_ok=True)
        with output.open("w", encoding="utf-8", newline="\n") as f:
            f.write("version 1\n\n")
            f.write("nodes\n")
            for bone in self.bones:
                parent = bone.parent
                f.write(f'{bone.index} "{bone.name.replace(chr(34), chr(39))}" {parent}\n')
            f.write("end\n\n")
            f.write("skeleton\n")
            f.write("time 0\n")
            for bone in self.bones:
                x, y, z = bone.pos
                rx, ry, rz = bone.rot
                f.write(f"{bone.index} {x:.9g} {y:.9g} {z:.9g} {rx:.9g} {ry:.9g} {rz:.9g}\n")
            f.write("end\n\n")
            f.write("triangles\n")
            for material, ia, ib, ic in triangles:
                f.write(f"{material}\n")
                for index in (ia, ib, ic):
                    v = self.vertices[index]
                    # SMD supports arbitrary influence counts; Source VVD v4 stores at most three.
                    links = []
                    for j in range(v.num_bones):
                        w = float(v.weights[j])
                        bone_id = int(v.bones[j])
                        if w > 1e-8 and 0 <= bone_id < len(self.bones):
                            links.append((bone_id, w))
                    total = sum(w for _, w in links)
                    if total <= 1e-8:
                        links = [(0, 1.0)]
                    else:
                        links = [(bi, w / total) for bi, w in links]
                    first_bone = links[0][0]
                    px, py, pz = v.pos
                    nx, ny, nz = v.normal
                    u, vv = v.uv
                    f.write(
                        f"{first_bone} {px:.9g} {py:.9g} {pz:.9g} "
                        f"{nx:.9g} {ny:.9g} {nz:.9g} {u:.9g} {vv:.9g} {len(links)}"
                    )
                    for bone_id, weight in links:
                        f.write(f" {bone_id} {weight:.9g}")
                    f.write("\n")
            f.write("end\n")
        report = {
            "output": str(output), "source_model": self.bundle.mdl_display,
            "source_archive": self.bundle.archive_info,
            "lod": lod, "skin": skin,
            "bone_count": len(self.bones), "triangle_count": len(triangles),
            "unique_source_vertices_referenced": len({i for _, a, b, c in triangles for i in (a, b, c)}),
            "bodygroups": included,
            "materials": sorted({m for m, *_ in triangles}),
            "warnings": list(dict.fromkeys(self.warnings)),
        }
        output.with_suffix(".report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return report


def _source_to_gltf_vec3(value: tuple[float, float, float]) -> tuple[float, float, float]:
    """Convert Source's Z-up basis (X, Y, Z) to glTF's Y-up basis (X, Z, -Y)."""
    x, y, z = value
    return (float(x), float(z), -float(y))


def _quat_mul(a: tuple[float, float, float, float],
              b: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    ax, ay, az, aw = a
    bx, by, bz, bw = b
    return (
        aw * bx + ax * bw + ay * bz - az * by,
        aw * by - ax * bz + ay * bw + az * bx,
        aw * bz + ax * by - ay * bx + az * bw,
        aw * bw - ax * bx - ay * by - az * bz,
    )


def _quat_normalize(q: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    length = math.sqrt(sum(float(x) * float(x) for x in q))
    if not math.isfinite(length) or length < 1e-12:
        return (0.0, 0.0, 0.0, 1.0)
    return tuple(float(x) / length for x in q)  # type: ignore[return-value]


def _source_to_gltf_quat(q: tuple[float, float, float, float]) -> tuple[float, float, float, float]:
    # Basis conversion C is a -90 degree rotation about X: (x, y, z) -> (x, z, -y).
    half = math.sqrt(0.5)
    c = (-half, 0.0, 0.0, half)
    c_inv = (half, 0.0, 0.0, half)
    source_q = _quat_normalize(tuple(float(x) for x in q))
    return _quat_normalize(_quat_mul(_quat_mul(c, source_q), c_inv))


def _mat4_identity() -> list[list[float]]:
    return [[1.0 if r == c else 0.0 for c in range(4)] for r in range(4)]


def _mat4_mul(a: list[list[float]], b: list[list[float]]) -> list[list[float]]:
    return [[sum(a[r][k] * b[k][c] for k in range(4)) for c in range(4)] for r in range(4)]


def _source_3x4_to_mat4(values: tuple[float, ...]) -> list[list[float]]:
    if len(values) != 12:
        raise ReaderError("A Source poseToBone matrix must contain 12 values")
    return [list(map(float, values[r * 4:(r + 1) * 4])) for r in range(3)] + [[0.0, 0.0, 0.0, 1.0]]


def _source_to_gltf_matrix(source_matrix: list[list[float]]) -> tuple[float, ...]:
    c = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, -1.0, 0.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
    c_inv = [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 0.0, -1.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]
    converted = _mat4_mul(_mat4_mul(c, source_matrix), c_inv)
    # glTF serializes matrices in column-major order.
    return tuple(converted[r][col] for col in range(4) for r in range(4))


def _quat_to_mat4(q: tuple[float, float, float, float],
                  translation: tuple[float, float, float]) -> list[list[float]]:
    x, y, z, w = _quat_normalize(q)
    matrix = [
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w), translation[0]],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w), translation[1]],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y), translation[2]],
        [0.0, 0.0, 0.0, 1.0],
    ]
    return matrix


def _column_major_to_mat4(values: tuple[float, ...]) -> list[list[float]]:
    return [[float(values[col * 4 + row]) for col in range(4)] for row in range(4)]


def _validate_bone_hierarchy(bones: list[SceneBone], warnings: list[str]) -> None:
    for bone in bones:
        if bone.parent < -1 or bone.parent >= len(bones) or bone.parent == bone.index:
            warnings.append(f"Bone {bone.index} has invalid parent {bone.parent}; exported as a root")
            bone.parent = -1
    # A malformed hierarchy must not create a recursive glTF node graph.
    for start in range(len(bones)):
        path: list[int] = []
        seen: set[int] = set()
        current = start
        while current >= 0:
            if current in seen:
                cycle_start = path.index(current)
                broken = path[cycle_start]
                warnings.append(f"Bone hierarchy cycle detected at bone {broken}; exported as a root")
                bones[broken].parent = -1
                break
            seen.add(current)
            path.append(current)
            current = bones[current].parent


def _validate_bind_pose(bones: list[SceneBone], warnings: list[str]) -> float:
    globals_: list[Optional[list[list[float]]]] = [None] * len(bones)
    def global_for(index: int) -> list[list[float]]:
        cached = globals_[index]
        if cached is not None:
            return cached
        bone = bones[index]
        local = _quat_to_mat4(bone.rotation, bone.translation)
        result = _mat4_mul(global_for(bone.parent), local) if bone.parent >= 0 else local
        globals_[index] = result
        return result
    max_error = 0.0
    for bone in bones:
        product = _mat4_mul(global_for(bone.index), _column_major_to_mat4(bone.inverse_bind_matrix))
        error = max(abs(product[r][c] - (1.0 if r == c else 0.0)) for r in range(4) for c in range(4))
        max_error = max(max_error, error)
    if max_error > 1e-3:
        warnings.append(f"Bone bind-pose consistency check max error is {max_error:.6g}; inspect skeleton transforms")
    return max_error


class _GLBBuilder:
    def __init__(self):
        self.binary = bytearray()
        self.buffer_views: list[dict] = []
        self.accessors: list[dict] = []

    def add_accessor(self, raw: bytes, component_type: int, accessor_type: str,
                     count: int, target: Optional[int] = None,
                     minimum: Optional[list[float | int]] = None,
                     maximum: Optional[list[float | int]] = None) -> int:
        while len(self.binary) % 4:
            self.binary.append(0)
        offset = len(self.binary)
        self.binary.extend(raw)
        view = {"buffer": 0, "byteOffset": offset, "byteLength": len(raw)}
        if target is not None:
            view["target"] = target
        view_index = len(self.buffer_views)
        self.buffer_views.append(view)
        accessor = {
            "bufferView": view_index,
            "byteOffset": 0,
            "componentType": component_type,
            "count": count,
            "type": accessor_type,
        }
        if minimum is not None:
            accessor["min"] = minimum
        if maximum is not None:
            accessor["max"] = maximum
        accessor_index = len(self.accessors)
        self.accessors.append(accessor)
        return accessor_index


def write_glb_file(scene: ModelScene, output: Path, animation_clip: Optional[dict] = None) -> dict:
    if not scene.vertices or not scene.primitives:
        raise ReaderError("Cannot export an empty unified scene")
    if not scene.bones:
        raise ReaderError("Cannot export a skinned GLB without a skeleton")
    output.parent.mkdir(parents=True, exist_ok=True)
    builder = _GLBBuilder()
    vertex_count = len(scene.vertices)

    positions = [v.position for v in scene.vertices]
    normals = [v.normal for v in scene.vertices]
    uvs = [v.uv for v in scene.vertices]
    joints = [v.joints for v in scene.vertices]
    weights = [v.weights for v in scene.vertices]
    position_flat = [x for v in positions for x in v]
    normal_flat = [x for v in normals for x in v]
    uv_flat = [x for v in uvs for x in v]
    joint_flat = [x for v in joints for x in v]
    weight_flat = [x for v in weights for x in v]
    if not all(math.isfinite(float(x)) for x in (*position_flat, *normal_flat, *uv_flat, *weight_flat)):
        raise ReaderError("Unified scene contains non-finite vertex attributes")
    if any(j < 0 or j >= len(scene.bones) for j in joint_flat):
        raise ReaderError("Unified scene contains an out-of-range JOINTS_0 index")

    pos_min = [min(v[k] for v in positions) for k in range(3)]
    pos_max = [max(v[k] for v in positions) for k in range(3)]
    attr_position = builder.add_accessor(struct.pack("<" + "f" * len(position_flat), *position_flat),
                                         5126, "VEC3", vertex_count, 34962, pos_min, pos_max)
    attr_normal = builder.add_accessor(struct.pack("<" + "f" * len(normal_flat), *normal_flat),
                                       5126, "VEC3", vertex_count, 34962)
    attr_uv = builder.add_accessor(struct.pack("<" + "f" * len(uv_flat), *uv_flat),
                                   5126, "VEC2", vertex_count, 34962)
    attr_joints = builder.add_accessor(struct.pack("<" + "H" * len(joint_flat), *joint_flat),
                                       5123, "VEC4", vertex_count, 34962)
    attr_weights = builder.add_accessor(struct.pack("<" + "f" * len(weight_flat), *weight_flat),
                                        5126, "VEC4", vertex_count, 34962)

    material_names: list[str] = []
    for primitive in scene.primitives:
        if primitive.material not in material_names:
            material_names.append(primitive.material)
    material_index = {name: i for i, name in enumerate(material_names)}
    gltf_primitives: list[dict] = []
    triangle_count = 0
    for primitive in scene.primitives:
        if not primitive.indices:
            continue
        if len(primitive.indices) % 3:
            raise ReaderError(f"Primitive {primitive.material!r} index count is not divisible by 3")
        if min(primitive.indices) < 0 or max(primitive.indices) >= vertex_count:
            raise ReaderError(f"Primitive {primitive.material!r} contains an out-of-range vertex index")
        accessor = builder.add_accessor(
            struct.pack("<" + "I" * len(primitive.indices), *primitive.indices),
            5125, "SCALAR", len(primitive.indices), 34963,
            [min(primitive.indices)], [max(primitive.indices)],
        )
        gltf_primitives.append({
            "attributes": {
                "POSITION": attr_position,
                "NORMAL": attr_normal,
                "TEXCOORD_0": attr_uv,
                "JOINTS_0": attr_joints,
                "WEIGHTS_0": attr_weights,
            },
            "indices": accessor,
            "material": material_index[primitive.material],
            "mode": 4,
            "extras": {"source_material": primitive.material,
                       "triangle_count": len(primitive.indices) // 3},
        })
        triangle_count += len(primitive.indices) // 3

    # The synthetic root allows a valid glTF skin when Source has multiple root bones.
    bone_node_offset = 1
    mesh_node_index = bone_node_offset + len(scene.bones)
    children: dict[int, list[int]] = {-1: []}
    for bone in scene.bones:
        children.setdefault(bone.index, [])
        children.setdefault(bone.parent, []).append(bone.index)
    nodes: list[dict] = [{"name": "SourceSkeletonRoot", "children": [bone_node_offset + i for i in children[-1]]}]
    for bone in scene.bones:
        node = {
            "name": bone.name,
            "translation": list(bone.translation),
            "rotation": list(bone.rotation),
        }
        child_ids = children.get(bone.index, [])
        if child_ids:
            node["children"] = [bone_node_offset + child for child in child_ids]
        nodes.append(node)
    nodes.append({"name": scene.name + "_Mesh", "mesh": 0, "skin": 0})

    inverse_bind_flat = [x for bone in scene.bones for x in bone.inverse_bind_matrix]
    inverse_bind_accessor = builder.add_accessor(
        struct.pack("<" + "f" * len(inverse_bind_flat), *inverse_bind_flat),
        5126, "MAT4", len(scene.bones),
    )
    gltf_animations: list[dict] = []
    animation_summary: Optional[dict] = None
    if animation_clip is not None:
        samplers: list[dict] = []
        channels: list[dict] = []
        time_accessor_cache: dict[tuple[float, ...], int] = {}
        for track in animation_clip.get("tracks", []):
            times = [float(value) for value in track["times"]]
            translations = [tuple(float(x) for x in value) for value in track["translations"]]
            rotations = [tuple(float(x) for x in value) for value in track["rotations"]]
            if not times or len(times) != len(translations) or len(times) != len(rotations):
                raise ReaderError(f"Animation track {track.get('bone_name')!r} has inconsistent key counts")
            if any(not math.isfinite(value) or value < 0.0 for value in times):
                raise ReaderError(f"Animation track {track.get('bone_name')!r} has invalid key times")
            if any(times[i + 1] <= times[i] for i in range(len(times) - 1)):
                raise ReaderError(f"Animation track {track.get('bone_name')!r} key times are not strictly increasing")
            if not all(math.isfinite(x) for value in (*translations, *rotations) for x in value):
                raise ReaderError(f"Animation track {track.get('bone_name')!r} has non-finite transform values")
            bone_index = int(track["target_bone_index"])
            if bone_index < 0 or bone_index >= len(scene.bones):
                raise ReaderError(f"Animation track {track.get('bone_name')!r} targets invalid bone index {bone_index}")
            if scene.bones[bone_index].name != track["bone_name"]:
                raise ReaderError(f"Animation track bone index/name mismatch for {track.get('bone_name')!r}")

            time_key = tuple(times)
            time_accessor = time_accessor_cache.get(time_key)
            if time_accessor is None:
                time_accessor = builder.add_accessor(
                    struct.pack("<" + "f" * len(times), *times), 5126, "SCALAR", len(times),
                    minimum=[min(times)], maximum=[max(times)],
                )
                time_accessor_cache[time_key] = time_accessor
            translation_flat = [component for value in translations for component in value]
            translation_accessor = builder.add_accessor(
                struct.pack("<" + "f" * len(translation_flat), *translation_flat),
                5126, "VEC3", len(translations),
            )
            rotation_flat = [component for value in rotations for component in value]
            rotation_accessor = builder.add_accessor(
                struct.pack("<" + "f" * len(rotation_flat), *rotation_flat),
                5126, "VEC4", len(rotations),
            )
            interpolation_name = str(track.get("interpolation", "LINEAR")).upper()
            if interpolation_name not in {"LINEAR", "STEP"}:
                raise ReaderError(f"Unsupported animation interpolation {interpolation_name!r}")
            node_index = bone_node_offset + bone_index
            for path_name, output_accessor in (("translation", translation_accessor),
                                               ("rotation", rotation_accessor)):
                sampler_index = len(samplers)
                samplers.append({
                    "input": time_accessor,
                    "output": output_accessor,
                    "interpolation": interpolation_name,
                    "extras": {
                        "bone_name": track["bone_name"],
                        "smd_node_id": track["smd_node_id"],
                        "source_frame_indices": track["frame_indices"],
                        "sparse_track": bool(track.get("is_sparse", False)),
                    },
                })
                channels.append({
                    "sampler": sampler_index,
                    "target": {"node": node_index, "path": path_name},
                })
        if not channels:
            raise ReaderError("Animation clip produced no glTF channels")
        gltf_animations.append({
            "name": animation_clip["name"],
            "samplers": samplers,
            "channels": channels,
            "extras": {
                "source_smd": animation_clip["source_smd"],
                "source_smd_type": animation_clip["source_smd_type"],
                "source_fps": animation_clip["fps"],
                "source_frame_indices": animation_clip["source_frame_indices"],
                "source_frame_count": animation_clip["source_frame_count"],
                "time_origin_frame": animation_clip["time_origin_frame"],
                "duration_seconds": animation_clip["duration_seconds"],
                "interpolation_requested": animation_clip["interpolation_requested"],
                "animated_bone_count": animation_clip["animated_bone_count"],
                "sparse_track_count": animation_clip["sparse_track_count"],
                "static_tracks_elided_count": animation_clip["static_tracks_elided_count"],
                "explicit_transform_count": animation_clip["explicit_transform_count"],
                "exported_transform_key_count": animation_clip["exported_transform_key_count"],
                "omitted_transform_count": animation_clip["omitted_transform_count"],
                "transform_semantics": animation_clip["transform_semantics"],
                "coordinate_conversion": animation_clip["coordinate_conversion"],
            },
        })
        animation_summary = {
            "name": animation_clip["name"],
            "source_smd": animation_clip["source_smd"],
            "fps": animation_clip["fps"],
            "source_frame_count": animation_clip["source_frame_count"],
            "source_frame_indices": animation_clip["source_frame_indices"],
            "duration_seconds": animation_clip["duration_seconds"],
            "animated_bone_count": animation_clip["animated_bone_count"],
            "sparse_track_count": animation_clip["sparse_track_count"],
            "static_tracks_elided_count": animation_clip["static_tracks_elided_count"],
            "explicit_transform_count": animation_clip["explicit_transform_count"],
            "exported_transform_key_count": animation_clip["exported_transform_key_count"],
            "omitted_transform_count": animation_clip["omitted_transform_count"],
            "channel_count": len(channels),
            "sampler_count": len(samplers),
            "transform_semantics": animation_clip["transform_semantics"],
        }

    gltf_materials = [{
        "name": name,
        "pbrMetallicRoughness": {
            "baseColorFactor": [0.8, 0.8, 0.8, 1.0],
            "metallicFactor": 0.0,
            "roughnessFactor": 0.9,
        },
        "extras": {"source_material": name, "texture_conversion": "not yet implemented"},
    } for name in material_names]

    gltf = {
        "asset": {"version": "2.0", "generator": f"Source Model Reader {VERSION}"},
        "scene": 0,
        "scenes": [{"name": scene.name, "nodes": [0, mesh_node_index]}],
        "nodes": nodes,
        "meshes": [{
            "name": scene.name,
            "primitives": gltf_primitives,
            "extras": {
                "source_model": scene.source_model,
                "lod": scene.lod,
                "skin_family": scene.skin,
                "bodygroups": scene.bodygroups,
                "unified_vertex_count": vertex_count,
            },
        }],
        "skins": [{
            "name": scene.name + "_Skeleton",
            "inverseBindMatrices": inverse_bind_accessor,
            "skeleton": 0,
            "joints": [bone_node_offset + i for i in range(len(scene.bones))],
        }],
        "materials": gltf_materials,
        **({"animations": gltf_animations} if gltf_animations else {}),
        "buffers": [{"byteLength": len(builder.binary)}],
        "bufferViews": builder.buffer_views,
        "accessors": builder.accessors,
        "extras": {
            "source_model": scene.source_model,
            "source_archive": scene.source_archive,
            "note": "Unified skinned geometry. Source VMT/VTF textures are not embedded in this alpha build.",
        },
    }
    json_chunk = json.dumps(gltf, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    json_chunk += b" " * ((4 - len(json_chunk) % 4) % 4)
    binary_chunk = bytes(builder.binary)
    binary_chunk += b"\0" * ((4 - len(binary_chunk) % 4) % 4)
    total_length = 12 + 8 + len(json_chunk) + 8 + len(binary_chunk)
    glb = bytearray(struct.pack("<4sII", b"glTF", 2, total_length))
    glb.extend(struct.pack("<I4s", len(json_chunk), b"JSON"))
    glb.extend(json_chunk)
    glb.extend(struct.pack("<I4s", len(binary_chunk), b"BIN\0"))
    glb.extend(binary_chunk)
    tmp = output.with_name(output.name + ".tmp")
    try:
        tmp.write_bytes(glb)
        tmp.replace(output)
    except Exception:
        try:
            tmp.unlink(missing_ok=True)
        finally:
            raise

    return {
        "tool_version": VERSION,
        "output": str(output),
        "output_format": "glTF 2.0 binary (GLB)",
        "source_model": scene.source_model,
        "source_archive": scene.source_archive,
        "lod": scene.lod,
        "skin_family": scene.skin,
        "bone_count": len(scene.bones),
        "unified_vertex_count": vertex_count,
        "triangle_count": triangle_count,
        "primitive_count": len(gltf_primitives),
        "material_count": len(material_names),
        "materials": material_names,
        "bodygroups": scene.bodygroups,
        "source_vertex_indices_referenced": len(scene.source_vertex_indices),
        "glb_bytes": len(glb),
        "animation": animation_summary,
        "coordinate_conversion": "Source Z-up (X,Y,Z) -> glTF Y-up (X,Z,-Y)",
        "material_note": "Material names are preserved with neutral placeholder PBR values; VMT/VTF conversion is not implemented.",
        "warnings": list(scene.warnings),
    }


def validate_glb_file(path: Path) -> dict:
    """Independently validate the structural and skinning data of a GLB file."""
    raw = path.read_bytes()
    errors: list[str] = []
    warnings: list[str] = []
    if len(raw) < 20:
        raise ReaderError("GLB is too small to contain a header and JSON chunk")
    magic, version, total_length = struct.unpack_from("<4sII", raw, 0)
    if magic != b"glTF":
        raise ReaderError("GLB magic is not 'glTF'")
    if version != 2:
        raise ReaderError(f"Unsupported GLB container version {version}; expected 2")
    if total_length != len(raw):
        raise ReaderError(f"GLB total length field is {total_length}, actual file size is {len(raw)}")

    chunks: list[tuple[bytes, bytes]] = []
    cursor = 12
    while cursor < len(raw):
        if cursor + 8 > len(raw):
            raise ReaderError("GLB chunk header is truncated")
        chunk_length, chunk_type = struct.unpack_from("<I4s", raw, cursor)
        cursor += 8
        if chunk_length % 4:
            raise ReaderError(f"GLB chunk {chunk_type!r} length is not 4-byte aligned")
        if cursor + chunk_length > len(raw):
            raise ReaderError("GLB chunk extends beyond end of file")
        chunks.append((chunk_type, raw[cursor:cursor + chunk_length]))
        cursor += chunk_length
    if not chunks or chunks[0][0] != b"JSON":
        raise ReaderError("GLB first chunk must be JSON")
    json_chunks = [data for kind, data in chunks if kind == b"JSON"]
    bin_chunks = [data for kind, data in chunks if kind == b"BIN\0"]
    if len(json_chunks) != 1 or len(bin_chunks) != 1:
        raise ReaderError("This validator expects exactly one JSON and one BIN chunk")
    try:
        gltf = json.loads(json_chunks[0].decode("utf-8").rstrip(" \t\r\n\0"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ReaderError(f"GLB JSON chunk is invalid: {exc}") from exc
    binary = bin_chunks[0]
    if gltf.get("asset", {}).get("version") != "2.0":
        errors.append("glTF asset.version is not 2.0")
    buffers = gltf.get("buffers", [])
    if len(buffers) != 1 or "uri" in (buffers[0] if buffers else {}):
        errors.append("GLB must contain exactly one embedded buffer without an external URI")
    elif int(buffers[0].get("byteLength", -1)) > len(binary):
        errors.append("Embedded buffer byteLength exceeds the BIN chunk")

    component_info = {
        5120: ("b", 1), 5121: ("B", 1), 5122: ("h", 2),
        5123: ("H", 2), 5125: ("I", 4), 5126: ("f", 4),
    }
    type_components = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT2": 4, "MAT3": 9, "MAT4": 16}
    views = gltf.get("bufferViews", [])
    for vi, view in enumerate(views):
        start = int(view.get("byteOffset", 0))
        length = int(view.get("byteLength", -1))
        if int(view.get("buffer", 0)) != 0 or start < 0 or length < 0 or start + length > len(binary):
            errors.append(f"bufferView {vi} references data outside the embedded BIN chunk")

    accessors = gltf.get("accessors", [])
    def accessor_layout(index: int) -> tuple[dict, str, int, int, int, int]:
        if index < 0 or index >= len(accessors):
            raise ReaderError(f"Accessor index {index} is out of range")
        accessor = accessors[index]
        if "bufferView" not in accessor:
            raise ReaderError(f"Accessor {index} has no bufferView (sparse/implicit accessors are not supported by this validator)")
        view_index = int(accessor["bufferView"])
        if view_index < 0 or view_index >= len(views):
            raise ReaderError(f"Accessor {index} references invalid bufferView {view_index}")
        component_type = int(accessor.get("componentType", 0))
        if component_type not in component_info:
            raise ReaderError(f"Accessor {index} has unsupported componentType {component_type}")
        fmt, component_size = component_info[component_type]
        accessor_type = accessor.get("type")
        if accessor_type not in type_components:
            raise ReaderError(f"Accessor {index} has unsupported type {accessor_type!r}")
        components = type_components[accessor_type]
        count = int(accessor.get("count", -1))
        if count < 0:
            raise ReaderError(f"Accessor {index} has invalid count")
        element_size = components * component_size
        view = views[view_index]
        view_start = int(view.get("byteOffset", 0))
        view_length = int(view.get("byteLength", -1))
        byte_offset = int(accessor.get("byteOffset", 0))
        stride = int(view.get("byteStride", element_size))
        if stride < element_size or byte_offset < 0:
            raise ReaderError(f"Accessor {index} has invalid byteStride/byteOffset")
        required = 0 if count == 0 else (count - 1) * stride + element_size
        if byte_offset + required > view_length:
            raise ReaderError(f"Accessor {index} data exceeds its bufferView")
        return accessor, fmt, component_size, components, view_start + byte_offset, stride

    def read_accessor(index: int) -> list[tuple[int | float, ...]]:
        accessor, fmt, component_size, components, start, stride = accessor_layout(index)
        count = int(accessor["count"])
        unpack_fmt = "<" + fmt * components
        values = []
        for element in range(count):
            offset = start + element * stride
            values.append(struct.unpack_from(unpack_fmt, binary, offset))
        return values

    # Validate every declared accessor's byte range, not just the attributes we inspect below.
    for ai in range(len(accessors)):
        try:
            accessor_layout(ai)
        except ReaderError as exc:
            errors.append(str(exc))

    nodes = gltf.get("nodes", [])
    meshes = gltf.get("meshes", [])
    skins = gltf.get("skins", [])
    materials = gltf.get("materials", [])
    node_parents: dict[int, list[int]] = {}
    for ni, node in enumerate(nodes):
        for child in node.get("children", []):
            child = int(child)
            if child < 0 or child >= len(nodes):
                errors.append(f"Node {ni} references invalid child node {child}")
            else:
                node_parents.setdefault(child, []).append(ni)
    multi_parent_nodes = [i for i, parents in node_parents.items() if len(parents) > 1]
    if multi_parent_nodes:
        warnings.append(f"{len(multi_parent_nodes)} node(s) have multiple parents")
    # Full DFS is small for character skeletons and avoids missing cycles on non-first children.
    node_cycles = 0
    color = [0] * len(nodes)
    def visit_node(index: int) -> None:
        nonlocal node_cycles
        if color[index] == 1:
            node_cycles += 1
            return
        if color[index] == 2:
            return
        color[index] = 1
        for child in nodes[index].get("children", []):
            child = int(child)
            if 0 <= child < len(nodes):
                visit_node(child)
        color[index] = 2
    for ni in range(len(nodes)):
        if color[ni] == 0:
            visit_node(ni)
    if node_cycles:
        errors.append(f"Node hierarchy contains {node_cycles} cycle(s)")

    mesh_nodes = [(i, node) for i, node in enumerate(nodes) if "mesh" in node]
    primitive_count = 0
    triangle_count = 0
    vertex_count = 0
    checked_weights = 0
    checked_weight_accessor_pairs: set[tuple[int, int, int]] = set()
    bad_weight_sums = 0
    invalid_joint_indices = 0
    out_of_range_indices = 0
    invalid_material_refs = 0
    skin_joint_count = 0
    if not mesh_nodes:
        errors.append("No node references a mesh")
    for node_index, node in mesh_nodes:
        mesh_index = int(node.get("mesh", -1))
        skin_index = int(node.get("skin", -1))
        if mesh_index < 0 or mesh_index >= len(meshes):
            errors.append(f"Mesh node {node_index} references invalid mesh {mesh_index}")
            continue
        if skin_index < 0 or skin_index >= len(skins):
            errors.append(f"Skinned mesh node {node_index} has no valid skin reference")
            continue
        skin = skins[skin_index]
        joints_nodes = [int(x) for x in skin.get("joints", [])]
        skin_joint_count = max(skin_joint_count, len(joints_nodes))
        if len(set(joints_nodes)) != len(joints_nodes):
            errors.append(f"Skin {skin_index} contains duplicate joint node references")
        for joint_node in joints_nodes:
            if joint_node < 0 or joint_node >= len(nodes):
                errors.append(f"Skin {skin_index} references invalid joint node {joint_node}")
        ibm_index = int(skin.get("inverseBindMatrices", -1))
        try:
            ibm_acc, _, _, _, _, _ = accessor_layout(ibm_index)
            if (ibm_acc.get("type") != "MAT4" or int(ibm_acc.get("componentType", 0)) != 5126 or
                    int(ibm_acc.get("count", -1)) != len(joints_nodes)):
                errors.append(f"Skin {skin_index} inverseBindMatrices accessor does not match its joint count")
            else:
                matrices = read_accessor(ibm_index)
                if any(not math.isfinite(float(value)) for matrix in matrices for value in matrix):
                    errors.append(f"Skin {skin_index} inverseBindMatrices contains non-finite values")
        except (ReaderError, ValueError, TypeError) as exc:
            errors.append(f"Skin {skin_index} inverseBindMatrices is invalid: {exc}")

        mesh = meshes[mesh_index]
        for pi, primitive in enumerate(mesh.get("primitives", [])):
            primitive_count += 1
            attrs = primitive.get("attributes", {})
            required = ("POSITION", "JOINTS_0", "WEIGHTS_0")
            if any(name not in attrs for name in required):
                errors.append(f"Mesh {mesh_index} primitive {pi} is missing required skinned attributes")
                continue
            try:
                pos_acc = int(attrs["POSITION"])
                joint_acc = int(attrs["JOINTS_0"])
                weight_acc = int(attrs["WEIGHTS_0"])
                pos_meta, *_ = accessor_layout(pos_acc)
                joint_meta, *_ = accessor_layout(joint_acc)
                weight_meta, *_ = accessor_layout(weight_acc)
                current_count = int(pos_meta["count"])
                if (joint_meta.get("type") != "VEC4" or weight_meta.get("type") != "VEC4" or
                        int(joint_meta.get("count", -1)) != current_count or
                        int(weight_meta.get("count", -1)) != current_count):
                    errors.append(f"Mesh {mesh_index} primitive {pi} has mismatched POSITION/JOINTS/WEIGHTS accessors")
                    continue
                vertex_count = max(vertex_count, current_count)
                for attribute_name, accessor_index in attrs.items():
                    try:
                        attribute_meta, *_ = accessor_layout(int(accessor_index))
                        if int(attribute_meta.get("count", -1)) != current_count:
                            errors.append(f"Mesh {mesh_index} primitive {pi} attribute {attribute_name} count differs from POSITION")
                    except (ReaderError, ValueError, TypeError) as exc:
                        errors.append(f"Mesh {mesh_index} primitive {pi} attribute {attribute_name} is invalid: {exc}")
                pair = (skin_index, joint_acc, weight_acc)
                if pair not in checked_weight_accessor_pairs:
                    checked_weight_accessor_pairs.add(pair)
                    joint_values = read_accessor(joint_acc)
                    weight_values = read_accessor(weight_acc)
                    for js, ws in zip(joint_values, weight_values):
                        checked_weights += 1
                        if any(int(j) < 0 or int(j) >= len(joints_nodes) for j in js):
                            invalid_joint_indices += 1
                        if not all(math.isfinite(float(w)) for w in ws):
                            errors.append(f"Mesh {mesh_index} primitive {pi} contains non-finite skin weights")
                            break
                        if any(float(w) < -1e-6 for w in ws):
                            warnings.append(f"Mesh {mesh_index} primitive {pi} contains negative skin weights")
                        total = sum(float(w) for w in ws)
                        if abs(total - 1.0) > 1e-3:
                            bad_weight_sums += 1

                if "indices" not in primitive:
                    errors.append(f"Mesh {mesh_index} primitive {pi} has no index accessor")
                else:
                    index_acc = int(primitive["indices"])
                    index_meta, *_ = accessor_layout(index_acc)
                    if index_meta.get("type") != "SCALAR" or int(index_meta.get("componentType", 0)) not in (5121, 5123, 5125):
                        errors.append(f"Mesh {mesh_index} primitive {pi} index accessor must be an unsigned SCALAR")
                    indices = read_accessor(index_acc)
                    flat_indices = [int(v[0]) for v in indices]
                    if len(flat_indices) % 3:
                        errors.append(f"Mesh {mesh_index} primitive {pi} index count is not divisible by 3")
                    if flat_indices and (min(flat_indices) < 0 or max(flat_indices) >= current_count):
                        out_of_range_indices += sum(1 for value in flat_indices if value < 0 or value >= current_count)
                    triangle_count += len(flat_indices) // 3
                if "material" in primitive:
                    material = int(primitive["material"])
                    if material < 0 or material >= len(materials):
                        invalid_material_refs += 1
            except (ReaderError, ValueError, TypeError, struct.error) as exc:
                errors.append(f"Mesh {mesh_index} primitive {pi} validation failed: {exc}")
    if invalid_joint_indices:
        errors.append(f"{invalid_joint_indices} JOINTS_0 entries exceed the associated skin joint count")
    if out_of_range_indices:
        errors.append(f"{out_of_range_indices} mesh indices exceed their POSITION accessor count")
    if invalid_material_refs:
        errors.append(f"{invalid_material_refs} primitive(s) reference missing materials")
    if bad_weight_sums:
        warnings.append(f"{bad_weight_sums} vertex/vertices have WEIGHTS_0 sums outside 1 ± 0.001")

    animation_count = 0
    animation_channel_count = 0
    animated_node_indices: set[int] = set()
    animations = gltf.get("animations", [])
    animation_count = len(animations)
    for animation_index, animation in enumerate(animations):
        samplers = animation.get("samplers", [])
        channels = animation.get("channels", [])
        used_targets: set[tuple[int, str]] = set()
        for channel_index, channel in enumerate(channels):
            animation_channel_count += 1
            sampler_index = int(channel.get("sampler", -1))
            if sampler_index < 0 or sampler_index >= len(samplers):
                errors.append(f"Animation {animation_index} channel {channel_index} references invalid sampler {sampler_index}")
                continue
            target = channel.get("target", {})
            node_index = int(target.get("node", -1))
            path_name = str(target.get("path", ""))
            if node_index < 0 or node_index >= len(nodes):
                errors.append(f"Animation {animation_index} channel {channel_index} references invalid target node {node_index}")
                continue
            if path_name not in {"translation", "rotation", "scale", "weights"}:
                errors.append(f"Animation {animation_index} channel {channel_index} has invalid target path {path_name!r}")
                continue
            target_key = (node_index, path_name)
            if target_key in used_targets:
                errors.append(f"Animation {animation_index} targets node {node_index} path {path_name!r} more than once")
            used_targets.add(target_key)
            animated_node_indices.add(node_index)
            sampler = samplers[sampler_index]
            interpolation_name = str(sampler.get("interpolation", "LINEAR"))
            if interpolation_name not in {"LINEAR", "STEP", "CUBICSPLINE"}:
                errors.append(f"Animation {animation_index} sampler {sampler_index} has unsupported interpolation {interpolation_name!r}")
            try:
                input_index = int(sampler.get("input", -1))
                output_index = int(sampler.get("output", -1))
                input_meta, *_ = accessor_layout(input_index)
                output_meta, *_ = accessor_layout(output_index)
                if input_meta.get("type") != "SCALAR" or int(input_meta.get("componentType", 0)) != 5126:
                    errors.append(f"Animation {animation_index} sampler {sampler_index} input must be a float SCALAR accessor")
                    continue
                expected_output_type = {"translation": "VEC3", "rotation": "VEC4", "scale": "VEC3"}.get(path_name)
                if expected_output_type and (output_meta.get("type") != expected_output_type or
                                             int(output_meta.get("componentType", 0)) != 5126):
                    errors.append(f"Animation {animation_index} channel {channel_index} output must be float {expected_output_type}")
                    continue
                input_values = [float(row[0]) for row in read_accessor(input_index)]
                output_values = read_accessor(output_index)
                if not input_values:
                    errors.append(f"Animation {animation_index} sampler {sampler_index} has no key times")
                elif any(not math.isfinite(value) or value < 0.0 for value in input_values):
                    errors.append(f"Animation {animation_index} sampler {sampler_index} has invalid key times")
                elif any(input_values[i + 1] <= input_values[i] for i in range(len(input_values) - 1)):
                    errors.append(f"Animation {animation_index} sampler {sampler_index} key times are not strictly increasing")
                expected_count = len(input_values) * (3 if interpolation_name == "CUBICSPLINE" else 1)
                if int(output_meta.get("count", -1)) != expected_count:
                    errors.append(f"Animation {animation_index} sampler {sampler_index} output key count does not match input")
                if any(not math.isfinite(float(component)) for row in output_values for component in row):
                    errors.append(f"Animation {animation_index} sampler {sampler_index} output contains non-finite values")
                if path_name == "rotation" and interpolation_name != "CUBICSPLINE":
                    for key_index, row in enumerate(output_values):
                        norm = math.sqrt(sum(float(component) ** 2 for component in row))
                        if abs(norm - 1.0) > 1e-3:
                            warnings.append(f"Animation {animation_index} sampler {sampler_index} rotation key {key_index} is not a unit quaternion")
            except (ReaderError, ValueError, TypeError, struct.error) as exc:
                errors.append(f"Animation {animation_index} sampler {sampler_index} is invalid: {exc}")

    findings = ([{"severity": "error", "message": x} for x in errors] +
                [{"severity": "warning", "message": x} for x in dict.fromkeys(warnings)])
    return {
        "tool_version": VERSION,
        "file": str(path),
        "file_bytes": len(raw),
        "container": {"magic": "glTF", "version": version, "chunks": [kind.decode("ascii", "replace") for kind, _ in chunks]},
        "summary": {
            "status": "error" if errors else "warning" if warnings else "ok",
            "mesh_nodes": len(mesh_nodes), "primitives": primitive_count,
            "shared_vertex_count": vertex_count, "triangles": triangle_count,
            "skin_joints": skin_joint_count, "unique_weighted_vertices_checked": checked_weights,
            "bad_weight_sums": bad_weight_sums,
            "animations": animation_count, "animation_channels": animation_channel_count,
            "animated_nodes": len(animated_node_indices),
        },
        "counts": {"errors": len(errors), "warnings": len(dict.fromkeys(warnings))},
        "findings": findings,
    }


def parse_bodygroup_args(items: list[str]) -> dict[int, int]:
    result: dict[int, int] = {}
    for item in items:
        try:
            left, right = item.split("=", 1)
            bp, choice = int(left), int(right)
        except ValueError:
            raise ReaderError(f"Invalid --bodygroup {item!r}; expected INDEX=CHOICE")
        if bp in result:
            raise ReaderError(f"BodyGroup {bp} was specified more than once")
        result[bp] = choice
    return result


def open_model(input_path: str, model_hint: Optional[str], backend: str) -> SourceModel:
    bundle = AssetBundle(Path(input_path), model_hint)
    try:
        if backend == "dx90":
            return SourceModel(bundle, ".dx90.vtx")
        if backend == "dx80":
            return SourceModel(bundle, ".dx80.vtx")
        try:
            return SourceModel(bundle, ".dx90.vtx")
        except ReaderError as e90:
            try:
                return SourceModel(bundle, ".dx80.vtx")
            except ReaderError as e80:
                raise ReaderError(f"DX90 attempt: {e90}\nDX80 attempt: {e80}") from e90
    except Exception:
        bundle.close()
        raise



MAX_SMD_FILE_BYTES = 64 * 1024 * 1024
MAX_SMD_FILES = 20_000


def collect_smd_sources(input_path: Path) -> tuple[list[tuple[str, bytes]], dict]:
    """Read raw SMD text assets from a file, directory, ZIP, or uncompressed GMA."""
    sources: list[tuple[str, bytes]] = []
    metadata: dict = {"input": str(input_path), "container_type": "unknown", "smd_count": 0}

    def add(name: str, data: bytes) -> None:
        if len(sources) >= MAX_SMD_FILES:
            raise ReaderError(f"Input contains more than {MAX_SMD_FILES} SMD files")
        if len(data) > MAX_SMD_FILE_BYTES:
            raise ReaderError(f"SMD file exceeds the {MAX_SMD_FILE_BYTES}-byte safety limit: {name}")
        sources.append((name, data))

    if input_path.is_file() and input_path.suffix.lower() == ".smd":
        metadata["container_type"] = "smd-file"
        if input_path.stat().st_size > MAX_SMD_FILE_BYTES:
            raise ReaderError(f"SMD file exceeds the {MAX_SMD_FILE_BYTES}-byte safety limit: {input_path}")
        add(input_path.name, input_path.read_bytes())
    elif input_path.is_dir():
        metadata["container_type"] = "directory"
        files = sorted((p for p in input_path.rglob("*") if p.is_file() and p.suffix.lower() == ".smd"),
                       key=lambda p: p.as_posix().lower())
        if len(files) > MAX_SMD_FILES:
            raise ReaderError(f"Directory contains more than {MAX_SMD_FILES} SMD files")
        for file_path in files:
            if file_path.stat().st_size > MAX_SMD_FILE_BYTES:
                raise ReaderError(f"SMD file exceeds the {MAX_SMD_FILE_BYTES}-byte safety limit: {file_path}")
            add(file_path.relative_to(input_path).as_posix(), file_path.read_bytes())
    elif input_path.is_file() and input_path.suffix.lower() == ".gma":
        metadata["container_type"] = "gma"
        archive = GMAArchive(input_path)
        try:
            metadata["archive"] = archive.metadata
            entries = [entry for entry in archive.entries if entry.path.lower().endswith(".smd")]
            for entry in sorted(entries, key=lambda item: item.path.lower()):
                if entry.size > MAX_SMD_FILE_BYTES:
                    raise ReaderError(f"SMD file exceeds the {MAX_SMD_FILE_BYTES}-byte safety limit: {entry.path}")
                add(entry.path, archive.read(entry.path))
        finally:
            archive.close()
    elif input_path.is_file() and zipfile.is_zipfile(input_path):
        metadata["container_type"] = "zip"
        with zipfile.ZipFile(input_path, "r") as zf:
            infos = [info for info in zf.infolist()
                     if not info.is_dir() and info.filename.lower().endswith(".smd")]
            if len(infos) > MAX_SMD_FILES:
                raise ReaderError(f"ZIP contains more than {MAX_SMD_FILES} SMD files")
            for info in sorted(infos, key=lambda item: item.filename.lower()):
                if info.file_size > MAX_SMD_FILE_BYTES:
                    raise ReaderError(f"SMD file exceeds the {MAX_SMD_FILE_BYTES}-byte safety limit: {info.filename}")
                add(info.filename, zf.read(info))
    else:
        raise ReaderError("SMD audit input must be an .smd file, a directory, a ZIP, or an uncompressed .gma")

    sources.sort(key=lambda item: item[0].lower())
    metadata["smd_count"] = len(sources)
    return sources, metadata


def build_smd_audit_report(input_path: Path, reference_name: Optional[str] = None,
                           mdl_bones: Optional[list[Bone]] = None,
                           tolerance: float = 1e-4) -> dict:
    from smd_reader import (SMDParseError, SMD_TYPE_LABELS, compare_smd_skeletons,
                            compare_smd_to_mdl, parse_smd_bytes)

    if not math.isfinite(tolerance) or tolerance < 0:
        raise ReaderError("--tolerance must be a finite non-negative number")
    sources, input_info = collect_smd_sources(input_path)
    if not sources:
        return {
            "tool_version": VERSION,
            "input": input_info,
            "summary": {"status": "not_found", "smd_files": 0, "parsed_files": 0,
                        "parse_errors": 0, "comparisons": 0, "pose_comparisons": 0,
                        "pose_comparisons_skipped": 0},
            "message": "No raw .smd files were found. This is not a parser failure; the package may contain only compiled MDL/VVD/VTX resources.",
            "mdl_reference": ({"bone_definition_count": len(mdl_bones),
                               "bone_names": [bone.name for bone in mdl_bones]} if mdl_bones is not None else None),
            "files": [], "comparisons": [], "mdl_mappings": [],
        }

    parsed = []
    file_reports = []
    parse_errors = []
    for name, raw in sources:
        try:
            document = parse_smd_bytes(raw, name)
            parent_names = document.parent_names
            nodes = [{
                "id": node.node_id,
                "name": node.name,
                "parent_id": node.parent_id,
                "parent_name": parent_names.get(node.node_id),
            } for node in document.nodes]
            pose_time = document.reference_pose_time
            frame_coverage = document.frame_coverage
            sparse_frame_count = sum(1 for frame in frame_coverage if frame["is_sparse"])
            file_report = {
                "source": name,
                "smd_type": document.smd_type,
                "smd_type_label": SMD_TYPE_LABELS.get(document.smd_type, document.smd_type),
                "pose_comparable_to_bind_reference": document.pose_comparable,
                "status": "warning" if document.warnings else "ok",
                "summary": {
                    "version": document.version,
                    "node_count": len(document.nodes),
                    "root_count": document.root_count,
                    "leaf_count": document.leaf_count,
                    "skeleton_frame_count": len(document.frames),
                    "frame_times": sorted(document.frames),
                    "first_frame_time": pose_time,
                    "first_frame_transform_count": len(document.reference_pose),
                    "reference_pose_time": pose_time if document.pose_comparable else None,
                    "reference_pose_transform_count": len(document.reference_pose) if document.pose_comparable else 0,
                    "sparse_frame_count": sparse_frame_count,
                    "frame_coverage_status": ("no_frames" if not frame_coverage else
                                               "empty_frame" if any(x["explicit_transform_count"] == 0 for x in frame_coverage) else
                                               "sparse" if sparse_frame_count else "complete"),
                    "triangle_count": document.triangle_count,
                    "triangle_vertex_records": document.triangle_vertex_records,
                    "material_count": len(document.materials),
                },
                "frame_coverage": frame_coverage,
                "nodes": nodes,
                "materials": document.materials,
                "warnings": document.warnings,
                "infos": document.infos,
            }
            file_reports.append(file_report)
            parsed.append(document)
        except (SMDParseError, UnicodeError, ValueError) as exc:
            parse_errors.append({"source": name, "error": str(exc)})
            file_reports.append({"source": name, "status": "error", "error": str(exc)})

    reference_selection_method = "explicit"
    if reference_name:
        ref_key = reference_name.replace("\\", "/").casefold()
        matches = [doc for doc in parsed if doc.source.replace("\\", "/").casefold() == ref_key
                   or Path(doc.source).name.casefold() == Path(ref_key).name.casefold()]
        if not matches:
            available = ", ".join(doc.source for doc in parsed[:12])
            raise ReaderError(f"--reference {reference_name!r} did not match a parsed SMD. Available: {available}")
        reference = matches[0]
    else:
        # Prefer the known base character slice when present. Otherwise choose
        # the first ordinary geometry slice, not an animation/physics SMD.
        preferred = [doc for doc in parsed
                     if Path(doc.source.replace("\\", "/")).name.casefold() == "aes_t.smd"
                     and doc.pose_comparable]
        geometry = [doc for doc in parsed if doc.pose_comparable]
        if preferred:
            reference = preferred[0]
            reference_selection_method = "preferred_base_slice_aes_t"
        elif geometry:
            reference = geometry[0]
            reference_selection_method = "first_model_slice_fallback"
        else:
            reference = parsed[0] if parsed else None
            reference_selection_method = "first_parsed_fallback_no_model_slice"

    comparisons = []
    if reference is not None:
        for document in parsed:
            if document is reference:
                continue
            comparisons.append(compare_smd_skeletons(reference, document, tolerance))

    mdl_mappings = []
    if mdl_bones is not None:
        mdl_mappings = [compare_smd_to_mdl(document, mdl_bones, tolerance) for document in parsed]

    warnings_count = sum(1 for f in file_reports if f.get("status") == "warning")
    warnings_count += sum(1 for c in comparisons if c["status"] == "warning")
    warnings_count += sum(1 for m in mdl_mappings if m["status"] == "warning")
    info_groups = sum(1 for f in file_reports if f.get("infos"))
    info_groups += sum(1 for c in comparisons if any(x.get("severity") == "info" for x in c.get("findings", [])))
    info_groups += sum(1 for m in mdl_mappings if any(x.get("severity") == "info" for x in m.get("findings", [])))
    sparse_frames = [
        {"source": f["source"], "smd_type": f["smd_type"], **frame}
        for f in file_reports for frame in f.get("frame_coverage", []) if frame.get("is_sparse")
    ]
    status = "error" if parse_errors else "warning" if warnings_count else "ok"
    type_counts: dict[str, int] = {}
    for document in parsed:
        type_counts[document.smd_type] = type_counts.get(document.smd_type, 0) + 1
    pose_comparisons = sum(1 for c in comparisons if c["reference_pose_comparison"] == "compared")
    pose_skipped = sum(1 for c in comparisons if c["reference_pose_comparison"] != "compared")
    mdl_pose_compared = sum(1 for m in mdl_mappings if m["reference_pose_comparison"] == "compared")
    mdl_pose_skipped = sum(1 for m in mdl_mappings if m["reference_pose_comparison"] != "compared")
    return {
        "tool_version": VERSION,
        "input": input_info,
        "summary": {
            "status": status,
            "smd_files": len(sources),
            "parsed_files": len(parsed),
            "parse_errors": len(parse_errors),
            "comparisons": len(comparisons),
            "pose_comparisons": pose_comparisons,
            "pose_comparisons_skipped": pose_skipped,
            "mdl_mappings": len(mdl_mappings),
            "mdl_pose_comparisons": mdl_pose_compared,
            "mdl_pose_comparisons_skipped": mdl_pose_skipped,
            "smd_type_counts": type_counts,
            "warning_groups": warnings_count,
            "info_groups": info_groups,
            "sparse_frame_count": len(sparse_frames),
            "sparse_animation_frame_count": sum(1 for frame in sparse_frames if frame["smd_type"] in {
                "animation", "proportion_animation", "corrective_animation",
                "animation_reference", "ragdoll_pose",
            }),
            "sparse_animation_file_count": len({frame["source"] for frame in sparse_frames if frame["smd_type"] in {
                "animation", "proportion_animation", "corrective_animation",
                "animation_reference", "ragdoll_pose",
            }}),
            "sparse_frames": sparse_frames,
            "reference_smd": reference.source if reference else None,
            "reference_selection_method": reference_selection_method,
            "tolerance": tolerance,
        },
        "classification_policy": {
            "model_slice": "ordinary geometry slice; eligible for bind/reference-pose comparison",
            "physics_geometry": "physics resource; skeleton structure is checked, pose comparison skipped",
            "animation": "animation resource; first frame is not assumed to be bind pose; non-empty sparse frame records are informational",
            "proportion_animation": "proportion adjustment; pose comparison skipped; non-empty sparse frame records are informational",
            "corrective_animation": "corrective animation; pose comparison skipped; non-empty sparse frame records are informational",
            "animation_reference": "animation reference resource; pose comparison skipped; non-empty sparse frame records are informational",
            "ragdoll_pose": "ragdoll pose resource; pose comparison skipped; non-empty sparse frame records are informational",
            "unknown": "unclassified; pose comparison skipped conservatively",
        },
        "mdl_reference": ({"bone_definition_count": len(mdl_bones),
                           "bone_names": [bone.name for bone in mdl_bones]} if mdl_bones is not None else None),
        "reference_skeleton": ({
            "source": reference.source,
            "smd_type": reference.smd_type,
            "node_count": len(reference.nodes),
            "root_count": reference.root_count,
            "leaf_count": reference.leaf_count,
            "reference_pose_time": reference.reference_pose_time if reference.pose_comparable else None,
        } if reference else None),
        "files": file_reports,
        "comparisons": comparisons,
        "mdl_mappings": mdl_mappings,
        "parse_errors": parse_errors,
    }

def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(
        prog="source_model_reader",
        description="Inspect Source 1 resources and export a unified skinned GLB or compatible SMD."
    )
    parser.add_argument("--version", action="version", version=VERSION)
    sub = parser.add_subparsers(dest="command", required=True)

    p_inspect = sub.add_parser("inspect", help="Inspect model headers, bones, materials, and BodyGroups")
    p_inspect.add_argument("input", help=".mdl file, model folder, ZIP archive, or uncompressed .gma")
    p_inspect.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA when multiple models exist")
    p_inspect.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_inspect.add_argument("--json", dest="json_path", help="Write full inspection data to a JSON file")

    p_diagnose = sub.add_parser("diagnose", help="Diagnose skeleton, vertex weights, BodyGroups, and selected geometry")
    p_diagnose.add_argument("input", help=".mdl file, model folder, ZIP archive, or uncompressed .gma")
    p_diagnose.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA when multiple models exist")
    p_diagnose.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_diagnose.add_argument("--lod", type=int, default=0, help="LOD index (0 is highest detail)")
    p_diagnose.add_argument("--skin", type=int, default=0, help="Skin family index")
    p_diagnose.add_argument("--bodygroup", action="append", default=[], metavar="INDEX=CHOICE",
                            help="Override a BodyGroup choice; repeat as needed")
    p_diagnose.add_argument("--json", dest="json_path", help="Write the full diagnostic report to a JSON file")

    p_export = sub.add_parser("export", help="Reconstruct the selected BodyGroups and export a weighted SMD")
    p_export.add_argument("input", help=".mdl file, model folder, ZIP archive, or uncompressed .gma")
    p_export.add_argument("-o", "--output", required=True, help="Output .smd path")
    p_export.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA when multiple models exist")
    p_export.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_export.add_argument("--lod", type=int, default=0, help="LOD index (0 is highest detail)")
    p_export.add_argument("--skin", type=int, default=0, help="Skin family index")
    p_export.add_argument("--bodygroup", action="append", default=[], metavar="INDEX=CHOICE",
                          help="Override a BodyGroup choice; repeat as needed, e.g. --bodygroup 18=1")

    p_export_glb = sub.add_parser("export-glb", help="Export one unified skinned character scene as glTF 2.0 GLB")
    p_export_glb.add_argument("input", help=".mdl file, model folder, ZIP archive, or uncompressed .gma")
    p_export_glb.add_argument("-o", "--output", required=True, help="Output .glb path")
    p_export_glb.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA when multiple models exist")
    p_export_glb.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_export_glb.add_argument("--lod", type=int, default=0, help="LOD index (0 is highest detail)")
    p_export_glb.add_argument("--skin", type=int, default=0, help="Skin family index")
    p_export_glb.add_argument("--bodygroup", action="append", default=[], metavar="INDEX=CHOICE",
                              help="Override a BodyGroup choice; repeat as needed, e.g. --bodygroup 18=1")

    p_export_animation = sub.add_parser(
        "export-animated-glb",
        help="Export a unified skinned character with one SMD animation clip embedded in GLB",
    )
    p_export_animation.add_argument("input", help=".mdl file, model folder, ZIP archive, or uncompressed .gma")
    p_export_animation.add_argument("-o", "--output", required=True, help="Output animated .glb path")
    p_export_animation.add_argument("--animation", required=True, help="Source animation .smd file (normally a multi-frame animation SMD)")
    p_export_animation.add_argument("--fps", required=True, type=float,
                                    help="Explicit source animation frame rate; SMD does not reliably declare FPS")
    p_export_animation.add_argument("--interpolation", choices=["auto", "linear", "step"], default="auto",
                                    help="auto uses LINEAR for dense tracks and STEP for sparse tracks")
    p_export_animation.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA when multiple models exist")
    p_export_animation.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_export_animation.add_argument("--lod", type=int, default=0, help="LOD index (0 is highest detail)")
    p_export_animation.add_argument("--skin", type=int, default=0, help="Skin family index")
    p_export_animation.add_argument("--bodygroup", action="append", default=[], metavar="INDEX=CHOICE",
                                    help="Override a BodyGroup choice; repeat as needed")
    p_export_animation.add_argument("--json", dest="json_path", help="Write the animation export report to JSON")

    p_bone_matrix = sub.add_parser("bone-matrix", help="Compare vertex-weight bone references across every BodyGroup option")
    p_bone_matrix.add_argument("input", help=".mdl file, model folder, ZIP archive, or uncompressed .gma")
    p_bone_matrix.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA when multiple models exist")
    p_bone_matrix.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_bone_matrix.add_argument("--lod", type=int, default=0, help="LOD index (0 is highest detail)")
    p_bone_matrix.add_argument("--skin", type=int, default=0, help="Skin family index")
    p_bone_matrix.add_argument("--json", dest="json_path", help="Write the full BodyGroup/Bone dependency matrix to JSON")

    p_validate_glb = sub.add_parser("validate-glb", help="Independently validate GLB container, mesh indices, skin, and weights")
    p_validate_glb.add_argument("input", help="GLB file to validate")
    p_validate_glb.add_argument("--json", dest="json_path", help="Write the validation report to a JSON file")

    p_smd_audit = sub.add_parser("smd-audit", help="Read SMD node/skeleton sections and compare skeletons across slices")
    p_smd_audit.add_argument("input", help="SMD file, folder, ZIP archive, or uncompressed GMA containing raw .smd files")
    p_smd_audit.add_argument("--reference", help="Reference SMD path/name; defaults to AES_T.smd when present, otherwise the first model slice")
    p_smd_audit.add_argument("--mdl", help="Optional MDL file, model folder, ZIP, or GMA for name/hierarchy/pose mapping")
    p_smd_audit.add_argument("--model", help="MDL path/name inside a folder, ZIP, or GMA used with --mdl")
    p_smd_audit.add_argument("--backend", choices=["auto", "dx90", "dx80"], default="auto")
    p_smd_audit.add_argument("--tolerance", type=float, default=1e-4,
                             help="Position/rotation comparison tolerance (Source units / radians; default 0.0001)")
    p_smd_audit.add_argument("--json", dest="json_path", help="Write the full SMD skeleton audit report to JSON")

    p_hd2_inspect = sub.add_parser(
        "hd2-inspect",
        help="Inspect Helldivers 2 package TOCs, Unit references, and Bones resources",
    )
    p_hd2_inspect.add_argument(
        "input",
        help="HD2 game data directory, legacy/DSAR archive package, or package file",
    )
    p_hd2_inspect.add_argument(
        "--package",
        help="Package/archive name or ID when input is the HD2 game data directory",
    )
    p_hd2_inspect.add_argument(
        "--json", dest="json_path", help="Write the full HD2 resource inventory to JSON",
    )

    p_hd2_catalog = sub.add_parser(
        "hd2-catalog",
        help="Scan all SLIM archive TOC type tables and find packages containing model resources",
    )
    p_hd2_catalog.add_argument("input", help="HD2 game data directory containing bundles.nxa")
    p_hd2_catalog.add_argument("--max-packages", type=int,
                               help="Optional limit for a quick diagnostic run")
    p_hd2_catalog.add_argument("--json", dest="json_path",
                               help="Write the full package catalog to JSON")

    p_hd2_material_census = sub.add_parser(
        "hd2-material-census",
        help="Census Material resources across SLIM packages without reading GPU/Stream sidecars",
    )
    p_hd2_material_census.add_argument(
        "input", help="HD2 game data directory containing bundles.nxa",
    )
    p_hd2_material_census.add_argument(
        "--max-packages", type=int,
        help="Optional package limit for a partial diagnostic scan",
    )
    p_hd2_material_census.add_argument(
        "--include-descriptors", action="store_true",
        help="Include ordered texture-node IDs and shader-variable descriptors for every Material",
    )
    p_hd2_material_census.add_argument(
        "--top", type=int, default=12,
        help="Number of frequent ParentMaterialIDs, texture types and shader variables to print (default: 12)",
    )
    p_hd2_material_census.add_argument(
        "--json", dest="json_path",
        help="Write the full Material census report to JSON",
    )

    p_hd2_find = sub.add_parser(
        "hd2-find",
        help="Locate resource FileIDs across SLIM packages using TOC entries only",
    )
    p_hd2_find.add_argument("input", help="HD2 game data directory containing bundles.nxa")
    p_hd2_find.add_argument(
        "--resource", action="append", required=True, metavar="FILE_ID",
        help="Resource FileID to locate (hexadecimal; repeat for multiple IDs)",
    )
    p_hd2_find.add_argument(
        "--type", required=True,
        choices=sorted(("Texture", "Material", "Unit", "Bones", "CompositeUnit",
                        "Animation", "StateMachine", "Physics")),
        help="Required resource type filter; use Texture for Material texture dependencies",
    )
    p_hd2_find.add_argument("--max-packages", type=int,
                            help="Optional limit for a partial diagnostic scan")
    p_hd2_find.add_argument("--json", dest="json_path",
                            help="Write the full resource lookup report to JSON")

    p_hd2_unit = sub.add_parser(
        "hd2-unit",
        help="Decode one HD2 Unit's TransformInfo, BoneInfo, streams, meshes, and GPU geometry",
    )
    p_hd2_unit.add_argument("input", help="HD2 game data directory or package/archive file")
    p_hd2_unit.add_argument("--package", help="Package/archive ID when input is the HD2 game data directory")
    p_hd2_unit.add_argument("--unit", required=True, help="Unit resource FileID (hexadecimal)")
    p_hd2_unit.add_argument("--json", dest="json_path", help="Write the complete Unit/geometry report to JSON")

    p_hd2_material = sub.add_parser(
        "hd2-material",
        help="Inspect one HD2 Material resource, texture dependencies and raw shader-variable descriptors",
    )
    p_hd2_material.add_argument("input", help="HD2 game data directory or package/archive file")
    p_hd2_material.add_argument("--package", help="Package/archive ID when input is the game data directory")
    p_hd2_material.add_argument("--material", required=True, help="Material resource FileID (hexadecimal, with or without 0x; use dec: for decimal)")
    p_hd2_material.add_argument("--json", dest="json_path", help="Write the complete Material resource report to JSON")

    p_hd2_texture = sub.add_parser(
        "hd2-texture",
        help="Inspect one HD2 Texture resource and export its SDK-style DDS payload",
    )
    p_hd2_texture.add_argument("input", help="HD2 SLIM game data directory containing bundles.nxa")
    p_hd2_texture.add_argument("--package", required=True, help="Package/archive ID containing the Texture")
    p_hd2_texture.add_argument("--texture", required=True, help="Texture resource FileID (hexadecimal)")
    p_hd2_texture.add_argument("--output", required=True, help="Output DDS file path")
    p_hd2_texture.add_argument("--preserve-raw", action="store_true",
                                help="Also write separate .gpu.bin and .stream.bin payload copies")
    p_hd2_texture.add_argument("--preview-png", action="store_true",
                                help="Decode a supported BC1/BC5_UNORM/BC7 base mip to PNG and report channel statistics (optional dependency)")
    p_hd2_texture.add_argument("--json", dest="json_path",
                                help="Write the complete Texture metadata report to JSON")

    args = parser.parse_args(argv)
    if args.command == "hd2-catalog":
        try:
            from hd2_resources import HD2ResourceError, format_catalog_text, scan_slim_package_catalog

            report = scan_slim_package_catalog(args.input, args.max_packages)
            print(format_catalog_text(report))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "hd2-material-census":
        try:
            from hd2_material_census import (
                format_material_census_text,
                scan_slim_material_census,
            )
            from hd2_resources import HD2ResourceError

            if args.top < 1:
                raise ValueError("--top must be at least 1")
            report = scan_slim_material_census(
                args.input,
                max_packages=args.max_packages,
                include_descriptors=args.include_descriptors,
            )
            print(format_material_census_text(report, top=args.top))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(
                    json.dumps(report, ensure_ascii=False, indent=2),
                    encoding="utf-8",
                )
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError,
                KeyError, OverflowError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "hd2-find":
        try:
            from hd2_material import parse_resource_id
            from hd2_resources import (
                HD2ResourceError,
                find_slim_resources,
                format_slim_resource_lookup_text,
            )

            resource_ids = [parse_resource_id(value) for value in args.resource]
            report = find_slim_resources(
                args.input, resource_ids, args.type, args.max_packages
            )
            print(format_slim_resource_lookup_text(report))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "hd2-material":
        try:
            from hd2_material import (
                format_hd2_material_text,
                inspect_hd2_material,
                parse_resource_id,
            )

            material_id = parse_resource_id(args.material)
            report = inspect_hd2_material(args.input, args.package, material_id)
            print(format_hd2_material_text(report))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (OSError, struct.error, ValueError, TypeError, KeyError, OverflowError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "hd2-texture":
        try:
            from hd2_material import parse_resource_id
            from hd2_resources import HD2ResourceError
            from hd2_texture import (
                decode_texture_preview,
                format_hd2_texture_text,
                inspect_hd2_texture,
            )

            texture_id = parse_resource_id(args.texture)
            report, dds_bytes, gpu_bytes, stream_bytes = inspect_hd2_texture(
                args.input, args.package, texture_id
            )
            preview_result = None
            preview_png = None
            if args.preview_png:
                selected_payload = stream_bytes if stream_bytes else gpu_bytes
                preview_result, preview_png = decode_texture_preview(
                    report, selected_payload
                )

            output = Path(args.output)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_bytes(dds_bytes)
            outputs = {"dds": str(output)}
            if preview_png is not None:
                preview_output = output.with_suffix(".png")
                preview_output.write_bytes(preview_png)
                outputs["preview_png"] = str(preview_output)
                report["preview"] = preview_result
            if args.preserve_raw:
                if gpu_bytes:
                    gpu_output = output.with_name(output.stem + ".gpu.bin")
                    gpu_output.write_bytes(gpu_bytes)
                    outputs["gpu_raw"] = str(gpu_output)
                if stream_bytes:
                    stream_output = output.with_name(output.stem + ".stream.bin")
                    stream_output.write_bytes(stream_bytes)
                    outputs["stream_raw"] = str(stream_output)
            report["outputs"] = outputs
            print(format_hd2_texture_text(report))
            if preview_result is not None:
                alpha = preview_result["alpha"]
                print(
                    f"{preview_result['codec']} base-mip preview: {outputs['preview_png']} "
                    f"({preview_result['width']}x{preview_result['height']})"
                )
                print(
                    f"Alpha: min={alpha['minimum']} max={alpha['maximum']} "
                    f"unique={alpha['unique_values']} "
                    f"transparent={alpha['fully_transparent_pixels']:,} "
                    f"partial={alpha['partial_alpha_pixels']:,} "
                    f"opaque={alpha['fully_opaque_pixels']:,}"
                )
            print(f"DDS exported: {output} ({len(dds_bytes):,} bytes)")
            for label, filename in outputs.items():
                if label != "dds":
                    print(f"{label}: {filename}")
            if args.json_path:
                json_path = Path(args.json_path)
                json_path.parent.mkdir(parents=True, exist_ok=True)
                json_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {json_path}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError,
                KeyError, OverflowError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "hd2-unit":
        try:
            from hd2_unit import HD2ResourceError, format_hd2_unit_text, inspect_hd2_unit

            report = inspect_hd2_unit(args.input, args.package, args.unit)
            print(format_hd2_unit_text(report))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError, KeyError, OverflowError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "hd2-inspect":
        try:
            from hd2_resources import HD2ResourceError, format_report_text, inspect_hd2_package

            report = inspect_hd2_package(args.input, args.package)
            print(format_report_text(report))
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 1 if report["summary"]["status"] == "warning" else 0
        except (HD2ResourceError, OSError, struct.error, ValueError, TypeError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
    if args.command == "smd-audit":
        mdl_model: Optional[SourceModel] = None
        try:
            if args.mdl:
                mdl_model = open_model(args.mdl, args.model, args.backend)
            report = build_smd_audit_report(
                Path(args.input), args.reference,
                mdl_model.bones if mdl_model is not None else None,
                args.tolerance,
            )
            summary = report["summary"]
            print(f"Source Model Reader {VERSION} - SMD skeleton audit")
            print(f"Input: {args.input} | Container: {report['input']['container_type']}")
            print(f"SMD files: {summary['smd_files']} | Parsed: {summary['parsed_files']} | "
                  f"Parse errors: {summary['parse_errors']} | Comparisons: {summary['comparisons']}")
            if report.get("mdl_reference"):
                print(f"MDL reference supplied: {report['mdl_reference']['bone_definition_count']} bone definitions")
            if summary["status"] == "not_found":
                print("No raw .smd files found.")
                print(report["message"])
            else:
                print(f"Status: {summary['status'].upper()} | Reference: {summary['reference_smd']} "
                      f"({summary['reference_selection_method']})")
                if summary.get("smd_type_counts"):
                    type_summary = ", ".join(f"{kind}={count}" for kind, count in sorted(summary["smd_type_counts"].items()))
                    print(f"SMD types: {type_summary}")
                print(f"Pose comparisons: {summary['pose_comparisons']} | "
                      f"skipped by type: {summary['pose_comparisons_skipped']}")
                print(f"Diagnostics: {summary.get('warning_groups', 0)} warning group(s) | "
                      f"{summary.get('info_groups', 0)} info group(s) | "
                      f"{summary.get('sparse_frame_count', 0)} sparse frame(s)")
                for file_report in report.get("files", []):
                    for message in file_report.get("warnings", []):
                        print(f"  WARNING {file_report['source']}: {message}")
                    for message in file_report.get("infos", []):
                        print(f"  INFO {file_report['source']}: {message}")
                if report["reference_skeleton"]:
                    ref = report["reference_skeleton"]
                    print(f"Reference skeleton: {ref['node_count']} nodes | {ref['root_count']} roots | "
                          f"{ref['leaf_count']} leaves | bind/reference pose time {ref['reference_pose_time']}")
                for comparison in report["comparisons"]:
                    pose_text = (f"pose {comparison['reference_pose_status']} "
                                 f"({comparison['reference_pose_mismatch_count']} mismatches)"
                                 if comparison["reference_pose_comparison"] == "compared"
                                 else f"pose SKIPPED ({comparison['candidate_smd_type']})")
                    print(f"  {comparison['candidate']}: {comparison['status'].upper()} | "
                          f"matched {comparison['matched_node_count']} | "
                          f"missing {comparison['missing_node_count']} | additional {comparison['additional_node_count']} | "
                          f"parent mismatches {comparison['parent_mismatch_count']} | {pose_text} | "
                          f"ID remaps {comparison['node_id_remap_count']}")
                for mapping in report["mdl_mappings"]:
                    pose_text = (f"pose {mapping['reference_pose_status']} "
                                 f"({mapping['reference_pose_mismatch_count']} mismatches)"
                                 if mapping["reference_pose_comparison"] == "compared"
                                 else f"pose SKIPPED ({mapping['smd_type']})")
                    print(f"  MDL map {mapping['smd']}: {mapping['status'].upper()} | "
                          f"matched {mapping['matched_name_count']} | "
                          f"SMD-only {mapping['missing_in_mdl_count']} | MDL-only {mapping['missing_in_smd_count']} | "
                          f"parent mismatches {mapping['parent_mismatch_count']} | {pose_text}")
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 2 if summary["status"] == "error" else 1 if summary["status"] == "warning" else 0
        except (ReaderError, OSError, zipfile.BadZipFile, struct.error, ValueError, TypeError) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2
        finally:
            if mdl_model:
                mdl_model.bundle.close()

    if args.command == "validate-glb":
        try:
            report = validate_glb_file(Path(args.input))
            print(f"Source Model Reader {VERSION} - GLB validation")
            print(f"File: {report['file']}")
            summary = report["summary"]
            print(f"Status: {summary['status'].upper()} | Mesh nodes: {summary['mesh_nodes']} | "
                  f"Primitives: {summary['primitives']} | Triangles: {summary['triangles']}")
            print(f"Skin joints: {summary['skin_joints']} | Unique weighted vertices checked: {summary['unique_weighted_vertices_checked']} | "
                  f"Bad weight sums: {summary['bad_weight_sums']}")
            print(f"Animations: {summary.get('animations', 0)} | Animation channels: {summary.get('animation_channels', 0)} | "
                  f"Animated nodes: {summary.get('animated_nodes', 0)}")
            for item in report["findings"]:
                print(f"  [{item['severity'].upper()}] {item['message']}")
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 2 if summary["status"] == "error" else 0
        except (ReaderError, OSError, struct.error) as exc:
            print(f"ERROR: {exc}", file=sys.stderr)
            return 2

    model: Optional[SourceModel] = None
    try:
        model = open_model(args.input, getattr(args, "model", None), args.backend)
        if args.command == "inspect":
            data = model.inspect_dict()
            bodygroups = data["bodygroups"]
            print(f"Source Model Reader {VERSION}")
            print(f"Model: {model.bundle.mdl_display}")
            if model.bundle.archive_info:
                meta = model.bundle.archive_info
                print(f"GMA v{meta['format_version']} | Addon: {meta['name']} | Author: {meta['author']} | Files: {meta['entry_count']}")
            print(f"MDL v{model.mdl_version} | VVD v{model.vvd_version} | VTX v{model.vtx_version} | checksum {model.checksum}")
            print(f"Bones: {model.num_bones} | VVD LOD0 vertices: {model.vvd_vertex_count} | BodyGroups: {len(bodygroups)}")
            print("\nBodyGroups (default choice = 0):")
            for bp in bodygroups:
                opts = "; ".join(f"{m['index']}:{m['name']} ({len(m['meshes'])} meshes)" for m in bp["models"])
                print(f"  [{bp['index']:02d}] {bp['name']} | base={bp['base']} | {opts}")
            if data["warnings"]:
                print("\nWarnings:")
                for warning in data["warnings"]:
                    print(f"  - {warning}")
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"\nJSON report: {out}")
            return 0
        if args.command == "bone-matrix":
            report = model.bone_dependency_matrix(args.lod, args.skin)
            summary = report["summary"]
            print(f"Source Model Reader {VERSION} - BodyGroup/Bone dependency matrix")
            print(f"Model: {model.bundle.mdl_display}")
            if model.bundle.archive_info:
                meta = model.bundle.archive_info
                print(f"GMA v{meta['format_version']} | Addon: {meta['name']} | Files: {meta['entry_count']}")
            print(f"MDL bone definitions: {summary['mdl_bone_definition_count']} | "
                  f"Default weight-referenced bones: {summary['baseline_weight_referenced_bone_count']} | "
                  f"BodyGroups: {summary['bodygroup_count']} | Options: {summary['option_count']}")
            print("Comparison: each option is tested with all other BodyGroups at choice 0")
            for group in report["bodygroups"]:
                print(f"\n[{group['bodygroup_index']:02d}] {group['bodygroup_name']}")
                for option in group["options"]:
                    delta = option["delta_vs_all_default"]
                    refs = option["weight_references"]
                    print(f"  ({option['option_index']}) {option['option_name']}: "
                          f"{refs['weighted_bone_count']} weight-referenced bones, "
                          f"+{delta['added_count']}/-{delta['removed_count']} vs default, "
                          f"{option['geometry']['triangles']} triangles")
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"\nJSON report: {out}")
            return 0

        choices = parse_bodygroup_args(args.bodygroup)
        if args.command == "diagnose":
            report = model.diagnose(args.lod, args.skin, choices)
            summary = report["summary"]
            print(f"Source Model Reader {VERSION} - model diagnostics")
            print(f"Model: {model.bundle.mdl_display}")
            if model.bundle.archive_info:
                meta = model.bundle.archive_info
                print(f"GMA v{meta['format_version']} | Addon: {meta['name']} | Files: {meta['entry_count']}")
            print(f"Status: {summary['status'].upper()} | MDL bone definitions: {summary['bone_definitions']} | "
                  f"Vertices: {summary['selected_vertices']} | Triangles: {summary['triangles']} | "
                  f"Materials: {summary['material_count']}")
            skeleton = report["skeleton"]
            print(f"MDL bone table: {skeleton['definition_count']} definitions | {skeleton['root_count']} roots | "
                  f"{skeleton['leaf_count']} leaves | "
                  f"Weight-referenced bones: {skeleton['weight_referenced_bone_count']} | "
                  f"Defined but not referenced by selected weights: {skeleton['defined_but_not_weight_referenced_count']}")
            print(f"Bind-pose max matrix error: {skeleton['bind_pose_max_identity_error']!s} | "
                  f"Non-finite bone transforms: {skeleton['non_finite_transforms']} | "
                  f"Non-unit quaternions: {skeleton['quaternion_norm']['outside_1_plus_minus_0_001']}")
            weights = report["vertices"]["selected_geometry"]
            sums = weights["weight_sum"]
            print(f"Weights: {weights['effective_influence_histogram']} | "
                  f"unweighted vertices: {weights['unweighted_vertices']} | "
                  f"weight-sum range: {sums['min']!s} .. {sums['max']!s}")
            print("Findings:")
            if not report["findings"]:
                print("  No findings. Structural checks passed.")
            else:
                for item in report["findings"]:
                    print(f"  [{item['severity'].upper()}] {item['code']}: {item['message']}")
            if args.json_path:
                out = Path(args.json_path)
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
                print(f"JSON report: {out}")
            return 2 if summary["status"] == "error" else 0
        if args.command == "export-animated-glb":
            from smd_reader import SMDParseError, parse_smd_bytes
            from smd_animation import build_animation_clip

            animation_path = Path(args.animation)
            if not animation_path.is_file():
                raise ReaderError(f"Animation SMD does not exist or is not a file: {animation_path}")
            if animation_path.suffix.lower() != ".smd":
                raise ReaderError("--animation must point to one .smd file")
            if animation_path.stat().st_size > MAX_SMD_FILE_BYTES:
                raise ReaderError(f"Animation SMD exceeds the {MAX_SMD_FILE_BYTES}-byte safety limit")
            try:
                animation_document = parse_smd_bytes(animation_path.read_bytes(), str(animation_path))
                scene = model.build_scene(args.lod, args.skin, choices)
                animation_clip = build_animation_clip(
                    animation_document, scene.bones, args.fps, args.interpolation
                )
            except (SMDParseError, ValueError) as exc:
                raise ReaderError(f"Animation SMD could not be exported: {exc}") from exc
            report = write_glb_file(scene, Path(args.output), animation_clip)
            report["warnings"] = list(dict.fromkeys(scene.warnings + report.get("warnings", [])))
            report_path = Path(args.json_path) if args.json_path else Path(args.output).with_suffix(".report.json")
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            animation_summary = report.get("animation") or {}
            print(f"Exported animated character scene: {report['output']}")
            print(f"Bones: {report['bone_count']} | Unified vertices: {report['unified_vertex_count']} | "
                  f"Triangles: {report['triangle_count']} | Material primitives: {report['primitive_count']}")
            print(f"Animation: {animation_summary.get('name')} | FPS: {animation_summary.get('fps')} | "
                  f"Frames: {animation_summary.get('source_frame_count')} | "
                  f"Duration: {animation_summary.get('duration_seconds'):.6g}s | "
                  f"Animated bones: {animation_summary.get('animated_bone_count')} | "
                  f"Sparse tracks: {animation_summary.get('sparse_track_count')} | "
                  f"Static tracks elided: {animation_summary.get('static_tracks_elided_count')}")
            print(f"Report: {report_path}")
            if report["warnings"]:
                print("Warnings:")
                for warning in report["warnings"]:
                    print(f"  - {warning}")
            return 0
        if args.command == "export-glb":
            report = model.write_glb(Path(args.output), args.lod, args.skin, choices)
            print(f"Exported unified scene: {report['output']}")
            print(f"Bones: {report['bone_count']} | Unified vertices: {report['unified_vertex_count']} | "
                  f"Triangles: {report['triangle_count']} | Material primitives: {report['primitive_count']}")
        else:
            report = model.write_smd(Path(args.output), args.lod, args.skin, choices)
            print(f"Exported: {report['output']}")
            print(f"Bones: {report['bone_count']} | Triangles: {report['triangle_count']} | "
                  f"Referenced vertices: {report['unique_source_vertices_referenced']}")
        print(f"Report: {Path(args.output).with_suffix('.report.json')}")
        if report["warnings"]:
            print("Warnings:")
            for warning in report["warnings"]:
                print(f"  - {warning}")
        return 0
    except (ReaderError, OSError, zipfile.BadZipFile, struct.error, ValueError, TypeError,
            KeyError, RecursionError, OverflowError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    finally:
        if model:
            model.bundle.close()


if __name__ == "__main__":
    raise SystemExit(main())
