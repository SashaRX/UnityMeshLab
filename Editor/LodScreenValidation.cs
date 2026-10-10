using System;
using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Deterministic, double-sided, depth-tested orthographic RGBA previews.
    // These source-local views are ranking guides, not the project's cameras/materials.
    internal sealed class LodScreenValidation
    {
        const int Size = 256;
        static readonly Vector3[] Directions = { Vector3.right,Vector3.up,Vector3.forward,
            new Vector3(1,1,1),new Vector3(1,2,-1),new Vector3(-2,1,1) };
        [Serializable] internal sealed class Report
        {
            public int views, detailViews, colorViews;
            public float detailLoss, colorLoss;
            public bool detailAccepted, colorAccepted;
            internal float Penalty(float colorWeight) => 4*(detailLoss+colorLoss*Mathf.Max(0,colorWeight));
            internal bool DoesNotWorsen(Report before) => detailLoss <= before.detailLoss+1e-6f && colorLoss <= before.colorLoss+1e-6f;
        }
        sealed class View
        {
            internal Vector3 horizontal, vertical, normal;
            internal Vector2 center;
            internal float pixelsPerUnit;
            internal LodScreenAcceptance acceptance;
        }
        sealed class Frame
        {
            internal readonly Color[] colors, coverage;
            internal Frame(int size) { colors = new Color[size*size]; coverage = new Color[size*size]; }
        }
        readonly List<View> views = new List<View>();
        readonly Matrix4x4 transform;
        readonly Vector3 origin;
        readonly int resolution;

        // pixelsPerUnit=0 fits the mesh at a fixed diagnostic footprint. A positive
        // value checks removal at the LODGroup transition's estimated screen size.
        internal LodScreenValidation(Mesh source,bool colors,Func<bool> cancelled = null,
            Matrix4x4? transform = null,float pixelsPerUnit = 0,int resolution = Size)
        {
            if (resolution < 32 || resolution > 1024) throw new ArgumentException("LOD screen resolution must be between 32 and 1024.");
            this.resolution = resolution;
            this.transform = transform ?? Matrix4x4.identity;
            origin = source.bounds.center;
            var positions = Positions(source); var triangles = Triangles(source); var rgba = Colors(source);
            bool varying = colors && HasVariation(rgba);
            foreach (var direction in Directions)
            {
                Check(cancelled);
                var normal = direction.normalized;
                var horizontal = Vector3.Cross(normal,Mathf.Abs(normal.y) > .9f ? Vector3.forward : Vector3.up).normalized;
                var vertical = Vector3.Cross(horizontal,normal).normalized;
                var minimum = Vector2.one*float.PositiveInfinity; var maximum = Vector2.one*float.NegativeInfinity;
                foreach (var p in positions)
                {
                    var projected = new Vector2(Vector3.Dot(p,horizontal),Vector3.Dot(p,vertical));
                    minimum = Vector2.Min(minimum,projected); maximum = Vector2.Max(maximum,projected);
                }
                float extent = Mathf.Max((maximum-minimum).x,(maximum-minimum).y);
                float fit = (resolution-8)/Mathf.Max(extent,1e-7f);
                var view = new View { horizontal = horizontal,vertical = vertical,normal = normal,
                    center = (minimum+maximum)*.5f,pixelsPerUnit = pixelsPerUnit > 0 ? Mathf.Min(fit,pixelsPerUnit) : fit };
                var frame = Rasterize(positions,triangles,rgba,view,resolution,cancelled);
                var settings = LodScreenAcceptance.Settings.Default; settings.checkColorBoundaries = varying;
                view.acceptance = new LodScreenAcceptance(frame.colors,frame.coverage,resolution,resolution,settings,cancelled);
                views.Add(view);
            }
        }

        internal Report Measure(Mesh mesh,Func<bool> cancelled = null)
        {
            var positions = Positions(mesh); var triangles = Triangles(mesh); var colors = Colors(mesh);
            var result = new Report { views = views.Count,detailAccepted = true };
            foreach (var view in views)
            {
                var frame = Rasterize(positions,triangles,colors,view,resolution,cancelled);
                var report = view.acceptance.Measure(frame.colors,frame.coverage,cancelled);
                if (report.detailEvaluated) result.detailViews++;
                if (report.colorEvaluated) result.colorViews++;
                result.detailLoss = Mathf.Max(result.detailLoss,Mathf.Max(report.worstThinRegionLoss,report.worstComponentLoss));
                result.colorLoss = Mathf.Max(result.colorLoss,report.worstColorRegionLoss);
                if (report.detailEvaluated) result.detailAccepted &= report.detailAccepted;
            }
            result.colorAccepted = result.colorViews > 0 && result.colorLoss <= .25f;
            result.detailAccepted &= result.detailViews > 0;
            return result;
        }
        Vector3[] Positions(Mesh mesh)
        {
            var values = mesh.vertices;
            for (int i = 0; i < values.Length; i++) values[i] = transform.MultiplyVector(values[i]-origin);
            return values;
        }
        static Color[] Colors(Mesh mesh) => mesh.colors;
        static bool HasVariation(Color[] values)
        {
            for (int i = 1; i < values.Length; i++)
            {
                Vector4 difference = values[i]-values[0];
                if (difference.sqrMagnitude > 1e-8f) return true;
            }
            return false;
        }
        static int[] Triangles(Mesh mesh)
        {
            var indices = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) indices.AddRange(LodMeshData.Triangles(mesh,s));
            return indices.ToArray();
        }
        static Frame Rasterize(Vector3[] positions,int[] indices,Color[] colors,View view,int size,Func<bool> cancelled)
        {
            var projected = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++) projected[i] = new Vector3(
                (Vector3.Dot(positions[i],view.horizontal)-view.center.x)*view.pixelsPerUnit+size*.5f,
                (Vector3.Dot(positions[i],view.vertical)-view.center.y)*view.pixelsPerUnit+size*.5f,
                Vector3.Dot(positions[i],view.normal));
            // Four subpixel samples distinguish fully covered paint interiors from borders.
            var depth = new float[size*size*4]; var samples = new Color[depth.Length];
            for (int i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;
            bool painted = colors.Length == positions.Length;
            for (int t = 0; t < indices.Length; t += 3)
            {
                if (t%384 == 0) Check(cancelled);
                int ia = indices[t], ib = indices[t+1], ic = indices[t+2];
                var a = projected[ia]; var b = projected[ib]; var c = projected[ic];
                float area = Cross(b-a,c-a);
                if (Mathf.Abs(area) < 1e-8f) continue;
                int left = Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))));
                int right = Mathf.Min(size-1,Mathf.FloorToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x))));
                int bottom = Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))));
                int top = Mathf.Min(size-1,Mathf.FloorToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))));
                for (int y = bottom; y <= top; y++)
                {
                    Check(cancelled);
                    for (int x = left; x <= right; x++) for (int sample = 0; sample < 4; sample++)
                    {
                        var p = new Vector3(x+((sample&1) == 0 ? .25f : .75f),y+((sample&2) == 0 ? .25f : .75f),0);
                        float u = Cross(b-p,c-p)/area, v = Cross(c-p,a-p)/area, w = 1-u-v;
                        if (u < -1e-6f || v < -1e-6f || w < -1e-6f) continue;
                        float z = u*a.z+v*b.z+w*c.z; int index = (y*size+x)*4+sample;
                        if (z <= depth[index]) continue;
                        depth[index] = z; samples[index] = painted ? colors[ia]*u+colors[ib]*v+colors[ic]*w : Color.white;
                    }
                }
            }
            var result = new Frame(size);
            for (int i = 0; i < result.colors.Length; i++)
            {
                int count = 0; Color sum = Color.clear;
                for (int s = 0; s < 4; s++) if (!float.IsNegativeInfinity(depth[i*4+s])) { count++; sum += samples[i*4+s]; }
                result.coverage[i] = new Color(count*.25f,0,0,0);
                if (count > 0) result.colors[i] = sum/count;
            }
            return result;
        }
        static float Cross(Vector3 a,Vector3 b) => a.x*b.y-a.y*b.x;
        static void Check(Func<bool> cancelled)
        {
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD screen validation cancelled.");
        }
    }
}
