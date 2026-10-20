"""Summarize the optional Unity furniture audit without treating UV quality as correspondence truth."""
import argparse
import collections
import csv
import json
import math
from pathlib import Path


METRICS = ("invalidFaces", "degenerateFaces", "overlapPairs", "outOfBoundsVertices",
           "worstAnisotropy", "areaWeightedAnisotropy", "overlapPairArea")


def compare(candidate, baseline):
    """Report tradeoffs instead of hiding regressions behind a weighted score."""
    if not all(row["quality"].get("overlapScanComplete") for row in (candidate, baseline)):
        return "incomplete"
    better = worse = False
    values = [(candidate["quality"][key], baseline["quality"][key]) for key in METRICS]
    values += [(candidate[key], baseline[key]) for key in ("geometryDegenerateFaces", "uvDegenerateFaces")]
    for value, original in values:
        if not math.isfinite(value) or not math.isfinite(original):
            return "invalid"
        tolerance = 1e-6 * max(abs(value), abs(original), 1e-12) if isinstance(value, float) else 0
        better |= value < original - tolerance
        worse |= value > original + tolerance
    return "tradeoff" if better and worse else "better" if better else "worse" if worse else "same"


def summarize(rows):
    """Validate run completeness and compare only identical input model/LOD pairs."""
    measured = [r for r in rows if r["status"] == "measured"]
    complete = bool(rows and rows[-1]["status"] == "complete")
    errors = [r for r in rows if r["status"] == "error"]
    excluded = [r for r in rows if r["status"] == "excluded"]
    index = {}
    for row in measured:
        key = tuple(row[k] for k in ("model", "source", "target", "profile", "method"))
        if key in index:
            raise ValueError("Duplicate measurement: " + str(key))
        index[key] = row
    pairs = {key[:3] for key in index}
    expected = [("default", method) for method in ("source", "grouped", "uv0-nearest", "surface-nearest", "surface-normal")]
    expected += [(profile, method) for profile in ("no-reparameterization", "oversample-8") for method in ("source", "grouped")]
    missing = [pair + suffix for pair in sorted(pairs) for suffix in expected if pair + suffix not in index]
    model_count = len({r["model"] for r in rows if r.get("model")})
    counts_match = complete and rows[-1].get("fileCount") == model_count and rows[-1].get("pairCount") == len(pairs) and rows[-1].get("errorCount") == len(errors)
    comparisons = collections.defaultdict(collections.Counter)
    for key, row in index.items():
        if key[-2:] == ("default", "source") or key[-2:] == ("default", "grouped"):
            continue
        base_key = key[:3] + ("default", "source" if key[-1] == "source" else "grouped")
        baseline = index.get(base_key)
        if baseline is None:
            continue
        if row["inputHash"] != baseline["inputHash"]:
            raise ValueError("Mismatched input hashes for " + str(key))
        comparisons["/".join(key[-2:])][compare(row, baseline)] += 1
    return dict(complete=bool(counts_match and pairs and not errors and not missing),
                countsMatch=counts_match,
                terminalMarker=complete, measuredRows=len(measured),
                models=model_count, measuredModels=len({r["model"] for r in measured}), pairs=len(pairs),
                errors=errors, exclusions=[{k: r.get(k) for k in ("model", "target", "error")} for r in excluded], missing=missing,
                incompleteScans=sum(not r["quality"]["overlapScanComplete"] for r in measured),
                comparisons={key: dict(value) for key, value in sorted(comparisons.items())})


def export(rows, destination):
    """Write a flat scoreboard and machine-readable completion/comparison summary."""
    destination.mkdir(parents=True, exist_ok=True)
    summary = summarize(rows)
    (destination / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    fields = ["model", "source", "target", "profile", "method", "inputHash", "atlasSide", "milliseconds",
              "geometryDegenerateFaces", "uvDegenerateFaces", "uvTriangleAreaSum", "rawOutOfBounds", "misses", "fallbackVertices"]
    quality_fields = ["faces", *METRICS, "overlapScanComplete"]
    with (destination / "measurements.csv").open("w", newline="", encoding="utf-8-sig") as stream:
        writer = csv.DictWriter(stream, fieldnames=fields + quality_fields)
        writer.writeheader()
        for row in rows:
            if row["status"] != "measured":
                continue
            writer.writerow({**{key: row[key] for key in fields}, **{key: row["quality"][key] for key in quality_fields}})
    return summary


def main():
    """Convert one captured run to CSV and a completeness-aware JSON summary."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    rows = [json.loads(line) for line in args.capture.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    summary = export(rows, args.out)
    print(json.dumps({k: v for k, v in summary.items() if k not in ("errors", "exclusions", "missing")}, indent=2))


if __name__ == "__main__":
    main()
