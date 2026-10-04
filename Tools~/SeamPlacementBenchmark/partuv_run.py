"""Run the official PartUV native pipeline on a prepared author hierarchy.

Designed for the Linux wheel. Loads the unmodified native extension directly;
the unused Python preprocessing wrapper need not reload Torch or the checkpoint.
Geometry/corner correspondence, chart coverage and UV density are checked before
writing an unpacked capture. Pack using the standalone probe's 'pack' mode.
"""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import struct
import time

import numpy as np

from seams import read_capture, topology


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", type=Path, required=True)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--threshold", type=float, default=1.25)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    package = importlib.util.find_spec("partuv")
    extension = next(Path(package.origin).parent.glob("_core*.so"))
    spec = importlib.util.spec_from_file_location("_core", extension)
    core = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(core)
    geometry = np.load(args.input / "geometry.npz", allow_pickle=False)
    p, faces = geometry["positions"], geometry["faces"]
    tree = {int(k): v for k, v in json.loads((args.input / "tree.json").read_text()).items()}
    args.out.mkdir(parents=True, exist_ok=True)
    started = time.perf_counter()
    final, parts = core.pipeline_numpy(p, faces.astype(np.int32), tree, str(args.config.resolve()), args.threshold, False)
    if not final.num_components:
        raise ValueError("Native pipeline returned no charts")
    lookup = {tuple(sorted(f)): i for i, f in enumerate(faces)}
    out_faces = np.full_like(faces, -1)
    out_p, out_uv, charts = [], [], []
    seen = set()
    diagonal = np.linalg.norm(np.ptp(p, axis=0))
    for chart, component in enumerate(final.components):
        v, f, uv = np.asarray(component.V), np.asarray(component.F), np.asarray(component.UV)
        if len(uv) != len(v) or uv.shape[1] != 2 or not np.isfinite(uv).all():
            raise ValueError("Invalid chart UVs")
        distances = np.linalg.norm(v[:, None, :] - p[None, :, :], axis=2)
        mapping = distances.argmin(axis=1)
        if distances[np.arange(len(v)), mapping].max() > diagonal * 1e-6:
            raise ValueError("Native pipeline moved the source surface")
        original = mapping[f]
        tri_p, tri_uv = p[original], uv[f]
        area3 = np.linalg.norm(np.cross(tri_p[:,1]-tri_p[:,0], tri_p[:,2]-tri_p[:,0]), axis=1).sum()/2
        a, b = tri_uv[:,1]-tri_uv[:,0], tri_uv[:,2]-tri_uv[:,0]
        area_uv = np.abs(a[:,0]*b[:,1]-a[:,1]*b[:,0]).sum()/2
        if area_uv <= 0:
            raise ValueError("Collapsed chart")
        uv = uv * np.sqrt(area3/area_uv)
        offset = len(out_p)
        out_p.extend(p[mapping])
        out_uv.extend(uv)
        charts.extend([chart]*len(v))
        for local, source in zip(f, original):
            face_index = lookup[tuple(sorted(source))]
            if face_index in seen:
                raise ValueError("Native pipeline duplicates a source face")
            seen.add(face_index)
            corner = {int(a): int(b) + offset for a, b in zip(source, local)}
            out_faces[face_index] = [corner[int(a)] for a in faces[face_index]]
    if len(seen) != len(faces) or (out_faces < 0).any():
        raise ValueError("Native pipeline lost source faces")
    output = args.out / "unpacked.bin"
    output.write_bytes(struct.pack("<3i", len(out_p), faces.size, int(final.num_components)) +
                       np.asarray(out_p, dtype="<f4").tobytes() + np.asarray(out_uv, dtype="<f4").tobytes() +
                       out_faces.astype("<i4").tobytes() + np.asarray(charts, dtype="<i4").tobytes())
    # UVParts may expose one combined component containing many disconnected
    # UV charts. Derive charts from actual 3D edge + UV continuity, not that count.
    _, _, _, pairs, cuts = topology(read_capture(output))
    parent = np.arange(len(faces))
    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i
    for a, b in pairs[~cuts]:
        parent[root(a)] = root(b)
    _, face_labels = np.unique([root(i) for i in range(len(faces))], return_inverse=True)
    positions, texture = np.asarray(out_p), np.asarray(out_uv)
    slots, out_p, out_uv, charts = {}, [], [], []
    for face_index, (triangle, chart) in enumerate(zip(out_faces.copy(), face_labels)):
        for corner, vertex in enumerate(triangle):
            key = int(vertex), int(chart)
            if key not in slots:
                slots[key] = len(out_p)
                out_p.append(positions[vertex])
                out_uv.append(texture[vertex])
                charts.append(int(chart))
            out_faces[face_index, corner] = slots[key]
    chart_count = int(face_labels.max()+1)
    output.write_bytes(struct.pack("<3i", len(out_p), faces.size, chart_count) +
                       np.asarray(out_p, dtype="<f4").tobytes() + np.asarray(out_uv, dtype="<f4").tobytes() +
                       out_faces.astype("<i4").tobytes() + np.asarray(charts, dtype="<i4").tobytes())
    (args.out / "charts.dat").write_bytes(face_labels.astype("<u4").tobytes())
    report = dict(charts=chart_count, nativeComponents=int(final.num_components), parts=len(parts), nativeDistortion=float(final.distortion),
                  threshold=args.threshold, seconds=time.perf_counter()-started,
                  configSha256=hashlib.sha256(args.config.read_bytes()).hexdigest(),
                  coreSha256=hashlib.sha256(extension.read_bytes()).hexdigest())
    (args.out / "report.json").write_text(json.dumps(report, indent=2))
    print(report, flush=True)


if __name__ == "__main__":
    main()
