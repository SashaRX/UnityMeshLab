using System;
using System.Collections.Generic;
using System.Linq;
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
            public int objectPixels, resolution;
            public float detailLoss, colorLoss;
            public bool detailAccepted, colorAccepted;
            public bool normalsEvaluated, rgbaEvaluated;
            public float silhouetteMean, silhouetteMax, normalRms, rgbaRms;
            public Vector4 rgbaChannelRms;
            public float headlightRms;
            public float[] normalViewRms, headlightViewRms;
            internal float Penalty(float colorWeight) => 4*(detailLoss+colorLoss*Mathf.Max(0,colorWeight));
            internal float ScreenScore(float normalWeight,float colorWeight) => silhouetteMean/.05f+silhouetteMax/.2f+
                normalRms/30*Mathf.Max(0,normalWeight)+rgbaRms/.1f*Mathf.Max(0,colorWeight)+Penalty(colorWeight);
            internal bool DoesNotWorsen(Report before,bool fields = false) => detailLoss <= before.detailLoss+1e-6f && colorLoss <= before.colorLoss+1e-6f &&
                (!fields || silhouetteMean <= before.silhouetteMean+1e-6f && silhouetteMax <= before.silhouetteMax+1e-6f &&
                    normalRms <= before.normalRms+1e-6f && ColorsDoNotWorsen(before));
            internal bool ColorsDoNotWorsen(Report before) => colorLoss <= before.colorLoss+1e-6f && rgbaRms <= before.rgbaRms+1e-6f && ChannelsDoNotWorsen(before);
            bool ChannelsDoNotWorsen(Report before)
            {
                for (int ch = 0; ch < 4; ch++) if (rgbaChannelRms[ch] > before.rgbaChannelRms[ch]+1e-6f) return false;
                return true;
            }
        }
        sealed class View
        {
            internal Vector3 horizontal, vertical, normal;
            internal Vector2 center;
            internal float pixelsPerUnit;
            internal LodScreenAcceptance acceptance;
            internal Frame source;
        }
        sealed class Frame
        {
            internal readonly Color[] colors, coverage;
            internal readonly Vector3[] normals;
            internal Vector3[] normalSamples;
            internal float[] headlight;
            internal Color[] samples;
            internal int[] triangleAt;
            internal Vector3[] baryAt;
            internal Frame(int size,bool fields) { colors = new Color[size*size]; coverage = new Color[size*size]; normals = fields ? new Vector3[size*size] : null; }
        }
        readonly List<View> views = new List<View>();
        readonly Matrix4x4 transform;
        readonly Vector3 origin;
        readonly int resolution;
        readonly int objectPixels;
        readonly bool measureFields, checkColors, hasNormals;
        readonly float sourceExtent;
        readonly int[] sourceSlots;
        readonly Vector3[] sourceFaceNormals;

        // pixelsPerUnit=0 fits the mesh at a fixed diagnostic footprint. A positive
        // value checks removal at the LODGroup transition's estimated screen size.
        internal LodScreenValidation(Mesh source,bool colors,Func<bool> cancelled = null,
            Matrix4x4? transform = null,float pixelsPerUnit = 0,int resolution = Size,int objectPixels = 0,bool measureFields = false,bool allSides = false)
        {
            if (resolution < 32 || resolution > 1024) throw new ArgumentException("LOD screen resolution must be between 32 and 1024.");
            if (objectPixels < 0 || objectPixels > resolution-8) throw new ArgumentException("Object footprint must fit inside the screen preview padding.");
            this.resolution = resolution;
            this.objectPixels = objectPixels > 0 ? objectPixels : resolution-8;
            this.transform = transform ?? Matrix4x4.identity;
            this.measureFields = measureFields;
            checkColors = colors && source.colors.Length == source.vertexCount;
            hasNormals = source.normals.Length == source.vertexCount;
            origin = source.bounds.center;
            var size = source.bounds.size; sourceExtent = Mathf.Max(size.x,Mathf.Max(size.y,size.z));
            var positions = Positions(source); var triangles = Triangles(source); var rgba = Colors(source);
            sourceSlots = FaceSlots(source); sourceFaceNormals = FaceNormals(positions,triangles);
            var sourceNormals = Normals(source);
            bool varying = colors && HasVariation(rgba);
            foreach (var direction in allSides ? Directions.Concat(Directions.Select(d => -d)) : Directions)
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
                float fit = this.objectPixels/Mathf.Max(extent,1e-7f);
                var view = new View { horizontal = horizontal,vertical = vertical,normal = normal,
                    center = (minimum+maximum)*.5f,pixelsPerUnit = pixelsPerUnit > 0 ? Mathf.Min(fit,pixelsPerUnit) : fit };
                var frame = Rasterize(positions,triangles,rgba,sourceNormals,view,resolution,cancelled,measureFields);
                if (measureFields) view.source = frame;
                var settings = LodScreenAcceptance.Settings.Default; settings.checkColorBoundaries = varying;
                view.acceptance = new LodScreenAcceptance(frame.colors,frame.coverage,resolution,resolution,settings,cancelled);
                views.Add(view);
            }
        }

        internal Report Measure(Mesh mesh,Func<bool> cancelled = null)
        {
            var positions = Positions(mesh); var triangles = Triangles(mesh); var colors = Colors(mesh);
            var normals = Normals(mesh);
            var result = new Report { views = views.Count,detailAccepted = true,objectPixels = objectPixels,resolution = resolution,
                normalsEvaluated = measureFields && hasNormals,rgbaEvaluated = measureFields && checkColors,
                normalViewRms = measureFields ? new float[views.Count] : null,headlightViewRms = measureFields ? new float[views.Count] : null };
            int viewIndex = 0;
            foreach (var view in views)
            {
                var frame = Rasterize(positions,triangles,colors,normals,view,resolution,cancelled,measureFields);
                if (measureFields) MeasureFields(view.source,frame,result,viewIndex,cancelled);
                var report = view.acceptance.Measure(frame.colors,frame.coverage,cancelled);
                if (report.detailEvaluated) result.detailViews++;
                if (report.colorEvaluated) result.colorViews++;
                result.detailLoss = Mathf.Max(result.detailLoss,Mathf.Max(report.worstThinRegionLoss,report.worstComponentLoss));
                result.colorLoss = Mathf.Max(result.colorLoss,report.worstColorRegionLoss);
                if (report.detailEvaluated) result.detailAccepted &= report.detailAccepted;
                viewIndex++;
            }
            result.colorAccepted = result.colorViews > 0 && result.colorLoss <= .25f;
            result.detailAccepted &= result.detailViews > 0;
            return result;
        }

        // Coordinate descent of an already source-derived candidate. Index and
        // attribute buffers stay intact; coincident render wedges move together.
        // Coverage counts permit local face updates without re-rendering the mesh.
        internal Mesh RefineSilhouette(Mesh mesh,out int moves,out int trials,Func<bool> cancelled = null)
        {
            moves = trials = 0;
            if (!measureFields || objectPixels > 128 || mesh.blendShapeCount > 0 ||
                mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight)) return null;
            var indices = Triangles(mesh); int faces = indices.Length/3;
            if (faces == 0 || faces > 512) return null;
            var positions = mesh.vertices; var groups = new Dictionary<Vector3,List<int>>();
            var attached = new List<int>[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                attached[i] = new List<int>();
                if (!groups.TryGetValue(positions[i],out var group)) groups[positions[i]] = group = new List<int>();
                group.Add(i);
            }
            var slotSets = new HashSet<int>[positions.Length];
            for (int i = 0; i < slotSets.Length; i++) slotSets[i] = new HashSet<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) foreach (int v in LodMeshData.Triangles(mesh,s)) slotSets[v].Add(s);
            var originalNormals = new Vector3[faces];
            for (int f = 0; f < faces; f++)
            {
                for (int k = 0; k < 3; k++) attached[indices[f*3+k]].Add(f);
                originalNormals[f] = FaceNormal(positions,indices,f);
            }
            var buffers = new CoverageBuffer[views.Count];
            for (int v = 0; v < views.Count; v++)
            {
                Check(cancelled); var buffer = new CoverageBuffer(faces,resolution*resolution*4); buffers[v] = buffer;
                for (int f = 0; f < faces; f++)
                {
                    buffer.faces[f] = FaceSamples(positions,indices,f,views[v],cancelled);
                    foreach (int sample in buffer.faces[f]) buffer.counts[sample]++;
                }
            }
            var before = CoverageErrors(buffers,cancelled); float score = before.x*20+before.y*5;
            var ordered = new List<KeyValuePair<Vector3,List<int>>>(groups);
            ordered.Sort((a,b) => { int x = a.Key.x.CompareTo(b.Key.x); int y = a.Key.y.CompareTo(b.Key.y); return x != 0 ? x : y != 0 ? y : a.Key.z.CompareTo(b.Key.z); });
            foreach (float step in new[] { 1f,.5f,.25f })
            {
                int groupIndex = 0;
                foreach (var pair in ordered)
                {
                    Check(cancelled);
                    if (groupIndex++%16 == 0) UvProgress.Report(UvProgress.Current.fraction,$"{mesh.name}: far silhouette refit {step:G2}px");
                    var group = pair.Value; var slots = new HashSet<int>();
                    foreach (int vertex in group) slots.UnionWith(slotSets[vertex]);
                    if (slots.Count > 1) continue; // Pin material interfaces.
                    var faceSet = new SortedSet<int>(); foreach (int vertex in group) faceSet.UnionWith(attached[vertex]);
                    if (faceSet.Count == 0) continue;
                    var incident = new List<int>(faceSet); var previous = positions[group[0]]; var best = previous;
                    int[][][] accepted = null;
                    for (int axis = 0; axis < 3; axis++) for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var proposed = previous; proposed[axis] += sign*step*sourceExtent/objectPixels;
                        if ((proposed-pair.Key).magnitude > 2*sourceExtent/objectPixels) continue;
                        foreach (int vertex in group) positions[vertex] = proposed;
                        bool valid = true;
                        foreach (int f in incident)
                        {
                            var normal = FaceNormal(positions,indices,f);
                            if (originalNormals[f].sqrMagnitude > 1e-20f &&
                                (normal.sqrMagnitude < originalNormals[f].sqrMagnitude*1e-4f || Vector3.Dot(normal,originalNormals[f]) <= 0)) { valid = false; break; }
                        }
                        if (!valid) continue;
                        var masks = new int[views.Count][][];
                        for (int v = 0; v < views.Count; v++)
                        {
                            masks[v] = new int[incident.Count][];
                            for (int i = 0; i < incident.Count; i++)
                            {
                                masks[v][i] = FaceSamples(positions,indices,incident[i],views[v],cancelled);
                                ReplaceCounts(buffers[v],incident[i],masks[v][i],1);
                            }
                        }
                        var error = CoverageErrors(buffers,cancelled); trials++;
                        for (int v = 0; v < views.Count; v++) for (int i = 0; i < incident.Count; i++) ReplaceCounts(buffers[v],incident[i],masks[v][i],-1);
                        float value = error.x*20+error.y*5;
                        if (value >= score-1e-6f || error.x > before.x+1e-6f || error.y > before.y+1e-6f) continue;
                        score = value; best = proposed; accepted = masks;
                    }
                    foreach (int vertex in group) positions[vertex] = best;
                    if (accepted == null) continue;
                    moves++;
                    for (int v = 0; v < views.Count; v++) for (int i = 0; i < incident.Count; i++)
                    {
                        ReplaceCounts(buffers[v],incident[i],accepted[v][i],1);
                        buffers[v].faces[incident[i]] = accepted[v][i];
                    }
                }
            }
            if (moves == 0) return null;
            Check(cancelled);
            var result = UnityEngine.Object.Instantiate(mesh);
            try { result.vertices = positions; result.RecalculateBounds(); return result; }
            catch { UnityEngine.Object.DestroyImmediate(result); throw; }
        }

        sealed class CoverageBuffer
        {
            internal readonly int[][] faces;
            internal readonly int[] counts;
            internal CoverageBuffer(int faceCount,int samples) { faces = new int[faceCount][]; counts = new int[samples]; }
        }
        static Vector3 FaceNormal(Vector3[] positions,int[] indices,int face)
            => Vector3.Cross(positions[indices[face*3+1]]-positions[indices[face*3]],positions[indices[face*3+2]]-positions[indices[face*3]]);
        static void ReplaceCounts(CoverageBuffer buffer,int face,int[] samples,int sign)
        {
            foreach (int sample in buffer.faces[face]) buffer.counts[sample] -= sign;
            foreach (int sample in samples) buffer.counts[sample] += sign;
        }
        Vector2 CoverageErrors(CoverageBuffer[] buffers,Func<bool> cancelled)
        {
            float mean = 0, maximum = 0;
            for (int v = 0; v < buffers.Length; v++)
            {
                double union = 0, difference = 0; var counts = buffers[v].counts; var source = views[v].source.coverage;
                for (int pixel = 0; pixel < source.Length; pixel++)
                {
                    if (pixel%1024 == 0) Check(cancelled);
                    float a = source[pixel].r, b = 0;
                    for (int sample = 0; sample < 4; sample++) if (counts[pixel*4+sample] > 0) b += .25f;
                    union += Mathf.Max(a,b); difference += Mathf.Abs(a-b);
                }
                float error = union > 0 ? (float)(difference/union) : 0;
                mean += error/buffers.Length; maximum = Mathf.Max(maximum,error);
            }
            return new Vector2(mean,maximum);
        }
        int[] FaceSamples(Vector3[] positions,int[] indices,int face,View view,Func<bool> cancelled)
        {
            Vector3 Project(int vertex)
            {
                var p = transform.MultiplyVector(positions[vertex]-origin);
                return new Vector3((Vector3.Dot(p,view.horizontal)-view.center.x)*view.pixelsPerUnit+resolution*.5f,
                    (Vector3.Dot(p,view.vertical)-view.center.y)*view.pixelsPerUnit+resolution*.5f,0);
            }
            var a = Project(indices[face*3]); var b = Project(indices[face*3+1]); var c = Project(indices[face*3+2]);
            float area = Cross(b-a,c-a); if (Mathf.Abs(area) < 1e-8f) return Array.Empty<int>();
            int left = Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))));
            int right = Mathf.Min(resolution-1,Mathf.FloorToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x))));
            int bottom = Mathf.Max(0,Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))));
            int top = Mathf.Min(resolution-1,Mathf.FloorToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))));
            var samples = new List<int>();
            for (int y = bottom; y <= top; y++)
            {
                Check(cancelled);
                for (int x = left; x <= right; x++) for (int sample = 0; sample < 4; sample++)
                {
                    var p = new Vector3(x+((sample&1) == 0 ? .25f : .75f),y+((sample&2) == 0 ? .25f : .75f),0);
                    float u = Cross(b-p,c-p)/area, v = Cross(c-p,a-p)/area;
                    if (u >= -1e-6f && v >= -1e-6f && 1-u-v >= -1e-6f) samples.Add((y*resolution+x)*4+sample);
                }
            }
            return samples.ToArray();
        }
        static void MeasureFields(Frame source,Frame target,Report result,int view,Func<bool> cancelled)
        {
            double union = 0, difference = 0, overlap = 0, normalSquared = 0, colorSquared = 0;
            double headlightSquared = 0;
            var channelSquared = new double[4];
            for (int i = 0; i < source.colors.Length; i++)
            {
                if (i%1024 == 0) Check(cancelled);
                float a = source.coverage[i].r, b = target.coverage[i].r;
                union += Mathf.Max(a,b); difference += Mathf.Abs(a-b);
                float weight = Mathf.Min(a,b); if (weight <= 0) continue;
                overlap += weight;
                if (result.normalsEvaluated)
                {
                    var n = source.normals[i]; var m = target.normals[i];
                    float angle = n.sqrMagnitude > .5f && m.sqrMagnitude > .5f ? Vector3.Angle(n,m) : (n-m).sqrMagnitude > .5f ? 180 : 0;
                    normalSquared += weight*angle*angle;
                    float light = source.headlight[i]-target.headlight[i]; headlightSquared += weight*light*light;
                }
                if (result.rgbaEvaluated)
                {
                    Vector4 delta = source.colors[i]-target.colors[i]; colorSquared += weight*delta.sqrMagnitude/4;
                    for (int ch = 0; ch < 4; ch++) channelSquared[ch] += weight*delta[ch]*delta[ch];
                }
            }
            float error = union > 0 ? (float)(difference/union) : 0;
            result.silhouetteMean += error/result.views; result.silhouetteMax = Mathf.Max(result.silhouetteMax,error);
            if (overlap > 0)
            {
                result.normalRms = Mathf.Max(result.normalRms,(float)Math.Sqrt(normalSquared/overlap));
                result.normalViewRms[view] = (float)Math.Sqrt(normalSquared/overlap);
                result.headlightViewRms[view] = (float)Math.Sqrt(headlightSquared/overlap);
                result.headlightRms = Mathf.Max(result.headlightRms,result.headlightViewRms[view]);
                result.rgbaRms = Mathf.Max(result.rgbaRms,(float)Math.Sqrt(colorSquared/overlap));
                for (int ch = 0; ch < 4; ch++) result.rgbaChannelRms[ch] = Mathf.Max(result.rgbaChannelRms[ch],(float)Math.Sqrt(channelSquared[ch]/overlap));
            }
        }
        internal int AddVisibleColorSamples(Mesh mesh,Action<int[],int,Vector3,Vector4,double> add,Func<bool> cancelled = null)
            => AddVisibleSamples(mesh,add,false,cancelled);
        internal int AddVisibleNormalSamples(Mesh mesh,Action<int[],int,Vector3,Vector4,double> add,Func<bool> cancelled = null)
            => AddVisibleSamples(mesh,add,true,cancelled);
        internal void AddVisibleSurfaceSamples(Mesh mesh,Action<int,Vector3,int,Vector3,Vector3,Vector3,int,int> add,Func<bool> cancelled = null)
        {
            if (!measureFields || !hasNormals || mesh.normals.Length != mesh.vertexCount) return;
            var positions = Positions(mesh); var indices = Triangles(mesh); var authored = Normals(mesh);
            var slots = FaceSlots(mesh); var normals = FaceNormals(positions,indices);
            for (int v = 0; v < views.Count; v++)
            {
                var view = views[v]; var frame = Rasterize(positions,indices,Colors(mesh),authored,view,resolution,cancelled,true);
                for (int at = 0; at < frame.triangleAt.Length; at++)
                {
                    if (at%1024 == 0) Check(cancelled);
                    int original = view.source.triangleAt[at], target = frame.triangleAt[at];
                    if (original < 0 || target < 0 || sourceSlots[original] != slots[target] || Vector3.Dot(sourceFaceNormals[original],normals[target]) <= 0) continue;
                    add(original,view.source.baryAt[at],target,frame.baryAt[at],
                        transform.transpose.MultiplyVector(view.source.normalSamples[at]).normalized,
                        transform.transpose.MultiplyVector(frame.normalSamples[at]).normalized,v,at/4);
                }
            }
        }
        int AddVisibleSamples(Mesh mesh,Action<int[],int,Vector3,Vector4,double> add,bool normalField,Func<bool> cancelled)
        {
            if (!measureFields || (normalField ? !hasNormals || mesh.normals.Length != mesh.vertexCount : !checkColors || mesh.colors.Length != mesh.vertexCount)) return 0;
            var positions = Positions(mesh); var indices = Triangles(mesh); var colors = Colors(mesh);
            var slots = FaceSlots(mesh); var normals = FaceNormals(positions,indices); int count = 0;
            var authored = Normals(mesh);
            foreach (var view in views)
            {
                var frame = Rasterize(positions,indices,colors,authored,view,resolution,cancelled,true);
                for (int pixel = 0; pixel < frame.colors.Length; pixel++)
                {
                    if (pixel%1024 == 0) Check(cancelled);
                    // Train on paired covered samples. Occluded samples and a
                    // different material or opposite geometric face cannot lend color.
                    for (int s = 0; s < 4; s++)
                    {
                        int at = pixel*4+s, original = view.source.triangleAt[at], target = frame.triangleAt[at];
                        if (original < 0 || target < 0 || sourceSlots[original] != slots[target] ||
                            Vector3.Dot(sourceFaceNormals[original],normals[target]) <= 0) continue;
                        Vector4 desired = view.source.samples[at];
                        if (normalField)
                        {
                            var n = view.source.normalSamples[at]; if (n.sqrMagnitude < .5f) continue;
                            desired = transform.transpose.MultiplyVector(n).normalized;
                        }
                        add(indices,target*3,frame.baryAt[at],desired,.25); count++;
                    }
                }
            }
            return count;
        }
        static int[] FaceSlots(Mesh mesh)
        {
            var result = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                int count = LodMeshData.Triangles(mesh,s).Length/3;
                for (int i = 0; i < count; i++) result.Add(s);
            }
            return result.ToArray();
        }
        static Vector3[] FaceNormals(Vector3[] positions,int[] indices)
        {
            var result = new Vector3[indices.Length/3];
            for (int i = 0; i < result.Length; i++) result[i] = FaceNormal(positions,indices,i).normalized;
            return result;
        }
        Vector3[] Normals(Mesh mesh)
        {
            if (!measureFields) return Array.Empty<Vector3>();
            var values = mesh.normals; var matrix = transform.inverse.transpose;
            for (int i = 0; i < values.Length; i++) values[i] = matrix.MultiplyVector(values[i]).normalized;
            return values;
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
        static Frame Rasterize(Vector3[] positions,int[] indices,Color[] colors,Vector3[] normals,View view,int size,Func<bool> cancelled,bool fields)
        {
            var projected = new Vector3[positions.Length];
            for (int i = 0; i < positions.Length; i++) projected[i] = new Vector3(
                (Vector3.Dot(positions[i],view.horizontal)-view.center.x)*view.pixelsPerUnit+size*.5f,
                (Vector3.Dot(positions[i],view.vertical)-view.center.y)*view.pixelsPerUnit+size*.5f,
                Vector3.Dot(positions[i],view.normal));
            // Four subpixel samples distinguish fully covered paint interiors from borders.
            var depth = new float[size*size*4]; var samples = new Color[depth.Length];
            var normalSamples = fields ? new Vector3[depth.Length] : null;
            var triangleAt = fields ? new int[depth.Length] : null;
            var baryAt = fields ? new Vector3[depth.Length] : null;
            for (int i = 0; i < depth.Length; i++) depth[i] = float.NegativeInfinity;
            if (fields) for (int i = 0; i < triangleAt.Length; i++) triangleAt[i] = -1;
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
                        if (fields) { triangleAt[index] = t/3; baryAt[index] = new Vector3(u,v,w); }
                        if (normalSamples != null && normals.Length == positions.Length)
                            normalSamples[index] = (normals[ia]*u+normals[ib]*v+normals[ic]*w).normalized;
                    }
                }
            }
            var result = new Frame(size,fields);
            if (fields) { result.samples = samples; result.normalSamples = normalSamples; result.headlight = new float[size*size]; result.triangleAt = triangleAt; result.baryAt = baryAt; }
            for (int i = 0; i < result.colors.Length; i++)
            {
                int count = 0; Color sum = Color.clear; Vector3 normalSum = Vector3.zero; float light = 0;
                for (int s = 0; s < 4; s++) if (!float.IsNegativeInfinity(depth[i*4+s]))
                { count++; sum += samples[i*4+s]; if (normalSamples != null) { normalSum += normalSamples[i*4+s]; light += .25f+.75f*Mathf.Abs(Vector3.Dot(normalSamples[i*4+s],view.normal)); } }
                result.coverage[i] = new Color(count*.25f,0,0,0);
                if (count > 0) result.colors[i] = sum/count;
                if (fields) { result.normals[i] = normalSum.normalized; if (count > 0) result.headlight[i] = light/count; }
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
