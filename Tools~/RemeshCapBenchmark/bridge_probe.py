"""Known torus-band Bridge versus separate disks; topology intent is explicit.

No automatic pairing, correspondence, Bridge generation or Unity integration.
"""
import argparse
from collections import defaultdict
import json
from pathlib import Path

import numpy as np

from analyze import scan
from boundaries import Links, extract
from incremental_probe import compact_box, reference_patch
from incremental import attempt
from planes import analyze
from topology import inspect


def signature(positions, triangles):
    """Euler/genus per orientable manifold component, including open boundaries.

    Genus is not reported for refused or singular source topology. Geometry
    intersections and intended reconstruction remain separate checks.
    """
    boundary = extract(positions, triangles)
    if not boundary['extractionAccepted'] or boundary['singularVertices']:
        return {'signatureAvailable': False, 'refusal': boundary.get('refusal', 'singular_vertex_fans')}
    _, slots = np.unique(positions, axis=0, return_inverse=True)
    faces = slots[triangles]
    links = Links(len(faces))
    edges = defaultdict(list)
    for i, triangle in enumerate(faces):
        for a, b in zip(triangle, np.roll(triangle, -1)):
            edges[tuple(sorted((int(a), int(b))))].append(i)
    for incident in edges.values():
        if len(incident) == 2:
            links.join(*incident)
    groups = defaultdict(list)
    for face in range(len(faces)):
        groups[links.root(face)].append(face)
    components = []
    for root, ids in sorted(groups.items()):
        vertices = len(np.unique(faces[ids]))
        edge_count = sum(links.root(incident[0]) == root for incident in edges.values())
        loops = sum(loop['component'] == root for loop in boundary['loops'])
        chi = vertices - edge_count + len(ids)
        twice_genus = 2 - loops - chi
        if twice_genus < 0 or twice_genus % 2:
            return {'signatureAvailable': False, 'refusal': 'inconsistent_manifold_signature'}
        components.append({'vertices': vertices, 'edges': edge_count, 'faces': len(ids),
                           'boundaryLoops': loops, 'eulerCharacteristic': chi, 'genus': twice_genus // 2})
    return {'signatureAvailable': True, 'componentCount': len(components), 'components': components,
            'eulerCharacteristic': sum(c['eulerCharacteristic'] for c in components),
            'boundaryLoops': sum(c['boundaryLoops'] for c in components)}


def matches_intent(actual, expected_genera):
    return (actual['signatureAvailable'] and actual['boundaryLoops'] == 0 and
            sorted(c['genus'] for c in actual['components']) == sorted(expected_genera))


def torus_gap(major=12, minor=8):
    if type(major) is not int or type(minor) is not int or major < 6 or minor < 4:
        raise ValueError('Torus fixture needs at least six major and four minor samples')
    theta = np.arange(major) * 2 * np.pi / major
    phi = np.arange(minor) * 2 * np.pi / minor
    points = np.array([[(2 + .5 * np.cos(v)) * np.cos(u),
                        (2 + .5 * np.cos(v)) * np.sin(u), .5 * np.sin(v)]
                       for u in theta for v in phi], dtype='f4')
    strips = []
    for i in range(major):
        strip = []
        for j in range(minor):
            a, b = i * minor + j, ((i + 1) % major) * minor + j
            c, d = ((i + 1) % major) * minor + (j + 1) % minor, i * minor + (j + 1) % minor
            strip.extend(((a, b, c), (a, c, d)))
        strips.append(np.array(strip, dtype='u4'))
    source = np.concatenate(strips[1:])
    bridge = np.concatenate((source, strips[0]))
    reference = np.concatenate(strips)
    boundary = extract(points, source)
    disks_p = list(points.copy())
    disks = []
    for loop in boundary['loops']:
        ring = [h['sourceVertices'][0] for h in loop['halfedges']]
        center = len(disks_p)
        disks_p.append(points[ring].astype('d').mean(axis=0).astype('f4'))
        disks.extend((b, a, center) for a, b in zip(ring, ring[1:] + ring[:1]))
    return points, source, bridge, np.asarray(disks_p, dtype='f4'), np.concatenate((source, np.asarray(disks, dtype='u4'))), reference


def candidate_report(points, faces, source_count, expected_genera):
    topology = inspect(points, faces)
    geometry = scan(points, faces, source_count)['summary']
    sig = signature(points, faces)
    intent = matches_intent(sig, expected_genera)
    return {'topology': topology, 'geometry': geometry, 'signature': sig,
            'explicitTopologyIntentMatched': intent,
            'referenceGatesPassed': topology['closedManifold'] and geometry['capGeometryAccepted'] and intent}


def sequential_disks(points, faces):
    """Demonstrate that local transaction acceptance cannot choose closure type."""
    steps = []
    for _ in range(2):
        boundary = extract(points, faces)
        loop = boundary['loops'][0]
        ring = [h['sourceVertices'][0] for h in loop['halfedges']]
        center = len(points)
        extra = points[ring].astype('d').mean(axis=0).astype(points.dtype)[None, :]
        caps = np.asarray([(b, a, center) for a, b in zip(ring, ring[1:] + ring[:1])], dtype=faces.dtype)
        points, faces, step = attempt(points, faces, boundary['sourceHash'],
                                      [h['halfedge'] for h in loop['halfedges']], extra, caps)
        steps.append(step)
        if not step['accepted']:
            break
    return {'stepsAccepted': [step['accepted'] for step in steps],
            'signature': signature(points, faces), 'expectedTorusIntentMatched': matches_intent(signature(points, faces), [1])}


def build_report():
    p, source, bridge, disks_p, disks, reference = torus_gap()
    plane_report = analyze(p, source)
    torus = {'sourceSignature': signature(p, source), 'referenceSignature': signature(p, reference),
             'expectedClosedGenera': [1],
             'wholePlaneStatuses': [loop['status'] for loop in plane_report['loops']],
             'separateDisks': candidate_report(disks_p, disks, len(source), [1]),
             'referenceBridge': candidate_report(p, bridge, len(source), [1]),
             'sequentialDisks': sequential_disks(p, source),
             'sourcePreserved': np.array_equal(source, bridge[:len(source)]) and
                                np.array_equal(source, disks[:len(source)]) and np.array_equal(p, disks_p[:len(p)])}
    box_p, box_source = compact_box([0, 1])
    box_caps = np.concatenate([reference_patch(box_p, face)[1] for face in (0, 1)])
    box = {'sourceSignature': signature(box_p, box_source), 'expectedClosedGenera': [0],
           'referenceDisks': candidate_report(box_p, np.concatenate((box_source, box_caps)), len(box_source), [0])}
    result = {'schemaVersion': 1, 'purpose': 'Known topology-intent fixtures, not automatic Bridge detection',
              'torusBandGap': torus, 'boxOppositeFaces': box}
    result['referenceProbePassed'] = (
        torus['sourcePreserved'] and torus['separateDisks']['topology']['closedManifold'] and
        torus['separateDisks']['geometry']['capGeometryAccepted'] and
        not torus['separateDisks']['explicitTopologyIntentMatched'] and
        torus['sequentialDisks']['stepsAccepted'] == [True, True] and
        not torus['sequentialDisks']['expectedTorusIntentMatched'] and
        torus['referenceBridge']['referenceGatesPassed'] and box['referenceDisks']['referenceGatesPassed'])
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    report = build_report()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2, allow_nan=False), encoding='utf-8')
    torus = report['torusBandGap']
    print(json.dumps({'referenceProbePassed': report['referenceProbePassed'],
                      'source': torus['sourceSignature'],
                      'diskGenus': torus['separateDisks']['signature']['components'][0]['genus'],
                      'bridgeGenus': torus['referenceBridge']['signature']['components'][0]['genus']}))
    return 0 if report['referenceProbePassed'] else 2


if __name__ == '__main__':
    raise SystemExit(main())
