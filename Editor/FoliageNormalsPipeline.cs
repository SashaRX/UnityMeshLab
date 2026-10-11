using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal sealed class FoliageNormalsPipeline : IDisposable
    {
        internal enum Stage { Capture, Scatter, Envelope, Smooth, Remesh, Transfer }
        readonly List<FoliageTargetSnapshot> targets = new List<FoliageTargetSnapshot>();
        readonly string[] keys = new string[6];
        CancellationTokenSource cancellation;
        int running;
        bool disposeRequested;
        Vector3[] capturePositions;
        int[] captureIndices;
        Matrix4x4 rootToWorld;
        Stopwatch runClock;
        double elapsedSeconds;
        internal IReadOnlyList<FoliageTargetSnapshot> Targets => targets;
        internal FoliageSurfaceSampler.Result Samples { get; private set; }
        internal FoliagePrimitive[] Primitives { get; private set; }
        internal FoliageShapeProxyBuilder.Result ShapeProxy { get; private set; }
        internal FoliageNormalSource NormalSource { get; private set; }
        internal string ProxyName => NormalSource == FoliageNormalSource.VoxelEnvelope ? "Voxel Envelope / Volume Proxy" : NormalSource + " Normal Proxy";
        internal RemeshNative.IndexedMesh Envelope { get; private set; }
        internal RemeshNative.IndexedMesh Smoothed { get; private set; }
        internal RemeshNative.IndexedMesh Remeshed { get; private set; }
        internal Mesh EnvelopeMesh { get; private set; }
        internal Mesh SmoothedMesh { get; private set; }
        internal Mesh RemeshedMesh { get; private set; }
        internal FoliageProxySmoother.Diagnostics SmoothDiagnostics { get; private set; }
        internal FoliageProxyRemesher.Diagnostics RemeshDiagnostics { get; private set; }
        internal FoliageNormalTransfer.Diagnostics TransferDiagnostics { get; private set; }
        internal Bounds CaptureBounds { get; private set; }
        internal GameObject CapturedRoot { get; private set; }
        internal bool IsRunning => Volatile.Read(ref running) != 0;
        internal Stage? RunningStage { get; private set; }
        internal string Status { get; private set; } = "Select a foliage root and capture its triangle submeshes.";
        internal double ElapsedSeconds => IsRunning && runClock != null ? runClock.Elapsed.TotalSeconds : elapsedSeconds;
        internal int ProxyComponents { get; private set; }
        internal Action Changed;
        readonly IFoliageProxyBuilder builder;
        readonly IFoliageProxyRemesher remesher;
        internal FoliageNormalsPipeline(IFoliageProxyBuilder builder = null, IFoliageProxyRemesher remesher = null)
        { this.builder = builder ?? new MeshoptVoxelProxyBuilder(); this.remesher = remesher ?? new FoliageProxyRemesher(); }

        internal bool Has(Stage stage) => keys[(int)stage] != null;
        internal bool CanApply => !IsRunning && Has(Stage.Transfer) && TransferDiagnostics?.transferred > 0 && CaptureCurrent();
        internal bool CaptureCurrent() {
            if (!CapturedRoot || CapturedRoot.transform.localToWorldMatrix != rootToWorld) return false;
            foreach (var t in targets) if (!t.IsCurrent(CapturedRoot.transform.worldToLocalMatrix)) return false;
            return targets.Count > 0;
        }

        internal static string Key(Stage stage, FoliageNormalsSettings s, GameObject root)
        {
            string Ids<T>(T[] objects) where T : Object => string.Join(",", Array.ConvertAll(objects, o => o ? o.GetInstanceID().ToString() : "0"));
            switch (stage) {
                case Stage.Capture: return $"{(root ? root.GetInstanceID() : 0)}|{s.lod0Only}|{s.selection}|{s.filterRenderers}|{Ids(s.renderers)}|{Ids(s.materials)}|{string.Join(",", s.submeshes)}";
                case Stage.Scatter: return s.normalSource != FoliageNormalSource.VoxelEnvelope ? s.normalSource.ToString() : FormattableString.Invariant($"{s.normalSource}|{s.sampleSpacing:R}|{s.relativeSpacing}|{s.radiusMultiplier:R}|{s.dedupSpacingFraction:R}|{s.minimumSamplesPerIsland}|{s.deterministicSeed}|{s.maximumPrimitiveCount}|{s.includeSourceVertices}|{s.includeTriangleCentroids}");
                case Stage.Envelope: return s.normalSource == FoliageNormalSource.VoxelEnvelope ? $"{s.normalSource}|{s.voxelResolution}|{s.primitiveSubdivision}" : FormattableString.Invariant($"{s.normalSource}|{s.autoFitProxy}|{s.proxyCenter.x:R}|{s.proxyCenter.y:R}|{s.proxyCenter.z:R}|{s.proxyRotation.x:R}|{s.proxyRotation.y:R}|{s.proxyRotation.z:R}|{s.proxyRadius:R}|{s.proxyRadiusScale:R}|{s.proxySegments}");
                case Stage.Smooth: return s.normalSource != FoliageNormalSource.VoxelEnvelope ? s.normalSource.ToString() : FormattableString.Invariant($"{s.smoothingIterations}|{s.smoothingStrength:R}|{s.lambda:R}|{s.mu:R}");
                case Stage.Remesh: return s.normalSource != FoliageNormalSource.VoxelEnvelope ? s.normalSource.ToString() : !s.remeshProxy ? "Disabled" : FormattableString.Invariant($"{s.remeshEdgeLength:R}|{s.remeshIterations}|{s.remeshRelaxation:R}|{s.maximumProxyTriangles}");
                default: return FormattableString.Invariant($"{s.maxTransferDistance:R}|{s.transferBlend:R}|{s.alignToOriginalHemisphere}|{s.gpuProjection}|{s.recalculateTangents}");
            }
        }

        internal void InvalidateChanged(FoliageNormalsSettings settings, GameObject root)
        {
            if (IsRunning) return;
            for (var stage = Stage.Capture; stage <= Stage.Transfer; stage++)
                if (keys[(int)stage] != null && (keys[(int)stage] != Key(stage, settings, root) || stage == Stage.Capture && !CaptureCurrent())) { ClearFrom(stage); break; }
        }

        internal void Cancel() { cancellation?.Cancel(); Status = "Cancelling after current native/GPU batch…"; Changed?.Invoke(); }

        internal async Task<bool> Run(GameObject root, FoliageNormalsSettings live, Stage to, CancellationToken externalToken = default)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(externalToken); var token = cancellation.Token;
            var clock = Stopwatch.StartNew(); runClock = clock; bool locked = false;
            try {
                live.Validate(); var options = live.Snapshot();
                NormalSource = options.normalSource;
                token.ThrowIfCancellationRequested();
                EditorApplication.LockReloadAssemblies(); locked = true;
                for (var stage = Stage.Capture; stage <= to; stage++) {
                    token.ThrowIfCancellationRequested();
                    bool changed = keys[(int)stage] == null || keys[(int)stage] != Key(stage, options, root) || stage == Stage.Capture && !CaptureCurrent();
                    if (!changed) continue;
                    ClearFromCore(stage); RunningStage = stage; Status = stage + "…"; Changed?.Invoke();
                    await Task.Yield(); token.ThrowIfCancellationRequested();
                    switch (stage) {
                        case Stage.Capture: await Capture(root, options, token); break;
                        case Stage.Scatter:
                            if (options.normalSource != FoliageNormalSource.VoxelEnvelope) break;
                            Samples = await Task.Run(() => FoliageSurfaceSampler.Sample(capturePositions, captureIndices, options, token), token);
                            Primitives = new FoliagePrimitive[Samples.samples.Length];
                            for (int i = 0; i < Primitives.Length; i++) Primitives[i] = new FoliagePrimitive { center = Samples.samples[i], radius = Samples.spacing * options.radiusMultiplier };
                            break;
                        case Stage.Envelope:
                            if (options.normalSource != FoliageNormalSource.VoxelEnvelope) {
                                ShapeProxy = await Task.Run(() => FoliageShapeProxyBuilder.Build(capturePositions, captureIndices, CaptureBounds, options, token), token);
                                Envelope = ShapeProxy.mesh; ProxyComponents = 1;
                                EnvelopeMesh = PreviewMesh(ProxyName, Envelope); break;
                            }
                            var spheres = Primitives;
                            Envelope = await Task.Run(() => builder.Build(FoliagePrimitiveBuilder.Build(spheres, options.primitiveSubdivision, token), options.voxelResolution, token), token);
                            ProxyComponents = await Task.Run(() => FoliageProxyBuilder.Validate(Envelope, token, false).euler.Count, token);
                            EnvelopeMesh = PreviewMesh(ProxyName, Envelope); break;
                        case Stage.Smooth:
                            if (options.normalSource != FoliageNormalSource.VoxelEnvelope) {
                                Smoothed = Envelope; SmoothedMesh = PreviewMesh(ProxyName, Smoothed); break;
                            }
                            FoliageProxySmoother.Diagnostics diagnostic = null;
                            Smoothed = await Task.Run(() => FoliageProxySmoother.Smooth(Envelope, options, token, out diagnostic), token);
                            SmoothDiagnostics = diagnostic;
                            if (diagnostic.VolumeChange > .15) UvtLog.Warn($"[Foliage] Smoothing volume changed {diagnostic.VolumeChange:P1}; reduce strength/iterations.");
                            SmoothedMesh = PreviewMesh("Smoothed Volume Proxy", Smoothed); break;
                        case Stage.Remesh:
                            if (options.normalSource != FoliageNormalSource.VoxelEnvelope || !options.remeshProxy) Remeshed = Smoothed;
                            else {
                                var remeshResult = await Task.Run(() => remesher.Remesh(Smoothed, options, token), token);
                                Remeshed = remeshResult.mesh; RemeshDiagnostics = remeshResult.diagnostics;
                                if (RemeshDiagnostics.volumeChange > .1) UvtLog.Warn($"[Foliage] Proxy remesh volume changed {RemeshDiagnostics.volumeChange:P1}; reduce target edge length.");
                                if (RemeshDiagnostics.after.edgeVariation > RemeshDiagnostics.before.edgeVariation + .01)
                                    UvtLog.Warn("[Foliage] Proxy edge variation increased; inspect the wire preview and adjust target edge length/iterations.");
                                UvtLog.Info("[Foliage] " + RemeshDiagnostics);
                            }
                            RemeshedMesh = PreviewMesh(options.normalSource == FoliageNormalSource.VoxelEnvelope ? "Remeshed Volume Proxy" : ProxyName, Remeshed); break;
                        default: await Transfer(options, token); break;
                    }
                    token.ThrowIfCancellationRequested(); keys[(int)stage] = Key(stage, options, root);
                    elapsedSeconds = clock.Elapsed.TotalSeconds; Changed?.Invoke();
                }
                Status = to == Stage.Transfer ? TransferDiagnostics.ToString() : $"{to} complete. {Primitives?.Length ?? 0:N0} primitives, {(Remeshed ?? Smoothed ?? Envelope)?.TriangleCount ?? 0:N0} proxy triangles.";
                UvtLog.Info("[Foliage] " + Status); return true;
            }
            catch (OperationCanceledException) { if (RunningStage.HasValue) ClearFromCore(RunningStage.Value); Status = "Cancelled."; return false; }
            catch (Exception e) { if (RunningStage.HasValue) ClearFromCore(RunningStage.Value); Status = e.Message; UvtLog.Error("[Foliage] " + e); return false; }
            finally {
                cancellation.Dispose(); cancellation = null;
                if (disposeRequested) { disposeRequested = false; ClearFromCore(Stage.Capture); }
                if (locked) EditorApplication.UnlockReloadAssemblies();
                RunningStage = null; elapsedSeconds = clock.Elapsed.TotalSeconds; runClock = null; Interlocked.Exchange(ref running, 0); Changed?.Invoke();
            }
        }

        async Task Capture(GameObject root, FoliageNormalsSettings options, CancellationToken token)
        {
            if (!root || EditorUtility.IsPersistent(root)) throw new InvalidOperationException("Select a scene or Prefab Mode root.");
            rootToWorld = root.transform.localToWorldMatrix;
            if (rootToWorld.determinant == 0) throw new InvalidOperationException("Root transform is singular.");
            var excluded = new HashSet<Renderer>();
            if (options.lod0Only) foreach (var group in root.GetComponentsInChildren<LODGroup>()) {
                var lods = group.GetLODs(); var first = new HashSet<Renderer>(lods.Length > 0 ? lods[0].renderers : Array.Empty<Renderer>());
                for (int i = 1; i < lods.Length; i++) foreach (var r in lods[i].renderers) if (!first.Contains(r)) excluded.Add(r);
            }
            foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>()) {
                token.ThrowIfCancellationRequested();
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || excluded.Contains(renderer) ||
                    (options.filterRenderers || options.renderers.Length > 0) && Array.IndexOf(options.renderers, renderer) < 0) continue;
                var filter = renderer.GetComponent<MeshFilter>(); if (!filter || !filter.sharedMesh) continue;
                if (MeshNaming.IsCollision(renderer.name) || MeshNaming.IsCollision(filter.sharedMesh.name) || options.lod0Only &&
                    (MeshNaming.LodIndex(renderer.name) > 0 || MeshNaming.LodIndex(filter.sharedMesh.name) > 0)) continue;
                bool eligible = false;
                var materials = renderer.sharedMaterials;
                for (int s = 0; s < filter.sharedMesh.subMeshCount; s++)
                    if (filter.sharedMesh.GetTopology(s) == MeshTopology.Triangles &&
                        FoliageTargetSnapshot.SelectsSubmesh(s, materials, options)) { eligible = true; break; }
                if (!eligible) continue;
                var snapshot = new FoliageTargetSnapshot(renderer, root.transform.worldToLocalMatrix, options, token, true);
                if (snapshot.selectedIndices.Length == 0) { snapshot.Dispose(); continue; }
                targets.Add(snapshot);
                await Task.Run(() => snapshot.PrepareArrays(token), token);
            }
            var geometry = await Task.Run(() => {
                var p = new List<Vector3>(); var t = new List<int>();
                foreach (var snapshot in targets) {
                    token.ThrowIfCancellationRequested(); int offset = p.Count;
                    p.AddRange(snapshot.positions); foreach (int v in snapshot.selectedIndices) t.Add(v + offset);
                }
                if (t.Count == 0) throw new InvalidOperationException("No selected static foliage triangles. Check renderer/material/submesh filters.");
                var points = new Vector3[t.Count]; for (int i = 0; i < points.Length; i++) points[i] = p[t[i]];
                return (positions: p.ToArray(), indices: t.ToArray(), bounds: FoliageProxyBuilder.BoundsOf(points));
            }, token);
            capturePositions = geometry.positions; captureIndices = geometry.indices;
            CaptureBounds = geometry.bounds; CapturedRoot = root;
            if (!CaptureCurrent()) throw new InvalidOperationException("Source changed during capture. Recapture.");
        }

        async Task Transfer(FoliageNormalsSettings options, CancellationToken token)
        {
            if (!CaptureCurrent()) throw new InvalidOperationException("Source mesh, materials or transforms changed. Recapture.");
            var bvh = await Task.Run(() => new TriangleBvh(Remeshed.positions, Remeshed.indices), token);
            using (var gpu = options.gpuProjection && GpuBvh.Supported ? GpuBvh.TryCreate(bvh) : null) {
                if (options.gpuProjection && gpu == null) UvtLog.Warn("[Foliage] GPU projection unavailable; using CPU queries (reported in diagnostics).");
                var outputs = new List<Mesh>(); var diagnostics = new FoliageNormalTransfer.Diagnostics();
                try {
                    float reach = options.maxTransferDistance > 0 ? options.maxTransferDistance : CaptureBounds.size.magnitude * 2;
                    foreach (var target in targets) {
                        token.ThrowIfCancellationRequested();
                        var result = await FoliageNormalTransfer.Transfer(target, Remeshed, bvh, gpu, options, reach, token);
                        outputs.Add(target.BuildResult(result.normals, options.recalculateTangents, token, out int duplicated));
                        result.diagnostics.duplicated = duplicated; diagnostics.Add(result.diagnostics);
                    }
                    token.ThrowIfCancellationRequested();
                    if (!CaptureCurrent()) throw new InvalidOperationException("Source changed during transfer. Recapture.");
                    if (diagnostics.transferred == 0) throw new InvalidOperationException("No normals transferred. Increase maximum transfer distance.");
                    for (int i = 0; i < targets.Count; i++) targets[i].working = outputs[i];
                    outputs.Clear(); TransferDiagnostics = diagnostics;
                }
                finally { foreach (var mesh in outputs) FoliageTargetSnapshot.Destroy(mesh); }
            }
        }

        internal static Mesh PreviewMesh(string name, RemeshNative.IndexedMesh geometry)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave,
                indexFormat = geometry.positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            try { mesh.vertices = geometry.positions; mesh.triangles = geometry.indices; mesh.normals = geometry.normals; mesh.RecalculateBounds(); return mesh; }
            catch { Object.DestroyImmediate(mesh); throw; }
        }

        internal void Apply()
        {
            if (!CanApply) throw new InvalidOperationException("Build and transfer a current result first.");
            Undo.IncrementCurrentGroup(); int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Apply Foliage Normals");
            try {
                foreach (var target in targets) {
                    // Scene/Undo owns this clone; clearing or rebuilding previews cannot destroy it.
                    var clone = Object.Instantiate(target.working); clone.hideFlags = HideFlags.None; clone.name = target.working.name;
                    Undo.RegisterCreatedObjectUndo(clone, "Foliage normal mesh");
                    Undo.RecordObject(target.renderer, "Foliage normals"); Undo.RecordObject(target.filter, "Foliage normals");
                    target.filter.sharedMesh = clone; target.applied = clone;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(target.filter);
                }
            }
            catch { Undo.RevertAllDownToGroup(group); throw; }
            finally { Undo.CollapseUndoOperations(group); }
        }

        internal string[] Save(string folder)
        {
            if (!CanApply) throw new InvalidOperationException("Build and transfer a current result first.");
            if (!MeshAssetOperations.EnsureOutputFolder(folder)) throw new InvalidOperationException("Choose a writable folder under Assets.");
            var paths = new List<string>(); AssetDatabase.StartAssetEditing();
            try {
                foreach (var target in targets) {
                    string name = target.working.name;
                    foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
                    string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + name + ".asset");
                    if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("Cannot create mesh asset path.");
                    var saved = Object.Instantiate(target.working); saved.hideFlags = HideFlags.None; saved.name = target.working.name;
                    try { AssetDatabase.CreateAsset(saved, path); paths.Add(path); }
                    catch { if (!EditorUtility.IsPersistent(saved)) Object.DestroyImmediate(saved); throw; }
                }
            }
            catch { foreach (string path in paths) AssetDatabase.DeleteAsset(path); throw; }
            finally { AssetDatabase.StopAssetEditing(); }
            AssetDatabase.SaveAssets(); return paths.ToArray();
        }

        internal void ClearFrom(Stage stage) { if (IsRunning) throw new InvalidOperationException("Cancel and await the run before clearing."); ClearFromCore(stage); Changed?.Invoke(); }
        void ClearFromCore(Stage stage)
        {
            for (int i = (int)stage; i < keys.Length; i++) keys[i] = null;
            if (stage <= Stage.Transfer) { foreach (var t in targets) { FoliageTargetSnapshot.Destroy(t.working); t.working = null; } TransferDiagnostics = null; }
            if (stage <= Stage.Remesh) { FoliageTargetSnapshot.Destroy(RemeshedMesh); RemeshedMesh = null; Remeshed = null; RemeshDiagnostics = null; }
            if (stage <= Stage.Smooth) { FoliageTargetSnapshot.Destroy(SmoothedMesh); SmoothedMesh = null; Smoothed = null; SmoothDiagnostics = null; }
            if (stage <= Stage.Envelope) { FoliageTargetSnapshot.Destroy(EnvelopeMesh); EnvelopeMesh = null; Envelope = null; ShapeProxy = null; ProxyComponents = 0; }
            if (stage <= Stage.Scatter) { Samples = null; Primitives = null; }
            if (stage <= Stage.Capture) { foreach (var t in targets) t.Dispose(); targets.Clear(); capturePositions = null; captureIndices = null; CapturedRoot = null; }
        }
        public void Dispose() { if (IsRunning) { disposeRequested = true; Cancel(); } else ClearFromCore(Stage.Capture); }
    }
}
