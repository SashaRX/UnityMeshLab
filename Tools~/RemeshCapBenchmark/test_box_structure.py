import unittest

import numpy as np

from box_structure_probe import POINTS, probe


class BoxStructureTests(unittest.TestCase):
    def test_single_and_opposite_missing_faces_have_planar_separate_rims(self):
        for missing, edges in (([1], [4]), ([0, 1], [4, 4])):
            report = probe('planar', missing)
            self.assertEqual(edges, [v['edges'] for v in report['loops']])
            self.assertTrue(all(v['wholePlane']['maxDistance'] < 1e-12 for v in report['loops']))
            self.assertEqual(0, report['groundTruthNewVertices'])
            self.assertEqual(2 * len(missing), report['groundTruthCapTriangles'])

    def test_adjacent_missing_faces_have_one_nonplanar_rim_but_valid_two_face_reference(self):
        report = probe('adjacent', [1, 3])
        self.assertEqual([6], [v['edges'] for v in report['loops']])
        self.assertAlmostEqual(2 * np.sqrt(2) / 3, report['loops'][0]['wholePlane']['maxDistance'])
        self.assertEqual(4, report['groundTruthCapTriangles'])
        self.assertEqual(0, report['groundTruthNewVertices'])
        self.assertTrue(report['groundTruthTopology']['closedManifold'])
        self.assertEqual(0, report['groundTruthGeometry']['newCrossingOrOverlapPairs'])

    def test_three_missing_faces_require_restoring_a_vertex_absent_from_source(self):
        report = probe('corner', [1, 3, 5])
        self.assertEqual([6], [v['edges'] for v in report['loops']])
        self.assertEqual(1, report['groundTruthNewVertices'])
        self.assertEqual(6, report['groundTruthCapTriangles'])
        self.assertTrue(report['groundTruthTopology']['closedManifold'])
        self.assertTrue(report['groundTruthGeometry']['capGeometryAccepted'])

    def test_plane_distances_follow_rotation_translation_and_scale(self):
        angle = .37
        rotation = np.array([[np.cos(angle), -np.sin(angle), 0],
                             [np.sin(angle), np.cos(angle), 0], [0, 0, 1]])
        for scale in (.125, 8):
            points = (POINTS.astype('d') @ rotation.T * scale + [3, -2, 1]).astype('f4')
            flat = probe('transformed-flat', [1], points)
            self.assertLess(flat['loops'][0]['wholePlane']['maxDistance'], 2e-6)
            folded = probe('transformed-folded', [1, 3], points)
            self.assertAlmostEqual(scale * 2 * np.sqrt(2) / 3,
                                   folded['loops'][0]['wholePlane']['maxDistance'], delta=2e-6)


if __name__ == '__main__':
    unittest.main()
