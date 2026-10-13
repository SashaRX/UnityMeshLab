"""Virtual +/- averaged boundary co-normal growth; evidence, not closure choice.

Computes closest approaches of bounded vertex trajectories. Does not move source
vertices or certify the swept surface against source triangles/occupied volume.
"""
from dataclasses import asdict, dataclass

import numpy as np

from boundaries import checked_arrays, extract, select_domains
from incremental import Refused


@dataclass(frozen=True)
class Options:
    reach_ratio: float = 1.0
    hit_ratio: float = 1e-4
    min_advance_ratio: float = 1e-5
    max_loop_vertices: int = 64
    max_pair_tests: int = 20_000
    max_faces: int = 8192
    max_vertices: int = 16384

    def validate(self):
        for name in ('reach_ratio', 'hit_ratio', 'min_advance_ratio'):
            value = getattr(self, name)
            if not np.isfinite(value) or value <= 0:
                raise ValueError('Growth ratios must be finite and positive')
        if self.hit_ratio >= self.reach_ratio or self.min_advance_ratio >= self.reach_ratio:
            raise ValueError('Growth hit/advance thresholds must be smaller than reach')
        if self.reach_ratio > 16:
            raise ValueError('Growth reach exceeds the bounded local profile')
        for name in ('max_loop_vertices', 'max_pair_tests', 'max_faces', 'max_vertices'):
            if type(getattr(self, name)) is not int or getattr(self, name) < 1:
                raise ValueError('Growth budgets must be positive integers')


def closest_trajectories(p, u, q, v, reach):
    """Closest points for p+s*u and q+t*v with unit directions, 0<=s,t<=reach."""
    w = p - q
    dot = float(np.clip(u @ v, -1, 1))
    d, e = float(u @ w), float(v @ w)
    determinant = 1 - dot * dot
    pairs = []
    if determinant > 1e-12:
        s, t = (dot * e - d) / determinant, (e - dot * d) / determinant
        if 0 <= s <= reach and 0 <= t <= reach:
            pairs.append((s, t))
    for s in (0.0, reach):
        pairs.append((s, float(np.clip(e + dot * s, 0, reach))))
    for t in (0.0, reach):
        pairs.append((float(np.clip(dot * t - d, 0, reach)), t))
    if 1 - dot > 1e-12:
        common = float(np.clip((e - d) / (2 * (1 - dot)), 0, reach))
        pairs.append((common, common))
    s, t = min(pairs, key=lambda st: (float(np.linalg.norm(w + st[0] * u - st[1] * v)),
                                     sum(st), abs(st[0] - st[1]), st))
    first, second = p + s * u, q + t * v
    return s, t, float(np.linalg.norm(first - second)), (first + second) / 2


def directions(points, faces, loop):
    ids = [h['sourceVertices'][0] for h in loop['halfedges']]
    q = points[ids]
    edges = np.roll(q, -1, axis=0) - q
    lengths = np.linalg.norm(edges, axis=1)
    unit = edges / lengths[:, None]
    normals = []
    for h in loop['halfedges']:
        a, b, c = points[faces[h['face']]]
        normal = np.cross(b - a, c - a)
        length = np.linalg.norm(normal)
        if not np.isfinite(length) or length == 0:
            raise Refused('unsupported_source_normal')
        normals.append(normal / length)
    normals = np.asarray(normals)
    result = []
    for i in range(len(q)):
        average_normal = normals[i - 1] * lengths[i - 1] + normals[i] * lengths[i]
        tangent = unit[i - 1] + unit[i]
        outward = np.cross(tangent, average_normal)
        length = float(np.linalg.norm(outward))
        if length <= 1e-12 or not np.isfinite(length):
            raise Refused('underdetermined_vertex_conormal')
        result.append(outward / length)
    return q, np.asarray(result)


def evaluate(positions, triangles, source_hash, loop_ids, options=Options(), cancel=None):
    p, ix = checked_arrays(positions, triangles)
    options.validate()
    report = {'schemaVersion': 1, 'sourceHash': source_hash, 'options': asdict(options),
              'analysisComplete': False, 'pairTests': 0, 'sides': [],
              'closureTypeCertified': False, 'sweptSurfaceCertified': False}

    def check():
        if cancel is not None and cancel():
            raise Refused('cancelled')

    try:
        check()
        if len(p) > options.max_vertices or len(ix) > options.max_faces:
            raise Refused('mesh_budget_exceeded')
        boundary = extract(p, ix, cancel=cancel)
        if not boundary['extractionAccepted']:
            raise Refused('source_boundary_' + boundary['refusal'])
        selection = select_domains(boundary, loop_ids, source_hash)
        lookup = {loop['id']: loop for loop in boundary['loops']}
        loops = [lookup[identity] for identity in selection['loopIds']]
        if not loops or selection['requiresJunctionResolution']:
            raise Refused('empty_selection_or_unresolved_junction')
        if any(len(loop['halfedges']) > options.max_loop_vertices for loop in loops):
            raise Refused('loop_vertex_budget_exceeded')
        ids = [h['sourceVertices'][0] for loop in loops for h in loop['halfedges']]
        selected = p[ids].astype('d')
        scale = float(np.linalg.norm(selected.max(axis=0) - selected.min(axis=0)))
        if not np.isfinite(scale) or scale == 0:
            raise Refused('unsupported_coordinate_range')
        local = (p.astype('d') - selected[0]) / scale
        rings = [directions(local, ix, loop) for loop in loops]
        report.update({'scale': scale, 'origin': selected[0].tolist(), 'selection': selection,
                       'outwardDirections': [{'loopId': loop['id'],
                                              'sourceVertices': [h['sourceVertices'][0] for h in loop['halfedges']],
                                              'unitConormals': ring[1].tolist()}
                                              for loop, ring in zip(loops, rings)]})
        for side, sign in (('outward_from_source', 1), ('inward_to_source', -1)):
            self_rows, between_rows = [], []

            def meeting(a, i, b, j):
                check()
                if report['pairTests'] >= options.max_pair_tests:
                    raise Refused('pair_test_budget_exceeded')
                report['pairTests'] += 1
                s, t, distance, point = closest_trajectories(
                    rings[a][0][i], rings[a][1][i] * sign, rings[b][0][j], rings[b][1][j] * sign, options.reach_ratio)
                if distance <= options.hit_ratio and min(s, t) >= options.min_advance_ratio:
                    return {'firstVertex': i, 'secondVertex': j, 'distanceRatio': distance,
                            'firstAdvanceRatio': s, 'secondAdvanceRatio': t, 'pointLocal': point.tolist()}
                return None

            for a, (q, _) in enumerate(rings):
                hits = [hit for i in range(len(q)) for j in range(i + 1, len(q))
                        if j - i not in (1, len(q) - 1) for hit in [meeting(a, i, a, j)] if hit is not None]
                covered = {hit[key] for hit in hits for key in ('firstVertex', 'secondVertex')}
                spread = 0.0
                if hits:
                    points = np.asarray([hit['pointLocal'] for hit in hits])
                    spread = float(np.linalg.norm(points - points.mean(axis=0), axis=1).max())
                self_rows.append({'loopId': loops[a]['id'], 'hitPairCount': len(hits),
                                  'vertexCoverage': len(covered) / len(q), 'meetingSpreadRatio': spread})
                for b in range(a + 1, len(rings)):
                    hits = [hit for i in range(len(q)) for j in range(len(rings[b][0]))
                            for hit in [meeting(a, i, b, j)] if hit is not None]
                    def rank(hit):
                        return (hit['distanceRatio'], hit['firstAdvanceRatio'] + hit['secondAdvanceRatio'],
                                hit['firstVertex'], hit['secondVertex'])
                    first, second = {}, {}
                    for hit in sorted(hits, key=rank):
                        first.setdefault(hit['firstVertex'], hit)
                        second.setdefault(hit['secondVertex'], hit)
                    mutual = [hit for i, hit in first.items() if second[hit['secondVertex']]['firstVertex'] == i]
                    between_rows.append({'loopIds': [loops[a]['id'], loops[b]['id']], 'hitPairCount': len(hits),
                                         'mutualPairCount': len(mutual), 'firstCoverage': len(mutual) / len(q),
                                         'secondCoverage': len(mutual) / len(rings[b][0]), 'mutualPairs': mutual})
            report['sides'].append({'side': side, 'selfConvergence': self_rows, 'betweenLoops': between_rows})
        check()
        report['analysisComplete'] = True
    except Refused as error:
        report['refusal'] = str(error)
        # Incomplete evidence cannot be treated as a finished classification.
        report['sides'] = []
    return report
