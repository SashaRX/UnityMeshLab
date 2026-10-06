"""Generate actual xatlas UVs from independent region proposals.

Each label is a material boundary, not a final chart: xatlas may add necessary
cuts within regions. The no-region control uses identical chart inputs/options.
UVs and authored normals are deliberately absent from this standalone probe.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import time

import numpy as np

from seams import align, read_capture, topology


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--probe", type=Path, required=True)
    parser.add_argument("--labels", nargs="*", type=Path, default=[])
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    reference = topology(read_capture(args.reference))
    p, faces, _, _, _ = reference
    args.out.mkdir(parents=True, exist_ok=True)
    capture = args.out / "geometry.bin"
    capture.write_bytes(struct.pack("<3i", len(p), faces.size, 1) + p.astype("<f4").tobytes() +
                        np.zeros((len(p), 2), dtype="<f4").tobytes() + faces.astype("<i4").tobytes() +
                        np.zeros(len(p), dtype="<i4").tobytes())
    cases = [("geometry-only", "-")]
    for file in args.labels:
        labels = np.load(file, allow_pickle=False)
        if labels.shape != (len(faces),) or not np.issubdtype(labels.dtype, np.integer) or (labels < 0).any():
            raise ValueError("Invalid face labels")
        raw = args.out / (file.stem + ".dat")
        raw.write_bytes(labels.astype("<u4").tobytes())
        cases.append((file.stem.removesuffix("-labels"), str(raw)))
    report = []
    for name, labels in cases:
        for cost, iterations, roundness in ((2, 1, 0.5), (2, 4, 0.5), (4, 4, 0.01)):
            key = f"region-{name}-cost{cost}-iter{iterations}-round{roundness}"
            output = args.out / (key + ".bin")
            started = time.perf_counter()
            result = subprocess.run([str(args.probe.resolve()), str(capture), labels, str(output),
                                     str(cost), str(iterations), str(roundness)], capture_output=True, text=True, check=True)
            align(read_capture(output), reference)
            report.append(dict(method=key, maxCost=cost, maxIterations=iterations, roundness=roundness,
                               labelFile=labels, labelSha256=hashlib.sha256(Path(labels).read_bytes()).hexdigest() if labels != "-" else None,
                               captureSha256=hashlib.sha256(output.read_bytes()).hexdigest(),
                               seconds=time.perf_counter()-started, output=result.stdout.strip()))
            print(report[-1], flush=True)
    (args.out / "regions.json").write_text(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
