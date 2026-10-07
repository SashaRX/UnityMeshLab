"""Render exact Remesh log replay buffers with NumPy and Matplotlib.

UV input is UvCapturedInputReplay's little-endian format: three int32 values
(vertex count, index count, chart count), all float32 XYZ, all float32 UV,
all int32 indices, then one int32 chart per vertex. Geometry input contains
two int32 counts followed by all float32 XYZ and all int32 indices.

Example: python render_remesh_log_diagnostics.py --plain plain.bin
         --merge merge.bin --mesh simplify.bin --out renders
This script draws recorded geometry and does not generate or repair UVs.
"""
import argparse
import colorsys
import hashlib
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import LineCollection, PolyCollection
from mpl_toolkits.mplot3d.art3d import Line3DCollection, Poly3DCollection
import numpy as np


def read_capture(path, uv=False):
    data = path.read_bytes()
    header_size = 12 if uv else 8
    if len(data) < header_size:
        raise ValueError(f"Incomplete capture header: {path}")
    header = np.frombuffer(data, "<i4", count=3 if uv else 2)
    vertices, index_count = map(int, header[:2])
    charts_count = int(header[2]) if uv else 0
    expected = header_size + vertices * (24 if uv else 12) + index_count * 4
    if vertices <= 0 or index_count <= 0 or index_count % 3 or len(data) != expected:
        raise ValueError(f"Invalid capture counts or length: {path}")
    positions = np.frombuffer(data, "<f4", count=vertices * 3, offset=header_size).reshape(-1, 3)
    offset = header_size + vertices * 12
    tex = np.frombuffer(data, "<f4", count=vertices * 2, offset=offset).reshape(-1, 2) if uv else None
    offset += vertices * 8 if uv else 0
    indices = np.frombuffer(data, "<i4", count=index_count, offset=offset).reshape(-1, 3)
    chart = np.frombuffer(data, "<i4", count=vertices, offset=offset + index_count * 4) if uv else None
    if not np.isfinite(positions).all() or np.any(indices < 0) or np.any(indices >= vertices):
        raise ValueError(f"Invalid positions or indices: {path}")
    if uv and (not np.isfinite(tex).all() or charts_count <= 0 or np.any(chart[indices] < 0) or
               np.any(chart[indices] >= charts_count) or np.any(chart[indices] != chart[indices[:, :1]])):
        raise ValueError(f"Invalid UV coordinates or per-face chart IDs: {path}")
    return positions.astype(float), indices, None if tex is None else tex.astype(float), chart, charts_count


def uv_metrics(capture):
    positions, indices, tex, charts, charts_count = capture
    tri = positions[indices]
    uv = tex[indices]
    e1, e2 = tri[:, 1] - tri[:, 0], tri[:, 2] - tri[:, 0]
    u1, u2 = uv[:, 1] - uv[:, 0], uv[:, 2] - uv[:, 0]
    length = np.linalg.norm(e1, axis=1)
    area2 = np.linalg.norm(np.cross(e1, e2), axis=1)
    if np.any(length <= 0) or np.any(area2 <= 0):
        raise ValueError("UV capture contains degenerate geometry triangles")
    tx = np.sum(e1 * e2, axis=1) / length
    ty = area2 / length
    first = u1 / length[:, None]
    second = (u2 - first * tx[:, None]) / ty[:, None]
    trace = np.sum(first * first + second * second, axis=1)
    determinant = first[:, 0] * second[:, 1] - first[:, 1] * second[:, 0]
    if np.any(determinant == 0):
        raise ValueError("UV capture contains collapsed triangles")
    largest = .5 * (trace + np.sqrt(np.maximum(0, trace * trace - 4 * determinant * determinant)))
    stretch = largest / np.abs(determinant)
    face_charts = charts[indices[:, 0]]
    counts = np.bincount(face_charts, minlength=charts_count)
    areas = np.bincount(face_charts, weights=area2, minlength=charts_count)
    uv_area2 = np.abs(u1[:, 0] * u2[:, 1] - u1[:, 1] * u2[:, 0])
    return {
        "faces": len(indices), "charts": charts_count, "smallCharts": int(np.sum((counts > 0) & (counts <= 8))),
        "meanStretch": float(np.sum(stretch * area2) / area2.sum()), "worstStretch": float(stretch.max()),
        "triangleUvArea": float(uv_area2.sum() / 2), "largestChartSurfaceFraction": float(areas.max() / areas.sum()),
        "outOfBoundsVertices": int(np.any((tex < 0) | (tex > 1), axis=1).sum()),
    }


def chart_color(chart):
    return colorsys.hsv_to_rgb((chart * .61803398875) % 1, .33, .90)


def draw_uvs(plain, merged, output):
    a, b = read_capture(plain, True), read_capture(merged, True)
    if not np.array_equal(a[0][a[1]], b[0][b[1]]):
        raise ValueError("Plain and merged captures have different 3D triangle corners")
    fig, axes = plt.subplots(1, 2, figsize=(14, 7.6), constrained_layout=True)
    rows = []
    for ax, capture, title in zip(axes, (a, b), ("Merge disabled", "Merge enabled")):
        _, indices, tex, charts, _ = capture
        metrics = uv_metrics(capture)
        rows.append(metrics)
        colors = [chart_color(int(chart)) for chart in charts[indices[:, 0]]]
        ax.set_facecolor("#f2f3f5")
        ax.add_collection(PolyCollection(tex[indices], facecolors=colors, edgecolors="#29465d", linewidths=.38))
        edges = np.sort(np.concatenate((indices[:, [0, 1]], indices[:, [1, 2]], indices[:, [2, 0]])), axis=1)
        unique, counts = np.unique(edges, axis=0, return_counts=True)
        ax.add_collection(LineCollection(tex[unique[counts == 1]], colors="#1b2734", linewidths=.75))
        ax.set(xlim=(0, 1), ylim=(0, 1), aspect="equal", xlabel="U", ylabel="V")
        ax.set_title(f"{title}: {metrics['charts']} charts, {metrics['smallCharts']} small\n"
                     f"stretch mean {metrics['meanStretch']:.3f}, worst {metrics['worstStretch']:.3f}; "
                     f"UV triangle area {metrics['triangleUvArea']:.1%}", fontsize=11)
    fig.suptitle(f"Exact Unity unwrap replay | {rows[0]['faces']:,} unchanged triangles", fontsize=14)
    fig.savefig(output / "uv-comparison.png", dpi=170)
    plt.close(fig)
    return {"plain": rows[0], "merge": rows[1], "same3dTriangleCorners": True}


def mesh_boundaries(positions, indices):
    # Bit-exact position welding mirrors the runtime topology inspector and
    # prevents duplicated UV/normal vertices from becoming false open edges.
    bits = positions.astype("<f4").view("<u4").reshape(-1, 3)
    _, first, slots = np.unique(bits, axis=0, return_index=True, return_inverse=True)
    welded = positions[first]
    triangles = slots[indices]
    edges = np.sort(np.concatenate((triangles[:, [0, 1]], triangles[:, [1, 2]], triangles[:, [2, 0]])), axis=1)
    unique, counts = np.unique(edges, axis=0, return_counts=True)
    return welded, triangles, unique[counts == 1], int(np.sum(counts > 2))


def boundary_components(edges):
    pending = set(range(len(edges)))
    touching = {}
    for edge, (a, b) in enumerate(edges):
        touching.setdefault(int(a), []).append(edge)
        touching.setdefault(int(b), []).append(edge)
    components = []
    while pending:
        stack = [min(pending)]; found = set()
        while stack:
            edge = stack.pop()
            if edge not in pending:
                continue
            pending.remove(edge); found.add(edge)
            for vertex in edges[edge]:
                stack.extend(touching[int(vertex)])
        components.append(edges[sorted(found)])
    return components


def draw_boundary(path, output):
    positions, indices, _, _, _ = read_capture(path)
    welded, triangles, edges, non_manifold = mesh_boundaries(positions, indices)
    components = boundary_components(edges)
    triangular = [edge for edge in components if len(edge) == 3 and len(np.unique(edge)) == 3]
    candidates = triangular if triangular else components
    selected = max(candidates, key=lambda e: np.linalg.norm(welded[e[:, 1]] - welded[e[:, 0]], axis=1).sum()) if candidates else None
    report = {"faces": len(indices), "boundaryEdges": len(edges), "boundaryComponents": len(components),
              "triangularBoundaryLoops": len(triangular), "nonManifoldEdges": non_manifold}
    center = (positions.min(axis=0) + positions.max(axis=0)) / 2
    extent = np.ptp(positions, axis=0).max()
    fig = plt.figure(figsize=(13, 7), constrained_layout=True)
    overview = fig.add_subplot(1, 2, 1, projection="3d")
    overview.add_collection3d(Poly3DCollection((positions[indices] - center) / extent,
        facecolors="#c5d5de", edgecolors="#416078", linewidths=.28))
    if len(edges):
        overview.add_collection3d(Line3DCollection((welded[edges] - center) / extent, colors="#ce2528", linewidths=2.4))
    for setter in (overview.set_xlim, overview.set_ylim, overview.set_zlim):
        setter(-.55, .55)
    overview.set_box_aspect((1, 1, 1)); overview.set_axis_off()
    overview.set_title(f"Supplied geometry: {len(indices):,} faces\n{len(edges)} welded open edges highlighted in red")
    closeup = fig.add_subplot(1, 2, 2)
    closeup.set_aspect("equal"); closeup.set_facecolor("#f5f6f8")
    if selected is not None:
        vertices = np.unique(selected)
        points = welded[vertices]
        hole_center = points.mean(axis=0)
        _, _, basis = np.linalg.svd(points - hole_center, full_matrices=True)
        normal = basis[-1]
        adjacent = np.any(np.isin(triangles, vertices), axis=1)
        normals = np.cross(positions[indices[adjacent, 1]] - positions[indices[adjacent, 0]],
                           positions[indices[adjacent, 2]] - positions[indices[adjacent, 0]])
        if np.dot(normal, normals.sum(axis=0)) < 0:
            normal = -normal
        u = basis[0]; v = np.cross(normal, u)
        radius = np.linalg.norm(welded[selected[:, 1]] - welded[selected[:, 0]], axis=1).max() * 3
        nearby = np.any(np.linalg.norm(positions[indices] - hole_center, axis=2) < radius, axis=1)
        projected = np.stack(((positions - hole_center) @ u, (positions - hole_center) @ v), axis=1) / radius
        closeup.add_collection(PolyCollection(projected[indices[nearby]], facecolors=(.64, .75, .82, .10),
                                              edgecolors="#667f90", linewidths=.65))
        projected_boundary = np.stack(((welded[selected] - hole_center) @ u, (welded[selected] - hole_center) @ v), axis=2) / radius
        closeup.add_collection(LineCollection(projected_boundary, colors="#ce2528", linewidths=2.4))
        labels = np.stack(((points - hole_center) @ u, (points - hole_center) @ v), axis=1) / radius
        closeup.scatter(labels[:, 0], labels[:, 1], color="#ce2528", s=22, zorder=5)
        for number, (x, y) in enumerate(labels, 1):
            closeup.annotate(str(number), (x, y), xytext=(5, 5), textcoords="offset points", color="#a61219")
        if len(points) == 3:
            missing_area = np.linalg.norm(np.cross(points[1] - points[0], points[2] - points[0])) * .5
            report["selectedBoundaryTriangleArea"] = float(missing_area)
            height = np.ptp(labels[:, 1])
            width = np.ptp(labels[:, 0])
            if 0 < height < width * .05:
                gain = width * .3 / height
                inset = closeup.inset_axes([.08, .05, .72, .23])
                amplified = projected_boundary.copy()
                amplified[:, :, 1] *= gain
                inset.add_collection(LineCollection(amplified, colors="#ce2528", linewidths=2))
                inset.autoscale(); inset.margins(.15); inset.set_aspect("equal"); inset.set_axis_off()
                inset.set_title(f"Thin boundary: axis 2 expanded {gain:.0f}x for display", fontsize=9)
        closeup.set(xlim=(-1, 1), ylim=(-1, 1), xlabel="Boundary-plane axis 1 / crop radius", ylabel="Boundary-plane axis 2 / crop radius")
        closeup.set_title(f"Local open boundary: {len(selected)} edges\nExact wireframe projected into its plane")
        overview.view_init(elev=np.degrees(np.arcsin(np.clip(normal[2], -1, 1))),
                           azim=np.degrees(np.arctan2(normal[1], normal[0])))
        report.update(selectedBoundaryEdges=len(selected), selectedBoundaryCenter=hole_center.tolist(), cropRadius=float(radius))
    else:
        closeup.text(.5, .5, "No open boundary in supplied geometry", ha="center", va="center", transform=closeup.transAxes)
        closeup.set_axis_off(); overview.view_init(elev=20, azim=-65)
    fig.suptitle("Geometry boundary diagnostic | position-welded topology", fontsize=14)
    fig.savefig(output / "boundary-closeup.png", dpi=170)
    plt.close(fig)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plain", type=Path)
    parser.add_argument("--merge", type=Path)
    parser.add_argument("--mesh", type=Path)
    parser.add_argument("--out", type=Path, default=Path("_results~/remesh-log-diagnostics"))
    args = parser.parse_args()
    if bool(args.plain) != bool(args.merge) or not (args.plain or args.mesh):
        parser.error("Provide both --plain and --merge, or --mesh, or all three.")
    args.out.mkdir(parents=True, exist_ok=True)
    report = {"inputs": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                         for path in (args.plain, args.merge, args.mesh) if path is not None}}
    if args.plain:
        report["uv"] = draw_uvs(args.plain, args.merge, args.out)
    if args.mesh:
        report["geometry"] = draw_boundary(args.mesh, args.out)
    (args.out / "diagnostics.json").write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print(args.out.resolve())


if __name__ == "__main__":
    main()
