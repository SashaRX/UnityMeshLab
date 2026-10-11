"""Audit fresh Unity candidate-selection captures and render their comparison."""
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
from render_lod_screen_acceptance import readback, morph


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def worst(captures, field, width=384, eligible=None):
    values = [s[field] for c in captures for s in c["screenAcceptance"]
              if s["width"] == width and (eligible is None or s[eligible])]
    return max(values) if values else None


def audit(directory, baseline, tests):
    result = ET.parse(tests).getroot().attrib
    assert int(result["total"]) == int(result["passed"]) == 203
    assert result["result"] == "Passed" and int(result["failed"]) == int(result["skipped"]) == 0
    paths = sorted(directory.glob("*-metrics.json"))
    assert len(paths) == 8
    reports, rows, hashes, control_identical, guided_identical, shaded_identical = [], [], {}, 0, 0, 0
    for path in paths:
        report = json.loads(path.read_text(encoding="utf-8"))
        previous = json.loads((baseline / path.name).read_text(encoding="utf-8"))
        assert report["sourceSha256"] == previous["sourceSha256"]
        assert report["resolution"] == 384 and report["graphicsApi"] != "Null" and report["gpu"]
        captures = report["captures"]
        assert len(captures) == 10
        assert {(c["variant"], c["view"]) for c in captures} == {
            (v, view) for v in ("source", "native-lod1", "native-lod2", "guided-lod1", "guided-lod2") for view in ("front", "oblique")}
        for capture in captures:
            assert len(capture["screenAcceptance"]) == 3
            assert capture["missingHardEdges"] == capture["missingProtectedTriangles"] == capture["missingPatchInterfaces"] == 0
            if capture["variant"].startswith("guided-"):
                assert capture["screenQuality"]["views"] == 6
                assert all(c["screenQuality"]["views"] == 6 for c in capture["budgetCandidates"])
            for kind in ("color", "coverage"):
                name = f"{report['fixture']}-{capture['variant']}-{capture['view']}-{kind}.rgba"
                raw = directory / name
                readback(raw); hashes[name] = digest(raw)
                if not capture["variant"].startswith("guided-"):
                    assert hashes[name] == digest(baseline / name), "Fresh control differs: " + name
                    control_identical += 1
                else:
                    control_name = name.replace("-guided-", "-native-")
                    guided_identical += hashes[name] == digest(directory / control_name)
            if capture["variant"].startswith("guided-"):
                name = f"{report['fixture']}-{capture['variant']}-{capture['view']}-shaded.rgba"
                shaded_identical += digest(directory / name) == digest(directory / name.replace("-guided-", "-native-"))
        for level in (1, 2):
            row = {"fixture": report["fixture"], "level": level, "sourceTriangles": report["sourceTriangles"]}
            for mode in ("native", "guided"):
                selected = [c for c in captures if c["variant"] == f"{mode}-lod{level}"]
                control = selected[0]
                fields = ("triangles", "targetTriangles", "budgetReached", "selectedCandidate", "candidateCount", "simplifyMs", "sourceDistanceRms", "normalRms", "surfaceColorRms", "selectionScore", "correctionNote", "colorsCorrected", "nativeBeltFallback", "hardEdgeSourceFallback")
                row[mode] = {k: control[k] for k in fields}
                row[mode]["gpuDetailLoss"] = max(worst(selected, "worstThinRegionLoss"), worst(selected, "worstComponentLoss"))
                row[mode]["gpuColorLoss"] = worst(selected, "worstColorRegionLoss", eligible="colorEvaluated")
                row[mode]["gpuRgbaRms"] = max(c["rmsPixelRgbaError"] for c in selected)
                row[mode]["gpuSilhouette"] = max(c["silhouetteMismatch"] for c in selected)
                row[mode]["screens"] = {str(size): {
                    "detailLoss": max(worst(selected, "worstThinRegionLoss", size), worst(selected, "worstComponentLoss", size)),
                    "colorLoss": worst(selected, "worstColorRegionLoss", size, "colorEvaluated")}
                    for size in (384, 192, 96)}
                guide = control.get("screenQuality")
                row[mode]["cpuGuide"] = guide if guide and guide["views"] > 0 else None
                row[mode]["candidates"] = control.get("budgetCandidates")
            rows.append(row)
        reports.append({k: report[k] for k in ("fixture", "sourceSha256", "meshName", "sourceTriangles", "unity", "gpu", "graphicsApi", "varyingVertexColors", "screenPartProposed", "screenPartTris", "screenPartAccepted")})
    sources = ("Editor/LodScreenValidation.cs", "Editor/LodScreenAcceptance.cs", "Editor/LodBudgetTriangleSimplifier.cs", "Editor/LodSmallParts.cs", "Editor/LodPipelineOps.cs", "Tests/Editor/LodScreenValidationTests.cs", "Tests/Editor/LodProjectVisualQualityTests.cs")
    root = Path(__file__).resolve().parents[1]
    originals = json.loads((directory / "original-source-hashes.json").read_text(encoding="utf-8-sig"))
    assert {r["name"]: r["sha256"] for r in originals} == {r["fixture"]: r["sourceSha256"] for r in reports}
    return {"tests": result, "testXmlSha256": digest(tests), "freshModels": 8, "freshViews": 80,
            "unchangedControlRawFiles": control_identical, "reports": reports, "rows": rows,
            "unchangedGuidedColorCoverageFiles": guided_identical, "unchangedGuidedShadedFiles": shaded_identical,
            "changedSelectedVariants": sum(r["native"]["selectedCandidate"] != r["guided"]["selectedCandidate"] for r in rows),
            "unchangedOriginalSources": originals,
            "rawSha256": hashes, "sourceSha256": {name: digest(root / name) for name in sources},
            "scope": "Fresh native control versus opt-in CPU screen-guided generation; 2 GPU views at 384px plus independent downsampled diagnostics. Small-part data is a separate source-only proposed-plan probe at 1080px, transition .25 and 8px part limit; generation comparison does not prune."}


def chart(directory, result):
    rows = result["rows"]
    fig, axes = plt.subplots(1, 3, figsize=(16, 12))
    labels = [f"{r['fixture']} · LOD{r['level']}" for r in rows]
    for column, field in enumerate(("triangles", "gpuDetailLoss", "gpuColorLoss")):
        axis = axes[column]
        for index, mode in enumerate(("native", "guided")):
            values = [r[mode][field] for r in rows]
            if column: values = [v*100 if v is not None else np.nan for v in values]
            axis.barh(np.arange(len(rows))+(index-.5)*.32, values, height=.3, label=mode)
            for row, value in enumerate(values):
                if value is None or np.isnan(value): axis.text(2, row+(index-.5)*.32, "n/a", va="center", fontsize=8)
        axis.set_yticks(np.arange(len(rows)), labels if column == 0 else [])
        axis.set_ylim(len(rows)-.5, -.5)
        axis.set_title(("Triangle count", "GPU worst local detail loss, %", "GPU worst RGBA boundary loss, %")[column])
        axis.grid(axis="x", alpha=.2)
        if column: axis.set_xlim(0, 105); axis.axvline(25, color="black", ls="--", lw=.7)
    axes[0].legend(loc="lower right")
    fig.suptitle("Actual project FBX · same requested budgets, fresh generation\nIndependent worst regional GPU losses at 384px / two views; n/a = no eligible authored color boundary", fontsize=14)
    fig.tight_layout(rect=(0, 0, 1, .94)); fig.savefig(directory / "screen-guided-errors.png", dpi=125); plt.close(fig)


def gallery(directory, result):
    selected = ("bench-colored", "fire-shield", "tent-colored", "wrench")
    fig, axes = plt.subplots(4, 3, figsize=(14, 16))
    for row, fixture in enumerate(selected):
        source = readback(directory / f"{fixture}-source-oblique-coverage.rgba")[:, :, 0] >= .5
        thin = source & ~morph(morph(source, 2, True), 2)
        data = next(r for r in result["rows"] if r["fixture"] == fixture and r["level"] == 2)
        for column, variant in enumerate(("source", "native-lod2", "guided-lod2")):
            picture = np.asarray(Image.open(directory / f"{fixture}-{variant}-oblique-shaded.png").convert("RGB")).copy()
            target = readback(directory / f"{fixture}-{variant}-oblique-coverage.rgba")[:, :, 0] >= .5
            picture[np.flipud(thin if column == 0 else thin & ~morph(target, 1))] = (65, 205, 220) if column == 0 else (240, 70, 70)
            axes[row, column].imshow(picture); axes[row, column].axis("off")
            count = data["sourceTriangles"] if column == 0 else data[("native", "guided")[column-1]]["triangles"]
            axes[row, column].set_title(f"{fixture} · {variant} · {count:,} tris", fontsize=11)
    fig.suptitle("Fresh actual project models · oblique LOD2\nSource thin pixels cyan / unsupported source thin pixels red; overlays include small regions excluded from the numerical guide", fontsize=13)
    fig.tight_layout(rect=(0, 0, 1, .94)); fig.savefig(directory / "screen-guided-models.png", dpi=125); plt.close(fig)


def paint_gallery(directory):
    fig, axes = plt.subplots(2, 3, figsize=(14, 11), layout="constrained")
    palette = plt.get_cmap("viridis").copy(); palette.set_bad("#1e2229")
    for row, fixture in enumerate(("bench-colored", "tent-colored")):
        source = readback(directory / f"{fixture}-source-oblique-color.rgba")
        coverage = readback(directory / f"{fixture}-source-oblique-coverage.rgba")[:, :, 0] >= .99999
        ranges = np.ptp(source[coverage], axis=0)
        channel = int(np.argmax(ranges))
        minimum = float(source[coverage, channel].min()); maximum = float(source[coverage, channel].max())
        for column, variant in enumerate(("source", "native-lod2", "guided-lod2")):
            colors = readback(directory / f"{fixture}-{variant}-oblique-color.rgba")
            mask = readback(directory / f"{fixture}-{variant}-oblique-coverage.rgba")[:, :, 0] >= .5
            field = np.ma.masked_where(~np.flipud(mask), np.flipud(colors[:, :, channel]))
            picture = axes[row, column].imshow(field, cmap=palette, vmin=minimum, vmax=maximum)
            axes[row, column].axis("off")
            axes[row, column].set_title(f"{fixture} · {variant}\nLinear {'RGBA'[channel]} channel", fontsize=11)
        fig.colorbar(picture, ax=axes[row].tolist(), shrink=.7, label="Same source channel scale")
    fig.suptitle("Actual authored vertex-color fields · fresh GPU readbacks\nEach model shows its largest-range source channel; RGBA metrics evaluate all four channels", fontsize=14)
    fig.savefig(directory / "screen-guided-colors.png", dpi=125); plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path); parser.add_argument("--baseline", type=Path, required=True); parser.add_argument("--tests", type=Path, required=True)
    args = parser.parse_args()
    result = audit(args.directory, args.baseline, args.tests)
    (args.directory / "screen-guided-audit.json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    chart(args.directory, result); gallery(args.directory, result); paint_gallery(args.directory)
    print(json.dumps({"tests": result["tests"]["passed"], "freshModels": result["freshModels"], "freshViews": result["freshViews"], "unchangedControlRawFiles": result["unchangedControlRawFiles"]}))
