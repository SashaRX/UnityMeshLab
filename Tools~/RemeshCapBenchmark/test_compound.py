import unittest

import numpy as np

from compound import generate, validate_boundaries, validate_projection
from topology import inspect


def open_tube(reverse=False):
    outer = np.array([[-2, 0, -2], [2, 0, -2], [2, 0, 2], [-2, 0, 2]], dtype='f4')
    inner = outer*.5
    points = np.concatenate((outer, outer+[0, 1, 0], inner, inner+[0, 1, 0])).astype('f4')
    indices = []
    for i in range(4):
        j = (i+1) % 4
        for a, b, c, d in ((j, i, i+4, j+4), (i+8, j+8, j+12, i+12), (i+4, i+12, j+12, j+4)):
            indices.extend(((a, b, c), (a, c, d)))
    indices = np.array(indices, dtype='u4')
    _, slots = np.unique(points, axis=0, return_inverse=True)
    loops = [slots[[3, 2, 1, 0]].tolist(), slots[[8, 9, 10, 11]].tolist()]
    if reverse:
        indices = indices[:, ::-1]
        loops = [loop[::-1] for loop in loops]
    return points, indices, loops


class CompoundTests(unittest.TestCase):
    def test_compound_tube_retains_hole_and_original_geometry(self):
        for reverse in (False, True):
            p, ix, loops = open_tube(reverse)
            positions, indices, report = generate(p, ix, loops, [0, 0], 0)
            self.assertTrue(report['capAccepted'])
            np.testing.assert_array_equal(p[ix], positions[indices[:len(ix)]])
            centers = positions[indices[len(ix):]].mean(axis=1)
            self.assertFalse(np.any((np.abs(centers[:, 0]) < 1) & (np.abs(centers[:, 2]) < 1)))
            self.assertEqual(0, report['geometry']['newCrossingOrOverlapPairs'])

    def test_incomplete_or_reversed_boundary_is_rejected(self):
        p, ix, loops = open_tube()
        _, slots = np.unique(p, axis=0, return_inverse=True)
        for invalid in (loops[:1], [loops[0][::-1], loops[1]]):
            with self.assertRaisesRegex(ValueError, 'every directed source boundary'):
                validate_boundaries(slots[ix], invalid)

    def test_self_crossed_projection_is_rejected(self):
        q = np.array([[0, 0], [2, 2], [0, 2], [2, 0]], dtype=float)
        with self.assertRaisesRegex(ValueError, 'crossed, overlapping'):
            validate_projection(q, [[0, 1, 2, 3]])

    def test_t_junction_and_overlapping_constraints_are_rejected(self):
        q = np.array([[0, 0], [4, 0], [4, 4], [0, 4], [2, 0], [3, 1], [1, 1]], dtype=float)
        with self.assertRaisesRegex(ValueError, 'nonadjacent touching'):
            validate_projection(q, [[0, 1, 2, 3], [4, 5, 6]])
        q = np.array([[0, 0], [4, 0], [2, 0], [0, 4]], dtype=float)
        with self.assertRaisesRegex(ValueError, 'overlapping'):
            validate_projection(q, [[0, 1, 2, 3]])

    def test_closed_bow_tie_vertex_fails_topology_despite_clean_edges(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1],
                      [-1, 0, 0], [0, -1, 0], [0, 0, -1]], dtype='f4')
        faces = np.array([[0, 2, 1], [0, 1, 3], [1, 2, 3], [2, 0, 3]], dtype='u4')
        other = np.array([0, 4, 5, 6], dtype='u4')[faces]
        result = inspect(p, np.concatenate((faces, other)))
        self.assertEqual(0, result['boundaryEdges'])
        self.assertEqual(0, result['nonManifoldEdges'])
        self.assertEqual(1, result['disconnectedVertexFans'])
        self.assertFalse(result['closedManifold'])
        self.assertEqual([[0, 0, 0]], result['disconnectedFanPositions'])

    def test_topology_welds_attribute_split_vertices(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1]], dtype='f4')
        faces = np.array([[0, 2, 1], [0, 1, 3], [1, 2, 3], [2, 0, 3]], dtype='u4')
        split = p[faces].reshape(-1, 3)
        self.assertTrue(inspect(split, np.arange(len(split)).reshape(-1, 3))['closedManifold'])


if __name__ == '__main__':
    unittest.main()
