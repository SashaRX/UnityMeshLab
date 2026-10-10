using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    // Bidirectional face-interior samples. This is a measured approximation,
    // not a certified Hausdorff bound. Reference triangles always belong to LOD0.
    internal static class LodSurfaceValidation
    {
        internal struct Limits
        {
            internal float distance, normalAngle, color;
            internal static Limits Unbounded => new Limits { distance = float.PositiveInfinity,
                normalAngle = float.PositiveInfinity, color = float.PositiveInfinity };
        }
        internal struct Metrics
        {
            internal float distance, normalAngle, authoredNormalAngle, colorError, weightedError;
            internal Vector4 colorMax;
            internal Vector4 colorSquaredIntegral;
            internal double colorArea;
            internal int colorSamples;
            internal double distanceSquaredIntegral, normalSquaredIntegral, uvSquaredIntegral, surfaceArea;
            internal float DistanceRms => surfaceArea > 0 ? Mathf.Sqrt((float)(distanceSquaredIntegral/surfaceArea)) : 0;
            internal float NormalRms => surfaceArea > 0 ? Mathf.Sqrt((float)(normalSquaredIntegral/surfaceArea)) : 0;
            internal float UvRms => surfaceArea > 0 ? Mathf.Sqrt((float)(uvSquaredIntegral/surfaceArea)) : 0;
            internal bool Rejected(Limits limits) => float.IsInfinity(weightedError) || float.IsNaN(weightedError) ||
                distance > limits.distance || normalAngle > limits.normalAngle || colorError > limits.color || float.IsNaN(colorError);
            internal Vector4 ColorRms => colorArea > 0 ? new Vector4(
                Mathf.Sqrt(colorSquaredIntegral.x / (float)colorArea), Mathf.Sqrt(colorSquaredIntegral.y / (float)colorArea),
                Mathf.Sqrt(colorSquaredIntegral.z / (float)colorArea), Mathf.Sqrt(colorSquaredIntegral.w / (float)colorArea)) : Vector4.zero;
            internal void Include(Metrics other)
            {
                distance = Mathf.Max(distance, other.distance);
                normalAngle = Mathf.Max(normalAngle, other.normalAngle);
                authoredNormalAngle = Mathf.Max(authoredNormalAngle, other.authoredNormalAngle);
                colorError = Mathf.Max(colorError, other.colorError);
                weightedError = Mathf.Max(weightedError, other.weightedError);
                colorMax = Vector4.Max(colorMax, other.colorMax);
                colorSquaredIntegral += other.colorSquaredIntegral;
                colorArea += other.colorArea;
                colorSamples += other.colorSamples;
                distanceSquaredIntegral += other.distanceSquaredIntegral;
                normalSquaredIntegral += other.normalSquaredIntegral;
                uvSquaredIntegral += other.uvSquaredIntegral;
                surfaceArea += other.surfaceArea;
            }
        }
        static readonly Vector3[] Samples =
        {
            new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1),
            new Vector3(.5f, .5f, 0), new Vector3(.5f, 0, .5f), new Vector3(0, .5f, .5f),
            new Vector3(1f/3, 1f/3, 1f/3), new Vector3(2f/3, 1f/6, 1f/6),
            new Vector3(1f/6, 2f/3, 1f/6), new Vector3(1f/6, 1f/6, 2f/3)
        };

        internal static Metrics Measure(LodMeshData data, int[] target, int[] reference, MeshSimplifier.SimplifySettings settings, Func<bool> cancelled = null)
            => MeasurePair(data, target, data, reference, settings, cancelled, false);

        internal static Metrics MeasureFaces(LodMeshData data, List<LodSourceTopology.Face> faces, MeshSimplifier.SimplifySettings settings, Func<bool> cancelled = null)
        {
            var metrics = new Metrics();
            foreach (var face in faces) metrics.Include(Measure(data, face.triangles, face.referenceTriangles, settings, cancelled));
            return metrics;
        }

        internal static Metrics MeasureMeshes(Mesh source, Mesh target, MeshSimplifier.SimplifySettings settings, Func<bool> cancelled = null, bool colorsOnly = false, Limits? limits = null,bool ignoreDegenerateFaces = false)
        {
            var metrics = new Metrics();
            if (source.subMeshCount != target.subMeshCount) { metrics.weightedError = float.PositiveInfinity; return metrics; }
            var original = new LodMeshData(source); var reduced = new LodMeshData(target);
            for (int s = 0; s < source.subMeshCount; s++)
            {
                var targetFaces = LodMeshData.Triangles(target,s); var sourceFaces = LodMeshData.Triangles(source,s);
                if (ignoreDegenerateFaces)
                {
                    targetFaces = SurfaceTriangles(reduced,targetFaces); sourceFaces = SurfaceTriangles(original,sourceFaces);
                }
                metrics.Include(MeasurePair(reduced,targetFaces,original,sourceFaces,settings,cancelled,colorsOnly,limits));
                if (limits.HasValue && metrics.Rejected(limits.Value)) break;
            }
            return metrics;
        }

        static Metrics MeasurePair(LodMeshData targetData, int[] target, LodMeshData referenceData, int[] reference, MeshSimplifier.SimplifySettings settings, Func<bool> cancelled, bool colorsOnly, Limits? limits = null)
        {
            // A dropped channel is a failure, including when its simplification cost is zero.
            if ((targetData.colors.Length > 0) != (referenceData.colors.Length > 0))
                return new Metrics { colorError = float.PositiveInfinity, colorMax = Vector4.one * float.PositiveInfinity, weightedError = float.PositiveInfinity };
            if (target.Length == 0 && reference.Length == 0) return default;
            if (target.Length == 0 || reference.Length == 0) return new Metrics { weightedError = float.PositiveInfinity };
            var metrics = new Metrics();
            SampleDirection(targetData, target, referenceData, reference, referenceData.scale, settings, cancelled, colorsOnly, limits, ref metrics);
            if (limits.HasValue && metrics.Rejected(limits.Value)) return metrics;
            SampleDirection(referenceData, reference, targetData, target, referenceData.scale, settings, cancelled, colorsOnly, limits, ref metrics);
            return metrics;
        }

        static void SampleDirection(LodMeshData from, int[] fromTriangles, LodMeshData to, int[] toTriangles, float scale,
            MeshSimplifier.SimplifySettings settings, Func<bool> cancelled, bool colorsOnly, Limits? limits, ref Metrics metrics)
        {
            var normals = new Vector3[toTriangles.Length / 3];
            for (int f = 0; f < normals.Length; f++) normals[f] = GeometricNormal(to, toTriangles, f * 3);
            // An unchanged topology/position mapping is exact correspondence,
            // including overlapping faces and arbitrarily thin seam triangles.
            // Attribute changes are still measured on their matching faces.
            bool sameConnectivity = (ReferenceEquals(fromTriangles,toTriangles) || fromTriangles.SequenceEqual(toTriangles)) &&
                (ReferenceEquals(from.positions,to.positions) || from.positions.SequenceEqual(to.positions));
            var bvh = sameConnectivity ? null : new TriangleBvh(to.positions, toTriangles);
            for (int i = 0; i < fromTriangles.Length; i += 3)
            {
                if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD surface validation cancelled.");
                var faceNormal = GeometricNormal(from, fromTriangles, i);
                if (faceNormal.sqrMagnitude < .5f)
                {
                    // Zero-area output triangles have no color-covered surface. The
                    // strict loop shape validator still rejects them; color-only QA
                    // evaluates the remaining surface and reverse source coverage.
                    if (colorsOnly) continue;
                    metrics.weightedError = float.PositiveInfinity; return;
                }
                foreach (var bary in Samples)
                {
                    var nearest = CorrespondingHit(from,fromTriangles,i,bary,to,toTriangles,bvh,normals,faceNormal,sameConnectivity);
                    if (nearest.triangleIndex < 0) { metrics.weightedError = float.PositiveInfinity; return; }
                    int j = nearest.triangleIndex * 3;
                    float distance = Mathf.Sqrt(nearest.distSq) / scale;
                    metrics.distance = Mathf.Max(metrics.distance, distance);
                    if (!colorsOnly)
                    {
                        metrics.weightedError = Mathf.Max(metrics.weightedError, distance);
                        metrics.normalAngle = Mathf.Max(metrics.normalAngle, Vector3.Angle(faceNormal, normals[nearest.triangleIndex]));
                    }
                    if (!colorsOnly && from.normals.Length == from.positions.Length && to.normals.Length == to.positions.Length)
                    {
                        var a = Interpolate(from.normals, fromTriangles, i, bary).normalized;
                        var b = Interpolate(to.normals, toTriangles, j, nearest.barycentric).normalized;
                        metrics.authoredNormalAngle = Mathf.Max(metrics.authoredNormalAngle, Vector3.Angle(a, b));
                        metrics.normalAngle = Mathf.Max(metrics.normalAngle, metrics.authoredNormalAngle);
                        metrics.weightedError = Mathf.Max(metrics.weightedError, (a - b).magnitude * settings.normalWeight);
                    }
                    if (from.colors.Length == from.positions.Length && to.colors.Length == to.positions.Length)
                    {
                        Color a = ColorAt(from.colors, fromTriangles, i, bary), b = ColorAt(to.colors, toTriangles, j, nearest.barycentric);
                        Vector4 delta = (Vector4)a - (Vector4)b;
                        IncludeColor(delta, ref metrics);
                        metrics.weightedError = Mathf.Max(metrics.weightedError, delta.magnitude * settings.colorWeight);
                    }
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if (colorsOnly) break;
                        if (from.uvs[ch] == null || to.uvs[ch] == null) continue;
                        var a = from.uvs[ch][fromTriangles[i]] * bary.x + from.uvs[ch][fromTriangles[i + 1]] * bary.y + from.uvs[ch][fromTriangles[i + 2]] * bary.z;
                        var b = to.uvs[ch][toTriangles[j]] * nearest.barycentric.x + to.uvs[ch][toTriangles[j + 1]] * nearest.barycentric.y + to.uvs[ch][toTriangles[j + 2]] * nearest.barycentric.z;
                        float weight = ch == settings.uvChannel ? settings.uv2Weight : 1f;
                        metrics.weightedError = Mathf.Max(metrics.weightedError, (a - b).magnitude * weight);
                    }
                    if (limits.HasValue && metrics.Rejected(limits.Value)) return;
                }
                if (from.colors.Length == from.positions.Length && to.colors.Length == to.positions.Length)
                    SampleColorInterior(from, fromTriangles, i, to, toTriangles, bvh, normals, faceNormal, sameConnectivity, settings, limits, ref metrics);
                if (!colorsOnly)
                    SampleSurfaceInterior(from,fromTriangles,i,to,toTriangles,bvh,normals,faceNormal,sameConnectivity,scale,settings.uvChannel,ref metrics);
                if (limits.HasValue && metrics.Rejected(limits.Value)) return;
            }
        }

        static void IncludeColor(Vector4 delta, ref Metrics metrics)
        {
            var error = new Vector4(Mathf.Abs(delta.x), Mathf.Abs(delta.y), Mathf.Abs(delta.z), Mathf.Abs(delta.w));
            metrics.colorMax = Vector4.Max(metrics.colorMax, error);
            metrics.colorError = Mathf.Max(metrics.colorError, Mathf.Max(Mathf.Max(error.x, error.y), Mathf.Max(error.z, error.w)));
            metrics.colorSamples++;
        }

        static void SampleSurfaceInterior(LodMeshData from,int[] triangles,int i,LodMeshData to,int[] target,
            TriangleBvh bvh,Vector3[] normals,Vector3 faceNormal,bool sameConnectivity,float scale,int uvChannel,ref Metrics metrics)
        {
            // Centroids of four equal-area subtriangles. Edges/corners still
            // contribute to maxima above; area RMS does not overweight seams.
            double weight = Vector3.Cross(from.positions[triangles[i+1]]-from.positions[triangles[i]],
                from.positions[triangles[i+2]]-from.positions[triangles[i]]).magnitude/8;
            bool vertexNormals = from.normals.Length == from.positions.Length && to.normals.Length == to.positions.Length;
            for (int sample = 6; sample < Samples.Length; sample++)
            {
                var bary = Samples[sample];
                var hit = CorrespondingHit(from,triangles,i,bary,to,target,bvh,normals,faceNormal,sameConnectivity);
                if (hit.triangleIndex < 0) { metrics.weightedError = float.PositiveInfinity; return; }
                float angle = vertexNormals
                    ? Vector3.Angle(Interpolate(from.normals,triangles,i,bary),Interpolate(to.normals,target,hit.triangleIndex*3,hit.barycentric))
                    : Vector3.Angle(faceNormal,normals[hit.triangleIndex]);
                metrics.distanceSquaredIntegral += hit.distSq/(scale*scale)*weight;
                metrics.normalSquaredIntegral += angle*angle*weight;
                if (from.uvs[uvChannel] != null && to.uvs[uvChannel] != null)
                {
                    var a = from.uvs[uvChannel][triangles[i]]*bary.x + from.uvs[uvChannel][triangles[i+1]]*bary.y + from.uvs[uvChannel][triangles[i+2]]*bary.z;
                    int j = hit.triangleIndex*3; var projected = hit.barycentric;
                    var b = to.uvs[uvChannel][target[j]]*projected.x + to.uvs[uvChannel][target[j+1]]*projected.y + to.uvs[uvChannel][target[j+2]]*projected.z;
                    metrics.uvSquaredIntegral += (a-b).sqrMagnitude*weight;
                }
                metrics.surfaceArea += weight;
            }
        }

        internal static TriangleBvh.HitResult CorrespondingHit(LodMeshData from,int[] triangles,int i,Vector3 bary,
            LodMeshData to,int[] toTriangles,TriangleBvh bvh,Vector3[] normals,Vector3 faceNormal,bool sameConnectivity)
        {
            Vector3 position = Interpolate(from.positions,triangles,i,bary);
            if (sameConnectivity) return new TriangleBvh.HitResult { triangleIndex = i/3,point = position,barycentric = bary,distSq = 0 };
            var nearest = bvh.FindNearestNormalFiltered(position,faceNormal,normals,.001f);
            if (nearest.triangleIndex < 0 || Mathf.Min(bary.x,Mathf.Min(bary.y,bary.z)) > 1e-6f) return nearest;
            // On an edge/corner, equal-distance triangles may lie on opposite sides
            // of an authored color/UV/normal seam. Select the one-sided limit from
            // this face's interior, without using color agreement as a tie-breaker.
            var inward = bary*.999f + Vector3.one*(.001f/3);
            var probe = bvh.FindNearestNormalFiltered(Interpolate(from.positions,triangles,i,inward),faceNormal,normals,.001f);
            if (probe.triangleIndex < 0 || probe.triangleIndex == nearest.triangleIndex) return nearest;
            int j = probe.triangleIndex*3;
            Vector3 closest = TriangleBvh.ClosestPointOnTriangle(position,to.positions[toTriangles[j]],
                to.positions[toTriangles[j+1]],to.positions[toTriangles[j+2]],out Vector3 projected);
            float distance = (closest-position).sqrMagnitude;
            float tolerance = from.scale*1e-5f;
            // The probe only resolves spatial ties; it cannot select a farther
            // surface merely because its attributes are more convenient.
            if (distance > nearest.distSq + tolerance*tolerance) return nearest;
            probe.barycentric = projected; probe.distSq = distance; probe.point = closest;
            return probe;
        }

        static void SampleColorInterior(LodMeshData from, int[] triangles, int i, LodMeshData to, int[] toTriangles,
            TriangleBvh bvh, Vector3[] normals, Vector3 faceNormal, bool sameConnectivity, MeshSimplifier.SimplifySettings settings, Limits? limits, ref Metrics metrics)
        {
            var ca = (Vector4)from.colors[triangles[i]];
            var cb = (Vector4)from.colors[triangles[i + 1]];
            var cc = (Vector4)from.colors[triangles[i + 2]];
            // Equal-area triangle centroids provide an area-weighted estimate. Increase
            // density for steep fields on either surface; colors may contain data, not sRGB.
            int divisions = Mathf.Max((ca - cb).magnitude, Mathf.Max((ca - cc).magnitude, (cb - cc).magnitude)) > .1f ? 8 : 4;
            foreach (var bary in Samples)
            {
                var nearest = CorrespondingHit(from,triangles,i,bary,to,toTriangles,bvh,normals,faceNormal,sameConnectivity);
                if (nearest.triangleIndex < 0) continue;
                int j = nearest.triangleIndex * 3;
                var ta = (Vector4)to.colors[toTriangles[j]]; var tb = (Vector4)to.colors[toTriangles[j + 1]]; var tc = (Vector4)to.colors[toTriangles[j + 2]];
                if (Mathf.Max((ta - tb).magnitude, Mathf.Max((ta - tc).magnitude, (tb - tc).magnitude)) > .1f) { divisions = 8; break; }
            }
            float area = Vector3.Cross(from.positions[triangles[i + 1]] - from.positions[triangles[i]],
                from.positions[triangles[i + 2]] - from.positions[triangles[i]]).magnitude * .5f;
            float weight = area / (divisions * divisions);
            var accumulated = new Metrics();
            for (int x = 0; x < divisions; x++) for (int y = 0; y < divisions - x; y++)
            {
                Sample(new Vector3((x + 1f / 3) / divisions, (y + 1f / 3) / divisions, 1 - (x + y + 2f / 3) / divisions));
                if (x + y < divisions - 1)
                    Sample(new Vector3((x + 2f / 3) / divisions, (y + 2f / 3) / divisions, 1 - (x + y + 4f / 3) / divisions));
                if (limits.HasValue && accumulated.Rejected(limits.Value)) { metrics.Include(accumulated); return; }
            }
            // Use a local accumulator because C# cannot capture a ref argument.
            metrics.Include(accumulated);

            void Sample(Vector3 bary)
            {
                var nearest = CorrespondingHit(from,triangles,i,bary,to,toTriangles,bvh,normals,faceNormal,sameConnectivity);
                if (nearest.triangleIndex < 0) { accumulated.weightedError = float.PositiveInfinity; return; }
                var delta = (Vector4)ColorAt(from.colors, triangles, i, bary) - (Vector4)ColorAt(to.colors, toTriangles, nearest.triangleIndex * 3, nearest.barycentric);
                IncludeColor(delta, ref accumulated);
                accumulated.weightedError = Mathf.Max(accumulated.weightedError, delta.magnitude * settings.colorWeight);
                accumulated.colorSquaredIntegral += new Vector4(delta.x * delta.x, delta.y * delta.y, delta.z * delta.z, delta.w * delta.w) * weight;
                accumulated.colorArea += weight;
            }
        }
        internal static int[] SurfaceTriangles(LodMeshData data,int[] triangles)
        {
            var surface = new List<int>(triangles.Length);
            for (int i = 0; i < triangles.Length; i += 3)
                if (GeometricNormal(data,triangles,i).sqrMagnitude > .5f)
                { surface.Add(triangles[i]); surface.Add(triangles[i+1]); surface.Add(triangles[i+2]); }
            return surface.ToArray();
        }
        internal static Vector3 GeometricNormal(LodMeshData data, int[] t, int i)
        {
            // Unity's Vector3.normalized uses an absolute epsilon; that can erase
            // valid tiny triangles. Normalize edges by the source scale first.
            var normal = Vector3.Cross((data.positions[t[i+1]]-data.positions[t[i]])/data.scale,
                (data.positions[t[i+2]]-data.positions[t[i]])/data.scale);
            float length = normal.magnitude;
            return length > 1e-12f ? normal/length : Vector3.zero;
        }
        static Vector3 Interpolate(Vector3[] values, int[] t, int i, Vector3 b) => values[t[i]] * b.x + values[t[i + 1]] * b.y + values[t[i + 2]] * b.z;
        static Color ColorAt(Color[] values, int[] t, int i, Vector3 b) => values[t[i]] * b.x + values[t[i + 1]] * b.y + values[t[i + 2]] * b.z;
    }
}
