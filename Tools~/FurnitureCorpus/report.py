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


def compare_runs(rows, baseline_rows):
    """Compare complete captures of the same corpus without hiding per-model regressions."""
    summaries = [summarize(capture) for capture in (rows, baseline_rows)]
    if any(not s["complete"] or s["incompleteScans"] for s in summaries):
        raise ValueError("Both captures must be complete with complete intersection scans.")
    fields = ("model", "source", "target", "profile", "method")
    indices = [{tuple(r[k] for k in fields): r for r in capture if r["status"] == "measured"}
               for capture in (rows, baseline_rows)]
    inventories = [{r["model"] for r in capture if r.get("model")} for capture in (rows, baseline_rows)]
    if indices[0].keys() != indices[1].keys() or inventories[0] != inventories[1]:
        raise ValueError("Captures contain different model/LOD/variant inventories.")
    counts = collections.defaultdict(collections.Counter)
    changed = []
    for key, candidate in sorted(indices[0].items()):
        baseline = indices[1][key]
        if any(candidate[field] != baseline[field] for field in ("inputHash", "resolution", "schema")):
            raise ValueError("Changed input hash, resolution or schema for " + str(key))
        result = compare(candidate, baseline)
        counts["/".join(key[-2:])][result] += 1
        if result != "same":
            changed.append(dict(zip(fields, key)) | {"comparison": result})
    return dict(measuredRows=len(indices[0]), comparisons={key: dict(value) for key, value in sorted(counts.items())},
                changed=changed)


def export(rows, destination, baseline_rows=None):
    """Write a flat scoreboard and machine-readable completion/comparison summary."""
    destination.mkdir(parents=True, exist_ok=True)
    summary = summarize(rows)
    if baseline_rows is not None:
        summary["baselineComparison"] = compare_runs(rows, baseline_rows)
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


def main(argv=None):
    """Convert one captured run to CSV and a completeness-aware JSON summary."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, help="Compare against a complete earlier capture of identical inputs.")
    parser.add_argument("--require-no-regressions", action="store_true", help="Fail on any worse, tradeoff or invalid baseline comparison.")
    args = parser.parse_args(argv)
    if args.require_no_regressions and args.baseline is None:
        parser.error("--require-no-regressions requires --baseline")
    rows = [json.loads(line) for line in args.capture.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    baseline = None if args.baseline is None else [json.loads(line) for line in
                args.baseline.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    summary = export(rows, args.out, baseline)
    print(json.dumps({k: v for k, v in summary.items() if k not in ("errors", "exclusions", "missing")}, indent=2))
    if args.require_no_regressions and any(row["comparison"] != "better" for row in summary["baselineComparison"]["changed"]):
        parser.exit(1, "The baseline comparison contains regressions or tradeoffs; see summary.json.\n")


if __name__ == "__main__":
    main()
