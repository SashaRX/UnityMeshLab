"""Audit final far normal correction and actual 64px fire-shield lighting."""
import argparse
import json
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
from PIL import Image, ImageDraw
from render_lod_far_budget import audit, digest
from render_lod_far_refinement import BACKGROUND, compare_gpu, coverage, frame
from render_lod_screen_acceptance import readback


def graph(models, output):
    fire = next(m for m in models if m["fixture"] == "fire-shield")
    fit = fire["visibleNormals"]
    fig, axes = plt.subplots(2, 2, figsize=(14, 9))
    for i, stage in enumerate(("Before", "After")):
        x = np.arange(6)+(i-.5)*.34
        axes[0, 0].bar(x, fit["normalViews"+stage], width=.34, label=stage.lower())
        axes[0, 1].bar(x, fit["headlightViews"+stage], width=.34)
        y = np.arange(len(models))+(i-.5)*.34
        axes[1, 0].barh(y, [m[stage.lower()+"64"]["normalRms"] for m in models], height=.34)
        axes[1, 1].barh(y, [100*m[stage.lower()+"64"]["silhouetteMean"] for m in models], height=.34)
    labels = ("+X", "+Y", "+Z", "1,1,1", "1,2,-1", "-2,1,1")
    for axis, title in zip(axes[0], ("Fire shield: normal RMS, degrees", "Fire shield: headlight multiplier RMS")):
        axis.set_xticks(np.arange(6), labels); axis.set_title(title); axis.grid(axis="y", alpha=.2)
    for axis, title in zip(axes[1], ("All models: worst-view normal RMS, degrees", "All models: mean silhouette error, %")):
        axis.set_yticks(np.arange(len(models)), [m["fixture"] for m in models]); axis.invert_yaxis()
        axis.set_title(title); axis.grid(axis="x", alpha=.2)
    axes[0, 0].legend()
    fig.suptitle("Far normal correction at a 64px object extent\nUnchanged geometry and triangle budgets; six CPU views, independent GPU front/oblique verification")
    fig.tight_layout(rect=(0, 0, 1, .94)); fig.savefig(output, dpi=130); plt.close(fig)


def gallery(directory, previous, output):
    fixture = "fire-shield"
    canvas = Image.new("RGB", (1200, 670), "#20242a"); draw = ImageDraw.Draw(canvas)
    draw.text((18, 12), "Actual project fire shield: source 1850 triangles / previous and corrected LOD2 204 triangles", fill="white")
    draw.text((18, 32), "64px source extent in a padded frame, magnified 3x. Fixed geometry; diagnostic headlight, not project materials.", fill="white")
    for row, view in enumerate(("front", "oblique")):
        source_mask = coverage(directory, fixture, "source", view)
        ref_mask = frame(source_mask[:, :, None], source_mask)[:, :, 0]
        ref_raw = readback(directory/f"{fixture}-source-{view}-shaded.rgba")
        ref = frame(ref_raw-BACKGROUND*(1-source_mask[:, :, None]), source_mask)/np.maximum(ref_mask[:, :, None], 1e-20)
        for col, (label, folder, variant) in enumerate((("source", directory, "source"), ("before", previous, "far-lod2"), ("after", directory, "far-lod2"))):
            raw = readback(folder/f"{fixture}-{variant}-{view}-shaded.rgba")
            rgb = np.clip(frame(raw, source_mask)[:, :, :3], 0, 1)
            pic = Image.fromarray((rgb*255).astype(np.uint8)).resize((216, 216), Image.Resampling.NEAREST)
            x, y = 15+col*236, 95+row*280
            draw.text((x, y), view+" / "+label, fill="white"); canvas.paste(pic, (x, y+22))
        for col, (label, folder) in enumerate((("before light error", previous), ("after light error", directory)), 3):
            mask = coverage(folder, fixture, "far-lod2", view)
            cov = frame(mask[:, :, None], source_mask)[:, :, 0]
            raw = readback(folder/f"{fixture}-far-lod2-{view}-shaded.rgba")
            values = frame(raw-BACKGROUND*(1-mask[:, :, None]), source_mask)/np.maximum(cov[:, :, None], 1e-20)
            # Lighting error is measured only where both surfaces cover the pixel.
            delta = np.sqrt(np.mean((values[:, :, :3]-ref[:, :, :3])**2, axis=2))
            strength = np.clip(delta/.4, 0, 1)*np.minimum(cov, ref_mask)
            rgb = np.stack((strength, strength*.25, np.zeros_like(strength)), axis=2)
            pic = Image.fromarray((rgb*255).astype(np.uint8)).resize((216, 216), Image.Resampling.NEAREST)
            x, y = 15+col*236, 95+row*280
            draw.text((x, y), label, fill="white"); canvas.paste(pic, (x, y+22))
    draw.text((18, 640), "Error maps: black = no overlapping lighting error; brighter orange = larger linear RGB RMS (scale 0..0.4).", fill="white")
    canvas.save(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path); parser.add_argument("--previous", required=True, type=Path)
    parser.add_argument("--baseline-report", type=Path, default=Path("Documentation~/LOD_FAR_REFINEMENT_RESULTS.json"))
    parser.add_argument("--completion", type=Path, help="Fresh passed run completing the one opt-in GPU readback test")
    parser.add_argument("--output", required=True, type=Path); args = parser.parse_args()
    result = audit(args.directory, args.previous, args.completion)
    baseline = {m["fixture"]: m for m in json.loads(args.baseline_report.read_text(encoding="utf-8"))["models"]}
    result["baselineCommit"] = "408d985"
    result["lineage"] += " Compared with the committed silhouette/RGBA stage: every far color/coverage raw pass and triangle count is identical. Seven other far shaded controls are identical; fire shield changes only normals/tangent orientation."
    for model in result["models"]:
        name = model["fixture"]
        old = json.loads((args.previous/f"{name}-metrics.json").read_text(encoding="utf-8"))
        new = json.loads((args.directory/f"{name}-metrics.json").read_text(encoding="utf-8"))
        before = next(c for c in old["captures"] if c["variant"] == "far-lod2" and c["view"] == "oblique")
        after = next(c for c in new["captures"] if c["variant"] == "far-lod2" and c["view"] == "oblique")
        recorded = baseline[name]
        assert before["triangles"] == recorded["afterTriangles"] and before["farComparison"] == recorded["after64"]
        assert after["triangles"] == before["triangles"]
        for field in ("silhouetteMean", "silhouetteMax", "rgbaRms", "detailLoss", "colorLoss"):
            assert after["farComparison"][field] == before["farComparison"][field], (name, field)
        assert after["farComparison"]["normalRms"] <= before["farComparison"]["normalRms"]+1e-4
        for view in ("front", "oblique"):
            for kind in ("color", "coverage", "shaded"):
                suffix = f"{view}-{kind}.rgba"
                assert digest(args.previous/f"{name}-far-lod2-{suffix}") == recorded["farRawSha256"][suffix]
                path = f"{name}-far-lod1-{suffix}"
                assert digest(args.directory/path) == digest(args.previous/path), "Near changed: "+path
                if kind != "shaded" or name != "fire-shield":
                    path = f"{name}-far-lod2-{suffix}"
                    assert digest(args.directory/path) == digest(args.previous/path), "Far control changed: "+path
        fit = after["attributeFit"]["visibleNormals"]
        if fit["accepted"]:
            for field, tolerance in (("normalViews", 1e-4), ("headlightViews", 1e-6)):
                assert len(fit[field+"Before"]) == len(fit[field+"After"]) == 6
                assert all(a <= b+tolerance for a, b in zip(fit[field+"After"], fit[field+"Before"]))
            assert fit["normalAfter"] < fit["normalBefore"] and fit["headlightAfter"] < fit["headlightBefore"]
        model.update(beforeTriangles=before["triangles"], afterTriangles=after["triangles"], before64=before["farComparison"],
                     after64=after["farComparison"], attributeFit=after["attributeFit"], visibleNormals=fit,
                     gpu64=compare_gpu(args.directory, args.previous, name))
        if name == "fire-shield":
            assert fit["accepted"] and model["afterTriangles"] == 204
            assert after["farComparison"]["normalRms"] < before["farComparison"]["normalRms"]
            assert all(v["after"]["shadingRms"] < v["before"]["shadingRms"] for v in model["gpu64"])
    args.output.with_suffix(".json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    graph(result["models"], args.output.with_name(args.output.name+"_ERRORS.png"))
    gallery(args.directory, args.previous, args.output.with_name(args.output.name+"_MODELS.png"))
    print(json.dumps({"tests": result["tests"], "counts": {m["fixture"]: m["afterTriangles"] for m in result["models"]}}))


if __name__ == "__main__": main()
