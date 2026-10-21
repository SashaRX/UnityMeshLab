using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Progressive coarse-to-fine atlas. Intermediate coordinates are texels;
    /// one final square normalization is shared by every LOD.</summary>
    internal static partial class ReverseUvTransfer
    {
        [Serializable] internal sealed class Options
        {
            public int seedResolution = 256, padding = 2, maxAtlasSize = 8192;
            public float projectionReach = .05f, normalDot = .5f, maxAnisotropy = 4;
            internal bool validateCutsIncrementally;
            internal bool projectExistingAtlasOnly;
            // Reverse captures already use world positions. Forward seam trials
            // keep their shared local projection frame and supply its render scale.
            internal Matrix4x4 stretchFrame = Matrix4x4.identity;
            internal float boundaryToleranceTexels = UvTransferTolerance.BoundaryTexels;
            internal float relativeStretchTolerance = UvTransferTolerance.RelativeStretch;
            public bool preserveProjectedOverlap;
            public bool rebuildSeedWithUnity;
            public bool cutNarrowJunctions;
            internal bool regroupPreparedAtlas;
            internal bool frozenShellCascade;
            internal FrozenShellPlacement frozenPlacement;
            internal Action<Result> beforeFinalPacking;
            internal TransferPerformanceProfile performance;
            internal bool cachePackingFootprints = true;
            internal bool cacheNativeCharts = true;
            internal bool indexFootprintSearch = true;
            internal bool cacheCollisionRows = true;
            internal bool nativeFootprintSearch = true;
            internal bool reuseOccupancySnapshots = true;
            internal bool burstFootprintRaster = true;
            // Opt-in: exact, but no corpus gain and slower on Kamaz exclusive (EXPERIMENTS.md).
            internal bool parallelFamilyValidation = false;
            internal ReverseUvOccupancySearch.Api footprintSearchApi;
            internal ReverseUvQueryBackend.Mode queryBackend=ReverseUvQueryBackend.Mode.Auto;
            internal ReverseUvQueryBackend.Api queryApi;
            public bool rotateCharts = true, rotateChartsToAxis;
            internal float seedDensity;
            internal bool repackFinalAtlas;
            public bool fillFinalAtlas;
            public bool splitDonorSeams = true, fillAtlasVacancies = true;
            public long comparisonBudget = 2000000;
            internal Dictionary<(int lod, int node), HashSet<int>> seamCutExclusions;
            internal Dictionary<(int lod,int node,int source),int> overlapPriority;
            internal Dictionary<Input,Vector2[]> newChartParameterizations;
            internal HashSet<(int lod,int node,int source)> inheritedPriority;
        }

        internal sealed class Input
        {
            internal Mesh mesh;
            internal Matrix4x4 toWorld = Matrix4x4.identity;
            internal string key;
            internal bool usePreparedUv2;
            internal ForwardUvReference forwardReference;
            internal int[] sourceFaces, removedSourceFaces;
            internal Vector3[] sourceBarycentrics;
            internal int splitSourceFaces;
        }

        internal sealed class Level
        {
            internal int lod;
            internal Input[] inputs;
        }

        [Serializable] internal sealed class Face
        {
            public int chart, parentLod = -1, parentMesh = -1, parentFace = -1;
            public int overlayMesh = -1, overlayFace = -1, layer;
            public bool inherited, intentionalOverlap, ambiguous, localFallback;
            public bool projectionSupport;
            public float distance;
            public int group;
            public int packingFamily=-1;
            // Independent local UV frames are packed separately. Zero is the
            // shared ordinary Transfer frame; inherited shells adopt the donor's.
            public int coordinateDomain;
            public string rejection;
            public string cutRefusal;
            public string wholeMatchRefusal;
            internal bool protectedInheritance;
            internal int projectionLayer = -1;
            internal HashSet<Face> overlapParents;
            internal Face Copy() => (Face)MemberwiseClone();
        }

        [Serializable] internal sealed class NodeReport
        {
            public int lod;
            public string key;
            public bool seed;
            public int inheritedFaces, newFaces, overlapFaces, ambiguousFaces, localFallbackFaces;
            public Face[] faces;
            // Output face -> pre-cleanup source face; ancestry face indices remain
            // in output order and can be resolved through the parent's map.
            public int[] sourceFaces, removedSourceFaces;
            public int splitSourceFaces;
            public Vector3[] sourceBarycentrics;
        }

        [Serializable] internal sealed class Report
        {
            public int schema = 2, seedResolution, atlasSize, inheritedFaces, newFaces, overlapFaces, ambiguousFaces, localFallbackFaces;
            public int splitSourceFaces;
            public float texelsPerUnit;
            public float boundaryToleranceTexels, relativeStretchTolerance;
            public bool preservesOverlap;
            public List<NodeReport> nodes = new List<NodeReport>();
            public List<OverlapRelation> overlaps = new List<OverlapRelation>();
            public List<ReverseUvCorrespondenceGraph.Edge> edges = new List<ReverseUvCorrespondenceGraph.Edge>();
            public string refinementRefusal;
            public string seedPacking, seedPackingRefusal;
            public string seedPreparation;
            public bool finalPackingFootprints;
            public bool finalPackingNative;
            public bool seedChartRotation;
            public string finalPacking, finalPackingRefusal;
            public string finalPackingRectangleRefusal;
            public int finalPackingFamilies;
            public float finalPackingScale;
            public int resolutionLimitedFaces;
        }

        [Serializable] internal sealed class OverlapRelation
        {
            public int lod, underMesh, underFace, overMesh, overFace;
        }

        internal sealed class Result : IDisposable
        {
            internal Report report;
            internal readonly List<Mesh[]> meshes = new List<Mesh[]>();
            internal List<Surface[]> surfaces;
            public void Dispose()
            {
                foreach (var level in meshes)
                    foreach (var mesh in level)
                        if (mesh) UnityEngine.Object.DestroyImmediate(mesh);
                meshes.Clear();
                surfaces=null;
            }
        }

        internal sealed class Surface
        {
            internal ReverseUvAttachmentSource attachmentSource;
            internal Input input;
            internal Vector3[] positions;
            internal int[] indices;
            internal Vector3[] sourcePositions;
            internal Vector3 projectionOrigin;
            internal int[] sourceIndices;
            internal Vector2[] sourceUvCorners;
            internal int[] preparedRegions;
            internal Vector2[] pixels;
            internal Vector2[] packingCoordinates;
            internal Face[] faces;
            internal int lod, node;
            internal int orientation;
            internal int[] sourceFaces;
            internal Vector3[] sourceBarycentrics;
            internal int splitSourceFaces;
            internal (double length,double x,double y)[] packingFrames;
            internal (double length,double x,double y)[] metricFrames;
        }

        internal sealed class Chart
        {
            internal int id;
            internal int coordinateDomain;
            internal Vector3[] positions, normals;
            internal Vector2[] pixels;
            internal int[] indices, nodes, faces, layers, orientations;
            internal TriangleBvh bvh;
            internal Bounds bounds;
            // Pixel-space triangles (z = 0), built on first coverage query.
            // Chart pixels and layers never change after construction.
            internal PixelIndex pixelIndex;
        }

        internal sealed class PixelIndex
        {
            // Null when a pixel is not finite: such a chart keeps the plain scan.
            internal TriangleBvh bvh;
        }

        static async Task<Result> BuildCore(Level[] levels, Options options, bool useAsync = false,
            CancellationToken token = default)
        {
            Validate(levels, options);
            var result = new Result { report = new Report { seedResolution = options.seedResolution,
                preservesOverlap = options.preserveProjectedOverlap, boundaryToleranceTexels = options.boundaryToleranceTexels,
                relativeStretchTolerance = options.relativeStretchTolerance } };
            var all = new List<Surface[]>();
            int nextChart = 0;
            float width = options.seedResolution, height = width;
            var origin = levels[0].inputs[0].toWorld.GetColumn(3);
            try
            {
                token.ThrowIfCancellationRequested();
                long captureStarted = options.performance == null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
                var preparedChain=options.regroupPreparedAtlas?levels.Select(l=>Capture(l,origin)).ToArray():null;
                if(preparedChain!=null)
                {
                    if(options.frozenShellCascade && !options.preserveProjectedOverlap)
                    {
                        // Unity mesh access stays on the editor thread. Every
                        // normalization/repair/cut below uses these owned arrays.
                        var preparedUv=preparedChain.SelectMany(l=>l).ToDictionary(s=>s,s=>s.input.mesh.uv2);
                        void PrepareFrozen()=>InitializeFrozenShellChain(preparedChain,options,result.report,
                            ref nextChart,ref width,ref height,token,preparedUv);
                        if(useAsync) await Task.Run(PrepareFrozen,token);
                        else PrepareFrozen();
                    }
                    else InitializePreparedChain(preparedChain,options,ref nextChart,ref width,ref height,token);
                }
                var seed = preparedChain==null?Capture(levels[0], origin):preparedChain[0];
                if(preparedChain==null) InitializeSeed(seed, options, ref nextChart, token);
                options.performance?.Record("Capture and normalization", System.Diagnostics.Stopwatch.GetTimestamp() - captureStarted);
                float density = options.seedDensity>0?options.seedDensity:Density(seed);
                result.report.texelsPerUnit = density;
                all.Add(seed);
                for (int level = 1; level < levels.Length; ++level)
                {
                    using var levelTiming = options.performance?.Measure("LOD correspondence and grouping", levels[level].lod);
                    token.ThrowIfCancellationRequested();
                    var targets = preparedChain==null?Capture(levels[level], origin):preparedChain[level];
                    if(preparedChain!=null)
                    {
                        void ProjectStep()
                        {
                            if(options.frozenShellCascade && !options.preserveProjectedOverlap)
                                ProjectFrozenShells(all[level-1],targets,options,ref nextChart,ref width,ref height,token);
                            else ProjectPreparedChain(all[level-1],targets,options,token);
                        }
                        if(useAsync) await Task.Run(ProjectStep,token);
                        else ProjectStep();
                        RecordOverlapRelations(targets,result.report,options,token);
                        all.Add(targets);
                        continue;
                    }
                    if(options.newChartParameterizations==null) options.newChartParameterizations=new Dictionary<Input,Vector2[]>();
                    foreach(var target in targets)
                    {
                        if(!options.newChartParameterizations.TryGetValue(target.input,out var uv))
                        {
                            var sourceUv=target.input.mesh.uv;
                            uv=target.indices.Select(i=>sourceUv.Length==target.positions.Length?sourceUv[i]:Vector2.zero).ToArray();
                            for(int i=0;i<uv.Length;++i) if(!Finite(uv[i].x)||!Finite(uv[i].y)) uv[i]=Vector2.zero;
                            uv=ReverseUvSeed.RepairAuthoredCharts(target.indices.Select(i=>target.positions[i]).ToArray(),uv,
                                new int[uv.Length],token,out _,joinPlanar:true);
                            options.newChartParameterizations.Add(target.input,uv);
                        }
                        target.sourceUvCorners=uv;
                    }
                    var donor = all[level - 1];
                    if (useAsync)
                        await Task.Run(() => Project(donor, targets, options, token), token);
                    else Project(donor, targets, options, token);
                    AppendNew(targets, all, density, options, ref width, ref height, ref nextChart, token);
                    if (useAsync)
                        await Task.Run(() => ProjectNewUnderlays(targets, options, token), token);
                    else ProjectNewUnderlays(targets, options, token);
                    RecordOverlapRelations(targets, result.report, options, token);
                    if (Math.Max(width, height) > options.maxAtlasSize)
                        throw new InvalidOperationException("Reverse UV atlas exceeds the configured size limit; no result was published.");
                    all.Add(targets);
                }
                int side = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Math.Max(width, height)));
                if (side > options.maxAtlasSize && !(options.frozenShellCascade && options.repackFinalAtlas))
                    throw new InvalidOperationException("Reverse UV square atlas exceeds the size limit.");
                result.report.atlasSize = side;
                using var outputTiming = options.performance?.Measure("Correspondence graph and output meshes");
                foreach (var level in all)
                {
                    result.report.edges.AddRange(ReverseUvCorrespondenceGraph.Build(level, token).edges);
                    token.ThrowIfCancellationRequested();
                    var outputs = new Mesh[level.Length];
                    result.meshes.Add(outputs);
                    foreach (var surface in level)
                    {
                        var normalized = surface.pixels.Select(p => p / side).ToArray();
                        outputs[surface.node] = surface.sourceBarycentrics == null
                            ? ReverseUvMesh.Copy(surface.input.mesh, surface.indices, normalized)
                            : ReverseUvRefinedMesh.Copy(surface.input.mesh, surface.sourceFaces, surface.sourceBarycentrics, normalized);
                        var node = new NodeReport { lod = surface.lod, key = surface.input.key, faces = surface.faces,
                            sourceFaces = surface.sourceFaces.Select(f => surface.input.sourceFaces == null ? f : surface.input.sourceFaces[f]).ToArray(),
                            removedSourceFaces = surface.input.removedSourceFaces, splitSourceFaces = surface.splitSourceFaces + surface.input.splitSourceFaces,
                            sourceBarycentrics = ReportBarycentrics(surface),
                            seed = surface.lod == levels[0].lod };
                        foreach (var face in surface.faces)
                        {
                            if (face.inherited) ++node.inheritedFaces;
                            else if (!node.seed) ++node.newFaces;
                            if (face.intentionalOverlap) ++node.overlapFaces;
                            if (face.ambiguous) ++node.ambiguousFaces;
                            if (face.localFallback) ++node.localFallbackFaces;
                        }
                        result.report.nodes.Add(node);
                        result.report.inheritedFaces += node.inheritedFaces;
                        result.report.newFaces += node.newFaces;
                        result.report.overlapFaces += node.overlapFaces;
                        result.report.ambiguousFaces += node.ambiguousFaces;
                        result.report.localFallbackFaces += node.localFallbackFaces;
                        result.report.splitSourceFaces += node.splitSourceFaces;
                    }
                }
                result.surfaces=all;
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        static async Task FinalizePacking(Result result,Options options,bool useAsync,CancellationToken token)
        {
            if(!options.repackFinalAtlas) return;
            CapturePackingEdges(result,token);
            options.beforeFinalPacking?.Invoke(result);
            int previousSide=result.report.atlasSize;
            int side;
            using (options.performance?.Measure("Final packing and atlas audits"))
                side=useAsync?await Task.Run(()=>ReverseUvFinalPacking.Pack(result.surfaces,options,result.report,previousSide,token),token)
                    :ReverseUvFinalPacking.Pack(result.surfaces,options,result.report,previousSide,token);
            if(result.report.finalPacking==null)
                throw new InvalidOperationException("Reverse UV final atlas packing failed; no result was published: "
                    +result.report.finalPackingRefusal);
            using var replacement=new Result();
            using var meshTiming = options.performance?.Measure("Final output meshes");
            foreach(var level in result.surfaces)
            {
                token.ThrowIfCancellationRequested();
                var outputs=new Mesh[level.Length];replacement.meshes.Add(outputs);
                foreach(var surface in level)
                {
                    var normalized=surface.pixels.Select(p=>p/side).ToArray();
                    outputs[surface.node]=surface.sourceBarycentrics==null
                        ?ReverseUvMesh.Copy(surface.input.mesh,surface.indices,normalized)
                        :ReverseUvRefinedMesh.Copy(surface.input.mesh,surface.sourceFaces,surface.sourceBarycentrics,normalized);
                }
            }
            foreach(var level in result.meshes)
                foreach(var mesh in level) if(mesh) UnityEngine.Object.DestroyImmediate(mesh);
            result.meshes.Clear();result.meshes.AddRange(replacement.meshes);replacement.meshes.Clear();
            result.report.atlasSize=side;
        }

        static void CapturePackingEdges(Result result,CancellationToken token)
        {
            for(int level=0;level<result.surfaces.Count;++level)
                foreach(var surface in result.surfaces[level])
                {
                    token.ThrowIfCancellationRequested();
                    var mesh=result.meshes[level][surface.node];
                    var positions=mesh.vertices;var indices=mesh.triangles;
                    surface.packingFrames=new (double,double,double)[surface.faces.Length];
                    for(int f=0;f<surface.faces.Length;++f)
                    {
                        int t=f*3;var a=positions[indices[t]];
                        surface.packingFrames[f]=TransferUvQuality.WorldFrame(a,positions[indices[t+1]],positions[indices[t+2]],surface.input.toWorld);
                    }
                }
        }

        static void Validate(Level[] levels, Options options)
        {
            if (levels == null || levels.Length < 2 || options == null)
                throw new ArgumentException("Reverse UV requires a coarse-to-fine chain with at least two levels.");
            if (options.seedResolution < 16 || options.seedResolution > options.maxAtlasSize || options.padding < 0
                || options.maxAtlasSize > 16384 || options.projectionReach <= 0 || !Finite(options.projectionReach)
                || options.normalDot <= 0 || options.normalDot > 1 || !Finite(options.normalDot) || options.maxAnisotropy < 1
                || !Finite(options.maxAnisotropy) || options.comparisonBudget <= 0
                || !Finite(options.boundaryToleranceTexels) || options.boundaryToleranceTexels<0 || options.boundaryToleranceTexels>.5f
                || !Finite(options.relativeStretchTolerance) || options.relativeStretchTolerance<0 || options.relativeStretchTolerance>.01f)
                throw new ArgumentException("Invalid reverse UV settings.");
            for (int i = 0; i < levels.Length; ++i)
            {
                if (levels[i] == null || levels[i].inputs == null || levels[i].inputs.Length == 0
                    || (i > 0 && levels[i].lod >= levels[i - 1].lod))
                    throw new ArgumentException("Levels must be nonempty and ordered from coarsest to finest.");
                foreach (var input in levels[i].inputs)
                    if (input == null || !input.mesh || !input.mesh.isReadable
                        || !Nonsingular(input.toWorld))
                        throw new ArgumentException("Reverse UV inputs require readable meshes and nonsingular transforms.");
            }
        }

        static double LinearDeterminant(Matrix4x4 m)
            => (double)m.m00*((double)m.m11*m.m22-(double)m.m12*m.m21)
                -(double)m.m01*((double)m.m10*m.m22-(double)m.m12*m.m20)
                +(double)m.m02*((double)m.m10*m.m21-(double)m.m11*m.m20);

        static bool Nonsingular(Matrix4x4 m)
        {
            for(int i=0;i<16;++i) if(!Finite(m[i])) return false;
            // Scale magnitude reflects units, not singularity. No inverse is
            // used here; transformed-face validation detects actual collapse.
            return LinearDeterminant(m)!=0;
        }

        // One translated world frame for the entire chain. Subtract translation
        // in double before rounding positions to float for the BVH/unwrap.
        static Vector3[] ReportBarycentrics(Surface surface)
        {
            var source=surface.input.sourceBarycentrics;
            if(source==null) return surface.sourceBarycentrics;
            var result=new Vector3[surface.faces.Length*3];
            for(int f=0;f<surface.faces.Length;++f)
                for(int k=0;k<3;++k)
                {
                    int t=surface.sourceFaces[f]*3;
                    var b=surface.sourceBarycentrics==null
                        ? (k==0?Vector3.right:k==1?Vector3.up:Vector3.forward)
                        :surface.sourceBarycentrics[f*3+k];
                    result[f*3+k]=source[t]*b.x+source[t+1]*b.y+source[t+2]*b.z;
                }
            return result;
        }

        internal static Vector3[] RelativePositions(Input input, Vector3 origin, int lod = -1)
        {
            var local = input.mesh.vertices;
            var indices = input.mesh.triangles;
            for (int f = 0; f < indices.Length; f += 3)
            {
                int a = indices[f], b = indices[f + 1], c = indices[f + 2];
                if (!MeshGeometry.HasArea(local[a], local[b], local[c]))
                    throw new InvalidOperationException($"Reverse UV input '{input.key ?? input.mesh.name}' LOD{lod} face {f / 3} has a degenerate geometric triangle in source-local coordinates "
                        + $"(vertices {a}, {b}, {c}); check duplicate/collinear source vertices. No geometry was removed.");
            }
            var m = input.toWorld;
            var positions = new Vector3[local.Length];
            for (int i = 0; i < local.Length; ++i)
            {
                var p = local[i];
                positions[i] = RelativePosition(m,p,origin);
            }
            for (int f = 0; f < indices.Length; f += 3)
                if (!MeshGeometry.HasArea(positions[indices[f]], positions[indices[f + 1]], positions[indices[f + 2]]))
                    throw new InvalidOperationException($"Reverse UV input '{input.key ?? input.mesh.name}' LOD{lod} face {f / 3} collapses in the shared projection frame; "
                        + "check transform scale and geometry precision. No result was published.");
            return positions;
        }

        internal static Vector3 RelativePosition(Matrix4x4 m,Vector3 p,Vector3 origin)
            =>new Vector3((float)((double)m.m00*p.x+(double)m.m01*p.y+(double)m.m02*p.z+((double)m.m03-origin.x)),
                (float)((double)m.m10*p.x+(double)m.m11*p.y+(double)m.m12*p.z+((double)m.m13-origin.y)),
                (float)((double)m.m20*p.x+(double)m.m21*p.y+(double)m.m22*p.z+((double)m.m23-origin.z)));

        static Surface[] Capture(Level level, Vector3 origin)
        {
            var surfaces = new Surface[level.inputs.Length];
            for (int node = 0; node < surfaces.Length; ++node)
            {
                var input = level.inputs[node];
                var indices = input.mesh.triangles;
                if (indices.Length == 0 || indices.Length > 750000)
                    throw new InvalidOperationException("Reverse UV prototype supports 1–250000 triangles per input.");
                var positions = RelativePositions(input, origin, level.lod);
                surfaces[node] = new Surface { input = input, indices = indices, positions = positions,projectionOrigin=origin,
                    sourcePositions = input.mesh.vertices, sourceIndices = indices.ToArray(),
                    pixels = new Vector2[indices.Length], faces = Enumerable.Range(0, indices.Length / 3).Select(_ => new Face()).ToArray(),
                    sourceFaces = Enumerable.Range(0, indices.Length / 3).ToArray(),
                    lod = level.lod, node = node, orientation = Math.Sign(LinearDeterminant(input.toWorld)) };
                RefreshMetricFrames(surfaces[node]);
            }
            return surfaces;
        }

        internal static void RefreshMetricFrames(Surface surface)
        {
            // Seam refinement also runs on the projection worker. Only use the
            // source arrays captured on the editor thread, never Mesh accessors.
            var vertices=surface.sourcePositions;var triangles=surface.sourceIndices;
            surface.metricFrames=new (double,double,double)[surface.faces.Length];
            var local=new Vector3[3];
            for(int f=0;f<surface.faces.Length;++f)
            {
                int source=surface.sourceFaces[f]*3;
                for(int k=0;k<3;++k)
                {
                    var b=surface.sourceBarycentrics==null?(k==0?Vector3.right:k==1?Vector3.up:Vector3.forward):surface.sourceBarycentrics[f*3+k];
                    local[k]=ReverseUvRefinedMesh.InterpolatePosition(vertices[triangles[source]],vertices[triangles[source+1]],vertices[triangles[source+2]],b);
                }
                surface.metricFrames[f]=TransferUvQuality.WorldFrame(local[0],local[1],local[2],surface.input.toWorld);
            }
        }

        static void InitializeSeed(Surface[] seed, Options options, ref int nextChart, CancellationToken token)
        {
            foreach (var surface in seed)
            {
                token.ThrowIfCancellationRequested();
                var uv = surface.input.mesh.uv2;
                if (uv.Length != surface.positions.Length) throw new InvalidOperationException("Coarsest LOD requires a prepared UV2 atlas.");
                for (int i = 0; i < surface.indices.Length; ++i)
                {
                    var p = uv[surface.indices[i]];
                    if (!Finite(p.x) || !Finite(p.y) || p.x < 0 || p.y < 0 || p.x > 1 || p.y > 1)
                        throw new InvalidOperationException("Coarsest UV2 is invalid or outside its atlas.");
                    surface.pixels[i] = p * options.seedResolution;
                }
                AssignCharts(surface, Enumerable.Range(0, surface.faces.Length).ToArray(), ref nextChart);
                for (int f = 0; f < surface.faces.Length; ++f)
                    if (Anisotropy(surface, f) > options.maxAnisotropy)
                        throw new InvalidOperationException($"Coarsest UV2 '{surface.input.key}' face {f} is collapsed or stretched (anisotropy {Anisotropy(surface,f):G5}); prepare its atlas first.");
            }
            var geometry = Atlas(seed);
            var quality = UvAtlasDiagnostics.Measure(geometry, token, comparisonBudget: options.comparisonBudget);
            if (!quality.complete || quality.pairs > 0 || quality.invalidFaces > 0 || quality.degenerateFaces > 0)
                throw new InvalidOperationException("Coarsest UV2 must have a fully checked nonoverlapping atlas.");
        }

        static float Density(Surface[] seed)
        {
            var densities = new List<double>();
            foreach (var s in seed)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    int t = f * 3;
                    double area = Vector3.Cross(s.positions[s.indices[t + 1]] - s.positions[s.indices[t]],
                        s.positions[s.indices[t + 2]] - s.positions[s.indices[t]]).magnitude;
                    double density=Math.Sqrt(Math.Abs(Cross(s.pixels[t + 1] - s.pixels[t], s.pixels[t + 2] - s.pixels[t])) / area);
                    if(density>0 && !double.IsInfinity(density) && !double.IsNaN(density)) densities.Add(density);
                }
            densities.Sort();
            return densities.Count==0?0:(float)densities[densities.Count / 2];
        }

        internal static List<Chart> Charts(Surface[] donor)
        {
            var grouped = new SortedDictionary<int, List<(Surface s, int f)>>();
            foreach (var s in donor)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    int id = s.faces[f].chart;
                    if (!grouped.TryGetValue(id, out var faces)) { faces = new List<(Surface, int)>(); grouped.Add(id, faces); }
                    faces.Add((s, f));
                }
            var charts = new List<Chart>();
            foreach (var pair in grouped)
            {
                int n = pair.Value.Count;
                var chart = new Chart { id = pair.Key, coordinateDomain=pair.Value[0].s.faces[pair.Value[0].f].coordinateDomain,
                    positions = new Vector3[n * 3], pixels = new Vector2[n * 3],
                    indices = Enumerable.Range(0, n * 3).ToArray(), normals = new Vector3[n], nodes = new int[n], faces = new int[n], layers = new int[n], orientations = new int[n] };
                for (int f = 0; f < n; ++f)
                {
                    var (s, face) = pair.Value[f];
                    for (int k = 0; k < 3; ++k)
                    {
                        chart.positions[f * 3 + k] = s.positions[s.indices[face * 3 + k]];
                        chart.pixels[f * 3 + k] = s.pixels[face * 3 + k];
                    }
                    chart.normals[f] = Normal(s, face);
                    chart.nodes[f] = s.node; chart.faces[f] = face; chart.layers[f] = s.faces[face].layer;
                    chart.orientations[f] = s.orientation;
                }
                chart.bounds = new Bounds(chart.positions[0], Vector3.zero);
                foreach (var p in chart.positions) chart.bounds.Encapsulate(p);
                chart.bvh = new TriangleBvh(chart.positions, chart.indices);
                charts.Add(chart);
            }
            return charts;
        }

        static readonly Vector3[] Interior = { new Vector3(.5f, .5f, 0), new Vector3(0, .5f, .5f),
            new Vector3(.5f, 0, .5f), Vector3.one / 3 };

        static TriangleBvh.HitResult NearestDonor(Chart chart, Vector3 point, Vector3 normal, Options options,
            ref long comparisons, CancellationToken token, out bool ambiguous, TriangleBvh.HitResult? precomputed=null)
        {
            var hit = precomputed??chart.bvh.FindNearestNormalFiltered(point, normal, chart.normals, options.normalDot, options.projectionReach);
            ambiguous = false;
            if (hit.triangleIndex < 0) return hit;
            var pixel = Pixel(chart, hit);
            float tolerance = Math.Max(1e-12f, options.projectionReach * options.projectionReach * 1e-6f);
            ambiguous = chart.bvh.AnyNormalFilteredWithin(point, normal, chart.normals, options.normalDot,
                hit.distSq + tolerance, candidate => candidate.triangleIndex != hit.triangleIndex
                    && (Pixel(chart, candidate) - pixel).sqrMagnitude > 1e-6f,
                ref comparisons, options.comparisonBudget, token);
            return hit;
        }

        static void Project(Surface[] donor, Surface[] targets, Options options, CancellationToken token)
        {
            List<Chart> charts;
            using (options.performance?.Accumulated("Projection donor charts")) charts = Charts(donor);
            long queries = 0;
            ProjectInitial(donor, targets, charts, options, ref queries, token);
            ProjectAfterInitial(donor, targets, charts, options, queries, token);
        }

        // Face projection and the first corner snap read neither the cut options
        // nor protected faces, so passes over one target can share their result.
        static void ProjectInitial(Surface[] donor, Surface[] targets, List<Chart> charts, Options options,
            ref long queries, CancellationToken token)
        {
            var performance = options.performance;
            using (performance?.Accumulated("Projection initial faces")) ProjectFaces(donor, targets, charts, options, ref queries, token);
            using (performance?.Accumulated("Projection corner snapping")) SnapContinuousCorners(targets, options);
        }

        static void ProjectAfterInitial(Surface[] donor, Surface[] targets, List<Chart> charts, Options options,
            long queries, CancellationToken token, DonorSeamIndex seamIndex = null)
        {
            var performance = options.performance;
            MarkProtectedSeamInheritance(targets, options);
            using (performance?.Accumulated("Projection overlap resolution")) ResolveOverlaps(targets, options, token);
            if (options.splitDonorSeams)
            {
                foreach (var s in targets) foreach (var face in s.faces) face.protectedInheritance |= face.inherited;
                using (performance?.Accumulated("Projection donor seam refinement")) RefineAtDonorSeams(donor, targets, options, token, seamIndex);
                if (targets.Any(s => s.sourceBarycentrics != null))
                    using (performance?.Accumulated("Projection refined faces"))
                        ProjectFaces(donor, targets.Where(s => s.sourceBarycentrics != null).ToArray(), charts, options, ref queries, token);
            }
            using (performance?.Accumulated("Projection corner snapping")) SnapContinuousCorners(targets, options);
            using (performance?.Accumulated("Projection overlap resolution")) ResolveOverlaps(targets, options, token);
        }

        static void MarkProtectedSeamInheritance(Surface[] targets, Options options)
        {
            foreach (var target in targets)
                if (options.seamCutExclusions != null && options.seamCutExclusions.TryGetValue((target.lod, target.node), out var protectedSources))
                    for (int face = 0; face < target.faces.Length; ++face)
                        target.faces[face].protectedInheritance = protectedSources.Contains(target.input.sourceFaces == null ? face : target.input.sourceFaces[face]);
        }

        /// <summary>Ordered superset of the charts whose bounds lie within reach of a
        /// point. Each chart is a degenerate triangle spanning its bounds plus a
        /// margin far above Float32 rounding of the distance test. Callers still
        /// apply that exact test to every candidate, in chart order, so the
        /// accepted charts and their order equal a scan of every chart.</summary>
        internal sealed class ChartBoundsIndex
        {
            readonly TriangleBvh bvh;
            ChartBoundsIndex(TriangleBvh bvh) { this.bvh = bvh; }

            /// <returns>Null when every chart must be scanned: no charts, an
            /// unbounded reach or non-finite chart bounds.</returns>
            internal static ChartBoundsIndex Create(List<Chart> charts, float reach, CancellationToken token = default)
            {
                token.ThrowIfCancellationRequested();
                if (charts.Count == 0 || !Finite(reach) || !Finite(reach * reach)) return null;
                var corners = new Vector3[charts.Count * 3];
                for (int i = 0; i < charts.Count; ++i)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    var min = charts[i].bounds.min; var max = charts[i].bounds.max;
                    float scale = Math.Max(Math.Max(Math.Max(Math.Abs(min.x), Math.Abs(min.y)), Math.Abs(min.z)),
                        Math.Max(Math.Max(Math.Abs(max.x), Math.Abs(max.y)), Math.Abs(max.z)));
                    if (!Finite(scale)) return null;
                    var pad = Vector3.one * (reach * 1.01f + 1e-5f * (1 + scale));
                    corners[i * 3] = min - pad; corners[i * 3 + 1] = corners[i * 3 + 2] = max + pad;
                }
                return new ChartBoundsIndex(new TriangleBvh(corners, Enumerable.Range(0, corners.Length).ToArray()));
            }

            /// <summary>Candidate chart indices in ascending order, written to the
            /// caller's buffer so concurrent queries never share one.</summary>
            internal List<int> Near(Vector3 point, List<int> nearby)
            {
                nearby.Clear();
                bvh.CollectOverlapping(new Bounds(point, Vector3.zero), nearby);
                nearby.Sort();
                return nearby;
            }
        }

        internal static void ProjectFaces(Surface[] donor, Surface[] targets, List<Chart> charts, Options options,
            ref long queries, CancellationToken token)
        {
            if (targets.Length == 0) return;
            using var projectionTiming = options.performance?.Measure("Face projection", targets[0].lod);
            using var batch=new ProjectionBatch(charts,options,token);
            var projector = new FaceProjector(donor, charts, options, token);
            var counters = projector.ProjectInParallel(targets, ref queries) ?? projector.ProjectInOrder(targets, batch, ref queries);
            options.performance?.Record("Chart bounds tested", 0, counters.candidates, targets[0].lod);
            options.performance?.Record("Parent footprint coverage", counters.footprintTicks, counters.footprintCalls, targets[0].lod);
        }

        // Interior probes alone miss holes between probes. Parent faces in one
        // layer have disjoint UV interiors, so their clipped areas measure exact
        // coverage without counting overlapping detail layers twice.
        // Charts below this face count keep the plain scan; the index only pays off
        // on larger parents. Internal so tests can force either path.
        internal static int CoverageIndexMinimumFaces { get; set; } = 64;
        [ThreadStatic] static List<int> coverageCandidates;

        internal static bool CoveredByParentLayer(Chart parent, int layer, Vector2[] pixels, int start,
            Options options, ref long comparisons, CancellationToken token)
        {
            var triangle = new[] { pixels[start], pixels[start + 1], pixels[start + 2] };
            double area = Math.Abs(Cross(triangle[1] - triangle[0], triangle[2] - triangle[0]));
            double covered = 0;
            var min = Vector2.Min(triangle[0], Vector2.Min(triangle[1], triangle[2]));
            var max = Vector2.Max(triangle[0], Vector2.Max(triangle[1], triangle[2]));
            int faceCount = parent.layers.Length;
            // The scan charges one comparison per parent face and throws once the
            // budget is exceeded; check cancellation and that outcome before the
            // first query builds the chart's index.
            if (faceCount >= CoverageIndexMinimumFaces)
            {
                token.ThrowIfCancellationRequested();
                if (comparisons + faceCount > options.comparisonBudget) throw new InvalidOperationException("Reverse UV footprint budget exceeded.");
            }
            var index = faceCount < CoverageIndexMinimumFaces ? null : LazyInitializer.EnsureInitialized(ref parent.pixelIndex, () => new PixelIndex {
                bvh = parent.pixels.All(p => !float.IsNaN(p.x) && !float.IsInfinity(p.x) && !float.IsNaN(p.y) && !float.IsInfinity(p.y))
                    ? new TriangleBvh(parent.pixels.Select(p => new Vector3(p.x, p.y, 0)).ToArray(), Enumerable.Range(0, parent.pixels.Length).ToArray())
                    : null }).bvh;
            if (index != null)
            {
                // All comparisons are charged first (checked above against the
                // budget). Candidates come back in ascending face order, so the
                // clipped areas are summed in the scan's order.
                comparisons += faceCount;
                var candidates = coverageCandidates ??= new List<int>();
                candidates.Clear();
                index.CollectOverlapping(new Vector3(min.x, min.y, 0), new Vector3(max.x, max.y, 0), candidates);
                candidates.Sort();
                foreach (int f in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (parent.layers[f] == layer) covered += ClippedCoverage(parent, f, triangle, min, max);
                }
            }
            else
                for (int f = 0; f < faceCount; ++f)
                {
                    token.ThrowIfCancellationRequested();
                    if (++comparisons > options.comparisonBudget) throw new InvalidOperationException("Reverse UV footprint budget exceeded.");
                    if (parent.layers[f] != layer) continue;
                    covered += ClippedCoverage(parent, f, triangle, min, max);
                }
            if(area > 0 && covered >= area * (1 - 1e-5)) return true;
            var regions=Enumerable.Range(0,parent.layers.Length).Where(f=>parent.layers[f]==layer)
                .Select(f=>new[] {parent.pixels[f*3],parent.pixels[f*3+1],parent.pixels[f*3+2]});
            return UvTransferTolerance.Covers(triangle,regions,options.boundaryToleranceTexels,
                ref comparisons,options.comparisonBudget,token);
        }

        /// <summary>Area of the triangle clipped to one parent face; zero when
        /// their pixel bounds do not overlap.</summary>
        static double ClippedCoverage(Chart parent, int f, Vector2[] triangle, Vector2 min, Vector2 max)
        {
            int t = f * 3;
            var a = parent.pixels[t]; var b = parent.pixels[t + 1]; var c = parent.pixels[t + 2];
            var lo = Vector2.Min(a, Vector2.Min(b, c)); var hi = Vector2.Max(a, Vector2.Max(b, c));
            if (hi.x <= min.x || hi.y <= min.y || lo.x >= max.x || lo.y >= max.y) return 0;
            var polygon = new List<Vector2>(triangle);
            double orientation = Math.Sign(Cross(b - a, c - a));
            for (int edge = 0; edge < 3 && polygon.Count > 0; ++edge)
            {
                var x = parent.pixels[t + edge]; var y = parent.pixels[t + (edge + 1) % 3];
                var clipped = new List<Vector2>(); var previous = polygon[polygon.Count - 1];
                double previousSide = orientation * Cross(y - x, previous - x);
                foreach (var point in polygon)
                {
                    double side = orientation * Cross(y - x, point - x);
                    if ((side >= 0) != (previousSide >= 0))
                        clipped.Add(Vector2.LerpUnclamped(previous, point, (float)(previousSide / (previousSide - side))));
                    if (side >= 0) clipped.Add(point);
                    previous = point; previousSide = side;
                }
                polygon = clipped;
            }
            double clippedArea = 0;
            for (int k = 1; k + 1 < polygon.Count; ++k)
                clippedArea += Cross(polygon[k] - polygon[0], polygon[k + 1] - polygon[0]);
            return Math.Abs(clippedArea);
        }

        static void SnapContinuousCorners(Surface[] targets, Options options)
        {
            var canonical = new Dictionary<(Vector3, int, int), Vector2>();
            foreach (var s in targets)
                for (int f = 0; f < s.faces.Length; ++f)
                {
                    var face = s.faces[f];
                    if (!face.inherited) continue;
                    for (int k = 0; k < 3; ++k)
                    {
                        int t = f * 3 + k;
                        var key = (s.positions[s.indices[t]], face.chart, face.layer);
                        if (canonical.TryGetValue(key, out var pixel))
                        {
                            if ((pixel - s.pixels[t]).sqrMagnitude <= 1e-6f) s.pixels[t] = pixel;
                        }
                        else canonical.Add(key, s.pixels[t]);
                    }
                    if (Anisotropy(s, f, options.stretchFrame) > options.maxAnisotropy)
                    { face.inherited = false; face.ambiguous = true; face.rejection = "snap-metric"; }
                }
        }

        static Vector2 Pixel(Chart chart, TriangleBvh.HitResult hit)
        {
            int t = hit.triangleIndex * 3;
            return chart.pixels[t] * hit.barycentric.x + chart.pixels[t + 1] * hit.barycentric.y + chart.pixels[t + 2] * hit.barycentric.z;
        }

        static void RecordOverlapRelations(Surface[] surfaces, Report report, Options options, CancellationToken token)
        {
            var refs=surfaces.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
            foreach(var part in CorrespondenceDomains(refs))
            {
                var scan = UvAtlasDiagnostics.Measure(part.geometry, token, comparisonBudget: options.comparisonBudget, collectConflicts:true);
                if(!scan.complete || scan.invalidFaces>0 || scan.degenerateFaces>0)
                    throw new InvalidOperationException($"Reverse UV correspondence validation failed (LOD{surfaces[0].lod}, complete {scan.complete}, invalid {scan.invalidFaces}, collapsed {scan.degenerateFaces}).");
                foreach(var (localA,localB) in scan.conflicts)
                {
                    int a=part.faces[localA],b=part.faces[localB];
                    var under=refs[a]; var over=refs[b];
                    if(under.s.faces[under.f].layer>over.s.faces[over.f].layer) { var swap=under; under=over; over=swap; }
                    var parent=under.s.faces[under.f]; var detail=over.s.faces[over.f];
                    if(!options.preserveProjectedOverlap || !IsOrderedOverlap(parent,detail))
                        throw new InvalidOperationException($"Reverse UV overlap has no ordered intentional ancestry: LOD{under.s.lod} "
                            +$"'{under.s.input.key}' face {under.f} (chart {parent.chart}, layer {parent.layer}, inherited {parent.inherited}, support {parent.projectionSupport}) / "
                            +$"'{over.s.input.key}' face {over.f} (chart {detail.chart}, layer {detail.layer}, inherited {detail.inherited}, support {detail.projectionSupport}, rejection {detail.rejection}).");
                    report.overlaps.Add(new OverlapRelation {lod=under.s.lod,underMesh=under.s.node,underFace=under.f,overMesh=over.s.node,overFace=over.f});
                }
            }
        }

        static void ResolveOverlaps(Surface[] surfaces, Options options, CancellationToken token)
        {
            // Face indices can change during donor seam refinement. Rebuild all
            // same-level relationships from current faces, including untouched nodes.
            foreach (var surface in surfaces)
                foreach (var face in surface.faces)
                {
                    face.intentionalOverlap = false; face.overlayMesh = face.overlayFace = -1;
                    face.overlapParents = null;
                    // Arbitration is rebuilt after cuts and optional underlay
                    // rollbacks. Its previous rank is not a new donor layer.
                    if(face.projectionLayer<0) face.projectionLayer=face.layer;
                    face.layer=face.projectionLayer;
                }
            var refs=surfaces.SelectMany(s=>Enumerable.Range(0,s.faces.Length).Select(f=>(s,f))).ToArray();
            var conflicts = new List<int>[refs.Length];
            foreach(var part in CorrespondenceDomains(refs,true))
            {
                var scan=UvAtlasDiagnostics.Measure(part.geometry,token,comparisonBudget:options.comparisonBudget,collectConflicts:true);
                if(!scan.complete) throw new InvalidOperationException("Reverse UV overlap audit exceeded its budget; no atlas was published.");
                foreach(var (localA,localB) in scan.conflicts)
                {
                    int a=part.faces[localA],b=part.faces[localB];
                    if (conflicts[a] == null) conflicts[a] = new List<int>();
                    if (conflicts[b] == null) conflicts[b] = new List<int>();
                    conflicts[a].Add(b); conflicts[b].Add(a);
                }
            }
            var accepted = new bool[refs.Length];
            foreach (int i in Enumerable.Range(0, refs.Length).OrderBy(i => refs[i].s.faces[refs[i].f].layer)
                .ThenBy(i=>OverlapPriority(refs[i].s,refs[i].f,options))
                .ThenByDescending(i=>WasInherited(refs[i].s,refs[i].f,options))
                .ThenByDescending(i => refs[i].s.faces[refs[i].f].protectedInheritance)
                .ThenBy(i => refs[i].s.faces[refs[i].f].distance).ThenBy(i => i))
            {
                var (s, f) = refs[i]; var face = s.faces[f];
                if (!face.inherited && !face.projectionSupport) continue;
                if (conflicts[i] != null)
                    foreach (int other in conflicts[i])
                    {
                        if (!accepted[other]) continue;
                        var (parent, pf) = refs[other];
                        var parentFace = parent.faces[pf];
                        if (!options.preserveProjectedOverlap || face.chart != parentFace.chart || SharedCoplanarEdge(s, f, parent, pf))
                        { face.inherited = false; face.ambiguous = true; face.rejection = "overlap"; break; }
                        if (face.overlapParents == null) face.overlapParents = new HashSet<Face>();
                        face.overlapParents.Add(parentFace);
                        // Keep a representative parent for legacy readers, while
                        // the report records every audited edge of the ancestry graph.
                        if (face.overlayMesh < 0 || parentFace.layer > surfaces[face.overlayMesh].faces[face.overlayFace].layer)
                        { face.overlayMesh = parent.node; face.overlayFace = pf; }
                        face.intentionalOverlap = true;
                        face.layer = Math.Max(face.layer, parentFace.layer + 1);
                    }
                accepted[i] = face.inherited || face.projectionSupport;
            }
        }

        static int OverlapPriority(Surface surface,int face,Options options)
        {
            int source=surface.sourceFaces[face];
            if(surface.input.sourceFaces!=null) source=surface.input.sourceFaces[source];
            return options.overlapPriority!=null && options.overlapPriority.TryGetValue((surface.lod,surface.node,source),out int layer)?layer:0;
        }

        static bool WasInherited(Surface surface,int face,Options options)
        {
            int source=surface.sourceFaces[face];
            if(surface.input.sourceFaces!=null) source=surface.input.sourceFaces[source];
            return options.inheritedPriority!=null && options.inheritedPriority.Contains((surface.lod,surface.node,source));
        }

        internal static bool IsOrderedOverlap(Face a, Face b)
        {
            var under = a.layer < b.layer ? a : b;
            var over = a.layer < b.layer ? b : a;
            return (under.inherited || under.projectionSupport) && over.inherited && under.chart == over.chart
                && under.layer < over.layer && over.intentionalOverlap
                && over.overlapParents != null && over.overlapParents.Contains(under);
        }

        static bool SharedCoplanarEdge(Surface a, int fa, Surface b, int fb)
        {
            int shared = 0;
            for (int i = 0; i < 3; ++i)
                for (int j = 0; j < 3; ++j)
                    if (a.positions[a.indices[fa * 3 + i]].Equals(b.positions[b.indices[fb * 3 + j]])) { ++shared; break; }
            if (shared < 2) return false;
            var normal = Normal(a, fa); var origin = a.positions[a.indices[fa * 3]];
            double span = Enumerable.Range(0,3).Max(k => (a.positions[a.indices[fa*3+k]]-origin).magnitude);
            return Enumerable.Range(0,3).All(k => Math.Abs(Vector3.Dot(b.positions[b.indices[fb*3+k]]-origin,normal)) <= span*1e-6);
        }

        static RemeshNative.Geometry CorrespondenceGeometry((Surface s,int f)[] faces)
            => new RemeshNative.Geometry {
                uv=faces.SelectMany(p=>Enumerable.Range(p.f*3,3).Select(c=>p.s.pixels[c])).ToArray(),
                indices=Enumerable.Range(0,faces.Length*3).ToArray(),
                charts=faces.SelectMany(p=>Enumerable.Repeat(p.s.faces[p.f].chart,3)).ToArray()};

        // Independent repair frames may have numerically equal UVs. They only
        // compete for pixels after final packing; donor descendants in the same
        // frame must still pass the complete overlap/contact audit.
        static IEnumerable<(int[] faces,RemeshNative.Geometry geometry)> CorrespondenceDomains(
            (Surface s,int f)[] faces,bool inheritedOnly=false)
        {
            foreach(var group in Enumerable.Range(0,faces.Length)
                .Where(i=>!inheritedOnly || faces[i].s.faces[faces[i].f].inherited || faces[i].s.faces[faces[i].f].projectionSupport)
                .GroupBy(i=>faces[i].s.faces[faces[i].f].coordinateDomain))
            {
                var indices=group.ToArray();
                yield return (indices,CorrespondenceGeometry(indices.Select(i=>faces[i]).ToArray()));
            }
        }

        static RemeshNative.Geometry Atlas(Surface[] surfaces, bool inheritedOnly = false)
        {
            var pixels = new List<Vector2>(); var labels = new List<int>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                    for (int k = 0; k < 3; ++k)
                    {
                        pixels.Add(inheritedOnly && !s.faces[f].inherited && !s.faces[f].projectionSupport ? Vector2.zero : s.pixels[f * 3 + k]);
                        labels.Add(s.faces[f].chart);
                    }
            return new RemeshNative.Geometry { uv = pixels.ToArray(), charts = labels.ToArray(),
                indices = Enumerable.Range(0, pixels.Count).ToArray() };
        }

        static void AppendNew(Surface[] surfaces, List<Surface[]> previousLevels, float density, Options options, ref float width,
            ref float height, ref int nextChart, CancellationToken token)
        {
            const string coordinateFormat = "G9";
            var refs = new List<(Surface s, int f)>(); var positions = new List<Vector3>();
            foreach (var s in surfaces)
                for (int f = 0; f < s.faces.Length; ++f)
                    if (!s.faces[f].inherited)
                    {
                        refs.Add((s, f));
                        for (int k = 0; k < 3; ++k) positions.Add(s.positions[s.indices[f * 3 + k]]);
                    }
            if (refs.Count == 0) return;
            token.ThrowIfCancellationRequested();
            // Unsupported finer geometry retains its own authored seam
            // domains and receives the same local metric repair as the seed.
            // Do not create thousands of fresh Unity charts from fragments
            // left between inherited faces.
            var uv=new Vector2[positions.Count];
            for(int i=0;i<refs.Count;++i)
            {
                var (surface,face)=refs[i];var sourceUv=surface.sourceUvCorners;
                int source=surface.sourceFaces[face]*3;
                for(int k=0;k<3;++k)
                {
                    var bary=surface.sourceBarycentrics==null?(k==0?Vector3.right:k==1?Vector3.up:Vector3.forward)
                        :surface.sourceBarycentrics[face*3+k];
                    var p=sourceUv[source]*bary.x+sourceUv[source+1]*bary.y+sourceUv[source+2]*bary.z;
                    uv[i*3+k]=Finite(p.x)&&Finite(p.y)?p:Vector2.zero;
                }
            }
            var regions=ReverseUvSeed.AuthoredRegions(positions.ToArray(),uv,
                refs.SelectMany(p=>Enumerable.Repeat(p.s.node,3)).ToArray(),token);
            var strips=options.cutNarrowJunctions?CutNewStrips(ref refs,ref positions,ref uv,options,density,token):null;
            if(strips!=null)
            {
                regions=strips;
                // Subdivision copies inherited records too. Their old
                // intentional contact graph refers to replaced objects.
                ResolveOverlaps(surfaces,options,token);
            }
            var frames=refs.Select(p=>p.s.metricFrames[p.f]).ToArray();
            bool[] fallback;
            Vector2[] pixels;
            if(regions==null) pixels=ReverseUvNewCharts.Prepare(positions.ToArray(),uv,density,options,token,out fallback,frames);
            else
            {
                fallback=ReverseUvNewCharts.Classify(positions.ToArray(),uv,options,token,frames,regions);
                pixels=ReverseUvSeedPacking.Prepare(positions.ToArray(),uv,density,true,options,token,out _,regions:regions,frames:frames,
                    preparedFallback:repaired=>fallback=repaired);
            }
            var min = pixels[0]; var max = min;
            foreach (var p in pixels) { min = Vector2.Min(min, p); max = Vector2.Max(max, p); }
            var offset = new Vector2(width + options.padding, options.padding);
            var placed = options.fillAtlasVacancies
                ? ReverseUvAtlasPlacement.Place(positions.ToArray(), pixels, previousLevels, surfaces,
                    options, width, height, token, fallback, density,frames)
                : pixels.Select(p => p - min + offset).ToArray();
            ReverseUvNewCharts.Stabilize(positions.ToArray(), placed, fallback, density, options.maxAnisotropy, token: token,frames:frames);
            for (int i = 0; i < refs.Count; ++i)
            {
                var (s, f) = refs[i];
                var face = s.faces[f];
                face.parentLod = face.parentMesh = face.parentFace = -1;
                face.overlayMesh = face.overlayFace = -1; face.layer = face.projectionLayer = 0; face.intentionalOverlap = false;
                face.localFallback = fallback[i];
                for (int k = 0; k < 3; ++k) s.pixels[f * 3 + k] = placed[i * 3 + k];
                if (Anisotropy(s, f) > options.maxAnisotropy)
                    throw new InvalidOperationException($"New reverse UV '{s.input.key}' face {f} exceeds the stretch limit (anisotropy {Anisotropy(s,f):G5}, local fallback {face.localFallback}); atlas was not published. "
                        + $"Density {density:G9}; positions {s.positions[s.indices[f*3]].ToString(coordinateFormat)} / {s.positions[s.indices[f*3+1]].ToString(coordinateFormat)} / {s.positions[s.indices[f*3+2]].ToString(coordinateFormat)}; "
                        + $"pixels {placed[i*3].ToString(coordinateFormat)} / {placed[i*3+1].ToString(coordinateFormat)} / {placed[i*3+2].ToString(coordinateFormat)}.");
            }
            foreach(var s in surfaces)
                AssignCharts(s,Enumerable.Range(0,s.faces.Length).Where(f=>!s.faces[f].inherited).ToArray(),ref nextChart);
            var quality = UvAtlasDiagnostics.Measure(Atlas(surfaces), token, comparisonBudget: options.comparisonBudget, collectConflicts: true);
            if (!quality.complete || quality.degenerateFaces > 0 || quality.invalidFaces > 0
                || (!options.preserveProjectedOverlap && quality.pairs > 0))
                throw new InvalidOperationException("Expanded reverse UV atlas failed validation.");
            var faceRefs = new List<(Surface surface,int face)>();
            foreach (var surface in surfaces)
                for(int f=0;f<surface.faces.Length;++f) faceRefs.Add((surface,f));
            foreach (var (a, b) in quality.conflicts)
                if (!IsOrderedOverlap(faceRefs[a].surface.faces[faceRefs[a].face],faceRefs[b].surface.faces[faceRefs[b].face]))
                    throw new InvalidOperationException($"Expanded reverse UV atlas contains an unclassified overlap: LOD{surfaces[0].lod} "
                        +$"node {faceRefs[a].surface.node}/face {faceRefs[a].face} / node {faceRefs[b].surface.node}/face {faceRefs[b].face}.");
            foreach (var p in placed) { width = Math.Max(width, p.x + options.padding); height = Math.Max(height, p.y + options.padding); }
        }

        static void AssignCharts(Surface surface, int[] faces, ref int nextChart)
        {
            var union = new DisjointSet(faces.Length);
            var edges = new Dictionary<((Vector3, Vector2), (Vector3, Vector2)), int>();
            for (int i = 0; i < faces.Length; ++i)
                for (int k = 0; k < 3; ++k)
                {
                    int a = faces[i] * 3 + k, b = faces[i] * 3 + (k + 1) % 3;
                    var x = (surface.positions[surface.indices[a]], surface.pixels[a]);
                    var y = (surface.positions[surface.indices[b]], surface.pixels[b]);
                    if (edges.TryGetValue((x, y), out int other) || edges.TryGetValue((y, x), out other)) union.Union(i, other);
                    else edges.Add((x, y), i);
                }
            var ids = new Dictionary<int, int>();
            for (int i = 0; i < faces.Length; ++i)
            {
                int root = union.Find(i);
                if (!ids.TryGetValue(root, out int id)) { id = nextChart++; ids.Add(root, id); }
                surface.faces[faces[i]].chart = id;
            }
        }

        static Vector3 Normal(Surface s, int face)
        {
            int t = face * 3;
            return MeshGeometry.UnitDirection(Vector3.Cross(s.positions[s.indices[t + 1]] - s.positions[s.indices[t]],
                s.positions[s.indices[t + 2]] - s.positions[s.indices[t]]) * s.orientation);
        }

        static double Anisotropy(Surface s, int f, Matrix4x4? stretchFrame = null)
        {
            int t = f * 3;
            if((!stretchFrame.HasValue || stretchFrame.Value==Matrix4x4.identity) && s.metricFrames!=null)
                return TriangleAnisotropy(s.metricFrames[f],s.pixels[t],s.pixels[t+1],s.pixels[t+2]);
            return TriangleAnisotropy(s.positions[s.indices[t]], s.positions[s.indices[t+1]], s.positions[s.indices[t+2]],
                s.pixels[t], s.pixels[t+1], s.pixels[t+2], stretchFrame ?? Matrix4x4.identity);
        }

        internal static double TriangleAnisotropy(Vector3 p, Vector3 q, Vector3 r, Vector2 aUv, Vector2 bUv, Vector2 cUv)
            => TriangleAnisotropy(TriangleFrame(p, q, r), aUv, bUv, cUv);

        internal static double TriangleAnisotropy(Vector3 p, Vector3 q, Vector3 r, Vector2 aUv, Vector2 bUv, Vector2 cUv, Matrix4x4 frame)
        {
            double ex = (double)q.x - p.x, ey = (double)q.y - p.y, ez = (double)q.z - p.z;
            double dx = (double)r.x - p.x, dy = (double)r.y - p.y, dz = (double)r.z - p.z;
            var transformed = TriangleFrame(
                frame.m00 * ex + frame.m01 * ey + frame.m02 * ez,
                frame.m10 * ex + frame.m11 * ey + frame.m12 * ez,
                frame.m20 * ex + frame.m21 * ey + frame.m22 * ez,
                frame.m00 * dx + frame.m01 * dy + frame.m02 * dz,
                frame.m10 * dx + frame.m11 * dy + frame.m12 * dz,
                frame.m20 * dx + frame.m21 * dy + frame.m22 * dz);
            return TriangleAnisotropy(transformed, aUv, bUv, cUv);
        }

        internal static double TriangleAnisotropy((double length, double x, double y) frame, Vector2 aUv, Vector2 bUv, Vector2 cUv)
        {
            var (length, x, y) = frame;
            // Promote before subtraction: float edge/dot arithmetic introduces
            // order-dependent shear on nearly collinear captured triangles.
            double ux = (double)bUv.x - aUv.x, uy = (double)bUv.y - aUv.y;
            double vx = (double)cUv.x - aUv.x, vy = (double)cUv.y - aUv.y;
            double j00 = ux / length, j10 = uy / length, j01 = (vx - ux * x / length) / y, j11 = (vy - uy * x / length) / y;
            double a = j00 * j00 + j10 * j10, b = j01 * j01 + j11 * j11, c = j00 * j01 + j10 * j11;
            double max = (a + b + Math.Sqrt(Math.Max(0, (a - b) * (a - b) + 4 * c * c))) * .5;
            double determinant = j00 * j11 - j01 * j10;
            double min = max > 0 ? determinant * determinant / max : 0;
            double ratio = min > 0 ? Math.Sqrt(max / min) : double.PositiveInfinity;
            return double.IsNaN(ratio) ? double.PositiveInfinity : ratio;
        }

        internal static (double length, double x, double y) TriangleFrame(Vector3 p, Vector3 q, Vector3 r)
        {
            double ex = (double)q.x - p.x, ey = (double)q.y - p.y, ez = (double)q.z - p.z;
            double dx = (double)r.x - p.x, dy = (double)r.y - p.y, dz = (double)r.z - p.z;
            return TriangleFrame(ex, ey, ez, dx, dy, dz);
        }

        internal static (double length, double x, double y) TriangleFrame(double ex, double ey, double ez, double dx, double dy, double dz)
        {
            double length = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            double cx = ey * dz - ez * dy, cy = ez * dx - ex * dz, cz = ex * dy - ey * dx;
            return (length, (ex * dx + ey * dy + ez * dz) / length,
                Math.Sqrt(cx * cx + cy * cy + cz * cz) / length);
        }

        static double Cross(Vector2 a, Vector2 b) => (double)a.x * b.y - (double)a.y * b.x;
        static bool Finite(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
    }
}
