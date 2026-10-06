"""Run separately acquired AutoUV and an authored-cut control on exact geometry.

The control uses authored UV only to establish connectivity. It regenerates all
coordinates; it is not an automatic seam-placement result. No private geometry
or third-party code is copied into the package.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

import numpy as np

from seams import align, read_capture, topology


def verify_geometry(capture, reference):
    cut = align(capture, reference)
    faces = topology(capture)[1]
    def oriented(triangles):
        return sorted(tuple(np.roll(face, -int(np.argmin(face)))) for face in triangles)
    if oriented(faces) != oriented(reference[1]):
        raise ValueError("Output changed source face winding")
    return cut


def save(path, positions, uv, faces, face_charts):
    vertex_charts = np.full(len(positions), -1, dtype=np.int32)
    for face, chart in zip(faces, face_charts):
        assigned = vertex_charts[face]
        if np.any((assigned != -1) & (assigned != chart)):
            raise ValueError("UV vertex shared between output charts")
        vertex_charts[face] = chart
    if np.any(vertex_charts < 0):
        raise ValueError("Unused output vertex")
    with path.open("wb") as stream:
        stream.write(struct.pack("<3i", len(positions), faces.size, len(np.unique(face_charts))))
        for array, dtype in ((positions, "<f4"), (uv, "<f4"),
                             (faces, "<i4"), (vertex_charts, "<i4")):
            stream.write(np.asarray(array, dtype=dtype).tobytes())


def authored_cut_control(capture, parameterize, postprocess, pack, arap_iters):
    positions, authored_uv, faces, charts, _ = capture
    face_charts = charts[faces[:, 0]]
    if not np.all(charts[faces] == face_charts[:, None]):
        raise ValueError("Input face spans multiple chart identifiers")
    islands, local_faces, output_positions, areas = [], [], [], []
    infos = []
    for chart in np.unique(face_charts):
        selected = faces[face_charts == chart]
        corners = selected.ravel()
        # UV distinguishes intentional incisions. Exact equal position/UV slots
        # reunite duplicates introduced only for normals. No approximate weld.
        keys = np.column_stack((positions[corners], authored_uv[corners]))
        _, first, inverse = np.unique(keys, axis=0, return_index=True, return_inverse=True)
        source_positions = positions[corners[first]]
        cut_faces = inverse.reshape(-1, 3)
        uv, unique, local, info = parameterize.parameterize_chart(
            source_positions, cut_faces, np.arange(len(cut_faces)), arap_iters=arap_iters)
        triangle_positions = source_positions[cut_faces]
        areas.append(float(np.linalg.norm(np.cross(
            triangle_positions[:, 1] - triangle_positions[:, 0],
            triangle_positions[:, 2] - triangle_positions[:, 0]), axis=1).sum() * .5))
        islands.append(postprocess.align_island(uv))
        output_positions.append(source_positions[unique])
        local_faces.append(local)
        infos.append(info)
    islands = postprocess.normalize_texel_density(islands, local_faces, areas)
    packed, fill = pack.pack(islands, resolution=512, padding_texels=3)
    output_faces, output_charts = [], []
    offset = 0
    for chart, (vertices, indices) in enumerate(zip(output_positions, local_faces)):
        output_faces.append(indices + offset)
        output_charts.append(np.full(len(indices), chart, dtype=np.int32))
        offset += len(vertices)
    return (np.concatenate(output_positions), np.concatenate(packed),
            np.concatenate(output_faces), np.concatenate(output_charts),
            dict(control="authored cuts; UV coordinates discarded", packFill=fill, charts=infos))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--autouv", type=Path, required=True, help="External AutoUV checkout")
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    sys.path.insert(0, str(args.autouv.resolve()))
    import autouv
    from autouv import param, postprocess, pack
    capture = read_capture(args.reference)
    reference = topology(capture)
    positions, faces = reference[:2]
    args.out.mkdir(parents=True, exist_ok=True)
    cases = {}
    for cone in (35, 50, 65):
        name = f"AutoUV-cone{cone}"
        result = autouv.unwrap(autouv.Mesh(positions.copy(), faces.copy()),
                               max_cone_deg=cone, weld=False,
                               resolution=512, padding_texels=3)
        file = args.out / (name + ".bin")
        save(file, result.vertices, result.uv, result.faces, result.face_chart)
        verify_geometry(read_capture(file), reference)
        cases[name] = dict(file=str(file), stats=result.stats)
        print(name, json.dumps(result.stats), flush=True)
    name = "CONTROL-artist-V1-cuts-AutoUV-solver"
    output = authored_cut_control(capture, param, postprocess, pack, arap_iters=4)
    file = args.out / (name + ".bin")
    save(file, *output[:4])
    predicted = verify_geometry(read_capture(file), reference)
    if not np.array_equal(predicted, reference[4]):
        raise ValueError("Solver changed authored seam connectivity")
    cases[name] = dict(file=str(file), stats=output[4])
    for case in cases.values():
        case["captureSha256"] = hashlib.sha256(Path(case["file"]).read_bytes()).hexdigest()
    report = dict(referenceSha256=hashlib.sha256(args.reference.read_bytes()).hexdigest(),
                  geometryPreserved=True, faceWindingPreserved=True, cases=cases)
    (args.out / "report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
