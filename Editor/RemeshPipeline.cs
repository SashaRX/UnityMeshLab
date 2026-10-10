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

        // Existing numeric values remain stable for callers and window preferences.
        internal enum Stage { Prepare = -1, Remesh, Simplify, Unwrap, Bake }

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
            internal RemeshSource unfilteredSource;
            internal RemeshPlanarCap.Support support;
            internal int[] voxelSyntheticFaces;
            internal RemeshSyntheticFaces.Regions syntheticRegions;
            public RemeshNative.IndexedMesh voxel, simplified;
            internal RemeshNative.IndexedMesh completeSimplified;
            internal int[] completeSyntheticFaces;
            internal TriangleBvh syntheticPickBvh;
            internal int[] syntheticFaces;
            internal bool[] selectedSyntheticFaces;
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
        string preparationKey;
        CancellationTokenSource cancellation;
        int running;
        bool hierarchy;
        Node previewNode;
        RemeshSource unfilteredRoot;
        Matrix4x4 capturedRootToWorld;
        bool disposeRequested;   // Dispose() during a run: clear once the run has observed cancellation
        /// <summary>The root the remesh stage captured; the save resolves its output folder from this,
        /// not from whatever is selected at save time. Null until the stage ran.</summary>
        public GameObject CapturedSource { get; private set; }

        // Previews come from the primary node: the weld itself, or the largest node of
        // a hierarchy so the existing preview panel keeps working.
        Mesh sourceMesh, closureMesh, closureRims, voxelMesh, simplifiedMesh, trimMaskMesh, syntheticMaskMesh;
        Texture2D baseColorPreview;

        /// <summary>Settings snapshots for the four existing native/bake stages; preparation has its own key.</summary>
        public IReadOnlyList<string> Keys => keys;
        public IReadOnlyList<Node> Nodes => nodes;
        public bool IsHierarchy => hierarchy;
        public bool IsRunning => Volatile.Read(ref running) != 0;
        public Stage? RunningStage { get; private set; }
        public bool CanCancel => cancellation != null && !cancellation.IsCancellationRequested;
        public string Status { get; private set; } = "Select a static model root. LODGroups contribute only LOD0.";
        public string ResultName { get; private set; }
        /// <summary>The source root's world scale at capture time; the save carries it.</summary>
        public Vector3 RootScale { get; private set; } = Vector3.one;
        /// <summary>The source root's world rotation at capture time; the save carries it.</summary>
        public Quaternion RootRotation { get; private set; } = Quaternion.identity;
        Matrix4x4 previewWorldToFrame = Matrix4x4.identity;
        internal Matrix4x4 PreviewSpaceToWorld => Primary is Node node ? previewWorldToFrame * node.spaceToWorld : Matrix4x4.identity;
        // Remove the scene root's position/rotation from display only. Scale and
        // child transforms still match capture space; export uses the original TRS.
        internal static Matrix4x4 PreviewRootFrameInverse(Transform root) =>
            Matrix4x4.TRS(root.position, root.rotation, Vector3.one).inverse;
        public Action Changed;

        public Node Primary
        {
            get {
                if (previewNode != null) return previewNode;
                Node best = null;
                foreach (var node in nodes)
                    if (best == null || (node.voxel != null && best.voxel != null && node.voxel.TriangleCount > best.voxel.TriangleCount)) best = node;
                return best;
            }
        }

        public Mesh SourceMesh => sourceMesh;
        internal Mesh ClosureMesh => closureMesh;
        internal Mesh ClosureRims => closureRims;
        internal string[] ClosureContourNames { get; private set; }
        internal Vector3[][] ClosureContourEdges { get; private set; }
        internal Color[] ClosureContourColors { get; private set; }
        internal string[] ClosureContourReasons { get; private set; }
        internal string ClosureSummary { get; private set; }
        internal string ClosureSelectionWarning { get; private set; }
        internal string ClosureLoopRanges { get; private set; }
        internal string[] ClosureContourInfo { get; private set; }
        public Mesh VoxelMesh => voxelMesh;
        /// <summary>The untrimmed remesh of the primary node with one colour per trim
        /// class (green kept, red back of a sheet, orange rim); null when nothing was trimmed.</summary>
        public Mesh TrimMaskMesh => trimMaskMesh;
        internal Mesh SyntheticMaskMesh => syntheticMaskMesh;
        /// <summary>Any node's capture renders both sides (two-sided materials or the setting).</summary>
        public bool ResultTwoSided => nodes.Exists(n => n.twoSided);
        public Mesh SimplifiedMesh => simplifiedMesh;
        public Mesh ResultMesh => Primary?.mesh;
        public RemeshNative.Geometry Geometry => Primary?.geometry;
        public RemeshBaker.Maps Maps => Primary?.maps;
        public Texture2D BaseColorPreview => baseColorPreview;
        public float SourceDiagonal => Primary?.source != null ? Primary.source.diagonal : 0f;
        public RemeshSource Source => Primary?.source;
        internal RemeshSource ProjectionSource(Node node, bool includeFiltered)
        {
            if (!includeFiltered) return node.source;
            if (node.unfilteredSource == null && unfilteredRoot != null) {
                var donor = unfilteredRoot.InSpace(node.spaceToWorld.inverse * capturedRootToWorld, node.source.diagonal);
                // Only the primary preview retains a rebased full-root snapshot.
                // Keeping one for every node would multiply memory by node count.
                if (node == Primary) node.unfilteredSource = donor;
                return donor;
            }
            return node.unfilteredSource ?? node.source;
        }
        public bool SourceHasColors => nodes.Exists(n => n.source != null && n.source.hasColors);

        // True only when EVERY node carries the stage's output: a keep-hierarchy run
        // fills the nodes one by one, and a repaint between two nodes must not read a
        // half-finished stage (Summary dereferences every node).
        public bool Has(Stage stage)
        {
            if (nodes.Count == 0) return false;
            foreach (var n in nodes) {
                bool done = stage == Stage.Prepare ? preparationKey != null
                    : stage == Stage.Remesh ? n.voxel != null
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
                case Stage.Prepare: return ClosureSummary;
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
                case Stage.Prepare: return $"{(source ? source.GetInstanceID() : 0)}|{s.lod0Only}|{s.keepHierarchy}|{s.sourceShape}|{s.shell}|" +
                    $"{s.minPartSize:R}|{s.minRodVoxels:R}|{s.voxelResolution}|{s.hullResolution}|{s.sourceBackfaces}|" +
                    $"cap{RemeshPlanarCap.Revision}|{s.planarCap}|{s.planarCapLoops}|{s.planarCapLocalPlanes}|{s.closureMode}|{s.capPlaneTolerance:R}";
                case Stage.Remesh: return $"{(source ? source.GetInstanceID() : 0)}|{s.voxelResolution}|{s.solve}|{s.shell}|{s.lod0Only}|{s.keepHierarchy}|" +
                    $"{s.sourceShape}|{s.hullResolution}|{s.hullTriangles}|{s.minPartSize:R}|{s.minRodVoxels:R}|{s.voxelResolution}|{s.trimToSource}|{s.sourceBackfaces}|" +
                    $"cap{RemeshPlanarCap.Revision}|{s.planarCap}|{s.planarCapLoops}|{s.planarCapLocalPlanes}|{s.closureMode}|{s.capPlaneTolerance:R}";
                case Stage.Simplify: return $"{s.simplify}|{s.targetTriangles}|{s.maximumError}|{s.regularize}|{s.preserveFolds}|{s.pruneSmallParts}";
                case Stage.Unwrap: return $"{s.hardEdges}|{s.normalCrease}|{s.normalSmoothing}|{s.normalWeighting}|{s.textureResolution}|{s.padding}|{s.chartMaxCost}|" +
                    $"{s.chartNormalDeviation}|{s.chartNormalSeam}|{s.chartStraightness}|{s.chartRoundness}|{s.chartIterations}|" +
                    $"{s.maxChartArea}|{s.maxChartBoundary}|{s.packRotate}|{s.packBlockAlign}|{s.packBruteForce}|{s.reduceUvFragmentation}|{s.mergeCharts}";
                default: return $"{s.bakeMode}|{s.bakeFilteredParts}|{s.projectionDistance}|{s.cageSmoothing:F3}|{s.cageFit}|{s.bakeSamples}|{s.transferVertexColor}|{s.transferVertexAlpha}|{s.vertexColorTint}|{s.proxyDepth:F4}|{s.sourceBackfaces}|{s.bakeSourceAO}|{s.multiplySourceAO}|{s.sourceAO?.Key}|{s.dilationRadius}";
            }
        }

        public bool IsStale(Stage stage, RemeshSettings settings, GameObject source)
            => StageKey(stage) != null && StageKey(stage) != Key(stage, settings, source ? source : CapturedSource);

        string StageKey(Stage stage) => stage == Stage.Prepare ? preparationKey : keys[(int)stage];

        /// <summary>The first stage that must run so `target` is up to date: a missing
        /// output or changed settings. An emptied Source field does not invalidate a
        /// remesh already captured.</summary>
        public Stage FirstStale(Stage target, RemeshSettings settings, GameObject source)
        {
            for (var s = Stage.Prepare; s < target; ++s)
                if (StageKey(s) == null || StageKey(s) != Key(s, settings, source ? source : CapturedSource)) return s;
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
                live.Validate();
                if (to >= Stage.Remesh) RemeshNative.CheckAvailable();
                var options = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(live));
                if (from == Stage.Remesh && (!Has(Stage.Prepare) || IsStale(Stage.Prepare, options, source))) from = Stage.Prepare;
                ClearFrom(from);
                EditorApplication.LockReloadAssemblies(); locked = true;
                for (var stage = from; stage <= to; ++stage) {
                    RunningStage = stage; Changed?.Invoke();
                    string key = Key(stage, options, source ? source : CapturedSource);
                    switch (stage) {
                        case Stage.Prepare: await RunPreparation(source, options, token); break;
                        case Stage.Remesh: await RunRemesh(options, token); break;
                        case Stage.Simplify: await RunSimplify(options, token); break;
                        case Stage.Unwrap: await RunUnwrap(options, token); break;
                        default: await RunBake(source, options, token); break;
                    }
                    if (stage == Stage.Prepare) preparationKey = key; else keys[(int)stage] = key;
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
                if (disposeRequested) { disposeRequested = false; ClearFrom(Stage.Prepare); }
                RunningStage = null;
                Interlocked.Exchange(ref running, 0); Changed?.Invoke();
            }
        }

        void Report(string message) { Status = message; Changed?.Invoke(); }

        // ── stages ──

        async Task RunPreparation(GameObject root, RemeshSettings options, CancellationToken token)
        {
            if (!root) throw new InvalidOperationException("Select a source root.");
            Report("Reading source geometry and textures…");
            await Task.Yield(); token.ThrowIfCancellationRequested();
            var renderers = RemeshSource.CollectRenderers(root, options.lod0Only);
            if (renderers.Count == 0) throw new InvalidOperationException("No remeshable renderers under the root.");
            hierarchy = options.keepHierarchy;
            ResultName = root.name;
            RootScale = root.transform.lossyScale;
            RootRotation = root.transform.rotation;
            previewWorldToFrame = PreviewRootFrameInverse(root.transform);
            var rootToWorld = root.transform.localToWorldMatrix;
            capturedRootToWorld = rootToWorld;
            var worldToRoot = root.transform.worldToLocalMatrix;
            var captures = new List<Node>();
            if (!hierarchy) {
                captures.Add(new Node { name = root.name, spaceToWorld = rootToWorld,
                    source = RemeshSource.Capture(worldToRoot, renderers, asyncTextures: true) });
            }
            else {
                // One node per renderer, each in its own TRS space relative to the root;
                // children are their own nodes, so nothing is captured twice.
                for (int i = 0; i < renderers.Count; ++i) {
                    await Task.Yield();
                    var renderer = renderers[i];
                    token.ThrowIfCancellationRequested();
                    Report($"Reading {renderer.name} ({i + 1}/{renderers.Count})…");
                    var toRoot = worldToRoot * renderer.transform.localToWorldMatrix;
                    var node = new Node { name = renderer.name, localPosition = toRoot.GetColumn(3),
                        localRotation = toRoot.rotation, localScale = toRoot.lossyScale };
                    node.spaceToWorld = rootToWorld * Matrix4x4.TRS(node.localPosition, node.localRotation, node.localScale);
                    node.source = RemeshSource.Capture(node.spaceToWorld.inverse, new[] { renderer }, required: false, asyncTextures: true);
                    if (node.source == null) { UvtLog.Warn(LogPrefix + renderer.name + ": nothing to remesh, skipped."); continue; }
                    captures.Add(node);
                }
                if (captures.Count == 0) throw new InvalidOperationException("Every captured node was empty; nothing to remesh.");
            }
            int droppedSmall = 0, droppedThin = 0;
            Report("Reading source textures…");
            // Drain submitted GPU reads before cancellation releases the capture.
            // The editor keeps ticking instead of blocking in Texture2D.ReadPixels.
            foreach (var node in captures) await node.source.TextureReadbacks;
            token.ThrowIfCancellationRequested();
            if (hierarchy) {
                // Retain fully removed renderers too, without reading textures twice.
                var parts = captures.ConvertAll(node => (node.source, worldToRoot * node.spaceToWorld));
                var groundNormal = worldToRoot.inverse.transpose.MultiplyVector(Vector3.up).normalized;
                unfilteredRoot = await Task.Run(() => RemeshSource.Combine(parts, groundNormal), token);
                token.ThrowIfCancellationRequested();
            }
            var shape = options.sourceShape;
            // Part filter first, whatever the shape: small pieces and rods whose section
            // the voxel grid cannot carry (bolts, pipes, cables, railings) only add voxel
            // noise to a remesh and inflate a proxy's boxes or hull.
            for (int i = captures.Count - 1; i >= 0; --i) {
                var node = captures[i];
                if (!hierarchy) node.unfilteredSource = node.source;
                node.source = node.source.CopyForFiltering();
                int gridResolution = shape == RemeshShape.Hull ? options.hullResolution : options.voxelResolution;
                Report($"Filtering parts of {node.name}…");
                var filtered = await Task.Run(() => {
                    token.ThrowIfCancellationRequested();
                    bool keep = node.source.FilterSmallParts(options.minPartSize, options.minRodVoxels, gridResolution, out int small, out int thin);
                    token.ThrowIfCancellationRequested();
                    return (keep, small, thin);
                }, token);
                if (!filtered.keep) {
                    if (hierarchy) { UvtLog.Warn(LogPrefix + node.name + ": every part is below the size/thickness filter, skipped."); captures.RemoveAt(i); continue; }
                    UvtLog.Warn("[Remesh] Every part is below the size/thickness filter; the filter was not applied.");
                }
                droppedSmall += filtered.small; droppedThin += filtered.thin;
            }
            if (captures.Count == 0) throw new InvalidOperationException("Every node fell below the part filter; nothing to remesh.");
            // Build every selected closure before publishing this immutable working snapshot.
            // Remaining unselected boundaries are allowed here; solid Remesh checks them later.
            IProgress<string> progress = new Progress<string>(message => {
                if (RunningStage == Stage.Prepare && preparationKey == null && !token.IsCancellationRequested && !disposeRequested) Report(message);
            });
            for (int i = 0; i < captures.Count; ++i) {
                var node = captures[i]; var captured = node.source;
                foreach (var warning in captured.warnings) UvtLog.Warn(LogPrefix + warning);
                node.twoSided = captured.TwoSidedFaces(options.sourceBackfaces) != null;
                Report($"Preparing closure {node.name} ({i + 1}/{captures.Count})…");
                await Task.Run(() => {
                    if (options.planarCap && shape != RemeshShape.BoundingBox && (shape == RemeshShape.Hull || !options.shell)) {
                        var capOwners = captured.FaceOwners();
                        try {
                            node.support = RemeshPlanarCap.Prepare(captured.positions, captured.indices, options.planarCapLoops, token, options.planarCapLocalPlanes, options.closureMode, options.capPlaneTolerance, capOwners,
                                (loop, done, total) => progress.Report($"{node.name}: processed loop {loop} ({done}/{total})"), continueOnRefusal: true, elementScopedContacts: true);
                            string closureLabel = options.closureMode == RemeshClosureMode.SurfaceCaps ? "surface Cap " :
                                options.closureMode == RemeshClosureMode.Bridge ? "Bridge " :
                                options.closureMode == RemeshClosureMode.Automatic ? "automatic Cap/Bridge " : "planar Cap ";
                            UvtLog.Info(LogPrefix + node.name + ": " + closureLabel + node.support.Description);
                            if (node.support.selectionWarning != null)
                                UvtLog.Warn(LogPrefix + node.name + ": " + node.support.selectionWarning);
                            foreach (var failure in node.support.loopFailures)
                                UvtLog.Warn(LogPrefix + node.name + $": closure loop {failure.Key} left open: {failure.Value}");
                            foreach (var pair in node.support.bridgePartners)
                                if (pair.Key < pair.Value) UvtLog.Info(LogPrefix + node.name + $": Bridge loops {pair.Key},{pair.Value}: " + node.support.bridgeSearch[pair.Key]);
                            var contacts = node.support.externalContacts;
                            if (contacts != null && contacts.count > 0)
                                UvtLog.Warn(LogPrefix + node.name + $": Cap/Bridge has {contacts.count} contacts with other source meshes " +
                                    $"(first added face {contacts.firstNewFace}, source face {contacts.firstSourceFace}). Prepared for inspection.");
                            RemeshGeometryDiagnostics.CaptureSupport(captured, node.support, options, node.name);

                        }
                        catch (InvalidOperationException failure) {
                            RemeshGeometryDiagnostics.CaptureFailure(captured.positions, captured.indices, null, null, options,
                                "Cap preparation", node.name, failure.Message, 0, 0, 0, node.support, capOwners);
                            throw;
                        }
                    }
                }, token);
            }
            token.ThrowIfCancellationRequested();
            nodes.AddRange(captures); CapturedSource = root;
            foreach (var node in nodes)
                if (previewNode == null || node.source.indices.Length > previewNode.source.indices.Length) previewNode = node;
            BuildSourcePreview(); BuildClosurePreview();
            ClosureSummary = $"{nodes.Count} node(s); " + ClosureCounts() +
                (droppedSmall + droppedThin > 0 ? $"; filtered {droppedSmall} small, {droppedThin} thin parts" : "");
            Report("Prepared: " + ClosureSummary + ". Inspect Cap / Bridge before Remesh.");
        }

        async Task RunRemesh(RemeshSettings options, CancellationToken token)
        {
            if (nodes.Count == 0) throw new InvalidOperationException("Prepare the source first.");
            var captures = nodes;
            var shape = options.sourceShape;
            long sourceTriangles = 0, resultTriangles = 0, trimmedFaces = 0, flippedFaces = 0, twoSidedFaces = 0;
            int warnings = 0;
            for (int i = 0; i < captures.Count; ++i) {
                var node = captures[i];
                warnings += node.source.warnings.Length;
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
                    if (node.support != null) {
                        try {
                            var closed = RemeshTopology.ClosedVolumeFaces(node.support.positions, node.support.indices, token);
                            foreach (bool face in closed) {
                                if (!face) {
                                    var remaining = RemeshTopology.Inspect(node.support.positions, node.support.indices, token);
                                    if (remaining.boundary.Count == 0)
                                        throw new InvalidOperationException("Cap support has no boundary edges but includes a zero-volume component. Inspect its geometry before solid Remesh.");
                                    throw new InvalidOperationException($"Cap support still has {remaining.boundary.Count} boundary edges " +
                                        $"after selection '{options.planarCapLoops}' from {node.support.loops} loops. " +
                                        (node.support.selectionWarning != null ? "Correct the loop selection using the available contour numbers in Cap / Bridge; accepted closures are retained." :
                                        node.support.loopFailures.Count > 0 ? "Inspect red refused contours and their reasons in Cap / Bridge; accepted closures are retained." :
                                            "Choose All boundaries to close the remaining openings, or an explicit Bridge selection."));
                                }
                            }
                        }
                        catch (InvalidOperationException failure) {
                            RemeshGeometryDiagnostics.CaptureFailure(captured.positions, captured.indices, null, null, options,
                                "Prepared solid input", node.name, failure.Message, 0, 0, 0, node.support, captured.FaceOwners());
                            throw;
                        }
                    }
                    var supportPositions = node.support?.positions ?? captured.positions;
                    var supportIndices = node.support?.indices ?? captured.indices;
                    if (shape == RemeshShape.Hull) return Hull(captured, node.support, options, token, node.name);
                    var voxel = RemeshNative.VoxelizeCaptured(supportPositions, supportIndices, options, token, node.name,
                        captured.positions, captured.indices, node.support);
                    if (!options.trimToSource || voxel == null) {
                        if (voxel != null) RemeshGeometryDiagnostics.Capture(captured, voxel, voxel, options);
                        return voxel;
                    }
                    // The remesh surface sits within a cell of the source; two cells of reach
                    // keep a closed source whole and still find nothing behind an open sheet.
                    Vector3 mn = captured.positions[0], mx = captured.positions[0];
                    foreach (var p in captured.positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                    var extent = mx - mn;
                    float cell = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) / Mathf.Max(1, options.voxelResolution);
                    trim = RemeshTrim.Trim(voxel, supportPositions, supportIndices, cell * 2f, token);
                    node.voxelRaw = voxel; node.trimClasses = trim.classes;
                    RemeshGeometryDiagnostics.Capture(captured, voxel, trim.mesh, options);
                    return trim.mesh;
                }, token);
                if (node.voxel == null || node.voxel.TriangleCount == 0) throw new InvalidOperationException("The remesh produced no geometry.");
                node.voxelSyntheticFaces = await Task.Run(() => RemeshSyntheticFaces.Classify(node.voxel.positions,node.voxel.indices,node.support,token),token);
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
            if (hierarchy) {
                var largestCapture = captures[0];
                foreach (var node in captures)
                    if (node.voxel.TriangleCount > largestCapture.voxel.TriangleCount) largestCapture = node;
                var toPreview = largestCapture.spaceToWorld.inverse * capturedRootToWorld;
                largestCapture.unfilteredSource = await Task.Run(() => unfilteredRoot.InSpace(toPreview, largestCapture.source.diagonal), token);
                token.ThrowIfCancellationRequested();
            }
            this.previewNode = null;
            this.previewNode = Primary;
            BuildSourcePreview(); BuildClosurePreview();
            var primary = Primary;
            voxelMesh = BuildMesh(ResultName + "_Voxel", primary.voxel.positions, primary.voxel.indices, primary.voxel.normals, uv: primary.voxel.uv, draftUv: primary.voxel.draftUv);
            trimMaskMesh = primary.trimClasses != null ? BuildTrimMask(ResultName + "_TrimMask", primary.voxelRaw, primary.trimClasses) : null;
            Status = (hierarchy ? $"Remesh: {nodes.Count} node(s), " : "Remesh: ") +
                $"{sourceTriangles:N0} → {resultTriangles:N0} triangles" +
                (shape == RemeshShape.BoundingBox ? $" ({resultTriangles / 12:N0} oriented boxes)" : shape == RemeshShape.Hull ? " (hull)" : "") +
                (trimmedFaces > 0 ? $"; trimmed {trimmedFaces:N0} face(s) the source has no surface for" : options.trimToSource && shape == RemeshShape.LOD0 ? "; nothing to trim" : "") +
                (flippedFaces > 0 ? $"; re-wound {flippedFaces:N0} face(s) to one orientation" : "") +
                (twoSidedFaces > 0 ? $"; {twoSidedFaces:N0} two-sided source face(s), the result renders both sides" : "") + "." +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "");
            UvtLog.Info(LogPrefix + Status);
        }

        void BuildSourcePreview()
        {
            if (sourceMesh) Object.DestroyImmediate(sourceMesh);
            var primary = Primary;
            sourceMesh = BuildMesh(ResultName + "_Source", primary.source.positions, primary.source.indices,
                primary.source.normals, primary.source.hasColors ? primary.source.colors : null, primary.source.uv);
            sourceMesh.tangents = primary.source.tangents;
        }

        string ClosureCounts()
        {
            int loops = 0, patches = 0, added = 0, remaining = 0, refused = 0, bridges = 0;
            foreach (var node in nodes) {
                var support = node.support;
                if (support == null) continue;
                loops += support.loops; patches += support.patchEnds.Count; added += support.addedFaces;
                remaining += support.remainingBoundaryEdges;
                refused += support.loopFailures.Count;
                bridges += support.bridgePartners.Count / 2;
            }
            return $"{loops} initial loops; {patches} patches ({bridges} Bridges); {added} added faces; {refused} refused loops; {remaining} open edges remain";
        }

        void BuildClosurePreview()
        {
            if (closureMesh) Object.DestroyImmediate(closureMesh);
            if (closureRims) Object.DestroyImmediate(closureRims);
            var positions = new List<Vector3>(); var colors = new List<Color>(); var indices = new List<int>();
            var rims = new List<Vector3>(); var rimIndices = new List<int>();
            var rimColors = new List<Color>();
            var contourNames = new List<string> { "All original rims" };
            var contourEdges = new List<Vector3[]>();
            var contourColors = new List<Color>(); var contourReasons = new List<string>();
            var contourInfo = new List<string>();
            var selectionWarnings = new List<string>(); var loopRanges = new List<string>();
            var toPrimary = Primary.spaceToWorld.inverse;
            foreach (var node in nodes) {
                var support = node.support;
                if (support != null) {
                    loopRanges.Add(node.name + (support.loops == 0 ? ": no boundary loops" : $": 0..{support.loops - 1}"));
                    if (support.selectionWarning != null) selectionWarnings.Add(node.name + ": " + support.selectionWarning);
                }
                var p = support?.positions ?? node.source.positions;
                var ix = support?.indices ?? node.source.indices;
                var matrix = toPrimary * node.spaceToWorld;
                for (int f = 0; f < ix.Length / 3; ++f) {
                    int patch = support?.facePatches == null ? 0 : support.facePatches[f];
                    var color = patch == 0 ? new Color(.35f, .4f, .45f) :
                        patch % 2 == 1 ? new Color(1f, .5f, .12f) : new Color(.65f, .35f, .95f);
                    for (int k = 0; k < 3; ++k) {
                        indices.Add(positions.Count); positions.Add(matrix.MultiplyPoint3x4(p[ix[f * 3 + k]])); colors.Add(color);
                    }
                }
                if (support?.boundaryLoops == null) continue;
                for (int loopId = 0; loopId < support.boundaryLoops.Length; ++loopId) {
                    var loop = support.boundaryLoops[loopId]; var pairs = new Vector3[loop.Length * 2];
                    bool refused = support.loopFailures.TryGetValue(loopId, out string reason);
                    bool bridged = support.bridgePartners.TryGetValue(loopId,out int partner);
                    contourInfo.Add(bridged ? $"Bridge loops {loopId},{partner}: " + support.bridgeSearch[loopId] : null);
                    var rimColor = refused ? new Color(1f, .12f, .15f, 1f) : new Color(.2f, .85f, 1f, .95f);
                    for (int k = 0; k < loop.Length; ++k) {
                        pairs[k * 2] = matrix.MultiplyPoint3x4(p[loop[k]]);
                        pairs[k * 2 + 1] = matrix.MultiplyPoint3x4(p[loop[(k + 1) % loop.Length]]);
                        rimIndices.Add(rims.Count); rims.Add(pairs[k * 2]);
                        rimIndices.Add(rims.Count); rims.Add(pairs[k * 2 + 1]);
                        rimColors.Add(rimColor); rimColors.Add(rimColor);
                    }
                    string state = refused ? " — REFUSED" : bridged ? $" — BRIDGE to {partner}" : "";
                    contourNames.Add($"{node.name} / loop {loopId} ({loop.Length} edges){state}"); contourEdges.Add(pairs);
                    contourColors.Add(rimColor); contourReasons.Add(reason);
                }
            }
            closureMesh = BuildMesh(ResultName + "_Closure", positions.ToArray(), indices.ToArray(), null, colors.ToArray());
            closureRims = new Mesh { name = ResultName + "_ClosureRims", hideFlags = HideFlags.HideAndDontSave,
                indexFormat = rims.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            closureRims.SetVertices(rims); closureRims.SetIndices(rimIndices, MeshTopology.Lines, 0); closureRims.RecalculateBounds();
            closureRims.SetColors(rimColors);
            ClosureContourNames = contourNames.ToArray(); ClosureContourEdges = contourEdges.ToArray();
            ClosureContourColors = contourColors.ToArray(); ClosureContourReasons = contourReasons.ToArray();
            ClosureContourInfo = contourInfo.ToArray();
            ClosureSelectionWarning = selectionWarnings.Count == 0 ? null : string.Join("\n", selectionWarnings);
            ClosureLoopRanges = loopRanges.Count == 0 ? null : string.Join("; ", loopRanges);
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
        static RemeshNative.IndexedMesh Hull(RemeshSource captured, RemeshPlanarCap.Support support, RemeshSettings options, CancellationToken token, string node)
        {
            var coarse = JsonUtility.FromJson<RemeshSettings>(JsonUtility.ToJson(options));
            coarse.voxelResolution = options.hullResolution; coarse.solve = false; coarse.shell = false;
            var voxel = RemeshNative.VoxelizeCaptured(support?.positions ?? captured.positions, support?.indices ?? captured.indices,
                coarse, token, node, captured.positions, captured.indices, support);
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
                    try {
                        node.simplified = await Task.Run(() => {
                            bool fit = options.solve && !options.shell && options.sourceShape == RemeshShape.LOD0;
                            return fit ? RemeshSurfaceRefine.Simplify(input, node.support?.positions ?? node.source.positions,
                                node.support?.indices ?? node.source.indices, options, token, out error)
                                : RemeshNative.Simplify(input, options, token, out error);
                        }, token);
                    }
                    catch (InvalidOperationException failure) {
                        uint flags = (options.solve ? 1u : 0u) | (options.shell ? 2u : 0u);
                        int resolution = options.sourceShape == RemeshShape.Hull ? options.hullResolution : options.voxelResolution;
                        RemeshGeometryDiagnostics.CaptureFailure(node.source.positions, node.source.indices,
                            node.voxelRaw, input, options, "Simplify", node.name, failure.Message, resolution, flags, flags, node.support);
                        throw;
                    }
                    node.simplifyError = error;
                    UvtLog.Info(UvtLog.Category.RemeshDiag, FormattableString.Invariant(
                        $"[{node.name}] Simplify: {input.TriangleCount} -> {node.simplified.TriangleCount} triangles; source={node.source.indices.Length / 3}; stopAt={options.targetTriangles}, maximumError={options.maximumError:G6}, achievedCollapseError={error:G6}."));
                }
                else { node.simplified = node.voxel; node.simplifyError = 0; }
                node.completeSimplified = node.simplified;
                node.syntheticFaces = await Task.Run(() => RemeshSyntheticFaces.Project(node.simplified.positions,
                    node.simplified.indices,node.voxel.positions,node.voxel.indices,node.voxelSyntheticFaces,token),token);
                node.selectedSyntheticFaces = node.syntheticFaces != null ? new bool[node.simplified.TriangleCount] : null;
                node.completeSyntheticFaces = node.syntheticFaces;
                node.syntheticRegions = node.syntheticFaces != null ? await Task.Run(() => RemeshSyntheticFaces.Group(node.simplified,node.syntheticFaces,node.spaceToWorld),token) : null;
                if (node.syntheticRegions != null) UvtLog.Info(UvtLog.Category.RemeshDiag,
                    $"[{node.name}] Closure mask projected from remesh: {node.syntheticRegions.faces.Count} connected regions; areas measured in world-space square units. Mixed regions require manual inspection.");
                node.syntheticPickBvh = node.syntheticFaces != null ? await Task.Run(() => new TriangleBvh(node.simplified.positions, node.simplified.indices), token) : null;
                before += node.voxel.TriangleCount; after += node.simplified.TriangleCount;
            }
            token.ThrowIfCancellationRequested();
            var primary = Primary;
            simplifiedMesh = BuildMesh(ResultName + "_Simplified", primary.simplified.positions, primary.simplified.indices, primary.simplified.normals, uv: primary.simplified.uv, draftUv: primary.simplified.draftUv);
            RebuildSyntheticPreview();
            Status = (hierarchy ? $"Simplify: {nodes.Count} node(s), " : "Simplify: ") + $"{before:N0} → {after:N0} triangles.";
        }

        public float SimplifyError => Primary?.simplifyError ?? 0f;

        internal void RebuildSyntheticPreview()
        {
            if (syntheticMaskMesh) Object.DestroyImmediate(syntheticMaskMesh);
            syntheticMaskMesh = Primary?.syntheticFaces != null ? RemeshSyntheticFaces.Preview(ResultName + "_SyntheticFaces",
                Primary.simplified, Primary.syntheticFaces, Primary.selectedSyntheticFaces) : null;
            Changed?.Invoke();
        }

        internal void RemoveSelectedSyntheticFaces()
        {
            if (IsRunning || !Has(Stage.Simplify)) return;
            if (!nodes.Exists(node => node.selectedSyntheticFaces != null && Array.Exists(node.selectedSyntheticFaces, value => value))) return;
            // Validate every node before replacing any stage output.
            var replacements = new List<RemeshNative.IndexedMesh>();
            foreach (var node in nodes) {
                if (node.selectedSyntheticFaces != null) {
                    for (int f = 0; f < node.selectedSyntheticFaces.Length; ++f) {
                        if (node.selectedSyntheticFaces[f] && node.syntheticFaces[f] == 0)
                            throw new InvalidOperationException("Original faces cannot be removed with the Cap selection.");
                    }
                }
                replacements.Add(node.selectedSyntheticFaces != null && Array.Exists(node.selectedSyntheticFaces, value => value)
                    ? RemeshSyntheticFaces.Remove(node.simplified, node.selectedSyntheticFaces) : node.simplified);
            }
            ClearFrom(Stage.Unwrap);
            for (int i = 0; i < nodes.Count; ++i) {
                var node = nodes[i]; var previous = node.simplified;
                node.simplified = replacements[i];
                if (ReferenceEquals(previous, node.simplified)) continue;
                var kept = new List<int>();
                for (int f = 0; f < node.syntheticFaces.Length; ++f) if (!node.selectedSyntheticFaces[f]) kept.Add(node.syntheticFaces[f]);
                node.syntheticFaces = kept.ToArray(); node.selectedSyntheticFaces = new bool[kept.Count];
                node.syntheticPickBvh = null;
                node.syntheticRegions = RemeshSyntheticFaces.Group(node.simplified,node.syntheticFaces,node.spaceToWorld);
            }
            Status = "Selected faces removed. Rerun Normals & UV, then Bake. Restore is available until Simplify is rerun.";
            RebuildSimplifiedPreview();
        }

        internal void RestoreSyntheticFaces()
        {
            if (IsRunning || !Has(Stage.Simplify)) return;
            ClearFrom(Stage.Unwrap);
            foreach (var node in nodes) {
                node.simplified = node.completeSimplified;
                node.syntheticFaces = node.completeSyntheticFaces;
                node.syntheticPickBvh = null;
                node.selectedSyntheticFaces = node.syntheticFaces != null ? new bool[node.syntheticFaces.Length] : null;
                node.syntheticRegions = node.syntheticFaces != null ? RemeshSyntheticFaces.Group(node.simplified,node.syntheticFaces,node.spaceToWorld) : null;
            }
            Status = "Restored optimized geometry. Rerun Normals & UV, then Bake."; RebuildSimplifiedPreview();
        }

        void RebuildSimplifiedPreview()
        {
            if (simplifiedMesh) Object.DestroyImmediate(simplifiedMesh);
            var primary = Primary;
            simplifiedMesh = BuildMesh(ResultName + "_Simplified", primary.simplified.positions, primary.simplified.indices,
                primary.simplified.normals, uv: primary.simplified.uv, draftUv: primary.simplified.draftUv);
            RebuildSyntheticPreview();
        }

        async Task RunUnwrap(RemeshSettings options, CancellationToken token)
        {
            if (!Has(Stage.Simplify)) throw new InvalidOperationException("Run the simplify stage first.");
            long charts = 0, vertices = 0, originalCharts = 0, smallCharts = 0, originalSmallCharts = 0;
            for (int i = 0; i < nodes.Count; ++i) {
                var node = nodes[i];
                token.ThrowIfCancellationRequested();
                Report(hierarchy ? $"Generating normals and unwrapping UVs on {node.name} ({i + 1}/{nodes.Count})…" : "Generating normals and unwrapping UVs…");
                var input = node.simplified;
                node.geometry = await Task.Run(() => RemeshNative.Unwrap(input, options, token), token);
                if (node.mesh) Object.DestroyImmediate(node.mesh);
                node.mesh = BuildResultMesh(node.name + "_LOD0", node.geometry, out node.tangents);
                charts += node.geometry.chartCount; vertices += node.geometry.positions.Length;
                originalCharts += node.geometry.originalChartCount;
                smallCharts += node.geometry.smallChartCount; originalSmallCharts += node.geometry.originalSmallChartCount;
            }
            token.ThrowIfCancellationRequested();
            Status = (hierarchy ? $"Unwrap: {nodes.Count} node(s), " : "Unwrap: ") +
                (charts < originalCharts ? $"{originalCharts:N0} → {charts:N0} islands" : $"{charts:N0} islands") +
                (smallCharts < originalSmallCharts ? $", small (≤8 tris): {originalSmallCharts:N0} → {smallCharts:N0}" : $", {smallCharts:N0} small (≤8 tris)") +
                $", {vertices:N0} vertices.";
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
            long sourceTriangles = 0, targetTriangles = 0, misses = 0, covered = 0, empty = 0, partialMisses = 0; int warnings = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < nodes.Count; ++i) {
                var node = nodes[i];
                token.ThrowIfCancellationRequested();
                Report(hierarchy ? $"Projecting {node.name} into its UV atlas ({i + 1}/{nodes.Count})…"
                    : beauty != null ? "Baking the lit view of the source into the atlas…" : "Projecting source materials into the new UV atlas…");
                var nodeBeauty = beauty?.ForSpace(node.spaceToWorld, node.source.diagonal);
                var source = options.bakeFilteredParts
                    ? await Task.Run(() => ProjectionSource(node, true), token) : node.source;
                var target = node.geometry; var frame = node.tangents;
                // The lightmap regions this node's faces use are read now, on the main
                // thread, and dropped again after the bake — they are float readbacks
                // and only Beauty needs them.
                if (nodeBeauty != null) { Report($"Reading lightmaps for {node.name}…"); source.ReadLightmaps(); }
                try {
                    // GPU: the band loop is driven from here (main thread) so the compute
                    // dispatches are legal; CPU: the whole bake on a worker.
                    node.maps = options.gpuProjection && GpuBvh.Supported
                        ? await RemeshBaker.BakeAsync(source, target, frame, options, token, nodeBeauty, RemeshBaker.CreateGpu, support: node.support)
                        : await Task.Run(() => RemeshBaker.Bake(source, target, frame, options, token, nodeBeauty, node.support), token);
                }
                finally { source.ReleaseLightmaps(); }
                // A re-bake without the transfer must not keep the previous colors.
                VertexChannels.SetColors(node.mesh, node.maps.vertexColors);
                sourceTriangles += source.indices.Length / 3; targetTriangles += target.indices.Length / 3;
                misses += node.maps.misses; covered += node.maps.covered; empty += node.maps.empty; warnings += source.warnings.Length;
                partialMisses += node.maps.partialMisses;
                LogDiagnostics(node, source, options.sourceShape);
                if (node.support?.addedFaces > 0) UvtLog.Info(LogPrefix + node.name + $": Cap-associated bake texels {node.maps.capCovered}, " +
                    $"missed {node.maps.capMisses}, partially projected {node.maps.capPartialMisses}. Original donors only; nearest-support face classification.");
            }
            token.ThrowIfCancellationRequested();
            var primary = Primary;
            baseColorPreview = TextureAssets.FromPixels(primary.maps.color, primary.maps.size, primary.maps.size, linear: false);
            Status = (hierarchy ? $"{nodes.Count} node(s): " : "") + $"{sourceTriangles:N0} → {targetTriangles:N0} triangles. " +
                (options.bakeFilteredParts ? "Full pre-filter source donors. " : "") +
                (misses == 0 && partialMisses == 0 ? "All covered texels projected." :
                    $"{misses:N0} / {covered:N0} texels missed (magenta), {partialMisses:N0} partially projected. Check projection distance and rebake.") +
                (empty > 0 ? $" {empty:N0} proxy texels see no geometry (alpha 0, filled from neighbours)." : "") +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "") +
                $" Bake {clock.Elapsed.TotalSeconds:F1} s ({(primary.maps.gpu ? "GPU" : "CPU")} queries).";
            UvtLog.Info(LogPrefix + Status);
        }

        // Bake health (RemeshDiag log category): BakeHealth judges the counters the bake
        // collected and the source/target scale; the pipeline only hands it the node.
        static void LogDiagnostics(Node node, RemeshSource source, RemeshShape shape)
        {
            BakeHealth.Log(BakeHealth.Build(node.name, node.maps, source.diagonal, source.indices.Length / 3,
                node.geometry.positions, node.geometry.indices.Length / 3, shape));
        }

        // ── meshes ──

        // Builds the result Mesh from unwrapped geometry. The tangent frame comes from
        // meshoptimizer over the final UV layout (MikkT-compatible, the same basis the
        // bake encodes against); Unity's recalculation is only a fallback if the native
        // tangents are missing.
        internal static Mesh BuildResultMesh(string name, RemeshNative.Geometry unwrapped, out Vector4[] tangents)
        {
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            bool urp = pipeline && pipeline.GetType().Name.Contains("Universal");
            unwrapped.normalFrameMode = urp ? RemeshNormalFrame.Mode.Urp : RemeshNormalFrame.Mode.BuiltIn;
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            bool missingUv = unwrapped.uv == null || unwrapped.uv.Length != unwrapped.positions.Length;
            mesh.vertices = unwrapped.positions; mesh.normals = unwrapped.normals;
            mesh.uv = unwrapped.uv; mesh.triangles = unwrapped.indices;
            MeshGeometry.EnsureMeshChannels(mesh);
            if (missingUv) { unwrapped.uv = mesh.uv; unwrapped.draftUv = true; }
            MeshUvState.SetDraft(mesh, unwrapped.draftUv);
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

        internal static Mesh BuildMesh(string name, Vector3[] positions, int[] indices, Vector3[] normals = null, Color[] colors = null, Vector2[] uv = null, bool draftUv = false)
        {
            var mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = positions; mesh.triangles = indices;
            mesh.normals = MeshGeometry.NormalsOrFallback(positions, indices, normals);
            if (uv == null || uv.Length != positions.Length) MeshUvState.SetGeneratedUv(mesh, MeshGeometry.NormalizedXYUv(positions));
            else if (draftUv) MeshUvState.SetGeneratedUv(mesh, uv);
            else mesh.uv = uv;
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
                if (syntheticMaskMesh) Object.DestroyImmediate(syntheticMaskMesh);
                syntheticMaskMesh = null;
                if (simplifiedMesh) Object.DestroyImmediate(simplifiedMesh);
                simplifiedMesh = null; keys[(int)Stage.Simplify] = null;
                foreach (var node in nodes) { node.simplified = node.completeSimplified = null; node.syntheticFaces = node.completeSyntheticFaces = null; node.syntheticPickBvh = null;
                    node.syntheticRegions = null;
                    node.selectedSyntheticFaces = null; node.simplifyError = 0; }
            }
            if (stage <= Stage.Remesh) {
                if (voxelMesh) Object.DestroyImmediate(voxelMesh);
                if (trimMaskMesh) Object.DestroyImmediate(trimMaskMesh);
                voxelMesh = null; trimMaskMesh = null; keys[(int)Stage.Remesh] = null;
                foreach (var node in nodes) { node.voxel = node.voxelRaw = null; node.trimClasses = null; node.voxelSyntheticFaces = null; }
            }
            if (stage <= Stage.Prepare) {
                if (sourceMesh) Object.DestroyImmediate(sourceMesh);
                if (closureMesh) Object.DestroyImmediate(closureMesh);
                if (closureRims) Object.DestroyImmediate(closureRims);
                sourceMesh = closureMesh = closureRims = null; preparationKey = null; ClosureSummary = null;
                ClosureSelectionWarning = null; ClosureLoopRanges = null;
                ClosureContourInfo = null;
                ClosureContourNames = null; ClosureContourEdges = null;
                ClosureContourColors = null; ClosureContourReasons = null;
                nodes.Clear(); previewNode = null; hierarchy = false; CapturedSource = null; unfilteredRoot = null;
            }
        }

        /// <summary>Cancels a running stage and drops every output. Cancellation is
        /// cooperative, so during a run the clear is deferred to the run's exit instead of
        /// racing the stage that is still executing.</summary>
        public void Dispose()
        {
            if (IsRunning) { disposeRequested = true; cancellation?.Cancel(); return; }
            ClearFrom(Stage.Prepare);
        }
    }
}
