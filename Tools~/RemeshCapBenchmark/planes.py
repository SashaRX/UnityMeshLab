"""Bounded whole-rim / contiguous-arc plane hypotheses; no reconstructed faces.

This conservative offline profile uses boundary turns as candidate junctions.
It retains ambiguous minimum-plane decompositions instead of certifying one as
the missing surface. Source occupancy and feature completion belong to P3.
"""
import argparse
from dataclasses import asdict, dataclass, fields
import json
from pathlib import Path

import numpy as np

from analyze import read_mesh
from boundaries import extract, select_domains
from compound import validate_projection


@dataclass(frozen=True)
class Options:
    relative_tolerance: float = 1e-5
    absolute_tolerance: float = 0
    rank_ratio: float = 1e-6
    min_plane_angle_degrees: float = 20
    min_junction_turn_degrees: float = 20
    min_planes: int = 2
    max_planes: int = 4
    max_hypotheses: int = 8
    max_fits: int = 50_000
    max_states: int = 100_000
    max_loop_edges: int = 512

    def validate(self):
        for name in ('relative_tolerance', 'absolute_tolerance', 'rank_ratio',
                     'min_plane_angle_degrees', 'min_junction_turn_degrees'):
            if not np.isfinite(getattr(self, name)):
                raise ValueError('Plane options must be finite')
        if self.relative_tolerance < 0 or self.absolute_tolerance < 0 or not 0 < self.rank_ratio < 1:
            raise ValueError('Invalid plane tolerance or rank ratio')
        if not 0 < self.min_plane_angle_degrees <= 90 or not 0 <= self.min_junction_turn_degrees < 180:
            raise ValueError('Invalid angular evidence thresholds')
        for name in ('min_planes', 'max_planes', 'max_hypotheses', 'max_fits', 'max_states', 'max_loop_edges'):
            if type(getattr(self, name)) is not int or getattr(self, name) < 1:
                raise ValueError('Plane budgets must be positive integers')
        if not 2 <= self.min_planes <= self.max_planes <= 32:
            raise ValueError('Piecewise plane count must be between two and 32')


class AnalysisStopped(Exception):
    pass


class Work:
    def __init__(self, options, cancel):
        self.options, self.cancel = options, cancel
        self.fits, self.states = 0, 0

    def step(self, kind):
        if self.cancel is not None and self.cancel():
            raise AnalysisStopped('cancelled')
        count = getattr(self, kind) + 1
        if count > getattr(self.options, 'max_' + kind):
            raise AnalysisStopped(kind + '_budget_exceeded')
        setattr(self, kind, count)


def fit_plane(points, closed=False, rank_ratio=1e-6):
    """Arc-length weighted fit in a centered/scaled frame, without a closure chord."""
    if not np.isfinite(rank_ratio) or not 0 < rank_ratio < 1:
        raise ValueError('Invalid plane rank ratio')
    q = np.asarray(points, dtype='d')
    if q.ndim != 2 or q.shape[1] != 3 or len(q) < 3 or not np.isfinite(q).all():
        raise ValueError('Plane support needs at least three finite 3D points')
    with np.errstate(over='ignore', invalid='ignore'):
        local = q - q[0]
    if not np.isfinite(local).all():
        raise ValueError('Coordinate range exceeds float64 analysis')
    scale = float(np.abs(local).max())
    if scale == 0:
        return {'rankSupported': False, 'reason': 'coincident_support'}
    local /= scale
    segments = np.roll(local, -1, axis=0) - local if closed else np.diff(local, axis=0)
    lengths = np.linalg.norm(segments, axis=1)
    if np.any(lengths == 0):
        return {'rankSupported': False, 'reason': 'zero_length_support_edge'}
    weights = ((lengths + np.roll(lengths, 1)) / 2 if closed else
               np.r_[lengths[0] / 2, (lengths[:-1] + lengths[1:]) / 2, lengths[-1] / 2])
    weights /= weights.sum()
    center = (local * weights[:, None]).sum(axis=0)
    _, singular, basis = np.linalg.svd((local - center) * np.sqrt(weights[:, None]), full_matrices=False)
    if singular[1] <= singular[0] * rank_ratio:
        return {'rankSupported': False, 'reason': 'collinear_or_ill_conditioned_support',
                'singularValuesNormalized': singular.tolist(), 'supportScale': scale}
    normal = basis[-1]
    if normal[np.abs(normal).argmax()] < 0:
        normal = -normal
    residual = np.abs((local - center) @ normal)
    u = np.cross(normal, np.eye(3)[np.abs(normal).argmin()])
    u /= np.linalg.norm(u)
    v = np.cross(normal, u)
    return {'rankSupported': True, 'origin': (q[0] + center * scale).tolist(),
            'normal': normal.tolist(), 'basisU': u.tolist(), 'basisV': v.tolist(),
            'maxDistance': float(residual.max() * scale),
            'rmsDistance': float(np.sqrt((weights * residual**2).sum()) * scale),
            'singularValuesNormalized': singular.tolist(), 'supportScale': scale}


def plane_angle(first, second):
    dot = abs(float(np.dot(first['normal'], second['normal'])))
    return float(np.degrees(np.arccos(np.clip(dot, 0, 1))))


def loop_analysis(points, loop, options, work):
    occurrences = loop['halfedges']
    q = np.asarray([points[h['sourceVertices'][0]] for h in occurrences], dtype='d')
    n = len(q)
    result = {'loopId': loop['id'], 'edgeCount': n,
              'requiresJunctionResolution': loop['requiresJunctionResolution'], 'hypotheses': []}
    if n > options.max_loop_edges:
        raise AnalysisStopped('loop_edge_budget_exceeded')
    if n < 3:
        result['status'] = 'insufficient_support'
        return result
    extent = q.max(axis=0) - q.min(axis=0)
    diameter = float(np.linalg.norm(extent))
    if not np.isfinite(diameter):
        raise ValueError('Boundary extent exceeds float64 analysis')
    tolerance = max(options.absolute_tolerance, options.relative_tolerance * diameter)
    if not np.isfinite(tolerance):
        raise ValueError('Plane tolerance exceeds float64 analysis')
    numerical = float(diameter * np.finfo('d').eps * 64)
    result.update({'bboxDiagonal': diameter, 'geometricTolerance': tolerance,
                   'numericalAllowance': numerical})

    def fitted(support, closed=False):
        work.step('fits')
        plane = fit_plane(support, closed, options.rank_ratio)
        plane['withinTolerance'] = bool(plane['rankSupported'] and plane['maxDistance'] <= tolerance + numerical)
        return plane

    whole = fitted(q, closed=True)
    result['wholePlane'] = whole
    if loop['requiresJunctionResolution']:
        result['status'] = 'junction_resolution_required'
        return result
    if whole['withinTolerance']:
        # The plane is fitted locally; keep exact original 3D coordinates.
        uv = (q - q[0]) @ np.asarray([whole['basisU'], whole['basisV']]).T
        try:
            validate_projection(uv, [list(range(n))])
        except ValueError as error:
            result.update({'status': 'projected_boundary_invalid', 'projectionRefusal': str(error)})
            return result
        result.update({'status': 'whole_plane_hypothesis', 'hypothesisCount': 1,
                       'hypotheses': [{'planeCount': 1, 'arcs': [
                           {'halfedges': [h['halfedge'] for h in occurrences], 'plane': whole}]}]})
        return result

    directions = np.roll(q, -1, axis=0) - q
    lengths = np.linalg.norm(directions, axis=1)
    if np.any(lengths == 0):
        result['status'] = 'zero_length_boundary_edge'
        return result
    directions /= lengths[:, None]
    turns = np.degrees(np.arccos(np.clip((np.roll(directions, 1, axis=0) * directions).sum(axis=1), -1, 1)))
    junctions = [i for i in range(n) if turns[i] >= options.min_junction_turn_degrees]
    result['candidateJunctions'] = [{'halfedge': occurrences[i]['halfedge'], 'turnDegrees': float(turns[i])}
                                  for i in junctions]
    if not whole['rankSupported']:
        result['status'] = 'underdetermined_support'
        return result
    arcs = {start: [] for start in junctions}
    extended = np.concatenate((q, q))
    for start in junctions:
        for end in junctions:
            length = (end - start) % n
            if length < 2:
                continue
            plane = fitted(extended[start:start + length + 1])
            if plane['withinTolerance']:
                arcs[start].append({'start': start, 'length': length, 'end': end,
                                    'halfedges': [occurrences[(start + i) % n]['halfedge'] for i in range(length)],
                                    'plane': plane})
        arcs[start].sort(key=lambda arc: (-arc['length'], arc['halfedges']))
    result['supportedArcCount'] = sum(map(len, arcs.values()))
    found = {}

    def search(start, current, consumed, path, count):
        work.step('states')
        if len(path) == count:
            if consumed != n or current != start or plane_angle(path[-1]['plane'], path[0]['plane']) < options.min_plane_angle_degrees:
                return
            key = tuple(sorted(tuple(arc['halfedges']) for arc in path))
            if key not in found:
                found[key] = {'planeCount': count, 'arcs': path.copy(),
                              'sumRmsDistance': sum(arc['plane']['rmsDistance'] for arc in path)}
            return
        for arc in arcs.get(current, []):
            remaining = n - consumed - arc['length']
            if remaining < 2 * (count - len(path) - 1):
                continue
            if path and plane_angle(path[-1]['plane'], arc['plane']) < options.min_plane_angle_degrees:
                continue
            search(start, arc['end'], consumed + arc['length'], path + [arc], count)

    for count in range(options.min_planes, min(options.max_planes, n // 2) + 1):
        for start in junctions:
            search(start, start, 0, [], count)
        if found:
            break
    ranked = sorted(found.items(), key=lambda pair: (pair[1]['sumRmsDistance'], pair[0]))
    result.update({'hypothesisCount': len(ranked), 'hypothesesTruncated': len(ranked) > options.max_hypotheses,
                   'hypotheses': [value for _, value in ranked[:options.max_hypotheses]],
                   'status': ('no_supported_piecewise_hypothesis' if not ranked else
                              'piecewise_plane_hypothesis' if len(ranked) == 1 else 'ambiguous_piecewise_planes')})
    return result


def analyze(positions, triangles, options=Options(), loop_ids=None, cancel=None):
    options.validate()
    boundary = extract(positions, triangles, cancel=cancel)
    result = {'schemaVersion': 1, 'sourceHash': boundary['sourceHash'], 'options': asdict(options),
              'analysisComplete': False, 'loops': [], 'facesGenerated': 0}
    if not boundary['extractionAccepted']:
        result['refusal'] = boundary['refusal']
        return result
    selection = select_domains(boundary, loop_ids)
    result['selection'] = selection
    work = Work(options, cancel)
    for loop in boundary['loops']:
        if loop['id'] not in selection['loopIds']:
            continue
        try:
            result['loops'].append(loop_analysis(positions, loop, options, work))
        except AnalysisStopped as error:
            result['refusal'] = str(error)
            result['incompleteLoopId'] = loop['id']
            break
    else:
        result['analysisComplete'] = True
    result['work'] = {'fits': work.fits, 'states': work.states}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--select', action='append')
    for option in fields(Options):
        parser.add_argument('--' + option.name.replace('_', '-'), type=option.type, default=option.default)
    args = parser.parse_args()
    options = Options(**{option.name: getattr(args, option.name) for option in fields(Options)})
    report = analyze(*read_mesh(args.source), options, args.select)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
    print(json.dumps({'analysisComplete': report['analysisComplete'], 'work': report.get('work'),
                      'statuses': [loop['status'] for loop in report['loops']],
                      'refusal': report.get('refusal')}, indent=2))
    return 0 if report['analysisComplete'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
