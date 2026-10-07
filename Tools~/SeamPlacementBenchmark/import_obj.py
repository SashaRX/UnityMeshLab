"""Convert third-party UV output, verifying unchanged source surface corners.

Only a positive uniform scale/translation may differ in the exported positions.
The correspondence is checked on every ordered triangle corner before restoring
the exact source float positions; connectivity checks then run in compare.py.
"""
import argparse
import json
from pathlib import Path
import struct

import numpy as np

from seams import read_capture


def read_obj(path):
    p, uv, faces, textures = [], [], [], []
    for line in Path(path).read_text().splitlines():
        parts = line.split()
        if not parts:
            continue
        if parts[0] == "v":
            p.append([float(x) for x in parts[1:4]])
        elif parts[0] == "vt":
            uv.append([float(x) for x in parts[1:3]])
        elif parts[0] == "f":
            if len(parts) != 4:
                raise ValueError("Expected triangle OBJ output")
            v, t = [], []
            for corner in parts[1:]:
                ids = corner.split("/")
                if len(ids) < 2 or not ids[1]:
                    raise ValueError("Missing output UVs")
                v.append(int(ids[0]) - 1)
                t.append(int(ids[1]) - 1)
            faces.append(v)
            textures.append(t)
    return np.asarray(p), np.asarray(uv), np.asarray(faces), np.asarray(textures)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("obj", type=Path)
    parser.add_argument("reference", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    p, uv, faces, textures = read_obj(args.obj)
    rp, _, rf, _, _ = read_capture(args.reference)
    if faces.shape != rf.shape:
        raise ValueError("Surface face count changed")
    source, target = p[faces].reshape(-1, 3), rp[rf].reshape(-1, 3)
    sc, tc = source.mean(0), target.mean(0)
    scale = ((source - sc) * (target - tc)).sum() / ((source - sc) ** 2).sum()
    transformed = (source - sc) * scale + tc
    residual = np.linalg.norm(transformed - target, axis=1).max()
    diagonal = np.linalg.norm(np.ptp(rp, axis=0))
    if scale <= 0 or residual > diagonal * 1e-5:
        raise ValueError(f"Ordered source corners changed: residual {residual}")
    # Split by actual output texture vertex identity. Triangles keep exact source
    # corners, and native output may preserve cuts within a single chart.
    slots, out_p, out_uv, out_faces = {}, [], [], []
    for face, tex, original in zip(faces, textures, rf):
        indices = []
        for vertex, texture, source_index in zip(face, tex, original):
            key = (int(vertex), int(texture))
            if key not in slots:
                slots[key] = len(out_p)
                out_p.append(rp[source_index])
                out_uv.append(uv[texture])
            elif not np.array_equal(out_p[slots[key]], rp[source_index]):
                raise ValueError("Output vertex maps to different source positions")
            indices.append(slots[key])
        out_faces.append(indices)
    out_p, out_uv, out_faces = np.asarray(out_p), np.asarray(out_uv), np.asarray(out_faces)
    # Find charts through actual welded UV continuity, including duplicate OBJ vt.
    from collections import defaultdict
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    _, welded = np.unique(out_p, axis=0, return_inverse=True)
    edges = defaultdict(list)
    for face, corners in enumerate(out_faces):
        for a, b in zip(corners, np.roll(corners, -1)):
            if welded[a] > welded[b]:
                a, b = b, a
            edges[(int(welded[a]), int(welded[b]))].append((face, out_uv[[a, b]]))
    joins = [(v[0][0], v[1][0]) for v in edges.values() if len(v) == 2 and np.max(np.abs(v[0][1] - v[1][1])) < 1e-7]
    a, b = np.asarray(joins, dtype=int).reshape(-1, 2).T
    graph = coo_matrix((np.ones(len(a) * 2), (np.r_[a, b], np.r_[b, a])), shape=(len(out_faces), len(out_faces))).tocsr()
    count, face_charts = connected_components(graph, directed=False)
    charts = np.full(len(out_p), -1, dtype=np.int32)
    for face, chart in zip(out_faces, face_charts):
        if any(charts[face] >= 0) and any((charts[face] >= 0) & (charts[face] != chart)):
            raise ValueError("Output reuses a UV vertex across disconnected charts")
        charts[face] = chart
    with args.output.open("wb") as stream:
        stream.write(struct.pack("<3i", len(out_p), out_faces.size, count))
        stream.write(out_p.astype("<f4").tobytes())
        stream.write(out_uv.astype("<f4").tobytes())
        stream.write(out_faces.astype("<i4").tobytes())
        stream.write(charts.astype("<i4").tobytes())
    report = dict(scale=float(scale), maxCornerResidual=float(residual), faces=len(out_faces), charts=int(count))
    args.output.with_suffix(".json").write_text(json.dumps(report, indent=2))
    print(report)


if __name__ == "__main__":
    main()
