from itertools import permutations
import json
import unittest

import numpy as np

from boundaries import extract
from incremental import Limits, Refused, attempt, audit_new_pairs, continuous_arc
from incremental_probe import compact_box, reference_patch, patch_support, reference_attempt
from planes import Options, analyze
from topology import inspect


class IncrementalTests(unittest.TestCase):
    def test_two_adjacent_faces_reanalyze_to_one_plane_in_both_orders(self):
        for order in permutations([1, 3]):
            p, ix = compact_box(order)
            old_p, old_ix = p.copy(), ix.copy()
            self.assertEqual('piecewise_plane_hypothesis', analyze(p, ix)['loops'][0]['status'])
            p, ix, first = reference_attempt(p, ix, order[0])
            self.assertTrue(first['accepted'], first.get('refusal'))
            self.assertEqual(6, first['boundaryEdgesBefore'])
            self.assertEqual(4, first['boundaryEdgesAfter'])
            self.assertEqual(['whole_plane_hypothesis'], [loop['status'] for loop in first['remainingPlanes']['loops']])
            p, ix, second = reference_attempt(p, ix, order[1])
            self.assertTrue(second['accepted'], second.get('refusal'))
            self.assertEqual(0, second['boundaryEdgesAfter'])
            self.assertTrue(inspect(p, ix)['closedManifold'])
            np.testing.assert_array_equal(old_p, p[:len(old_p)])
            np.testing.assert_array_equal(old_ix, ix[:len(old_ix)])

    def test_three_faces_reconstruct_reference_corner_in_all_six_orders(self):
        closed_geometry = []
        for order in permutations([1, 3, 5]):
            p, ix = compact_box(order)
            self.assertEqual(7, len(p))  # Missing corner is not hidden in input.
            self.assertEqual('ambiguous_piecewise_planes', analyze(p, ix)['loops'][0]['status'])
            for step, face in enumerate(order):
                p, ix, report = reference_attempt(p, ix, face)
                self.assertTrue(report['accepted'], report.get('refusal'))
                statuses = [loop['status'] for loop in report['remainingPlanes']['loops']]
                self.assertEqual([['piecewise_plane_hypothesis'], ['whole_plane_hypothesis'], []][step], statuses)
                self.assertEqual(int(step == 0), report['addedVertices'])
                self.assertFalse(report['solidCertified'])
                self.assertFalse(report['intendedShapeCertified'])
                json.loads(json.dumps(report, allow_nan=False))
            self.assertTrue(inspect(p, ix)['closedManifold'])
            closed_geometry.append(sorted(tuple(sorted(tuple(p[v]) for v in t)) for t in ix))
        self.assertTrue(all(g == closed_geometry[0] for g in closed_geometry))

    def test_arc_rejects_discontinuity_duplicate_order_and_stale_snapshot(self):
        p, ix = compact_box([1])
        boundary = extract(p, ix)
        h = [edge['halfedge'] for edge in boundary['loops'][0]['halfedges']]
        for invalid in ([h[0], h[2]], h[::-1], [h[0], h[0]], [h[0], -1]):
            with self.assertRaises(ValueError):
                continuous_arc(boundary, boundary['sourceHash'], invalid)
        with self.assertRaises(ValueError):
            continuous_arc(boundary, 'stale', h)
        self.assertEqual(len(h), len(continuous_arc(boundary, boundary['sourceHash'], h[2:] + h[:2])))

    def test_arc_cannot_jump_between_cycles_or_through_singular_fans(self):
        p, ix = compact_box([0, 1])
        boundary = extract(p, ix)
        with self.assertRaises(ValueError):
            continuous_arc(boundary, boundary['sourceHash'], [loop['halfedges'][0]['halfedge'] for loop in boundary['loops']])
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [-1, 0, 0], [0, -1, 0]], dtype='f4')
        ix = np.array([[0, 1, 2], [0, 3, 4]], dtype='u4')
        boundary = extract(p, ix)
        with self.assertRaisesRegex(ValueError, 'junction'):
            continuous_arc(boundary, boundary['sourceHash'], [h['halfedge'] for h in boundary['loops'][0]['halfedges']])

    def test_new_cap_crossing_or_coplanar_overlap_with_source_rolls_back(self):
        for blocker, expected in (([[0, -.5, .8], [0, .5, .8], [0, 0, 1.2]], 'crossing'),
                                  ([[0, -.5, 1], [.5, .5, 1], [-.5, .5, 1]], 'coplanar_overlap')):
            p, ix = compact_box([1])
            ix = np.concatenate((ix, [[len(p), len(p) + 1, len(p) + 2]])).astype('u4')
            p = np.concatenate((p, np.asarray(blocker, dtype='f4')))
            saved_p, saved_ix = p.copy(), ix.copy()
            result_p, result_ix, report = reference_attempt(p, ix, 1)
            self.assertFalse(report['accepted'])
            self.assertEqual('new_intersections_or_contacts', report['refusal'])
            self.assertIn(expected, [hit['kind'] for hit in report['geometry']['hits']])
            np.testing.assert_array_equal(saved_p, result_p)
            np.testing.assert_array_equal(saved_ix, result_ix)
            np.testing.assert_array_equal(saved_p, p)
            np.testing.assert_array_equal(saved_ix, ix)

    def test_exact_audit_rejects_new_new_overlap_and_nonadjacent_contact(self):
        p = np.array([[0, 0, 0], [2, 0, 0], [0, 2, 0], [1, 1, 0], [3, 1, 0], [1, 3, 0]], dtype='f4')
        audit = audit_new_pairs(p, np.array([[0, 1, 2], [3, 4, 5]]), 0, Limits(), None)
        self.assertFalse(audit['geometryAccepted'])
        self.assertEqual('new-new', audit['hits'][0]['role'])
        self.assertEqual('point_contact', audit['hits'][0]['kind'])
        p[3] = [.5, .5, 0]
        audit = audit_new_pairs(p, np.array([[0, 1, 2], [3, 4, 5]]), 0, Limits(), None)
        self.assertEqual('coplanar_overlap', audit['hits'][0]['kind'])
        # A shared edge is allowed only if the intersection stays on that edge.
        p = np.array([[0, 0, 0], [2, 0, 0], [0, 2, 0], [1, 1, 0]], dtype='f4')
        audit = audit_new_pairs(p, np.array([[0, 1, 2], [1, 0, 3]]), 0, Limits(), None)
        self.assertEqual('coplanar_overlap', audit['hits'][0]['kind'])

    def test_sequential_steps_preserve_closure_after_rigid_transform_and_scale(self):
        angle = .41
        rotation = np.array([[np.cos(angle), 0, np.sin(angle)], [0, 1, 0],
                             [-np.sin(angle), 0, np.cos(angle)]])
        for scale in (.125, 8):
            base, ix = compact_box([1, 3, 5])
            p = (base.astype('d') @ rotation.T * scale + [3, -2, 1]).astype('f4')
            saved_p, saved_ix = p.copy(), ix.copy()
            for face in [1, 3, 5]:
                extra, caps, candidate = reference_patch(base, face)
                _, support = patch_support(base, ix, candidate, caps)
                moved_extra = (extra.astype('d') @ rotation.T * scale + [3, -2, 1]).astype('f4')
                p, ix, report = attempt(p, ix, extract(p, ix)['sourceHash'], support, moved_extra, caps)
                self.assertTrue(report['accepted'], report.get('refusal'))
                base = candidate
            self.assertTrue(inspect(p, ix)['closedManifold'])
            np.testing.assert_array_equal(saved_p, p[:len(saved_p)])
            np.testing.assert_array_equal(saved_ix, ix[:len(saved_ix)])

    def test_unselected_opening_cannot_be_closed_by_the_same_attempt(self):
        p, ix = compact_box([0, 1])
        extra, caps, candidate = reference_patch(p, 1)
        boundary, support = patch_support(p, ix, candidate, caps)
        _, other, _ = reference_patch(p, 0)
        _, _, report = attempt(p, ix, boundary['sourceHash'], support, extra, np.concatenate((caps, other)))
        self.assertFalse(report['accepted'])
        # Two opposite faces cannot belong to the selected support plane either.
        self.assertIn(report['refusal'], ('patch_not_on_support_plane', 'selected_arc_coverage_or_unselected_boundary_changed'))
        # Same-plane openings exercise coverage rather than the plane guard.
        p, ix = compact_box([1])
        _, caps, _ = reference_patch(p, 1)
        count = len(p)
        two_p = np.concatenate((p, p + np.array([4, 0, 0], dtype='f4')))
        two_ix = np.concatenate((ix, ix + count))
        boundary, support = patch_support(two_p, two_ix, two_p, caps)
        _, _, report = attempt(two_p, two_ix, boundary['sourceHash'], support,
                               np.empty((0, 3), dtype='f4'), np.concatenate((caps, caps + count)))
        self.assertEqual('selected_arc_coverage_or_unselected_boundary_changed', report['refusal'])

    def test_detached_added_faces_are_refused_even_when_the_patch_plane_matches(self):
        p, ix = compact_box([1])
        _, caps, _ = reference_patch(p, 1)
        boundary, support = patch_support(p, ix, p, caps)
        extra = np.array([[4, 0, 1], [5, 0, 1], [4, 1, 1]], dtype='f4')
        caps = np.concatenate((caps, [[len(p), len(p) + 1, len(p) + 2]])).astype('u4')
        result_p, result_ix, report = attempt(p, ix, boundary['sourceHash'], support, extra, caps)
        self.assertEqual('detached_added_faces', report['refusal'])
        np.testing.assert_array_equal(p, result_p)
        np.testing.assert_array_equal(ix, result_ix)

    def test_previous_accepted_caps_are_protected_by_current_snapshot(self):
        p, ix = compact_box([1, 3])
        old_hash = extract(p, ix)['sourceHash']
        p, ix, report = reference_attempt(p, ix, 1)
        self.assertTrue(report['accepted'])
        extra, caps, candidate = reference_patch(p, 3)
        _, support = patch_support(p, ix, candidate, caps)
        result_p, result_ix, report = attempt(p, ix, old_hash, support, extra, caps)
        self.assertFalse(report['accepted'])
        self.assertIn('Stale', report['refusal'])
        np.testing.assert_array_equal(p, result_p)
        np.testing.assert_array_equal(ix, result_ix)

    def test_wrong_winding_and_nonplanar_patch_do_not_commit(self):
        p, ix = compact_box([1, 3, 5])
        extra, caps, candidate = reference_patch(p, 1)
        boundary, support = patch_support(p, ix, candidate, caps)
        for added, faces in ((extra, caps[:, ::-1]), (extra + np.array([0, 0, .1], dtype='f4'), caps)):
            result_p, result_ix, report = attempt(p, ix, boundary['sourceHash'], support, added, faces)
            self.assertFalse(report['accepted'])
            np.testing.assert_array_equal(p, result_p)
            np.testing.assert_array_equal(ix, result_ix)

    def test_work_refusal_and_reanalysis_refusal_rollback(self):
        p, ix = compact_box([1, 3])
        for kwargs in ({'limits': Limits(max_faces=1)}, {'limits': Limits(max_pair_tests=1)},
                       {'cancel': lambda: True}, {'plane_options': Options(max_loop_edges=3)}):
            result_p, result_ix, report = reference_attempt(p, ix, 1, **kwargs)
            self.assertFalse(report['accepted'])
            np.testing.assert_array_equal(p, result_p)
            np.testing.assert_array_equal(ix, result_ix)
        with self.assertRaises(Refused):
            audit_new_pairs(p, ix, 0, Limits(), lambda: True)


if __name__ == '__main__':
    unittest.main()
