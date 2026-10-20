"""Optional solver controls; standard-library report tests remain dependency-free."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

AVAILABLE = all(importlib.util.find_spec(name) is not None for name in ("igl", "numpy", "scipy", "matplotlib"))
if AVAILABLE:
    from slim_probe import probe


@unittest.skipUnless(AVAILABLE, "optional SLIM environment unavailable")
class ProbeTests(unittest.TestCase):
    def test_closed_low_stretch_patch_is_reported_as_unsupported(self):
        """A low-distortion UV assignment does not make a tetrahedron a disk."""
        capture = dict(positions=[dict(zip(("x", "y", "z"), p)) for p in
                                  ((0, 0, 0), (1, 0, 0), (0, 1, 0), (0, 0, 1))],
                       uv=[dict(zip(("x", "y"), p)) for p in ((0, 0), (1, 0), (0, 1), (1, 1))],
                       triangles=[0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tetra.json"
            path.write_text(json.dumps(capture), encoding="utf-8")
            report = probe(path, Path(directory) / "out")
        self.assertEqual(len(report["patches"]), 1)
        self.assertEqual(report["patches"][0]["status"], "refused")
        self.assertIn("disk", report["patches"][0]["error"])


if __name__ == "__main__":
    unittest.main()
