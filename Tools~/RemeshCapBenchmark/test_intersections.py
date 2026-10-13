import itertools
import unittest
import numpy as np

from intersections import BoundsTree, exact_positions, triangle_intersection


class IntersectionTests(unittest.TestCase):
    def kind(self, a, b):
        p, _ = exact_positions(np.array(a+b, dtype='f4'))
        hit = triangle_intersection(tuple(p[:3]), tuple(p[3:]))
        return hit.kind if hit else None

    def test_crossing_is_symmetric_and_independent_of_winding(self):
        a = [(0,0,0),(2,0,0),(0,2,0)]
        b = [(0.5,0.5,-1),(0.5,0.5,1),(1.5,0.5,0)]
        for first in itertools.permutations(a):
            for second in itertools.permutations(b):
                self.assertEqual('crossing', self.kind(list(first), list(second)))
                self.assertEqual('crossing', self.kind(list(second), list(first)))

    def test_allowed_common_edge_and_vertex(self):
        a = [(0,0,0),(2,0,0),(0,2,0)]
        for b in [[(0,0,0),(2,0,0),(0,-1,0)], [(0,0,0),(2,0,0),(0,1,1)], [(0,0,0),(-1,0,0),(0,-1,1)]]:
            self.assertIsNone(self.kind(a,b))

    def test_shared_edge_does_not_hide_coplanar_overlap(self):
        a = [(0,0,0),(2,0,0),(0,2,0)]
        self.assertEqual('coplanar_overlap', self.kind(a,[(0,0,0),(2,0,0),(1,1,0)]))

    def test_common_vertex_does_not_hide_crossing(self):
        a = [(0,0,0),(2,0,0),(0,2,0)]
        b = [(0,0,0),(1,0.5,1),(1,0.5,-1)]
        self.assertEqual('crossing', self.kind(a,b))

    def test_coplanar_containment_and_opposed_duplicate(self):
        a = [(0,0,0),(2,0,0),(0,2,0)]
        self.assertEqual('coplanar_overlap', self.kind(a,[(0.1,0.1,0),(0.8,0.1,0),(0.1,0.8,0)]))
        self.assertEqual('coplanar_overlap', self.kind(a,list(reversed(a))))

    def test_nonadjacent_contacts_are_reported(self):
        a = [(0,0,0),(2,0,0),(0,2,0)]
        self.assertEqual('segment_contact', self.kind(a,[(0.5,0,0),(1.5,0,0),(1,-1,0)]))
        self.assertEqual('point_contact', self.kind(a,[(1,0,0),(2,-1,0),(0,-1,0)]))

    def test_tiny_separation_is_not_an_epsilon_overlap(self):
        for scale in (1, 0.001, 1e-10):
            a = np.array([(0,0,0),(2,0,0),(0,2,0)], dtype=float)*scale
            b = a.copy(); b[:,2]=scale*1e-12
            self.assertIsNone(self.kind(a.tolist(),b.tolist()))

    def test_large_translation_and_small_scale_preserve_crossing(self):
        a = np.array([(0,0,0),(2,0,0),(0,2,0)])
        b = np.array([(0.5,0.5,-1),(0.5,0.5,1),(1.5,0.5,0)])
        for scale, translation in ((1e-6,0),(1,100000),(1000,-50000)):
            self.assertEqual('crossing', self.kind((a*scale+translation).tolist(),(b*scale+translation).tolist()))

    def test_broad_phase_matches_brute_bounds(self):
        rng=np.random.default_rng(19)
        triangles=rng.normal(size=(80,3,3))
        tree=BoundsTree(triangles)
        for triangle in triangles:
            lo,hi=triangle.min(axis=0),triangle.max(axis=0)
            expected=np.flatnonzero(np.all(tree.minimum<=hi,axis=1)&np.all(tree.maximum>=lo,axis=1))
            self.assertEqual(set(expected),set(tree.query(lo,hi)))

    def test_degenerate_triangle_is_not_silently_accepted(self):
        with self.assertRaises(ValueError):
            self.kind([(0,0,0),(1,0,0),(2,0,0)],[(0,0,0),(1,0,0),(0,1,0)])


if __name__ == '__main__':
    unittest.main()
