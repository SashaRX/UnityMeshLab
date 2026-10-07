"""Controls for source orientation and complete fixed-cut UV validation."""
from pathlib import Path
import importlib.util
import tempfile
import unittest

import numpy as np

from autouv_probe import save, verify_geometry
from seams import topology

if all(importlib.util.find_spec(name) is not None for name in ("igl", "scipy", "matplotlib")):
    from fixed_cut_slim import DiskSlim, valid_uv, validate_atlas
else:
    DiskSlim = None


class SourceOrientationChecks(unittest.TestCase):
    def test_face_reordering_is_allowed_but_reversed_face_is_rejected(self):
        p = np.array([[0., 0, 0], [1., 0, 0], [0., 1, 0], [0., 0, 1]])
        f = np.array([[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]])
        capture = p, p[:, :2], f, np.zeros(4, dtype=int), 1
        reference = topology(capture)
        verify_geometry((p, p[:, :2], f[::-1, [1, 2, 0]], capture[3], 1), reference)
        reversed_faces = f.copy()
        reversed_faces[0] = reversed_faces[0, ::-1]
        with self.assertRaisesRegex(ValueError, "winding"):
            verify_geometry((p, p[:, :2], reversed_faces, capture[3], 1), reference)


@unittest.skipIf(DiskSlim is None, "optional libigl/SciPy/Matplotlib dependencies unavailable")
class FixedCutChecks(unittest.TestCase):
    def test_positive_orientation_does_not_hide_nonlocal_containment(self):
        uv = np.array([[0., 0], [1., 0], [0., 1], [0.1, 0.1], [0.3, 0.1], [0.1, 0.3]])
        f = np.arange(6).reshape(-1, 3)
        self.assertFalse(valid_uv(uv, f))
        uv[3:] += 2
        self.assertTrue(valid_uv(uv, f))

    def test_shared_edge_zero_area_contact_is_allowed(self):
        uv = np.array([[0., 0], [1., 0], [0., 1], [1., 1]])
        self.assertTrue(valid_uv(uv, np.array([[0, 1, 2], [1, 3, 2]])))

    def test_closed_chart_is_rejected_instead_of_silently_cut(self):
        p = np.array([[0., 0, 0], [1., 0, 0], [0., 1, 0], [0., 0, 1]])
        f = np.array([[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]])
        with self.assertRaisesRegex(ValueError, "disk"):
            DiskSlim.parameterize_chart(p, f, np.arange(4))

    def test_saved_float32_atlas_has_global_overlap_and_bounds_gate(self):
        p = np.array([[0., 0, 0], [1., 0, 0], [0., 1, 0], [0., 0, 1], [1., 0, 1], [0., 1, 1]])
        f = np.arange(6).reshape(-1, 3)
        uv = np.array([[0., 0], [.4, 0], [0., .4], [.6, .6], [1., .6], [.6, 1.]])
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "atlas.bin"
            save(path, p, uv, f, np.array([0, 1]))
            self.assertEqual(validate_atlas(path)["overlapPairs"], 0)
            for changed in (uv - [.05, 0], np.vstack((uv[:3], uv[:3]))):
                save(path, p, changed, f, np.array([0, 1]))
                with self.assertRaisesRegex(ValueError, "quality scan"):
                    validate_atlas(path)


if __name__ == "__main__":
    unittest.main()
