"""Untrained graph capacity/memory probe. Random logits are not seam predictions."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

try:
    import torch
    from torch import nn
except ModuleNotFoundError:
    torch = None


if torch is not None:
    class SeamGraph(nn.Module):
        def __init__(self, features=2, width=64, blocks=4):
            super().__init__()
            self.input = nn.Linear(features, width)
            self.blocks = nn.ModuleList([
                nn.Sequential(nn.Linear(width * 3, width), nn.ReLU(), nn.Linear(width, width))
                for _ in range(blocks)])
            self.norms = nn.ModuleList([nn.LayerNorm(width) for _ in range(blocks)])
            self.head = nn.Sequential(nn.Linear(width * 3, width), nn.ReLU(), nn.Linear(width, 1))

        def forward(self, features, neighbors):
            hidden = torch.relu(self.input(features))
            valid = neighbors >= 0
            for block, norm in zip(self.blocks, self.norms):
                gathered = hidden[neighbors.clamp_min(0)]
                mean = (gathered * valid[..., None]).sum(1) / valid.sum(1).clamp_min(1)[:, None]
                maximum = gathered.masked_fill(~valid[..., None], torch.finfo(hidden.dtype).min).amax(1)
                maximum = torch.where(valid.any(1)[:, None], maximum, torch.zeros_like(maximum))
                hidden = norm(hidden + block(torch.cat((hidden, mean, maximum), dim=1)))
            mean = hidden.mean(0).expand_as(hidden)
            maximum = hidden.amax(0).expand_as(hidden)
            return self.head(torch.cat((hidden, mean, maximum), dim=1)).squeeze(-1)
else:
    SeamGraph = None


def probe(path, output, device="auto"):
    if torch is None:
        raise RuntimeError("Optional PyTorch is unavailable; no packages or weights are installed by this probe")
    path, output = Path(path), Path(output)
    if output.exists():
        raise ValueError("Probe output already exists; choose a fresh JSON path")
    with np.load(path, allow_pickle=False) as data:
        features, neighbors = data["edge_features"], data["edge_neighbors"]
    if (features.ndim != 2 or not len(features) or not np.isfinite(features).all()
            or neighbors.shape != (len(features), 4) or not np.issubdtype(neighbors.dtype, np.integer)
            or neighbors.min() < -1 or neighbors.max() >= len(features)):
        raise ValueError("Invalid prepared graph arrays")
    device = "cuda:0" if device == "auto" and torch.cuda.is_available() else "cpu" if device == "auto" else device
    device = torch.device(device)
    torch.manual_seed(73)
    model = SeamGraph(features=features.shape[1]).to(device=device, dtype=torch.float32)
    x = torch.as_tensor(features, dtype=torch.float32, device=device)
    graph = torch.as_tensor(neighbors, dtype=torch.long, device=device)
    parameters = sum(parameter.numel() for parameter in model.parameters())
    model_hash = hashlib.sha256()
    for name, value in model.state_dict().items():
        model_hash.update(name.encode())
        model_hash.update(value.detach().cpu().numpy().astype("<f4").tobytes())
    # Initialize kernels; gradients are discarded and parameters never updated.
    model(x, graph).square().mean().backward()
    model.zero_grad(set_to_none=True)
    baseline = None
    if device.type == "cuda":
        torch.cuda.synchronize(device)
        torch.cuda.empty_cache()
        baseline = torch.cuda.memory_allocated(device)
        torch.cuda.reset_peak_memory_stats(device)
    logits = model(x, graph)
    logits.square().mean().backward()
    finite = bool(torch.isfinite(logits).all()) and all(
        parameter.grad is not None and bool(torch.isfinite(parameter.grad).all()) for parameter in model.parameters())
    memory, gpu = None, None
    if device.type == "cuda":
        torch.cuda.synchronize(device)
        properties = torch.cuda.get_device_properties(device)
        gpu = dict(index=device.index or 0, name=properties.name, capability=list(torch.cuda.get_device_capability(device)),
                   total_memory_bytes=properties.total_memory)
        memory = dict(baseline_allocated_bytes=baseline, peak_allocated_bytes=torch.cuda.max_memory_allocated(device),
                      peak_reserved_bytes=torch.cuda.max_memory_reserved(device))
        memory["incremental_peak_bytes"] = memory["peak_allocated_bytes"] - baseline
    if not finite:
        raise ValueError("Non-finite logits or gradients; diagnostic probe failed")
    report = dict(schema_version=1, input_npz=str(path.resolve()), input_sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                  probe_source_sha256=hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                  random_state_sha256=model_hash.hexdigest(), torch_version=torch.__version__, cuda_version=torch.version.cuda,
                  device=str(device), gpu=gpu, memory=memory, batch_size=1, edges=len(features), input_features=features.shape[1],
                  width=64, residual_blocks=4, parameters=parameters, precision="float32", finite_logits_and_gradients=finite,
                  warmup_passes=1, measured_forward_backward_passes=1, parameter_updates=0, trained=False,
                  quality_evaluated=False, outputs_saved=False,
                  memory_scope="PyTorch allocated/reserved tensors after warmup; CUDA context/other applications excluded")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--device", choices=("auto", "cpu", "cuda:0"), default="auto")
    arguments = parser.parse_args()
    print(json.dumps(probe(arguments.input, arguments.out, arguments.device), indent=2))
