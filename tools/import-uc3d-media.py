#!/usr/bin/env python3
"""Import a local UC3D Matrix donor module into a private MatrixDesktop library.

This utility reads an explicitly supplied, unpacked v3.4 module without loading
or executing it. No donor captions, images, glyphs, or audio are bundled here.
Use UPX separately if needed: upx -d -o MatrixReloaded.dll MatrixReloaded.uc3d
The output directory must be new. Only the Python standard library is required.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
import hashlib
import json
from pathlib import Path
import struct
import sys

MAX_INPUT = 64 * 1024 * 1024
MAX_RESOURCE = 32 * 1024 * 1024
MAX_RESOURCE_NODES = 4096


class ImportFailure(ValueError):
    """The input does not satisfy the selected, bounded donor format."""


@dataclass(frozen=True)
class MediaLayout:
    sha256: str
    picture_ids: int
    captions: int
    count: int
    caption_stride: int = 50
    reloaded_range: tuple[int, int] = (5, 54)
    revolutions_range: tuple[int, int] = (50, 100)
    glyph_columns: int = 79
    glyph_size: int = 32


# Structural facts recovered from the owner's v3.4 module, never donor media.
# Addresses are virtual addresses at the PE's preferred image base.
DONOR_V34 = MediaLayout(
    sha256="9d0afd4c287b01538be1fdd6e81d4974140504d31738d2be4508fd4525683c7e",
    picture_ids=0x100165A4, captions=0x10016678, count=106,
)
PACKED_V34_SHA256 = "563743b1c67409d01d02c93f08c9bad62f08af33ce4401cfa3d2c71540e73e9a"


class PortableExecutable:
    """Bounded PE32 resource reader. Resource names never become output paths."""

    def __init__(self, data: bytes):
        self.data = data
        self.sections: list[tuple[int, int, int]] = []
        if len(data) < 64 or len(data) > MAX_INPUT or data[:2] != b"MZ":
            raise ImportFailure("Expected a bounded DOS/PE executable header.")
        pe = self.u32(0x3C)
        if self.read(pe, 4) != b"PE\0\0":
            raise ImportFailure("Missing PE signature.")
        machine, count = self.unpack("<HH", pe + 4)
        optional_size = self.u16(pe + 20)
        optional = pe + 24
        if machine != 0x14C or not 1 <= count <= 96 or optional_size < 120:
            raise ImportFailure("Expected a native x86 PE32 image.")
        self.read(optional, optional_size)
        if self.u16(optional) != 0x10B or self.u32(optional + 92) < 3:
            raise ImportFailure("PE32 resource directory is unavailable.")
        self.image_base = self.u32(optional + 28)
        for index in range(count):
            entry = optional + optional_size + index * 40
            self.read(entry, 40)
            virtual_size, rva, raw_size, raw = self.unpack("<IIII", entry + 8)
            if rva + max(raw_size, virtual_size) > 0x100000000:
                raise ImportFailure("Section address range overflows PE32.")
            self.read(raw, raw_size)
            self.sections.append((rva, raw_size, raw))
        resource_rva, self.resource_size = self.unpack("<II", optional + 112)
        if not 16 <= self.resource_size <= MAX_RESOURCE:
            raise ImportFailure("Resource directory has an invalid size.")
        self.resource_base = self.file_offset(resource_rva, self.resource_size)

    def read(self, offset: int, size: int) -> bytes:
        if offset < 0 or size < 0 or offset + size > len(self.data):
            raise ImportFailure("Truncated or out-of-bounds PE data.")
        return self.data[offset:offset + size]

    def unpack(self, fmt: str, offset: int) -> tuple:
        return struct.unpack(fmt, self.read(offset, struct.calcsize(fmt)))

    def u16(self, offset: int) -> int:
        return self.unpack("<H", offset)[0]

    def u32(self, offset: int) -> int:
        return self.unpack("<I", offset)[0]

    def file_offset(self, rva: int, size: int) -> int:
        if size < 0 or rva < 0 or rva + size > 0x100000000:
            raise ImportFailure("Invalid RVA range.")
        matches = [raw + rva - start for start, length, raw in self.sections
                   if start <= rva and rva + size <= start + length]
        if len(matches) != 1:
            raise ImportFailure("RVA is unmapped, virtual-only, or ambiguous.")
        self.read(matches[0], size)
        return matches[0]

    def at_address(self, address: int, size: int) -> bytes:
        return self.read(self.file_offset(address - self.image_base, size), size)

    def resource_relative(self, offset: int, size: int) -> int:
        if offset < 0 or size < 0 or offset + size > self.resource_size:
            raise ImportFailure("Resource metadata escapes its directory.")
        return self.resource_base + offset

    def resources(self) -> dict[tuple[str | int, str | int, str | int], bytes]:
        output = {}
        visited = set()
        nodes = 0

        def visit(relative: int, path: tuple):
            nonlocal nodes
            if relative in visited or len(path) > 2:
                raise ImportFailure("Cyclic, reused, or overly deep resource directory.")
            visited.add(relative)
            directory = self.resource_relative(relative, 16)
            named, numbered = self.unpack("<HH", directory + 12)
            total = named + numbered
            nodes += total + 1
            if nodes > MAX_RESOURCE_NODES:
                raise ImportFailure("Resource directory exceeds its node limit.")
            self.resource_relative(relative + 16, total * 8)
            for index in range(total):
                name, target = self.unpack("<II", directory + 16 + index * 8)
                if name & 0x80000000:
                    name_offset = name & 0x7FFFFFFF
                    location = self.resource_relative(name_offset, 2)
                    length = self.u16(location)
                    if length > 128:
                        raise ImportFailure("Resource name exceeds its length limit.")
                    location = self.resource_relative(name_offset + 2, length * 2)
                    try:
                        name = self.read(location, length * 2).decode("utf-16le")
                    except UnicodeDecodeError as error:
                        raise ImportFailure("Invalid Unicode resource name.") from error
                else:
                    name &= 0x7FFFFFFF
                names = path + (name,)
                if target & 0x80000000:
                    visit(target & 0x7FFFFFFF, names)
                    continue
                if len(names) != 3:
                    raise ImportFailure("Expected type/name/language resource layout.")
                descriptor = self.resource_relative(target, 16)
                rva, size, _, _ = self.unpack("<IIII", descriptor)
                if not 0 < size <= MAX_RESOURCE:
                    raise ImportFailure("Resource data exceeds its size limit.")
                blob = self.read(self.file_offset(rva, size), size)
                if names in output:
                    raise ImportFailure("Duplicate resource identity.")
                output[names] = blob

        visit(0, ())
        return output


def resource(resources: dict, kind: str | int, identity: int) -> bytes:
    matches = [blob for (type_name, number, _), blob in resources.items()
               if type_name == kind and number == identity]
    if not matches or any(blob != matches[0] for blob in matches[1:]):
        raise ImportFailure(f"Resource {kind}/{identity} is missing or has conflicting languages.")
    return matches[0]


def glyph_bitmap(dib: bytes, columns: int, cell_size: int) -> bytes:
    """Crop the used left-hand cells of the donor's uncompressed 8-bit DIB."""
    if len(dib) < 40:
        raise ImportFailure("Glyph bitmap header is truncated.")
    header_size, width, height, planes, bits, compression = struct.unpack_from("<IiiHHI", dib)
    if (header_size != 40 or planes != 1 or bits != 8 or compression != 0
            or height == 0 or abs(height) != cell_size or columns < 1 or cell_size < 1):
        raise ImportFailure("Glyph atlas must be an uncompressed 8-bit single-row DIB.")
    target_width = columns * cell_size
    if width < target_width or width > 16384 or abs(height) > 4096:
        raise ImportFailure("Glyph atlas dimensions are invalid.")
    palette_entries = struct.unpack_from("<I", dib, 32)[0] or 256
    if palette_entries > 256:
        raise ImportFailure("Glyph palette exceeds 8-bit bounds.")
    pixels = header_size + palette_entries * 4
    stride = (width + 3) & ~3
    target_stride = (target_width + 3) & ~3
    if pixels + stride * abs(height) > len(dib):
        raise ImportFailure("Glyph bitmap pixels are truncated.")
    header = bytearray(dib[:pixels])
    struct.pack_into("<i", header, 4, target_width)
    struct.pack_into("<I", header, 20, target_stride * abs(height))
    rows = [dib[pixels + row * stride:pixels + row * stride + target_width]
            + bytes(target_stride - target_width) for row in range(abs(height))]
    cropped = bytes(header) + b"".join(rows)
    return b"BM" + struct.pack("<IHHI", 14 + len(cropped), 0, 0, 14 + pixels) + cropped


def prepare_library(data: bytes, layout: MediaLayout, *, glyphs: bool = True, sound: bool = True):
    digest = hashlib.sha256(data).hexdigest()
    if digest != layout.sha256:
        if digest == PACKED_V34_SHA256:
            raise ImportFailure("The module is UPX-packed. Unpack a separate copy with upx -d -o first.")
        raise ImportFailure("Unsupported donor module identity; expected the unpacked v3.4 Matrix module.")
    if not 1 <= layout.count <= 256 or not 1 <= layout.caption_stride <= 200:
        raise ImportFailure("Unsupported donor table bounds.")
    pe = PortableExecutable(data)
    resources = pe.resources()
    ids = struct.unpack("<" + "H" * layout.count, pe.at_address(layout.picture_ids, layout.count * 2))
    captions = pe.at_address(layout.captions, layout.count * layout.caption_stride)
    images = []
    files = {}
    for index, identity in enumerate(ids):
        raw_caption = captions[index * layout.caption_stride:(index + 1) * layout.caption_stride]
        if b"\0" not in raw_caption:
            raise ImportFailure(f"Caption {index} is not terminated within its table slot.")
        caption = raw_caption.split(b"\0", 1)[0].decode("cp1252", errors="replace")
        blob = resource(resources, "IPIC", identity)
        if not blob.startswith(b"\xff\xd8\xff") or not blob.endswith(b"\xff\xd9"):
            raise ImportFailure(f"Image resource {identity} is not a complete JPEG.")
        name = f"resource-{identity}.jpg"
        files[name] = blob
        groups = ["all"]
        if layout.reloaded_range[0] <= index <= layout.reloaded_range[1]:
            groups.append("reloaded")
        if layout.revolutions_range[0] <= index <= layout.revolutions_range[1]:
            groups.append("revolutions")
        if len(groups) == 1:
            groups.append("matrix")
        images.append({"file": name, "caption": caption, "groups": groups, "originalIndex": index})
    manifest = {"schema": 1, "source": {"sha256": digest, "profile": "uc3d-matrix-donor-3.4"}, "images": images}
    if glyphs:
        files["glyphs.bmp"] = glyph_bitmap(resource(resources, 2, 101), layout.glyph_columns, layout.glyph_size)
        manifest["glyphs"] = {"file": "glyphs.bmp", "columns": layout.glyph_columns, "rows": 1, "cellSize": layout.glyph_size}
    if sound:
        wav = resource(resources, "WAVE", 106)
        if len(wav) < 12 or wav[:4] != b"RIFF" or wav[8:12] != b"WAVE" or struct.unpack_from("<I", wav, 4)[0] + 8 > len(wav):
            raise ImportFailure("Code sound resource is not a complete RIFF/WAVE file.")
        files["code-sizzle.wav"] = wav
        manifest["sound"] = {"file": "code-sizzle.wav"}
    return manifest, files


def import_library(source: Path, destination: Path, *, layout: MediaLayout = DONOR_V34,
                   glyphs: bool = True, sound: bool = True) -> dict:
    if source.stat().st_size > MAX_INPUT:
        raise ImportFailure("Input exceeds the 64 MiB import limit.")
    if destination.exists():
        raise ImportFailure("Output directory already exists; choose a new private library directory.")
    with source.open("rb") as stream:
        data = stream.read(MAX_INPUT + 1)
    if len(data) > MAX_INPUT:
        raise ImportFailure("Input exceeds the 64 MiB import limit.")
    # Validate the complete input before creating any output directory.
    manifest, files = prepare_library(data, layout, glyphs=glyphs, sound=sound)
    destination.mkdir(parents=True, exist_ok=False)
    for name, blob in files.items():
        with (destination / name).open("xb") as stream:
            stream.write(blob)
    # Published last so an interrupted import cannot present a complete manifest.
    with (destination / "uc3d-media.json").open("x", encoding="utf-8") as stream:
        json.dump(manifest, stream, indent=2, ensure_ascii=False)
        stream.write("\n")
    return {"output": str(destination), "images": len(manifest["images"]),
            "unique_image_files": sum(name.endswith(".jpg") for name in files),
            "glyphs": glyphs, "sound": sound, "source_sha256": manifest["source"]["sha256"]}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Explicit unpacked MatrixReloaded v3.4 DLL/module")
    parser.add_argument("--output", required=True, type=Path, help="New private media library directory")
    parser.add_argument("--no-glyphs", action="store_true")
    parser.add_argument("--no-sound", action="store_true")
    args = parser.parse_args(argv)
    try:
        result = import_library(args.source, args.output, glyphs=not args.no_glyphs, sound=not args.no_sound)
    except (ImportFailure, OSError) as error:
        print(f"Import failed: {error}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
