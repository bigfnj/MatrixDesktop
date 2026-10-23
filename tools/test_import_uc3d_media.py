"""Synthetic PE/resource tests. No donor file or installed software is required."""
from dataclasses import replace
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import sys
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("uc3d_media_import", Path(__file__).with_name("import-uc3d-media.py"))
importer = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = importer
spec.loader.exec_module(importer)


def synthetic_dib():
    header = bytearray(40)
    struct.pack_into("<IiiHHII", header, 0, 40, 6, 2, 1, 8, 0, 16)
    palette = b"".join(bytes((value, value, value, 0)) for value in range(256))
    return bytes(header) + palette + bytes((0, 1, 2, 3, 4, 5, 0, 0, 10, 11, 12, 13, 14, 15, 0, 0))


def fixture():
    blobs = {
        ("IPIC", 201, 1033): b"\xff\xd8\xfffixture-a\xff\xd9",
        ("IPIC", 202, 1033): b"\xff\xd8\xfffixture-b\xff\xd9",
        (2, 101, 1033): synthetic_dib(),
        ("WAVE", 106, 1033): b"RIFF" + struct.pack("<I", 40) + b"WAVEfmt "
        + struct.pack("<IHHIIHH", 16, 1, 1, 8000, 8000, 1, 8) + b"data" + struct.pack("<I", 4) + bytes(4),
    }
    section = bytearray(0x3000)
    tree = {}
    for keys, data in blobs.items():
        tree.setdefault(keys[0], {}).setdefault(keys[1], {})[keys[2]] = data
    cursor = 0
    descriptors = {}

    def allocate(size):
        nonlocal cursor
        start = cursor
        cursor = (cursor + size + 3) & ~3
        assert cursor < 0x2000
        return start

    def directory(items, path=()):
        location = allocate(16 + len(items) * 8)
        struct.pack_into("<HH", section, location + 12, 0, len(items))
        for index, (key, value) in enumerate(items.items()):
            if isinstance(key, str):
                encoded = key.encode("utf-16le")
                name_location = allocate(2 + len(encoded))
                struct.pack_into("<H", section, name_location, len(key))
                section[name_location + 2:name_location + 2 + len(encoded)] = encoded
                name = 0x80000000 | name_location
            else:
                name = key
            keys = path + (key,)
            if isinstance(value, dict):
                target = 0x80000000 | directory(value, keys)
            else:
                target = allocate(16)
                data_location = allocate(len(value))
                section[data_location:data_location + len(value)] = value
                struct.pack_into("<IIII", section, target, 0x1000 + data_location, len(value), 0, 0)
                descriptors[keys] = target
            struct.pack_into("<II", section, location + 16 + index * 8, name, target)
        return location

    assert directory(tree) == 0
    resource_size = cursor
    struct.pack_into("<HHH", section, 0x2000, 201, 202, 201)
    for index, caption in enumerate(("Fixture A", "Fixture B", "Repeated image")):
        encoded = caption.encode("ascii") + b"\0"
        section[0x2100 + index * 50:0x2100 + index * 50 + len(encoded)] = encoded
    header = bytearray(0x200)
    header[:2] = b"MZ"
    struct.pack_into("<I", header, 0x3C, 0x80)
    header[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HH", header, 0x84, 0x14C, 1)
    struct.pack_into("<H", header, 0x94, 0xE0)
    struct.pack_into("<H", header, 0x98, 0x10B)
    struct.pack_into("<I", header, 0x98 + 28, 0x10000000)
    struct.pack_into("<I", header, 0x98 + 92, 16)
    struct.pack_into("<II", header, 0x98 + 112, 0x1000, resource_size)
    header[0x178:0x180] = b".rsrc\0\0\0"
    struct.pack_into("<IIII", header, 0x180, len(section), 0x1000, len(section), 0x200)
    data = bytes(header + section)
    layout = importer.MediaLayout(hashlib.sha256(data).hexdigest(), 0x10003000, 0x10003100, 3,
                                  reloaded_range=(1, 1), revolutions_range=(1, 2), glyph_columns=2, glyph_size=2)
    return data, layout, descriptors


class ImportTests(unittest.TestCase):
    def setUp(self):
        self.data, self.layout, self.descriptors = fixture()

    def altered(self, data):
        return replace(self.layout, sha256=hashlib.sha256(data).hexdigest())

    def test_repeated_resources_keep_caption_slots_and_overlapping_groups(self):
        manifest, files = importer.prepare_library(self.data, self.layout)
        self.assertEqual(manifest["schema"], 1)
        self.assertEqual([entry["originalIndex"] for entry in manifest["images"]], [0, 1, 2])
        self.assertEqual([entry["caption"] for entry in manifest["images"]], ["Fixture A", "Fixture B", "Repeated image"])
        self.assertEqual(manifest["images"][0]["groups"], ["all", "matrix"])
        self.assertEqual(manifest["images"][1]["groups"], ["all", "reloaded", "revolutions"])
        self.assertEqual(manifest["images"][2]["groups"], ["all", "revolutions"])
        self.assertEqual(manifest["images"][0]["file"], manifest["images"][2]["file"])
        self.assertEqual(len([name for name in files if name.endswith(".jpg")]), 2)

    def test_atlas_crops_used_cells_and_preserves_each_scanline(self):
        bmp = importer.glyph_bitmap(synthetic_dib(), 2, 2)
        self.assertEqual(struct.unpack_from("<ii", bmp, 18), (4, 2))
        self.assertEqual(struct.unpack_from("<I", bmp, 2)[0], len(bmp))
        pixels = struct.unpack_from("<I", bmp, 10)[0]
        self.assertEqual(bmp[pixels:], bytes((0, 1, 2, 3, 10, 11, 12, 13)))

    def test_optional_auxiliary_resources_are_not_required_when_disabled(self):
        manifest, files = importer.prepare_library(self.data, self.layout, glyphs=False, sound=False)
        self.assertNotIn("glyphs", manifest)
        self.assertNotIn("sound", manifest)
        self.assertEqual(set(files), {"resource-201.jpg", "resource-202.jpg"})

    def test_actual_writer_publishes_conventional_names_and_manifest(self):
        with tempfile.TemporaryDirectory(prefix="uc3d-media-test-") as temporary:
            root = Path(temporary)
            source = root / "fixture.dll"
            output = root / "library"
            source.write_bytes(self.data)
            result = importer.import_library(source, output, layout=self.layout)
            self.assertEqual(result["images"], 3)
            self.assertEqual(result["unique_image_files"], 2)
            self.assertEqual(source.read_bytes(), self.data)
            self.assertEqual(set(path.name for path in output.iterdir()),
                             {"resource-201.jpg", "resource-202.jpg", "glyphs.bmp", "code-sizzle.wav", "uc3d-media.json"})
            self.assertEqual(json.loads((output / "uc3d-media.json").read_text())["images"][2]["caption"], "Repeated image")

    def test_existing_output_is_preserved(self):
        with tempfile.TemporaryDirectory(prefix="uc3d-media-test-") as temporary:
            root = Path(temporary)
            source = root / "fixture.dll"
            source.write_bytes(self.data)
            output = root / "existing-library"
            output.mkdir()
            sentinel = output / "keep.txt"
            sentinel.write_text("keep")
            with self.assertRaisesRegex(importer.ImportFailure, "already exists"):
                importer.import_library(source, output, layout=self.layout)
            self.assertEqual(sentinel.read_text(), "keep")

    def test_unknown_binary_identity_is_rejected(self):
        with self.assertRaisesRegex(importer.ImportFailure, "identity"):
            importer.prepare_library(self.data + b"changed", self.layout)

    def test_invalid_caption_creates_no_output(self):
        changed = bytearray(self.data)
        changed[0x2300:0x2300 + 50] = b"X" * 50
        with tempfile.TemporaryDirectory(prefix="uc3d-media-test-") as temporary:
            source = Path(temporary) / "fixture.dll"
            output = Path(temporary) / "library"
            source.write_bytes(changed)
            with self.assertRaisesRegex(importer.ImportFailure, "Caption 0"):
                importer.import_library(source, output, layout=self.altered(changed))
            self.assertFalse(output.exists())

    def test_missing_picture_resource_is_named(self):
        changed = bytearray(self.data)
        struct.pack_into("<H", changed, 0x2200, 999)
        with self.assertRaisesRegex(importer.ImportFailure, "IPIC/999"):
            importer.prepare_library(changed, self.altered(changed))

    def test_missing_jpeg_end_marker_is_rejected(self):
        changed = self.data.replace(b"fixture-a\xff\xd9", b"fixture-aXX")
        with self.assertRaisesRegex(importer.ImportFailure, "complete JPEG"):
            importer.prepare_library(changed, self.altered(changed))

    def test_truncated_pe_and_section_are_rejected(self):
        for data in (b"MZ", self.data[:0x200], self.data[:-1]):
            with self.subTest(length=len(data)), self.assertRaises(importer.ImportFailure):
                importer.PortableExecutable(data)

    def test_virtual_tail_and_ambiguous_rvas_are_rejected(self):
        pe = importer.PortableExecutable(self.data)
        with self.assertRaisesRegex(importer.ImportFailure, "unmapped"):
            pe.file_offset(0x4000, 1)
        pe.sections.append(pe.sections[0])
        with self.assertRaisesRegex(importer.ImportFailure, "ambiguous"):
            pe.file_offset(0x1000, 16)

    def test_cyclic_resource_directory_is_rejected(self):
        changed = bytearray(self.data)
        struct.pack_into("<I", changed, 0x200 + 20, 0x80000000)
        with self.assertRaisesRegex(importer.ImportFailure, "Cyclic"):
            importer.PortableExecutable(changed).resources()

    def test_outside_resource_name_and_node_exhaustion_are_rejected(self):
        changed = bytearray(self.data)
        struct.pack_into("<I", changed, 0x200 + 16, 0x800FFFFF)
        with self.assertRaisesRegex(importer.ImportFailure, "escapes"):
            importer.PortableExecutable(changed).resources()
        changed = bytearray(self.data)
        struct.pack_into("<HH", changed, 0x200 + 12, 0xFFFF, 0xFFFF)
        with self.assertRaisesRegex(importer.ImportFailure, "node limit"):
            importer.PortableExecutable(changed).resources()

    def test_resource_data_cannot_point_outside_file_backed_image(self):
        changed = bytearray(self.data)
        descriptor = self.descriptors[("IPIC", 201, 1033)]
        struct.pack_into("<I", changed, 0x200 + descriptor, 0xFFFFFFF0)
        with self.assertRaises(importer.ImportFailure):
            importer.PortableExecutable(changed).resources()

    def test_bitmap_geometry_palette_and_data_are_validated(self):
        for offset, value in ((4, 1), (16, 1), (32, 257)):
            changed = bytearray(synthetic_dib())
            struct.pack_into("<I", changed, offset, value)
            with self.subTest(offset=offset), self.assertRaises(importer.ImportFailure):
                importer.glyph_bitmap(changed, 2, 2)
        with self.assertRaisesRegex(importer.ImportFailure, "truncated"):
            importer.glyph_bitmap(synthetic_dib()[:-1], 2, 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
