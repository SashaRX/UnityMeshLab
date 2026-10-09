"""Decode Mesh Lab v2/v3 failure/support captures without Unity or private assets."""
import argparse
import io
import json
from pathlib import Path
import struct

import numpy as np

from topology import inspect


def take(stream, count):
    result = stream.read(count)
    if len(result) != count:
        raise ValueError('Truncated failure capture')
    return result


def read_string(stream):
    length = 0
    for shift in range(0, 35, 7):
        value = take(stream, 1)[0]
        length |= (value & 127) << shift
        if not value & 128:
            if length > 16*1024*1024:
                raise ValueError('Capture metadata is too large')
            return take(stream, length).decode('utf-8')
    raise ValueError('Invalid BinaryWriter string length')


def read_failure(path):
    stream = io.BytesIO(Path(path).read_bytes())
    magic, version = struct.unpack('<II', take(stream, 8))
    if magic != 0x524D4C42 or version not in (2, 3):
        raise ValueError('Expected a version 2 or 3 Remesh failure capture')
    metadata = json.loads(read_string(stream))
    if not isinstance(metadata, dict):
        raise ValueError('Capture metadata must be an object')
    meshes = {}
    slots = ('source', 'prepared', 'raw', 'input') if version == 3 else ('source', 'raw', 'input')
    for name in slots:
        present = take(stream, 1)[0]
        if present == 0:
            meshes[name] = None
            continue
        if present != 1:
            raise ValueError('Invalid mesh presence flag')
        vertices, indices = struct.unpack('<II', take(stream, 8))
        if indices % 3:
            raise ValueError('Capture indices must be triangles')
        p = np.frombuffer(take(stream, vertices*12), dtype='<f4').reshape(-1, 3).copy()
        ix = np.frombuffer(take(stream, indices*4), dtype='<u4').reshape(-1, 3).copy()
        if not np.isfinite(p).all() or (ix.size and int(ix.max()) >= len(p)):
            raise ValueError('Invalid captured positions or indices')
        meshes[name] = (p, ix)
    if stream.read(1):
        raise ValueError('Unexpected trailing capture data')
    return metadata, meshes


def export(path, output):
    metadata, meshes = read_failure(path)
    output = Path(output)
    if output.exists() and any(output.iterdir()):
        raise ValueError('Output directory must be empty to avoid stale stage geometry')
    output.mkdir(parents=True, exist_ok=True)
    report = {'capture': str(Path(path).resolve()), 'metadata': metadata, 'meshes': {}}
    for name, mesh in meshes.items():
        if mesh is None:
            report['meshes'][name] = None
            continue
        positions, indices = mesh
        with (output/(name+'.bin')).open('wb') as stream:
            np.array([len(positions), indices.size], dtype='<u4').tofile(stream)
            positions.astype('<f4').tofile(stream)
            indices.astype('<u4').tofile(stream)
        np.savez(output/(name+'.npz'), positions=positions, indices=indices)
        report['meshes'][name] = {'vertices': len(positions), 'faces': len(indices),
                                   'topology': inspect(positions, indices)}
    (output/'capture.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--capture', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    print(json.dumps(export(args.capture, args.output), indent=2), flush=True)


if __name__ == '__main__':
    main()
