import json
import unittest

import numpy as np

from boundaries import extract
from bridge_probe import torus_gap
from collar_growth import Options, closest_trajectories, evaluate
from incremental_probe import compact_box


def growth(p, ix, **kwargs):
    b = extract(p, ix)
    return evaluate(p, ix, b['sourceHash'], [loop['id'] for loop in b['loops']], **kwargs)


def flat_ring(n=8):
    angle = np.arange(n) * 2 * np.pi / n
    p = np.array([[r * np.cos(t), r * np.sin(t), 0] for r in (1, 2) for t in angle], dtype='f4')
    ix = np.array([tri for i in range(n) for tri in ((i, n + i, n + (i + 1) % n),
                                                  (i, n + (i + 1) % n, (i + 1) % n))], dtype='u4')
    return p, ix


class CollarGrowthTests(unittest.TestCase):
    def test_torus_outward_growth_meets_other_rim_reciprocally(self):
        p, ix, *_ = torus_gap()
        saved = p.copy()
        report = growth(p, ix)
        self.assertTrue(report['analysisComplete'])
        outside, inside = report['sides']
        pair = outside['betweenLoops'][0]
        self.assertEqual(8, pair['mutualPairCount'])
        self.assertEqual(1, pair['firstCoverage'])
        self.assertEqual(1, pair['secondCoverage'])
        self.assertEqual(0, inside['betweenLoops'][0]['hitPairCount'])
        self.assertFalse(report['closureTypeCertified'])
        json.loads(json.dumps(report, allow_nan=False))
        np.testing.assert_array_equal(saved, p)

    def test_box_has_only_inward_meetings_so_unsigned_counts_would_mislead(self):
        report = growth(*compact_box([0, 1]))
        self.assertEqual(0, report['sides'][0]['betweenLoops'][0]['mutualPairCount'])
        self.assertEqual(4, report['sides'][1]['betweenLoops'][0]['mutualPairCount'])

    def test_flat_inner_rim_converges_to_center_without_moving_vertices(self):
        p, ix = flat_ring()
        b = extract(p, ix)
        inner = min(b['loops'], key=lambda loop: np.linalg.norm(p[loop['halfedges'][0]['sourceVertices'][0]]))
        report = evaluate(p, ix, b['sourceHash'], [inner['id']])
        self.assertTrue(report['analysisComplete'])
        outside = report['sides'][0]['selfConvergence'][0]
        self.assertEqual(1, outside['vertexCoverage'])
        self.assertLess(outside['meetingSpreadRatio'], 1e-6)
        self.assertEqual(0, report['sides'][1]['selfConvergence'][0]['hitPairCount'])

    def test_closest_trajectories_handles_collinear_facing_and_skew_lines(self):
        p, q = np.array([0., 0, 0]), np.array([2., 0, 0])
        u = np.array([1., 0, 0])
        s, t, distance, _ = closest_trajectories(p, u, q, -u, 2)
        self.assertEqual((1., 1., 0.), (s, t, distance))
        _, _, distance, _ = closest_trajectories(p, u, q + [0, 0, 1], np.array([0., 1, 0]), 2)
        self.assertEqual(1, distance)

    def test_transform_scale_and_reversed_winding_preserve_evidence(self):
        p, ix, *_ = torus_gap(8, 6)
        for scale, faces in ((.125, ix), (8, ix[:, ::-1])):
            q = (p[:, [1, 2, 0]] * scale + [3, -2, 1]).astype('f4')
            report = growth(q, faces)
            self.assertTrue(report['analysisComplete'])
            self.assertEqual(1, report['sides'][0]['betweenLoops'][0]['firstCoverage'])

    def test_limits_cancellation_and_options_refuse_incomplete_evidence(self):
        p, ix, *_ = torus_gap(8, 6)
        for kwargs in ({'options': Options(max_pair_tests=1)}, {'options': Options(max_loop_vertices=3)},
                       {'cancel': lambda: True}):
            report = growth(p, ix, **kwargs)
            self.assertFalse(report['analysisComplete'])
            self.assertEqual([], report['sides'])
        for options in (Options(reach_ratio=0), Options(hit_ratio=float('nan')), Options(max_faces=-1)):
            with self.assertRaises(ValueError):
                growth(p, ix, options=options)


if __name__ == '__main__':
    unittest.main()
