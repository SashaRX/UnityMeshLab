"""Audit source-based far budgets and compare actual project models at 64 pixels."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw
from render_lod_screen_acceptance import readback


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def audit(directory, previous):
    test = ET.parse(directory / "final.xml").getroot().attrib
    assert test["result"] == "Passed" and int(test["total"]) == int(test["passed"]) > 0
    assert int(test["failed"]) == int(test["skipped"]) == 0
    paths = sorted(directory.glob("*-metrics.json"))
    assert len(paths) == 8
    models = []
    for path in paths:
        report = json.loads(path.read_text(encoding="utf-8"))
        old = json.loads((previous / path.name).read_text(encoding="utf-8"))
        fixture = report["fixture"]
        assert report["sourceSha256"] == old["sourceSha256"] == digest(Path(report["sourcePath"]))
        assert report["graphicsApi"] != "Null" and report["resolution"] == 384
        captures = report["captures"]
        assert len(captures) == 10
        by_name = {c["variant"]: c for c in captures if c["view"] == "oblique"}
        assert set(by_name) == {"source", "native-lod1", "native-lod2", "far-lod1", "far-lod2"}
        for name, capture in by_name.items():
            comparison = capture["farComparison"]
            assert comparison["views"] == 6 and comparison["objectPixels"] == 64 and comparison["resolution"] == 72
            assert all(np.isfinite(comparison[f]) for f in ("silhouetteMean", "silhouetteMax", "normalRms", "rgbaRms", "detailLoss", "colorLoss"))
            if name == "source":
                assert comparison["silhouetteMax"] == comparison["normalRms"] == comparison["rgbaRms"] == 0
            if name.startswith("native-") or name == "far-lod1":
                assert not capture["screenBudgetRelaxed"]
                assert capture["missingHardEdges"] == capture["missingProtectedTriangles"] == capture["missingPatchInterfaces"] == 0
            for view in ("front", "oblique"):
                for kind in ("color", "coverage", "shaded"):
                    raw = directory / f"{fixture}-{name}-{view}-{kind}.rgba"
                    readback(raw)
                    if name in ("source", "native-lod1", "native-lod2"):
                        assert digest(raw) == digest(previous / raw.name), "Fresh control changed: " + raw.name
        native, far = by_name["native-lod2"], by_name["far-lod2"]
        assert far["screenBudgetRelaxed"] and not far["hardEdgeSourceFallback"]
        assert far["budgetReached"] and far["triangles"] <= far["targetTriangles"]
        assert far["triangles"] >= far["targetTriangles"] - max(2, np.ceil(far["targetTriangles"]*.05))
        assert far["screenQuality"]["objectPixels"] == 64
        assert all(c["screenQuality"]["objectPixels"] == 64 for c in far["budgetCandidates"])
        source_hashes, native_hashes, far_hashes = {}, {}, {}
        for view in ("front", "oblique"):
            for kind in ("color", "coverage", "shaded"):
                suffix = f"{view}-{kind}.rgba"
                source_hashes[suffix] = digest(directory / f"{fixture}-source-{suffix}")
                native_hashes[suffix] = digest(directory / f"{fixture}-native-lod2-{suffix}")
                far_hashes[suffix] = digest(directory / f"{fixture}-far-lod2-{suffix}")
        models.append({"fixture": fixture, "sourceSha256": report["sourceSha256"],
            "sourceTriangles": report["sourceTriangles"], "targetTriangles": far["targetTriangles"],
            "previousTriangles": native["triangles"], "farTriangles": far["triangles"],
            "sourceReduction": report["sourceTriangles"] / far["triangles"],
            "native64": native["farComparison"], "far64": far["farComparison"],
            "candidate": far["selectedCandidate"], "candidates": far["budgetCandidates"],
            "missingExactEdges": far["missingHardEdges"], "missingExactFaces": far["missingProtectedTriangles"],
            "missingExactInterfaces": far["missingPatchInterfaces"],
            "normalSurfaceRms": far["normalRms"], "sourceDistanceRms": far["sourceDistanceRms"],
            "surfaceColorRms": far["surfaceColorRms"], "surfaceColorMax": far["surfaceColorMax"],
            "correction": far["correctionNote"], "normalsCorrected": far["normalsCorrected"], "colorsCorrected": far["colorsCorrected"],
            "nativeGenerateSeconds": native["simplifyMs"]/1000, "farGenerateSeconds": far["simplifyMs"]/1000,
            "sourceRawSha256": source_hashes, "nativeRawSha256": native_hashes, "farRawSha256": far_hashes})
    return {"tests": {k: test[k] for k in ("total", "passed", "failed", "skipped", "duration")},
            "lineage": "Both controls and far candidates freshly generated. Eight original FBX hashes unchanged; all fresh source/native raw passes match the preceding eight-model baseline. CPU quality comparison uses six source-local depth-tested views at 64px maximum object extent. Quality is ranked, not certified by reaching a budget.",
            "models": models}


def graph(result, output):
    rows = result["models"]
    fig, axes = plt.subplots(1, 4, figsize=(17, 8))
    for i, mode in enumerate(("native", "far")):
        y = np.arange(len(rows))+(i-.5)*.32
        values = [100*r["previousTriangles" if i == 0 else "farTriangles"]/r["sourceTriangles"] for r in rows]
        axes[0].barh(y, values, height=.3, label=mode)
        for col, field in enumerate(("silhouetteMean", "normalRms", "rgbaRms"), 1):
            values = [r[mode+"64"][field]*(100 if col == 1 else 1) if col != 3 or r[mode+"64"]["rgbaEvaluated"] else np.nan for r in rows]
            axes[col].barh(y, values, height=.3)
            for row, value in enumerate(values):
                if np.isnan(value): axes[col].text(0, y[row], "n/a", va="center", fontsize=8)
    for col, axis in enumerate(axes):
        axis.set_yticks(np.arange(len(rows)), [r["fixture"] for r in rows] if col == 0 else [])
        axis.set_ylim(len(rows)-.5, -.5); axis.grid(axis="x", alpha=.2)
        axis.set_title(("Source triangles retained, %", "Mean coverage error, %", "Worst-view normal RMS, degrees", "Worst-view linear RGBA RMS")[col], fontsize=11)
    axes[0].axvline(100/9, color="black", ls="--", lw=1); axes[0].legend()
    fig.suptitle("Actual project models: source-based LOD2 at approximately 1/9\nFresh native control / far screen budget; both evaluated in six CPU views at a 64px object extent", fontsize=14)
    fig.tight_layout(rect=(0, 0, 1, .93)); fig.savefig(output, dpi=130); plt.close(fig)


def gallery(directory, result, output):
    canvas = Image.new("RGB", (1040, 80+230*len(result["models"])), "#20242a")
    draw = ImageDraw.Draw(canvas)
    draw.text((18, 12), "Actual project meshes: source / native LOD2 / far LOD2 / source coverage difference", fill="white")
    draw.text((18, 32), "64px maximum object extent, shown 3x for inspection. Red: missing coverage; cyan: added coverage.", fill="white")
    draw.text((18, 52), "Fresh GPU headlight previews downsampled in source framing; project materials/cameras are not evaluated.", fill="white")
    for row, model in enumerate(result["models"]):
        fixture = model["fixture"]
        coverage = readback(directory / f"{fixture}-source-oblique-coverage.rgba")[:, :, 0]
        ys, xs = np.where(coverage >= .5); box = (xs.min(), ys.min(), xs.max()+1, ys.max()+1)
        scale = 64 / max(box[2]-box[0], box[3]-box[1])
        size = (max(1, round((box[2]-box[0])*scale)), max(1, round((box[3]-box[1])*scale)))
        def fit(picture):
            small = picture.crop(box).resize(size, Image.Resampling.BOX).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            tile = Image.new("RGB", (72, 72), "#212121"); tile.paste(small, ((72-size[0])//2, (72-size[1])//2))
            return tile.resize((216,216), Image.Resampling.NEAREST)
        for col, variant in enumerate(("source", "native-lod2", "far-lod2")):
            raw = readback(directory / f"{fixture}-{variant}-oblique-shaded.rgba")
            picture = Image.fromarray(np.uint8(np.clip(raw[:, :, :3],0,1)*255))
            count = model[("sourceTriangles", "previousTriangles", "farTriangles")[col]]
            x, y = 18+col*255, 80+row*230
            draw.text((x, y), f"{fixture} | {count} tris", fill="white"); canvas.paste(fit(picture), (x, y+16))
        target = readback(directory / f"{fixture}-far-lod2-oblique-coverage.rgba")[:, :, 0]
        delta = target-coverage
        difference = np.full((384,384,3), 32, dtype=np.uint8)
        difference[delta < -.25] = (250,65,65); difference[delta > .25] = (50,210,240)
        x, y = 18+3*255, 80+row*230
        draw.text((x,y), "far vs source coverage", fill="white"); canvas.paste(fit(Image.fromarray(difference)), (x,y+16))
    canvas.save(output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--previous", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    result = audit(args.directory, args.previous)
    args.output.with_suffix(".json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    graph(result, args.output.with_name(args.output.name+"_ERRORS.png"))
    gallery(args.directory, result, args.output.with_name(args.output.name+"_MODELS.png"))
    colors(args.directory, result, args.output.with_name(args.output.name+"_COLORS.png"))
    print(json.dumps({"passed": result["tests"]["passed"], "models": len(result["models"]),
                      "farCounts": {m["fixture"]: m["farTriangles"] for m in result["models"]}}))


def colors(directory, result, output):
    rows = [r for r in result["models"] if r["far64"]["rgbaEvaluated"]]
    canvas = Image.new("RGB", (820, 85+255*len(rows)), "#20242a")
    draw = ImageDraw.Draw(canvas)
    draw.text((18,12), "Actual authored vertex RGB: source / native LOD2 / far LOD2", fill="white")
    draw.text((18,32), "64px maximum source extent, shown 3x; linear RGB clamped for display. All RGBA metrics remain Float32.", fill="white")
    draw.text((18,52), "Fresh GPU color/coverage passes; these are attributes, not original project material rendering.", fill="white")
    for row, model in enumerate(rows):
        fixture = model["fixture"]
        source = readback(directory / f"{fixture}-source-oblique-coverage.rgba")[:, :, 0]
        ys, xs = np.where(source >= .5); box = (xs.min(),ys.min(),xs.max()+1,ys.max()+1)
        scale = 64/max(box[2]-box[0],box[3]-box[1]); size = (max(1,round((box[2]-box[0])*scale)),max(1,round((box[3]-box[1])*scale)))
        for col, name in enumerate(("source", "native-lod2", "far-lod2")):
            color = readback(directory / f"{fixture}-{name}-oblique-color.rgba")[:, :, :3]
            coverage = readback(directory / f"{fixture}-{name}-oblique-coverage.rgba")[:, :, 0, None]
            picture = Image.fromarray(np.uint8(np.clip(color*coverage+.13*(1-coverage),0,1)*255))
            small = picture.crop(box).resize(size, Image.Resampling.BOX).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            tile = Image.new("RGB",(72,72),"#212121"); tile.paste(small,((72-size[0])//2,(72-size[1])//2))
            x,y = 18+col*270,85+row*255
            draw.text((x,y), f"{fixture} | {name}", fill="white"); canvas.paste(tile.resize((216,216),Image.Resampling.NEAREST),(x,y+22))
    canvas.save(output)


if __name__ == "__main__":
    main()
