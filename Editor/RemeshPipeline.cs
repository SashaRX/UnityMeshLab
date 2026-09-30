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

        // Previews come from the primary node: the weld itself, or the largest node of
        // a hierarchy so the existing preview panel keeps working.
        Mesh sourceMesh, voxelMesh, simplifiedMesh;
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
        public Mesh SimplifiedMesh => simplifiedMesh;
        public Mesh ResultMesh => Primary?.mesh;
        public RemeshNative.Geometry Geometry => Primary?.geometry;
        public RemeshBaker.Maps Maps => Primary?.maps;
        public Texture2D BaseColorPreview => baseColorPreview;
        public float SourceDiagonal => Primary?.source != null ? Primary.source.diagonal : 0f;
        public bool SourceHasColors { get { foreach (var n in nodes) if (n.source != null && n.source.hasColors) return true; return false; } }

        public bool Has(Stage stage)
        {
            if (nodes.Count == 0) return false;
            switch (stage) {
                case Stage.Remesh: return nodes[0].voxel != null;
                case Stage.Simplify: return nodes[0].simplified != null;
                case Stage.Unwrap: return nodes[0].geometry != null;
                default: return nodes[0].maps != null;
            }
        }

        /// <summary>Short stage summary for the UI headers; null before the stage ran.</summary>
        public string Summary(Stage stage)
        {
            if (!Has(stage)) return null;
            long total = 0;
            switch (stage) {
                case Stage.Remesh: foreach (var n in nodes) total += n.voxel.TriangleCount; return $"{total:N0} tris";
                case Stage.Simplify: foreach (var n in nodes) total += n.simplified.TriangleCount; return $"{total:N0} tris";
                case Stage.Unwrap: foreach (var n in nodes) total += n.geometry.chartCount; return $"{total:N0} islands";
                default: foreach (var n in nodes) total += n.maps.misses; return total > 0 ? $"{total:N0} missed" : "done";
            }
        }

        /// <summary>The settings that feed each stage, so a change marks it stale.</summary>
        public static string Key(Stage stage, RemeshSettings s, GameObject source)
        {
            switch (stage) {
                case Stage.Remesh: return $"{(source ? source.GetInstanceID() : 0)}|{s.voxelResolution}|{s.solve}|{s.shell}|{s.lod0Only}|{s.keepHierarchy}|{s.sourceShape}";
                case Stage.Simplify: return $"{s.simplify}|{s.targetTriangles}|{s.maximumError}|{s.regularize}|{s.preserveFolds}|{s.pruneSmallParts}";
                case Stage.Unwrap: return $"{s.hardEdges}|{s.normalCrease}|{s.normalSmoothing}|{s.normalWeighting}|{s.textureResolution}|{s.padding}|{s.chartMaxCost}|" +
                    $"{s.chartNormalDeviation}|{s.chartNormalSeam}|{s.chartStraightness}|{s.chartRoundness}|{s.chartIterations}|" +
                    $"{s.maxChartArea}|{s.maxChartBoundary}|{s.packRotate}|{s.packBlockAlign}|{s.packBruteForce}";
                default: return $"{s.bakeMode}|{s.projectionDistance}|{s.bakeSamples}|{s.transferVertexColor}|{s.transferVertexAlpha}";
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
            catch (Exception e) { Status = e.Message; UvtLog.Error("[Remesh] " + e); return false; }
            finally {
                cancellation.Dispose(); cancellation = null;
                if (locked) EditorApplication.UnlockReloadAssemblies();
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
                    if (node.source == null) { UvtLog.Warn("[Remesh] " + renderer.name + ": nothing to remesh, skipped."); continue; }
                    captures.Add(node);
                }
                if (captures.Count == 0) throw new InvalidOperationException("Every captured node was empty; nothing to remesh.");
            }
            long sourceTriangles = 0, resultTriangles = 0; int warnings = 0;
            bool box = options.sourceShape == RemeshShape.BoundingBox;
            for (int i = 0; i < captures.Count; ++i) {
                var node = captures[i];
                foreach (var warning in node.source.warnings) { UvtLog.Warn("[Remesh] " + warning); ++warnings; }
                token.ThrowIfCancellationRequested();
                Report(hierarchy
                    ? (box ? $"Bounding {node.name} ({i + 1}/{captures.Count})…" : $"Voxel remeshing {node.name} ({i + 1}/{captures.Count})…")
                    : (box ? "Building the bounding-box proxy…" : "Voxel remeshing…"));
                var captured = node.source;
                // The bounding-box shape replaces the voxelizer with the capture's own
                // AABB — a far-LOD proxy; materials and lighting still bake from the
                // captured geometry, projected onto the box downstream.
                node.voxel = await Task.Run(() => box
                    ? BoxMesh(captured.positions)
                    : RemeshNative.Voxelize(captured.positions, captured.indices, options, token), token);
                sourceTriangles += captured.indices.Length / 3;
                resultTriangles += node.voxel.TriangleCount;
            }
            token.ThrowIfCancellationRequested();
            nodes.AddRange(captures);
            var primary = Primary;
            sourceMesh = BuildMesh(ResultName + "_Source", primary.source.positions, primary.source.indices,
                primary.source.normals, primary.source.hasColors ? primary.source.colors : null);
            voxelMesh = BuildMesh(ResultName + "_Voxel", primary.voxel.positions, primary.voxel.indices);
            Status = (hierarchy ? $"Remesh: {nodes.Count} node(s), " : "Remesh: ") +
                $"{sourceTriangles:N0} → {resultTriangles:N0} triangles" + (box ? " (bounding box)." : ".") +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "");
        }

        // Axis-aligned box mesh around a point cloud — the Bounding-box remesh shape.
        // Outward winding, capture-space in and out.
        internal static RemeshNative.IndexedMesh BoxMesh(Vector3[] positions)
        {
            Vector3 mn = positions[0], mx = positions[0];
            foreach (var p in positions) { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
            var corners = new[] {
                new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z), new Vector3(mx.x, mx.y, mn.z), new Vector3(mn.x, mx.y, mn.z),
                new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z), new Vector3(mx.x, mx.y, mx.z), new Vector3(mn.x, mx.y, mx.z),
            };
            int[] faces = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            return new RemeshNative.IndexedMesh { positions = corners, indices = faces };
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
            }
            long sourceTriangles = 0, targetTriangles = 0, misses = 0, covered = 0; int warnings = 0;
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
                try { node.maps = await Task.Run(() => RemeshBaker.Bake(source, target, frame, options, token, nodeBeauty), token); }
                finally { source.ReleaseLightmaps(); }
                // A re-bake without the transfer must not keep the previous colors.
                node.mesh.colors = node.maps.vertexColors;
                sourceTriangles += source.indices.Length / 3; targetTriangles += target.indices.Length / 3;
                misses += node.maps.misses; covered += node.maps.covered; warnings += source.warnings.Length;
                LogDiagnostics(node);
            }
            token.ThrowIfCancellationRequested();
            var primary = Primary;
            baseColorPreview = new Texture2D(primary.maps.size, primary.maps.size, TextureFormat.RGBA32, false, false) { hideFlags = HideFlags.HideAndDontSave };
            baseColorPreview.SetPixels32(primary.maps.color); baseColorPreview.Apply();
            Status = (hierarchy ? $"{nodes.Count} node(s): " : "") + $"{sourceTriangles:N0} → {targetTriangles:N0} triangles. " +
                (misses == 0 ? "All covered texels projected." : $"{misses:N0} / {covered:N0} texels missed (magenta). Increase projection distance and rebake.") +
                (warnings > 0 ? $" {warnings} material warning(s), see Console." : "");
            UvtLog.Info("[Remesh] " + Status);
        }

        // Bake health counters (RemeshDiag log category): cage welding, one-sided
        // border normals, nearest-query fallbacks and normal-map tilt. A loud,
        // strongly-tilted map on a smooth-ish source is the signature of displaced
        // projection samples; loud fraction > 5% also warns on its own.
        static void LogDiagnostics(Node node)
        {
            var baked = node.maps; var captured = node.source; var target = node.geometry;
            // Source and target live in the same capture space by construction; the
            // diagonal ratio proves it at a glance and catches scale regressions.
            Vector3 mn = target.positions[0], mx = target.positions[0];
            foreach (var pv in target.positions) { mn = Vector3.Min(mn, pv); mx = Vector3.Max(mx, pv); }
            float targetDiagonal = (mx - mn).magnitude;
            float scaleRatio = targetDiagonal / Mathf.Max(1e-8f, captured.diagonal);
            string prefix = "[" + node.name + "] ";
            if (UvtLog.IsCategoryEnabled(UvtLog.Category.RemeshDiag))
                UvtLog.Info(UvtLog.Category.RemeshDiag, prefix +
                    $"cage: {baked.weldedPositions:N0} welded positions ({baked.splitCopies:N0} split copies), " +
                    $"{baked.oneSidedNormals:N0} one-sided border normals, max cage deviation {baked.maxOneSidedDeg:F0}°, {baked.zeroNormals:N0} zero normals; " +
                    $"projection: {baked.rayFallbacks:N0} nearest-fallback samples, {baked.misses:N0} missed texels, front-face filter {(baked.facingFilter ? "on" : "off")}; " +
                    $"normal map tilt: mean {baked.meanTiltDeg:F1}° / max {baked.maxTiltDeg:F0}°, {baked.loudTexels:N0} texels >45°; " +
                    $"bounds diagonal: source {captured.diagonal:F3} / target {targetDiagonal:F3} (ratio {scaleRatio:F2})");
            if (baked.zeroNormals > 0)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, prefix +
                    baked.zeroNormals + " result vertices have zero normals — their texels bake through zeroed ray directions " +
                    "and tangent frames. Re-run the UV stage; if it repeats, the remesh produced degenerate faces (lower simplification error or raise voxel resolution).");
            else if (baked.rayFallbacks > baked.covered * 4 / 5 && baked.covered > 0)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, prefix +
                    baked.rayFallbacks.ToString("N0") + " of the projection samples fell back to nearest-point search — the rays " +
                    "are not hitting the source. Check the projection distance and the hard-edge mode, and rebake.");
            if (Mathf.Abs(scaleRatio - 1f) > 0.1f)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, prefix +
                    $"target/source bounds diagonal ratio is {scaleRatio:F2} — the remeshed mesh no longer matches the source size. " +
                    "Check voxel resolution, small-part pruning and simplification settings, and rebake.");
            if (baked.loudTexels > baked.covered / 20 && baked.meanTiltDeg > 30f)
                UvtLog.Warn(UvtLog.Category.RemeshDiag, prefix +
                    $"{100.0 * baked.loudTexels / Mathf.Max(1, baked.covered):F1}% of texels lean >45° with a {baked.meanTiltDeg:F0}° mean tilt — " +
                    "the map is dominated by extreme normals. Check the hard-edge mode, projection distance and cage fit, " +
                    "and compare against the source: fine detail should tilt a map, not saturate it.");
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
                    if (node.mesh) node.mesh.colors = null;
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
                voxelMesh = null; sourceMesh = null; keys[(int)Stage.Remesh] = null;
                nodes.Clear(); hierarchy = false;
            }
        }

        public void Dispose()
        {
            cancellation?.Cancel();
            ClearFrom(Stage.Remesh);
        }
    }
}
