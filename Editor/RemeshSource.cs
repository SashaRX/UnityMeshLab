using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // All Unity objects are read on the main thread. Workers only see these snapshots.
    // Geometry is expressed in the capture space (the source root or a hierarchy node).
    internal sealed class RemeshSource
    {
        public Vector3[] positions, normals;
        public Vector4[] tangents;
        public Vector2[] uv;
        public Vector2[] uv2;               // lightmap UVs; zero where the source mesh has none
        public Color[] colors;       // per vertex; white where the source mesh has none
        public bool hasColors;       // any contributing mesh carried vertex colours
        public int[] indices, faceMaterials;
        public int[] faceLightmaps;  // per face: index into lightmapRefs/lightmaps, -1 when the renderer had none
        // Unique (texture, direction, scaleOffset) triples the faces reference. The
        // pixels are NOT read at capture: a Beauty bake calls ReadLightmaps on the
        // main thread right before it runs and ReleaseLightmaps after, so Materials
        // bakes never pay for them and the float readbacks live only during the bake.
        public LightmapRef[] lightmapRefs;
        public RemeshBeauty.Lightmap[] lightmaps; // decoded linear, null outside a Beauty bake

        internal sealed class LightmapRef
        {
            public Texture2D color, direction;
            public Vector4 scaleOffset;
        }

        /// <summary>Reads the referenced lightmap regions (main thread). No-op when already read.</summary>
        public void ReadLightmaps()
        {
            if (lightmaps != null || lightmapRefs == null) return;
            var read = new RemeshBeauty.Lightmap[lightmapRefs.Length];
            for (int i = 0; i < read.Length; ++i)
                read[i] = RemeshBeauty.ReadLightmap(lightmapRefs[i].color, lightmapRefs[i].direction, lightmapRefs[i].scaleOffset);
            lightmaps = read;
        }

        public void ReleaseLightmaps() { lightmaps = null; }
        public Surface[] materials;
        public float diagonal;
        public string[] warnings = Array.Empty<string>();
        // Which captured renderer each vertex came from, and that renderer's local →
        // capture-space matrix: the oriented-box shape measures every renderer's
        // (filtered) geometry along its own authored axes.
        public int[] vertexRenderer;
        public Matrix4x4[] rendererToSpace;

        internal sealed class Image
        {
            public Color32[] pixels;
            public bool srgb;
            public Color[] hdrPixels;
            public int width, height;
            public TextureWrapMode wrapU, wrapV;
            public Color Sample(Vector2 uv)
            {
                float x = uv.x * width - 0.5f, y = uv.y * height - 0.5f;
                int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
                float fx = x - ix, fy = y - iy;
                return Color.LerpUnclamped(Color.LerpUnclamped(At(ix, iy), At(ix + 1, iy), fx),
                    Color.LerpUnclamped(At(ix, iy + 1), At(ix + 1, iy + 1), fx), fy);
            }
            Color At(int x, int y)
            {
                int index = Wrap(y, height, wrapV) * width + Wrap(x, width, wrapU);
                Color c = hdrPixels != null ? hdrPixels[index] : (Color)pixels[index];
                return srgb ? c.linear : c;
            }
            static int Wrap(int x, int size, TextureWrapMode mode)
            {
                if (mode == TextureWrapMode.Clamp) return Mathf.Clamp(x, 0, size - 1);
                if (mode == TextureWrapMode.MirrorOnce) return Mathf.Clamp(x < 0 ? -x - 1 : x, 0, size - 1);
                int period = mode == TextureWrapMode.Mirror ? size * 2 : size;
                int v = ((x % period) + period) % period;
                return v < size ? v : period - 1 - v;
            }
        }
        internal sealed class Map
        {
            public Image image;
            public Vector2 scale = Vector2.one, offset;
            public Color Sample(Vector2 uv, Color fallback) => image == null ? fallback : image.Sample(Vector2.Scale(uv, scale) + offset);
        }
        internal sealed class Surface
        {
            public Map color, normal, metal, ao, emission;
            public Color tint, emissionTint;
            public float metallic, smoothness, normalScale, aoStrength;
            public bool smoothnessFromAlbedo;
        }

        /// <summary>The whole subtree under root welded into one snapshot in root-local space.</summary>
        public static RemeshSource Capture(GameObject root, bool lod0Only)
        {
            if (!root) throw new ArgumentException("Select a source root.");
            return Capture(root.transform.worldToLocalMatrix, CollectRenderers(root, lod0Only));
        }

        /// <summary>
        /// Snapshot of the given renderers expressed in the space whose world→local
        /// matrix is worldToSpace. Returns null when they contribute no triangles and
        /// required is false; throws otherwise.
        /// </summary>
        public static RemeshSource Capture(Matrix4x4 worldToSpace, IList<Renderer> renderers, bool required = true)
        {
            var positions = new List<Vector3>(); var normals = new List<Vector3>();
            var tangents = new List<Vector4>(); var uv = new List<Vector2>(); var colors = new List<Color>();
            var uv2 = new List<Vector2>();
            bool hasColors = false;
            var indices = new List<int>(); var faces = new List<int>(); var materials = new List<Surface>();
            var materialIds = new Dictionary<Material, int>();
            var faceLightmaps = new List<int>();
            var lightmapRefs = new List<LightmapRef>();
            var lightmapIds = new Dictionary<(Texture2D, Texture2D, Vector4), int>();
            var vertexRenderer = new List<int>(); var rendererToSpace = new List<Matrix4x4>();
            string[] warnings;
            using (var reader = new Reader()) {
                foreach (var renderer in renderers) {
                    // One hostile mesh (line submeshes, no UV0, an unreadable import)
                    // must cost its own exclusion, never the whole scene-block capture:
                    // every renderer is isolated, and recoverable offenders degrade to
                    // a warning + skip.
                    Mesh mesh = null;
                    try {
                        // A skinned source is baked at its current pose into a fresh runtime
                        // mesh. BakeMesh bakes the LAST EVALUATED skinning, which in edit mode
                        // can predate the current bone transforms — every part then lands at
                        // its authored origin instead of its posed place, so the bone list is
                        // reassigned first to mark the skinning dirty and force a re-evaluation.
                        // The result stays in the renderer's local space with no transform
                        // scale, and the shared path below applies the transform exactly once,
                        // like a static mesh under the same node.
                        if (renderer is SkinnedMeshRenderer skin) {
                            mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                            var bones = skin.bones;
                            skin.bones = Array.Empty<Transform>();
                            skin.bones = bones;
                            skin.BakeMesh(mesh);
                            if (skin.sharedMesh) mesh.name = skin.sharedMesh.name;
                        }
                        else if (renderer is MeshRenderer) {
                            var filter = renderer.GetComponent<MeshFilter>();
                            if (!filter || !filter.sharedMesh) continue;
                            // An Instantiate clone of a Read/Write-disabled import carries no CPU data,
                            // but editor code can still read the imported asset; copy from that.
                            mesh = UvCanvasView.MakeReadableCopy(filter.sharedMesh);
                        }
                        else continue;
                        // Non-triangle submeshes (curtain lines, quad exports) drop out of the
                        // capture; the renderer survives on its triangle submeshes alone and is
                        // skipped with a warning when none remain.
                        int triangleSubmeshes = 0;
                        for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                            if (mesh.GetTopology(sub) == MeshTopology.Triangles) ++triangleSubmeshes;
                        if (triangleSubmeshes == 0) {
                            reader.warnings.Add(renderer.name + ": no triangle submeshes, skipped.");
                            continue;
                        }
                        if (triangleSubmeshes < mesh.subMeshCount)
                            reader.warnings.Add(renderer.name + ": " + (mesh.subMeshCount - triangleSubmeshes) + " non-triangle submesh(es) skipped.");
                        if (mesh.uv.Length != mesh.vertexCount) {
                            reader.warnings.Add(renderer.name + ": no source UV0 for material transfer, skipped.");
                            continue;
                        }
                        if (mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
                        if (mesh.tangents.Length != mesh.vertexCount) mesh.RecalculateTangents();
                        var p = mesh.vertices; var n = mesh.normals; var t = mesh.tangents;
                        if (t.Length != p.Length) throw new InvalidOperationException(renderer.name + " has no valid tangent frame.");
                        var transform = worldToSpace * renderer.localToWorldMatrix;
                        if (Mathf.Abs(transform.determinant) < 1e-12f) throw new InvalidOperationException("Zero-scale source transform.");
                        var normalTransform = transform.inverse.transpose;
                        float sign = transform.determinant < 0 ? -1 : 1;
                        int first = positions.Count;
                        int rendererId = rendererToSpace.Count;
                        rendererToSpace.Add(transform);
                        for (int i = 0; i < p.Length; ++i) {
                            vertexRenderer.Add(rendererId);
                            positions.Add(transform.MultiplyPoint3x4(p[i]));
                            Vector3 nn = normalTransform.MultiplyVector(n[i]).normalized;
                            normals.Add(nn);
                            Vector3 tt = transform.MultiplyVector(new Vector3(t[i].x, t[i].y, t[i].z));
                            tt = (tt - nn * Vector3.Dot(nn, tt)).normalized;
                            tangents.Add(new Vector4(tt.x, tt.y, tt.z, t[i].w * sign));
                        }
                        uv.AddRange(mesh.uv);
                        // Beauty bakes sample the renderer's baked lightmap at its UV2;
                        // only the reference is kept here (see ReadLightmaps).
                        var lmUv = mesh.uv2;
                        if (lmUv.Length == p.Length) uv2.AddRange(lmUv);
                        else for (int i = 0; i < p.Length; ++i) uv2.Add(Vector2.zero);
                        int lightmapId = -1;
                        int lightmapIndex = renderer.lightmapIndex;
                        if (lightmapIndex >= 0 && lightmapIndex < LightmapSettings.lightmaps.Length) {
                            var data = LightmapSettings.lightmaps[lightmapIndex];
                            var map = data.lightmapColor;
                            var st = renderer.lightmapScaleOffset;
                            if (map != null) {
                                var key = (map, data.lightmapDir, st);
                                if (!lightmapIds.TryGetValue(key, out lightmapId)) {
                                    lightmapId = lightmapRefs.Count;
                                    lightmapRefs.Add(new LightmapRef { color = map, direction = data.lightmapDir, scaleOffset = st });
                                    lightmapIds.Add(key, lightmapId);
                                }
                            }
                        }
                        var c = mesh.colors;
                        if (c.Length == p.Length) { colors.AddRange(c); hasColors = true; }
                        else for (int i = 0; i < p.Length; ++i) colors.Add(Color.white);
                        var shared = renderer.sharedMaterials;
                        for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
                            if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                            if (sub >= shared.Length || !shared[sub]) throw new InvalidOperationException(renderer.name + " has a missing material.");
                            if (!materialIds.TryGetValue(shared[sub], out int material)) {
                                material = materials.Count;
                                materials.Add(reader.Capture(shared[sub])); materialIds.Add(shared[sub], material);
                            }
                            var tri = mesh.GetTriangles(sub);
                            for (int i = 0; i < tri.Length; i += 3) {
                                indices.Add(first + tri[i]);
                                indices.Add(first + tri[i + (sign < 0 ? 2 : 1)]);
                                indices.Add(first + tri[i + (sign < 0 ? 1 : 2)]);
                                faces.Add(material);
                                faceLightmaps.Add(lightmapId);
                            }
                        }
                    }
                    catch (InvalidOperationException e) {
                        reader.warnings.Add(renderer.name + ": " + e.Message + " Skipped.");
                    }
                    finally { if (mesh) Object.DestroyImmediate(mesh); }
                }
                warnings = reader.warnings.ToArray();
            }
            if (indices.Count == 0) {
                if (!required) return null;
                throw new InvalidOperationException("No source triangles found.");
            }
            var bounds = new Bounds(positions[0], Vector3.zero);
            foreach (var p in positions) bounds.Encapsulate(p);
            if (bounds.size.magnitude <= 1e-8f) throw new InvalidOperationException("Source bounds are empty.");
            return new RemeshSource { positions = positions.ToArray(), normals = normals.ToArray(), tangents = tangents.ToArray(),
                uv = uv.ToArray(), uv2 = uv2.ToArray(), colors = colors.ToArray(), hasColors = hasColors, indices = indices.ToArray(), faceMaterials = faces.ToArray(),
                faceLightmaps = faceLightmaps.ToArray(), lightmapRefs = lightmapRefs.ToArray(), materials = materials.ToArray(),
                diagonal = bounds.size.magnitude, warnings = warnings,
                vertexRenderer = vertexRenderer.ToArray(), rendererToSpace = rendererToSpace.ToArray() };
        }

        /// <summary>
        /// Drops connected pieces that are too small or too rod-like to matter: a piece
        /// whose extent is under minSize × capture diagonal, or whose CROSS-SECTION — the
        /// two smaller of its three extents along its own principal axes — is under
        /// minRodVoxels voxel cells (a cell = the capture's largest side / resolution,
        /// the grid the voxel remesh runs on). Pipes, cables, railings and bolts go; a
        /// sheet of any thickness (a gate leaf, a glass pane, a decal) is one thin
        /// extent, not two, and stays. 0 disables either test. Pieces are the connected
        /// components of the position-welded triangle graph, so a pipe goes even when it
        /// shares a mesh with the wall. Vertices are compacted; the capture diagonal is
        /// kept so later fractions stay relative to the original model. Returns false
        /// (and changes nothing) when every piece would go.
        /// </summary>
        public bool FilterSmallParts(float minSize, float minRodVoxels, int resolution, out int removedSmall, out int removedThin)
        {
            removedSmall = removedThin = 0;
            if (minSize <= 0 && minRodVoxels <= 0) return true;
            int vertexCount = positions.Length, faceCount = indices.Length / 3;
            if (vertexCount == 0) return true;
            Vector3 boundsMin = positions[0], boundsMax = positions[0];
            foreach (var p in positions) { boundsMin = Vector3.Min(boundsMin, p); boundsMax = Vector3.Max(boundsMax, p); }
            var extent = boundsMax - boundsMin;
            float cell = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z)) / Mathf.Max(1, resolution);
            float rodLimit = minRodVoxels * cell;
            // Weld by exact position so split-normal / UV-seam duplicates join their piece.
            var slot = new int[vertexCount];
            var slots = new Dictionary<(int, int, int), int>(vertexCount);
            var unique = new List<Vector3>(vertexCount);
            for (int i = 0; i < vertexCount; ++i) {
                var p = positions[i];
                var key = (BitConverter.SingleToInt32Bits(p.x), BitConverter.SingleToInt32Bits(p.y), BitConverter.SingleToInt32Bits(p.z));
                if (!slots.TryGetValue(key, out int id)) { id = slots.Count; slots[key] = id; unique.Add(p); }
                slot[i] = id;
            }
            var parent = new int[slots.Count];
            for (int i = 0; i < parent.Length; ++i) parent[i] = i;
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[a] = b; }
            for (int f = 0; f < faceCount; ++f) {
                Union(slot[indices[f * 3]], slot[indices[f * 3 + 1]]);
                Union(slot[indices[f * 3]], slot[indices[f * 3 + 2]]);
            }
            var members = new Dictionary<int, List<Vector3>>();
            for (int i = 0; i < unique.Count; ++i) {
                int root = Find(i);
                if (!members.TryGetValue(root, out var list)) members[root] = list = new List<Vector3>();
                list.Add(unique[i]);
            }
            var drop = new HashSet<int>();
            foreach (var pair in members) {
                var extents = PrincipalExtents(pair.Value);  // descending
                bool small = minSize > 0 && extents.magnitude < minSize * diagonal;
                bool rod = !small && rodLimit > 0 && extents.y < rodLimit;
                if (small) ++removedSmall; else if (rod) ++removedThin;
                if (small || rod) drop.Add(pair.Key);
            }
            if (drop.Count == 0) return true;
            if (drop.Count == members.Count) { removedSmall = removedThin = 0; return false; }
            // Compact faces, then vertices.
            var keptFaces = new List<int>(faceCount);
            for (int f = 0; f < faceCount; ++f)
                if (!drop.Contains(Find(slot[indices[f * 3]]))) keptFaces.Add(f);
            var remap = new int[vertexCount];
            for (int i = 0; i < vertexCount; ++i) remap[i] = -1;
            int next = 0;
            var newIndices = new int[keptFaces.Count * 3];
            var newFaceMaterials = new int[keptFaces.Count];
            var newFaceLightmaps = new int[keptFaces.Count];
            for (int k = 0; k < keptFaces.Count; ++k) {
                int f = keptFaces[k];
                for (int c = 0; c < 3; ++c) {
                    int v = indices[f * 3 + c];
                    if (remap[v] < 0) remap[v] = next++;
                    newIndices[k * 3 + c] = remap[v];
                }
                newFaceMaterials[k] = faceMaterials[f];
                newFaceLightmaps[k] = faceLightmaps[f];
            }
            T[] Compact<T>(T[] source) {
                if (source == null) return null;
                var result = new T[next];
                for (int i = 0; i < vertexCount; ++i) if (remap[i] >= 0) result[remap[i]] = source[i];
                return result;
            }
            positions = Compact(positions); normals = Compact(normals); tangents = Compact(tangents);
            uv = Compact(uv); uv2 = Compact(uv2); colors = Compact(colors); vertexRenderer = Compact(vertexRenderer);
            indices = newIndices; faceMaterials = newFaceMaterials; faceLightmaps = newFaceLightmaps;
            return true;
        }

        /// <summary>
        /// The extents of a point set along its own principal axes (covariance
        /// eigenvectors), largest first — a pipe lying diagonally reads as long × thin ×
        /// thin here where its axis-aligned bounds would read as fat.
        /// </summary>
        internal static Vector3 PrincipalExtents(List<Vector3> points)
        {
            int n = points.Count;
            if (n == 0) return Vector3.zero;
            Vector3 mean = Vector3.zero;
            foreach (var p in points) mean += p;
            mean /= n;
            // Covariance (symmetric 3×3), then Jacobi rotations to diagonalize it.
            double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
            foreach (var p in points) {
                double dx = p.x - mean.x, dy = p.y - mean.y, dz = p.z - mean.z;
                xx += dx * dx; yy += dy * dy; zz += dz * dz; xy += dx * dy; xz += dx * dz; yz += dy * dz;
            }
            var a = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 32; ++sweep) {
                double off = a[0, 1] * a[0, 1] + a[0, 2] * a[0, 2] + a[1, 2] * a[1, 2];
                if (off < 1e-24) break;
                for (int p = 0; p < 2; ++p)
                    for (int q = p + 1; q < 3; ++q) {
                        if (Math.Abs(a[p, q]) < 1e-30) continue;
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0) t = 1;
                        double c = 1 / Math.Sqrt(t * t + 1), sn = t * c;
                        for (int k = 0; k < 3; ++k) {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - sn * akq; a[k, q] = sn * akp + c * akq;
                        }
                        for (int k = 0; k < 3; ++k) {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - sn * aqk; a[q, k] = sn * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; ++k) {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - sn * vkq; v[k, q] = sn * vkp + c * vkq;
                        }
                    }
            }
            var extents = new float[3];
            for (int axis = 0; axis < 3; ++axis) {
                var dir = new Vector3((float)v[0, axis], (float)v[1, axis], (float)v[2, axis]);
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (var p in points) { float d = Vector3.Dot(p - mean, dir); if (d < lo) lo = d; if (d > hi) hi = d; }
                extents[axis] = hi - lo;
            }
            Array.Sort(extents);
            return new Vector3(extents[2], extents[1], extents[0]);
        }

        /// <summary>
        /// One box per captured renderer, measured over that renderer's (filtered)
        /// vertices along the renderer's own axes and placed back in capture space —
        /// the authored bounding box carried by the transform, not an axis-aligned box
        /// in the root's frame. Flat renderers get a minimal thickness. All boxes merged
        /// into one outward-wound mesh.
        /// </summary>
        public RemeshNative.IndexedMesh OrientedBoxes()
        {
            int renderers = rendererToSpace.Length;
            var mins = new Vector3[renderers]; var maxs = new Vector3[renderers]; var any = new bool[renderers];
            var toLocal = new Matrix4x4[renderers];
            for (int r = 0; r < renderers; ++r) toLocal[r] = rendererToSpace[r].inverse;
            for (int i = 0; i < positions.Length; ++i) {
                int r = vertexRenderer[i];
                var local = toLocal[r].MultiplyPoint3x4(positions[i]);
                if (!any[r]) { any[r] = true; mins[r] = local; maxs[r] = local; }
                else { mins[r] = Vector3.Min(mins[r], local); maxs[r] = Vector3.Max(maxs[r], local); }
            }
            var vertices = new List<Vector3>(); var tris = new List<int>();
            int[] faces = { 0,2,1, 0,3,2, 4,5,6, 4,6,7, 0,1,5, 0,5,4, 3,7,6, 3,6,2, 0,4,7, 0,7,3, 1,2,6, 1,6,5 };
            for (int r = 0; r < renderers; ++r) {
                if (!any[r]) continue;
                Vector3 mn = mins[r], mx = maxs[r];
                // A flat renderer (plaza, road sheet, decal) would give a zero-thickness box:
                // degenerate side faces and two z-fighting sheets. Pad any axis under 0.4% of
                // the largest side to that, centred on the true extent.
                var size = mx - mn;
                float minSide = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.004f;
                for (int axis = 0; axis < 3; ++axis) {
                    if (size[axis] >= minSide) continue;
                    float centre = (mn[axis] + mx[axis]) * 0.5f;
                    mn[axis] = centre - minSide * 0.5f; mx[axis] = centre + minSide * 0.5f;
                }
                int baseIndex = vertices.Count;
                var m = rendererToSpace[r];
                vertices.Add(m.MultiplyPoint3x4(new Vector3(mn.x, mn.y, mn.z))); vertices.Add(m.MultiplyPoint3x4(new Vector3(mx.x, mn.y, mn.z)));
                vertices.Add(m.MultiplyPoint3x4(new Vector3(mx.x, mx.y, mn.z))); vertices.Add(m.MultiplyPoint3x4(new Vector3(mn.x, mx.y, mn.z)));
                vertices.Add(m.MultiplyPoint3x4(new Vector3(mn.x, mn.y, mx.z))); vertices.Add(m.MultiplyPoint3x4(new Vector3(mx.x, mn.y, mx.z)));
                vertices.Add(m.MultiplyPoint3x4(new Vector3(mx.x, mx.y, mx.z))); vertices.Add(m.MultiplyPoint3x4(new Vector3(mn.x, mx.y, mx.z)));
                bool mirrored = m.determinant < 0;
                for (int i = 0; i < 36; i += 3) {
                    tris.Add(baseIndex + faces[i]);
                    tris.Add(baseIndex + faces[mirrored ? i + 2 : i + 1]);
                    tris.Add(baseIndex + faces[mirrored ? i + 1 : i + 2]);
                }
            }
            if (tris.Count == 0) return null;
            return new RemeshNative.IndexedMesh { positions = vertices.ToArray(), indices = tris.ToArray() };
        }

        /// <summary>
        /// The renderers a capture of root contributes, in hierarchy order: enabled
        /// MeshRenderers with a mesh and SkinnedMeshRenderers, minus LODGroup levels above
        /// LOD0, collision nodes (`_COL`, `_COL_Hull{N}`, … on the node or the mesh asset)
        /// and, with lod0Only, `Name_LOD1`-and-higher names on either. The weld and the
        /// keep-hierarchy node list both come from here, so they agree by construction.
        /// </summary>
        internal static List<Renderer> CollectRenderers(GameObject root, bool lod0Only)
        {
            var excluded = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>()) {
                var lods = group.GetLODs();
                var first = new HashSet<Renderer>(lods.Length > 0 ? lods[0].renderers : Array.Empty<Renderer>());
                for (int l = 1; l < lods.Length; ++l)
                    foreach (var r in lods[l].renderers) if (!first.Contains(r)) excluded.Add(r);
            }
            var result = new List<Renderer>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
                if (!renderer.enabled || excluded.Contains(renderer)) continue;
                Mesh mesh;
                if (renderer is SkinnedMeshRenderer skin) mesh = skin.sharedMesh;
                else if (renderer is MeshRenderer && renderer.TryGetComponent<MeshFilter>(out var filter)) mesh = filter.sharedMesh;
                else continue;
                if (!mesh) continue;
                if (MeshHygieneUtility.IsCollisionNodeName(renderer.name) || MeshHygieneUtility.IsCollisionNodeName(mesh.name)) continue;
                if (lod0Only && (IsHigherLodName(renderer.name) || IsHigherLodName(mesh.name))) continue;
                result.Add(renderer);
            }
            return result;
        }

        // Repo LOD naming is Name_LOD{N} (see the LOD/collision naming rule); anything
        // above LOD0 is a coarser duplicate of what LOD0 already captures.
        internal static bool IsHigherLodName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int at = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (at < 0 || at + 4 >= name.Length) return false;
            int level = 0;
            for (int i = at + 4; i < name.Length; ++i) {
                if (name[i] < '0' || name[i] > '9') return false;
                level = level * 10 + (name[i] - '0');
            }
            return level > 0;
        }

        sealed class Reader : IDisposable
        {
            readonly Dictionary<(Texture, bool, bool, bool), Image> cache = new Dictionary<(Texture, bool, bool, bool), Image>();
            readonly Material blit;
            long bytes;
            public Reader()
            {
                var shader = Shader.Find("Hidden/MeshLab/RemeshReadback");
                if (!shader || !shader.isSupported) throw new InvalidOperationException("Remesh readback shader is unavailable.");
                blit = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            }
            public void Dispose() { Object.DestroyImmediate(blit); }
            public readonly List<string> warnings = new List<string>();
            public Surface Capture(Material m)
            {
                string shaderName = m.shader ? m.shader.name : "<missing shader>";
                bool urp = shaderName == "Universal Render Pipeline/Lit";
                if (!urp && shaderName != "Standard") return CaptureGeneric(m, shaderName);
                if ((urp && m.GetFloat("_Surface") != 0) || (!urp && m.GetFloat("_Mode") != 0) || m.IsKeywordEnabled("_ALPHATEST_ON"))
                    warnings.Add(m.name + ": transparency/alpha clipping is ignored; the result is opaque.");
                if (m.IsKeywordEnabled("_DETAIL_MULX2") || m.IsKeywordEnabled("_DETAIL_SCALED") || m.IsKeywordEnabled("_PARALLAXMAP"))
                    warnings.Add(m.name + ": detail and parallax layers are not baked.");
                bool specular = urp && m.HasProperty("_WorkflowMode") && m.GetFloat("_WorkflowMode") == 0;
                if (specular) warnings.Add(m.name + ": specular workflow is baked as non-metallic.");
                string color = urp ? "_BaseMap" : "_MainTex";
                bool metal = !specular && m.IsKeywordEnabled(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
                var surface = new Surface {
                    color = Read(m, color, true), normal = Read(m, "_BumpMap", false, true, m.IsKeywordEnabled("_NORMALMAP")),
                    metal = Read(m, "_MetallicGlossMap", false, false, metal), ao = Read(m, "_OcclusionMap", false),
                    emission = Read(m, "_EmissionMap", true, false, m.IsKeywordEnabled("_EMISSION"), true),
                    tint = m.GetColor(urp ? "_BaseColor" : "_Color").linear,
                    emissionTint = m.IsKeywordEnabled("_EMISSION") ? m.GetColor("_EmissionColor").linear : Color.black,
                    metallic = specular ? 0 : m.GetFloat("_Metallic"),
                    smoothness = m.GetFloat(urp ? "_Smoothness" : (metal || m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A") ? "_GlossMapScale" : "_Glossiness")),
                    normalScale = m.GetFloat("_BumpScale"), aoStrength = m.GetFloat("_OcclusionStrength"),
                    smoothnessFromAlbedo = m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")
                };
                ShareBaseTransform(surface);
                return surface;
            }

            // Any other shader: bake what the common property names expose (base colour,
            // tangent-space normal, scalar metallic/smoothness, occlusion, emission) and
            // say so, instead of refusing the whole model.
            Surface CaptureGeneric(Material m, string shaderName)
            {
                warnings.Add(m.name + ": shader '" + shaderName + "' is not Standard or URP/Lit; baking base colour, normal, occlusion and emission from common property names.");
                string color = First(m, "_BaseMap", "_MainTex", "_BaseColorMap", "_AlbedoMap", "_Albedo");
                string normal = First(m, "_BumpMap", "_NormalMap");
                bool emissive = m.HasProperty("_EmissionColor") && (m.IsKeywordEnabled("_EMISSION") || m.HasProperty("_EmissionMap") && m.GetTexture("_EmissionMap"));
                var surface = new Surface {
                    color = color != null ? Read(m, color, true) : new Map(),
                    normal = normal != null ? Read(m, normal, false, true) : new Map(),
                    metal = new Map(), ao = Read(m, "_OcclusionMap", false),
                    emission = emissive ? Read(m, "_EmissionMap", true, false, true, true) : new Map(),
                    tint = ColorOr(m, Color.white, "_BaseColor", "_Color").linear,
                    emissionTint = emissive ? m.GetColor("_EmissionColor").linear : Color.black,
                    metallic = FloatOr(m, 0, "_Metallic"), smoothness = FloatOr(m, 0.5f, "_Smoothness", "_Glossiness"),
                    normalScale = FloatOr(m, 1, "_BumpScale", "_NormalScale"), aoStrength = FloatOr(m, 1, "_OcclusionStrength")
                };
                ShareBaseTransform(surface);
                return surface;
            }

            // Standard and URP/Lit sample these maps with the base UV transform.
            static void ShareBaseTransform(Surface surface)
            {
                foreach (var map in new[] { surface.normal, surface.metal, surface.ao, surface.emission }) {
                    map.scale = surface.color.scale; map.offset = surface.color.offset;
                }
            }
            static string First(Material m, params string[] names)
            {
                foreach (var name in names) if (m.HasProperty(name) && m.GetTexture(name)) return name;
                foreach (var name in names) if (m.HasProperty(name)) return name;
                return null;
            }
            static Color ColorOr(Material m, Color fallback, params string[] names)
            {
                foreach (var name in names) if (m.HasProperty(name)) return m.GetColor(name);
                return fallback;
            }
            static float FloatOr(Material m, float fallback, params string[] names)
            {
                foreach (var name in names) if (m.HasProperty(name)) return m.GetFloat(name);
                return fallback;
            }
            Map Read(Material material, string property, bool color, bool normal = false, bool enabled = true, bool hdr = false)
            {
                var map = new Map();
                var texture = enabled && material.HasProperty(property) ? material.GetTexture(property) : null;
                if (material.HasProperty(property)) { map.scale = material.GetTextureScale(property); map.offset = material.GetTextureOffset(property); }
                if (!texture) return map;
                if (!(texture is Texture2D)) {
                    warnings.Add(material.name + "." + property + ": only 2D textures are baked; '" + texture.name + "' is skipped.");
                    return map;
                }
                map.scale = material.GetTextureScale(property); map.offset = material.GetTextureOffset(property);
                var key = (texture, color, normal, hdr);
                if (!cache.TryGetValue(key, out var image)) {
                    bytes += (long)texture.width * texture.height * (hdr ? 16 : 4);
                    if (bytes > 512L * 1024 * 1024) throw new InvalidOperationException("Source texture readback exceeds 512 MiB. Process the model in smaller groups.");
                    var previous = RenderTexture.active;
                    var rt = RenderTexture.GetTemporary(texture.width, texture.height, 0, hdr ? RenderTextureFormat.ARGBFloat : RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                    Texture2D copy = null;
                    try {
                        blit.SetFloat("_HDR", hdr ? 1 : 0);
                        blit.SetFloat("_DecodeNormal", normal ? 1 : 0); blit.SetFloat("_ColorMap", color ? 1 : 0);
                        Graphics.Blit(texture, rt, blit);
                        RenderTexture.active = rt;
                        copy = new Texture2D(texture.width, texture.height, hdr ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, true);
                        copy.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0); copy.Apply();
                        image = new Image { pixels = hdr ? null : copy.GetPixels32(), hdrPixels = hdr ? copy.GetPixels() : null, srgb = color && !hdr, width = texture.width, height = texture.height,
                            wrapU = texture.wrapModeU, wrapV = texture.wrapModeV };
                        cache.Add(key, image);
                    }
                    finally {
                        RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
                        if (copy) Object.DestroyImmediate(copy);
                    }
                }
                map.image = image;
                return map;
            }
        }
    }
}
