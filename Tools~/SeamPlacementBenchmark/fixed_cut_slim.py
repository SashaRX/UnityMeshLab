"""Authored-cut oracle: discard UV coordinates and solve verified disk charts.

This is a solver control, not automatic seam inference. Requires the separately
acquired AutoUV checkout for density normalization and rectangular packing, plus
libigl's SLIM implementation. Unsupported chart topology fails explicitly.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

import igl
import numpy as np
from scipy.sparse import coo_matrix, diags
from scipy.sparse.linalg import spsolve

from autouv_probe import authored_cut_control, save, verify_geometry
from seams import read_capture, topology
from render import metrics

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from analyze_uv_reference import overlaps


def valid_uv(uv, faces):
    if not np.isfinite(uv).all():
        return False
    triangles = uv[faces]
    a, b = triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]
    signed = a[:, 0] * b[:, 1] - a[:, 1] * b[:, 0]
    return bool(np.all(signed > 1e-14) and not overlaps(triangles, np.zeros(len(faces), dtype=int)))


def validate_atlas(path):
    capture = read_capture(path)
    if not np.isfinite(capture[1]).all():
        raise ValueError("Saved atlas contains nonfinite UV")
    _, _, quality, _ = metrics(path)
    if any(quality[key] for key in ("overlapPairs", "mixedWindingFaces", "zeroAreaFaces", "outOfBoundsVertices")):
        raise ValueError("Saved atlas failed complete float32 quality scan: " + json.dumps(quality))
    return quality


class DiskSlim:
    @staticmethod
    def parameterize_chart(vertices, faces, face_ids, arap_iters=0):
        faces = np.asarray(faces[face_ids], dtype=np.int64)
        edge_pairs = np.sort(np.concatenate((faces[:, [0, 1]], faces[:, [1, 2]],
                                            faces[:, [2, 0]])), axis=1)
        edges, counts = np.unique(edge_pairs, axis=0, return_counts=True)
        boundary = igl.boundary_loop(faces)
        if (len(vertices) - len(edges) + len(faces) != 1 or len(boundary) != np.sum(counts == 1)
                or np.any(counts > 2)):
            raise ValueError("SLIM control requires a manifold disk with one boundary loop")
        # Positive uniform weights + convex boundary establish an injective seed.
        # No authored UV values are used for coordinates or initialization.
        angle = np.arange(len(boundary)) * (2 * np.pi / len(boundary))
        uv = np.zeros((len(vertices), 2))
        uv[boundary] = np.column_stack((np.cos(angle), np.sin(angle)))
        row, col = np.concatenate((edges[:, 0], edges[:, 1])), np.concatenate((edges[:, 1], edges[:, 0]))
        adjacency = coo_matrix((np.ones(len(row)), (row, col)), shape=(len(vertices), len(vertices))).tocsr()
        laplacian = diags(np.asarray(adjacency.sum(axis=1)).ravel()) - adjacency
        internal = np.setdiff1d(np.arange(len(vertices)), boundary)
        if len(internal):
            uv[internal] = spsolve(laplacian[internal][:, internal].tocsc(),
                                   -laplacian[internal][:, boundary] @ uv[boundary])
        tri = uv[faces]
        a, b = tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]
        if np.all(a[:, 0] * b[:, 1] - a[:, 1] * b[:, 0] < 0):
            uv[:, 1] *= -1
        if not valid_uv(uv, faces):
            raise ValueError("Convex-boundary seed did not pass complete intersection scan")
        data = igl.slim_precompute(np.asarray(vertices, dtype=np.float64), faces.astype(np.int32), uv,
                                   igl.MappingEnergyType.SYMMETRIC_DIRICHLET,
                                   np.empty(0, dtype=np.int32), np.empty((0, 2)), 0.0)
        accepted, rejected = 0, 0
        best = uv.copy()
        for iteration in (10, 20, 30):
            candidate = igl.slim_solve(data, 10).copy()
            if valid_uv(candidate, faces):
                best, accepted = candidate, iteration
            else:
                rejected += 1
        info = dict(method="uniform disk seed + SLIM symmetric Dirichlet", acceptedIteration=accepted,
                    rejectedCheckpoints=rejected, fullIntersectionScan=True, chi=1,
                    boundaryEdges=len(boundary))
        return best, np.arange(len(vertices)), faces, info


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--autouv", type=Path, required=True)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--packer", type=Path, help="Optional standalone seam_region_probe executable: fixed-texel xatlas packing")
    args = parser.parse_args()
    sys.path.insert(0, str(args.autouv.resolve()))
    from autouv import postprocess, pack
    capture = read_capture(args.reference)
    output = authored_cut_control(capture, DiskSlim, postprocess, pack, arap_iters=0)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    save(args.out, *output[:4])
    packing = dict(method="AutoUV skyline", requestedResolution=512, requestedPadding=3,
                   limitation="Padding is scaled with the atlas; achieved padding is not fixed at 3 texels")
    if args.packer:
        before = args.out.with_suffix(".before-pack.bin")
        args.out.replace(before)
        labels = args.out.with_suffix(".charts.dat")
        labels.write_bytes(np.asarray(output[3], dtype="<u4").tobytes())
        packed = subprocess.run([str(args.packer.resolve()), str(before.resolve()), str(labels.resolve()),
                                 str(args.out.resolve()), "pack-square", "1", "0.5"],
                                check=True, capture_output=True, text=True)
        dimensions = json.loads(packed.stdout)
        packing = dict(method="standalone xatlas pack-only; uniform square normalization", requestedResolution=512, requestedPadding=3,
                       actualRaster=dimensions, normalizedRasterPaddingAt512=3 * 512 / max(dimensions["width"], dimensions["height"]),
                       executableSha256=hashlib.sha256(args.packer.read_bytes()).hexdigest())
    restored = read_capture(args.out)
    if not np.array_equal(verify_geometry(restored, topology(capture)), topology(capture)[4]):
        raise ValueError("Output changed authored cuts")
    report = dict(control="authored cuts; all UV coordinates discarded", geometryPreserved=True,
                  seamsPreserved=True, faceWindingPreserved=True, referenceSha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(),
                  captureSha256=hashlib.sha256(args.out.read_bytes()).hexdigest(), charts=output[4]["charts"],
                  packing=packing, finalAtlas=validate_atlas(args.out))
    args.out.with_suffix(".json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("control", "geometryPreserved", "seamsPreserved")}))


if __name__ == "__main__":
    main()
