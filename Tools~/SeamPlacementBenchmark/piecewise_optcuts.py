"""Research: mandatory geometry regions + separate OptCuts solves + UV packing.

External OptCuts executable required; no optimization code is redistributed.
Unlike global minimum seam length, region boundaries cannot disappear. A failed
region aborts the result; there is no planar fallback disguising solver failure.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import time

import numpy as np

from import_obj import read_obj
from seams import align, read_capture, topology


def split_boundary_fans(positions, faces):
    """A connected region may touch itself at a vertex across a removed part.

    Split only disconnected incident face fans, keeping each source corner and
    every shared edge inside the region. This changes indexing, not the surface.
    """
    from collections import defaultdict
    parent = np.arange(faces.size)
    def root(index):
        while parent[index] != index:
            parent[index] = parent[parent[index]]
            index = parent[index]
        return index
    incidence = defaultdict(list)
    for face_index, triangle in enumerate(faces):
        for a, b in ((0, 1), (1, 2), (2, 0)):
            if triangle[a] > triangle[b]:
                a, b = b, a
            incidence[int(triangle[a]), int(triangle[b])].append((face_index*3+a, face_index*3+b))
    for neighbors in incidence.values():
        if len(neighbors) > 2:
            raise ValueError("Region has non-manifold edge")
        if len(neighbors) == 2:
            for a, b in zip(*neighbors):
                parent[root(a)] = root(b)
    representatives, slots = np.unique([root(i) for i in range(faces.size)], return_inverse=True)
    return positions[faces.ravel()[representatives]], slots.reshape(-1, 3)


def write_capture(path, positions, uv, faces, charts):
    path.write_bytes(struct.pack("<3i", len(positions), faces.size, int(charts.max() + 1)) +
                     positions.astype("<f4").tobytes() + uv.astype("<f4").tobytes() +
                     faces.astype("<i4").tobytes() + charts.astype("<i4").tobytes())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--labels", type=Path, required=True)
    parser.add_argument("--optcuts", type=Path, required=True)
    parser.add_argument("--dll-directory", type=Path)
    parser.add_argument("--packer", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--distortion", type=float, default=4.5, help="OptCuts symmetric Dirichlet bound, minimum 4")
    args = parser.parse_args()
    if not np.isfinite(args.distortion) or args.distortion <= 4:
        raise ValueError("Distortion bound must be finite and greater than 4")
    reference = topology(read_capture(args.reference))
    p, faces, _, _, _ = reference
    labels = np.load(args.labels, allow_pickle=False)
    if labels.shape != (len(faces),) or not np.issubdtype(labels.dtype, np.integer) or (labels < 0).any():
        raise ValueError("Invalid input labels")
    args.out = args.out.resolve()
    args.out.mkdir(parents=True, exist_ok=True)
    environment = os.environ.copy()
    if args.dll_directory:
        environment["PATH"] = str(args.dll_directory.resolve()) + os.pathsep + environment["PATH"]
    out_p, out_uv, out_chart = [], [], []
    out_faces = np.zeros_like(faces)
    report = []
    for region in np.unique(labels):
        selected = np.flatnonzero(labels == region)
        local_positions, local = split_boundary_fans(p, faces[selected])
        name = f"region_{int(region):03d}"
        obj = args.out / (name + ".obj")
        text = ["v " + " ".join(f"{x:.17g}" for x in point) for point in local_positions]
        text += ["f " + " ".join(str(int(x)+1) for x in triangle) for triangle in local]
        obj.write_text("\n".join(text) + "\n")
        output_dir = args.out / "output" / (name + "_Tutte_0.999_1_OptCuts_probe")
        if output_dir.exists():
            raise ValueError("Use a fresh output directory to avoid stale solver output")
        started = time.perf_counter()
        with (args.out / (name + ".log")).open("w") as log:
            result = subprocess.run([str(args.optcuts.resolve()), "100", obj.name, "0.999", "1", "0", str(args.distortion), "1", "1", "probe"],
                                    cwd=args.out, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=60)
        if result.returncode:
            raise RuntimeError(f"OptCuts region {region} failed; see {name}.log")
        vp, uv, vf, tf = read_obj(output_dir / "finalResult_mesh_normalizedUV.obj")
        source, target = vp[vf].reshape(-1, 3), p[faces[selected]].reshape(-1, 3)
        if source.shape != target.shape:
            raise ValueError("Region face count changed")
        sc, tc = source.mean(0), target.mean(0)
        scale = ((source-sc)*(target-tc)).sum() / ((source-sc)**2).sum()
        residual = np.linalg.norm((source-sc)*scale+tc-target, axis=1).max()
        if scale <= 0 or residual > np.linalg.norm(np.ptp(p, axis=0))*1e-5:
            raise ValueError("Region source corners changed")
        area3 = np.linalg.norm(np.cross(target.reshape(-1,3,3)[:,1]-target.reshape(-1,3,3)[:,0],
                                        target.reshape(-1,3,3)[:,2]-target.reshape(-1,3,3)[:,0]), axis=1).sum()/2
        tri_uv = uv[tf]
        delta, other = tri_uv[:,1]-tri_uv[:,0], tri_uv[:,2]-tri_uv[:,0]
        area_uv = np.abs(delta[:,0]*other[:,1]-delta[:,1]*other[:,0]).sum()/2
        if area_uv <= 0:
            raise ValueError("Region has no UV area")
        uv *= np.sqrt(area3/area_uv)
        slots = {}
        for face_index, v, t in zip(selected, vf, tf):
            for corner, (vertex, texture) in enumerate(zip(v, t)):
                key = int(vertex), int(texture)
                if key not in slots:
                    slots[key] = len(out_p)
                    out_p.append(p[faces[face_index, corner]])
                    out_uv.append(uv[texture])
                    out_chart.append(int(region))
                elif not np.array_equal(out_p[slots[key]], p[faces[face_index, corner]]):
                    raise ValueError("Inconsistent corner mapping")
                out_faces[face_index, corner] = slots[key]
        row = dict(region=int(region), faces=len(selected), maxCornerResidual=float(residual), seconds=time.perf_counter()-started)
        report.append(row)
        print(row, flush=True)
    unpacked = args.out / "unpacked.bin"
    write_capture(unpacked, np.asarray(out_p), np.asarray(out_uv), out_faces, np.asarray(out_chart))
    label_file = args.out / "regions.dat"
    label_file.write_bytes(labels.astype("<u4").tobytes())
    output = args.out / "piecewise-optcuts.bin"
    subprocess.run([str(args.packer.resolve()), str(unpacked), str(label_file), str(output), "pack", "1", "0.5"], check=True)
    align(read_capture(output), reference)
    (args.out / "report.json").write_text(json.dumps(dict(regions=report, distortionBound=args.distortion,
                                                        labelsSha256=hashlib.sha256(args.labels.read_bytes()).hexdigest()), indent=2))


if __name__ == "__main__":
    main()
