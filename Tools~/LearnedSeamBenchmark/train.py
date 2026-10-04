"""Local edge-label baseline training; held-out edge metrics do not certify a UV unwrap."""
import argparse
from collections import defaultdict
import hashlib
import json
import os
from pathlib import Path
import random
import sys
import tempfile
import uuid

import numpy as np

from model_probe import SeamGraph, torch
from prepare import extract
from step_timing import load_graph


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def encoded(value):
    return json.dumps(value, sort_keys=True, allow_nan=False).encode("utf-8")


def atomic(path, value, checkpoint=False):
    path = Path(path)
    with tempfile.NamedTemporaryFile(dir=path.parent, delete=False) as stream:
        temporary = Path(stream.name)
        if checkpoint:
            torch.save(value, stream)
        else:
            stream.write(encoded(value))
        stream.flush()
        os.fsync(stream.fileno())
    os.replace(temporary, path)


def dataset(prepared):
    """Verify numerical geometry and rebuild grouping; do not trust report counts or IDs."""
    prepared = Path(prepared).resolve()
    report_path = prepared / "report.json"
    report = json.loads(report_path.read_text(encoding="utf-8"))
    if (report.get("schema_version") != 1 or report.get("feature_names") !=
            ["length_over_diagonal", "dihedral_radians"] or not report.get("records")):
        raise ValueError("Unsupported prepared report/schema/features")
    graphs, parents, names = [], {}, set()

    def find(key):
        parents.setdefault(key, key)
        if parents[key] != key:
            parents[key] = find(parents[key])
        return parents[key]

    def union(a, b):
        parents[find(a)] = find(b)

    for row in report["records"]:
        if (not isinstance(row, dict) or any(not isinstance(row.get(k), str) or not row[k].strip()
                for k in ("asset_id", "family_id", "npz", "npz_sha256", "geometry_sha256"))
                or row.get("split") not in ("train", "val", "test")
                or type(row.get("model_group")) is not int or row["model_group"] < 0):
            raise ValueError("Explicit train/val/test splits and valid record IDs are required")
        name = row["npz"]
        if name in names:
            raise ValueError("Duplicate NPZ record")
        names.add(name)
        path = prepared / name
        if ("/" in name or "\\" in name or Path(name).suffix != ".npz"
                or path.resolve().parent != prepared or path.is_symlink()):
            raise ValueError("NPZ must be a direct file in the prepared directory")
        if sha(path) != row["npz_sha256"]:
            raise ValueError("NPZ hash mismatch")
        features, neighbors, labels, mask = load_graph(path)
        with np.load(path, allow_pickle=False) as saved:
            p, f = saved["positions"], saved["faces"]
            rebuilt, geometry = extract((p, np.zeros((len(p), 2)), f, None, None))
            for key in ("positions", "faces", "edges", "face_pairs", "edge_neighbors",
                        "learned_mask", "forced_boundary"):
                if not np.array_equal(saved[key], rebuilt[key]):
                    raise ValueError("Prepared graph disagrees with geometry: " + key)
            if features.shape[1] != 2 or not np.allclose(features, rebuilt["edge_features"], rtol=1e-6, atol=1e-7):
                raise ValueError("Prepared features disagree with geometry")
            edges = rebuilt["edges"]
            lengths = np.linalg.norm(p[edges[:, 1]].astype(float) - p[edges[:, 0]].astype(float), axis=1)
        actual = dict(vertices=len(p), faces=len(f), edges=len(features), internal_cuts=int(labels.sum()),
                      forced_boundaries=int((~mask).sum()))
        if geometry != row["geometry_sha256"] or any(row.get(k) != v for k, v in actual.items()):
            raise ValueError("Report geometry/count mismatch")
        union(("asset", row["asset_id"]), ("geometry", geometry))
        graphs.append(dict(row=row, x=features, neighbors=neighbors, y=labels, mask=mask, lengths=lengths))
    models, groups = defaultdict(set), defaultdict(set)
    for graph in graphs:
        row = graph["row"]
        root = find(("asset", row["asset_id"]))
        models[root].add(row["model_group"])
        groups[row["model_group"]].add(root)
    if any(len(v) != 1 for v in list(models.values()) + list(groups.values())):
        raise ValueError("Invalid model_group grouping")
    for graph in graphs:
        row = graph["row"]
        union(("asset", row["asset_id"]), ("family", row["family_id"]))
    split_groups = defaultdict(set)
    declared_groups = defaultdict(set)
    for graph in graphs:
        row = graph["row"]
        split_groups[find(("asset", row["asset_id"]))].add(row["split"])
        declared_groups[row["model_group"]].add(row["split"])
    if any(len(v) != 1 for v in list(split_groups.values()) + list(declared_groups.values())):
        raise ValueError("Split leakage through asset/family/geometry/model_group")
    if {g["row"]["split"] for g in graphs} != {"train", "val", "test"}:
        raise ValueError("Independent train, val and test groups are all required")
    if (report.get("captures") != len(graphs) or report.get("independent_models") != len(models)
            or report.get("split_groups") != len(split_groups)):
        raise ValueError("Report group/count mismatch")
    fingerprint = hashlib.sha256(encoded(dict(report_sha256=sha(report_path),
        npz_sha256=[g["row"]["npz_sha256"] for g in graphs]))).hexdigest()
    return graphs, fingerprint


def assets(graphs, split):
    result = defaultdict(list)
    for graph in graphs:
        if graph["row"]["split"] == split:
            result[str(graph["row"]["model_group"])].append(graph)
    return result


def normalization(training):
    """Equal weight per asset and per reference; statistics use training features only."""
    moments = [np.mean([np.stack((g["x"].astype(float).mean(0),
                  np.square(g["x"].astype(float)).mean(0))) for g in variants], axis=0)
               for variants in training.values()]
    mean, second = np.mean(moments, axis=0)
    scale = np.sqrt(np.maximum(second - mean * mean, 1e-12))
    return dict(mean=mean.tolist(), scale=scale.tolist())


def metrics(predictions, threshold):
    per_asset = defaultdict(list)
    for graph, probabilities in predictions:
        mask = graph["mask"]
        weight = graph["lengths"][mask]
        weight = weight / weight.sum()
        actual, predicted = graph["y"][mask], probabilities[mask] >= threshold
        counts = [float(weight[actual & predicted].sum()), float(weight[~actual & predicted].sum()),
                  float(weight[actual & ~predicted].sum())]
        per_asset[str(graph["row"]["model_group"])].append(counts)

    def scores(counts):
        tp, fp, fn = map(float, counts)
        return dict(precision=tp / (tp + fp) if tp + fp else 0., recall=tp / (tp + fn) if tp + fn else 0.,
                    f1=2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else 0.,
                    normalized_tp=tp, normalized_fp=fp, normalized_fn=fn)

    counts = {key: np.mean(value, axis=0) for key, value in per_asset.items()}
    individual = {key: dict(scores(value), references=len(per_asset[key])) for key, value in counts.items()}
    return dict(threshold=float(threshold), per_asset=individual,
                macro_asset={key: float(np.mean([s[key] for s in individual.values()]))
                             for key in ("precision", "recall", "f1")},
                normalized_micro=scores(np.mean(list(counts.values()), axis=0)))


def predictions(model, grouped, norm, device):
    model.eval()
    result = []
    with torch.no_grad():
        for variants in grouped.values():
            for graph in variants:
                x = torch.as_tensor((graph["x"] - norm["mean"]) / norm["scale"], dtype=torch.float32, device=device)
                neighbors = torch.as_tensor(graph["neighbors"], dtype=torch.long, device=device)
                probability = torch.sigmoid(model(x, neighbors)).cpu().numpy()
                if not np.isfinite(probability).all():
                    raise ValueError("Non-finite evaluation probabilities")
                result.append((graph, probability))
    return result


def prediction_hash(values):
    digest = hashlib.sha256()
    for graph, probability in values:
        digest.update(graph["row"]["npz"].encode())
        digest.update(probability.astype("<f4").tobytes())
    return digest.hexdigest()


def rng_state(include_cuda=False):
    state = np.random.get_state()
    return dict(python=random.getstate(), numpy=[state[0], torch.tensor(state[1].astype(np.int64)),
        int(state[2]), int(state[3]), float(state[4])], torch=torch.get_rng_state(),
        cuda=torch.cuda.get_rng_state_all() if include_cuda else [])


def restore_rng(state):
    random.setstate(state["python"])
    name, keys, position, gaussian, cached = state["numpy"]
    np.random.set_state((name, keys.cpu().numpy().astype(np.uint32), position, gaussian, cached))
    torch.set_rng_state(state["torch"].cpu())
    if state["cuda"]:
        torch.cuda.set_rng_state_all([value.cpu() for value in state["cuda"]])


def train(prepared, output, epochs=200, lr=.001, width=64, blocks=4, seed=73, device="cuda:0",
          resume=False, max_epochs=None, progress=False):
    if torch is None:
        raise RuntimeError("Local PyTorch is required; this trainer installs nothing")
    if (type(epochs) is not int or epochs < 1 or type(width) is not int or width < 1
            or type(blocks) is not int or blocks < 1 or type(seed) is not int or not 0 <= seed < 2**32
            or not np.isfinite(lr) or lr <= 0
            or (max_epochs is not None and (type(max_epochs) is not int or max_epochs < 1))):
        raise ValueError("Invalid training configuration")
    output = Path(output).resolve()
    if not resume and output.exists():
        raise ValueError("Output must be fresh; use resume only for this trainer's last checkpoint")
    graphs, fingerprint = dataset(prepared)
    grouped = {split: assets(graphs, split) for split in ("train", "val", "test")}
    device = torch.device(device)
    workspace, gpu = None, None
    if device.type == "cuda":
        workspace = os.environ.get("CUBLAS_WORKSPACE_CONFIG")
        if workspace is None and torch.cuda.is_initialized():
            raise ValueError("Launch a fresh process with CUBLAS_WORKSPACE_CONFIG=:4096:8 before CUDA initialization")
        workspace = os.environ.setdefault("CUBLAS_WORKSPACE_CONFIG", ":4096:8")
        if workspace not in (":4096:8", ":16:8"):
            raise ValueError("Deterministic CUDA requires CUBLAS_WORKSPACE_CONFIG=:4096:8 or :16:8")
    if device.type == "cuda" and not torch.cuda.is_available():
        raise ValueError("Requested CUDA is unavailable")
    if device.type == "cuda":
        properties = torch.cuda.get_device_properties(device)
        gpu = dict(name=properties.name, capability=list(torch.cuda.get_device_capability(device)),
                   total_memory_bytes=properties.total_memory)
    config = dict(epochs=epochs, lr=float(lr), weight_decay=.01, width=width, blocks=blocks, seed=seed,
                  device=str(device), precision="float32", features=2, torch_version=str(torch.__version__),
                  cuda_version=torch.version.cuda, gpu=gpu, cublas_workspace=workspace,
                  python_version=sys.version.split()[0], numpy_version=str(np.__version__),
                  cpu_threads=torch.get_num_threads(), deterministic=True, tf32=False,
                  source_sha256={name: sha(Path(__file__).with_name(name))
                  for name in ("train.py", "model_probe.py", "prepare.py", "step_timing.py")})
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    torch.use_deterministic_algorithms(True)
    torch.backends.cuda.matmul.allow_tf32 = False
    torch.backends.cudnn.allow_tf32 = False
    model = SeamGraph(features=2, width=width, blocks=blocks).to(device=device, dtype=torch.float32)
    optimizer = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=.01, foreach=False, fused=False)
    norm = normalization(grouped["train"])
    start, best_score, history, run_id = 0, -1., [], str(uuid.uuid4())
    if resume:
        state = torch.load(output / "last.pt", map_location=device, weights_only=True)
        if state["config"] != config or state["dataset_fingerprint"] != fingerprint or state["normalization"] != norm:
            raise ValueError("Resume configuration or dataset changed")
        if state["finished"]:
            final_path = output / "metrics.json"
            if not final_path.exists() or final_path.read_bytes() != encoded(state["final"]):
                atomic(final_path, state["final"])
                return dict(state["final"], recovered_final_metrics=True)
            raise ValueError("Completed test run cannot be resumed")
        model.load_state_dict(state["model"])
        optimizer.load_state_dict(state["optimizer"])
        restore_rng(state["rng"])
        start, best_score, history, run_id = state["epoch"], state["best_score"], state["history"], state["run_id"]
    else:
        output.mkdir(parents=True, exist_ok=False)
    provenance = dict(schema_version=1, run_id=run_id, dataset_fingerprint=fingerprint, config=config,
        normalization=norm, independent_models=len({g["row"]["model_group"] for g in graphs}),
        assets_per_split={key: len(value) for key, value in grouped.items()},
        multi_reference_assets=[key for key, value in grouped["train"].items() if len(value) > 1],
        asset_ids_by_model_group={key: sorted({g["row"]["asset_id"] for g in variants})
                                 for value in grouped.values() for key, variants in value.items()},
        objective="Equal asset contribution; mean masked internal-edge BCE across each asset's references",
        metric_definition="3D lengths normalized per reference; equal references/assets; zero denominators return zero",
        scope="Seam edge labels only; no topology repair, UV solver, overlap or distortion validation")
    atomic(output / "provenance.json", provenance)

    def checkpoint(epoch, finished=False, final=None):
        return dict(schema_version=1, run_id=run_id, epoch=epoch, finished=finished, config=config,
            dataset_fingerprint=fingerprint, normalization=norm, model=model.state_dict(),
            optimizer=optimizer.state_dict(), rng=rng_state(device.type == "cuda"),
            best_score=best_score, history=history, final=final)

    stop = min(epochs, start + max_epochs) if max_epochs else epochs
    for epoch in range(start, stop):
        model.train()
        order = list(grouped["train"])
        random.shuffle(order)
        losses = []
        for asset in order:
            variants = list(grouped["train"][asset])
            random.shuffle(variants)
            optimizer.zero_grad(set_to_none=True)
            asset_loss = 0.
            for graph in variants:
                x = torch.as_tensor((graph["x"] - norm["mean"]) / norm["scale"], dtype=torch.float32, device=device)
                neighbors = torch.as_tensor(graph["neighbors"], dtype=torch.long, device=device)
                selected = torch.as_tensor(graph["mask"], dtype=torch.bool, device=device)
                y = torch.as_tensor(graph["y"], dtype=torch.float32, device=device)
                loss = torch.nn.functional.binary_cross_entropy_with_logits(model(x, neighbors)[selected], y[selected])
                if not bool(torch.isfinite(loss)):
                    raise ValueError("Non-finite training loss")
                (loss / len(variants)).backward()
                asset_loss += float(loss.detach()) / len(variants)
            if any(not bool(torch.isfinite(p.grad).all()) for p in model.parameters()):
                raise ValueError("Non-finite training gradients")
            optimizer.step()
            losses.append(asset_loss)
        validation = predictions(model, grouped["val"], norm, device)
        candidates = [metrics(validation, t) for t in np.linspace(0., 1., 101)]
        score = max(candidates, key=lambda m: (m["macro_asset"]["f1"], -abs(m["threshold"] - .5)))
        improved = score["macro_asset"]["f1"] > best_score
        if improved:
            best_score = score["macro_asset"]["f1"]
        history.append(dict(epoch=epoch + 1, train_bce=float(np.mean(losses)), val=score))
        current = checkpoint(epoch + 1)
        if improved:
            current["val_prediction_sha256"] = prediction_hash(validation)
            atomic(output / "best.pt", current, checkpoint=True)
        atomic(output / "last.pt", current, checkpoint=True)
        atomic(output / "metrics.json", dict(completed=False, history=history, test_evaluated=False))
        if progress:
            print(f"Epoch {epoch + 1}/{epochs}: train BCE={history[-1]['train_bce']:.6f}, "
                  f"val macro F1={score['macro_asset']['f1']:.6f}, "
                  f"threshold={score['threshold']:.2f}, best={improved}", flush=True)
    if stop < epochs:
        return dict(completed=False, epochs_completed=stop, test_evaluated=False)
    best = torch.load(output / "best.pt", map_location=device, weights_only=True)
    if (best["run_id"] != run_id or best["config"] != config or best["dataset_fingerprint"] != fingerprint
            or best["normalization"] != norm):
        raise ValueError("Best checkpoint provenance mismatch")
    model.load_state_dict(best["model"])
    validation = predictions(model, grouped["val"], norm, device)
    if prediction_hash(validation) != best["val_prediction_sha256"]:
        raise ValueError("Reloaded best checkpoint did not reproduce validation predictions")
    final = dict(completed=True, history=history, best_epoch=best["epoch"],
                 threshold=best["history"][-1]["val"]["threshold"], test_evaluated=True,
                 held_out_edge_metrics_evaluated=True, uv_quality_evaluated=False)
    test = predictions(model, grouped["test"], norm, device)
    final["test"] = metrics(test, final["threshold"])
    final["test_prediction_sha256"] = prediction_hash(test)
    atomic(output / "last.pt", checkpoint(epochs, finished=True, final=final), checkpoint=True)
    atomic(output / "metrics.json", final)
    return final


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prepared", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--epochs", type=int, default=200)
    parser.add_argument("--lr", type=float, default=.001)
    parser.add_argument("--width", type=int, default=64)
    parser.add_argument("--blocks", type=int, default=4)
    parser.add_argument("--seed", type=int, default=73)
    parser.add_argument("--device", choices=("cpu", "cuda:0"), default="cuda:0")
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--max-epochs", type=int, help="Epochs in this invocation; total --epochs stays unchanged")
    args = parser.parse_args()
    result = train(args.prepared, args.out, args.epochs, args.lr, args.width, args.blocks,
                   args.seed, args.device, args.resume, args.max_epochs, progress=True)
    print(json.dumps({key: result[key] for key in ("completed", "test_evaluated")}))
