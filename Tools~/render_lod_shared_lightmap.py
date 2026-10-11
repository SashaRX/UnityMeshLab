"""Audit actual shared-atlas UV2 candidates in source-framed 64px GPU views."""
import argparse
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image, ImageDraw
from render_lod_far_budget import digest
from render_lod_far_refinement import BACKGROUND, coverage, frame
from render_lod_screen_acceptance import readback


def field(directory, variant, view, mode, source_mask):
    mask = coverage(directory, "fire-shield", variant, view)
    cov = frame(mask[:, :, None], source_mask)[:, :, 0]
    raw = readback(directory/f"fire-shield-{variant}-{view}-{mode}.rgba")
    values = frame(raw-BACKGROUND*(1-mask[:, :, None]), source_mask)/np.maximum(cov[:, :, None], 1e-20)
    return values, cov, frame(raw, source_mask)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--variant", default="uv2-min16")
    parser.add_argument("--tests", default="integration.xml")
    args = parser.parse_args()
    test = ET.parse(args.directory/args.tests).getroot()
    assert test.get("result") == "Passed" and test.get("failed") == test.get("skipped") == "0"
    report = json.loads((args.directory/"fire-shield-shared-lightmap.json").read_text())
    assert report["sourceTriangles"] == 1850 and report["targetTriangles"] == 204
    assert report["transfer"]["accepted"] and report["transfer"]["bakedField"]
    assert report["integratedTransfer"]["accepted"] and report["integratedTransfer"]["verticesAfter"] == 288
    report["selectedVariant"] = args.variant
    report["tests"] = {k: test.get(k) for k in ("total","passed","failed","skipped","duration")}
    for view in ("front", "oblique"):
        for kind in ("coverage", "color", "shaded"):
            name = f"fire-shield-far-lod2-{view}-{kind}.rgba"
            assert digest(args.directory/name) == digest(args.baseline/name)
            assert digest(args.directory/f"fire-shield-{args.variant}-{view}-{kind}.rgba") == digest(args.baseline/name)
        assert digest(args.directory/f"fire-shield-{args.variant}-{view}-coverage.rgba") == digest(args.directory/f"fire-shield-far-lod2-{view}-coverage.rgba")
    canvas = Image.new("RGB", (1200, 1240), "#20242a"); draw = ImageDraw.Draw(canvas)
    draw.text((18, 12), "Actual Level_Plan / TransformerStation_Building / FireShield: shared Bakery atlas 22", fill="white")
    draw.text((18, 32), "Source 1850 triangles; both LOD2 variants 204. Object extent 64px, magnified 3x. Baked irradiance, original PBR omitted.", fill="white")
    report["gpu64"] = []
    row = 0
    for view in ("front", "oblique"):
        source_mask = coverage(args.directory, "fire-shield", "source", view)
        for mode in ("baked-color", "baked-directional"):
            ref, ref_cov, _ = field(args.directory, "source", view, mode, source_mask)
            errors = []
            for col, variant in enumerate(("source", "far-lod2", args.variant)):
                values, cov, raw = field(args.directory, variant, view, mode, source_mask)
                delta = np.sqrt(np.mean((values[:, :, :3]-ref[:, :, :3])**2, axis=2))
                weight = np.minimum(cov, ref_cov)
                rms = float(np.sqrt(np.sum(delta**2*weight)/weight.sum()))
                rgb = np.clip(raw[:, :, :3], 0, 1)
                rgb = np.where(rgb <= .0031308, rgb*12.92, 1.055*rgb**(1/2.4)-.055)
                pic = Image.fromarray((rgb*255).astype(np.uint8)).resize((216,216), Image.Resampling.NEAREST)
                x, y = 15+col*236, 90+row*280
                draw.text((x, y), f"{view} / {mode} / {variant}", fill="white"); canvas.paste(pic, (x,y+22))
                if col > 0:
                    strength = np.clip(delta/.3, 0, 1)*weight
                    error_rgb = np.stack((strength,strength*.25,np.zeros_like(strength)),axis=2)
                    error_image = Image.fromarray((error_rgb*255).astype(np.uint8)).resize((216,216), Image.Resampling.NEAREST)
                    errors.append((rms,error_image))
            before, after = errors[0][0], errors[1][0]
            assert after < before, (view, mode, before, after)
            report["gpu64"].append(dict(view=view,mode=mode,beforeRms=before,afterRms=after,improvement=1-after/before))
            for col, (rms, pic) in enumerate(errors, 3):
                x, y = 15+col*236, 90+row*280
                draw.text((x,y), f"light error RMS {rms:.5f}", fill="white"); canvas.paste(pic,(x,y+22))
            row += 1
    draw.text((18,1215), "Heatmap: covered-overlap linear RGB RMS, scale 0..0.3. Geometry/coverage unchanged; direction uses the scene normal transform.", fill="white")
    report["rawSha256"] = {p.name: digest(p) for p in args.directory.glob("fire-shield-*baked-*.rgba")}
    args.output.with_suffix(".json").write_text(json.dumps(report,indent=2)+"\n")
    canvas.save(args.output.with_name(args.output.name+"_MODELS.png"))
    print(json.dumps(report["gpu64"]))


if __name__ == "__main__": main()
