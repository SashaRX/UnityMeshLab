// FbxStructureWrite.cs — the geometry a save adds to or replaces in an FBX beyond
// per-vertex channels: generated LODs, collision from the sidecar, and meshes whose faces
// MeshLab changed. It is written into the same FBX document as the channel edits
// (FbxChannelWrite), through the FBX SDK, without re-exporting the file:
//
//   • a generated LOD becomes a node next to the node it was generated from, with that
//     node's transform and materials, named so Unity groups it with its siblings;
//   • collision becomes `{key}_COL` (or a `{key}_COL` container of `_COL_Hull{i}` nodes)
//     next to its source node; one already in the file with the same geometry is left alone;
//   • a mesh with changed faces gets a new FBX mesh on the same node(s).
//
// A node is removed only when a new one takes its name; nothing is renamed, moved or
// normalised (that stays the explicit Prefab Builder action). Unity-space geometry goes
// back into control-point space through the map fitted to the source mesh's tagged import
// (FbxSpaceFit), so it lands exactly where Unity shows it, whatever the import settings.
// What cannot be placed that way is refused with the reason, and the file is not written.

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using FbxMesh = Autodesk.Fbx.FbxMesh;
using FbxNode = Autodesk.Fbx.FbxNode;
using FbxScene = Autodesk.Fbx.FbxScene;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>A structure change the FBX document cannot take as asked; the file is left as it was.</summary>
    internal sealed class FbxStructureRefusal : InvalidOperationException
    {
        public FbxStructureRefusal(string message) : base(message) { }
    }

    /// <summary>What a save adds to or replaces in one FBX beyond per-vertex channels.</summary>
    internal sealed class FbxStructurePlan : IDisposable
    {
        internal sealed class NewLod
        {
            public string name, sourceName;
            public Mesh mesh;
        }

        internal readonly List<NewLod> lods = new List<NewLod>();
        internal readonly List<(string name, Mesh mesh)> reshaped = new List<(string, Mesh)>();
        /// <summary>From the sidecar; the meshes are owned by the plan.</summary>
        internal readonly List<(string key, List<Mesh> meshes, bool convex)> collisions = new List<(string, List<Mesh>, bool)>();
        internal readonly List<string> refusals = new List<string>();

        public bool IsEmpty => lods.Count == 0 && reshaped.Count == 0 && collisions.Count == 0 && refusals.Count == 0;

        /// <summary>Meshes written whole by the structure step; the channel step leaves them out.</summary>
        internal ICollection<string> WholeMeshNames => new HashSet<string>(lods.Select(l => l.name).Concat(reshaped.Select(r => r.name)), StringComparer.Ordinal);

        public void Dispose()
        {
            foreach (var (_, meshes, _) in collisions)
                foreach (var mesh in meshes)
                    if (mesh != null) Object.DestroyImmediate(mesh);
            collisions.Clear();
        }
    }

    internal static class FbxStructureWrite
    {
        // ── Plan (Unity side, before anything is imported) ──

        /// <summary>
        /// The structure changes of one FBX group: entries whose mesh the file does not have
        /// (generated LODs, paired with the source-LOD mesh they were generated from), FBX
        /// meshes whose faces or points changed, and the sidecar's collision.
        /// </summary>
        internal static FbxStructurePlan Plan(string fbxPath, List<(MeshEntry entry, Mesh resultMesh)> group, int sourceLodIndex)
        {
            var plan = new FbxStructurePlan();
            bool InFile(MeshEntry e) => e.fbxMesh != null
                && string.Equals(AssetDatabase.GetAssetPath(e.fbxMesh), fbxPath, StringComparison.OrdinalIgnoreCase);
            var sources = group.Where(p => InFile(p.entry) && p.entry.lodIndex == sourceLodIndex).Select(p => p.entry.fbxMesh.name).ToList();
            foreach (var (entry, result) in group)
            {
                if (InFile(entry))
                {
                    if (GeometryDiffers(entry.fbxMesh, result)) plan.reshaped.Add((entry.fbxMesh.name, result));
                    continue;
                }
                string name = FbxExport.ResolveExportMeshName(entry, result);
                // Generated LODs are named after their source: base (pipeline suffixes and LOD stripped) + _LOD{n}.
                string stem = MeshNaming.StripPipelineSuffixes(name);
                var from = sources.Where(n => MeshNaming.StripPipelineSuffixes(n) == stem).Distinct().ToList();
                if (from.Count == 1) plan.lods.Add(new FbxStructurePlan.NewLod { name = name, sourceName = from[0], mesh = result });
                else plan.refusals.Add($"'{name}' cannot be paired with the LOD{sourceLodIndex} mesh of '{System.IO.Path.GetFileName(fbxPath)}' it was generated from");
            }
            plan.collisions.AddRange(SidecarStore.CollisionMeshes(fbxPath));
            return plan;
        }

        // Faces differ from the import: per submesh, the same faces (as position loops, any
        // starting corner, same winding) in any order and any vertex numbering. Vertex dedup,
        // UV welds and symmetry splits keep that and are channel work, not geometry; a
        // re-triangulated quad, a flipped or moved face, or a submesh change is geometry.
        internal static bool GeometryDiffers(Mesh imported, Mesh result)
        {
            if (result == null || result == imported || !imported.isReadable || !result.isReadable) return false;
            if (imported.subMeshCount != result.subMeshCount) return true;
            var importedVertices = imported.vertices;
            var resultVertices = result.vertices;
            for (int s = 0; s < imported.subMeshCount; s++)
            {
                if (imported.GetTopology(s) != result.GetTopology(s)) return true;
                int size = imported.GetTopology(s) == MeshTopology.Quads ? 4 : 3;
                if (!SameFaces(importedVertices, imported.GetIndices(s), resultVertices, result.GetIndices(s), size)) return true;
            }
            return false;
        }

        static bool SameFaces(Vector3[] aVertices, int[] aIndices, Vector3[] bVertices, int[] bIndices, int size)
        {
            if (aIndices.Length != bIndices.Length) return false;
            var counts = new Dictionary<(Vector3, Vector3, Vector3, Vector3), int>();
            for (int f = 0; f + size <= aIndices.Length; f += size)
            {
                var key = FaceLoop(aVertices, aIndices, f, size);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
            for (int f = 0; f + size <= bIndices.Length; f += size)
            {
                var key = FaceLoop(bVertices, bIndices, f, size);
                if (!counts.TryGetValue(key, out int n) || n == 0) return false;
                counts[key] = n - 1;
            }
            return true;
        }

        // The face's positions in winding order, started at the rotation that is smallest.
        static (Vector3, Vector3, Vector3, Vector3) FaceLoop(Vector3[] vertices, int[] indices, int start, int size)
        {
            int best = 0;
            for (int r = 1; r < size; r++)
                if (CompareRotations(vertices, indices, start, size, r, best) < 0) best = r;
            Vector3 At(int k) => k < size ? vertices[indices[start + (best + k) % size]] : default;
            return (At(0), At(1), At(2), At(3));
        }

        static int CompareRotations(Vector3[] vertices, int[] indices, int start, int size, int r1, int r2)
        {
            for (int k = 0; k < size; k++)
            {
                var a = vertices[indices[start + (r1 + k) % size]];
                var b = vertices[indices[start + (r2 + k) % size]];
                int c = a.x.CompareTo(b.x);
                if (c == 0) c = a.y.CompareTo(b.y);
                if (c == 0) c = a.z.CompareTo(b.z);
                if (c != 0) return c;
            }
            return 0;
        }

        // ── Apply (FBX document, after the channel edits) ──

        // A source mesh as the document and its tagged import know it.
        sealed class Reference
        {
            public string name;
            public FbxMesh mesh;
            public FbxNode node;
            public FbxSpaceFit fit;
            public bool reverse;
            /// <summary>Node material index per Unity submesh; null when the node has no materials.</summary>
            public int[] submeshMaterials;
            public int uvSets;
            public bool colors;
        }

        /// <summary>
        /// Writes <paramref name="plan"/> into <paramref name="document"/>; returns the number of
        /// nodes added or meshes replaced, with one line per change in <paramref name="log"/>.
        /// Throws <see cref="FbxStructureRefusal"/> for what cannot be placed.
        /// </summary>
        internal static int Apply(FbxSourceDocument document, Dictionary<string, FbxChannelWrite.Tagged> tagged,
            FbxStructurePlan plan, bool swapUv, bool preserveHierarchy, int minUvSets, List<string> log)
        {
            if (plan.refusals.Count > 0) throw new FbxStructureRefusal(plan.refusals[0]);
            var scene = document.Scene;
            var references = new Dictionary<string, Reference>(StringComparer.Ordinal);
            Reference Ref(string name)
            {
                if (!references.TryGetValue(name, out var r)) references[name] = r = Resolve(document, tagged, name);
                return r;
            }

            // Every source is resolved and checked before the document changes: a mesh being
            // replaced may also be the source of a LOD or a collider.
            foreach (var lod in plan.lods) RequireLodSibling(scene, Ref(lod.sourceName), lod.name, preserveHierarchy);
            var collisions = plan.collisions.Where(c => !CollisionUnchanged(c, tagged)).ToList();
            foreach (var (key, _, _) in collisions) RequireSiblingRoom(scene, Ref(key), preserveHierarchy);
            foreach (var (name, _) in plan.reshaped) Ref(name);

            int changes = 0;
            foreach (var lod in plan.lods)
            {
                var r = Ref(lod.sourceName);
                int removed = RemoveNamed(scene, lod.name);
                var data = FbxMeshData.FromTriangles(SourceOf(lod.mesh, r, swapUv, minUvSets), r.fit, r.reverse, r.submeshMaterials);
                var node = FbxStructureEdit.AddSibling(r.node, lod.name, true);
                FbxStructureEdit.AddMaterials(node, r.node, Enumerable.Range(0, r.node.GetMaterialCount()));
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, lod.name, data));
                log.Add($"'{lod.name}': {data.polygonSizes.Length} triangle(s) next to '{r.node.GetName()}'{(removed > 0 ? " (replacing the node of that name)" : "")}");
                changes++;
            }
            foreach (var collision in collisions)
            {
                changes += WriteCollision(scene, Ref(collision.key), collision, log);
            }
            foreach (var (name, mesh) in plan.reshaped)
            {
                var r = Ref(name);
                var data = FbxMeshData.FromTriangles(SourceOf(mesh, r, swapUv, minUvSets), r.fit, r.reverse, r.submeshMaterials);
                int nodes = FbxStructureEdit.ReplaceMesh(r.mesh, FbxStructureEdit.CreateMesh(scene, r.mesh.GetName(), data));
                log.Add($"'{name}': geometry replaced ({data.polygonSizes.Length} triangle(s), {nodes} node(s); node, transform and materials kept)");
                changes++;
            }
            return changes;
        }

        static Reference Resolve(FbxSourceDocument document, Dictionary<string, FbxChannelWrite.Tagged> tagged, string name)
        {
            if (!tagged.TryGetValue(name, out var tag))
                throw new FbxStructureRefusal($"'{name}' is not a mesh of the FBX (or its corner tags did not survive the import)");
            var mesh = document.Meshes[tag.ordinal];
            if (FbxStructureEdit.HasDeformers(mesh))
                throw new FbxStructureRefusal($"'{name}' is skinned or has blend shapes; geometry made from it cannot be placed through its import");
            var node = FbxStructureEdit.NodeOf(mesh, name)
                ?? throw new FbxStructureRefusal($"'{name}' is instanced by several nodes and none is named after it");

            var topology = new FbxLayerChannels.Topology(mesh);
            int relation = FbxSpaceFit.WindingRelation(topology.polygonSizes, tag.submeshCorners.SelectMany(c => c).ToArray());
            // No triangle tells (all degenerate): Unity mirrors X on import and reverses the winding.
            if (relation == 0) relation = -1;
            var fit = FbxSpaceFit.FitCorners(FbxStructureEdit.ControlPoints(mesh), topology.cornerControlPoint, tag.cornerPositions, relation, out string error)
                ?? throw new FbxStructureRefusal($"'{name}': {error}");

            int[] submeshMaterials = null;
            if (node.GetMaterialCount() > 0)
            {
                var polygonMaterials = FbxStructureEdit.PolygonMaterials(mesh);
                submeshMaterials = new int[Math.Max(1, tag.submeshCorners.Length)];
                for (int s = 0; s < tag.submeshCorners.Length; s++)
                {
                    var corners = tag.submeshCorners[s];
                    int at = Array.FindIndex(corners, c => c >= 0 && c < topology.CornerCount);
                    int material = at >= 0 ? polygonMaterials[topology.cornerPolygon[corners[at]]] : -1;
                    submeshMaterials[s] = material >= 0 && material < node.GetMaterialCount() ? material : 0;
                }
            }
            return new Reference
            {
                name = name, mesh = mesh, node = node, fit = fit, reverse = relation < 0, submeshMaterials = submeshMaterials,
                uvSets = FbxLayerChannels.UvElements(mesh).Count, colors = FbxLayerChannels.ColorElement(mesh) != null,
            };
        }

        static void RequireLodSibling(FbxScene scene, Reference source, string name, bool preserveHierarchy)
        {
            string sourceNode = source.node.GetName();
            if (!MeshNaming.TryParseLod(sourceNode, out string sourceBase, out _) || !MeshNaming.TryParseLod(name, out string newBase, out _)
                || !string.Equals(sourceBase, newBase, StringComparison.OrdinalIgnoreCase))
                throw new FbxStructureRefusal(
                    $"'{name}' would not be grouped with '{sourceNode}': Unity groups LODs by one base name with _LOD<N> suffixes, " +
                    "and renaming the source node is the explicit hierarchy normalisation (Prefab Builder)");
            RequireSiblingRoom(scene, source, preserveHierarchy);
        }

        static void RequireSiblingRoom(FbxScene scene, Reference source, bool preserveHierarchy)
        {
            if (!preserveHierarchy && FbxStructureEdit.IsSoleTopNode(scene, source.node))
                throw new FbxStructureRefusal(
                    $"'{source.node.GetName()}' is the FBX's only top-level node, which Unity imports as the model root; a node next to it " +
                    "would change the imported hierarchy (normalise it in Prefab Builder first)");
        }

        // Removes the nodes a new one replaces (same name); a node with children is not ours to drop.
        static int RemoveNamed(FbxScene scene, string name)
        {
            var nodes = FbxStructureEdit.FindNodes(scene, name);
            foreach (var node in nodes)
            {
                if (node.GetChildCount() > 0)
                    throw new FbxStructureRefusal($"'{name}' already exists with child nodes; replacing it would drop them");
                FbxStructureEdit.RemoveSubtree(node);
            }
            return nodes.Count;
        }

        static FbxMeshData.Source SourceOf(Mesh mesh, Reference reference, bool swapUv, int minUvSets)
        {
            var source = new FbxMeshData.Source
            {
                positions = Flatten(mesh.vertices),
                submeshTriangles = new int[mesh.subMeshCount][],
            };
            for (int s = 0; s < mesh.subMeshCount; s++)
                source.submeshTriangles[s] = mesh.GetTopology(s) == MeshTopology.Triangles ? mesh.GetTriangles(s) : null;
            var normals = mesh.normals;
            if (normals.Length == mesh.vertexCount) source.normals = Flatten(normals);

            // The UV sets the source FBX mesh has, no more: a channel Unity generates on import
            // (lightmap UVs) is not written into the file, unless UV1 is being baked.
            var list = new List<Vector2>();
            int sets = Math.Max(reference.uvSets, minUvSets);
            for (int set = 0; set < sets; set++)
            {
                int channel = FbxChannelWrite.FbxUvSet(set, swapUv);
                mesh.GetUVs(channel, list);
                if (list.Count != mesh.vertexCount)
                {
                    UvtLog.Warn($"[FBX Export] '{mesh.name}' has no UV{channel}; it gets {set} of the {sets} UV set(s) of '{reference.name}'.");
                    break;
                }
                var uv = new float[list.Count * 2];
                for (int v = 0; v < list.Count; v++) { uv[v * 2] = list[v].x; uv[v * 2 + 1] = list[v].y; }
                source.uvs.Add(uv);
            }
            var colors = mesh.colors;
            if (reference.colors && colors.Length == mesh.vertexCount)
            {
                source.colors = new float[colors.Length * 4];
                for (int v = 0; v < colors.Length; v++)
                {
                    source.colors[v * 4] = colors[v].r; source.colors[v * 4 + 1] = colors[v].g;
                    source.colors[v * 4 + 2] = colors[v].b; source.colors[v * 4 + 3] = colors[v].a;
                }
            }
            return source;
        }

        static float[] Flatten(Vector3[] vectors)
        {
            var result = new float[vectors.Length * 3];
            for (int i = 0; i < vectors.Length; i++) { result[i * 3] = vectors[i].x; result[i * 3 + 1] = vectors[i].y; result[i * 3 + 2] = vectors[i].z; }
            return result;
        }

        // ── Collision ──

        // The node layout FbxExport.InjectCollisionMeshes has always written: one `{key}_COL`
        // mesh node for a single simplified collider, else a `{key}_COL` container of hulls.
        static bool IsSingle((string key, List<Mesh> meshes, bool convex) collision) => !collision.convex && collision.meshes.Count == 1;

        static string HullName(string key, int hull) => $"{key}_COL_Hull{hull}";

        static int WriteCollision(FbxScene scene, Reference r, (string key, List<Mesh> meshes, bool convex) collision, List<string> log)
        {
            string name = collision.key + "_COL";
            int removed = FbxStructureEdit.FindNodes(scene, name).Count;
            foreach (var old in FbxStructureEdit.FindNodes(scene, name)) FbxStructureEdit.RemoveSubtree(old);
            // Colliders never render: positions, triangles and normals only, no material
            // (TS_UnityExport_SDK PIPELINE_RULES: no UV, no vertex colour, no material on _COL).

            if (IsSingle(collision))
            {
                var node = FbxStructureEdit.AddSibling(r.node, name, true);
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, name, CollisionData(collision.meshes[0], r)));
            }
            else
            {
                var container = FbxStructureEdit.AddSibling(r.node, name, false);
                for (int i = 0; i < collision.meshes.Count; i++)
                {
                    var hull = FbxStructureEdit.AddChild(container, HullName(collision.key, i), r.node);
                    if (!FbxStructureEdit.SamePlacement(r.node, hull, true))
                        throw new FbxStructureRefusal($"'{HullName(collision.key, i)}' does not land where '{r.node.GetName()}' is");
                    hull.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, HullName(collision.key, i), CollisionData(collision.meshes[i], r)));
                }
            }
            log.Add($"'{name}': {collision.meshes.Count} collision mesh(es) next to '{r.node.GetName()}'{(removed > 0 ? " (replacing the node of that name)" : "")}");
            return 1;
        }

        // Positions, triangles and normals only.
        static FbxMeshData CollisionData(Mesh mesh, Reference r)
        {
            var source = new FbxMeshData.Source { positions = Flatten(mesh.vertices), submeshTriangles = new[] { mesh.triangles } };
            var normals = mesh.normals;
            if (normals.Length == mesh.vertexCount) source.normals = Flatten(normals);
            return FbxMeshData.FromTriangles(source, r.fit, r.reverse, null);
        }

        // The file already holds this collision: same node layout, same triangles on the same
        // points (within the precision an import round trip leaves).
        static bool CollisionUnchanged((string key, List<Mesh> meshes, bool convex) collision, Dictionary<string, FbxChannelWrite.Tagged> tagged)
        {
            bool single = IsSingle(collision);
            if (single == tagged.ContainsKey(HullName(collision.key, 0))) return false;
            if (!single && (tagged.ContainsKey(collision.key + "_COL") || tagged.ContainsKey(HullName(collision.key, collision.meshes.Count)))) return false;
            for (int i = 0; i < collision.meshes.Count; i++)
            {
                string name = single ? collision.key + "_COL" : HullName(collision.key, i);
                if (!tagged.TryGetValue(name, out var tag) || !SameGeometry(tag, collision.meshes[i])) return false;
            }
            return true;
        }

        static bool SameGeometry(FbxChannelWrite.Tagged tag, Mesh mesh)
        {
            if (tag.submeshCorners.Sum(c => c.Length) != mesh.triangles.Length) return false;
            var imported = new List<Vector3>();
            for (int c = 0; c * 3 < tag.cornerPositions.Length; c++)
                if (!float.IsNaN(tag.cornerPositions[c * 3]))
                    imported.Add(new Vector3(tag.cornerPositions[c * 3], tag.cornerPositions[c * 3 + 1], tag.cornerPositions[c * 3 + 2]));
            var stored = mesh.vertices;
            float scale = 0;
            foreach (var p in stored) scale = Mathf.Max(scale, Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z));
            float tolerance = 1e-5f * Mathf.Max(scale, 1e-3f);
            return Covers(imported, stored, tolerance) && Covers(stored, imported, tolerance);
        }

        // Every point of b has a point of a within tolerance (grid of tolerance-sized cells).
        static bool Covers(IList<Vector3> a, IList<Vector3> b, float tolerance)
        {
            var grid = new Dictionary<(long, long, long), List<Vector3>>();
            (long, long, long) Cell(Vector3 p) => ((long)Math.Floor(p.x / tolerance), (long)Math.Floor(p.y / tolerance), (long)Math.Floor(p.z / tolerance));
            foreach (var p in a)
            {
                var cell = Cell(p);
                if (!grid.TryGetValue(cell, out var list)) grid[cell] = list = new List<Vector3>(1);
                list.Add(p);
            }
            foreach (var p in b)
                if (!HasNear(grid, Cell(p), p, tolerance)) return false;
            return true;
        }

        static bool HasNear(Dictionary<(long, long, long), List<Vector3>> grid, (long x, long y, long z) cell, Vector3 p, float tolerance)
        {
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                        if (grid.TryGetValue((cell.x + dx, cell.y + dy, cell.z + dz), out var list)
                            && list.Exists(q => (q - p).sqrMagnitude <= tolerance * tolerance))
                            return true;
            return false;
        }
    }
}
#endif
