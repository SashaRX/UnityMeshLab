"""Audit original/Cap intersections and spatially associate voxel defects.

Inputs are private geometry captures, not Unity assets. Face IDs are zero-based.
The capped capture must keep the original face geometry as its prefix.
"""
import argparse
import collections
import json
from pathlib import Path
import time

import numpy as np

from intersections import BoundsTree, cross, exact_positions, subtract, triangle_intersection
from topology import inspect


def read_mesh(path):
    data = Path(path).read_bytes()
    if len(data) < 8:
        raise ValueError(f"Incomplete mesh header: {path}")
    vertices, indices = map(int, np.frombuffer(data, '<u4', count=2))
    if vertices < 3 or indices < 3 or indices % 3 or len(data) != 8+vertices*12+indices*4:
        raise ValueError(f"Invalid mesh counts/length: {path}")
    p = np.frombuffer(data, '<f4', count=vertices*3, offset=8).reshape(-1,3).copy()
    ix = np.frombuffer(data, '<u4', count=indices, offset=8+vertices*12).reshape(-1,3).copy()
    if not np.isfinite(p).all() or ix.max() >= vertices:
        raise ValueError(f"Invalid mesh positions/indices: {path}")
    return p, ix


def cap_groups(indices, original_count):
    caps = indices[original_count:]
    parent = list(range(len(caps)))
    def root(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]; a = parent[a]
        return a
    edges = {}
    for face, triangle in enumerate(caps):
        for a,b in zip(triangle, np.roll(triangle,-1)):
            key = tuple(sorted((int(a),int(b))))
            if key in edges:
                parent[root(face)] = root(edges[key])
            else:
                edges[key] = face
    labels, roots = [], {}
    for face in range(len(caps)):
        component = root(face)
        if component not in roots:
            roots[component] = len(roots)
        labels.append(roots[component])
    return np.array([-1]*original_count+labels, dtype=int)


def scan(positions, indices, original_count):
    started = time.perf_counter()
    triangles = positions[indices].astype('d')
    tree = BoundsTree(triangles)
    points, scale = exact_positions(positions)
    exact = [tuple(points[int(v)] for v in face) for face in indices]
    valid = [cross(subtract(t[1],t[0]), subtract(t[2],t[0])) != (0,0,0) for t in exact]
    groups = cap_groups(indices, original_count)
    pairs, counts = [], collections.defaultdict(collections.Counter)
    candidates = 0
    for first, triangle in enumerate(triangles):
        if not valid[first]:
            continue
        for second in tree.query(triangle.min(axis=0), triangle.max(axis=0)):
            second = int(second)
            if second <= first or not valid[second]:
                continue
            candidates += 1
            hit = triangle_intersection(exact[first], exact[second])
            if hit is None:
                continue
            role = 'source-source' if second < original_count else 'source-cap' if first < original_count else 'cap-cap'
            counts[role][hit.kind] += 1
            pairs.append({'faceA': first, 'faceB': second, 'role': role, 'kind': hit.kind,
                          'capA': int(groups[first]), 'capB': int(groups[second]),
                          'points': [[float(x/scale) for x in p] for p in hit.points]})
        if first and first % 1000 == 0:
            print(f"Scanned {first}/{len(triangles)} faces, {len(pairs)} intersections/contacts", flush=True)
    return {'positions': positions, 'indices': indices, 'groups': groups, 'pairs': pairs,
            'summary': {'faces': len(indices), 'originalFaces': original_count, 'capFaces': len(indices)-original_count,
                        'capComponents': int(groups.max()+1), 'degenerateFaces': sum(not v for v in valid),
                        'degenerateCapFaces': sum(not v for v in valid[original_count:]),
                        'boundsCandidates': candidates, 'pairCounts': dict(counts),
                        'capGeometryAccepted': all(valid[original_count:]) and not any(p['role']!='source-source' for p in pairs),
                        'newCrossingOrOverlapPairs': sum(p['role']!='source-source' and p['kind'] in ('crossing','coplanar_overlap') for p in pairs),
                        'newNonadjacentContactPairs': sum(p['role']!='source-source' and p['kind'] not in ('crossing','coplanar_overlap') for p in pairs),
                        'seconds': round(time.perf_counter()-started,3)}}


class NearestTriangles:
    def __init__(self, triangles):
        self.a = triangles[:,0].astype('d')
        self.edges = [triangles[:,(k+1)%3].astype('d')-triangles[:,k] for k in range(3)]
        self.origins = [triangles[:,k].astype('d') for k in range(3)]
        self.ab = triangles[:,1].astype('d')-self.a
        self.ac = triangles[:,2].astype('d')-self.a
        self.normal = np.cross(self.ab,self.ac)
        self.normal2 = np.einsum('ij,ij->i',self.normal,self.normal)

    def distances(self, point):
        r = point-self.a
        v = np.divide(np.einsum('ij,ij->i',np.cross(r,self.ac),self.normal),self.normal2,
                      out=np.zeros(len(self.a)),where=self.normal2>0)
        w = np.divide(np.einsum('ij,ij->i',np.cross(self.ab,r),self.normal),self.normal2,
                      out=np.zeros(len(self.a)),where=self.normal2>0)
        inside = (self.normal2>0)&(v>=0)&(w>=0)&(v+w<=1)
        distance = np.full(len(self.a),np.inf)
        dots = np.einsum('ij,ij->i',r,self.normal)
        distance[inside] = dots[inside]**2/self.normal2[inside]
        for a,edge in zip(self.origins,self.edges):
            length2 = np.einsum('ij,ij->i',edge,edge)
            t = np.divide(np.einsum('ij,ij->i',point-a,edge),length2,
                          out=np.zeros(len(edge)),where=length2>0).clip(0,1)
            delta = point-(a+edge*t[:,None])
            distance = np.minimum(distance,np.einsum('ij,ij->i',delta,delta))
        return distance


def pair_geometry(pairs):
    triangles = []
    for pair in pairs:
        p = pair['points']
        if len(p) == 1:
            triangles.append([p[0]]*3)
        elif len(p) == 2:
            triangles.append([p[0],p[1],p[1]])
        else:
            triangles.extend([p[0],p[k],p[k+1]] for k in range(1,len(p)-1))
    return np.array(triangles,dtype=float).reshape(-1,3,3)


def voxel_samples(positions, indices):
    sorted_faces = np.sort(indices,axis=1)
    _, first, counts = np.unique(sorted_faces,axis=0,return_index=True,return_counts=True)
    faces = first[counts>1]
    edge_corners = indices[:,[0,1,1,2,2,0]].reshape(-1,2)
    edges, incidence = np.unique(np.sort(edge_corners,axis=1),axis=0,return_counts=True)
    edges = edges[incidence>2]
    points = [positions[indices[f]].astype('d').mean(axis=0) for f in faces]
    points += [positions[e].astype('d').mean(axis=0) for e in edges]
    info = [{'kind':'duplicate_face','face':int(f)} for f in faces]
    info += [{'kind':'non_manifold_edge','vertices':list(map(int,e))} for e in edges]
    return np.array(points), info, {'duplicateFaceGroups':len(faces),
                                  'duplicateFaceIncidences':int(counts[counts>1].sum()),
                                  'duplicateExcessFaces':int((counts[counts>1]-1).sum()),
                                  'nonManifoldEdges':len(edges)}


def associate(scan_result, voxel_path, resolution):
    with np.load(voxel_path) as data:
        p, ix = data['positions'], data['indices']
    if not np.isfinite(p).all() or ix.ndim != 2 or ix.shape[1] != 3 or ix.max() >= len(p):
        raise ValueError(f"Invalid voxel mesh: {voxel_path}")
    samples, info, counts = voxel_samples(p,ix)
    vertices, indices, groups = scan_result['positions'], scan_result['indices'], scan_result['groups']
    source_count = scan_result['summary']['originalFaces']
    nearest = NearestTriangles(vertices[indices])
    cell = float(np.ptp(vertices.astype('d'),axis=0).max())*(resolution+0.01)/resolution/(resolution-2)
    sets = {'existingCrossings': [pair for pair in scan_result['pairs'] if pair['role']=='source-source' and pair['kind'] in ('crossing','coplanar_overlap')],
            'capCrossings': [pair for pair in scan_result['pairs'] if pair['role']!='source-source' and pair['kind'] in ('crossing','coplanar_overlap')],
            'existingContacts': [pair for pair in scan_result['pairs'] if pair['role']=='source-source' and pair['kind'] not in ('crossing','coplanar_overlap')],
            'capContacts': [pair for pair in scan_result['pairs'] if pair['role']!='source-source' and pair['kind'] not in ('crossing','coplanar_overlap')]}
    geometries = {key:NearestTriangles(pair_geometry(pairs)) for key,pairs in sets.items() if pairs}
    summary = collections.defaultdict(collections.Counter)
    result = []
    for point, row in zip(samples,info):
        distances = nearest.distances(point)
        first = int(distances[:source_count].argmin())
        second = int(distances[source_count:].argmin()+source_count)
        tag = 'source' if distances[first]<=distances[second] else 'cap'
        summary[row['kind']][tag+'Nearest'] += 1
        row = dict(row,point=point.tolist(),nearest=tag,sourceFace=first,capFace=second,capComponent=int(groups[second]),
                   sourceDistance=float(np.sqrt(distances[first])),capDistance=float(np.sqrt(distances[second])))
        for key,geometry in geometries.items():
            distance = float(np.sqrt(geometry.distances(point).min()))
            row[key+'Distance'] = distance
            for cells in (0.5,1,2):
                if distance <= cell*cells:
                    summary[row['kind']][key+'Within'+str(cells)+'Cells'] += 1
        result.append(row)
    return {'file':str(Path(voxel_path).resolve()),'resolution':resolution,'cellSize':cell,
            'counts':counts,'association':dict(summary),'samples':result}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source',required=True,type=Path)
    parser.add_argument('--capped',required=True,type=Path)
    parser.add_argument('--voxel',action='append',type=Path,default=[])
    parser.add_argument('--resolution',type=int,default=128)
    parser.add_argument('--output',required=True,type=Path)
    args = parser.parse_args()
    if args.resolution < 4:
        parser.error('Resolution must be at least four')
    sp,si = read_mesh(args.source)
    p,ix = read_mesh(args.capped)
    if len(ix)<len(si) or not np.array_equal(sp[si],p[ix[:len(si)]]):
        raise ValueError('Capped input changed or reordered original face geometry')
    if len(ix)==len(si):
        raise ValueError('No added cap faces to associate')
    result = scan(p,ix,len(si))
    topology = inspect(p,ix)
    report = {'schemaVersion':1,'faceIds':'zero-based','source':str(args.source.resolve()),'capped':str(args.capped.resolve()),
              'summary':result['summary'],'pairs':result['pairs'],
              'topology':topology,'capAccepted':bool(topology['closedManifold'] and result['summary']['capGeometryAccepted']),
              'solidCandidateAccepted':bool(topology['closedManifold'] and not result['pairs']),
              'voxels':[associate(result,path,args.resolution) for path in args.voxel]}
    args.output.parent.mkdir(parents=True,exist_ok=True)
    args.output.write_text(json.dumps(report,indent=2),encoding='utf-8')
    print(json.dumps(report['summary'],indent=2),flush=True)
    for voxel in report['voxels']:
        print(Path(voxel['file']).name,json.dumps(voxel['association'],indent=2),flush=True)
    print('Report:',args.output.resolve(),flush=True)


if __name__ == '__main__':
    main()
