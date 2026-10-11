import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("coverage_import", Path(__file__).with_name("sonar-unity-coverage.py"))
coverage_import = importlib.util.module_from_spec(spec)
spec.loader.exec_module(coverage_import)


class CoverageImportTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        (self.root / "Editor").mkdir()
        (self.root / "Editor/A.cs").write_text("class A {}\n")
        self.bundle = self.root / "bundle"
        (self.bundle / "opencover").mkdir(parents=True)
        self.output = self.root / "coverage.xml"
        self.manifest = {"package_root": "C:/measured/package", "files": coverage_import.source_hashes(self.root)}
        self.write_manifest()
        (self.bundle / "tests.xml").write_text('<test-run passed="2" failed="0"/>')
        self.write_report("one.xml", "C:/measured/package/Editor/A.cs", 0)

    def write_manifest(self):
        (self.bundle / "sources.json").write_text(json.dumps(self.manifest))

    def write_report(self, name, path, visits):
        (self.bundle / "opencover" / name).write_text(f'''<CoverageSession><Modules><Module>
        <Files><File uid="1" fullPath="{path}"/></Files><Classes><Class><Methods><Method><SequencePoints>
        <SequencePoint fileid="1" sl="1" vc="{visits}"/>
        <SequencePoint fileid="1" sl="2" vc="0"/>
        </SequencePoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>''')

    def convert(self):
        return coverage_import.convert(self.root, self.bundle, self.output)

    def test_merges_real_visits_and_preserves_uncovered_lines(self):
        self.write_report("two.xml", "C:\\measured\\package\\Editor\\A.cs", 3)
        self.assertEqual(self.convert(), {"lines": 2, "covered": 1, "reports": 2})
        lines = ET.parse(self.output).findall("./file/lineToCover")
        self.assertEqual([p.get("covered") for p in lines], ["true", "false"])

    def test_rejects_stale_sources(self):
        (self.root / "Editor/A.cs").write_text("class Changed {}\n")
        with self.assertRaisesRegex(ValueError, "sources differ"):
            self.convert()

    def test_rejects_deleted_sources(self):
        (self.root / "Editor/A.cs").unlink()
        with self.assertRaisesRegex(ValueError, "sources differ"):
            self.convert()

    def test_rejects_failed_or_empty_runs(self):
        for tests in ('<test-run passed="1" failed="1"/>', '<test-run passed="0" failed="0"/>'):
            (self.bundle / "tests.xml").write_text(tests)
            with self.assertRaisesRegex(ValueError, "passing Unity"):
                self.convert()

    def test_rejects_unknown_paths_and_empty_measurements(self):
        for path in ("C:/elsewhere/A.cs", "C:/measured/package/Editor/../A.cs"):
            self.write_report("one.xml", path, 1)
            with self.assertRaises(ValueError):
                self.convert()
        self.write_report("one.xml", "C:/measured/package/Editor/A.cs", 0)
        with self.assertRaisesRegex(ValueError, "No executed"):
            self.convert()


if __name__ == "__main__":
    unittest.main()
