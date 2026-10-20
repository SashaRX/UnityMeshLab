"""Optional chart-local SLIM control on private JSON captures; no production or packing changes.

Input: {positions: [{x,y,z}], uv: [{x,y}], triangles: [indices]}.
Each indexed edge-connected patch is solved independently. Point-only junctions
are therefore separated: this is not an authored-seam-preserving full atlas.
Requires the same optional libigl environment as SeamPlacementBenchmark.
"""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import sys
import time

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "SeamPlacementBenchmark"))
from fixed_cut_slim import DiskSlim, valid_uv


def patches(faces):
    """Partition by indexed full edges, with deterministic traversal."""
    edges, adjacent = defaultdict(list), defaultdict(set)
    for face, triangle in enumerate(faces):
        for corner in range(3):
            edges[tuple(sorted((triangle[corner], triangle[(corner + 1) % 3])))].append(face)
    for uses in edges.values():
        for face in uses:
            adjacent[face].update(uses)
    unseen = set(range(len(faces)))
    while unseen:
        first = min(unseen)
        unseen.remove(first)
        selected, stack = [first], [first]
        while stack:
            for face in sorted(adjacent[stack.pop()] & unseen):
                unseen.remove(face)
                selected.append(face)
                stack.append(face)
        yield np.array(sorted(selected))


def worst_stretch(positions, faces, uv):
    """Measure intrinsic singular-value anisotropy in double precision."""
    edge = positions[faces[:, 1]] - positions[faces[:, 0]]
    other = positions[faces[:, 2]] - positions[faces[:, 0]]
    length = np.linalg.norm(edge, axis=1)
    x = np.einsum("ij,ij->i", edge, other) / length
    y = np.linalg.norm(np.cross(edge, other), axis=1) / length
    a, b = uv[faces[:, 1]] - uv[faces[:, 0]], uv[faces[:, 2]] - uv[faces[:, 0]]
    jacobian = np.stack((a / length[:, None], (b - a * (x / length)[:, None]) / y[:, None]), axis=2)
    singular = np.linalg.svd(jacobian, compute_uv=False)
    if np.any(singular[:, 1] == 0):
        return None
    value = float(np.max(singular[:, 0] / singular[:, 1]))
    return value if np.isfinite(value) else None


def probe(path, destination):
    """Save before/after chart buffers and explicit refusals; never claim atlas acceptance."""
    raw = path.read_bytes()
    data = json.loads(raw)
    positions = np.array([[v[k] for k in ("x", "y", "z")] for v in data["positions"]], dtype=np.float64)
    uv = np.array([[v[k] for k in ("x", "y")] for v in data["uv"]], dtype=np.float64)
    faces = np.array(data["triangles"], dtype=np.int64).reshape(-1, 3)
    if not np.isfinite(positions).all() or not np.isfinite(uv).all():
        raise ValueError("Nonfinite input")
    if len(uv) != len(positions) or faces.min() < 0 or faces.max() >= len(positions):
        raise ValueError("Invalid input topology")
    destination.mkdir(parents=True, exist_ok=False)
    rows = []
    for index, selected in enumerate(patches(faces)):
        vertices, inverse = np.unique(faces[selected], return_inverse=True)
        local = inverse.reshape(-1, 3)
        points, original = positions[vertices], uv[vertices]
        row = dict(patch=index, faces=len(selected))
        cross = np.linalg.norm(np.cross(points[local[:, 1]] - points[local[:, 0]],
                                        points[local[:, 2]] - points[local[:, 0]]), axis=1)
        if np.any(cross == 0):
            rows.append(row | dict(status="geometry-degenerate"))
            continue
        try:
            DiskSlim.validate_disk(points, local)
        except ValueError as error:
            rows.append(row | dict(status="refused", error=str(error)))
            continue
        row["before"] = worst_stretch(points, local, original)
        if row["before"] is not None and row["before"] <= 4:
            rows.append(row | dict(status="below-probe-threshold"))
            continue
        start = time.perf_counter()
        try:
            candidate, _, _, info = DiskSlim.parameterize_chart(points, local, np.arange(len(local)))
            rounded = candidate.astype(np.float32).astype(np.float64)
            row.update(status="candidate", after=worst_stretch(points, local, candidate),
                       float32=worst_stretch(points, local, rounded), validFloat32=valid_uv(rounded, local), info=info)
            np.savez_compressed(destination / f"patch-{index}.npz", positions=points, faces=local,
                                sourceFaces=selected, original=original, candidate=candidate, rounded=rounded)
        except (ValueError, RuntimeError) as error:
            row.update(status="refused", error=str(error))
        row["seconds"] = time.perf_counter() - start
        rows.append(row)
    report = dict(input=path.name, sha256=hashlib.sha256(raw).hexdigest(),
                  scope="independent edge patches; no packing, transfer or global atlas certification", patches=rows)
    (destination / "report.json").write_text(json.dumps(report, indent=2, allow_nan=False), encoding="utf-8")
    return report


def main():
    """Run the optional chart probe into a fresh directory with retained buffers."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    report = probe(args.capture, args.out)
    print(json.dumps({"input": report["input"], "patches": len(report["patches"])}))


if __name__ == "__main__":
    main()
