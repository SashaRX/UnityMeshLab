"""Prepare local numerical seam data; no training, automatic splitting or asset edits."""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import sys

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "SeamPlacementBenchmark"))
from seams import read_capture


def extract(capture, tolerance=1e-7):
    positions, uv, original_faces, _, _ = capture
    positions, uv, original_faces = map(np.asarray, (positions, uv, original_faces))
    if (positions.ndim != 2 or positions.shape[1] != 3 or uv.shape != (len(positions), 2)
            or original_faces.ndim != 2 or original_faces.shape[1] != 3
            or not len(positions) or not len(original_faces)
            or not np.issubdtype(original_faces.dtype, np.integer)):
        raise ValueError("Invalid or empty geometry arrays")
    if not np.isfinite(positions).all() or not np.isfinite(uv).all():
        raise ValueError("Non-finite geometry")
    if original_faces.min() < 0 or original_faces.max() >= len(positions):
        raise ValueError("Invalid index")
    if not np.isfinite(tolerance) or tolerance < 0:
        raise ValueError("Invalid UV tolerance")
    positions, uv = positions.astype(float), uv.astype(float)
    positions, slots = np.unique(positions[original_faces.ravel()], axis=0, return_inverse=True)
    faces = slots.reshape(-1, 3)
    canonical = np.sort(faces, axis=1)
    canonical = canonical[np.lexsort(canonical.T[::-1])]
    if np.any(np.diff(canonical, axis=1) == 0) or len(np.unique(canonical, axis=0)) != len(faces):
        raise ValueError("Degenerate or duplicate geometric face")
    vectors = np.cross(positions[faces[:, 1]] - positions[faces[:, 0]],
                       positions[faces[:, 2]] - positions[faces[:, 0]])
    areas = np.linalg.norm(vectors, axis=1)
    if np.any(areas == 0):
        raise ValueError("Degenerate zero-area face")
    normals = vectors / areas[:, None]
    incidence, links = defaultdict(list), defaultdict(lambda: defaultdict(set))
    corner_uv = uv[original_faces]
    for face, triangle in enumerate(faces):
        for corner in range(3):
            a, b, c = map(int, np.roll(triangle, -corner))
            key = tuple(sorted((a, b)))
            endpoints = corner_uv[face, [corner, (corner + 1) % 3]]
            incidence[key].append((face, endpoints if a < b else endpoints[::-1], a < b))
            links[a][b].add(c)
            links[a][c].add(b)
    if any(len(sides) > 2 for sides in incidence.values()):
        raise ValueError("Nonmanifold edge")
    if any(len(sides) == 2 and sides[0][2] == sides[1][2] for sides in incidence.values()):
        raise ValueError("Inconsistent face winding")
    for link in links.values():
        seen, todo = set(), [next(iter(link))]
        while todo:
            vertex = todo.pop()
            if vertex not in seen:
                seen.add(vertex)
                todo.extend(link[vertex] - seen)
        degrees = [len(neighbors) for neighbors in link.values()]
        if len(seen) != len(link) or max(degrees) > 2 or degrees.count(1) not in (0, 2):
            raise ValueError("Nonmanifold vertex fan")
    edges = np.asarray(sorted(incidence), dtype=np.int64)
    pairs = np.full((len(edges), 2), -1, dtype=np.int64)
    seam = np.zeros(len(edges), dtype=bool)
    for edge, key in enumerate(map(tuple, edges)):
        sides = incidence[key]
        pairs[edge, :len(sides)] = [side[0] for side in sides]
        seam[edge] = len(sides) == 2 and np.max(np.abs(sides[0][1] - sides[1][1])) > tolerance
    boundary = pairs[:, 1] < 0
    face_edges = defaultdict(list)
    for edge, pair in enumerate(pairs):
        for face in pair[pair >= 0]:
            face_edges[int(face)].append(edge)
    neighbors = np.full((len(edges), 4), -1, dtype=np.int64)
    for edge, pair in enumerate(pairs):
        adjacent = sorted({other for face in pair[pair >= 0] for other in face_edges[int(face)]} - {edge})
        neighbors[edge, :len(adjacent)] = adjacent
    diagonal = np.linalg.norm(np.ptp(positions, axis=0))
    lengths = np.linalg.norm(positions[edges[:, 1]] - positions[edges[:, 0]], axis=1)
    angle = np.zeros(len(edges))
    internal = ~boundary
    angle[internal] = np.arccos(np.clip(np.einsum("ij,ij->i", normals[pairs[internal, 0]],
                                               normals[pairs[internal, 1]]), -1, 1))
    geometry_hash = hashlib.sha256(positions.astype("<f4").tobytes() + canonical.astype("<i8").tobytes()).hexdigest()
    arrays = dict(positions=positions.astype(np.float32), faces=faces, edges=edges, face_pairs=pairs,
                  edge_neighbors=neighbors, edge_features=np.column_stack((lengths / diagonal, angle)).astype(np.float32),
                  seam_labels=seam, learned_mask=internal, forced_boundary=boundary)
    return arrays, geometry_hash


def prepare(manifest, output):
    manifest, output = Path(manifest), Path(output)
    if output.exists() and (not output.is_dir() or any(output.iterdir())):
        raise ValueError("Output must be a new or empty directory; existing inputs/results are never overwritten")
    entries = json.loads(manifest.read_text(encoding="utf-8"))
    if not isinstance(entries, list) or not entries:
        raise ValueError("Manifest must be a nonempty list")
    records, arrays, parents = [], [], {}

    def find(key):
        parents.setdefault(key, key)
        if parents[key] != key:
            parents[key] = find(parents[key])
        return parents[key]

    def union(a, b):
        parents[find(a)] = find(b)

    for number, entry in enumerate(entries):
        if not isinstance(entry, dict) or any(not isinstance(entry.get(key), str) or not entry[key].strip()
               for key in ("asset_id", "family_id", "capture")):
            raise ValueError("Each entry needs asset_id, family_id and capture strings")
        split = entry.get("split")
        if split not in (None, "train", "val", "test"):
            raise ValueError("Split must be train, val, test, or omitted")
        capture = (manifest.parent / entry["capture"]).resolve()
        data, geometry = extract(read_capture(capture))
        asset = ("asset", entry["asset_id"])
        union(asset, ("geometry", geometry))
        arrays.append(data)
        records.append(dict(entry=number, asset_id=entry["asset_id"], family_id=entry["family_id"],
                            split=split, capture=str(capture), capture_sha256=hashlib.sha256(capture.read_bytes()).hexdigest(),
                            geometry_sha256=geometry, vertices=len(data["positions"]), faces=len(data["faces"]),
                            edges=len(data["edges"]), internal_cuts=int(data["seam_labels"].sum()),
                            forced_boundaries=int(data["forced_boundary"].sum())))
    model_groups = {find(("asset", record["asset_id"])) for record in records}
    model_ids = {key: number for number, key in enumerate(sorted(model_groups))}
    for record in records:
        record["model_group"] = model_ids[find(("asset", record["asset_id"]))]
    for record in records:
        union(("asset", record["asset_id"]), ("family", record["family_id"]))
    group_splits = defaultdict(set)
    for record in records:
        if record["split"]:
            group_splits[find(("asset", record["asset_id"]))].add(record["split"])
    if any(len(splits) > 1 for splits in group_splits.values()):
        raise ValueError("Split leakage: asset, exact geometry or family crosses splits")
    for record in records:
        group = find(("asset", record["asset_id"]))
        record["split"] = next(iter(group_splits[group]), "unassigned")
    report = dict(schema_version=1, captures=len(records), independent_models=len(model_groups),
                  split_groups=len({find(("asset", r["asset_id"])) for r in records}),
                  feature_names=["length_over_diagonal", "dihedral_radians"],
                  label_definition="UV endpoint discontinuity > 1e-7; internal edges only; chart IDs/normals/SG ignored",
                  manifest_sha256=hashlib.sha256(manifest.read_bytes()).hexdigest(), records=records,
                  trained=False, generalization_evaluated=False)
    output.mkdir(parents=True, exist_ok=True)
    for record, data in zip(records, arrays):
        filename = f"capture-{record['entry']:04d}.npz"
        np.savez_compressed(output / filename, **data)
        record["npz"] = filename
        record["npz_sha256"] = hashlib.sha256((output / filename).read_bytes()).hexdigest()
    (output / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--out", required=True)
    arguments = parser.parse_args()
    report = prepare(arguments.manifest, arguments.out)
    print(json.dumps({key: report[key] for key in ("captures", "independent_models", "split_groups", "trained")}))
