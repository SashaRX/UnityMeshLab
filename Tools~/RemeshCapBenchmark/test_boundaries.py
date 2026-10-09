import copy
import unittest

import numpy as np

from boundaries import extract, select_domains, validate_coverage
from box_structure_probe import POINTS, QUADS, triangulate
from topology import inspect


def source_box(missing):
    return POINTS.copy(), triangulate([q for i, q in enumerate(QUADS) if i not in missing])


def raw_edges(report):
    return {tuple(h['sourceVertices']) for loop in report['loops'] for h in loop['halfedges']}


class BoundaryTests(unittest.TestCase):
    def test_box_openings_are_extracted_without_supplied_cycles(self):
        for missing, sizes in (([], []), ([1], [4]), ([1, 3], [6]),
                               ([0, 1], [4, 4]), ([1, 3, 5], [6])):
            p, ix = source_box(missing)
            report = extract(p, ix)
            self.assertTrue(report['extractionAccepted'])
            self.assertEqual(sizes, sorted(len(loop['halfedges']) for loop in report['loops']))
            self.assertEqual(inspect(p, ix)['boundaryEdges'], report['boundaryEdges'])
            self.assertEqual([], report['singularVertices'])
            for loop in report['loops']:
                edges = loop['halfedges']
                for h, following in zip(edges, edges[1:] + edges[:1]):
                    self.assertEqual(h['successor'], following['halfedge'])
                    self.assertEqual(h['slots'][1], following['slots'][0])
                    self.assertEqual(h['sourceFans'][1], following['sourceFans'][0])

    def test_interior_edge_rotation_follows_the_source_face_fan(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0]], dtype='f4')
        ix = np.array([[0, 1, 2], [0, 2, 3]], dtype='u4')
        report = extract(p, ix)
        self.assertEqual([0, 1, 4, 5], [h['halfedge'] for h in report['loops'][0]['halfedges']])
        self.assertEqual({(0, 1), (1, 2), (2, 3), (3, 0)}, raw_edges(report))

    def test_touching_fans_do_not_cross_pair_at_the_shared_position(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0],
                      [-1, 0, 0], [0, -1, 0]], dtype='f4')
        ix = np.array([[0, 1, 2], [0, 3, 4]], dtype='u4')
        report = extract(p, ix)
        self.assertTrue(report['extractionAccepted'])
        self.assertEqual(2, len(report['loops']))
        self.assertEqual(1, len(report['singularVertices']))
        self.assertTrue(all(loop['requiresJunctionResolution'] for loop in report['loops']))
        successors = {h['halfedge']: h['successor'] for loop in report['loops'] for h in loop['halfedges']}
        self.assertEqual(0, successors[2])
        self.assertEqual(3, successors[5])

    def test_attribute_seams_are_virtually_welded_without_changing_source_arrays(self):
        p, ix = source_box([1])
        split_points = p[ix].reshape(-1, 3).copy()
        split_ix = np.arange(ix.size, dtype='u4').reshape(-1, 3)
        saved_p, saved_ix = split_points.copy(), split_ix.copy()
        report = extract(split_points, split_ix)
        self.assertEqual(4, report['boundaryEdges'])
        self.assertEqual(1, len(report['loops']))
        self.assertEqual([], report['singularVertices'])
        for loop in report['loops']:
            for h in loop['halfedges']:
                self.assertEqual([int(split_ix[h['face'], h['corner']]),
                                  int(split_ix[h['face'], (h['corner'] + 1) % 3])], h['sourceVertices'])
        np.testing.assert_array_equal(saved_p, split_points)
        np.testing.assert_array_equal(saved_ix, split_ix)

    def test_connected_pinched_rim_keeps_distinct_occurrences_of_one_position(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0],
                      [-1, 0, 1], [0, -1, 1]], dtype='f4')
        ix = np.array([[0, 1, 2], [0, 3, 4], [2, 1, 3], [3, 1, 4]], dtype='u4')
        report = extract(p, ix)
        self.assertTrue(report['extractionAccepted'])
        self.assertEqual(1, len(report['loops']))
        loop = report['loops'][0]
        self.assertEqual(6, len(loop['halfedges']))
        self.assertEqual(2, sum(h['sourceVertices'][0] == 0 for h in loop['halfedges']))
        self.assertTrue(loop['requiresJunctionResolution'])
        at_junction = [h['sourceFans'][0] for h in loop['halfedges'] if h['sourceVertices'][0] == 0]
        self.assertEqual(2, len(set(at_junction)))

    def test_closed_singular_source_is_diagnosed_without_inventing_boundaries(self):
        from test_branch import touching_tetrahedra

        report = extract(*touching_tetrahedra())
        self.assertTrue(report['extractionAccepted'])
        self.assertEqual([], report['loops'])
        self.assertEqual(0, report['boundaryEdges'])
        self.assertEqual(1, len(report['singularVertices']))

    def test_tiny_gap_between_sheets_is_not_welded(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0],
                      [0, 0, 1e-8], [1, 0, 1e-8], [0, 1, 1e-8]], dtype='f4')
        report = extract(p, np.array([[0, 1, 2], [3, 4, 5]], dtype='u4'))
        self.assertEqual(6, report['geometricVertices'])
        self.assertEqual(2, len(report['loops']))
        self.assertEqual([], report['singularVertices'])

    def test_reversed_winding_and_rotated_face_corners_preserve_boundary_geometry(self):
        p, ix = source_box([1, 3])
        edges = raw_edges(extract(p, ix))
        self.assertEqual({(b, a) for a, b in edges}, raw_edges(extract(p, ix[:, ::-1])))
        self.assertEqual(edges, raw_edges(extract(p, np.roll(ix, 1, axis=1))))
        self.assertEqual(edges, raw_edges(extract(p, ix[::-1])))

    def test_snapshot_identity_and_cycle_start_are_deterministic(self):
        p, ix = source_box([0, 1])
        report = extract(p, ix)
        self.assertEqual(report, extract(np.asfortranarray(p), np.asfortranarray(ix)))
        for loop in report['loops']:
            ids = [h['halfedge'] for h in loop['halfedges']]
            self.assertEqual(min(ids), ids[0])
        changed = p.copy()
        changed[0, 0] += .125
        self.assertNotEqual(report['sourceHash'], extract(changed, ix)['sourceHash'])

    def test_rigid_transform_and_scale_do_not_change_source_boundary_occurrences(self):
        p, ix = source_box([0, 1])
        expected = raw_edges(extract(p, ix))
        for scale in (.001, 1024):
            transformed = (p[:, [2, 0, 1]].astype('d') * scale + [3, -2, 1]).astype('f4')
            report = extract(transformed, ix)
            self.assertTrue(report['extractionAccepted'])
            self.assertEqual(expected, raw_edges(report))
            self.assertEqual([], report['singularVertices'])

    def test_selected_opening_coverage_preserves_the_unselected_boundary(self):
        report = extract(*source_box([0, 1]))
        selection = select_domains(report, [report['loops'][0]['id']], report['sourceHash'])
        self.assertEqual(4, len(selection['selectedHalfedges']))
        self.assertEqual(4, len(selection['unselectedHalfedges']))
        self.assertEqual(selection, validate_coverage(report, selection, selection['selectedHalfedges'][::-1]))
        for supplied in (selection['selectedHalfedges'][:-1],
                         selection['selectedHalfedges'] + selection['selectedHalfedges'][:1],
                         selection['selectedHalfedges'] + selection['unselectedHalfedges']):
            with self.assertRaises(ValueError):
                validate_coverage(report, selection, supplied)
        with self.assertRaises(ValueError):
            validate_coverage(report, selection, map(float, selection['selectedHalfedges']))
        altered = copy.deepcopy(selection)
        altered['unselectedHalfedges'] = []
        with self.assertRaises(ValueError):
            validate_coverage(report, altered, selection['selectedHalfedges'])
        self.assertEqual([], select_domains(report, [])['selectedHalfedges'])

    def test_stale_duplicate_and_unknown_loop_selections_are_refused(self):
        report = extract(*source_box([1]))
        identity = report['loops'][0]['id']
        for ids, snapshot in (([identity], 'stale'), ([identity, identity], None), (['unknown'], None)):
            with self.assertRaises(ValueError):
                select_domains(report, ids, snapshot)

    def test_bad_source_faces_edges_and_winding_are_refused_with_witnesses(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0],
                      [0, -1, 0], [0, 0, 1]], dtype='f4')
        cases = [([[0, 1, 2], [0, 1, 2]], 'duplicateFaces'),
                 ([[0, 1, 2], [1, 0, 3], [0, 1, 4]], 'nonManifoldEdges'),
                 ([[0, 1, 2], [0, 1, 3]], 'windingEdges'),
                 ([[0, 1, 1]], 'degenerateFaces')]
        for faces, defect in cases:
            report = extract(p, np.array(faces, dtype='u4'))
            self.assertFalse(report['extractionAccepted'])
            self.assertTrue(report['sourceDefects'][defect])
            self.assertEqual([], report['loops'])
            with self.assertRaises(ValueError):
                select_domains(report)

    def test_collinear_and_malformed_arrays_are_not_accepted(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [2, 0, 0]], dtype='f4')
        self.assertFalse(extract(p, np.array([[0, 1, 2]], dtype='u4'))['extractionAccepted'])
        invalid = [(p, np.array([[0, 1, 3]], dtype='u4')),
                   (p, np.array([[0, 1, -1]], dtype='i4')),
                   (p, np.array([[0., 1., 2.]])), (p, np.array([0, 1, 2])),
                   (p[:, :2], np.array([[0, 1, 2]], dtype='u4')),
                   (p * np.nan, np.array([[0, 1, 2]], dtype='u4'))]
        for points, faces in invalid:
            with self.assertRaises(ValueError):
                extract(points, faces)

    def test_budget_and_cancellation_do_not_publish_partial_loops(self):
        p, ix = source_box([0, 1])
        self.assertEqual('halfedge_budget_exceeded', extract(p, ix, max_halfedges=1)['refusal'])
        self.assertEqual('cancelled', extract(p, ix, cancel=lambda: True)['refusal'])
        calls = 0

        def cancel_later():
            nonlocal calls
            calls += 1
            return calls >= 8

        report = extract(p, ix, cancel=cancel_later)
        self.assertFalse(report['extractionAccepted'])
        self.assertEqual([], report['loops'])
        np.testing.assert_array_equal(source_box([0, 1])[1], ix)


if __name__ == '__main__':
    unittest.main()
