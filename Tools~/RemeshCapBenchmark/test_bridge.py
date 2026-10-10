import json
import unittest

import numpy as np

from analyze import scan
from boundaries import extract
from bridge import Options, generate, audit_annulus, selected_rims, Work
from bridge_probe import signature, torus_gap
from bridge_generation_probe import subdivide_boundary, blocked_gap
from incremental import Limits
from incremental_probe import compact_box


def run(p, ix, **kwargs):
    boundary = extract(p, ix)
    return generate(p, ix, boundary['sourceHash'], [loop['id'] for loop in boundary['loops']], **kwargs)


class BridgeTests(unittest.TestCase):
    def test_generated_torus_bridge_preserves_borders_and_handle_without_reference_faces(self):
        p, ix, *_ = torus_gap()
        saved_p, saved_ix = p.copy(), ix.copy()
        points, faces, report = run(p, ix)
        self.assertTrue(report['accepted'], report.get('refusal'))
        self.assertTrue(report['searchComplete'])
        self.assertEqual(0, report['addedVertices'])
        self.assertEqual(16, report['addedFaces'])
        self.assertGreater(report['auditedAlternativeCount'], 1)
        self.assertEqual(1, signature(points, faces)['components'][0]['genus'])
        self.assertTrue(scan(points, faces, len(ix))['summary']['capGeometryAccepted'])
        np.testing.assert_array_equal(saved_p, points)
        np.testing.assert_array_equal(saved_ix, faces[:len(ix)])
        np.testing.assert_array_equal(saved_p, p)
        np.testing.assert_array_equal(saved_ix, ix)
        json.loads(json.dumps(report, allow_nan=False))
        self.assertFalse(report['intendedShapeCertified'])
        self.assertFalse(report['solidCertified'])

    def test_unequal_rims_are_zipped_without_resampling_or_moving_source_edges(self):
        p, ix, *_ = torus_gap(8, 6)
        p, ix = subdivide_boundary(p, ix)
        b = extract(p, ix)
        self.assertEqual([6, 7], sorted(len(loop['halfedges']) for loop in b['loops']))
        q, faces, report = run(p, ix)
        self.assertTrue(report['accepted'], report.get('refusal'))
        self.assertEqual(13, report['addedFaces'])
        np.testing.assert_array_equal(p, q)
        np.testing.assert_array_equal(ix, faces[:len(ix)])
        self.assertEqual([], extract(q, faces)['loops'])
        self.assertTrue(scan(q, faces, len(ix))['summary']['capGeometryAccepted'])

    def test_same_input_and_swapped_selection_are_deterministic(self):
        p, ix, *_ = torus_gap(8, 6)
        b = extract(p, ix)
        ids = [loop['id'] for loop in b['loops']]
        first = generate(p, ix, b['sourceHash'], ids)
        second = generate(p, ix, b['sourceHash'], ids[::-1])
        np.testing.assert_array_equal(first[1], second[1])
        self.assertEqual(first[2], second[2])

    def test_winding_order_transform_and_scale_keep_valid_bridge(self):
        p, ix, *_ = torus_gap(8, 6)
        for scale, faces in ((.125, ix[:, ::-1]), (8, ix[::-1]), (1, np.roll(ix, 1, axis=1))):
            q = (p[:, [1, 2, 0]] * scale + [3, -2, 1]).astype('f4')
            points, candidate, report = run(q, faces)
            self.assertTrue(report['accepted'], report.get('refusal'))
            self.assertEqual(1, signature(points, candidate)['components'][0]['genus'])

    def test_unselected_opening_is_preserved_while_two_rims_close(self):
        p, ix, *_ = torus_gap(8, 6)
        extra = np.array([[8, 0, 0], [9, 0, 0], [8, 1, 0]], dtype='f4')
        faces = np.concatenate((ix, [[len(p), len(p) + 1, len(p) + 2]])).astype('u4')
        p = np.concatenate((p, extra))
        b = extract(p, faces)
        ids = [loop['id'] for loop in b['loops'] if len(loop['halfedges']) == 6]
        q, candidate, report = generate(p, faces, b['sourceHash'], ids)
        self.assertTrue(report['accepted'], report.get('refusal'))
        self.assertEqual(1, len(extract(q, candidate)['loops']))
        self.assertEqual(3, len(report['selection']['unselectedHalfedges']))
        self.assertEqual(['whole_plane_hypothesis'], [loop['status'] for loop in report['candidates'][report['selectedCandidate']]['remainingPlanes']['loops']])

    def test_bridge_can_join_two_components_with_explicit_intent(self):
        p, source, *_ = torus_gap(8, 6)
        # Remove the opposite strip too: two open source components, four rims.
        remove = set(range((4 - 1) * 12, 4 * 12))
        ix = np.asarray([face for i, face in enumerate(source) if i not in remove], dtype='u4')
        b = extract(p, ix)
        ids = [loop['id'] for loop in b['loops']
               if {h['sourceVertices'][0] // 6 for h in loop['halfedges']} <= {0, 1}]
        self.assertEqual(2, signature(p, ix)['componentCount'])
        q, candidate, report = generate(p, ix, b['sourceHash'], ids)
        self.assertTrue(report['accepted'], report.get('refusal'))
        result = signature(q, candidate)
        self.assertEqual(1, result['componentCount'])
        self.assertEqual(2, result['boundaryLoops'])
        self.assertEqual(0, result['components'][0]['genus'])

    def test_occupied_gap_rejects_all_generated_bridges_without_changing_source(self):
        p, ix = blocked_gap()
        b = extract(p, ix)
        ids = [loop['id'] for loop in b['loops'] if len(loop['halfedges']) == 6]
        q, candidate, report = generate(p, ix, b['sourceHash'], ids)
        self.assertFalse(report['accepted'])
        self.assertTrue(report['searchComplete'])
        self.assertTrue(any(row.get('refusal') == 'new_intersections_or_contacts' for row in report['candidates']))
        np.testing.assert_array_equal(p, q)
        np.testing.assert_array_equal(ix, candidate)

    def test_attribute_split_indices_do_not_break_geometric_correspondence(self):
        p, ix, *_ = torus_gap(8, 6)
        p = p[ix].reshape(-1, 3)
        ix = np.arange(ix.size, dtype='u4').reshape(-1, 3)
        q, faces, report = run(p, ix)
        self.assertTrue(report['accepted'], report.get('refusal'))
        self.assertEqual(1, signature(q, faces)['components'][0]['genus'])
        np.testing.assert_array_equal(p, q)
        np.testing.assert_array_equal(ix, faces[:len(ix)])

    def test_wrong_coverage_and_winding_in_supplied_annulus_are_refused(self):
        from planes import Options as PlaneOptions
        p, ix, reference, *_ = torus_gap(8, 6)
        boundary = extract(p, ix)
        selection, loops = selected_rims(boundary, boundary['sourceHash'], [l['id'] for l in boundary['loops']])
        for caps in (reference[len(ix):, ::-1], reference[len(ix):-1]):
            report = audit_annulus(p, ix, caps, boundary, selection, loops, Limits(), Work(Options(), None), PlaneOptions())
            self.assertFalse(report['accepted'])

    def test_bridge_on_box_rims_is_refused_by_topology_or_occupied_geometry_gates(self):
        p, ix = compact_box([0, 1])
        q, candidate, report = run(p, ix)
        self.assertFalse(report['accepted'])
        self.assertTrue(report['searchComplete'])
        self.assertGreater(len(report['candidates']), 0)
        np.testing.assert_array_equal(p, q)
        np.testing.assert_array_equal(ix, candidate)

    def test_budget_refusals_never_publish_a_partial_search_winner(self):
        p, ix, *_ = torus_gap(8, 6)
        for options in (Options(max_phases=1), Options(max_loop_edges=3), Options(max_dp_states=1),
                        Options(max_candidates=1), Options(max_pair_tests=1)):
            q, faces, report = run(p, ix, options=options)
            self.assertFalse(report['accepted'])
            self.assertFalse(report['searchComplete'])
            np.testing.assert_array_equal(p, q)
            np.testing.assert_array_equal(ix, faces)
        self.assertFalse(run(p, ix, limits=Limits(max_faces=len(ix)))[2]['accepted'])

    def test_cancellation_is_checked_inside_dynamic_program_and_audits(self):
        p, ix, *_ = torus_gap(8, 6)
        calls = 0
        def cancel():
            nonlocal calls
            calls += 1
            return calls >= 200
        _, faces, report = run(p, ix, cancel=cancel)
        self.assertFalse(report['accepted'])
        self.assertEqual('cancelled', report['refusal'])
        np.testing.assert_array_equal(ix, faces)

    def test_unknown_stale_or_nonpaired_selection_requires_explicit_valid_intent(self):
        p, ix, *_ = torus_gap(8, 6)
        b = extract(p, ix)
        ids = [loop['id'] for loop in b['loops']]
        for chosen, source_hash in ((ids[:1], b['sourceHash']), (ids + ids[:1], b['sourceHash']),
                                    (ids, 'stale'), ([ids[0], ids[0]], b['sourceHash'])):
            with self.assertRaises(ValueError):
                generate(p, ix, source_hash, chosen)
        for options in (Options(paths_per_phase=9), Options(min_area_ratio=float('nan')), Options(max_phases=0)):
            with self.assertRaises(ValueError):
                run(p, ix, options=options)


if __name__ == '__main__':
    unittest.main()
