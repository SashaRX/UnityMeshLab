"""Audit and plot actual Unity crease-coarsening captures (no synthetic images).

Run LodProjectVisualQualityTests with -meshlabLodBudget -meshlabLodHardEdges
-meshlabLodFeatureChains, then pass its output directory here.
"""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def evaluate(directory, baseline, tests):
    reports = [json.loads(p.read_text(encoding="utf-8")) for p in sorted(directory.glob("*-metrics.json"))]
    if len(reports) != 8:
        raise ValueError(f"Expected eight actual models, found {len(reports)}")
    result = ET.parse(tests).getroot().attrib
    if int(result["failed"]) or int(result["skipped"]):
        raise ValueError(f"Verification failed or skipped: {result}")
    rows, regions, source_pngs, source_png_differences = [], 0, 0, 0
    for report in reports:
        assert report["gpu"] and report["graphicsApi"] != "Null"
        assert digest(Path(report["sourcePath"])) == report["sourceSha256"]
        assert digest(directory / (report["fixture"] + "-source-front-shaded.png"))
        if baseline:
            old = json.loads((baseline / (report["fixture"] + "-metrics.json")).read_text(encoding="utf-8"))
            assert old["sourceSha256"] == report["sourceSha256"]
            for png in directory.glob(report["fixture"] + "-source-*.png"):
                if digest(png) == digest(baseline / png.name):
                    source_pngs += 1
                else:
                    source_png_differences += 1
        lookup = {(c["variant"], c["view"]): c for c in report["captures"]}
        for level in (1, 2):
            strict = lookup[(f"hard-lod{level}", "front")]
            chains = lookup[(f"chains-lod{level}", "front")]
            assert strict["targetTriangles"] == chains["targetTriangles"]
            for capture in (strict, chains):
                assert capture["missingHardEdges"] == capture["missingProtectedTriangles"] == capture["missingPatchInterfaces"] == 0
                assert not capture["hardEdgeSourceFallback"]
                assert capture["budgetReached"] == (capture["triangles"] <= capture["targetTriangles"])
                before, after = capture["regionNormalsBefore"], capture["regionNormalsAfter"]
                assert len(before) == len(after)
                for a, b in zip(before, after):
                    assert a["region"] == b["region"] and b["rms"] <= a["rms"] + .00011 and b["maximum"] <= a["maximum"] + .00011
                    regions += 1
                if capture["colorsCorrected"]:
                    assert all(capture["surfaceColorRms"][k] <= capture["colorRmsBefore"][k] + 1e-6 for k in ("x", "y", "z", "w"))
                    assert all(capture["surfaceColorMax"][k] <= capture["colorMaxBefore"][k] + 1e-6 for k in ("x", "y", "z", "w"))
            def summary(c):
                return dict(triangles=c["triangles"], budgetReached=c["budgetReached"], normalRms=c["normalRms"],
                            colorRms=max(c["surfaceColorRms"].values()), geometryRms=c["sourceDistanceRms"],
                            silhouette=max(lookup[(c["variant"], view)]["silhouetteMismatch"] for view in ("front", "oblique")),
                            shadingRms=max(lookup[(c["variant"], view)]["rmsShadingError"] for view in ("front", "oblique")))
            rows.append(dict(fixture=report["fixture"], level=level, sourceTriangles=report["sourceTriangles"],
                             target=chains["targetTriangles"], removedPoints=chains["coarsenedFeaturePoints"],
                             removedTriangles=chains["coarsenedFeatureTriangles"], strict=summary(strict), chains=summary(chains),
                             note=chains["reductionNote"]))
        assert lookup[("chains-lod2", "front")]["triangles"] <= lookup[("chains-lod1", "front")]["triangles"]
    audit = dict(tests=result, allCapturesFresh=True, modelCount=len(reports), regionChecks=regions,
                 sources=[{k: r[k] for k in ("fixture", "sourceSha256", "meshName", "sourceTriangles", "submeshes", "unity", "gpu", "graphicsApi", "resolution")} for r in reports],
                 identicalHistoricalSourcePngs=source_pngs, differentHistoricalSourcePngs=source_png_differences,
                 comparison="Strict and coarsened variants use the same freshly rendered source in this run; historical pixels may differ with preview DPI/rasterization.",
                 strictBudgets=sum(r["strict"]["budgetReached"] for r in rows),
                 chainBudgets=sum(r["chains"]["budgetReached"] for r in rows), rows=rows)
    (directory / "feature-chains-audit.json").write_text(json.dumps(audit, indent=2), encoding="utf-8")
    return reports, rows, audit


def plots(directory, reports, rows):
    fig, axes = plt.subplots(len(reports), 3, figsize=(13, 3.15 * len(reports)), squeeze=False)
    for i, report in enumerate(reports):
        lookup = {c["variant"]: c for c in report["captures"] if c["view"] == "oblique"}
        for j, (variant, label) in enumerate((("source", "LOD0"), ("hard-lod2", "Strict LOD2"), ("chains-lod2", "Coarsened creases LOD2"))):
            axis = axes[i, j]
            axis.imshow(Image.open(directory / f"{report['fixture']}-{variant}-oblique-shaded.png"))
            axis.axis("off")
            capture = lookup[variant]
            suffix = "" if variant == "source" else f" / target {capture['targetTriangles']:,}"
            axis.set_title(f"{report['fixture']} · {label}\n{capture['triangles']:,} tris{suffix}", fontsize=10)
    fig.suptitle("Actual project FBX · source / strict belt / coarsened creases\nUnity GPU captures · LOD2 target = 1/9 of LOD0", fontsize=15, y=.995)
    fig.text(.5, .008, "Uniform diagnostic shading; original URP materials / normal maps are not evaluated. Vertex colors are shown separately.", ha="center", fontsize=10)
    fig.tight_layout(rect=(0, .018, 1, .975))
    fig.savefig(directory / "feature-chains-models.png", dpi=125)
    plt.close(fig)
    fig, axes = plt.subplots(1, 4, figsize=(22, 9))
    labels = [f"{r['fixture']} · LOD{r['level']}" for r in rows]
    y = np.arange(len(rows))
    for axis, field, title, scale in ((axes[0], "triangles", "Triangles / requested budget", 1),
                                     (axes[1], "normalRms", "Surface normal RMS, degrees", 1),
                                     (axes[2], "silhouette", "Worst of two GPU silhouette errors, %", 100),
                                     (axes[3], "colorRms", "Surface RGBA RMS, worst channel", 1)):
        axis.barh(y-.18, [r["strict"][field]*scale for r in rows], height=.34, label="Strict belt", color="#818a99")
        axis.barh(y+.18, [r["chains"][field]*scale for r in rows], height=.34, label="Coarsened creases", color="#2a9d8f")
        if field == "triangles":
            axis.scatter([r["target"] for r in rows], y, marker="|", c="#c0392b", s=130, label="Target")
        axis.set_yticks(y, labels if field == "triangles" else [])
        axis.invert_yaxis(); axis.set_title(title); axis.grid(axis="x", alpha=.2); axis.set_axisbelow(True)
    axes[0].legend()
    fig.suptitle("Measured real models · LOD1 1/3, LOD2 1/9\nLower is better; triangle counts can differ between methods", fontsize=16)
    fig.tight_layout(rect=(0, 0, 1, .92))
    fig.savefig(directory / "feature-chains-errors.png", dpi=150)
    plt.close(fig)
    colored = [r for r in reports if r["varyingVertexColors"]]
    if colored:
        fig, axes = plt.subplots(len(colored)*2, 5, figsize=(18, len(colored)*6), squeeze=False)
        variants = (("source", "LOD0"), ("hard-lod1", "Strict LOD1"), ("chains-lod1", "Coarsened LOD1"),
                    ("hard-lod2", "Strict LOD2"), ("chains-lod2", "Coarsened LOD2"))
        for i, report in enumerate(colored):
            lookup = {(c["variant"], c["view"]): c for c in report["captures"]}
            for k, view in enumerate(("front", "oblique")):
                for j, (variant, label) in enumerate(variants):
                    axis = axes[i*2+k, j]; capture = lookup[(variant, view)]
                    axis.imshow(Image.open(directory / f"{report['fixture']}-{variant}-{view}-color.png").convert("RGB"))
                    axis.axis("off")
                    axis.set_title(f"{report['fixture']} · {view} · {label}\n{capture['triangles']:,} tris · RGBA RMS {max(capture['surfaceColorRms'].values()):.4f}", fontsize=9)
        fig.suptitle("Actual authored vertex-color fields · same freshly rendered source\nLOD1 1/3 / LOD2 1/9 targets · RGB displayed; RMS includes alpha", fontsize=16)
        fig.tight_layout(rect=(0, 0, 1, .94))
        fig.savefig(directory / "feature-chains-colors.png", dpi=140)
        plt.close(fig)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--tests", type=Path, required=True)
    parser.add_argument("--baseline", type=Path)
    args = parser.parse_args()
    reports, rows, audit = evaluate(args.directory, args.baseline, args.tests)
    plots(args.directory, reports, rows)
    print(json.dumps({k: v for k, v in audit.items() if k not in ("rows", "sources")}, indent=2))
    for row in rows:
        print(row["fixture"], "LOD"+str(row["level"]), row["strict"]["triangles"], "->", row["chains"]["triangles"],
              "target", row["target"], "removed points", row["removedPoints"])


if __name__ == "__main__":
    main()
