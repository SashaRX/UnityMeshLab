"""Ephemeral AdamW step timing; not a training-quality experiment or trainer."""
import argparse
import hashlib
import json
from pathlib import Path
import time

import numpy as np

from model_probe import SeamGraph, torch


def load_graph(path):
    with np.load(path, allow_pickle=False) as data:
        x, neighbors = data["edge_features"], data["edge_neighbors"]
        labels, mask, boundary = data["seam_labels"], data["learned_mask"], data["forced_boundary"]
    n = len(x)
    if (x.ndim != 2 or not n or not np.isfinite(x).all() or neighbors.shape != (n, 4)
            or not np.issubdtype(neighbors.dtype, np.integer) or neighbors.min() < -1 or neighbors.max() >= n
            or any(a.shape != (n,) or a.dtype != np.bool_ for a in (labels, mask, boundary))
            or not mask.any() or not np.array_equal(mask, ~boundary) or labels[boundary].any()):
        raise ValueError("Invalid graph/labels: only internal edges may enter learned loss")
    return x, neighbors, labels, mask


def timing(path, output, warmup=10, steps=50, device="cuda:0"):
    if torch is None:
        raise RuntimeError("Optional PyTorch unavailable; this helper installs nothing")
    if warmup < 1 or steps < 1:
        raise ValueError("Warmup and measured steps must both be positive")
    path, output = Path(path), Path(output)
    if output.exists():
        raise ValueError("Output already exists; choose a fresh JSON path")
    features, neighbors, labels, mask = load_graph(path)
    device = torch.device(device)
    torch.manual_seed(73)
    model = SeamGraph(features=features.shape[1]).to(device=device, dtype=torch.float32)
    x = torch.as_tensor(features, dtype=torch.float32, device=device)
    graph = torch.as_tensor(neighbors, dtype=torch.long, device=device)
    y = torch.as_tensor(labels, dtype=torch.float32, device=device)
    selected = torch.as_tensor(mask, dtype=torch.bool, device=device)
    optimizer = torch.optim.AdamW(model.parameters(), lr=0.001, weight_decay=0.01, foreach=False, fused=False)

    def step():
        optimizer.zero_grad(set_to_none=True)
        logits = model(x, graph)
        loss = torch.nn.functional.binary_cross_entropy_with_logits(logits[selected], y[selected])
        loss.backward()
        optimizer.step()
        return loss

    for _ in range(warmup):
        loss = step()
    baseline = None
    if device.type == "cuda":
        torch.cuda.synchronize(device)
        torch.cuda.empty_cache()
        baseline = torch.cuda.memory_allocated(device)
        torch.cuda.reset_peak_memory_stats(device)
    wall_ms, gpu_ms = [], []
    for _ in range(steps):
        start = time.perf_counter()
        if device.type == "cuda":
            begin, end = torch.cuda.Event(enable_timing=True), torch.cuda.Event(enable_timing=True)
            begin.record()
        loss = step()
        if device.type == "cuda":
            end.record()
            torch.cuda.synchronize(device)
            gpu_ms.append(begin.elapsed_time(end))
        wall_ms.append((time.perf_counter() - start) * 1000)
    if not bool(torch.isfinite(loss)) or any(not bool(torch.isfinite(p).all()) for p in model.parameters()):
        raise ValueError("Non-finite ephemeral loss or parameters")
    memory, gpu = None, None
    if device.type == "cuda":
        properties = torch.cuda.get_device_properties(device)
        gpu = dict(index=device.index or 0, name=properties.name, capability=list(torch.cuda.get_device_capability(device)),
                   total_memory_bytes=properties.total_memory)
        memory = dict(baseline_allocated_bytes=baseline, peak_allocated_bytes=torch.cuda.max_memory_allocated(device),
                      peak_reserved_bytes=torch.cuda.max_memory_reserved(device))

    def summary(samples):
        return dict(median_ms=float(np.median(samples)), p90_ms=float(np.percentile(samples, 90))) if samples else None

    report = dict(schema_version=1, input_npz=str(path.resolve()), input_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                  helper_source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  model_source_sha256=hashlib.sha256(Path(__file__).with_name("model_probe.py").read_bytes()).hexdigest(),
                  torch_version=torch.__version__, cuda_version=torch.version.cuda, device=str(device), gpu=gpu,
                  parameters=sum(p.numel() for p in model.parameters()), batch_size=1, edges=len(features),
                  learned_edges=int(mask.sum()), positive_labels=int(labels[mask].sum()), input_features=features.shape[1],
                  precision="float32", optimizer="AdamW", learning_rate=0.001, weight_decay=0.01,
                  foreach=False, fused=False, warmup_steps=warmup, measured_steps=steps,
                  ephemeral_parameter_updates=warmup + steps, wall_step=summary(wall_ms), cuda_event_step=summary(gpu_ms),
                  memory=memory, checkpoints_saved=False, quality_evaluated=False,
                  excluded=["data loading", "feature preprocessing", "topology decoder", "parameterization solver",
                            "full validation", "rendering", "checkpoint IO"],
                  memory_scope="PyTorch tensors including gradients/AdamW states; CUDA context and other applications excluded")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--warmup", type=int, default=10)
    parser.add_argument("--steps", type=int, default=50)
    parser.add_argument("--device", choices=("cpu", "cuda:0"), default="cuda:0")
    args = parser.parse_args()
    print(json.dumps(timing(args.input, args.out, args.warmup, args.steps, args.device), indent=2))
