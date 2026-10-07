"""Export an exact indexed capture for independent unwrap algorithms."""
import argparse
from pathlib import Path

import numpy as np

from seams import read_capture


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--uv", action="store_true", help="Retain the capture UVs as initialization")
    parser.add_argument("--flip-v", action="store_true", help="Reflect UV orientation for a solver requiring positive UV winding")
    args = parser.parse_args()
    p, uv, faces, _, _ = read_capture(args.capture)
    if args.flip_v:
        uv[:, 1] = 1 - uv[:, 1]
    welded, slots = np.unique(p, axis=0, return_inverse=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8") as stream:
        for v in welded:
            stream.write("v " + " ".join(f"{x:.17g}" for x in v) + "\n")
        if args.uv:
            for v in uv:
                stream.write("vt " + " ".join(f"{x:.17g}" for x in v) + "\n")
        for face in faces:
            corners = [f"{slots[c] + 1}/{c + 1}" if args.uv else str(slots[c] + 1) for c in face]
            stream.write("f " + " ".join(corners) + "\n")


if __name__ == "__main__":
    main()
