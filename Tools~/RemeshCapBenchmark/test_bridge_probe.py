import json
import unittest

import numpy as np

from boundaries import extract
from bridge_probe import build_report, candidate_report, matches_intent, signature, torus_gap
from planes import analyze


class BridgeProbeTests(unittest.TestCase):
    def test_separate_disks_pass_local_gates_but_lose_the_torus_handle(self):
        report = build_report()
        self.assertTrue(report['referenceProbePassed'])
        torus = report['torusBandGap']
        disks, bridge = torus['separateDisks'], torus['referenceBridge']
        self.assertEqual(['whole_plane_hypothesis'] * 2, torus['wholePlaneStatuses'])
        for cap in (disks, bridge):
            self.assertTrue(cap['topology']['closedManifold'])
            self.assertTrue(cap['geometry']['capGeometryAccepted'])
            self.assertEqual(1, cap['signature']['componentCount'])
        self.assertEqual(2, disks['signature']['eulerCharacteristic'])
        self.assertEqual(0, disks['signature']['components'][0]['genus'])
        self.assertFalse(disks['explicitTopologyIntentMatched'])
        self.assertEqual(0, bridge['signature']['eulerCharacteristic'])
        self.assertEqual(1, bridge['signature']['components'][0]['genus'])
        self.assertTrue(bridge['explicitTopologyIntentMatched'])
        self.assertEqual([True, True], torus['sequentialDisks']['stepsAccepted'])
        self.assertFalse(torus['sequentialDisks']['expectedTorusIntentMatched'])
        json.loads(json.dumps(report, allow_nan=False))

    def test_same_open_signature_does_not_determine_disk_or_bridge_intent(self):
        report = build_report()
        signatures = [report['torusBandGap']['sourceSignature'], report['boxOppositeFaces']['sourceSignature']]
        for sig in signatures:
            self.assertEqual(1, sig['componentCount'])
            self.assertEqual(2, sig['boundaryLoops'])
            self.assertEqual(0, sig['eulerCharacteristic'])
            self.assertEqual(0, sig['components'][0]['genus'])
        self.assertTrue(report['boxOppositeFaces']['referenceDisks']['referenceGatesPassed'])

    def test_bridge_and_disks_preserve_source_and_oppose_all_boundary_edges(self):
        p, source, bridge, disk_p, disks, reference = torus_gap()
        saved_p, saved_ix = p.copy(), source.copy()
        before = extract(p, source)
        self.assertEqual(2, len(before['loops']))
        for points, faces in ((p, bridge), (disk_p, disks)):
            after = extract(points, faces)
            self.assertTrue(after['extractionAccepted'])
            self.assertEqual([], after['loops'])
            np.testing.assert_array_equal(p, points[:len(p)])
            np.testing.assert_array_equal(source, faces[:len(source)])
        self.assertEqual(sorted(tuple(sorted(t)) for t in reference), sorted(tuple(sorted(t)) for t in bridge))
        np.testing.assert_array_equal(saved_p, p)
        np.testing.assert_array_equal(saved_ix, source)

    def test_signatures_survive_winding_seams_transform_and_unused_vertices(self):
        p, source, bridge, _, _, _ = torus_gap(10, 6)
        for faces, genus, loops in ((source, 0, 2), (bridge, 1, 0)):
            for variant in (faces, faces[:, ::-1], np.roll(faces, 1, axis=1), faces[::-1]):
                points = np.concatenate((p[:, [1, 2, 0]] * 8 + [3, -2, 1], [[100, 100, 100]])).astype('f4')
                sig = signature(points, variant)
                self.assertTrue(sig['signatureAvailable'])
                self.assertEqual(genus, sig['components'][0]['genus'])
                self.assertEqual(loops, sig['boundaryLoops'])
            split_p = p[faces].reshape(-1, 3)
            split_ix = np.arange(faces.size, dtype='u4').reshape(-1, 3)
            sig = signature(split_p, split_ix)
            self.assertEqual(genus, sig['components'][0]['genus'])

    def test_signature_refuses_nonmanifold_or_singular_topology(self):
        p = np.array([[0, 0, 0], [1, 0, 0], [0, 1, 0], [-1, 0, 0], [0, -1, 0]], dtype='f4')
        for faces in ([[0, 1, 2], [0, 3, 4]], [[0, 1, 2], [0, 1, 2]]):
            self.assertFalse(signature(p, np.array(faces, dtype='u4'))['signatureAvailable'])

    def test_explicit_intent_includes_component_count_and_closed_boundaries(self):
        p, source, bridge, _, _, _ = torus_gap(8, 6)
        self.assertFalse(matches_intent(signature(p, source), [1]))
        two_p = np.concatenate((p, p + np.array([8, 0, 0], dtype='f4')))
        two_ix = np.concatenate((bridge, bridge + len(p)))
        sig = signature(two_p, two_ix)
        self.assertFalse(matches_intent(sig, [1]))
        self.assertTrue(matches_intent(sig, [1, 1]))

    def test_reference_bridge_stays_clean_after_rotation_and_scaling(self):
        p, source, bridge, _, _, _ = torus_gap(8, 6)
        angle = .31
        rotation = np.array([[np.cos(angle), -np.sin(angle), 0], [np.sin(angle), np.cos(angle), 0], [0, 0, 1]])
        for scale in (.125, 8):
            moved = (p.astype('d') @ rotation.T * scale + [3, -2, 1]).astype('f4')
            self.assertTrue(candidate_report(moved, bridge, len(source), [1])['referenceGatesPassed'])
            self.assertTrue(all(l['status'] == 'whole_plane_hypothesis' for l in analyze(moved, source)['loops']))


if __name__ == '__main__':
    unittest.main()
