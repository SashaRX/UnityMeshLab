using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// The Remesh &amp; Bake stage machine: capture → voxel remesh → simplify → normals &amp; UV
    /// → bake, each stage re-runnable on its own with everything after it cleared.
    /// The work unit is a <see cref="Node"/>; the weld is one node in root space,
    /// keep-hierarchy is one node per renderer in its own space, and every stage runs
    /// the same loop over the list. Owns every Unity object it creates (preview meshes,
    /// result meshes, the base color preview) and destroys them on clear.
    /// </summary>
    internal sealed class RemeshPipeline : IDisposable
    {
        const string LogPrefix = "[Remesh] ";

        internal enum Stage { Remesh, Simplify, Unwrap, Bake }

        /// <summary>One captured piece of the source and its stage outputs.</summary>
        internal sealed class Node
        {
            public string name;
            // Placement relative to the source root; identity for the weld. The capture
            // space is root × TRS(these), so a sheared renderer matrix folds its residual
            // into the captured geometry instead of being dropped from the saved transform.
            public Vector3 localPosition = Vector3.zero;
            public Quaternion localRotation = Quaternion.identity;
            public Vector3 localScale = Vector3.one;
            public Matrix4x4 spaceToWorld = Matrix4x4.identity; // captured at remesh time
            public RemeshSource source;
            public RemeshNative.IndexedMesh voxel, simplified;
            // Trim mask: the untrimmed remesh and the class of each of its faces
            // (RemeshTrim.Kept/Back/Rim), for the Remesh stage's preview; null without a trim.
            public RemeshNative.IndexedMesh voxelRaw;
            public byte[] trimClasses;
            // The capture has faces whose material renders both sides: the result
            // material does too.
            public bool twoSided;
            public float simplifyError;
            public RemeshNative.Geometry geometry;
            public Vector4[] tangents;
            public Mesh mesh;      // the unwrapped result (HideAndDontSave), also the Result preview
            public RemeshBaker.Maps maps;
        }

        readonly List<Node> nodes = new List<Node>();
        readonly string[] keys = new string[4];
        CancellationTokenSource cancellation;
        int running;
        bool hierarchy;
        bool disposeRequested;   // Dispose() during a run: clear once the run has observed cancellation
        /// <summary>The root the remesh stage captured; the save resolves its output folder from this,
        /// not from whatever is selected at save time. Null until the stage ran.</summary>
        public GameObject CapturedSource { get; private set; }

        // Previews come from the primary node: the weld itself, or the largest node of
        // a hierarchy so the existing preview panel keeps working.
        Mesh sourceMesh, voxelMesh, simplifiedMesh, trimMaskMesh;
        Texture2D baseColorPreview;

        /// <summary>Settings snapshot every stage output was built with, indexed by Stage.</summary>
        public IReadOnlyList<string> Keys => keys;
        public IReadOnlyList<Node> Nodes => nodes;
        public bool IsHierarchy => hierarchy;
        public bool IsRunning => Volatile.Read(ref running) != 0;
        public bool CanCancel => cancellation != null && !cancellation.IsCancellationRequested;
        public string Status { get; private set; } = "Select a static model root. LODGroups contribute only LOD0.";
        public string ResultName { get; private set; }
        /// <summary>The source root's world scale at capture time; the save carries it.</summary>
        public Vector3 RootScale { get; private set; } = Vector3.one;
        public Action Changed;

        public Node Primary
        {
            get {
                Node best = null;
                foreach (var node in nodes)
                    if (best == null || (node.voxel != null && best.voxel != null && node.voxel.TriangleCount > best.voxel.TriangleCount)) best = node;
                return best;
            }
        }

        public Mesh SourceMesh => sourceMesh;
        public Mesh VoxelMesh => voxelMesh;
        /// <summary>The untrimmed remesh of the primary node with one colour per trim
        /// class (green kept, red back of a sheet, orange rim); null when nothing was trimmed.</summary>
        public Mesh TrimMaskMesh => trimMaskMesh;
        /// <summary>Any node's capture renders both sides (two-sided materials or the setting).</summary>
        public bool ResultTwoSided => nodes.Exists(n => n.twoSided);
        public Mesh SimplifiedMesh => simplifiedMesh;
        public Mesh ResultMesh => Primary?.mesh;
        public RemeshNative.Geometry Geometry => Primary?.geometry;
        public RemeshBaker.Maps Maps => Primary?.maps;
        public Texture2D BaseColorPreview => baseColorPreview;
        public float SourceDiagonal => Primary?.source != null ? Primary.source.diagonal : 0f;
        public RemeshSource Source => Primary?.source;
        public bool SourceHasColors => nodes.Exists(n => n.source != null && n.source.hasColors);

        // True only when EVERY node carries the stage's output: a keep-hierarchy run
        // fills the nodes one by one, and a repaint between two nodes must not read a
        // half-finished stage (Summary dereferences every node).
        public bool Has(Stage stage)
        {
            if (nodes.Count == 0) return false;
            foreach (var n in nodes) {
                bool done = stage == Stage.Remesh ? n.voxel != null
                    : stage == Stage.Simplify ? n.simplified != null
                    : stage == Stage.Unwrap ? n.geometry != null
                    : n.maps != null;
                if (!done) return false;
            }
            return true;
        }

        /// <summary>Short stage summary for the UI headers; null before the stage ran.</summary>
        public string Summary(Stage stage)
        {
            if (!Has(stage)) return null;
            long total = 0;
            switch (stage) {
                case Stage.Remesh:
                    foreach (var n in nodes) { total += n.voxel.TriangleCount; }
                    return $"{total:N0} tris";
                case Stage.Simplify:
                    foreach (var n in nodes) { total += n.simplified.TriangleCount; }
                    return $"{total:N0} tris";
                case Stage.Unwrap:
                    foreach (var n in nodes) { total += n.geometry.chartCount; }
                    return $"{total:N0} islands";
                default:
                    foreach (var n in nodes) { total += n.maps.misses; }
                    return total > 0 ? $"{total:N0} missed" : "done";
            }
        }

        /// <summary>The settings that feed each stage, so a change marks it stale.</summary>
        public static string Key(Stage stage, RemeshSettings s, GameObject source)
        {
            switch (stage) {
                case Stage.Remesh: return $"{(source ? source.GetInstanceID() : 0)}|{s.voxelResolution}|{s.solve}|{s.shell}|{s.lod0Only}|{s.keepHierarchy}|" +
                    $"{s.sourceShape}|{s.hullResolution}|{s.hullTriangles}|{s.minPartSize:F4}|{s.minRodVoxels:F3}|{s.voxelResolution}|{s.trimToSource}|{s.sourceBackfaces}";
                case Stage.Simplify: return $"{s.simplify}|{s.targetTriangles}|{s.maximumError}|{s.regularize}|{s.preserveFolds}|{s.pruneSmallParts}";
                case Stage.Unwrap: return $"{s.hardEdges}|{s.normalCrease}|{s.normalSmoothing}|{s.normalWeighting}|{s.textureResolution}|{s.padding}|{s.chartMaxCost}|" +
                    $"{s.chartNormalDeviation}|{s.chartNormalSeam}|{s.chartStraightness}|{s.chartRoundness}|{s.chartIterations}|" +
                    $"{s.maxChartArea}|{s.maxChartBoundary}|{s.packRotate}|{s.packBlockAlign}|{s.packBruteForce}";
                default: return $"{s.bakeMode}|{s.projectionDistance}|{s.cageSmoothing:F3}|{s.cageFit}|{s.bakeSamples}|{s.transferVertexColor}|{s.transferVertexAlpha}|{s.vertexColorTint}|{s.proxyDepth:F4}|{s.sourceBackfaces}";
            }
        }

        public bool IsStale(Stage stage, RemeshSettings settings, GameObject source)
            => keys[(int)stage] != null && keys[(int)stage] != Key(stage, settings, source);

        /// <summary>The first stage that must run so `target` is up to date: a missing
        /// output or changed settings. An emptied Source field does not invalidate a
        /// remesh already captured.</summary>
        public Stage FirstStale(Stage target, RemeshSettings settings, GameObject source)
        {
            for (var s = Stage.Remesh; s < target; ++s)
                if (keys[(int)s] == null || keys[(int)s] != Key(s, settings, source) && (s != Stage.Remesh || source)) return s;
            return target;
        }

        public void Cancel()
        {
            cancellation?.Cancel();
            Status = "Cancelling after the current phase…";
        }

        /// <summary>Runs stages from..to on a copy of the settings. Returns false when a
        /// run is already in progress. Errors land in Status and the log.</summary>
        public async Task<bool> Run(GameObject source, RemeshSettings live, Stage from, Stage to)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
            cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            bool locked = false;
            try {
                live.Validate(); RemeshNative.CheckAvailable();
                var options = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(live));
                ClearFrom(from);
                EditorApplication.LockReloadAssemblies(); locked = true;
                for (var stage = from; stage <= to; ++stage) {
                    string key = Key(stage, options, source);
                    switch (stage) {
                        case Stage.Remesh: await RunRemesh(source, options, token); break;
                        case Stage.Simplify: await RunSimplify(options, token); break;
                        case Stage.Unwrap: await RunUnwrap(options, token); break;
                        default: await RunBake(source, options, token); break;
                    }
                    keys[(int)stage] = key;
                }
                return true;
            }
            catch (OperationCanceledException) { Status = "Cancelled. Source assets were preserved."; return false; }
            catch (Exception e) { Status = e.Message; UvtLog.Error(LogPrefix + e); return false; }
            finally {
                cancellation.Dispose(); cancellation = null;
                if (locked) EditorApplication.UnlockReloadAssemblies();
                // A Dispose() that arrived mid-run waited for this point: the stage code
                // above never sees a cleared node list or a destroyed mesh.
                if (disposeRequested) { disposeRequested = false; ClearFrom(Stage.Remesh); }
                Interlocked.Exchange(ref running, 0); Changed?.Invoke();
            }
        }

        void Report(string message) { Status = message; Changed?.Invoke(); }

        // ── stages ──

        async Task RunRemesh(GameObject root, RemeshSettings options, CancellationToken token)
        {
            if (!root) throw new InvalidOperationException("Select a source root.");
            Report("Reading source geometry and textures…");
            await Task.Yield(); token.ThrowIfCancellationRequested();
            var renderers = RemeshSource.CollectRenderers(root, options.lod0Only);
            if (renderers.Count == 0) throw new InvalidOperationException("No remeshable renderers under the root.");
            hierarchy = options.keepHierarchy;
            ResultName = root.name;
            RootScale = root.transform.lossyScale;
            var rootToWorld = root.transform.localToWorldMatrix;
            var worldToRoot = root.transform.worldToLocalMatrix;
            var captures = new List<Node>();
            if (!hierarchy) {
                captures.Add(new Node { name = root.name, spaceToWorld = rootToWorld,
                    source = RemeshSource.Capture(worldToRoot, renderers) });
            }
            else {
                // One node per renderer, each in its own TRS space relative to the root;
                // children are their own nodes, so nothing is captured twice.
                for (int i = 0; i < renderers.Count; ++i) {
                    var renderer = renderers[i];
                    token.ThrowIfCancellationRequested();
                    Report($"Reading {renderer.name} ({i + 1}/{renderers.Count})…");
                    var toRoot = worldToRoot * renderer.transform.localToWorldMatrix;
                    var node = new Node { name = renderer.name, localPosition = toRoot.GetColumn(3),
                        localRotation = toRoot.rotation, localScale = toRoot.lossyScale };
                    node.spaceToWorld = rootToWorld * Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale);
                    node.source = RemeshSource.Capture(node.spaceToWorld.inverse, new[] { renderer }, required: false);
                    if (node.source == null) { UvtLog.Warn(LogPrefix + renderer.name + ": nothing to remesh, skipped."); continue; }
                    captures.Add(node);
                }
                if (captures.Count == 0) throw new InvalidOperationException("Every captured node was empty; nothing to remesh.");
            }
            long sourceTriangles = 0, resultTriangles = 0, trimmedFaces = 0, flippedFaces = 0, twoSidedFaces = 0; int warnings = 0, droppedSmall = 0, droppedThin = 0;
            var shape = options.sourceShape;
            // Part filter first, whatever the shape: small pieces and rods whose section
            // the voxel grid cannot carry (bolts, pipes, cables, railings) only add voxel
            // noise to a remesh and inflate a proxy's boxes or hull.
            for (int i = captures.Count - 1; i >= 0; --i) {
                var node = captures[i];
                int gridResolution = shape == RemeshShape.Hull ? options.hullResolution : options.voxelResolution;
                if (!node.source.FilterSmallParts(options.minPartSize, options.minRodVoxels, gridResolution, out int small, out int thin)) {
                    if (hierarchy) { UvtLog.Warn(LogPrefix + node.name + ": every part is below the size/thickness filter, skipped."); captures.RemoveAt(i); continue; }
                    UvtLog.Warn("[Remesh] Every part is below the size/thickness filter; the filter was not applied.");
                }
                droppedSmall += small; droppedThin += thin;
            }
            if (captures.Count == 0) throw new InvalidOperationException("Every node fell below the part filter; nothing to remesh.");
            for (int i = 0; i < captures.Count; ++i) {
                var node = captures[i];
                foreach (var warning in node.source.warnings) { UvtLog.Warn(LogPrefix + warning); ++warnings; }
                token.ThrowIfCancellationRequested();
                string verb = shape == RemeshShape.LOD0 ? "Voxel remeshing" : shape == RemeshShape.BoundingBox ? "Boxing" : "Hulling";
                Report(hierarchy ? $"{verb} {node.name} ({i + 1}/{captures.Count})…" : verb + "…");
                var captured = node.source;
                // The proxy shapes replace the voxelizer: one oriented box per renderer,
                // or a coarse voxel hull; materials and lighting still bake from the
                // captured geometry, projected onto the proxy downstream.
                var twoSidedMask = captured.TwoSidedFaces(options.sourceBackfaces);
                node.twoSided = twoSidedMask != null;
                if (twoSidedMask != null) foreach (bool two in twoSidedMask) if (two) ++twoSidedFaces;
                RemeshTrim.Result trim = null;
                node.voxel = await Task.Run(() => {
                    if (shape == RemeshShape.BoundingBox) return captured.OrientedBoxes();
                    if (shape == RemeshShape.Hull) return Hull(captured, options, token);
                    var voxel = RemeshNative.Voxelize(captured.positions, captured.indices, options, token);
                    if (!options.trimToSource || voxel == null) return voxel;
                    // The remesh surface sits within a cell of the source; two cells of reach
                    // keep a closed source whole and still find nothing behind an open sheet.
                    Vector3 mn = captured.positions[0], mx = captured.positions[0];
                    foreach (var p in captured.positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                    var extent = mx - mn;
                    float cell = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) / Mathf.Max(1, options.voxelResolution);
                    trim = RemeshTrim.Trim(voxel, captured.positions, captured.indices, cell * 2f, token);
                    node.voxelRaw = voxel; node.trimClasses = trim.classes;
                    return trim.mesh;
                }, token);
                if (node.voxel == null || node.voxel.TriangleCount == 0) throw new InvalidOperationException("The remesh produced no geometry.");
                if (trim != null) {
                    if (trim.gaveUp) UvtLog.Warn(LogPrefix + (hierarchy ? node.name + ": " : "") + "Trim to source surface kept under a tenth of the remesh " +
                        "with the source's winding and with its inverse, so nothing was trimmed. The source winding is mixed beyond one flip, or the remesh sits " +
                        "more than two cells from it (raise the voxel resolution).");
                    trimmedFaces += trim.removed; flippedFaces += trim.flipped;
                    if (UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag)) {
                        int back = 0, rim = 0;
                        foreach (byte c in trim.classes) { if (c == RemeshTrim.Back) ++back; else if (c == RemeshTrim.Rim) ++rim; }
                        UvtLog.Info(UvtLog.Category.RemeshDiag, "[" + node.name + "] trim: " + $"{trim.classes.Length:N0} remesh faces, kept {trim.classes.Length - trim.removed:N0}, " +
                            $"back of a sheet {back:N0}, rim/no source {rim:N0}, re-wound {trim.flipped:N0}{(trim.gaveUp ? ", GAVE UP (nothing trimmed)" : "")}; " +
                            $"two-sided source faces {(twoSidedMask == null ? 0 : CountTrue(twoSidedMask)):N0}");
                    }
                }
                sourceTriangles += captured.indices.Length / 3;
                resultTriangles += node.voxel.TriangleCount;
            }
            token.ThrowIfCancellationRequested();
            nodes.AddRange(captures);
            CapturedSource = root;
            var primary = Primary;
            sourceMesh = BuildMesh(ResultName + "_Source", primary.source.positions, primary.source.indices,
                primary.source.normals, primary.source.hasColors ? primary.source.colors : null);
            voxelMesh = BuildMesh(ResultName + "_Voxel", primary.voxel.positions, primary.voxel.indices);
            trimMaskMesh = primary.trimClasses != null ? BuildTrimMask(ResultName + "_TrimMask", primary.voxelRaw, primary.trimClasses) : null;
            Status = (hierarchy ? $"Remesh: {nodes.Count} node(s), " : "Remesh: ") +
                $"{sourceTriangles:N0} → {resultTriangles:N0} triangles" +
                (shape == RemeshShape.BoundingBox ? $" ({resultTriangles / 12:N0} oriented boxes)" : shape == RemeshShape.Hull ? " (hull)" : "") +
                (droppedSmall + droppedThin > 0 ? $"; excluded {droppedSmall:N0} small part(s), {droppedThin:N0} rod(s)" : "") +
                (trimmedFaces > 0 ? $"; trimmed {trimmedFaces:N0} face(s) the source has no surface for" : options.trimToSource && shape == RemeshShape.LOD0 ? "; nothing to trim" : "") +
                (flippedFaces > 0 ? $"; re-wound {flippedFaces:N0} face(s) to one orientation" : "") +
                (twoSidedFaces > 0 ? $"; {twoSidedFaces:N0} two-sided source face(s), the result renders both sides" : "") + "." +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "");
            UvtLog.Info(LogPrefix + Status);
        }

        static int CountTrue(bool[] mask)
        {
            int n = 0;
            foreach (bool b in mask) { if (b) ++n; }
            return n;
        }

        // The untrimmed remesh with one flat colour per face class, so the 3D view can
        // show what the trim removed and why before the simplifier touches it.
        static Mesh BuildTrimMask(string name, RemeshNative.IndexedMesh raw, byte[] classes)
        {
            int faces = classes.Length;
            var positions = new Vector3[faces * 3]; var colors = new Color[faces * 3]; var indices = new int[faces * 3];
            var kept = new Color(0.45f, 0.8f, 0.45f); var back = new Color(0.9f, 0.25f, 0.25f); var rim = new Color(1f, 0.6f, 0.15f);
            for (int f = 0; f < faces; ++f) {
                var color = classes[f] == RemeshTrim.Kept ? kept : classes[f] == RemeshTrim.Back ? back : rim;
                for (int k = 0; k < 3; ++k) {
                    positions[f * 3 + k] = raw.positions[raw.indices[f * 3 + k]];
                    colors[f * 3 + k] = color; indices[f * 3 + k] = f * 3 + k;
                }
            }
            return BuildMesh(name, positions, indices, null, colors);
        }

        // The Hull shape: a coarse voxelization without surface fitting (blocky by
        // design), then a strongly regularized simplification down to a small budget
        // with small-part pruning, so the result is a chunky silhouette that keeps L/T
        // footprints, courtyards and roof steps.
        static RemeshNative.IndexedMesh Hull(RemeshSource captured, RemeshSettings options, CancellationToken token)
        {
            var coarse = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(options));
            coarse.voxelResolution = options.hullResolution; coarse.solve = false; coarse.shell = false;
            var voxel = RemeshNative.Voxelize(captured.positions, captured.indices, coarse, token);
            coarse.targetTriangles = options.hullTriangles; coarse.maximumError = 0.5f;
            coarse.regularize = RemeshRegularize.Strong; coarse.preserveFolds = false; coarse.pruneSmallParts = true;
            return RemeshNative.Simplify(voxel, coarse, token, out _);
        }

        async Task RunSimplify(RemeshSettings options, CancellationToken token)
        {
            if (!Has(Stage.Remesh)) throw new InvalidOperationException("Run the remesh stage first.");
            long before = 0, after = 0;
            for (int i = 0; i < nodes.Count; ++i) {
                var node = nodes[i];
                token.ThrowIfCancellationRequested();
                Report(hierarchy ? $"Simplifying {node.name} ({i + 1}/{nodes.Count})…" : "Simplifying…");
                if (options.simplify) {
                    float error = 0;
                    var input = node.voxel;
                    node.simplified = await Task.Run(() => RemeshNative.Simplify(input, options, token, out error), token);
                    node.simplifyError = error;
                }
                else { node.simplified = node.voxel; node.simplifyError = 0; }
                before += node.voxel.TriangleCount; after += node.simplified.TriangleCount;
            }
            token.ThrowIfCancellationRequested();
            var primary = Primary;
            simplifiedMesh = BuildMesh(ResultName + "_Simplified", primary.simplified.positions, primary.simplified.indices);
            Status = (hierarchy ? $"Simplify: {nodes.Count} node(s), " : "Simplify: ") + $"{before:N0} → {after:N0} triangles.";
        }

        public float SimplifyError => Primary?.simplifyError ?? 0f;

        async Task RunUnwrap(RemeshSettings options, CancellationToken token)
        {
            if (!Has(Stage.Simplify)) throw new InvalidOperationException("Run the simplify stage first.");
            long charts = 0, vertices = 0;
            for (int i = 0; i < nodes.Count; ++i) {
                var node = nodes[i];
                token.ThrowIfCancellationRequested();
                Report(hierarchy ? $"Generating normals and unwrapping UVs on {node.name} ({i + 1}/{nodes.Count})…" : "Generating normals and unwrapping UVs…");
                var input = node.simplified;
                node.geometry = await Task.Run(() => RemeshNative.Unwrap(input, options, token), token);
                if (node.mesh) Object.DestroyImmediate(node.mesh);
                node.mesh = BuildResultMesh(node.name + "_LOD0", node.geometry, out node.tangents);
                charts += node.geometry.chartCount; vertices += node.geometry.positions.Length;
            }
            token.ThrowIfCancellationRequested();
            Status = (hierarchy ? $"Unwrap: {nodes.Count} node(s), " : "Unwrap: ") + $"{charts:N0} islands, {vertices:N0} vertices.";
        }

        async Task RunBake(GameObject root, RemeshSettings options, CancellationToken token)
        {
            if (!Has(Stage.Unwrap)) throw new InvalidOperationException("Run the UV stage first.");
            // The beauty environment reads scene state (lights, probes, view), so it is
            // captured once on the main thread before the workers start and re-based
            // into each node's capture space.
            RemeshBeauty beauty = null;
            if (options.bakeMode == RemeshBakeMode.Beauty) {
                Report("Capturing scene lighting…");
                if (!root) throw new InvalidOperationException("The source root is gone; reselect it and rerun the remesh stage.");
                beauty = new RemeshBeauty(root, SourceDiagonal);
                if (!string.IsNullOrEmpty(beauty.occluderSummary)) UvtLog.Info("[Remesh] Beauty shadows also test " + beauty.occluderSummary + ".");
            }
            long sourceTriangles = 0, targetTriangles = 0, misses = 0, covered = 0, empty = 0; int warnings = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < nodes.Count; ++i) {
                var node = nodes[i];
                token.ThrowIfCancellationRequested();
                Report(hierarchy ? $"Projecting {node.name} into its UV atlas ({i + 1}/{nodes.Count})…"
                    : beauty != null ? "Baking the lit view of the source into the atlas…" : "Projecting source materials into the new UV atlas…");
                var nodeBeauty = beauty?.ForSpace(node.spaceToWorld, node.source.diagonal);
                var source = node.source; var target = node.geometry; var frame = node.tangents;
                // The lightmap regions this node's faces use are read now, on the main
                // thread, and dropped again after the bake — they are float readbacks
                // and only Beauty needs them.
                if (nodeBeauty != null) { Report($"Reading lightmaps for {node.name}…"); source.ReadLightmaps(); }
                try {
                    // GPU: the band loop is driven from here (main thread) so the compute
                    // dispatches are legal; CPU: the whole bake on a worker.
                    node.maps = options.gpuProjection && GpuBvh.Supported
                        ? await RemeshBaker.BakeAsync(source, target, frame, options, token, nodeBeauty, RemeshBaker.CreateGpu)
                        : await Task.Run(() => RemeshBaker.Bake(source, target, frame, options, token, nodeBeauty), token);
                }
                finally { source.ReleaseLightmaps(); }
                // A re-bake without the transfer must not keep the previous colors.
                VertexChannels.SetColors(node.mesh, node.maps.vertexColors);
                sourceTriangles += source.indices.Length / 3; targetTriangles += target.indices.Length / 3;
                misses += node.maps.misses; covered += node.maps.covered; empty += node.maps.empty; warnings += source.warnings.Length;
                LogDiagnostics(node, options.sourceShape);
            }
            token.ThrowIfCancellationRequested();
            var primary = Primary;
            baseColorPreview = TextureAssets.FromPixels(primary.maps.color, primary.maps.size, primary.maps.size, linear: false);
            Status = (hierarchy ? $"{nodes.Count} node(s): " : "") + $"{sourceTriangles:N0} → {targetTriangles:N0} triangles. " +
                (misses == 0 ? "All covered texels projected." : $"{misses:N0} / {covered:N0} texels missed (magenta). Increase projection distance and rebake.") +
                (empty > 0 ? $" {empty:N0} proxy texels see no geometry (alpha 0, filled from neighbours)." : "") +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "") +
                $" Bake {clock.Elapsed.TotalSeconds:F1} s ({(primary.maps.gpu ? "GPU" : "CPU")} queries).";
            UvtLog.Info(LogPrefix + Status);
        }

        // Bake health (RemeshDiag log category): BakeHealth judges the counters the bake
        // collected and the source/target scale; the pipeline only hands it the node.
        static void LogDiagnostics(Node node, RemeshShape shape)
        {
            BakeHealth.Log(BakeHealth.Build(node.name, node.maps, node.source.diagonal, node.source.indices.Length / 3,
                node.geometry.positions, node.geometry.indices.Length / 3, shape));
        }

        // ── meshes ──

        // Builds the result Mesh from unwrapped geometry. The tangent frame comes from
        // meshoptimizer over the final UV layout (MikkT-compatible, the same basis the
        // bake encodes against); Unity's recalculation is only a fallback if the native
        // tangents are missing.
        internal static Mesh BuildResultMesh(string name, RemeshNative.Geometry unwrapped, out Vector4[] tangents)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = unwrapped.positions; mesh.normals = unwrapped.normals;
            mesh.uv = unwrapped.uv; mesh.triangles = unwrapped.indices;
            mesh.RecalculateBounds();
            bool anyTangent = false;
            if (unwrapped.tangents != null)
                for (int i = 0; i < unwrapped.tangents.Length && !anyTangent; ++i)
                    anyTangent = unwrapped.tangents[i].sqrMagnitude > 1e-12f;
            if (anyTangent) mesh.tangents = unwrapped.tangents;
            else {
                mesh.RecalculateTangents();
                UvtLog.Warn("[Remesh] Native tangents missing; fell back to Unity's recalculation. Rebuild the native plugins for this commit.");
            }
            tangents = mesh.tangents;
            return mesh;
        }

        static Mesh BuildMesh(string name, Vector3[] positions, int[] indices, Vector3[] normals = null, Color[] colors = null)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = positions; mesh.triangles = indices;
            if (normals != null) mesh.normals = normals; else mesh.RecalculateNormals();
            if (colors != null) mesh.colors = colors;
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>Drops the output of `stage` and every stage after it.</summary>
        public void ClearFrom(Stage stage)
        {
            if (stage <= Stage.Bake) {
                if (baseColorPreview) Object.DestroyImmediate(baseColorPreview);
                baseColorPreview = null; keys[(int)Stage.Bake] = null;
                foreach (var node in nodes) {
                    node.maps = null;
                    if (node.mesh) VertexChannels.SetColors(node.mesh, null);
                }
            }
            if (stage <= Stage.Unwrap) {
                keys[(int)Stage.Unwrap] = null;
                foreach (var node in nodes) {
                    if (node.mesh) Object.DestroyImmediate(node.mesh);
                    node.mesh = null; node.geometry = null; node.tangents = null;
                }
            }
            if (stage <= Stage.Simplify) {
                if (simplifiedMesh) Object.DestroyImmediate(simplifiedMesh);
                simplifiedMesh = null; keys[(int)Stage.Simplify] = null;
                foreach (var node in nodes) { node.simplified = null; node.simplifyError = 0; }
            }
            if (stage <= Stage.Remesh) {
                if (voxelMesh) Object.DestroyImmediate(voxelMesh);
                if (sourceMesh) Object.DestroyImmediate(sourceMesh);
                if (trimMaskMesh) Object.DestroyImmediate(trimMaskMesh);
                voxelMesh = null; sourceMesh = null; trimMaskMesh = null; keys[(int)Stage.Remesh] = null;
                nodes.Clear(); hierarchy = false; CapturedSource = null;
            }
        }

        /// <summary>Cancels a running stage and drops every output. Cancellation is
        /// cooperative, so during a run the clear is deferred to the run's exit instead of
        /// racing the stage that is still executing.</summary>
        public void Dispose()
        {
            if (IsRunning) { disposeRequested = true; cancellation?.Cancel(); return; }
            ClearFrom(Stage.Remesh);
        }
    }
}
