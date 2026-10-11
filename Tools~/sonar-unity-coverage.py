"""Bind Unity OpenCover measurements to the exact sources the scanner analyses."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import xml.etree.ElementTree as ET


def source_hashes(root):
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for folder in ("Editor", "Tests", "Runtime")
            for p in sorted((root / folder).rglob("*.cs"))}


def convert(root, bundle, output):
    manifest = json.loads((bundle / "sources.json").read_text(encoding="utf-8-sig"))
    if manifest["files"] != source_hashes(root):
        raise ValueError("Coverage sources differ from the analysed checkout; run Unity again")
    tests = ET.parse(bundle / "tests.xml").getroot()
    if tests.tag != "test-run" or int(tests.get("failed", "1")) != 0 or int(tests.get("passed", "0")) == 0:
        raise ValueError("Coverage requires a completed passing Unity test run")
    prefix = manifest["package_root"].replace("\\", "/").rstrip("/") + "/"
    points = {}
    reports = sorted((bundle / "opencover").rglob("*.xml"))
    if not reports:
        raise ValueError("No raw Unity OpenCover reports")
    for report in reports:
        session = ET.parse(report).getroot()
        if session.tag != "CoverageSession":
            raise ValueError("Expected a Unity OpenCover CoverageSession")
        for module in session.findall("./Modules/Module"):
            files = {}
            for file in module.findall("./Files/File"):
                path = file.get("fullPath", "").replace("\\", "/")
                if not path.casefold().startswith(prefix.casefold()):
                    raise ValueError(f"Coverage file outside measured package: {path}")
                relative = path[len(prefix):]
                if relative not in manifest["files"] or ".." in PurePosixPath(relative).parts:
                    raise ValueError(f"Unknown coverage source: {relative}")
                files[file.get("uid")] = relative
            for point in module.iter("SequencePoint"):
                relative = files[point.get("fileid")]
                line = int(point.get("sl"))
                if line <= 0 or line >= 0xFEEFEE:
                    continue
                key = (relative, line)
                points[key] = points.get(key, False) or int(point.get("vc", "0")) > 0
    if not points or not any(points.values()):
        raise ValueError("No executed source lines in Unity coverage")
    coverage = ET.Element("coverage", version="1")
    for relative in sorted({path for path, _ in points}):
        file = ET.SubElement(coverage, "file", path=relative)
        for (path, line), covered in sorted(points.items()):
            if path == relative:
                ET.SubElement(file, "lineToCover", lineNumber=str(line), covered=str(covered).lower())
    output.parent.mkdir(parents=True, exist_ok=True)
    ET.ElementTree(coverage).write(output, encoding="utf-8", xml_declaration=True)
    return {"lines": len(points), "covered": sum(points.values()), "reports": len(reports)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--bundle", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    print(json.dumps(convert(args.root.resolve(), args.bundle, args.output)))
