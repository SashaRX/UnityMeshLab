"""Compare real tent polygon paint / retriangulation with its preceding 62-triangle stage."""
import argparse
import json
import xml.etree.ElementTree as ET
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

from render_lod_far_refinement import BACKGROUND, coverage, frame, compare_gpu
from render_lod_screen_acceptance import readback, digest


def fields(directory, variant, view, source_mask):
    mask = coverage(directory, "tent-colored", variant, view)
    raw = readback(directory / f"tent-colored-{variant}-{view}-color.rgba")
    cov = frame(mask[:, :, None], source_mask)[:, :, 0]
    color = frame(raw - BACKGROUND * (1-mask[:, :, None]), source_mask)
    return color / np.maximum(cov[:, :, None], 1e-20), cov


def capture(source, target, a, b):
    weight = np.minimum(a, b)
    channels = np.sqrt(np.sum((source-target)**2*weight[:, :, None], axis=(0, 1))/max(weight.sum(), 1e-20))
    return {"rgbaRms": float(np.sqrt(np.mean(channels**2))), "rgbaChannelRms": channels.tolist(),
            "coverageError": float(np.abs(a-b).sum()/max(np.maximum(a,b).sum(), 1e-20))}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("baseline", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--bench-baseline", type=Path)
    parser.add_argument("--retriangulated", action="store_true", help="Audit retained geometric corners and refitted UV2 instead of unchanged triangles.")
    args = parser.parse_args()
    directory, baseline, output = args.directory, args.baseline, args.output
    test = ET.parse(directory / "tests.xml").getroot().attrib
    assert test["result"] == "Passed" and test["total"] == test["passed"] and test["skipped"] == "0"
    current = json.loads((directory / "tent-colored-metrics.json").read_text(encoding="utf-8"))
    old = json.loads((baseline / "tent-colored-metrics.json").read_text(encoding="utf-8"))
    assert current["sourceSha256"] == old["sourceSha256"]
    row = next(r for r in current["captures"] if r["variant"] == "far-lod2")
    prior = next(r for r in old["captures"] if r["variant"] == "far-lod2")
    mesh = json.loads((directory / "tent-colored-far-lod2-mesh.json").read_text(encoding="utf-8"))
    previous_mesh = json.loads((baseline / "tent-colored-far-lod2-mesh.json").read_text(encoding="utf-8"))
    assert row["triangles"] == prior["triangles"] == 62
    assert len(mesh["positions"]) <= len(previous_mesh["positions"]) == 154
    def corners(data, field):
        return [[data[field][v] for v in slot["indices"]] for slot in data["slots"]]
    unchanged = {field: corners(mesh,field) == corners(previous_mesh,field) for field in ("positions", "normals", "tangents")}
    unchanged["slots"] = [len(s["indices"]) for s in mesh["slots"]] == [len(s["indices"]) for s in previous_mesh["slots"]]
    unchanged["uvs"] = all(a["dimension"] == b["dimension"] and (a["dimension"] == 0 or
        [[a["values"][v] for v in s["indices"]] for s in mesh["slots"]] ==
        [[b["values"][v] for v in s["indices"]] for s in previous_mesh["slots"]]) for a,b in zip(mesh["uvs"],previous_mesh["uvs"]))
    retained_corners = {tuple(p[k] for k in "xyz") for p in mesh["positions"]} == {tuple(p[k] for k in "xyz") for p in previous_mesh["positions"]}
    assert len(mesh["slots"]) == len(previous_mesh["slots"]) and unchanged["slots"]
    assert [v["dimension"] for v in mesh["uvs"]] == [v["dimension"] for v in previous_mesh["uvs"]]
    if args.retriangulated:
        correction = row["attributeFit"]["paintRetriangulation"]
        assert retained_corners and correction["accepted"] and correction["flips"] > 0
        assert abs(correction["boundaryBefore"] - prior["farComparison"]["colorBoundaryMean"]) < 1e-6
        assert correction["uvAfter"] <= correction["uvBefore"] + 1e-6
        assert all(a <= b + correction["uvViewRmsTolerance"] + 1e-7 for a, b in zip(correction["uvViewsAfter"], correction["uvViewsBefore"]))
    else:
        assert all(unchanged.values())
    for view in ("front", "oblique"):
        for mode in ("color", "coverage", "shaded"):
            name = f"tent-colored-source-{view}-{mode}.rgba"
            assert digest(directory / name) == digest(baseline / name)
    gpu = compare_gpu(directory, baseline, "tent-colored")
    views = ("paint-edge", "paint-angle", "front", "oblique")
    fig, axes = plt.subplots(4, 3, figsize=(12, 11), squeeze=False)
    fig.subplots_adjust(left=.09, right=.97, top=.9, bottom=.08, hspace=.3, wspace=.08)
    extra = []
    for n, view in enumerate(views):
        mask = coverage(directory, "tent-colored", "source", view)
        source, a = fields(directory, "source", view, mask)
        colors = [source]; coverages = [a]
        for variant, folder in (("paint-before", directory), ("far-lod2", directory)):
            actual_variant = variant if view.startswith("paint-") else "far-lod2"
            actual_folder = folder if view.startswith("paint-") or variant == "far-lod2" else baseline
            color, b = fields(actual_folder, actual_variant, view, mask)
            colors.append(color)
            coverages.append(b)
            extra.append({"view": view, "variant": variant, **capture(source, color, a, b)})
        for col, color in enumerate(colors):
            display = np.clip(color[:, :, :3], 0, 1)
            display[coverages[col] <= 0] = BACKGROUND[:3]
            # Field captures are linear. Display encoding is separate from metrics.
            display = np.where(display <= .0031308, display*12.92, 1.055*display**(1/2.4)-.055)
            axes[n, col].imshow(np.flipud(display), interpolation="nearest")
            axes[n, col].set_xticks([]); axes[n, col].set_yticks([])
            if n == 0: axes[n, col].set_title(("Source · 560 triangles", "Before · 62 / 154 vertices", f"{'Paint geometry' if args.retriangulated else 'Polygon paint'} · 62 / {len(mesh['positions'])} vertices")[col])
        axes[n, 0].set_ylabel(view)
    fig.suptitle("Actual project rainbow tent · vertex RGB\nSource frame: 64-pixel maximum extent, aspect ratio retained; nearest-pixel enlargement")
    fig.text(.5, .025, "Fresh Unity GPU fields; original project materials and complete tent assembly are not evaluated.", ha="center")
    output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(str(output)+"_MODELS.png", dpi=140); plt.close(fig)
    before, after = prior["farComparison"], row["farComparison"]
    labels = ["CPU mean lost paint pairs", "CPU worst RGBA RMS", "GPU RGBA RMS at 64px"]
    quantities = [(before["colorBoundaryMean"], after["colorBoundaryMean"]), (before["rgbaRms"], after["rgbaRms"]),
                  ([g["before"]["rgbaRms"] for g in gpu], [g["after"]["rgbaRms"] for g in gpu])]
    if args.retriangulated:
        labels.insert(1, "Source surface UV2 RMS")
        quantities.insert(1, (correction["uvBefore"], correction["uvAfter"]))
    fig, axes = plt.subplots(1, len(labels), figsize=(4.3*len(labels), 4))
    for axis, label, values in zip(axes, labels, quantities):
        if label.startswith("GPU"):
            x = np.arange(2); axis.bar(x-.18, values[0], .36, label="Before"); axis.bar(x+.18, values[1], .36, label="After")
            axis.set_xticks(x, [g["view"] for g in gpu]); axis.legend()
        else:
            bars = axis.bar(("Before", "After"), values)
            for bar, value in zip(bars, values): axis.text(bar.get_x()+bar.get_width()/2, value, f"{value:.5f}", ha="center", va="bottom")
        axis.set_title(label); axis.grid(axis="y", alpha=.2)
    fig.tight_layout(); fig.savefig(str(output)+"_ERRORS.png", dpi=140); plt.close(fig)
    report = {"fixture": current["meshName"], "sourceSha256": current["sourceSha256"], "sourceTriangles": 560,
              "triangles": row["triangles"], "verticesBefore": 154, "verticesAfter": len(mesh["positions"]),
              "fieldsUnchanged": unchanged, "before64": before, "after64": after,
              "polygonCorrection": row["attributeFit"]["polygonColorBoundaries"], "gpu64": gpu, "paintGpu64": extra,
              "tests": {k: test[k] for k in ("total", "passed", "failed", "skipped", "duration")},
              "raw": str(directory), "baselineRaw": str(baseline),
              "limits": "Diagnostic vertex RGBA; original materials/assembly/transition and semantic paint labels unverified. CPU guide acceptance remains explicit."}
    if args.retriangulated:
        report["retriangulation"] = correction
        report["geometricCornersRetained"] = retained_corners
        report["limits"] += " UV2 metrics are coordinate correspondence, not an actual baked-atlas irradiance test. One view's UV2 RMS may increase by at most 0.00002."
    if args.bench_baseline:
        bench = json.loads((directory / "bench-colored-metrics.json").read_text(encoding="utf-8"))
        old_bench = json.loads((args.bench_baseline / "bench-colored-metrics.json").read_text(encoding="utf-8"))
        assert bench["sourceSha256"] == old_bench["sourceSha256"]
        bench_row = next(r for r in bench["captures"] if r["variant"] == "far-lod2")
        old_row = next(r for r in old_bench["captures"] if r["variant"] == "far-lod2")
        stage = bench_row["attributeFit"]["paintRetriangulation"]
        assert bench_row["triangles"] == old_row["triangles"] == 1032
        assert stage["verticesBefore"] == stage["verticesAfter"] == 2251 and not stage["accepted"]
        matched = {}
        for variant in ("source", "native-lod1", "native-lod2", "far-lod1", "far-lod2"):
            for view in ("front", "oblique"):
                for mode in ("color", "coverage", "shaded"):
                    name = f"bench-colored-{variant}-{view}-{mode}.rgba"
                    matched[name] = digest(directory / name) == digest(args.bench_baseline / name)
        assert all(matched.values())
        report["benchRegression"] = {"sourceSha256": bench["sourceSha256"], "triangles": bench_row["triangles"],
            "vertices": stage["verticesAfter"], "stageBypassed": True, "gpuFieldsIdentical": matched,
            "gpu64": compare_gpu(directory, args.bench_baseline, "bench-colored"), "baselineRaw": str(args.bench_baseline)}
    Path(str(output)+"_RESULTS.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps({"tests": report["tests"], "polygonCorrection": report["polygonCorrection"], "gpu64": gpu}, indent=2))


if __name__ == "__main__":
    main()
