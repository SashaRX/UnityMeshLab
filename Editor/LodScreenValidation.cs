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
            public int gradientViews,sharpColorPairs,gradientPairs;
            public float gradientWorstRegionRms,gradientMeanRms;
            public float[] gradientViewRms;
            public float[] gradientRegionRms;
            public string colorGuide;
            public int objectPixels, resolution;
            public float detailLoss, colorLoss;
            public bool detailAccepted, colorAccepted;
            public bool normalsEvaluated, rgbaEvaluated;
            public float silhouetteMean, silhouetteMax, normalRms, rgbaRms;
            public float[] silhouetteViewErrors;
            public Vector4 rgbaChannelRms;
            public float[] rgbaViewRms;
            public Vector4[] rgbaChannelViewRms;
            public float headlightRms;
            public float colorBoundaryMean;
            public float[] colorBoundaryViewLoss;
            public float[] normalViewRms, headlightViewRms;
            internal float Penalty(float colorWeight) => 4*(detailLoss+colorLoss*Mathf.Max(0,colorWeight));
            internal float ScreenScore(float normalWeight,float colorWeight) => silhouetteMean/.05f+silhouetteMax/.2f+
                normalRms/30*Mathf.Max(0,normalWeight)+rgbaRms/.1f*Mathf.Max(0,colorWeight)+Penalty(colorWeight);
            internal bool DoesNotWorsen(Report before,bool fields = false) => detailLoss <= before.detailLoss+1e-6f && colorLoss <= before.colorLoss+1e-6f &&
                GradientsDoNotWorsen(before) &&
                (!fields || silhouetteMean <= before.silhouetteMean+1e-6f && silhouetteMax <= before.silhouetteMax+1e-6f &&
                    normalRms <= before.normalRms+1e-6f && ColorsDoNotWorsen(before));
            internal bool ColorsDoNotWorsen(Report before) => colorLoss <= before.colorLoss+1e-6f && rgbaRms <= before.rgbaRms+1e-6f && ChannelsDoNotWorsen(before) && BoundariesDoNotWorsen(before) && GradientsDoNotWorsen(before);
            internal bool PaintViewsDoNotWorsen(Report before) => colorLoss <= before.colorLoss+1e-6f && BoundariesDoNotWorsen(before) && GradientsDoNotWorsen(before);
            internal bool ColorViewsDoNotWorsen(Report before)
                => ColorViewsWithin(before,0) && ColorsDoNotWorsen(before);
            internal bool SilhouetteViewsDoNotWorsen(Report before)
            {
                if (silhouetteViewErrors == null || before.silhouetteViewErrors == null)
                    return silhouetteViewErrors == null && before.silhouetteViewErrors == null;
                return silhouetteViewErrors.Length == before.silhouetteViewErrors.Length &&
                    silhouetteViewErrors.Where((error,v) => error > before.silhouetteViewErrors[v]+1e-6f).Count() == 0;
            }
            internal bool ColorViewsWithin(Report before,float tolerance)
            {
                if (rgbaViewRms == null || before.rgbaViewRms == null || rgbaChannelViewRms == null || before.rgbaChannelViewRms == null ||
                    rgbaViewRms.Length != before.rgbaViewRms.Length || rgbaChannelViewRms.Length != rgbaViewRms.Length ||
                    before.rgbaChannelViewRms.Length != before.rgbaViewRms.Length) return false;
                for (int v = 0; v < rgbaViewRms.Length; v++)
                {
                    if (rgbaViewRms[v] > before.rgbaViewRms[v]+tolerance+1e-6f) return false;
                    for (int ch = 0; ch < 4; ch++) if (rgbaChannelViewRms[v][ch] > before.rgbaChannelViewRms[v][ch]+tolerance+1e-6f) return false;
                }
                return true;
            }
            bool BoundariesDoNotWorsen(Report before)
            {
                if (colorBoundaryViewLoss == null || before.colorBoundaryViewLoss == null) return true;
                if (colorBoundaryViewLoss.Length != before.colorBoundaryViewLoss.Length) return false;
                for (int i = 0; i < colorBoundaryViewLoss.Length; i++)
                    if (colorBoundaryViewLoss[i] > before.colorBoundaryViewLoss[i]+1e-6f) return false;
                return colorBoundaryMean <= before.colorBoundaryMean+1e-6f && GradientsDoNotWorsen(before);
            }
            internal bool GradientsDoNotWorsen(Report before)
            {
                const float tolerance=1f/255+1e-6f;
                if (gradientWorstRegionRms>before.gradientWorstRegionRms+tolerance || gradientMeanRms>before.gradientMeanRms+tolerance) return false;
                if (gradientRegionRms!=null && before.gradientRegionRms!=null &&
                    (gradientRegionRms.Length!=before.gradientRegionRms.Length ||
                    gradientRegionRms.Where((rms,r) => rms>before.gradientRegionRms[r]+tolerance).Any())) return false;
                if (gradientViewRms==null || before.gradientViewRms==null) return true;
                return gradientViewRms.Length==before.gradientViewRms.Length &&
                    gradientViewRms.Where((loss,v) => loss>before.gradientViewRms[v]+tolerance).Count()==0;
            }
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
        readonly float requestedPixelsPerUnit;
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
            requestedPixelsPerUnit = pixelsPerUnit;
            checkColors = colors && source.colors.Length == source.vertexCount;
            hasNormals = source.normals.Length == source.vertexCount;
            origin = source.bounds.center;
            var size = source.bounds.size; sourceExtent = Mathf.Max(size.x,Mathf.Max(size.y,size.z));
            var positions = Positions(source); var triangles = Triangles(source); var rgba = Colors(source);
            sourceSlots = FaceSlots(source); sourceFaceNormals = FaceNormals(positions,triangles);
            var sourceNormals = Normals(source);
            bool varying = colors && HasVariation(rgba);
            var colorRegions=measureFields && varying ? ContinuousColorRegions(source,cancelled) : null;
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
                bool SharpPair(int a,int b)
                {
                    int region=-1;
                    foreach (int pixel in new[] { a,b }) for (int sample=0; sample<4; sample++)
                    {
                        int face=frame.triangleAt[pixel*4+sample]; if (face<0) return true;
                        int next=colorRegions[face]; if (region>=0 && region!=next) return true; region=next;
                    }
                    // Connected vertex paint can still contain a narrow stripe
                    // or abrupt slope change. Only a locally regular ramp is smooth.
                    return !LodScreenAcceptance.HasLinearColorProfile(frame.colors,resolution,a,b,i => frame.coverage[i].r>=.99999f);
                }
                view.acceptance = new LodScreenAcceptance(frame.colors,frame.coverage,resolution,resolution,settings,cancelled,
                    colorRegions!=null ? SharpPair : (Func<int,int,bool>)null);
                views.Add(view);
            }
        }

        internal Report Measure(Mesh mesh,Func<bool> cancelled = null)
        {
            var positions = Positions(mesh); var triangles = Triangles(mesh); var colors = Colors(mesh);
            var normals = Normals(mesh);
            var result = new Report { views = views.Count,detailAccepted = true,objectPixels = objectPixels,resolution = resolution,
                colorGuide=measureFields && checkColors ? "source topology and profile: sharp pairs / local gradient RGBA RMS" : "image profile: sharp pairs / local gradient RGBA RMS",
                normalsEvaluated = measureFields && hasNormals,rgbaEvaluated = measureFields && checkColors,colorBoundaryViewLoss = new float[views.Count],
                gradientViewRms=new float[views.Count],
                normalViewRms = measureFields ? new float[views.Count] : null,headlightViewRms = measureFields ? new float[views.Count] : null,
                silhouetteViewErrors = measureFields ? new float[views.Count] : null,
                rgbaViewRms = measureFields && checkColors ? new float[views.Count] : null,
                rgbaChannelViewRms = measureFields && checkColors ? new Vector4[views.Count] : null };
            int viewIndex = 0;
            var gradientRegions=new List<float>();
            foreach (var view in views)
            {
                var frame = Rasterize(positions,triangles,colors,normals,view,resolution,cancelled,measureFields);
                if (measureFields) MeasureFields(view.source,frame,result,viewIndex,cancelled);
                var report = view.acceptance.Measure(frame.colors,frame.coverage,cancelled);
                result.sharpColorPairs+=report.sharpColorPairs; result.gradientPairs+=report.gradientPairs;
                if (report.gradientEvaluated)
                {
                    result.gradientViews++; result.gradientViewRms[viewIndex]=report.gradientRms;
                    result.gradientMeanRms+=report.gradientRms; result.gradientWorstRegionRms=Mathf.Max(result.gradientWorstRegionRms,report.worstGradientRegionRms);
                }
                gradientRegions.AddRange(report.gradientRegionRms);
                if (report.detailEvaluated) result.detailViews++;
                if (report.colorEvaluated)
                {
                    result.colorViews++; result.colorBoundaryViewLoss[viewIndex] = report.colorBoundaryLoss;
                    result.colorBoundaryMean += report.colorBoundaryLoss;
                }
                result.detailLoss = Mathf.Max(result.detailLoss,Mathf.Max(report.worstThinRegionLoss,report.worstComponentLoss));
                result.colorLoss = Mathf.Max(result.colorLoss,report.worstColorRegionLoss);
                if (report.detailEvaluated) result.detailAccepted &= report.detailAccepted;
                viewIndex++;
            }
            result.colorAccepted = result.colorViews > 0 && result.colorLoss <= .25f;
            if (result.colorViews > 0) result.colorBoundaryMean /= result.colorViews;
            if (result.gradientViews>0) result.gradientMeanRms/=result.gradientViews;
            result.gradientRegionRms=gradientRegions.ToArray();
            result.detailAccepted &= result.detailViews > 0;
            return result;
        }

        internal LodScreenValidation WithAllSides(Mesh source,Func<bool> cancelled = null)
            => new LodScreenValidation(source,checkColors,cancelled,transform,requestedPixelsPerUnit,resolution,objectPixels,measureFields,allSides:true);

        static int[] ContinuousColorRegions(Mesh source,Func<bool> cancelled)
        {
            Check(cancelled);
            var data=new LodMeshData(source); var indices=Triangles(source);
            var regions=Enumerable.Range(0,indices.Length/3).ToArray();
            int Root(int f) { while (regions[f]!=f) { regions[f]=regions[regions[f]]; f=regions[f]; } return f; }
            int offset=0;
            for (int slot=0; slot<source.subMeshCount; slot++)
            {
                var faces=LodMeshData.Triangles(source,slot);
                foreach (var pair in LodSmoothingRegions.EdgePairs(data,faces))
                {
                    Check(cancelled);
                    bool Same(int a,int b) => MaxChannel((Vector4)data.colors[a]-(Vector4)data.colors[b])<=1f/255+1e-6f;
                    if (!Same(pair[0].a,pair[1].b) || !Same(pair[0].b,pair[1].a)) continue;
                    int a=Root(offset+pair[0].face),b=Root(offset+pair[1].face);
                    regions[Mathf.Max(a,b)]=Mathf.Min(a,b);
                }
                offset+=faces.Length/3;
            }
            for (int f=0; f<regions.Length; f++) regions[f]=Root(f);
            return regions;
        }
        static float MaxChannel(Vector4 v) => Mathf.Max(Mathf.Abs(v.x),Mathf.Max(Mathf.Abs(v.y),Mathf.Max(Mathf.Abs(v.z),Mathf.Abs(v.w))));

        internal void CaptureDiagnostic(Mesh mesh,Action<int,Color[],Color[],Color[],Color[]> capture,Func<bool> cancelled = null)
        {
            var positions = Positions(mesh); var triangles = Triangles(mesh); var colors = Colors(mesh); var normals = Normals(mesh);
            for (int i = 0; i < views.Count; i++)
            {
                var view = views[i]; var target = Rasterize(positions,triangles,colors,normals,view,resolution,cancelled,measureFields);
                capture(i,view.source.colors,view.source.coverage,target.colors,target.coverage);
            }
        }

        internal void VisitLostPaintPairs(Mesh mesh,Action<int,int,int,Color,Color,int> visit,Func<bool> cancelled = null)
        {
            var positions = Positions(mesh); var triangles = Triangles(mesh); var colors = Colors(mesh); var normals = Normals(mesh);
            for (int i = 0; i < views.Count; i++)
            {
                int index = i; var view = views[i]; var target = Rasterize(positions,triangles,colors,normals,view,resolution,cancelled,measureFields);
                view.acceptance.VisitLostColorPairs(target.colors,target.coverage,(a,b,ca,cb,region) => visit(index,a,b,ca,cb,region),cancelled);
            }
        }

        internal int[] LostPaintFaces(Mesh mesh,Func<bool> cancelled = null)
        {
            var faces = new SortedSet<int>();
            var positions = Positions(mesh); var indices = Triangles(mesh); var colors = Colors(mesh); var normals = Normals(mesh);
            foreach (var view in views)
            {
                var target = Rasterize(positions,indices,colors,normals,view,resolution,cancelled,true);
                void Add(int a,int b,Color ca,Color cb,int region)
                {
                    int radius = view.acceptance.CorrespondenceRadius;
                    foreach (int pixel in new[] { a,b }) for (int dy = -radius; dy <= radius; dy++) for (int dx = -radius; dx <= radius; dx++)
                    {
                        int x = pixel%resolution+dx,y = pixel/resolution+dy;
                        if (x < 0 || y < 0 || x >= resolution || y >= resolution) continue;
                        for (int sample = 0; sample < 4; sample++)
                        {
                            int face = target.triangleAt[(y*resolution+x)*4+sample]; if (face >= 0) faces.Add(face);
                        }
                    }
                }
                view.acceptance.VisitLostColorPairs(target.colors,target.coverage,Add,cancelled,includeSmallRegions:true);
                // Continuous paint needs diagonal corrections too, without being
                // fed to the categorical per-polygon color stage.
                view.acceptance.VisitLostGradientPairs(target.colors,target.coverage,Add,cancelled);
            }
            return faces.ToArray();
        }

        // Coordinate descent of an already source-derived candidate. Index and
        // attribute buffers stay intact; coincident render wedges move together.
        // Coverage counts permit local face updates without re-rendering the mesh.
        internal Mesh RefineSilhouette(Mesh mesh,out int moves,out int trials,Func<bool> cancelled = null,Func<Mesh,bool> acceptMove = null)
        {
            moves = trials = 0;
            if (!measureFields || objectPixels > 128 || mesh.blendShapeCount > 0 ||
                mesh.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.BlendWeight)) return null;
            var indices = Triangles(mesh); int faces = indices.Length/3;
            if (faces == 0 || faces > 512) return null;
            var positions = mesh.vertices; var originalPositions = (Vector3[])positions.Clone(); var groups = new Dictionary<Vector3,List<int>>();
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
            // A geometry-only optimum can repaint newly covered pixels or move
            // the surface away from its lightmap chart. Validate each group before
            // committing its coverage counts, so one bad move cannot discard all
            // useful moves. This owned probe never changes the input mesh.
            Mesh probe = acceptMove == null ? null : UnityEngine.Object.Instantiate(mesh);
            try
            {
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
                        int[][][] accepted = null; float previousScore = score;
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
                            if (!valid || LodTriangleIntersections.MovesThroughFaces(previous,proposed,positions,indices,faceSet,cancelled) ||
                                LodTriangleIntersections.IntroducesContacts(originalPositions,positions,indices,faceSet,cancelled)) continue;
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
                        if (probe)
                        {
                            Check(cancelled); probe.vertices = positions; probe.RecalculateBounds();
                            if (!acceptMove(probe))
                            {
                                foreach (int vertex in group) positions[vertex] = previous;
                                score = previousScore; continue;
                            }
                        }
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
            finally { if (probe) UnityEngine.Object.DestroyImmediate(probe); }
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
                    if (InsideSample(u,b,c,area) && InsideSample(v,c,a,area) && InsideSample(1-u-v,a,b,area)) samples.Add((y*resolution+x)*4+sample);
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
            result.silhouetteViewErrors[view] = error;
            result.silhouetteMean += error/result.views; result.silhouetteMax = Mathf.Max(result.silhouetteMax,error);
            if (overlap > 0)
            {
                result.normalRms = Mathf.Max(result.normalRms,(float)Math.Sqrt(normalSquared/overlap));
                result.normalViewRms[view] = (float)Math.Sqrt(normalSquared/overlap);
                result.headlightViewRms[view] = (float)Math.Sqrt(headlightSquared/overlap);
                result.headlightRms = Mathf.Max(result.headlightRms,result.headlightViewRms[view]);
                result.rgbaRms = Mathf.Max(result.rgbaRms,(float)Math.Sqrt(colorSquared/overlap));
                if (result.rgbaEvaluated)
                {
                    result.rgbaViewRms[view] = (float)Math.Sqrt(colorSquared/overlap);
                    for (int ch = 0; ch < 4; ch++) result.rgbaChannelViewRms[view][ch] = (float)Math.Sqrt(channelSquared[ch]/overlap);
                }
                for (int ch = 0; ch < 4; ch++) result.rgbaChannelRms[ch] = Mathf.Max(result.rgbaChannelRms[ch],(float)Math.Sqrt(channelSquared[ch]/overlap));
            }
        }
        internal int AddVisibleColorSamples(Mesh mesh,Action<int[],int,Vector3,Vector4,double> add,Func<bool> cancelled = null)
            => AddVisibleSamples(mesh,add,false,cancelled);
        internal int AddVisibleBoundaryColorSamples(Mesh mesh,Action<int[],int,Vector3,Vector4,double> add,float boundaryWeight,Func<bool> cancelled = null)
            => AddVisibleSamples(mesh,add,false,cancelled,boundaryWeight);
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
        int AddVisibleSamples(Mesh mesh,Action<int[],int,Vector3,Vector4,double> add,bool normalField,Func<bool> cancelled,float boundaryWeight = 1)
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
                        double weight = !normalField && view.acceptance.IsColorBoundary(pixel) ? .25*boundaryWeight : .25;
                        add(indices,target*3,frame.baryAt[at],desired,weight); count++;
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
                        // A shared edge belongs to one triangle. Otherwise a color
                        // seam's samples depend on triangle order at equal depth.
                        if (!InsideSample(u,b,c,area) || !InsideSample(v,c,a,area) || !InsideSample(w,a,b,area)) continue;
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
        static bool InsideSample(float bary,Vector3 a,Vector3 b,float area)
        {
            if (bary>1e-6f) return true;
            if (bary<-1e-6f) return false;
            var edge=area>0 ? b-a : a-b;
            return edge.y<0 || edge.y==0 && edge.x<0;
        }
        static float Cross(Vector3 a,Vector3 b) => a.x*b.y-a.y*b.x;
        static void Check(Func<bool> cancelled)
        {
            if (cancelled?.Invoke() == true) throw new OperationCanceledException("LOD screen validation cancelled.");
        }
    }
}
