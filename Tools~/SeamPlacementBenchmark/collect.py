"""Collect boundary proposals and actual UV cuts with per-input provenance."""
import argparse
import hashlib
import json
from pathlib import Path

import numpy as np

from seams import align, read_capture, topology


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", type=Path, required=True)
    parser.add_argument("--prototype", action="append", type=Path, default=[])
    parser.add_argument("--directory", type=Path)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()
    reference = topology(read_capture(args.reference))
    cuts, manifest = {}, {}
    for file in args.prototype:
        with np.load(file, allow_pickle=False) as proposals:
            for name in proposals.files:
                if name in cuts or proposals[name].shape != reference[4].shape or proposals[name].dtype != bool:
                    raise ValueError("Invalid proposal")
                cuts[name] = proposals[name].copy()
                manifest[name] = dict(kind="boundary-proposal", file=str(file), sha256=hashlib.sha256(file.read_bytes()).hexdigest())
    if args.directory:
        for file in sorted(args.directory.glob("region-*.bin")):
            if file.stem in cuts:
                raise ValueError("Duplicate method")
            cuts[file.stem] = align(read_capture(file), reference)
            manifest[file.stem] = dict(kind="actual-UV", file=str(file), sha256=hashlib.sha256(file.read_bytes()).hexdigest())
    args.out.parent.mkdir(parents=True, exist_ok=True)
    np.savez(args.out, **cuts)
    args.out.with_suffix(".json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(len(cuts), "methods collected")


if __name__ == "__main__":
    main()
