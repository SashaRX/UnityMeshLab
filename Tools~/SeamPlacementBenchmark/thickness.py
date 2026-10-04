"""Research prototype: ray thickness + unsupervised graph segmentation.

Inspired by shape-diameter segmentation, not an implementation of CGAL SDF.
No UV labels, author normals, trained edge classifier or artist masks are inputs.
The output is a region-boundary proposal, not a validated unwrap.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from sklearn.mixture import GaussianMixture

from relocate import expansion, split_components
from seams import read_capture, topology


def ray_thickness(positions, faces, rays=25):
    triangle = positions[faces]
    e1, e2 = triangle[:, 1] - triangle[:, 0], triangle[:, 2] - triangle[:, 0]
    cross = np.cross(e1, e2)
    normals = cross / np.linalg.norm(cross, axis=1)[:, None]
    volume = np.einsum("ij,ij->i", triangle[:, 0], np.cross(triangle[:, 1], triangle[:, 2])).sum() / 6
    if volume <= 0:
        raise ValueError("Thickness requires consistently outward faces")
    axis = -normals
    helper = np.eye(3)[np.abs(axis).argmin(axis=1)]
    tangent = np.cross(axis, helper)
    tangent /= np.linalg.norm(tangent, axis=1)[:, None]
    bitangent = np.cross(axis, tangent)
    cosine = 1 - 0.5 * (np.arange(rays) + 0.5) / rays
    sine = np.sqrt(1 - cosine ** 2)
    azimuth = np.arange(rays) * (np.pi * (3 - np.sqrt(5)))
    direction = (axis[:, None, :] * cosine[None, :, None] +
                 tangent[:, None, :] * (sine * np.cos(azimuth))[None, :, None] +
                 bitangent[:, None, :] * (sine * np.sin(azimuth))[None, :, None]).reshape(-1, 3)
    bias = np.linalg.norm(np.ptp(positions, axis=0)) * 1e-7
    origin = np.repeat(triangle.mean(axis=1) + axis * bias, rays, axis=0)
    hits = np.full(len(origin), np.nan)
    for start in range(0, len(origin), 128):
        d = direction[start:start + 128]
        relative = origin[start:start + 128, None, :] - triangle[None, :, 0, :]
        h = np.cross(d[:, None, :], e2[None, :, :])
        determinant = np.einsum("si,qsi->qs", e1, h)
        valid = determinant < -1e-14  # only intersections exiting the solid
        inverse = np.divide(1., determinant, out=np.zeros_like(determinant), where=valid)
        u = np.einsum("qsi,qsi->qs", relative, h) * inverse
        q = np.cross(relative, e1[None, :, :])
        v = np.einsum("qi,qsi->qs", d, q) * inverse
        distance = np.einsum("si,qsi->qs", e2, q) * inverse
        valid &= (u >= 0) & (v >= 0) & (u + v <= 1) & (distance > bias)
        nearest = np.where(valid, distance, np.inf).min(axis=1)
        hits[start:start + len(d)] = np.where(np.isfinite(nearest), nearest, np.nan)
    hits = hits.reshape(-1, rays)
    result = np.full(len(faces), np.nan)
    for i, row in enumerate(hits):
        finite = np.isfinite(row)
        if finite.any():
            selected = finite & (np.abs(row - np.nanmedian(row)) <= np.nanstd(row) + 1e-12)
            result[i] = np.average(row[selected], weights=cosine[selected])
    return result, int(np.isnan(hits).sum()), normals


def segment(positions, faces, pairs, edges, thickness, normals, clusters, smoothing):
    areas = np.linalg.norm(np.cross(positions[faces[:, 1]] - positions[faces[:, 0]],
                                    positions[faces[:, 2]] - positions[faces[:, 0]]), axis=1) / 2
    value = np.log(np.maximum(thickness, 1e-8))[:, None]
    model = GaussianMixture(clusters, covariance_type="full", random_state=591, n_init=5).fit(value)
    probabilities = model.predict_proba(value)
    unary = -np.log(np.maximum(probabilities, 1e-12)) * areas[:, None]
    a, b = pairs.T
    lengths = np.linalg.norm(positions[edges[:, 1]] - positions[edges[:, 0]], axis=1)
    angle = np.arccos(np.clip(np.einsum("ij,ij->i", normals[a], normals[b]), -1, 1))
    centers = positions[faces].mean(axis=1)
    concave = np.einsum("ij,ij->i", centers[b] - centers[a], normals[a]) > 0
    # Discontinuities across concave folds are less expensive, helping separate
    # connected shape parts rather than individual triangles of similar normals.
    feature = np.exp(-(angle / np.radians(45)) ** 2) * np.where(concave, 0.2, 1)
    weights = smoothing * lengths * (0.05 + 0.95 * feature)
    labels = probabilities.argmax(axis=1)
    history = []
    for _ in range(8):
        updated, energies = expansion(labels, unary, pairs, weights)
        history.append(dict(before=energies[0], after=energies[-1], moves=len(energies) - 1))
        if np.array_equal(labels, updated):
            break
        labels = updated
    return split_components(labels, pairs), history


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    p, faces, edges, pairs, _ = topology(read_capture(args.reference))
    args.out.mkdir(parents=True, exist_ok=True)
    values, misses, normals = ray_thickness(p, faces)
    if np.isnan(values).any():
        raise ValueError("No thickness estimate for some faces; repair input first")
    np.save(args.out / "thickness.npy", values)
    cuts, results = {}, []
    for clusters in (3, 5, 8):
        for smoothing in (0.01, 0.03, 0.1):
            name = f"thickness-{clusters}-{smoothing:g}"
            labels, history = segment(p, faces, pairs, edges, values, normals, clusters, smoothing)
            cuts[name] = labels[pairs[:, 0]] != labels[pairs[:, 1]]
            np.save(args.out / (name + "-labels.npy"), labels)
            row = dict(method=name, connectedRegions=int(labels.max() + 1), cutEdges=int(cuts[name].sum()), history=history)
            results.append(row)
            print(row, flush=True)
    np.savez(args.out / "thickness-prototypes.npz", **cuts)
    (args.out / "thickness.json").write_text(json.dumps(dict(rays=25, coneFullDegrees=120,
        raysMissed=misses, faces=len(faces), results=results), indent=2))


if __name__ == "__main__":
    main()
