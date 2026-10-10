import unittest

import numpy as np

from branch import reconnect
from analyze import scan
from topology import inspect


def touching_tetrahedra(opposite=False):
    points = np.array([[0, 0, 0], [2, 0, 0], [2, 1, 0], [2, 0, 1],
                       [0, 0, 2], [1, 0, 2], [0, 1, 2]], dtype='f4')
    if opposite:
        points = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [0, 0, 1],
                           [-1, 0, 0], [0, -1, 0], [0, 0, -1]], dtype='f4')
    faces = np.array([[0, 2, 1], [0, 1, 3], [1, 2, 3], [2, 0, 3]], dtype='u4')
    return points, np.concatenate((faces, np.array([0, 4, 5, 6], dtype='u4')[faces]))


class BranchTests(unittest.TestCase):
    def test_reconnects_without_source_changes_or_new_intersections(self):
        for reverse in (False, True):
            points, indices = touching_tetrahedra()
            if reverse:
                indices = indices[:, ::-1]
            original_points, original_indices = points.copy(), indices.copy()
            self.assertEqual(1, inspect(points, indices)['disconnectedVertexFans'])
            candidate, report = reconnect(points, indices, 1)
            self.assertTrue(report['changed'])
            self.assertTrue(report['topology']['closedManifold'])
            self.assertEqual(len(indices)+2, len(candidate))
            np.testing.assert_array_equal(original_points, points)
            np.testing.assert_array_equal(original_indices, indices)
            np.testing.assert_array_equal(indices[:1], candidate[:1])
            self.assertEqual({}, scan(points, candidate, 1)['summary']['pairCounts'])

    def test_budget_stops_before_successful_candidate(self):
        points, indices = touching_tetrahedra()
        candidate, report = reconnect(points, indices, 1, max_pairs=5)
        self.assertFalse(report['changed'])
        self.assertTrue(report['budgetExhausted'])
        self.assertEqual(5, len(report['attempts']))
        np.testing.assert_array_equal(indices, candidate)

    def test_refuses_when_connection_cannot_avoid_intersections(self):
        points, indices = touching_tetrahedra(opposite=True)
        candidate, report = reconnect(points, indices, 1)
        self.assertFalse(report['changed'])
        self.assertTrue(any('hits' in attempt for attempt in report['attempts']))
        np.testing.assert_array_equal(indices, candidate)

    def test_protected_source_fans_are_not_modified(self):
        points, indices = touching_tetrahedra()
        candidate, report = reconnect(points, indices, len(indices))
        self.assertFalse(report['changed'])
        self.assertIn('editable Cap face', report['attempts'][0]['refusal'])
        np.testing.assert_array_equal(indices, candidate)

    def test_closed_manifold_is_idempotent(self):
        points, indices = touching_tetrahedra()
        candidate, _ = reconnect(points, indices, 1)
        again, report = reconnect(points, candidate, 1)
        self.assertFalse(report['changed'])
        self.assertEqual([], report['attempts'])
        np.testing.assert_array_equal(candidate, again)

    def test_open_edges_and_attribute_splits_are_rejected(self):
        points, indices = touching_tetrahedra()
        candidate, report = reconnect(points, indices[:-1], 1)
        self.assertIn('closed oriented edges', report['refusal'])
        np.testing.assert_array_equal(indices[:-1], candidate)
        split = points[indices].reshape(-1, 3)
        with self.assertRaisesRegex(ValueError, 'geometric vertex slots'):
            reconnect(split, np.arange(len(split)).reshape(-1, 3), 1)


if __name__ == '__main__':
    unittest.main()
