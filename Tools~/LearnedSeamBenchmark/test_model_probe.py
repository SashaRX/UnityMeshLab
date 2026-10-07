import unittest

from model_probe import SeamGraph, torch


@unittest.skipUnless(torch is not None, "Optional PyTorch is unavailable")
class GraphChecks(unittest.TestCase):
    def test_edge_reordering_and_neighbor_slot_order_preserve_logits(self):
        torch.manual_seed(17)
        model = SeamGraph()
        features = torch.randn(6, 2)
        neighbors = torch.tensor([[1,2,3,4], [0,2,4,5], [0,1,3,5], [0,2,4,5], [0,1,3,5], [1,2,3,4]])
        permutation = torch.tensor([4, 2, 0, 5, 1, 3])
        inverse = torch.argsort(permutation)
        with torch.no_grad():
            expected = model(features, neighbors)
            actual = model(features[permutation], inverse[neighbors[permutation]])
            torch.testing.assert_close(actual, expected[permutation], atol=2e-6, rtol=2e-6)
            torch.testing.assert_close(model(features, neighbors[:, [3, 1, 0, 2]]), expected, atol=2e-6, rtol=2e-6)

    def test_missing_neighbors_produce_finite_logits_and_gradients(self):
        torch.manual_seed(19)
        model = SeamGraph()
        features = torch.tensor([[0., 0.], [1., 3.14159], [0.5, 0.2]])
        neighbors = torch.tensor([[-1,-1,-1,-1], [2,-1,-1,-1], [1,-1,-1,-1]])
        logits = model(features, neighbors)
        self.assertTrue(torch.isfinite(logits).all())
        logits.square().mean().backward()
        for parameter in model.parameters():
            self.assertIsNotNone(parameter.grad)
            self.assertTrue(torch.isfinite(parameter.grad).all())


if __name__ == "__main__":
    unittest.main()
