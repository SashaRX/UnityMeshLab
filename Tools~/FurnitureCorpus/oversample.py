"""Explain the oversample-8 profile against the default pack.

The pack reports its internal (oversampled) atlas size, and grouped transfer takes
that size as the atlas side for its texel tolerances. Oversample 8 therefore changes
two things at once: the packed source layout and the transfer's tolerances (half as
wide in UV space as at the default oversample 4). A capture recorded with
MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS=1 adds `grouped-at-resolution` rows, which transfer
against the requested lightmap resolution instead, so the two effects separate.
"""
import argparse
import collections
import json
from pathlib import Path

from report import compare, metric_verdicts


PROFILE = "oversample-8"
AT_RESOLUTION = "grouped-at-resolution"


def comparisons(profile=PROFILE):
    """(label, candidate profile/method, baseline profile/method) for every question asked."""
    return [
        ("packed source: oversample 8 vs 4", (profile, "source"), ("default", "source")),
        ("transfer, internal atlas side: oversample 8 vs 4", (profile, "grouped"), ("default", "grouped")),
        ("transfer, requested resolution: oversample 8 vs 4", (profile, AT_RESOLUTION), ("default", AT_RESOLUTION)),
        ("transfer side only, oversample 4: requested vs internal", ("default", AT_RESOLUTION), ("default", "grouped")),
        ("transfer side only, oversample 8: requested vs internal", (profile, AT_RESOLUTION), (profile, "grouped")),
    ]


def index_rows(rows):
    """Measured rows by (model, source, target, profile, method)."""
    fields = ("model", "source", "target", "profile", "method")
    return {tuple(row[k] for k in fields): row for row in rows if row.get("status") == "measured"}


def pattern(verdicts):
    """The metrics a tradeoff improves and the metrics it worsens."""
    better = "+".join(k for k, v in verdicts.items() if v == "better") or "-"
    worse = "+".join(k for k, v in verdicts.items() if v == "worse") or "-"
    return f"better {better} | worse {worse}"


def compare_group(index, candidate, baseline, top=10):
    """Verdict counts, per-metric verdicts and the most common tradeoff patterns."""
    verdicts = collections.Counter()
    metrics = collections.defaultdict(collections.Counter)
    tradeoffs = collections.Counter()
    pairs = sorted({key[:3] for key in index})
    for pair in pairs:
        row, base = index.get(pair + candidate), index.get(pair + baseline)
        if row is None or base is None:
            verdicts["missing"] += 1
            continue
        result = compare(row, base)
        verdicts[result] += 1
        if result in ("incomplete", "invalid"):
            continue
        for metric, verdict in metric_verdicts(row, base).items():
            metrics[metric][verdict] += 1
        if result == "tradeoff":
            tradeoffs[pattern(metric_verdicts(row, base))] += 1
    return dict(verdicts=dict(verdicts), metrics={k: dict(v) for k, v in sorted(metrics.items())},
                tradeoffs=dict(tradeoffs.most_common(top)))


def retention(index, method, profile=PROFILE):
    """For each pair: the packed-source verdict against the transfer verdict of `method`."""
    table = collections.Counter()
    for pair in sorted({key[:3] for key in index}):
        rows = [index.get(pair + key) for key in ((profile, "source"), ("default", "source"), (profile, method), ("default", method))]
        if any(row is None for row in rows):
            continue
        table[f"source {compare(rows[0], rows[1])} -> transfer {compare(rows[2], rows[3])}"] += 1
    return dict(sorted(table.items()))


def oversample_reached(index, profile=PROFILE):
    """atlasSide / requested resolution of each packed source, once per (model, source mesh)."""
    reached = collections.defaultdict(collections.Counter)
    seen = set()
    for key, row in sorted(index.items()):
        model, source, _, row_profile, method = key
        if method != "source" or row_profile not in ("default", profile) or (model, source, row_profile) in seen:
            continue
        seen.add((model, source, row_profile))
        resolution = row.get("resolution") or 0
        ratio = row["atlasSide"] / resolution if resolution > 0 else float("nan")
        reached[row_profile][f"{ratio:g}"] += 1
    return {k: dict(sorted(v.items())) for k, v in sorted(reached.items())}


def analyze(rows, profile=PROFILE):
    """Everything the oversample report shows, as plain data."""
    index = index_rows(rows)
    return dict(
        profile=profile, pairs=len({key[:3] for key in index}),
        atlasSideControl=any(key[4] == AT_RESOLUTION for key in index),
        comparisons={label: compare_group(index, candidate, baseline)
                     for label, candidate, baseline in comparisons(profile)},
        retention={method: retention(index, method, profile) for method in ("grouped", AT_RESOLUTION)},
        oversampleReached=oversample_reached(index, profile))


def render(analysis):
    """A summary that can be posted without any geometry or per-model rows."""
    lines = [f"# {analysis['profile']} against default: {analysis['pairs']} LOD pairs", ""]
    if not analysis["atlasSideControl"]:
        lines += ["No `grouped-at-resolution` rows: record with MESHLAB_CORPUS_OVERSAMPLE_ANALYSIS=1 "
                  "to separate the pack from the transfer's atlas side.", ""]
    lines += ["## Verdicts", "", "| comparison | better | worse | tradeoff | same | other |", "|---|---|---|---|---|---|"]
    for label, group in analysis["comparisons"].items():
        v = group["verdicts"]
        other = sum(n for k, n in v.items() if k not in ("better", "worse", "tradeoff", "same"))
        lines.append(f"| {label} | {v.get('better', 0)} | {v.get('worse', 0)} | {v.get('tradeoff', 0)} | {v.get('same', 0)} | {other} |")
    for label, group in analysis["comparisons"].items():
        if not group["metrics"]:
            continue
        lines += ["", f"## Per metric: {label}", "", "| metric | better | worse | same |", "|---|---|---|---|"]
        for metric, v in group["metrics"].items():
            lines.append(f"| {metric} | {v.get('better', 0)} | {v.get('worse', 0)} | {v.get('same', 0)} |")
        if group["tradeoffs"]:
            lines += ["", "Most common tradeoffs:", ""] + [f"- {n} × {p}" for p, n in group["tradeoffs"].items()]
    for method, table in analysis["retention"].items():
        if table:
            lines += ["", f"## Does a source verdict survive `{method}`?", ""] + [f"- {n} × {k}" for k, n in table.items()]
    lines += ["", "## Internal oversample reached (atlasSide / requested resolution, per source mesh)", ""]
    for profile, ratios in analysis["oversampleReached"].items():
        lines.append(f"- {profile}: " + ", ".join(f"{k}× {n}" for k, n in ratios.items()))
    return "\n".join(lines) + "\n"


def main(argv=None):
    """Write oversample.json and oversample.md next to the regular report."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--profile", default=PROFILE)
    args = parser.parse_args(argv)
    rows = [json.loads(line) for line in args.capture.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    analysis = analyze(rows, args.profile)
    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "oversample.json").write_text(json.dumps(analysis, indent=2), encoding="utf-8")
    text = render(analysis)
    (args.out / "oversample.md").write_text(text, encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
