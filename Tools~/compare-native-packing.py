"""Compare original/optimized native placement on the exact captured requests.

Build both replay executables with MESHLAB_COPY_PLUGINS=OFF. Each replay also
checks every output Float32 bit, reference, chart ID and atlas dimension against
the capture. Native placement times exclude managed preparation and process I/O.
"""
import argparse
import csv
import hashlib
import json
import statistics
import subprocess
from pathlib import Path


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("captures", type=Path)
    parser.add_argument("reference", type=Path)
    parser.add_argument("optimized", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--repetitions", type=int, choices=range(1, 6), default=3)
    return parser.parse_args()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_times(path):
    with path.open(newline="", encoding="utf-8-sig") as stream:
        rows = list(csv.DictReader(stream))
    times = {row["request"]: float(row["milliseconds"]) for row in rows}
    if len(times) != len(rows):
        raise RuntimeError("Duplicate native request report rows")
    return times


def main():
    args = arguments()
    captures = args.captures.resolve()
    files = sorted(captures.rglob("*.xatlaspack"))
    hashes = {str(p.relative_to(captures)): digest(p) for p in files}
    policies = {p.relative_to(captures).parts[:3] for p in files}
    expected = {(model, "32", mode) for model in ("Cafe_Table_A_01", "Modern_Kitchen_A", "kamaz_Off")
                for mode in ("reverse-exclusive", "reverse-overlap")}
    if policies != expected:
        raise RuntimeError("All three models and both overlap policies must execute")
    args.output.mkdir(parents=True, exist_ok=True)
    if any(args.output.iterdir()):
        raise RuntimeError("Choose a fresh output directory")
    measurements = {"reference": [], "optimized": []}
    for repeat in range(args.repetitions):
        order = ("reference", "optimized") if repeat % 2 == 0 else ("optimized", "reference")
        for variant in order:
            report = args.output / f"{variant}-{repeat + 1}.csv"
            executable = getattr(args, variant).resolve()
            result = subprocess.run([str(executable), str(captures), str(report)],
                                    text=True, capture_output=True, check=True, timeout=1800)
            times = read_times(report)
            if set(times) != {p.relative_to(captures).as_posix() for p in files}:
                raise RuntimeError("Native requests changed between variants")
            measurements[variant].append(times)
            print(f"{variant} run {repeat + 1}: {result.stdout.strip()}", flush=True)
    if any(digest(captures / path) != value for path, value in hashes.items()):
        raise RuntimeError("Native input captures were modified")
    rows = []
    for policy in sorted(expected):
        prefix = "/".join(policy) + "/"
        totals = {variant: [sum(ms for key, ms in run.items() if key.startswith(prefix)) / 1000
                            for run in runs] for variant, runs in measurements.items()}
        before = statistics.median(totals["reference"])
        after = statistics.median(totals["optimized"])
        rows.append({"model": policy[0], "mode": policy[2], "referenceSeconds": before,
                     "optimizedSeconds": after, "speedup": before / after, "runs": totals,
                     "requests": sum(p.relative_to(captures).parts[:3] == policy for p in files)})
    summary = {"scope": "Native chart placement only; managed solve and export excluded",
               "requests": len(files), "repetitions": args.repetitions,
               "allNativeOutputsByteIdentical": True, "inputSha256": hashes, "rows": rows}
    (args.output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    for row in rows:
        print(f"{row['model']} {row['mode']}: {row['referenceSeconds']:.3f} -> "
              f"{row['optimizedSeconds']:.3f}s ({row['speedup']:.2f}x)", flush=True)


if __name__ == "__main__":
    main()
