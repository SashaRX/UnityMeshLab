"""Render every actual UV capture, measure overlaps, save a local HTML gallery.

No inferred boundary proposal is presented as a finished UV layout. Each PNG
records its capture hash, actual metrics and validation findings. Raw private
geometry stays in the caller's capture directory and is not embedded in HTML.
"""
import argparse
import csv
import hashlib
import html
import json
from pathlib import Path
import sys
import textwrap

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.collections import PolyCollection
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from analyze_uv_packing import measure
from analyze_uv_reference import overlaps
from seams import align, read_capture, topology


def metrics(file):
    triangles, charts, row = measure(file)
    p, uv, faces, _, _ = read_capture(file)
    a, b = triangles[:, 1] - triangles[:, 0], triangles[:, 2] - triangles[:, 0]
    signed = a[:, 0] * b[:, 1] - a[:, 1] * b[:, 0]
    pairs = overlaps(triangles, charts)
    mixed = 0
    for chart in np.unique(charts):
        positive = int((signed[charts == chart] > 1e-14).sum())
        negative = int((signed[charts == chart] < -1e-14).sum())
        mixed += min(positive, negative)
    row.update(overlapPairs=len(pairs), sameChartPairs=int(sum(r["chartA"] == r["chartB"] for r in pairs)),
               pairAreaSum=float(sum(r["area"] for r in pairs)), mixedWindingFaces=mixed,
               zeroAreaFaces=int((np.abs(signed) <= 1e-14).sum()),
               outOfBoundsVertices=int(((uv < -1e-7) | (uv > 1 + 1e-7)).any(axis=1).sum()),
               captureSha256=hashlib.sha256(file.read_bytes()).hexdigest())
    flagged = sorted({r[k] for r in pairs for k in ("faceA", "faceB")})
    return triangles, charts, row, flagged


def draw(ax, triangles, charts, flagged):
    ax.add_collection(PolyCollection(triangles, facecolors=plt.get_cmap("tab20")(charts % 20),
                                    edgecolors="#26313d", linewidths=0.22))
    if flagged:
        ax.add_collection(PolyCollection(triangles[flagged], facecolors="#ff000040",
                                        edgecolors="#d00000", linewidths=0.85))
    ax.set_xlim(0, 1)
    ax.set_ylim(0, 1)
    ax.set_aspect("equal")
    ax.set_xlabel("U")
    ax.set_ylabel("V")
    ax.set_facecolor("#f4f5f7")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--capture", action="append", default=[], metavar="NAME=FILE")
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--scores", type=Path, help="Optional placement scores.csv for gallery metadata")
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--reuse", action="store_true", help="Reuse exact-hash matching PNGs/quality CSV; overview omits overlap highlights")
    args = parser.parse_args()
    cases = [(name, Path(file)) for name, file in (s.split("=", 1) for s in args.capture)]
    if args.directory:
        cases.extend((file.stem, file) for file in sorted(args.directory.glob("region-*.bin")))
    if not cases or len({name for name, _ in cases}) != len(cases):
        raise ValueError("Expected nonempty uniquely named captures")
    for name, _ in cases:
        if Path(name).name != name or name in (".", "..") or any(c in name for c in '/\\:'):
            raise ValueError("Unsafe output name")
    reference = topology(read_capture(args.reference))
    scores = {}
    if args.scores:
        for row in csv.DictReader(args.scores.open(encoding="utf-8")):
            if row["reference"] == "artist-v1":
                scores[row["method"]] = row
    args.out.mkdir(parents=True, exist_ok=True)
    cached = {}
    cache_file = args.out / "quality.csv"
    if args.reuse and cache_file.exists():
        integer_keys = {"faces", "charts", "small", "overlapPairs", "sameChartPairs", "mixedWindingFaces", "zeroAreaFaces", "outOfBoundsVertices"}
        for row in csv.DictReader(cache_file.open(encoding="utf-8")):
            cached[row["method"]] = {k: int(v) if k in integer_keys else v if k in ("method", "file", "captureSha256") else float(v) for k, v in row.items()}
    rows, cards, overview = [], [], []
    for index, (name, file) in enumerate(cases):
        align(read_capture(file), reference)
        reuse = name in cached and (args.out / (name + ".png")).exists() and cached[name]["captureSha256"] == hashlib.sha256(file.read_bytes()).hexdigest()
        if reuse:
            _, uv, faces, vertex_charts, _ = read_capture(file)
            triangles, charts = uv[faces], vertex_charts[faces[:, 0]]
            row, flagged = cached[name].copy(), []
            row.pop("method")
        else:
            triangles, charts, row, flagged = metrics(file)
        row = dict(method=name, **row)
        rows.append(row)
        summary = (f'{row["charts"]} charts; fill {row["filledArea"]:.1%}; '
                   f'stretch {row["meanStretch"]:.3f} / {row["worstStretch"]:.3f}\n'
                   f'{row["overlapPairs"]} overlap pairs ({row["sameChartPairs"]} within chart); '
                   f'{row["mixedWindingFaces"]} mixed-winding; {row["zeroAreaFaces"]} zero-area; '
                   f'{row["outOfBoundsVertices"]} out-of-bounds')
        if not reuse:
            fig, ax = plt.subplots(figsize=(9, 9.5), layout="constrained")
            draw(ax, triangles, charts, flagged)
            ax.set_title(textwrap.fill(name, 65) + "\n" + summary, fontsize=10)
            fig.text(.5, .003, f'Actual UV capture SHA256 {row["captureSha256"]}', ha="center", fontsize=7)
            fig.savefig(args.out / (name + ".png"), dpi=140)
            plt.close(fig)
        placement = scores.get(name)
        extra = (f'V1 seam precision {float(placement["precision"]):.1%}; '
                 f'recall {float(placement["recall"]):.1%}; F1 {float(placement["f1"]):.3f}') if placement else ""
        caption = name + " " + summary.replace("\n", " ") + " " + extra
        escaped = html.escape(name)
        cards.append(f'<article data-search="{html.escape(caption.lower(), quote=True)}"><a href="{escaped}.png">'
                     f'<img loading="lazy" src="{escaped}.png" alt="{escaped}"></a><h2>{escaped}</h2>'
                     f'<p>{html.escape(summary)}<br>{html.escape(extra)}</p></article>')
        overview.append((name, triangles, charts, flagged, row))
        print(index + 1, "/", len(cases), name, row["charts"], "charts", len(flagged), "overlapping faces", flush=True)
    with (args.out / "quality.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    (args.out / "quality.json").write_text(json.dumps(rows, indent=2), encoding="utf-8")
    pages = []
    for start in range(0, len(overview), 12):
        page_rows = (min(12, len(overview)-start)+3)//4
        fig, axes = plt.subplots(page_rows, 4, figsize=(18, 5*page_rows), layout="constrained", squeeze=False)
        for ax in axes.flat:
            ax.set_axis_off()
        for ax, (name, triangles, charts, flagged, row) in zip(axes.flat, overview[start:start+12]):
            ax.set_axis_on()
            draw(ax, triangles, charts, flagged)
            ax.set_title(textwrap.fill(name, 36) + f'\n{row["charts"]} charts / {row["overlapPairs"]} overlaps', fontsize=8)
        page = f"overview-{start // 12 + 1:02d}.png"
        fig.savefig(args.out / page, dpi=110)
        plt.close(fig)
        pages.append(page)
    document = '<!doctype html><meta charset="utf-8"><title>UV method test gallery</title>'
    document += '''<style>body{font:15px system-ui;margin:24px;background:#f2f4f8;color:#202733}
input{padding:12px;width:min(600px,90%);margin-bottom:20px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:16px}
article{background:white;padding:12px;border-radius:8px}img{width:100%}h2{font-size:15px;overflow-wrap:anywhere}p{white-space:pre-line;font-size:13px}
a{color:#194e90}</style><h1>Actual UV layouts — identical geometry</h1>
<p>Click a layout for its full PNG. Red outlines mark positive-area overlaps, including within a chart.
Different colours identify charts, not artist parts. These are research results, not accepted defaults.</p>
<input id="filter" placeholder="Filter: planes, thickness, partfield, crease, relocate, cost4...">
<p><a href="quality.csv">Full quality metrics CSV</a></p><main>'''
    document += "\n".join(cards) + '</main><script>document.getElementById("filter").addEventListener("input", e=>{const q=e.target.value.toLowerCase();document.querySelectorAll("article").forEach(a=>a.hidden=!a.dataset.search.includes(q));});</script>'
    (args.out / "index.html").write_text(document, encoding="utf-8")
    readme = "# UV test renders\n\nOpen `index.html` in a browser to filter methods/settings. All PNGs are actual UV outputs on identical geometry.\n\n"
    readme += "Overview pages:\n\n" + "\n".join(f"- [{p}]({p})" for p in pages)
    readme += "\n\nIndividual renders:\n\n" + "\n".join(f"- [{n}]({n}.png)" for n, _ in cases) + "\n"
    (args.out / "README.md").write_text(readme, encoding="utf-8")


if __name__ == "__main__":
    main()
