from pathlib import Path
import tempfile
import unittest

import numpy as np

from step_timing import load_graph, timing, torch


class TimingGuards(unittest.TestCase):
    def test_boundary_labels_and_loss_mask_cannot_enter_training_loss(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "graph.npz"
            arrays = dict(edge_features=np.zeros((3, 2)), edge_neighbors=np.full((3, 4), -1),
                          seam_labels=np.array([True, False, False]), learned_mask=np.array([True, True, False]),
                          forced_boundary=np.array([False, False, True]))
            np.savez(path, **arrays)
            load_graph(path)
            arrays["learned_mask"][2] = True
            np.savez(path, **arrays)
            with self.assertRaisesRegex(ValueError, "only internal"):
                load_graph(path)
            arrays["learned_mask"][2] = False
            arrays["seam_labels"][2] = True
            np.savez(path, **arrays)
            with self.assertRaisesRegex(ValueError, "only internal"):
                load_graph(path)

    @unittest.skipUnless(torch is not None, "Optional PyTorch unavailable")
    def test_existing_output_is_preserved_without_loading_input(self):
        with tempfile.TemporaryDirectory() as folder:
            output = Path(folder) / "result.json"
            output.write_bytes(b"existing result")
            with self.assertRaisesRegex(ValueError, "already exists"):
                timing(Path(folder) / "absent.npz", output)
            self.assertEqual(output.read_bytes(), b"existing result")


if __name__ == "__main__":
    unittest.main()
