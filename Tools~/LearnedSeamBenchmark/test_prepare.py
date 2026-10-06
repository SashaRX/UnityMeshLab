import json
from pathlib import Path
import struct
import tempfile
import unittest

import numpy as np

from prepare import extract, prepare


def tetrahedron(split_vertices=False, seam=False):
    positions = np.array([[0., 0, 0], [1., 0, 0], [0., 1, 0], [0., 0, 1]])
    faces = np.array([[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]])
    uv = positions[:, :2].copy()
    if split_vertices:
        positions, uv = positions[faces.ravel()], uv[faces.ravel()]
        faces = np.arange(12).reshape(4, 3)
        if seam:
            uv[:3] += 0.2
    return positions, uv, faces, np.arange(len(positions)), 4


class PrepareChecks(unittest.TestCase):
    def test_uv_labels_ignore_normal_vertex_splits_and_chart_ids(self):
        plain, plain_hash = extract(tetrahedron())
        split, split_hash = extract(tetrahedron(True))
        cut, cut_hash = extract(tetrahedron(True, True))
        self.assertEqual(plain_hash, split_hash)
        self.assertEqual(plain_hash, cut_hash)
        self.assertFalse(split["seam_labels"].any())
        self.assertEqual(cut["seam_labels"].sum(), 3)
        self.assertFalse(cut["forced_boundary"].any())

    def test_geometry_hash_and_labels_survive_index_face_reordering(self):
        original = tetrahedron(True, True)
        p, uv, f, charts, nc = original
        order = np.arange(len(p))[::-1]
        reverse = np.argsort(order)
        reordered = p[order], uv[order], reverse[f[::-1, [1, 2, 0]]], charts[order], nc
        a, ah = extract(original)
        b, bh = extract(reordered)
        self.assertEqual(ah, bh)
        np.testing.assert_array_equal(a["edges"], b["edges"])
        np.testing.assert_array_equal(a["seam_labels"], b["seam_labels"])
        np.testing.assert_allclose(a["edge_features"], b["edge_features"])

    def test_boundary_edges_are_forced_and_never_training_labels(self):
        p, uv, f, chart, nc = tetrahedron()
        data, _ = extract((p, uv, f[:1], chart, nc))
        self.assertEqual(data["forced_boundary"].sum(), 3)
        self.assertFalse(data["learned_mask"].any())
        self.assertFalse(data["seam_labels"].any())

    def test_features_survive_uniform_scale_and_translation(self):
        p, uv, f, chart, nc = tetrahedron(True, True)
        a, _ = extract((p, uv, f, chart, nc))
        b, _ = extract((p * 0.001 + [2., -3., 4.], uv, f, chart, nc))
        np.testing.assert_allclose(a["edge_features"], b["edge_features"], rtol=1e-6)
        np.testing.assert_array_equal(a["seam_labels"], b["seam_labels"])

    def test_bad_topology_is_rejected(self):
        p, uv, f, chart, nc = tetrahedron()
        for bad in (np.array([[0, 0, 1]]), np.vstack((f, f[0])),
                    np.array([[0, 1, 2], [1, 0, 3], [0, 1, 4]])):
            with self.assertRaisesRegex(ValueError, "Degenerate|Nonmanifold"):
                extract((np.vstack((p, [0, 0, -1])), np.vstack((uv, [0, -1])), bad, chart, nc))
        with self.assertRaisesRegex(ValueError, "winding"):
            extract((p, uv, np.vstack((f[:1, ::-1], f[1:])), chart, nc))
        with self.assertRaisesRegex(ValueError, "vertex fan"):
            extract((np.array([[0.,0,0], [1.,0,0], [0.,1,0], [-1.,0,0], [0.,-1,0]]),
                     np.zeros((5, 2)), np.array([[0,1,2], [0,3,4]]), np.zeros(5), 1))

    def test_invalid_numeric_arrays_never_produce_labels(self):
        p, uv, f, chart, nc = tetrahedron()
        for invalid in (np.nan, np.inf, -np.inf):
            bad_p = p.copy(); bad_p[0, 0] = invalid
            bad_uv = uv.copy(); bad_uv[0, 0] = invalid
            for pp, uu in ((bad_p, uv), (p, bad_uv)):
                with self.assertRaisesRegex(ValueError, "Non-finite"):
                    extract((pp, uu, f, chart, nc))
        for ff in (f[:0], np.array([[-1, 1, 2]]), np.array([[0, 1, len(p)]]), f.astype(float)):
            with self.assertRaisesRegex(ValueError, "Invalid|empty"):
                extract((p, uv, ff, chart, nc))
        for pp in (p[:0], np.zeros_like(p), np.array([[0.,0,0], [1.,0,0], [2.,0,0], [3.,0,0]])):
            with self.assertRaisesRegex(ValueError, "Invalid|empty|Degenerate"):
                extract((pp, uv[:len(pp)], f, chart, nc))

    def test_nonempty_output_is_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            manifest = root / "report.json"
            original = b'[]'
            manifest.write_bytes(original)
            with self.assertRaisesRegex(ValueError, "never overwritten"):
                prepare(manifest, root)
            self.assertEqual(manifest.read_bytes(), original)
            existing = root / "capture-0000.npz"
            existing.write_bytes(b"original capture")
            with self.assertRaisesRegex(ValueError, "never overwritten"):
                prepare(manifest, existing)
            self.assertEqual(existing.read_bytes(), b"original capture")

    def test_exact_geometry_and_family_split_leaks_fail_before_output(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            p, uv, f, chart, nc = tetrahedron(True, True)
            data = struct.pack("<3i", len(p), f.size, nc) + p.astype("<f4").tobytes() + uv.astype("<f4").tobytes() + f.astype("<i4").tobytes() + chart.astype("<i4").tobytes()
            (root / "mesh.bin").write_bytes(data)
            entries = [dict(asset_id="bust", family_id="organic", capture="mesh.bin", split="train"),
                       dict(asset_id="alias", family_id="different", capture="mesh.bin", split="test")]
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps(entries))
            with self.assertRaisesRegex(ValueError, "Split leakage"):
                prepare(manifest, root / "out")
            self.assertFalse((root / "out").exists())
            entries[1]["split"] = "train"
            manifest.write_text(json.dumps(entries))
            report = prepare(manifest, root / "out")
            self.assertEqual(report["independent_models"], 1)
            self.assertEqual(report["split_groups"], 1)
            changed = p.copy(); changed[:, 0] *= 2
            other = struct.pack("<3i", len(p), f.size, nc) + changed.astype("<f4").tobytes() + data[12 + p.size * 4:]
            (root / "other.bin").write_bytes(other)
            entries[1].update(capture="other.bin", family_id="organic", split="test")
            manifest.write_text(json.dumps(entries))
            with self.assertRaisesRegex(ValueError, "Split leakage"):
                prepare(manifest, root / "other-out")


if __name__ == "__main__":
    unittest.main()
