"""Synthetic trainer contract tests; these tetrahedra are not a UV quality dataset."""
import copy
import json
from pathlib import Path
import struct
import tempfile
import unittest
from unittest.mock import patch

import numpy as np

from prepare import prepare
import train as trainer


def fixture(root):
    entries = []
    faces = np.array([[0, 2, 1], [0, 1, 3], [0, 3, 2], [1, 2, 3]])
    for number, (asset, split, deform, variants) in enumerate([
            ("a", "train", .8, 2), ("b", "train", 1.2, 1),
            ("c", "val", 1.6, 1), ("d", "test", 2.1, 1)]):
        p = np.array([[0., 0, 0], [1., 0, 0], [.1 * number, deform, 0], [0., .2, 1.]])
        p = p[faces.ravel()]
        f = np.arange(12).reshape(4, 3)
        for variant in range(variants):
            uv = p[:, :2].copy()
            uv[variant * 3:variant * 3 + 3] += .2
            path = root / f"{asset}-{variant}.bin"
            path.write_bytes(struct.pack("<3i", len(p), f.size, 4) + p.astype("<f4").tobytes()
                + uv.astype("<f4").tobytes() + f.astype("<i4").tobytes()
                + np.arange(len(p)).astype("<i4").tobytes())
            entries.append(dict(asset_id=asset, family_id="lineage-" + asset, split=split, capture=path.name))
    manifest = root / "manifest.json"
    manifest.write_text(json.dumps(entries), encoding="utf-8")
    output = root / "prepared"
    prepare(manifest, output)
    return output


def report(path):
    return json.loads((path / "report.json").read_text(encoding="utf-8"))


def write_report(path, value):
    (path / "report.json").write_text(json.dumps(value), encoding="utf-8")


def replace_arrays(path, row, **updates):
    file = path / row["npz"]
    with np.load(file, allow_pickle=False) as data:
        arrays = {key: data[key] for key in data.files}
    arrays.update(updates)
    np.savez_compressed(file, **arrays)
    row["npz_sha256"] = trainer.sha(file)


class DatasetChecks(unittest.TestCase):
    def test_single_asset_or_missing_split_fails_before_output_creation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = fixture(root)
            value = report(prepared)
            value["records"] = value["records"][:2]
            value.update(captures=2, independent_models=1, split_groups=1)
            write_report(prepared, value)
            with self.assertRaisesRegex(ValueError, "Independent train, val and test"):
                trainer.train(prepared, root / "run", device="cpu")
            self.assertFalse((root / "run").exists())
            value["records"][0]["split"] = "unassigned"
            write_report(prepared, value)
            with self.assertRaisesRegex(ValueError, "Explicit train/val/test"):
                trainer.dataset(prepared)

    def test_family_asset_geometry_and_report_group_leaks_are_rechecked(self):
        with tempfile.TemporaryDirectory() as temporary:
            prepared = fixture(Path(temporary))
            original = report(prepared)
            for key in ("family_id", "asset_id", "model_group"):
                changed = copy.deepcopy(original)
                changed["records"][3][key] = changed["records"][0][key]
                write_report(prepared, changed)
                with self.subTest(key=key), self.assertRaisesRegex(ValueError, "leakage|model_group"):
                    trainer.dataset(prepared)
            changed = copy.deepcopy(original)
            source, target = changed["records"][0], changed["records"][3]
            (prepared / target["npz"]).write_bytes((prepared / source["npz"]).read_bytes())
            for key in ("npz_sha256", "geometry_sha256", "vertices", "faces", "edges", "internal_cuts", "forced_boundaries"):
                target[key] = source[key]
            write_report(prepared, changed)
            with self.assertRaisesRegex(ValueError, "leakage|model_group"):
                trainer.dataset(prepared)

    def test_hash_and_reconstructed_geometry_reject_forged_inputs(self):
        with tempfile.TemporaryDirectory() as temporary:
            prepared = fixture(Path(temporary))
            value = report(prepared)
            row = value["records"][0]
            file = prepared / row["npz"]
            original = file.read_bytes()
            file.write_bytes(original + b"changed")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                trainer.dataset(prepared)
            file.write_bytes(original)
            with np.load(file, allow_pickle=False) as data:
                features = data["edge_features"].copy()
            features[0, 0] = 99.
            replace_arrays(prepared, row, edge_features=features)
            write_report(prepared, value)
            with self.assertRaisesRegex(ValueError, "features disagree"):
                trainer.dataset(prepared)
            file.write_bytes(original)
            row["npz_sha256"] = trainer.sha(file)
            with np.load(file, allow_pickle=False) as data:
                labels, mask, boundary = data["seam_labels"].copy(), data["learned_mask"].copy(), data["forced_boundary"].copy()
            labels[0], mask[0], boundary[0] = False, False, True
            replace_arrays(prepared, row, seam_labels=labels, learned_mask=mask, forced_boundary=boundary)
            write_report(prepared, value)
            with self.assertRaisesRegex(ValueError, "disagrees with geometry"):
                trainer.dataset(prepared)

    def test_training_only_normalization_and_length_asset_balancing(self):
        graphs = [dict(row=dict(asset_id="a", model_group=0, split="train"), x=np.array([[1., 2.], [3., 4.]])),
                  dict(row=dict(asset_id="b", model_group=1, split="val"), x=np.array([[1e6, 1e6]]))]
        norm = trainer.normalization(trainer.assets(graphs, "train"))
        np.testing.assert_allclose(norm["mean"], [2., 3.])
        graphs[1]["x"] *= 100
        self.assertEqual(norm, trainer.normalization(trainer.assets(graphs, "train")))
        a = dict(row=dict(asset_id="a", model_group=0), mask=np.array([True, True, False]),
                 y=np.array([True, False, True]), lengths=np.array([1., 3., 1000.]))
        b = dict(row=dict(asset_id="b", model_group=1), mask=np.array([True]), y=np.array([True]), lengths=np.array([50.]))
        scores = trainer.metrics([(a, np.array([.9, .9, .9])), (b, np.array([.1]))], .5)
        self.assertAlmostEqual(scores["per_asset"]["0"]["precision"], .25)
        self.assertAlmostEqual(scores["per_asset"]["0"]["recall"], 1.)
        self.assertAlmostEqual(scores["per_asset"]["0"]["f1"], .4)
        repeated = trainer.metrics([(a, np.array([.9, .9, .9])), (a, np.array([.9, .9, .9])),
                                    (b, np.array([.1]))], .5)
        self.assertEqual(scores["macro_asset"], repeated["macro_asset"])
        self.assertEqual(scores["normalized_micro"], repeated["normalized_micro"])


@unittest.skipUnless(trainer.torch is not None, "Optional local PyTorch unavailable")
class TrainingChecks(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.threads = trainer.torch.get_num_threads()
        trainer.torch.set_num_threads(1)

    @classmethod
    def tearDownClass(cls):
        trainer.torch.set_num_threads(cls.threads)

    def run_training(self, prepared, output, **kwargs):
        return trainer.train(prepared, output, epochs=4, width=8, blocks=1, device="cpu", **kwargs)

    def test_exact_epoch_resume_optimizer_rng_and_safe_checkpoint_reload(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = fixture(root)
            whole = self.run_training(prepared, root / "whole")
            partial = self.run_training(prepared, root / "resumed", max_epochs=2)
            self.assertFalse(partial["test_evaluated"])
            resumed = self.run_training(prepared, root / "resumed", resume=True)
            self.assertEqual(whole, resumed)
            a = trainer.torch.load(root / "whole" / "last.pt", weights_only=True)
            b = trainer.torch.load(root / "resumed" / "last.pt", weights_only=True)
            for name in a["model"]:
                trainer.torch.testing.assert_close(a["model"][name], b["model"][name], atol=0., rtol=0.)
            for index in a["optimizer"]["state"]:
                for key in a["optimizer"]["state"][index]:
                    trainer.torch.testing.assert_close(a["optimizer"]["state"][index][key],
                        b["optimizer"]["state"][index][key], atol=0., rtol=0.)
            trainer.torch.testing.assert_close(a["rng"]["torch"], b["rng"]["torch"], atol=0., rtol=0.)
            self.assertEqual(a["rng"]["python"], b["rng"]["python"])
            self.assertFalse(whole["uv_quality_evaluated"])
            self.assertEqual(whole["test_prediction_sha256"], resumed["test_prediction_sha256"])

    def test_resume_rejects_changed_config_data_completed_run_and_output_overwrite(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = fixture(root)
            output = root / "run"
            self.run_training(prepared, output, max_epochs=1)
            original_checkpoint = (output / "last.pt").read_bytes()
            with self.assertRaisesRegex(ValueError, "configuration or dataset changed"):
                self.run_training(prepared, output, resume=True, lr=.02)
            with self.assertRaisesRegex(ValueError, "fresh"):
                self.run_training(prepared, output)
            value = report(prepared)
            unchanged_report = (prepared / "report.json").read_bytes()
            value["manifest_sha256"] = "different input manifest"
            write_report(prepared, value)
            with self.assertRaisesRegex(ValueError, "configuration or dataset changed"):
                self.run_training(prepared, output, resume=True)
            self.assertEqual(original_checkpoint, (output / "last.pt").read_bytes())
            (prepared / "report.json").write_bytes(unchanged_report)
            self.run_training(prepared, output, resume=True)
            with self.assertRaisesRegex(ValueError, "Completed test run"):
                self.run_training(prepared, output, resume=True)

    def test_test_split_is_evaluated_only_after_last_training_epoch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = fixture(root)
            calls = []
            original = trainer.predictions

            def tracked(model, grouped, norm, device):
                calls.append(next(iter(grouped.values()))[0]["row"]["split"])
                return original(model, grouped, norm, device)

            with patch.object(trainer, "predictions", side_effect=tracked):
                self.run_training(prepared, root / "run", max_epochs=2)
                self.assertNotIn("test", calls)
                self.run_training(prepared, root / "run", resume=True)
            self.assertEqual(calls.count("test"), 1)
            self.assertEqual(calls[-1], "test")

    def test_aliases_share_one_optimizer_step_and_final_json_recovers_without_test(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            prepared = fixture(root)
            value = report(prepared)
            value["records"][1]["asset_id"] = "alias-a"
            write_report(prepared, value)
            graphs, _ = trainer.dataset(prepared)
            self.assertEqual(len(trainer.assets(graphs, "train")), 2)
            steps = []
            original_step = trainer.torch.optim.AdamW.step

            def tracked_step(optimizer, *args, **kwargs):
                steps.append(1)
                return original_step(optimizer, *args, **kwargs)

            with patch.object(trainer.torch.optim.AdamW, "step", tracked_step):
                self.run_training(prepared, root / "run", max_epochs=1)
            self.assertEqual(len(steps), 2)
            self.run_training(prepared, root / "run", resume=True)
            state = trainer.torch.load(root / "run" / "last.pt", weights_only=True)
            (root / "run" / "metrics.json").write_bytes(b'{"completed": false}')
            with patch.object(trainer, "predictions", side_effect=AssertionError("Must not repeat test")):
                recovered = self.run_training(prepared, root / "run", resume=True)
            self.assertTrue(recovered["recovered_final_metrics"])
            self.assertEqual((root / "run" / "metrics.json").read_bytes(), trainer.encoded(state["final"]))


if __name__ == "__main__":
    unittest.main()
