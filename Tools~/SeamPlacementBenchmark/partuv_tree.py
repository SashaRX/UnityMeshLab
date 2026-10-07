"""Run PartUV's own hierarchy builder on frozen official PartField features."""
import argparse
import json
from pathlib import Path
import sys

import numpy as np

from seams import read_capture, topology


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--partuv", type=Path, required=True)
    parser.add_argument("--features", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(args.partuv.resolve()))
    import trimesh
    from preprocess_utils.partfield_official.AgglomerativeClustering import solve_clustering_mesh
    positions, faces, _, _, _ = topology(read_capture(args.reference))
    features = np.load(args.features, allow_pickle=False)
    if features.shape != (len(faces), 448) or not np.isfinite(features).all():
        raise ValueError("Invalid feature correspondence")
    mesh = trimesh.Trimesh(vertices=positions, faces=faces, process=False)
    tree, root = solve_clustering_mesh(mesh, features, None, sample_on_faces=True, pca_dim=None)
    tree = {int(key): {"left": int(value["left"]), "right": int(value["right"])} for key, value in tree.items()}
    args.out.mkdir(parents=True, exist_ok=True)
    np.savez(args.out / "geometry.npz", positions=positions, faces=faces)
    (args.out / "tree.json").write_text(json.dumps(tree))
    print(len(tree), "hierarchy nodes; root", root)


if __name__ == "__main__":
    main()
