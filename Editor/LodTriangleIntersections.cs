using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal sealed class LodTriangleIntersections
    {
        readonly Vector3[] positions;
        readonly int[] triangles, triangleFaces;
        readonly TriangleBvh bvh;
        readonly List<int> overlaps = new List<int>();
        internal LodTriangleIntersections(List<LodSourceTopology.Face> faces, Vector3[] positions)
        {
            this.positions = positions;
            triangles = faces.SelectMany(f => f.triangles).ToArray();
            triangleFaces = faces.SelectMany((f, i) => Enumerable.Repeat(i, f.triangles.Length / 3)).ToArray();
            bvh = new TriangleBvh(positions, triangles);
        }

        internal bool Intersects(List<LodSourceTopology.Face> replacements, HashSet<int> removedFaces)
        {
            int[] added = replacements.SelectMany(f => f.triangles).ToArray();
            for (int i = 0; i < added.Length; i += 3)
            {
                var bounds = TriangleBounds(positions, added, i);
                overlaps.Clear(); bvh.CollectOverlapping(bounds, overlaps);
                foreach (int f in overlaps)
                    if (!removedFaces.Contains(triangleFaces[f]) && Overlap(positions, added, i, triangles, f * 3)) return true;
                for (int j = 0; j < i; j += 3)
                    if (bounds.Intersects(TriangleBounds(positions, added, j)) && Overlap(positions, added, i, added, j)) return true;
            }
            return false;
        }

        static Bounds TriangleBounds(Vector3[] p, int[] t, int i)
        {
            var bounds = new Bounds(p[t[i]], Vector3.zero);
            bounds.Encapsulate(p[t[i + 1]]); bounds.Encapsulate(p[t[i + 2]]);
            return bounds;
        }

        internal static bool Overlap(Vector3[] p, int[] a, int i, int[] b, int j)
        {
            Vector3 a0 = p[a[i]], a1 = p[a[i + 1]], a2 = p[a[i + 2]];
            Vector3 b0 = p[b[j]], b1 = p[b[j + 1]], b2 = p[b[j + 2]];
            float scale = Mathf.Max((a1-a0).magnitude, (a2-a0).magnitude, (b1-b0).magnitude, (b2-b0).magnitude);
            float epsilon = Mathf.Max(scale * 1e-6f, 1e-10f);
            Vector3 an = Vector3.Cross(a1-a0, a2-a0).normalized;
            Vector3 bn = Vector3.Cross(b1-b0, b2-b0).normalized;
            if (Vector3.Cross(an, bn).sqrMagnitude < 1e-10f && Mathf.Abs(Vector3.Dot(an, b0-a0)) <= epsilon)
            {
                int axis = Mathf.Abs(an.x) > Mathf.Abs(an.y) ? (Mathf.Abs(an.x) > Mathf.Abs(an.z) ? 0 : 2) : (Mathf.Abs(an.y) > Mathf.Abs(an.z) ? 1 : 2);
                Vector2[] av = { Project(a0, axis), Project(a1, axis), Project(a2, axis) };
                Vector2[] bv = { Project(b0, axis), Project(b1, axis), Project(b2, axis) };
                float areaEpsilon = epsilon * scale;
                if (Inside((av[0]+av[1]+av[2])/3, bv, areaEpsilon) || Inside((bv[0]+bv[1]+bv[2])/3, av, areaEpsilon)) return true;
                for (int x = 0; x < 3; x++)
                {
                    if (Inside(av[x], bv, areaEpsilon) || Inside(bv[x], av, areaEpsilon)) return true;
                    for (int y = 0; y < 3; y++)
                        if (Crossing(av[x], av[(x+1)%3], bv[y], bv[(y+1)%3], areaEpsilon)) return true;
                }
                return false;
            }
            return SegmentHits(a0,a1,b0,b1,b2,bn,epsilon) || SegmentHits(a1,a2,b0,b1,b2,bn,epsilon) || SegmentHits(a2,a0,b0,b1,b2,bn,epsilon) ||
                SegmentHits(b0,b1,a0,a1,a2,an,epsilon) || SegmentHits(b1,b2,a0,a1,a2,an,epsilon) || SegmentHits(b2,b0,a0,a1,a2,an,epsilon);
        }
        static bool SegmentHits(Vector3 from, Vector3 to, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float epsilon)
        {
            float d0 = Vector3.Dot(from-a, normal), d1 = Vector3.Dot(to-a, normal);
            if ((d0 >= -epsilon && d1 >= -epsilon) || (d0 <= epsilon && d1 <= epsilon)) return false;
            float t = d0 / (d0-d1);
            var point = Vector3.LerpUnclamped(from,to,t);
            Vector3 e0 = b-a, e1 = c-a, q = point-a;
            float d00 = Vector3.Dot(e0,e0), d01 = Vector3.Dot(e0,e1), d11 = Vector3.Dot(e1,e1);
            float denominator = d00*d11-d01*d01;
            if (denominator <= 0) return false;
            float u = (d11*Vector3.Dot(q,e0)-d01*Vector3.Dot(q,e1))/denominator;
            float v = (d00*Vector3.Dot(q,e1)-d01*Vector3.Dot(q,e0))/denominator;
            return u > 1e-6f && v > 1e-6f && u+v < 1-1e-6f;
        }
        static Vector2 Project(Vector3 p, int axis) => axis == 0 ? new Vector2(p.y,p.z) : axis == 1 ? new Vector2(p.x,p.z) : new Vector2(p.x,p.y);
        static float Cross(Vector2 a, Vector2 b) => a.x*b.y-a.y*b.x;
        static bool Inside(Vector2 p, Vector2[] triangle, float e)
        {
            float a = Cross(triangle[1]-triangle[0],p-triangle[0]);
            float b = Cross(triangle[2]-triangle[1],p-triangle[1]);
            float c = Cross(triangle[0]-triangle[2],p-triangle[2]);
            return (a>e && b>e && c>e) || (a < -e && b < -e && c < -e);
        }
        static bool Crossing(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float e)
        {
            float a0 = Cross(b-a,c-a), a1 = Cross(b-a,d-a), b0 = Cross(d-c,a-c), b1 = Cross(d-c,b-c);
            return ((a0>e && a1 < -e)||(a1>e && a0 < -e)) && ((b0>e && b1 < -e)||(b1>e && b0 < -e));
        }
    }
}
