"""Audit fresh far-footprint runs and show actual project meshes at 64 pixels."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET

import numpy as np
from PIL import Image, ImageDraw
from render_lod_screen_acceptance import readback


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def audit(directories, previous):
    tests, rows = [], []
    for directory in directories:
        test = ET.parse(directory / "final.xml").getroot().attrib
        assert test["result"] == "Passed" and int(test["passed"]) > 0
        assert int(test["total"]) == int(test["passed"])
        assert int(test["failed"]) == int(test["skipped"]) == 0
        tests.append({k: test[k] for k in ("total", "passed", "failed", "skipped", "duration")})
        for path in sorted(directory.glob("*-metrics.json")):
            report = json.loads(path.read_text(encoding="utf-8"))
            old = json.loads((previous / path.name).read_text(encoding="utf-8"))
            assert report["sourceSha256"] == old["sourceSha256"] == digest(Path(report["sourcePath"]))
            assert report["resolution"] == 384 and report["graphicsApi"] != "Null"
            assert len(report["captures"]) == 10
            captures = {c["variant"]: c for c in report["captures"] if c["view"] == "oblique"}
            old_captures = {c["variant"]: c for c in old["captures"] if c["view"] == "oblique"}
            row = {"fixture": report["fixture"], "sourceTriangles": report["sourceTriangles"],
                   "sourceSha256": report["sourceSha256"], "unity": report["unity"],
                   "gpu": report["gpu"], "graphicsApi": report["graphicsApi"], "levels": []}
            for level in (1, 2):
                name = f"guided-lod{level}"
                capture = captures[name]
                assert capture["missingHardEdges"] == capture["missingProtectedTriangles"] == capture["missingPatchInterfaces"] == 0
                expected = 248 if level == 1 else 64
                assert capture["screenQuality"]["objectPixels"] == expected
                assert capture["screenQuality"]["resolution"] == expected + 8
                assert capture["screenQuality"]["views"] == 6
                assert all(c["screenQuality"]["objectPixels"] == expected for c in capture["budgetCandidates"])
                equal = all(digest(directory / f"{report['fixture']}-{name}-{view}-{kind}.rgba") ==
                            digest(previous / f"{report['fixture']}-{name}-{view}-{kind}.rgba")
                            for view in ("front", "oblique") for kind in ("color", "coverage", "shaded"))
                row["levels"].append({"level": level, "triangles": capture["triangles"],
                    "targetTriangles": capture["targetTriangles"], "selectedCandidate": capture["selectedCandidate"],
                    "previousTriangles": old_captures[name]["triangles"],
                    "rawCapturesIdenticalToPrevious": equal,
                    "previousGuide": old_captures[name]["screenQuality"], "guide": capture["screenQuality"]})
            row["nativeGenerateSeconds"] = captures["native-lod1"]["simplifyMs"] / 1000
            row["guidedGenerateSeconds"] = captures["guided-lod1"]["simplifyMs"] / 1000
            row["previousGuidedGenerateSeconds"] = old_captures["guided-lod1"]["simplifyMs"] / 1000
            rows.append(row)
    return {"tests": tests, "note": "Fresh generations; CPU guide uses six views. GPU illustration is a source-framed resize of fresh 384px captures, not a project camera screenshot. Timings are single runs.", "models": rows}


def gallery(directories, audit_result, output):
    width, row_height = 1050, 230
    canvas = Image.new("RGB", (width, 95 + row_height * len(audit_result["models"])), "#20242a")
    draw = ImageDraw.Draw(canvas)
    draw.text((18, 12), "Actual project models: LOD0 / control LOD2 / guided LOD2", fill="white")
    draw.text((18, 32), "Object maximum extent 64px; left: native size, right: 2x nearest-neighbor inspection.", fill="white")
    draw.text((18, 52), "RGB = authored vertex color; original project materials/cameras are not evaluated.", fill="white")
    for row, model in enumerate(audit_result["models"]):
        directory = next(d for d in directories if (d / f"{model['fixture']}-metrics.json").exists())
        fixture = model["fixture"]
        source_coverage = readback(directory / f"{fixture}-source-oblique-coverage.rgba")[:, :, 0]
        ys, xs = np.where(source_coverage >= .5)
        box = (xs.min(), ys.min(), xs.max()+1, ys.max()+1)
        scale = 64 / max(box[2]-box[0], box[3]-box[1])
        size = (max(1, round((box[2]-box[0])*scale)), max(1, round((box[3]-box[1])*scale)))
        for col, variant in enumerate(("source", "native-lod2", "guided-lod2")):
            color = readback(directory / f"{fixture}-{variant}-oblique-color.rgba")
            coverage = readback(directory / f"{fixture}-{variant}-oblique-coverage.rgba")[:, :, 0]
            rgb = np.clip(color[:, :, :3], 0, 1)
            # Composite in the same source framing before area downsampling.
            picture = Image.fromarray(np.uint8(np.clip(rgb*coverage[:, :, None]+.13*(1-coverage[:, :, None]), 0, 1)*255))
            small = picture.crop(box).resize(size, Image.Resampling.BOX).transpose(Image.Transpose.FLIP_TOP_BOTTOM)
            tile = Image.new("RGB", (72, 72), "#212121")
            tile.paste(small, ((72-size[0])//2, (72-size[1])//2))
            x, y = 20+col*345, 95+row*row_height
            count = model["sourceTriangles"] if col == 0 else model["levels"][1]["triangles"]
            draw.text((x, y), f"{fixture} | {variant} | {count} tris", fill="white")
            canvas.paste(tile, (x, y+38))
            canvas.paste(tile.resize((144,144), Image.Resampling.NEAREST), (x+90, y+28))
    canvas.save(output)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directories", nargs="+", type=Path)
    parser.add_argument("--previous", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--focused-tests", type=Path)
    args = parser.parse_args()
    result = audit(args.directories, args.previous)
    xml_paths = [d / "final.xml" for d in args.directories]
    if args.focused_tests:
        tree = ET.parse(args.focused_tests).getroot()
        assert tree.attrib["result"] == "Passed"
        assert int(tree.attrib["total"]) == int(tree.attrib["passed"]) > 0
        assert int(tree.attrib["failed"]) == int(tree.attrib["skipped"]) == 0
        result["focusedTests"] = {k: tree.attrib[k] for k in ("total", "passed", "failed", "skipped", "duration")}
        xml_paths.append(args.focused_tests)
    result["uniquePassedTests"] = len({c.attrib["fullname"] for p in xml_paths
        for c in ET.parse(p).getroot().iter("test-case") if c.attrib["result"] == "Passed"})
    args.output.with_suffix(".json").write_text(json.dumps(result, indent=2)+"\n", encoding="utf-8")
    gallery(args.directories, result, args.output.with_suffix(".png"))
    print(json.dumps({"uniquePassedTests": result["uniquePassedTests"],
                      "fixtures": [m["fixture"] for m in result["models"]]}))


if __name__ == "__main__":
    main()
