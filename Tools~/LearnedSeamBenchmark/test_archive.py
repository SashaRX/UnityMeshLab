import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from archive import archive, _safe_output_path


class ArchiveChecks(unittest.TestCase):
    @staticmethod
    def manifest(root, entries):
        path = root / "manifest.json"
        path.write_text(json.dumps(entries), encoding="utf-8")
        return path

    @staticmethod
    def entry(asset="bust", family="original-A", files=None):
        return dict(asset_id=asset, family_id=family,
                    files=files if files is not None else [dict(role="artist", path="low.fbx")])

    def test_binary_bytes_relative_directories_hashes_and_family_grouping(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "low.fbx").write_bytes(b"\x00\xffraw FBX\r\n")
            source = root / "textures" / "nested"
            source.mkdir(parents=True)
            payload = bytes(range(256))
            (source / "нормаль.exr").write_bytes(payload)
            (root / "textures" / "empty").mkdir()
            manifest = self.manifest(root, [self.entry(files=[
                dict(role="artist", path="low.fbx"), dict(role="textures", path="textures")]),
                self.entry("bust-variant", files=[dict(role="source", path="low.fbx")])])
            original_manifest = manifest.read_bytes()
            out = root / "new-archive"
            result = archive(manifest, out)
            self.assertEqual((out / "assets/bust/artist/low.fbx").read_bytes(), (root / "low.fbx").read_bytes())
            self.assertEqual((out / "assets/bust/textures/textures/nested/нормаль.exr").read_bytes(), payload)
            self.assertTrue((out / "assets/bust/textures/textures/empty").is_dir())
            self.assertEqual(result["asset_count"], 2)
            self.assertEqual(result["file_count"], 3)
            self.assertEqual({asset["family_id"] for asset in result["assets"]}, {"original-A"})
            for record in result["files"]:
                self.assertEqual(record["input_sha256"], record["output_sha256"])
                self.assertEqual(record["output_sha256"], hashlib.sha256((out / record["archived"]).read_bytes()).hexdigest())
            self.assertEqual((out / "collection-manifest.json").read_bytes(), original_manifest)
            self.assertEqual(manifest.read_bytes(), original_manifest)
            self.assertFalse(result["dependency_discovery_performed"])
            self.assertFalse(result["completeness_verified"])
            self.assertFalse(result["fields_extracted"])

    def test_late_missing_input_preflight_preserves_inputs_and_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            original = b"unchanged model"
            (root / "low.fbx").write_bytes(original)
            manifest = self.manifest(root, [self.entry(files=[dict(role="artist", path="low.fbx"),
                                                             dict(role="source", path="missing.fbx")])])
            out = root / "archive"
            with self.assertRaisesRegex(ValueError, "Missing explicitly listed"):
                archive(manifest, out)
            self.assertFalse(out.exists())
            self.assertEqual((root / "low.fbx").read_bytes(), original)
            out.mkdir()
            (out / "keep.txt").write_bytes(b"existing archive")
            with self.assertRaisesRegex(ValueError, "brand-new"):
                archive(manifest, out)
            self.assertEqual((out / "keep.txt").read_bytes(), b"existing archive")

    def test_windows_case_collision_even_on_case_sensitive_filesystem(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for directory, filename in (("a", "Map.png"), ("b", "map.png")):
                (root / directory).mkdir()
                (root / directory / filename).write_bytes(directory.encode())
            manifest = self.manifest(root, [self.entry(files=[dict(role="textures", path="a/Map.png"),
                                                             dict(role="textures", path="b/map.png")])])
            out = root / "archive"
            with self.assertRaisesRegex(ValueError, "Windows path collision"):
                archive(manifest, out)
            self.assertFalse(out.exists())
            self.assertEqual((root / "a/Map.png").read_bytes(), b"a")
            self.assertEqual((root / "b/map.png").read_bytes(), b"b")

    def test_unsafe_ids_duplicate_assets_and_file_directory_collision_fail_before_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "low.fbx").write_bytes(b"model")
            cases = [self.entry(asset="../escape"), self.entry(asset="CON"),
                     self.entry(files=[dict(role="AUX.txt", path="low.fbx")]),
                     self.entry(family="family/escape")]
            for case in cases:
                manifest = self.manifest(root, [case])
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    archive(manifest, root / "archive")
                self.assertFalse((root / "archive").exists())
            manifest = self.manifest(root, [self.entry("Bust"), self.entry("bust")])
            with self.assertRaisesRegex(ValueError, "asset_id"):
                archive(manifest, root / "archive")
            (root / "other").mkdir()
            (root / "other/low.fbx").mkdir()
            (root / "other/low.fbx/inside.txt").write_bytes(b"nested")
            manifest = self.manifest(root, [self.entry(files=[dict(role="artist", path="low.fbx"),
                dict(role="artist", path="other/low.fbx")])])
            with self.assertRaisesRegex(ValueError, "Windows path collision"):
                archive(manifest, root / "archive")
            self.assertFalse((root / "archive").exists())

    def test_differently_cased_roles_with_distinct_files_fail_before_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "normal.png").write_bytes(b"normal map")
            (root / "color.png").write_bytes(b"color map")
            manifest = self.manifest(root, [self.entry(files=[dict(role="textures", path="normal.png"),
                                                             dict(role="Textures", path="color.png")])])
            out = root / "new-parent/archive"
            with self.assertRaisesRegex(ValueError, "Windows path collision"):
                archive(manifest, out)
            self.assertFalse(out.parent.exists())
            self.assertEqual((root / "normal.png").read_bytes(), b"normal map")
            self.assertEqual((root / "color.png").read_bytes(), b"color map")

    def test_output_inside_explicit_tree_is_rejected_without_self_copy(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "inputs").mkdir()
            (root / "inputs/source.max").write_bytes(b"native scene")
            manifest = self.manifest(root, [self.entry(files=[dict(role="native", path="inputs")])])
            out = root / "inputs/archive"
            with self.assertRaisesRegex(ValueError, "self-copy"):
                archive(manifest, out)
            self.assertFalse(out.exists())
            self.assertEqual(list((root / "inputs").iterdir()), [root / "inputs/source.max"])

    def test_unsafe_output_components_fail_without_creating_output_or_parents(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "low.fbx").write_bytes(b"untouched source")
            manifest = self.manifest(root, [self.entry()])
            parent = root / "new-parent"
            for component in ("AUX.txt", "archive.", "archive "):
                with self.assertRaisesRegex(ValueError, "Unsafe Windows path component"):
                    archive(manifest, parent / component)
                self.assertFalse(parent.exists())
                self.assertEqual((root / "low.fbx").read_bytes(), b"untouched source")

    @unittest.skipUnless(os.name == "nt", "Windows drive and UNC path syntax")
    def test_valid_drive_and_unc_anchors_are_not_treated_as_components(self):
        _safe_output_path(Path("C:/dataset/new-archive"))
        _safe_output_path(Path("//server/share/dataset/new-archive"))

    def test_symlink_tree_entry_and_symlink_ancestor_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "real").mkdir()
            (root / "real/source.max").write_bytes(b"private original")
            (root / "input").mkdir()
            link = root / "input/link"
            try:
                link.symlink_to(root / "real", target_is_directory=True)
            except (NotImplementedError, OSError) as error:
                self.skipTest(f"Symlink creation unavailable on this host: {error}")
            for path in ("input", "input/link/source.max"):
                manifest = self.manifest(root, [self.entry(files=[dict(role="source", path=path)])])
                with self.assertRaisesRegex(ValueError, "Symlink/junction"):
                    archive(manifest, root / "archive")
                self.assertFalse((root / "archive").exists())
            self.assertEqual((root / "real/source.max").read_bytes(), b"private original")

    @unittest.skipUnless(os.name == "nt", "Windows junction fixture")
    def test_windows_junction_tree_input_and_output_ancestor_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "real").mkdir()
            (root / "real/source.max").write_bytes(b"native scene")
            (root / "input").mkdir()
            link = root / "input/junction"
            result = subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(root / "real")],
                                    capture_output=True, check=False)
            if result.returncode:
                self.skipTest(f"Junction creation unavailable: {result.stderr!r}")
            for path in ("input", "input/junction/source.max"):
                manifest = self.manifest(root, [self.entry(files=[dict(role="source", path=path)])])
                with self.assertRaisesRegex(ValueError, "Symlink/junction"):
                    archive(manifest, root / "archive")
                self.assertFalse((root / "archive").exists())
            manifest = self.manifest(root, [self.entry(files=[dict(role="source", path="real/source.max")])])
            with self.assertRaisesRegex(ValueError, "Symlink/junction"):
                archive(manifest, link / "archive")
            self.assertEqual((root / "real/source.max").read_bytes(), b"native scene")
            self.assertFalse((root / "real/archive").exists())


if __name__ == "__main__":
    unittest.main()
