"""Exact triangle intersections for the offline Remesh/Cap diagnostic.

Binary float positions are represented by integers with one shared power-of-two
scale. Plane signs and rational clipping therefore need no model-size epsilon.
This checks geometry, including adjacent faces that overlap beyond their common
edge. It is deliberately separate from the Unity runtime/BVH query contract.
"""
from dataclasses import dataclass
from fractions import Fraction

import numpy as np


def subtract(a, b):
    return tuple(x-y for x, y in zip(a, b))


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def cross(a, b):
    return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])


def orient(a, b, c):
    u, v = subtract(b, a), subtract(c, a)
    return u[0]*v[1]-u[1]*v[0]


def interpolate(a, b, t):
    return tuple(x+(y-x)*t for x, y in zip(a, b))


def exact_positions(positions):
    if not np.isfinite(positions).all():
        raise ValueError("Positions must be finite")
    ratios = [[float(x).as_integer_ratio() for x in p] for p in positions]
    scale = max(d for row in ratios for _, d in row)
    return [tuple(n*(scale//d) for n, d in row) for row in ratios], scale


@dataclass(frozen=True)
class Intersection:
    kind: str
    # One point, the two ends of a segment, or a clipped coplanar polygon.
    points: tuple


def section(triangle, distances):
    points = [tuple(Fraction(x) for x in p) for p, d in zip(triangle, distances) if d == 0]
    for i in range(3):
        j = (i+1) % 3
        if distances[i]*distances[j] < 0:
            t = Fraction(distances[i], distances[i]-distances[j])
            points.append(interpolate(triangle[i], triangle[j], t))
    return list(dict.fromkeys(points))


def same_side(distances):
    return min(distances) > 0 or max(distances) < 0


def allowed_contact(points, shared):
    if not shared:
        return False
    if len(shared) == 1:
        return all(p == shared[0] for p in points)
    if len(shared) != 2:
        return False
    a, b = shared
    direction = subtract(b, a)
    axis = max(range(len(a)), key=lambda k: abs(direction[k]))
    for p in points:
        t = Fraction(p[axis]-a[axis], direction[axis])
        if not 0 <= t <= 1 or p != interpolate(a, b, t):
            return False
    return True


def coplanar(a, b, normal, shared):
    drop = max(range(3), key=lambda k: abs(normal[k]))
    axes = [k for k in range(3) if k != drop]
    project = lambda p: tuple(p[k] for k in axes)
    pa, pb = list(map(project, a)), list(map(project, b))
    sign = 1 if orient(*pb) > 0 else -1
    polygon = pa
    for i in range(3):
        start, end = pb[i], pb[(i+1) % 3]
        output = []
        if not polygon:
            return None
        previous = polygon[-1]
        dp = sign*orient(start, end, previous)
        for current in polygon:
            dc = sign*orient(start, end, current)
            if (dc >= 0) != (dp >= 0):
                output.append(interpolate(previous, current, Fraction(dp, dp-dc)))
            if dc >= 0:
                output.append(current)
            previous, dp = current, dc
        polygon = list(dict.fromkeys(output))
    if not polygon:
        return None
    area = sum(p[0]*q[1]-p[1]*q[0] for p, q in zip(polygon, polygon[1:]+polygon[:1]))
    if area == 0 and allowed_contact(polygon, list(map(project, shared))):
        return None
    def lift(p):
        q = [Fraction(0)]*3
        q[axes[0]], q[axes[1]] = p
        q[drop] = Fraction(dot(normal, a[0])-sum(normal[k]*q[k] for k in axes), normal[drop])
        return tuple(q)
    kind = "coplanar_overlap" if area else "segment_contact" if len(polygon) > 1 else "point_contact"
    return Intersection(kind, tuple(map(lift, polygon)))


def triangle_intersection(a, b):
    na = cross(subtract(a[1], a[0]), subtract(a[2], a[0]))
    nb = cross(subtract(b[1], b[0]), subtract(b[2], b[0]))
    if na == (0, 0, 0) or nb == (0, 0, 0):
        raise ValueError("Degenerate triangle must be diagnosed separately")
    da = [dot(na, subtract(p, a[0])) for p in b]
    if same_side(da):
        return None
    db = [dot(nb, subtract(p, b[0])) for p in a]
    if same_side(db):
        return None
    shared = sorted(set(a).intersection(b))
    if all(d == 0 for d in da):
        return coplanar(a, b, na, shared)
    direction = cross(na, nb)
    axis = max(range(3), key=lambda k: abs(direction[k]))
    sa, sb = section(a, db), section(b, da)
    if not sa or not sb:
        return None
    sa.sort(key=lambda p: p[axis]); sb.sort(key=lambda p: p[axis])
    lo, hi = max(sa[0][axis], sb[0][axis]), min(sa[-1][axis], sb[-1][axis])
    if lo > hi:
        return None
    if sa[-1][axis] == sa[0][axis]:
        points = (sa[0],)
    else:
        def at(x):
            return interpolate(sa[0], sa[-1], Fraction(x-sa[0][axis], sa[-1][axis]-sa[0][axis]))
        points = tuple(dict.fromkeys((at(lo), at(hi))))
    if allowed_contact(points, shared):
        return None
    crossing = min(da) < 0 < max(da) and min(db) < 0 < max(db)
    kind = "crossing" if lo < hi and crossing else "segment_contact" if lo < hi else "point_contact"
    return Intersection(kind, points)


class BoundsTree:
    """Conservative AABB broad phase; exact classification happens afterwards."""
    def __init__(self, triangles):
        self.minimum = triangles.min(axis=1)
        self.maximum = triangles.max(axis=1)
        self.nodes = []
        if len(triangles):
            self._build(np.arange(len(triangles)))

    def _build(self, indices):
        node = len(self.nodes)
        lo = self.minimum[indices].min(axis=0)
        hi = self.maximum[indices].max(axis=0)
        self.nodes.append((lo, hi, -1, -1, indices))
        if len(indices) > 8:
            centers = (self.minimum[indices]+self.maximum[indices])*0.5
            axis = int(np.ptp(centers, axis=0).argmax())
            order = indices[np.argsort(centers[:, axis], kind='stable')]
            mid = len(order)//2
            left, right = self._build(order[:mid]), self._build(order[mid:])
            self.nodes[node] = (lo, hi, left, right, None)
        return node

    def query(self, lower, upper):
        stack = [0] if self.nodes else []
        while stack:
            lo, hi, left, right, indices = self.nodes[stack.pop()]
            if np.any(hi < lower) or np.any(lo > upper):
                continue
            if indices is None:
                stack.extend((right, left))
            else:
                mask = np.all(self.maximum[indices] >= lower, axis=1) & np.all(self.minimum[indices] <= upper, axis=1)
                yield from indices[mask]
