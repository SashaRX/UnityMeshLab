using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Double-sided orthographic coverage in six fixed source-local directions.
    // This is a ranking approximation, independent of Editor GPU/material state.
    internal sealed class LodSilhouetteValidation
    {
        const int Size = 128;
        static readonly Vector3[] Directions = { Vector3.right,Vector3.up,Vector3.forward,
            new Vector3(1,1,1),new Vector3(1,2,-1),new Vector3(-2,1,1) };
        internal struct Metrics { internal float mean, maximum; }
        sealed class View
        {
            internal Vector3 horizontal, vertical;
            internal Vector2 minimum, extent;
            internal bool[] source;
        }
        readonly List<View> views = new List<View>();

        internal LodSilhouetteValidation(Mesh source,Func<bool> cancelled = null)
        {
            var positions = source.vertices; var triangles = ReadTriangles(source);
            foreach (var direction in Directions)
            {
                Check(cancelled);
                var normal = direction.normalized;
                var horizontal = Vector3.Cross(normal,Mathf.Abs(normal.y) > .9f ? Vector3.forward : Vector3.up).normalized;
                var vertical = Vector3.Cross(horizontal,normal).normalized;
                var minimum = Vector2.one*float.PositiveInfinity; var maximum = Vector2.one*float.NegativeInfinity;
                foreach (var position in positions)
                {
                    var projected = new Vector2(Vector3.Dot(position,horizontal),Vector3.Dot(position,vertical));
                    minimum = Vector2.Min(minimum,projected); maximum = Vector2.Max(maximum,projected);
                }
                float minimumExtent = Mathf.Max(source.bounds.size.magnitude*1e-5f,1e-7f);
                var extent = maximum-minimum;
                extent.x = Mathf.Max(extent.x,minimumExtent); extent.y = Mathf.Max(extent.y,minimumExtent);
                var view = new View { horizontal = horizontal,vertical = vertical,
                    minimum = minimum-extent*.05f,extent = extent*1.1f };
                view.source = Rasterize(positions,triangles,view,cancelled); views.Add(view);
            }
        }

        internal Metrics Measure(Mesh target,Func<bool> cancelled = null)
        {
            var metrics = new Metrics(); var positions = target.vertices; var triangles = ReadTriangles(target);
            foreach (var view in views)
            {
                Check(cancelled);
                var coverage = Rasterize(positions,triangles,view,cancelled);
                int union = 0, difference = 0;
                for (int i = 0; i < coverage.Length; i++)
                {
                    if (view.source[i] || coverage[i]) union++;
                    if (view.source[i] != coverage[i]) difference++;
                }
                float error = union > 0 ? (float)difference/union : 0;
                metrics.mean += error/views.Count; metrics.maximum = Mathf.Max(metrics.maximum,error);
            }
            return metrics;
        }

        static int[] ReadTriangles(Mesh mesh)
        {
            var triangles = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) triangles.AddRange(LodMeshData.Triangles(mesh,s));
            return triangles.ToArray();
        }

        static bool[] Rasterize(Vector3[] positions,int[] triangles,View view,Func<bool> cancelled)
        {
            var projected = new Vector2[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                projected[i] = new Vector2((Vector3.Dot(positions[i],view.horizontal)-view.minimum.x)/view.extent.x*Size,
                    (Vector3.Dot(positions[i],view.vertical)-view.minimum.y)/view.extent.y*Size);
            var coverage = new bool[Size*Size];
            for (int i = 0; i < triangles.Length; i += 3)
            {
                if (i%384 == 0) Check(cancelled);
                var a = projected[triangles[i]]; var b = projected[triangles[i+1]]; var c = projected[triangles[i+2]];
                float area = Cross(b-a,c-a);
                if (Mathf.Abs(area) < 1e-8f) continue;
                int left = Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))));
                int right = Mathf.Min(Size-1,Mathf.FloorToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x))));
                int bottom = Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))));
                int top = Mathf.Min(Size-1,Mathf.FloorToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))));
                for (int y = bottom; y <= top; y++) for (int x = left; x <= right; x++)
                {
                    int pixel = y*Size+x;
                    if (coverage[pixel]) continue;
                    var point = new Vector2(x+.5f,y+.5f);
                    float e0 = Cross(b-a,point-a), e1 = Cross(c-b,point-b), e2 = Cross(a-c,point-c);
                    if (area > 0 ? e0 >= -1e-6f && e1 >= -1e-6f && e2 >= -1e-6f
                        : e0 <= 1e-6f && e1 <= 1e-6f && e2 <= 1e-6f) coverage[pixel] = true;
                }
            }
            return coverage;
        }
        static float Cross(Vector2 a,Vector2 b) => a.x*b.y-a.y*b.x;
        static void Check(Func<bool> cancelled)
        {
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD silhouette validation cancelled.");
        }
    }
}
