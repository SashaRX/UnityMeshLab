"""Generate one explicitly selected planar disk Cap; offline support geometry only.

This does not choose disk versus Bridge intent. No source attributes, Unity assets
or native plugins are edited. Acceptance uses the independent incremental audit.
"""
import argparse
import json
from pathlib import Path

import numpy as np

from analyze import read_mesh
from boundaries import checked_arrays, extract
from compound import triangulate, validate_projection
from incremental import Limits, attempt
from planes import Options, fit_plane


def generate(positions, triangles, source_hash, loop_id, *, closure_intent,
             options=Options(), limits=Limits(), cancel=None):
    """Append a constrained disk only after continuity, plane and geometry gates.

    The caller supplies a snapshot-bound loop ID and explicit disk intent. A
    planar rim alone cannot establish that intent (e.g. two torus cut rims).
    Refusals return the original arrays; accepted arrays preserve the old prefix.
    """
    options.validate()
    limits.validate()
    if closure_intent != 'disk':
        raise ValueError('An explicit disk closure intent is required; Bridge is a separate operation')
    positions, triangles = checked_arrays(positions, triangles)
    report = {'schemaVersion': 1, 'experimental': True, 'accepted': False,
              'sourceHash': source_hash, 'loopId': loop_id, 'closureIntent': closure_intent,
              'solidCertified': False, 'intendedShapeCertified': False}

    def refuse(reason):
        report['refusal'] = reason
        return positions, triangles, report

    if cancel is not None and cancel():
        return refuse('cancelled')
    if len(triangles) >= limits.max_faces or len(positions) > limits.max_vertices:
        return refuse('mesh_budget_exceeded')
    boundary = extract(positions, triangles, cancel=cancel)
    if not boundary['extractionAccepted']:
        return refuse('source_boundary_' + boundary['refusal'])
    if source_hash != boundary['sourceHash']:
        return refuse('stale_source_snapshot')
    loops = [loop for loop in boundary['loops'] if loop['id'] == loop_id]
    if len(loops) != 1:
        return refuse('unknown_selected_loop')
    loop = loops[0]
    if loop['requiresJunctionResolution']:
        return refuse('selected_loop_requires_junction_resolution')
    edges = loop['halfedges']
    if len(edges) > options.max_loop_edges:
        return refuse('loop_edge_budget_exceeded')
    if len(triangles) + len(edges) - 2 > limits.max_faces or len(positions) > limits.max_vertices:
        return refuse('mesh_budget_exceeded')
    ids = np.array([edge['sourceVertices'][0] for edge in edges])
    points = positions[ids].astype('d')
    plane = fit_plane(points, closed=True, rank_ratio=options.rank_ratio)
    report['supportPlane'] = plane
    if not plane['rankSupported']:
        return refuse('underdetermined_patch_plane')
    diagonal = float(np.linalg.norm(points.max(axis=0) - points.min(axis=0)))
    tolerance = max(options.absolute_tolerance, options.relative_tolerance * diagonal)
    tolerance += diagonal * np.finfo('d').eps * 64
    if plane['maxDistance'] > tolerance:
        return refuse('selected_loop_not_planar')
    projected = (points - plane['origin']) @ np.array([plane['basisU'], plane['basisV']]).T
    try:
        areas = validate_projection(projected, [list(range(len(ids)))])
    except ValueError as error:
        return refuse(str(error))
    if cancel is not None and cancel():
        return refuse('cancelled')
    segments = np.column_stack((np.arange(len(ids)), np.roll(np.arange(len(ids)), -1)))
    # pYCS0Q keeps every constraint vertex; no Steiner vertices or new source positions.
    caps = ids[triangulate(projected, segments, [])].astype(triangles.dtype)
    if areas[0] > 0:
        caps = caps[:, ::-1].copy()
    result_p, result_ix, audit = attempt(positions, triangles, source_hash,
        [edge['halfedge'] for edge in edges], np.empty((0, 3), positions.dtype), caps,
        plane_options=options, limits=limits, cancel=cancel)
    report['audit'] = audit
    if not audit['accepted']:
        return refuse(audit['refusal'])
    report.update({'accepted': True, 'addedFaces': len(caps), 'addedVertices': 0,
                   'resultHash': audit['resultHash'], 'boundaryEdgesAfter': audit['boundaryEdgesAfter']})
    return result_p, result_ix, report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--source-hash', required=True)
    parser.add_argument('--loop-id', required=True)
    parser.add_argument('--intent', required=True, choices=['disk'])
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    if args.output.exists() or args.output.with_suffix('.json').exists():
        raise ValueError('Output mesh/report already exists')
    p, ix, report = generate(*read_mesh(args.source), args.source_hash, args.loop_id,
                             closure_intent=args.intent)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.with_suffix('.json').write_text(json.dumps(report, indent=2), encoding='utf-8')
    if report['accepted']:
        with args.output.open('wb') as stream:
            np.array((len(p), ix.size), '<u4').tofile(stream)
            p.astype('<f4').tofile(stream)
            ix.astype('<u4').tofile(stream)
    print(json.dumps({k: v for k, v in report.items() if k not in ('supportPlane', 'audit')}, indent=2))
    return 0 if report['accepted'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
