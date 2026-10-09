from collections import Counter
import json
import unittest

import numpy as np

from boundaries import extract
from planes import Options, analyze, fit_plane
from test_boundaries import source_box


def rim_surface(rim):
    rim = np.asarray(rim, dtype='f8')
    center = rim.mean(axis=0) + [0, 0, -1]
    p = np.concatenate((rim, center[None, :]))
    ix = np.array([[i, (i + 1) % len(rim), len(rim)] for i in range(len(rim))], dtype='u4')
    return p, ix


def plane_axes(hypothesis):
    axes = []
    for arc in hypothesis['arcs']:
        normal, origin = np.array(arc['plane']['normal']), np.array(arc['plane']['origin'])
        axis = int(np.abs(normal).argmax())
        axes.append((axis, float(normal @ origin)))
    return sorted(axes)


class PlaneTests(unittest.TestCase):
    def test_plane_reports_roundtrip_through_strict_json_for_replay(self):
        for missing in ([1], [0, 1], [1, 3], [1, 3, 5]):
            report = analyze(*source_box(missing))
            self.assertEqual(report, json.loads(json.dumps(report, allow_nan=False)))

    def test_whole_plane_recognizes_single_and_opposite_missing_box_faces(self):
        for missing, count in (([1], 1), ([0, 1], 2)):
            report = analyze(*source_box(missing))
            self.assertTrue(report['analysisComplete'])
            self.assertEqual(count, len(report['loops']))
            self.assertTrue(all(loop['status'] == 'whole_plane_hypothesis' for loop in report['loops']))
            self.assertEqual(0, report['facesGenerated'])

    def test_adjacent_missing_faces_have_one_supported_two_plane_partition(self):
        report = analyze(*source_box([1, 3]))
        loop = report['loops'][0]
        self.assertFalse(loop['wholePlane']['withinTolerance'])
        self.assertEqual('piecewise_plane_hypothesis', loop['status'])
        self.assertEqual(1, loop['hypothesisCount'])
        hypothesis = loop['hypotheses'][0]
        self.assertEqual(2, hypothesis['planeCount'])
        self.assertEqual([3, 3], [len(arc['halfedges']) for arc in hypothesis['arcs']])
        axes = plane_axes(hypothesis)
        self.assertEqual([0, 2], [axis for axis, _ in axes])
        np.testing.assert_allclose([offset for _, offset in axes], [1, 1], atol=1e-12)
        self.assertEqual(Counter(report['selection']['selectedHalfedges']),
                         Counter(h for arc in hypothesis['arcs'] for h in arc['halfedges']))

    def test_three_missing_faces_keep_both_valid_plane_decompositions_for_p3(self):
        report = analyze(*source_box([1, 3, 5]))
        loop = report['loops'][0]
        self.assertEqual('ambiguous_piecewise_planes', loop['status'])
        self.assertEqual(2, loop['hypothesisCount'])
        for hypothesis in loop['hypotheses']:
            self.assertEqual(3, hypothesis['planeCount'])
            self.assertEqual([2, 2, 2], [len(arc['halfedges']) for arc in hypothesis['arcs']])
            self.assertEqual([0, 1, 2], [axis for axis, _ in plane_axes(hypothesis)])
        offsets = [[offset for _, offset in plane_axes(h)] for h in loop['hypotheses']]
        self.assertTrue(any(np.allclose(v, [1, 1, 1]) for v in offsets))
        self.assertTrue(any(np.allclose(v, [-1, -1, -1]) for v in offsets))

    def test_smooth_nonplanar_rim_is_not_forced_into_many_flat_faces(self):
        angle = np.arange(64) * (2 * np.pi / 64)
        rim = np.column_stack((np.cos(angle), np.sin(angle), .15 * np.sin(3 * angle)))
        loop = analyze(*rim_surface(rim))['loops'][0]
        self.assertFalse(loop['wholePlane']['withinTolerance'])
        self.assertEqual('no_supported_piecewise_hypothesis', loop['status'])
        self.assertEqual([], loop['hypotheses'])

    def test_collinear_and_nearly_collinear_support_do_not_define_a_plane(self):
        self.assertFalse(fit_plane([[0, 0, 0], [1, 0, 0], [2, 1e-10, 0]])['rankSupported'])
        loop = analyze(*rim_surface([[0, 0, 0], [1, 0, 0], [2, 0, 0], [3, 0, 0]]))['loops'][0]
        self.assertEqual('underdetermined_support', loop['status'])

    def test_planar_projected_crossing_is_refused(self):
        loop = analyze(*rim_surface([[0, 0, 0], [1, 1, 0], [0, 1, 0], [1, 0, 0]]))['loops'][0]
        self.assertTrue(loop['wholePlane']['withinTolerance'])
        self.assertEqual('projected_boundary_invalid', loop['status'])

    def test_junctions_are_preserved_instead_of_flattened_into_polygons(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [-1, 0, 0], [0, -1, 0]], dtype='f4')
        ix = np.array([[0, 1, 2], [0, 3, 4]], dtype='u4')
        report = analyze(p, ix)
        self.assertTrue(report['analysisComplete'])
        self.assertTrue(all(loop['status'] == 'junction_resolution_required' for loop in report['loops']))
        self.assertTrue(all(not loop['hypotheses'] for loop in report['loops']))

    def test_rotation_translation_scale_winding_and_corner_offsets_preserve_structure(self):
        p, ix = source_box([1, 3])
        angle = .37
        rotation = np.array([[np.cos(angle), -np.sin(angle), 0],
                             [np.sin(angle), np.cos(angle), 0], [0, 0, 1]])
        for scale in (.125, 8):
            moved = (p.astype('d') @ rotation.T * scale + [3, -2, 1]).astype('f4')
            for faces in (ix, ix[:, ::-1], np.roll(ix, 1, axis=1), ix[::-1]):
                loop = analyze(moved, faces)['loops'][0]
                self.assertEqual('piecewise_plane_hypothesis', loop['status'])
                self.assertEqual(2, loop['hypotheses'][0]['planeCount'])

    def test_uneven_boundary_subdivision_does_not_change_the_two_plane_structure(self):
        p, ix = source_box([1, 3])
        for _ in range(4):
            h = extract(p, ix)['loops'][0]['halfedges'][0]
            face, corner = h['face'], h['corner']
            a, b, c = map(int, np.roll(ix[face], -corner))
            m = len(p)
            p = np.concatenate((p, ((p[a] * 3 + p[b]) / 4)[None, :])).astype('f4')
            ix = np.concatenate((np.delete(ix, face, axis=0), [[a, m, c], [m, b, c]])).astype('u4')
        loop = analyze(p, ix)['loops'][0]
        self.assertEqual('piecewise_plane_hypothesis', loop['status'])
        self.assertEqual([0, 2], [axis for axis, _ in plane_axes(loop['hypotheses'][0])])

    def test_maximum_residual_rejects_an_outlier_hidden_by_average_error(self):
        angle = np.arange(100) * (2 * np.pi / 100)
        rim = np.column_stack((np.cos(angle), np.sin(angle), np.zeros(100)))
        rim[0, 2] = .1
        loop = analyze(*rim_surface(rim), Options(relative_tolerance=0, absolute_tolerance=.02))['loops'][0]
        self.assertLess(loop['wholePlane']['rmsDistance'], .02)
        self.assertGreater(loop['wholePlane']['maxDistance'], .02)
        self.assertFalse(loop['wholePlane']['withinTolerance'])

    def test_tolerated_noise_and_source_preservation(self):
        p, ix = source_box([1])
        p[:, 2] += np.arange(len(p), dtype='f4') % 3 * 1e-4
        before_p, before_ix = p.copy(), ix.copy()
        loop = analyze(p, ix, Options(relative_tolerance=1e-4))['loops'][0]
        self.assertEqual('whole_plane_hypothesis', loop['status'])
        self.assertGreater(loop['wholePlane']['maxDistance'], 0)
        np.testing.assert_array_equal(before_p, p)
        np.testing.assert_array_equal(before_ix, ix)

    def test_selected_loop_and_truncated_ambiguity_are_explicit(self):
        p, ix = source_box([0, 1])
        selected = extract(p, ix)['loops'][1]['id']
        self.assertEqual([selected], [loop['loopId'] for loop in analyze(p, ix, loop_ids=[selected])['loops']])
        loop = analyze(*source_box([1, 3, 5]), Options(max_hypotheses=1))['loops'][0]
        self.assertEqual('ambiguous_piecewise_planes', loop['status'])
        self.assertTrue(loop['hypothesesTruncated'])
        self.assertEqual(2, loop['hypothesisCount'])

    def test_higher_plane_count_can_be_requested_after_future_structure_refusals(self):
        loop = analyze(*source_box([1, 3]), Options(min_planes=3))['loops'][0]
        self.assertGreater(loop['hypothesisCount'], 0)
        self.assertTrue(all(h['planeCount'] == 3 for h in loop['hypotheses']))

    def test_budget_cancellation_and_invalid_parameters_are_explicit(self):
        p, ix = source_box([1, 3])
        for options, reason in ((Options(max_fits=1), 'fits_budget_exceeded'),
                                (Options(max_states=1), 'states_budget_exceeded'),
                                (Options(max_loop_edges=3), 'loop_edge_budget_exceeded')):
            report = analyze(p, ix, options)
            self.assertFalse(report['analysisComplete'])
            self.assertEqual(reason, report['refusal'])
            self.assertEqual(0, report['facesGenerated'])
        self.assertEqual('cancelled', analyze(p, ix, cancel=lambda: True)['refusal'])
        for options in (Options(relative_tolerance=-1), Options(absolute_tolerance=np.nan),
                        Options(max_planes=1), Options(min_plane_angle_degrees=0),
                        Options(relative_tolerance=1e308)):
            with self.assertRaises(ValueError):
                analyze(p, ix, options)


if __name__ == '__main__':
    unittest.main()
