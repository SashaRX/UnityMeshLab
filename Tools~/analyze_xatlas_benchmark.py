"""Summarize XatlasDefaultsBenchmark CSVs, retaining sample count and full UV controls.
Usage: python Tools~/analyze_xatlas_benchmark.py INPUT_DIRECTORY OUTPUT_DIRECTORY
Requires matplotlib only for the PNG; the benchmark itself runs in Unity.
"""
import csv
import json
import statistics
import sys
from collections import defaultdict
from pathlib import Path


def main():
    source, destination = map(Path, sys.argv[1:3])
    destination.mkdir(parents=True, exist_ok=True)
    controls = ["chartMaxCost", "chartNormalDeviation", "chartRoundness", "chartStraightness",
                "chartNormalSeam", "chartIterations", "textureResolution", "padding",
                "packBruteForce", "packRotate", "packBlockAlign", "reduceUvFragmentation", "mergeCharts"]
    files = ["chart-first-pass.csv", "shortlist-repaired.csv", "joint-repaired.csv",
             "packing-complete.csv", "final-ab.csv"]
    summaries = []
    for filename in files:
        groups = defaultdict(list)
        with (source / filename).open(encoding="utf-8-sig") as stream:
            for row in csv.DictReader(stream):
                groups[row["case"], row["profile"]].append(row)
        for (case, profile), rows in groups.items():
            row = rows[0]
            result = {"run": filename.removesuffix(".csv"), "case": case, "profile": profile,
                      "samples": len(rows), "faces": int(row["faces"]),
                      "complete": all(r["complete"] == "True" for r in rows),
                      "valid": all(r["valid"] == "True" for r in rows),
                      "error": "; ".join(sorted({r["error"] for r in rows if r["error"]}))}
            for field in ["charts", "small", "mean", "worst", "overlap", "uvArea", "unwrapMs", "checkMs"]:
                result[field] = statistics.median(float(r[field]) for r in rows)
            settings = json.loads(row["settings"])
            result.update({field: settings[field] for field in controls})
            summaries.append(result)
    with (destination / "XATLAS_DEFAULTS_BENCHMARK.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, summaries[0].keys())
        writer.writeheader()
        writer.writerows(summaries)
    final = [r for r in summaries if r["run"] == "final-ab"]
    for case in sorted({r["case"] for r in final}):
        print("\n" + case)
        for row in final:
            if row["case"] == case:
                print(row["profile"], f'n={row["samples"]} charts={row["charts"]:g} small={row["small"]:g} '
                      f'mean={row["mean"]:.5f} worst={row["worst"]:.5f} overlap={row["overlap"]:g} '
                      f'area={row["uvArea"]:.4f} ms={row["unwrapMs"]:.1f}', row["error"])
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    profiles = ["old-default_res512_merge0", "round05_res512_merge0",
                "old-default_res512_merge1", "round05_res512_merge1", "user-current_res512_merge1"]
    labels = ["Old / merge off (n=3)", "Recommended / off (n=3)", "Old / merge on (n=3)", "Recommended / on (n=3)", "User / merge on (n=1)"]
    data = [next(r for r in final if r["case"] == "bust" and r["profile"] == name) for name in profiles]
    fig, axes = plt.subplots(1, 3, figsize=(12, 4.5), layout="constrained")
    colors = ["#73839c", "#16837c", "#73839c", "#16837c", "#ac753f"]
    for ax, field, title in zip(axes, ["charts", "mean", "unwrapMs"], ["Final charts (0 overlaps)", "Mean conformal stretch", "Unwrap time (ms)"]):
        values = [r[field] for r in data]
        bars = ax.barh(labels, values, color=colors)
        ax.invert_yaxis()
        ax.set_title(title)
        ax.set_xlim(0, max(values) * 1.28)
        ax.spines[["top", "right"]].set_visible(False)
        for bar, value in zip(bars, values):
            ax.text(value + max(values) * .02, bar.get_y() + bar.get_height() / 2,
                    f"{value:.3f}" if field == "mean" else f"{value:.0f}", va="center", fontsize=9)
    axes[1].set_yticklabels([])
    axes[2].set_yticklabels([])
    fig.suptitle("Bust, fixed 1997 faces, 512 atlas / padding 2, production UV pipeline", fontsize=12)
    fig.savefig(destination / "XATLAS_DEFAULTS_BENCHMARK.png", dpi=160)
    plt.close(fig)
    print(f"\n{len(summaries)} distinct case/profile measurements; {sum(r['samples'] for r in summaries)} measured calls")


if __name__ == "__main__":
    main()
