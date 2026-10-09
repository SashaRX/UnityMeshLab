"""Offline orientable-manifold component signatures; not a shape/solid audit."""
from collections import defaultdict

import numpy as np

from boundaries import Links, extract


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
