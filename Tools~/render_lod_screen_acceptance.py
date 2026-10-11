"""Audit C# screen-acceptance results against existing actual Unity GPU readbacks."""
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


def readback(path):
    raw = path.read_bytes()
    size = np.frombuffer(raw[:8], dtype="<i4")
    assert tuple(size) == (384, 384) and len(raw) == 8 + 384 * 384 * 16
    return np.frombuffer(raw, dtype="<f4", offset=8).reshape(384, 384, 4)


def audit(directory, baseline, tests):
    result = ET.parse(tests).getroot().attrib
    assert int(result["total"]) > 0 and int(result["failed"]) == int(result["skipped"]) == 0
    previous = ET.parse(baseline / "final.xml").getroot().attrib
    assert int(previous["passed"]) == 183 and previous["result"] == "Passed"
    files = sorted(directory.glob("*-screen.json"))
    assert len(files) == 8
    evaluations, sources, raw_hashes = [], [], {}
    for path in files:
        evaluation = json.loads(path.read_text(encoding="utf-8"))
        fixture = evaluation["fixture"]
        source_path = baseline / evaluation["captureFile"]
        original = json.loads(source_path.read_text(encoding="utf-8"))
        assert evaluation["sourceSha256"] == original["sourceSha256"]
        assert original["graphicsApi"] != "Null" and original["gpu"] and original["resolution"] == 384
        assert len(evaluation["rows"]) == len(original["captures"]) == 22
        lookup = {(c["variant"], c["view"]): c for c in original["captures"]}
        seen = set()
        for row in evaluation["rows"]:
            key = row["variant"], row["view"]
            assert key not in seen and row["triangles"] == lookup[key]["triangles"]
            seen.add(key)
            assert {s["width"] for s in row["screens"]} == {384, 192, 96}
            for screen in row["screens"]:
                assert screen["height"] == screen["width"]
                for field in ("thinLoss", "worstThinRegionLoss", "worstComponentLoss", "colorBoundaryLoss", "worstColorRegionLoss"):
                    assert 0 <= screen[field] <= 1
                if row["variant"] == "source":
                    assert screen["lostThinPixels"] == screen["lostComponents"] == screen["lostColorPairs"] == 0
                    assert screen["detailAccepted"]
                    assert screen["colorAccepted"] == screen["colorEvaluated"]
            for kind in ("color", "coverage"):
                raw_path = baseline / f"{fixture}-{row['variant']}-{row['view']}-{kind}.rgba"
                readback(raw_path)
                raw_hashes[raw_path.name] = digest(raw_path)
        assert seen == set(lookup)
        sources.append({k: original[k] for k in ("fixture", "meshName", "sourceSha256", "unity", "gpu", "graphicsApi", "resolution", "varyingVertexColors")}
                       | {"captureMetadataSha256": digest(source_path)})
        evaluations.append(evaluation)
    fresh = []
    for path in sorted(directory.glob("*-metrics.json")):
        regenerated = json.loads(path.read_text(encoding="utf-8"))
        reference = next(e for e in evaluations if e["fixture"] == regenerated["fixture"])
        assert regenerated["sourceSha256"] == reference["sourceSha256"] and len(regenerated["captures"]) == 22
        rows = {(r["variant"], r["view"]): r for r in reference["rows"]}
        identical = 0
        for capture in regenerated["captures"]:
            expected = rows[capture["variant"], capture["view"]]
            assert capture["triangles"] == expected["triangles"] and len(capture["screenAcceptance"]) == 3
            for kind in ("color", "coverage"):
                name = f"{regenerated['fixture']}-{capture['variant']}-{capture['view']}-{kind}.rgba"
                readback(directory / name)
                identical += digest(directory / name) == raw_hashes[name]
        fresh.append(dict(fixture=regenerated["fixture"], captures=22,
                          identicalRawReadbacks=identical, rawReadbacks=44, metadataSha256=digest(path)))
    assert len(fresh) == 1 and fresh[0]["fixture"] == "tent-colored"
    root = Path(__file__).resolve().parent.parent
    code = {name: digest(root / name) for name in ("Editor/LodScreenAcceptance.cs", "Tests/Editor/LodScreenAcceptanceTests.cs",
                                                 "Tests/Editor/LodProjectVisualQualityTests.cs", "Tests/Editor/LodVisualQualityTests.cs")}
    report = dict(tests=result, captureTests=previous, models=8, variantViews=176,
                  scaleComparisons=528, rawReadbacks=len(raw_hashes), sources=sources,
                  freshRegeneration=fresh, acceptanceSourceSha256=code,
                  lineage="Re-evaluated existing linear GPU readbacks from the 8034110 native/matched run; no new simplification or GPU rendering in the replay.",
                  settings=dict(thinRadiusPixels=2, correspondenceTolerancePixels=1, minimumRegionPixels=8,
                                minimumBoundaryPairs=4, colorInteriorMarginPixels=1,
                                sharpRgbaContrast=.15, absoluteRgbaTolerance=.1, maximumRegionLoss=.25),
                  limits="Visible image regions, not mesh component identities or categorical labels. Box-filtered footprints from a fixed diagnostic camera; no original materials or transition validation. No eligible region means not evaluated.",
                  rawSha256=raw_hashes, evaluations=evaluations)
    (directory / "screen-acceptance-audit.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    return evaluations, report


def charts(directory, evaluations):
    modes = (("chains", "Coarsened belt", "#2a9d8f"), ("native", "Native source budgets", "#d48545"),
             ("matched", "Native matched counts", "#9255aa"))
    keys = [(e["fixture"], level) for e in evaluations for level in (1, 2)]
    lookup = {(e["fixture"], row["variant"], row["view"], s["width"]): s
              for e in evaluations for row in e["rows"] for s in row["screens"]}
    fig, axes = plt.subplots(1, 6, figsize=(28, 11))
    for column, width in enumerate((384, 192, 96)):
        for kind in range(2):
            axis = axes[column * 2 + kind]
            for offset, (tag, label, color) in enumerate(modes):
                values = []
                for fixture, level in keys:
                    screens = [lookup[fixture, f"{tag}-lod{level}", view, width] for view in ("front", "oblique")]
                    eligible = [s for s in screens if s["colorEvaluated"]] if kind else screens
                    value = max((s["worstColorRegionLoss"] if kind else max(s["worstThinRegionLoss"], s["worstComponentLoss"]) for s in eligible), default=np.nan)
                    values.append(value * 100)
                axis.barh(np.arange(len(keys)) + (offset - 1) * .24, values, height=.22, color=color, label=label)
                if kind and offset == 0:
                    for index, value in enumerate(values):
                        if np.isnan(value): axis.text(2, index, "n/a", color="#777777", fontsize=9, va="center")
            axis.axvline(25, color="#c0392b", linestyle="--", linewidth=1)
            axis.set_xlim(0, 105); axis.invert_yaxis(); axis.grid(axis="x", alpha=.2)
            axis.set_title(f"{width}px frame\nWorst {'sharp RGBA boundary' if kind else 'visible detail'} loss, %")
            axis.set_yticks(np.arange(len(keys)), [f"{f} · LOD{l}" for f, l in keys] if column == kind == 0 else [])
            axis.set_ylim(len(keys) - .5, -.5)
    axes[0].legend(loc="lower right", fontsize=9)
    fig.suptitle("Local acceptance on actual project GPU readbacks · lower is better\nWorst eligible region / two views; dashed line = experimental 25% loss guide; n/a = no eligible color boundary", fontsize=16)
    fig.tight_layout(rect=(0, 0, 1, .92)); fig.savefig(directory / "screen-acceptance-errors.png", dpi=125); plt.close(fig)


def morph(mask, radius, erosion=False):
    padded = np.pad(mask, radius, constant_values=False)
    windows = np.lib.stride_tricks.sliding_window_view(padded, (2 * radius + 1, 2 * radius + 1))
    return windows.all(axis=(-1, -2)) if erosion else windows.any(axis=(-1, -2))


def gallery(directory, baseline, evaluations):
    selected = ("bench-colored", "bench", "fire-shield", "tent-colored")
    fig, axes = plt.subplots(4, 4, figsize=(15, 15))
    for row, fixture in enumerate(selected):
        view = "oblique"
        source = readback(baseline / f"{fixture}-source-{view}-coverage.rgba")[:, :, 0] >= .5
        thin = source & ~morph(morph(source, 2, True), 2)
        evaluation = next(e for e in evaluations if e["fixture"] == fixture)
        for column, variant in enumerate(("source", "chains-lod2", "matched-lod2", "native-lod2")):
            picture = np.asarray(Image.open(baseline / f"{fixture}-{variant}-{view}-shaded.png").convert("RGB")).copy()
            # Raw readbacks are bottom-up; exported review PNG uses the opposite orientation.
            target = readback(baseline / f"{fixture}-{variant}-{view}-coverage.rgba")[:, :, 0] >= .5
            missing = thin & ~morph(target, 1)
            picture[np.flipud(thin if variant == "source" else missing)] = (65, 205, 220) if variant == "source" else (240, 70, 70)
            capture = next(r for r in evaluation["rows"] if r["variant"] == variant and r["view"] == view)
            metric = next(s for s in capture["screens"] if s["width"] == 384)
            loss = max(metric["worstThinRegionLoss"], metric["worstComponentLoss"]) * 100
            axes[row, column].imshow(picture); axes[row, column].axis("off")
            axes[row, column].set_title(f"{fixture} · {variant}\n{capture['triangles']:,} tris · worst detail loss {loss:.1f}%", fontsize=10)
    fig.suptitle("Actual project models · source thin regions cyan / unsupported thin pixels red\n384px diagnostics, one-pixel coverage tolerance; colors show all thin pixels, metrics exclude regions below eight pixels", fontsize=14)
    fig.tight_layout(rect=(0, 0, 1, .94)); fig.savefig(directory / "screen-acceptance-models.png", dpi=125); plt.close(fig)


def color_gallery(directory, baseline, evaluations):
    fig, axes = plt.subplots(2, 4, figsize=(15, 10))
    for row, fixture in enumerate(("bench-colored", "tent-colored")):
        evaluation = next(e for e in evaluations if e["fixture"] == fixture)
        for column, variant in enumerate(("source", "chains-lod2", "matched-lod2", "native-lod2")):
            capture = next(r for r in evaluation["rows"] if r["variant"] == variant and r["view"] == "oblique")
            metric = next(s for s in capture["screens"] if s["width"] == 384)
            axes[row, column].imshow(Image.open(baseline / f"{fixture}-{variant}-oblique-color.png")); axes[row, column].axis("off")
            loss = f"{metric['worstColorRegionLoss'] * 100:.1f}%" if metric["colorEvaluated"] else "n/a"
            axes[row, column].set_title(f"{fixture} · {variant}\n{capture['triangles']:,} tris · worst boundary loss {loss}", fontsize=10)
    fig.suptitle("Actual authored vertex-color fields · same GPU capture lineage\nRGB displayed; local sharp-boundary checks include alpha and are independent of global RGBA RMS", fontsize=14)
    fig.tight_layout(rect=(0, 0, 1, .91)); fig.subplots_adjust(hspace=.3)
    fig.savefig(directory / "screen-acceptance-colors.png", dpi=125); plt.close(fig)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path); parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--tests", type=Path, required=True)
    args = parser.parse_args()
    evaluations, report = audit(args.directory, args.baseline, args.tests)
    charts(args.directory, evaluations); gallery(args.directory, args.baseline, evaluations); color_gallery(args.directory, args.baseline, evaluations)
    print(json.dumps({k: report[k] for k in ("tests", "models", "variantViews", "scaleComparisons", "rawReadbacks")}, indent=2))
