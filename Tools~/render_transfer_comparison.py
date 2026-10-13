"""Render the optional TransferBenchmarkExport layouts, without modifying mesh data.

Usage: python Tools~/render_transfer_comparison.py RUN_DIRECTORY
Requires numpy and matplotlib. The input directory contains forward and reverse
artifacts from the frozen Kamaz EditMode benchmark, including Viewer quality.
"""
import json
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
from matplotlib.colors import LogNorm
import numpy as np


def vectors(values, keys):
    return np.array([[v[k] for k in keys] for v in values], dtype=float)


def load(directory, lod):
    layouts = [json.loads(p.read_text(encoding="utf-8"))
               for p in sorted(directory.glob(f"*_LOD{lod}.layout.json"))]
    if not layouts:
        raise ValueError(f"No LOD{lod} layouts in {directory}")
    uv, positions, owners = [], [], []
    for layout in layouts:
        ix = np.array(layout["indices"], dtype=int).reshape(-1, 3)
        uv.append(vectors(layout["uv"], "xy")[ix])
        positions.append(vectors(layout["positions"], "xyz")[ix])
        owners.extend((layout["key"], f) for f in range(len(ix)))
    return np.concatenate(uv), np.concatenate(positions), owners, json.loads(
        (directory / f"quality_LOD{lod}.json").read_text(encoding="utf-8"))


def stretch(uv, positions):
    e, d = positions[:, 1] - positions[:, 0], positions[:, 2] - positions[:, 0]
    length = np.linalg.norm(e, axis=1)
    cross = np.linalg.norm(np.cross(e, d), axis=1)
    u, v = uv[:, 1] - uv[:, 0], uv[:, 2] - uv[:, 0]
    with np.errstate(divide="ignore", invalid="ignore"):
        x = np.einsum("ij,ij->i", e, d) / length
        y = cross / length
        j0 = u / length[:, None]
        j1 = (v - u * (x / length)[:, None]) / y[:, None]
        aa, bb = (j0 * j0).sum(axis=1), (j1 * j1).sum(axis=1)
        ab = (j0 * j1).sum(axis=1)
        determinant = j0[:, 0] * j1[:, 1] - j0[:, 1] * j1[:, 0]
        maximum = (aa + bb + np.sqrt(np.maximum(0, (aa - bb) ** 2 + 4 * ab * ab))) / 2
        ratio = maximum / np.abs(determinant)
    ratio[~np.isfinite(ratio)] = 1e12
    return np.maximum(1, ratio), cross / 2


def main(root):
    modes = [("Forward", root / "forward/kamaz_Off"),
             ("Reverse", root / "reverse/kamaz-frame3-exclusive"),
             ("Reverse + overlap", root / "reverse/kamaz-frame3-overlap")]
    if any(not path.exists() for _, path in modes):
        raise ValueError("Expected the frozen frame 3 forward and reverse artifacts")
    output = root / "visuals"
    output.mkdir(exist_ok=True)
    cmap = plt.get_cmap("turbo").copy()
    cmap.set_over("#e00077")
    norm = LogNorm(1, 4)
    fig, axes = plt.subplots(3, 3, figsize=(15, 15), layout="constrained")
    model, model_axes = plt.subplots(3, 3, figsize=(15, 11), layout="constrained")
    summaries = []
    worst = []
    trials = json.loads((root / "reverse/kamaz-trials.json").read_text(encoding="utf-8"))["trials"]
    overlap_trial = next(t for t in trials if t["overlap"])
    overlap_audit = json.loads(Path(overlap_trial["audit"]).read_text(encoding="utf-8"))
    ancestry, ancestry_axes = plt.subplots(3, 2, figsize=(12, 14), layout="constrained")
    ancestry_palette = np.array([[.40, .44, .53, 1], [.04, .61, .75, 1], [.99, .58, .10, 1]])
    for col, (mode, directory) in enumerate(modes):
        for row, lod in enumerate((0, 1, 2)):
            uv, positions, owners, quality = load(directory, lod)
            ratio, area = stretch(uv, positions)
            rgba = cmap(norm(ratio))
            ax = axes[row, col]
            artist = PolyCollection(uv, facecolors=rgba, edgecolors="none", rasterized=True)
            ax.add_collection(artist)
            bbox = vectors([quality["bboxMin"], quality["bboxMax"]], "xy")
            x0, y0 = bbox[0]
            x1, y1 = bbox[1]
            ax.plot([x0, x1, x1, x0, x0], [y0, y0, y1, y1, y0], color="#30343b", lw=.7)
            ax.set(xlim=(0, 1), ylim=(0, 1), aspect="equal", xlabel="U", ylabel="V")
            ax.set_title(f"{mode} / LOD{lod} — score {quality['score']}/100\n"
                         f"bbox fill {quality['fill']:.1%}; P95 stretch {quality['stretchP95']:.2f}x\n"
                         f"collapsed {quality['collapsedFaces']}; unexpected overlaps {quality['unexpectedOverlapPairs']}", fontsize=10)
            ax.set_facecolor("#edeff2")
            direction = np.array([1., .35, .7])
            direction /= np.linalg.norm(direction)
            right = np.cross(np.array([0., 1., 0.]), direction)
            right /= np.linalg.norm(right)
            up = np.cross(direction, right)
            projected = np.stack((positions @ right, positions @ up), axis=-1)
            depth = (positions @ direction).mean(axis=1)
            order = np.argsort(depth)
            m = model_axes[row, col]
            m.add_collection(PolyCollection(projected[order], facecolors=rgba[order], edgecolors="none", rasterized=True))
            points = projected.reshape(-1, 2)
            m.set(xlim=(points[:, 0].min() - .2, points[:, 0].max() + .2),
                  ylim=(points[:, 1].min() - .2, points[:, 1].max() + .2), aspect="equal")
            m.axis("off")
            m.set_title(f"{mode} / LOD{lod}\narea >4x: {quality['severeArea']:.2%}; max {quality['worstStretch']:.3g}x", fontsize=11)
            summaries.append(dict(mode=mode, lod=lod, **quality))
            for f in np.argsort(ratio)[-10:][::-1]:
                worst.append(dict(mode=mode, lod=lod, mesh=owners[f][0], outputFace=owners[f][1],
                                  stretch=float(ratio[f]), worldArea=float(area[f]),
                                  center=positions[f].mean(axis=0).tolist()))
            if col == 2:
                nodes = {n["key"]: n for n in overlap_audit["report"]["nodes"] if n["lod"] == lod}
                faces = [nodes[key]["faces"][f] for key, f in owners]
                kinds = np.array([2 if f["intentionalOverlap"] else 0 if f["inherited"] else 1 for f in faces])
                for a, polygons in ((ancestry_axes[row, 0], uv), (ancestry_axes[row, 1], projected[order])):
                    colors = ancestry_palette[kinds] if a is ancestry_axes[row, 0] else ancestry_palette[kinds[order]]
                    a.add_collection(PolyCollection(polygons, facecolors=colors, edgecolors="none", rasterized=True))
                    a.set_aspect("equal")
                    if a is ancestry_axes[row, 0]:
                        a.set(xlim=(0, 1), ylim=(0, 1), xlabel="U", ylabel="V")
                    else:
                        a.set(xlim=m.get_xlim(), ylim=m.get_ylim())
                        a.axis("off")
                inherited = sum(area[f] for f, face in enumerate(faces) if face["inherited"]) / area.sum()
                detail = sum(area[f] for f, face in enumerate(faces) if face["intentionalOverlap"]) / area.sum()
                ancestry_axes[row, 0].set_title(f"LOD{lod}: inherited world area {inherited:.1%}; projected detail area {detail:.1%}")
                ancestry_axes[row, 1].set_title(f"LOD{lod}: {int((kinds == 2).sum())} projected detail faces; {int((kinds == 1).sum())} new faces")
    scalar = matplotlib.cm.ScalarMappable(norm=norm, cmap=cmap)
    for target, axlist in ((fig, axes), (model, model_axes)):
        bar = target.colorbar(scalar, ax=axlist.ravel().tolist(), shrink=.5, extend="max", pad=.01)
        bar.set_ticks([1, 2, 3, 4], labels=["1x", "2x", "3x", "4x"])
        bar.set_label("World-surface / UV anisotropy; magenta >4x or collapsed")
    fig.suptitle("Kamaz Typhoon: UV layouts — bbox fill counts overlapping coverage once", fontsize=15)
    model.suptitle("Kamaz Typhoon: stretch on geometry (orthographic diagnostic, no materials)", fontsize=15)
    fig.savefig(output / "uv-comparison.png", dpi=145)
    model.savefig(output / "surface-stretch.png", dpi=160)
    from matplotlib.patches import Patch
    ancestry.legend(handles=[Patch(color=ancestry_palette[0], label="Inherited"),
                             Patch(color=ancestry_palette[1], label="New UV"),
                             Patch(color=ancestry_palette[2], label="Intentional projected overlap")],
                    loc="outside lower center", ncol=3)
    ancestry.suptitle("Reverse + overlap: saved per-face ancestry", fontsize=15)
    ancestry.savefig(output / "reverse-ancestry.png", dpi=145)
    plt.close(fig)
    plt.close(model)
    plt.close(ancestry)
    (output / "summary.json").write_text(json.dumps(summaries, ensure_ascii=False, indent=2), encoding="utf-8")
    (output / "worst-faces.json").write_text(json.dumps(worst, indent=2), encoding="utf-8")
    for s in summaries:
        print(f"{s['mode']:18s} LOD{s['lod']} score={s['score']} bbox={s['fill']:.1%} "
              f"P95={s['stretchP95']:.3g} max={s['worstStretch']:.3g} "
              f"collapse={s['collapsedFaces']} overlap={s['unexpectedOverlapPairs']} allowed={s['allowedOverlapPairs']}")


if __name__ == "__main__":
    main(Path(sys.argv[1]).resolve())
