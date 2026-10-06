"""Research-only PartField feature probe with graph-connected region proposals.

Requires the separately acquired PartUV source, official PartField checkpoint
and their dependencies/licenses. This is NOT the full PartUV UV pipeline and
does not redistribute any third-party code or weights. No training/fine tuning.
"""
import argparse
import hashlib
import json
from pathlib import Path
import sys
import time

import numpy as np
from scipy.sparse import coo_matrix
from sklearn.cluster import AgglomerativeClustering

from seams import read_capture, topology


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--partuv", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--device", choices=("cuda", "cpu"), default="cuda")
    args = parser.parse_args()
    sys.path.insert(0, str(args.partuv.resolve()))
    import torch
    import trimesh
    from yacs.config import CfgNode
    from preprocess_utils.partfield_official.partfield.config import default_argument_parser, setup
    from preprocess_utils.partfield_official.partfield.model_trainer_pvcnn_only_demo import Model
    args.out.mkdir(parents=True, exist_ok=True)
    configuration = default_argument_parser().parse_args([])
    configuration.config_file = str(args.partuv / "preprocess_utils/partfield_official/configs/final/demo.yaml")
    cfg = setup(configuration)
    started = time.perf_counter()
    # The checkpoint's globals were inspected with pickletools: tensors,
    # OrderedDict, CfgNode and set only. Keep restricted loading enabled.
    with torch.serialization.safe_globals([CfgNode, set]):
        checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    model = Model(cfg, device=args.device)
    model.load_state_dict(checkpoint["state_dict"], strict=True)
    del checkpoint
    model.to(args.device).eval()
    print("Loaded strict pretrained weights", time.perf_counter() - started, flush=True)
    p, faces, edges, pairs, _ = topology(read_capture(args.reference))
    mesh = trimesh.Trimesh(vertices=p.copy(), faces=faces.copy(), process=False)
    with torch.inference_mode():
        features, returned_mesh, bridges = model.run_inference(None, mesh=mesh, device=args.device,
            sample_on_faces=10, sample_batch_size=256, seed=42)
    if bridges or not np.array_equal(returned_mesh.faces, faces):
        raise ValueError("Inference changed input topology")
    features = features.detach().cpu().float().numpy()
    if features.shape != (len(faces), 448) or not np.isfinite(features).all():
        raise ValueError("Invalid pretrained features")
    np.save(args.out / "partfield-features.npy", features)
    features /= np.maximum(np.linalg.norm(features, axis=1)[:, None], 1e-12)
    a, b = pairs.T
    graph = coo_matrix((np.ones(len(a) * 2), (np.r_[a, b], np.r_[b, a])), shape=(len(faces), len(faces))).tocsr()
    cuts = {}
    for count in (3, 5, 8, 12, 20):
        labels = AgglomerativeClustering(count, linkage="ward", connectivity=graph).fit_predict(features)
        name = f"partfield-{count}"
        cuts[name] = labels[pairs[:, 0]] != labels[pairs[:, 1]]
        np.save(args.out / (name + "-labels.npy"), labels)
        print(name, int(cuts[name].sum()), "boundary edges", flush=True)
    np.savez(args.out / "partfield-prototypes.npz", **cuts)
    report = dict(checkpointSha256=hashlib.sha256(args.checkpoint.read_bytes()).hexdigest(),
                  device=args.device, faceSamples=10, faceBatch=256, seed=42,
                  inferenceAndClusteringSeconds=time.perf_counter() - started,
                  implementation="PartUV bundled PartField; own connected Ward clustering; no UV solver")
    (args.out / "partfield.json").write_text(json.dumps(report, indent=2))
    print(report, flush=True)


if __name__ == "__main__":
    main()
