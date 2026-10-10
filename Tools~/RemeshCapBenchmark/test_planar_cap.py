import unittest

import numpy as np

from boundaries import extract
from incremental import Limits
from incremental_probe import compact_box
from planar_cap import generate
from planes import Options, fit_plane
from topology import inspect


class PlanarCapTests(unittest.TestCase):
    def candidate(self, missing, **kwargs):
        p, ix = compact_box(missing)
        b = extract(p, ix)
        return p, ix, generate(p, ix, b['sourceHash'], b['loops'][0]['id'],
                               closure_intent='disk', **kwargs)

    def test_each_single_box_face_is_closed_with_source_prefix_preserved(self):
        for face in range(6):
            with self.subTest(face=face):
                p, ix, (q, result, report) = self.candidate([face])
                self.assertTrue(report['accepted'], report.get('refusal'))
                self.assertEqual(2, report['addedFaces'])
                self.assertEqual(0, report['addedVertices'])
                self.assertTrue(inspect(q, result)['closedManifold'])
                np.testing.assert_array_equal(p, q)
                np.testing.assert_array_equal(ix, result[:len(ix)])
                self.assertFalse(report['solidCertified'])
                self.assertFalse(report['intendedShapeCertified'])

    def test_compound_nonplanar_box_opening_is_not_flattened(self):
        p, ix, (q, result, report) = self.candidate([1, 3])
        self.assertFalse(report['accepted'])
        self.assertEqual('selected_loop_not_planar', report['refusal'])
        self.assertIs(p, q)
        self.assertIs(ix, result)

    def test_concave_rim_uses_constraints_instead_of_a_convex_fan(self):
        ring = np.array([[0, 0], [2, 0], [2, 1], [1, 1], [1, 2], [0, 2]], dtype='f4')
        p = np.concatenate((np.column_stack((ring, np.zeros(6, 'f4'))),
                            np.column_stack((ring, np.ones(6, 'f4')))))
        faces = [[6, 7, 8], [6, 8, 9], [6, 9, 11], [9, 10, 11]]
        for a in range(6):
            b = (a + 1) % 6
            faces.extend([[a, b, b + 6], [a, b + 6, a + 6]])
        ix = np.array(faces, 'u4'); boundary = extract(p, ix)
        q, result, report = generate(p, ix, boundary['sourceHash'], boundary['loops'][0]['id'], closure_intent='disk')
        self.assertTrue(report['accepted'], report.get('refusal'))
        self.assertEqual(4, report['addedFaces'])
        self.assertTrue(inspect(q, result)['closedManifold'])
        np.testing.assert_array_equal(ix, result[:len(ix)])

    def test_added_cap_cannot_cross_an_existing_surface(self):
        p, ix = compact_box([1]); boundary = extract(p, ix)
        ids = [edge['sourceVertices'][0] for edge in boundary['loops'][0]['halfedges']]
        plane = fit_plane(p[ids], closed=True)
        o, n, u, v = (np.array(plane[k]) for k in ('origin', 'normal', 'basisU', 'basisV'))
        obstacle = np.array([o + .1*u + .1*v + n, o - .1*u + .1*v - n, o + .1*u - .1*v - n], p.dtype)
        ix = np.concatenate((ix, np.array([[len(p), len(p)+1, len(p)+2]], ix.dtype)))
        p = np.concatenate((p, obstacle)); boundary = extract(p, ix)
        selected = next(loop for loop in boundary['loops'] if len(loop['halfedges']) == 4)
        q, result, report = generate(p, ix, boundary['sourceHash'], selected['id'], closure_intent='disk')
        self.assertFalse(report['accepted'])
        self.assertEqual('new_intersections_or_contacts', report['refusal'])
        self.assertIs(p, q); self.assertIs(ix, result)

    def test_generation_is_deterministic(self):
        p, ix = compact_box([1]); boundary = extract(p, ix)
        args = (p, ix, boundary['sourceHash'], boundary['loops'][0]['id'])
        a = generate(*args, closure_intent='disk')
        b = generate(*args, closure_intent='disk')
        np.testing.assert_array_equal(a[0], b[0]); np.testing.assert_array_equal(a[1], b[1])
        self.assertEqual(a[2], b[2])

    def test_selected_cap_preserves_another_opening(self):
        p, ix, (q, result, report) = self.candidate([0, 1])
        self.assertTrue(report['accepted'], report.get('refusal'))
        self.assertEqual(4, report['boundaryEdgesAfter'])
        self.assertEqual(1, len(extract(q, result)['loops']))
        np.testing.assert_array_equal(ix, result[:len(ix)])

    def test_stale_or_unselected_loop_is_refused(self):
        p, ix = compact_box([1]); b = extract(p, ix)
        for hash_value, loop_id in [('stale', b['loops'][0]['id']), (b['sourceHash'], 'other')]:
            q, result, report = generate(p, ix, hash_value, loop_id, closure_intent='disk')
            self.assertFalse(report['accepted'])
            self.assertIs(p, q); self.assertIs(ix, result)

    def test_operation_intent_must_not_be_inferred_from_planarity(self):
        p, ix = compact_box([1]); b = extract(p, ix)
        for intent in ('bridge', 'auto', None):
            with self.assertRaises(ValueError):
                generate(p, ix, b['sourceHash'], b['loops'][0]['id'], closure_intent=intent)

    def test_cancellation_and_budgets_preserve_source(self):
        for args, expected in [({'cancel': lambda: True}, 'cancelled'),
                               ({'options': Options(max_loop_edges=3)}, 'loop_edge_budget_exceeded'),
                               ({'limits': Limits(max_faces=10)}, 'mesh_budget_exceeded')]:
            p, ix, (q, result, report) = self.candidate([1], **args)
            self.assertFalse(report['accepted'])
            self.assertEqual(expected, report['refusal'])
            self.assertIs(p, q); self.assertIs(ix, result)


if __name__ == '__main__':
    unittest.main()
