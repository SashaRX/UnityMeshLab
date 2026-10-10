import json
from pathlib import Path
import tempfile
import unittest

import numpy as np

from bridge_probe import torus_gap
from compare_bridge import border, prepare, verify


class BridgeComparisonTests(unittest.TestCase):
    def test_reference_bridge_preserves_intent(self):
        p, source, bridge, *_ = torus_gap()
        selected, _ = border(p, source)
        report = verify(p, source, p, bridge, selected)
        self.assertTrue(report['accepted'])
        self.assertEqual(report['after']['components'][0]['genus'], 1)

    def test_disks_do_not_pass_annulus_gate(self):
        p, source, _, disks_p, disks, _ = torus_gap()
        selected, _ = border(p, source)
        report = verify(p, source, disks_p, disks, selected)
        self.assertFalse(report['accepted'])
        self.assertFalse(report['eulerPreserved'])

    def test_untouched_input_does_not_pass_bridge_gate(self):
        p, source, *_ = torus_gap()
        selected, _ = border(p, source)
        self.assertFalse(verify(p, source, p, source, selected)['accepted'])

    def test_bridge_preserves_an_unselected_opening(self):
        p, source, bridge, *_ = torus_gap(12, 16)
        source = np.delete(source, 166, axis=0)
        bridge = np.delete(bridge, 166, axis=0)
        loops, _ = border(p, source)
        selected = [loop for loop in loops if len(loop['halfedges']) == 16]
        report = verify(p, source, p, bridge, selected)
        self.assertTrue(report['accepted'])
        self.assertEqual(report['after']['boundaryLoops'], 1)

    def test_changed_donor_is_rejected(self):
        p, source, bridge, *_ = torus_gap()
        selected, _ = border(p, source)
        moved = p.copy()
        moved[0, 0] += .01
        with self.assertRaisesRegex(ValueError, 'original oriented faces'):
            verify(p, source, moved, bridge, selected)

    def test_prepare_writes_five_replayable_fixtures(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory)
            manifest = prepare(output, [])
            self.assertEqual(len(manifest['cases']), 5)
            self.assertEqual(json.loads((output / 'manifest.json').read_text()), manifest)
            self.assertTrue(all(Path(case['source']).is_file() for case in manifest['cases']))


if __name__ == '__main__':
    unittest.main()
