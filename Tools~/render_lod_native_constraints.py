"""Audit real Unity FBX captures and compare native constraints with both controls."""
import argparse
import json
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image

from render_lod_feature_chains import evaluate, digest


def native_audit(directory, tests):
    reports, rows, audit = evaluate(directory, None, tests)
    region_checks = 0
    for report in reports:
        lookup = {(c["variant"], c["view"]): c for c in report["captures"]}
        for level in (1, 2):
            capture = lookup[(f"native-lod{level}", "front")]
            row = next(r for r in rows if r["fixture"] == report["fixture"] and r["level"] == level)
            assert capture["nativeCreaseConstraints"] and not capture["hardEdgeSourceFallback"]
            assert capture["missingHardEdges"] == capture["missingProtectedTriangles"] == capture["missingPatchInterfaces"] == 0
            assert capture["targetTriangles"] == row["target"]
            assert capture["budgetReached"] == (capture["triangles"] <= row["target"])
            before, after = capture["regionNormalsBefore"], capture["regionNormalsAfter"]
            assert len(before) == len(after)
            for a, b in zip(before, after):
                assert a["region"] == b["region"] and b["rms"] <= a["rms"] + .00011 and b["maximum"] <= a["maximum"] + .00011
                region_checks += 1
            if capture["colorsCorrected"]:
                for k in ("x", "y", "z", "w"):
                    assert capture["surfaceColorRms"][k] <= capture["colorRmsBefore"][k] + 1e-6
                    assert capture["surfaceColorMax"][k] <= capture["colorMaxBefore"][k] + 1e-6
            row["native"] = dict(triangles=capture["triangles"], budgetReached=capture["budgetReached"],
                normalRms=capture["normalRms"], colorRms=max(capture["surfaceColorRms"].values()),
                geometryRms=capture["sourceDistanceRms"], uvRms=capture["uvRms"],
                silhouette=max(lookup[(capture["variant"], view)]["silhouetteMismatch"] for view in ("front", "oblique")),
                shadingRms=max(lookup[(capture["variant"], view)]["rmsShadingError"] for view in ("front", "oblique")),
                lockedChainRetry=capture["lockedChainRetry"], protectedTriangles=capture["protectedTriangles"],
                nativeProbes=capture["nativeProbes"], note=capture["reductionNote"])
        assert lookup[("native-lod2", "front")]["triangles"] <= lookup[("native-lod1", "front")]["triangles"]
    audit["nativeBudgets"] = sum(r["native"]["budgetReached"] for r in rows)
    audit["regionChecks"] += region_checks
    audit["comparison"] = "Fresh source/strict belt/coarsened belt/direct native constraints; original URP materials and transitions are outside this diagnostic. Counts differ."
    root = Path(__file__).resolve().parent.parent
    audit["nativePlugins"] = {str(p.relative_to(root)): digest(p) for p in sorted((root / "Plugins").rglob("*")) if p.suffix in (".dll", ".so", ".dylib")}
    (directory / "native-constraints-audit.json").write_text(json.dumps(audit, indent=2), encoding="utf-8")
    return reports, rows, audit


def plots(directory, reports, rows):
    modes = (("hard", "Strict belt", "#818a99"), ("chains", "Coarsened belt", "#2a9d8f"), ("native", "Native constraints", "#9255aa"))
    variants = (("source", "LOD0"),) + tuple((mode+"-lod2", label+" LOD2") for mode, label, _ in modes)
    fig, axes = plt.subplots(len(reports), 4, figsize=(17, 3.2*len(reports)), squeeze=False)
    for i, report in enumerate(reports):
        lookup = {(c["variant"], c["view"]): c for c in report["captures"]}
        for j, (variant, label) in enumerate(variants):
            axis = axes[i, j]; capture = lookup[(variant, "oblique")]
            axis.imshow(Image.open(directory / f"{report['fixture']}-{variant}-oblique-shaded.png")); axis.axis("off")
            target = "" if variant == "source" else f" / target {capture['targetTriangles']:,}"
            axis.set_title(f"{report['fixture']} · {label}\n{capture['triangles']:,} tris{target}", fontsize=10)
    fig.suptitle("Actual project models · direct native crease constraints\nFresh Unity GPU captures · LOD2 requested at 1/9 of source", fontsize=16, y=.995)
    fig.text(.5, .008, "Uniform diagnostic shading; original URP materials / normal maps are outside this evaluation.", ha="center", fontsize=10)
    fig.tight_layout(rect=(0, .018, 1, .975)); fig.savefig(directory / "native-constraints-models.png", dpi=125); plt.close(fig)

    fig, axes = plt.subplots(1, 4, figsize=(22, 10)); y = np.arange(len(rows))
    fields = (("triangles", "Triangles / requested budget", 1), ("normalRms", "Surface normal RMS, degrees", 1),
              ("silhouette", "Worst two-view GPU silhouette error, %", 100), ("colorRms", "Surface RGBA RMS, worst channel", 1))
    for axis, (field, title, scale) in zip(axes, fields):
        for i, (mode, label, color) in enumerate(modes):
            key = "strict" if mode == "hard" else mode
            axis.barh(y+(i-1)*.25, [r[key][field]*scale for r in rows], height=.23, label=label, color=color)
        if field == "triangles": axis.scatter([r["target"] for r in rows], y, marker="|", c="#c0392b", s=130, label="Target")
        axis.set_yticks(y, [f"{r['fixture']} · LOD{r['level']}" for r in rows] if field == "triangles" else [])
        axis.invert_yaxis(); axis.set_title(title); axis.grid(axis="x", alpha=.2); axis.set_axisbelow(True)
    axes[0].legend(); fig.suptitle("Measured real models · LOD1 1/3, LOD2 1/9 targets\nLower is better; methods can return different triangle counts", fontsize=16)
    fig.tight_layout(rect=(0, 0, 1, .92)); fig.savefig(directory / "native-constraints-errors.png", dpi=150); plt.close(fig)

    colored = [r for r in reports if r["varyingVertexColors"]]
    if colored:
        variants = (("source", "LOD0"), ("chains-lod1", "Coarsened belt LOD1"), ("native-lod1", "Native LOD1"),
                    ("chains-lod2", "Coarsened belt LOD2"), ("native-lod2", "Native LOD2"))
        fig, axes = plt.subplots(len(colored)*2, 5, figsize=(18, len(colored)*6), squeeze=False)
        for i, report in enumerate(colored):
            lookup = {(c["variant"], c["view"]): c for c in report["captures"]}
            for k, view in enumerate(("front", "oblique")):
                for j, (variant, label) in enumerate(variants):
                    axis = axes[i*2+k, j]; capture = lookup[(variant, view)]
                    axis.imshow(Image.open(directory / f"{report['fixture']}-{variant}-{view}-color.png").convert("RGB")); axis.axis("off")
                    axis.set_title(f"{report['fixture']} · {view} · {label}\n{capture['triangles']:,} tris · RGBA RMS {max(capture['surfaceColorRms'].values()):.4f}", fontsize=9)
        fig.suptitle("Actual authored vertex-color fields · RGB displayed, RMS includes alpha\nFresh source and both methods; triangle counts differ", fontsize=16)
        fig.tight_layout(rect=(0, 0, 1, .94)); fig.savefig(directory / "native-constraints-colors.png", dpi=140); plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path); parser.add_argument("--tests", type=Path, required=True)
    args = parser.parse_args()
    reports, rows, audit = native_audit(args.directory, args.tests); plots(args.directory, reports, rows)
    print(json.dumps({k: v for k, v in audit.items() if k not in ("sources", "rows")}, indent=2))
    for row in rows:
        print(row["fixture"], "LOD"+str(row["level"]), row["chains"]["triangles"], "->", row["native"]["triangles"], "target", row["target"])
