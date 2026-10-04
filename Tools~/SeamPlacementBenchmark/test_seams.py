"""Small independent correctness checks; run with python -m unittest discover."""
import itertools
from pathlib import Path
import struct
import tempfile
import unittest

import numpy as np

from relocate import binary_cut, expansion, labeling_energy
from seams import align, exact_scores, read_capture, sample_distances, topology


class SeamChecks(unittest.TestCase):
    def test_area_bound_is_independent_of_face_count(self):
        from bounded_parts import partition
        tree = {4: {"left": 0, "right": 1}, 5: {"left": 2, "right": 3},
                6: {"left": 4, "right": 5}}
        areas = np.array([7., 1., 1., 1.])
        labels = partition(tree, areas, 0.5)
        self.assertNotEqual(labels[0], labels[1])
        self.assertEqual(labels[2], labels[3])
        for label in np.unique(labels):
            selected = labels == label
            self.assertTrue(areas[selected].sum() <= 5 or selected.sum() == 1)

    def test_triangle_aspect_is_invariant_under_uniform_scale(self):
        from shape_report import aspect
        triangles = np.array([[[0.,0], [1.,0], [0.5,np.sqrt(3)/2]], [[0.,0], [1.,0], [0.,0.01]]])
        areas = np.array([np.sqrt(3)/4, 0.005])
        self.assertAlmostEqual(aspect(triangles, areas)[0], 1)
        np.testing.assert_allclose(aspect(triangles, areas), aspect(triangles*17, areas*17**2))

    def test_region_fans_split_point_contacts_and_keep_shared_edges(self):
        from piecewise_optcuts import split_boundary_fans
        p = np.array([[0.,0,0], [1.,0,0], [0.,1,0], [-1.,0,0], [0.,-1,0]])
        faces = np.array([[0,1,2], [0,3,4]])
        v, f = split_boundary_fans(p, faces)
        self.assertEqual(len(v), 6)
        self.assertFalse(set(f[0]) & set(f[1]))
        np.testing.assert_array_equal(v[f], p[faces])
        faces = np.array([[0,1,2], [0,3,1]])
        v, f = split_boundary_fans(p, faces)
        self.assertEqual(len(v), 4)
        self.assertEqual(len(set(f[0]) & set(f[1])), 2)
        np.testing.assert_array_equal(v[f], p[faces])

    def tetrahedron(self):
        positions = np.array([[0., 0, 0], [1., 0, 0], [0., 1, 0], [0., 0, 1]])
        faces = np.array([[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]])
        return positions, np.zeros((4, 2)), faces, np.zeros(4, dtype=int), 1

    def test_duplicates_for_normals_are_not_uv_seams(self):
        p, uv, faces, charts, count = self.tetrahedron()
        reference = topology((p, uv, faces, charts, count))
        corners = faces.ravel()
        split = p[corners], uv[corners], np.arange(12).reshape(4, 3), charts[corners], count
        np.testing.assert_array_equal(align(split, reference), np.zeros(6, dtype=bool))

    def test_geometry_mismatch_is_rejected(self):
        original = self.tetrahedron()
        reference = topology(original)
        p, uv, faces, charts, count = original
        changed = p.copy()
        changed[1, 0] += 0.001
        with self.assertRaisesRegex(ValueError, "geometry/connectivity differ"):
            align((changed, uv, faces, charts, count), reference)

    def test_boundary_mesh_cannot_enter_closed_mesh_benchmark(self):
        p, uv, faces, charts, count = self.tetrahedron()
        with self.assertRaisesRegex(ValueError, "closed manifold"):
            topology((p, uv, faces[:-1], charts, count))

    def test_capture_truncation_and_trailing_bytes_are_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            file = Path(folder) / "bad.bin"
            for data in (b"short", struct.pack("<3i", 4, 12, 1),
                         struct.pack("<3i", 4, 12, 1) + bytes(4*24+12*4+1)):
                file.write_bytes(data)
                with self.assertRaises(ValueError):
                    read_capture(file)

    def test_empty_target_is_missing_not_false_near_match(self):
        positions = np.array([[0., 0, 0], [1., 0, 0]])
        distances, weights = sample_distances(positions, np.array([[0, 1]]), np.array([1.]),
                                              np.array([True]), np.array([False]))
        self.assertTrue(np.isinf(distances).all())
        self.assertAlmostEqual(weights.sum(), 1)

    def test_adjacent_fold_overlaps_are_not_skipped(self):
        import sys
        sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
        from analyze_uv_reference import overlaps
        triangles = np.array([[[0., 0], [1., 0], [0., 1]], [[0., 0], [1., 0], [0., 0.5]]])
        self.assertEqual(len(overlaps(triangles, np.zeros(2, dtype=int))), 1)
        triangles[1, 2, 1] = -0.5
        self.assertEqual(len(overlaps(triangles, np.zeros(2, dtype=int))), 0)

    def test_weighted_scores_do_not_reward_dense_triangulation(self):
        predicted = np.array([True, False, False])
        expected = np.array([True, True, True])
        result = exact_scores(predicted, expected, np.array([8.0, 1.0, 1.0]))
        self.assertAlmostEqual(result["recall"], 0.8)
        self.assertEqual(result["precision"], 1)

    def test_point_to_segments_handles_unequal_edge_lengths(self):
        # One long segment compared with its two collinear subdivisions: all
        # sampled distances must be zero although the midpoints differ.
        p = np.array([[0., 0, 0], [1., 0, 0], [0.2, 0, 0]])
        edges = np.array([[0, 1], [0, 2], [2, 1]])
        lengths = np.array([1., 0.2, 0.8])
        distances, weights = sample_distances(p, edges, lengths,
                                              np.array([True, False, False]),
                                              np.array([False, True, True]))
        np.testing.assert_allclose(distances, 0, atol=1e-15)
        self.assertAlmostEqual(weights.sum(), 1)

    def test_binary_flow_matches_exhaustive_solution(self):
        rng = np.random.default_rng(591)
        pairs = np.array([[0, 1], [1, 2], [2, 3], [3, 0], [0, 2]])
        for _ in range(25):
            costs = rng.random((4, 2))
            weights = rng.random(len(pairs))
            cut = binary_cut(costs[:, 0], costs[:, 1], pairs, weights).astype(int)
            actual = labeling_energy(cut, costs, pairs, weights)
            optimal = min(labeling_energy(np.array(v), costs, pairs, weights)
                          for v in itertools.product(range(2), repeat=4))
            self.assertAlmostEqual(actual, optimal, places=6)

    def test_alpha_move_never_increases_original_energy(self):
        pairs = np.array([[0, 1], [1, 2], [2, 3], [3, 0], [0, 2]])
        labels = np.array([0, 1, 2, 0])
        unary = np.array([[0.1, 2, 1], [0.2, 0.8, 1], [1, 0.1, 0.5], [1, 0.2, 0.6]])
        weights = np.array([0.2, 0.8, 0.1, 0.3, 0.4])
        moved, energies = expansion(labels, unary, pairs, weights)
        self.assertTrue(all(a > b for a, b in zip(energies, energies[1:])))
        self.assertAlmostEqual(energies[-1], labeling_energy(moved, unary, pairs, weights))


if __name__ == "__main__":
    unittest.main()
