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
        /// <summary>A mesh written whole carries UV1 work (repack, transfer) of its own.</summary>
        internal bool wholeMeshUv1Edited;

        public bool IsEmpty => lods.Count == 0 && reshaped.Count == 0 && collisions.Count == 0 && refusals.Count == 0;

        /// <summary>Meshes written whole by the structure step; the channel step leaves them out.</summary>
        internal ICollection<string> WholeMeshNames() => new HashSet<string>(lods.Select(l => l.name).Concat(reshaped.Select(r => r.name)), StringComparer.Ordinal);

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
            string file = System.IO.Path.GetFileName(fbxPath);
            foreach (var (entry, result) in group)
            {
                bool uv1Work = entry.repackedMesh != null || entry.transferredMesh != null;
                if (InFile(entry))
                {
                    if (GeometryDiffers(entry.fbxMesh, result))
                    {
                        AddReshaped(plan, entry.fbxMesh.name, result);
                        plan.wholeMeshUv1Edited |= uv1Work;
                    }
                    else if (ShadingChanged(entry.fbxMesh, result))
                        plan.refusals.Add($"'{entry.fbxMesh.name}' has changed normals or tangents; the document save writes them only with new geometry");
                    continue;
                }
                plan.wholeMeshUv1Edited |= uv1Work;
                string name = FbxExport.ResolveExportMeshName(entry, result);
                // Generated LODs are named after their source: base (pipeline suffixes and LOD stripped) + _LOD{n}.
                string stem = MeshNaming.StripPipelineSuffixes(name);
                var from = sources.Where(n => MeshNaming.StripPipelineSuffixes(n) == stem).Distinct().ToList();
                // Two branches generating one name would write one node: the file keeps names unique per LOD.
                if (plan.lods.Any(l => l.name == name))
                    plan.refusals.Add($"'{name}' is generated more than once (by same-named meshes in different branches); the file can take one node of that name");
                else if (from.Count == 1) plan.lods.Add(new FbxStructurePlan.NewLod { name = name, sourceName = from[0], mesh = result });
                else plan.refusals.Add($"'{name}' cannot be paired with the LOD{sourceLodIndex} mesh of '{System.IO.Path.GetFileName(fbxPath)}' it was generated from");
            }
            int rematerialled = group.Count(p => MaterialsChanged(p.entry));
            if (rematerialled > 0)
                plan.refusals.Add($"{rematerialled} renderer(s) of '{file}' have changed materials; the document save does not rewrite material assignments");
            plan.collisions.AddRange(SidecarStore.CollisionMeshes(fbxPath));
            // Collision meshes Cleanup flagged (UVs or colours) are rewritten clean when the
            // sidecar replaces them; any other is the rebuild's to strip.
            var replaced = new HashSet<string>(plan.collisions.SelectMany(NodeNames), StringComparer.Ordinal);
            var flagged = FbxExport.CollisionMeshesWithSurfaceData(fbxPath).Where(n => !replaced.Contains(n)).ToList();
            if (flagged.Count > 0)
                plan.refusals.Add($"{string.Join(", ", flagged.Select(n => $"'{n}'"))} in '{file}' carry UVs or vertex colours that collision meshes should not have; the rebuild strips them");
            return plan;
        }

        // Instances of one FBX mesh share it: the same new geometry twice is one replacement,
        // different geometry cannot both be written.
        static void AddReshaped(FbxStructurePlan plan, string name, Mesh mesh)
        {
            int at = plan.reshaped.FindIndex(r => r.name == name);
            if (at < 0) { plan.reshaped.Add((name, mesh)); return; }
            if (!SameMeshData(plan.reshaped[at].mesh, mesh))
                plan.refusals.Add($"'{name}' is instanced and its instances were edited differently; they share one FBX mesh");
        }

        static bool SameMeshData(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a.subMeshCount != b.subMeshCount || !a.vertices.SequenceEqual(b.vertices)) return false;
            for (int s = 0; s < a.subMeshCount; s++)
                if (!a.GetIndices(s).SequenceEqual(b.GetIndices(s))) return false;
            return true;
        }

        // Normals or tangents a channel save cannot write: the attribute was added or removed
        // (Cleanup), or the values changed on a working copy that kept the import's vertices.
        // Tangent values count only when the importer reads the file's (Import); otherwise
        // Unity computes them on every import and a recomputed set is no edit of the file.
        internal static bool ShadingChanged(Mesh imported, Mesh result)
        {
            if (imported == null || result == null || imported == result) return false;
            if (imported.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal) != result.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Normal)) return true;
            if (imported.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent) != result.HasVertexAttribute(UnityEngine.Rendering.VertexAttribute.Tangent)) return true;
            if (!imported.isReadable || !result.isReadable || imported.vertexCount != result.vertexCount) return false;
            // Only an unrenumbered copy pairs vertices by index.
            if (!imported.vertices.SequenceEqual(result.vertices)) return false;
            if (!imported.normals.SequenceEqual(result.normals)) return true;
            return AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(imported)) is ModelImporter importer
                && importer.importTangents == ModelImporterTangents.Import
                && !imported.tangents.SequenceEqual(result.tangents);
        }

        // The scene renderer's materials differ from the model's (e.g. after Cleanup's material
        // fixes): a material change is the rebuild's to write, not the channel save's.
        internal static bool MaterialsChanged(MeshEntry entry)
        {
            if (entry?.renderer == null) return false;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(entry.renderer);
            return source != null && !entry.renderer.sharedMaterials.SequenceEqual(source.sharedMaterials);
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

        /// <summary>What a mesh written whole carries besides positions, faces and normals.</summary>
        internal sealed class Options
        {
            public bool swapUv, preserveHierarchy, writeTangents;
            /// <summary>UV sets every mesh gets, warned about when missing (the baked UV1).</summary>
            public int requiredUvSets;
            /// <summary>UV sets the save may write: a mesh carries them from MeshLab's work when the file lacks them.</summary>
            public int editableUvSets;
            /// <summary>The save writes vertex colours: a mesh carries them even when its source had none.</summary>
            public bool writeColors;
            /// <summary>Set by <see cref="Apply"/>: some mesh got the UV set Unity imports as UV1.</summary>
            public bool uv1Written;
        }

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
            /// <summary>The file's names of those sets; null when they could not be read.</summary>
            public List<string> uvNames;
            public bool colors;
        }

        /// <summary>
        /// Writes <paramref name="plan"/> into <paramref name="document"/>; returns the number of
        /// nodes added or meshes replaced, with one line per change in <paramref name="log"/>.
        /// Throws <see cref="FbxStructureRefusalException"/> for what cannot be placed.
        /// </summary>
        internal static int Apply(FbxSourceDocument document, Dictionary<string, FbxChannelWrite.Tagged> tagged,
            FbxStructurePlan plan, Options options, List<string> log)
        {
            bool preserveHierarchy = options.preserveHierarchy;
            if (plan.refusals.Count > 0) throw new FbxStructureRefusalException(plan.refusals[0]);
            var scene = document.Scene;
            var references = new Dictionary<string, Reference>(StringComparer.Ordinal);
            Reference Ref(string name)
            {
                if (!references.TryGetValue(name, out var r)) references[name] = r = Resolve(document, tagged, name);
                return r;
            }

            // Every source is resolved and checked before the document changes: a mesh being
            // replaced may also be the source of a LOD or a collider.
            foreach (var lod in plan.lods)
            {
                RequireLodSibling(scene, Ref(lod.sourceName), lod.name, preserveHierarchy);
                RequirePlaceable(Ref(lod.sourceName), lod.mesh, lod.name);
            }
            var collisions = plan.collisions.Where(c => !CollisionUnchanged(c, tagged)).ToList();
            foreach (var (key, meshes, _) in collisions)
            {
                var source = Ref(CollisionSource(tagged, key));
                RequireSiblingRoom(scene, source, preserveHierarchy);
                foreach (var mesh in meshes) RequirePlaceable(source, mesh, key + "_COL");
            }
            foreach (var (name, mesh) in plan.reshaped) RequirePlaceable(Ref(name), mesh, name);

            int changes = 0;
            foreach (var lod in plan.lods)
            {
                var r = Ref(lod.sourceName);
                int removed = RemoveNamed(r.node.GetParent(), lod.name);
                var data = FbxMeshData.FromTriangles(SourceOf(lod.mesh, r, options), r.fit, r.reverse, r.submeshMaterials);
                var node = FbxStructureEdit.AddSibling(r.node, lod.name, true);
                FbxStructureEdit.AddMaterials(node, r.node, Enumerable.Range(0, r.node.GetMaterialCount()));
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, lod.name, data));
                log.Add($"'{lod.name}': {data.polygonSizes.Length} polygon(s) next to '{r.node.GetName()}'{(removed > 0 ? " (replacing the node of that name)" : "")}");
                changes++;
            }
            foreach (var collision in collisions)
            {
                changes += WriteCollision(scene, Ref(CollisionSource(tagged, collision.key)), collision, log);
            }
            foreach (var (name, mesh) in plan.reshaped)
            {
                var r = Ref(name);
                var data = FbxMeshData.FromTriangles(SourceOf(mesh, r, options), r.fit, r.reverse, r.submeshMaterials);
                int nodes = FbxStructureEdit.ReplaceMesh(r.mesh, FbxStructureEdit.CreateMesh(scene, r.mesh.GetName(), data));
                log.Add($"'{name}': geometry replaced ({data.polygonSizes.Length} polygon(s), {nodes} node(s); node, transform and materials kept)");
                changes++;
            }
            return changes;
        }

        static Reference Resolve(FbxSourceDocument document, Dictionary<string, FbxChannelWrite.Tagged> tagged, string name)
        {
            if (!tagged.TryGetValue(name, out var tag))
                throw new FbxStructureRefusalException($"'{name}' is not a mesh of the FBX (or its corner tags did not survive the import)");
            var mesh = document.Meshes[tag.ordinal];
            if (FbxStructureEdit.HasDeformers(mesh))
                throw new FbxStructureRefusalException($"'{name}' is skinned or has blend shapes; geometry made from it cannot be placed through its import");
            var node = FbxStructureEdit.NodeOf(mesh, name)
                ?? throw new FbxStructureRefusalException($"'{name}' is instanced by several nodes and none is named after it");

            var topology = new FbxLayerChannels.Topology(mesh);
            int relation = FbxSpaceFit.WindingRelation(topology.polygonSizes, tag.submeshCorners.SelectMany(c => c).ToArray());
            // No triangle tells (all degenerate): Unity mirrors X on import and reverses the winding.
            if (relation == 0) relation = -1;
            var fit = FbxSpaceFit.FitCorners(FbxStructureEdit.ControlPoints(mesh), topology.cornerControlPoint, tag.cornerPositions, relation, out string error)
                ?? throw new FbxStructureRefusalException($"'{name}': {error}");

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
            int uvSets = FbxLayerChannels.UvElements(mesh).Count;
            // A material's texture finds its UV set by name: a rebuilt mesh keeps the source's.
            var uvNames = document.UvSetNames(mesh.GetName());
            return new Reference
            {
                name = name, mesh = mesh, node = node, fit = fit, reverse = relation < 0, submeshMaterials = submeshMaterials,
                uvSets = uvSets, uvNames = uvNames != null && uvNames.Count == uvSets ? uvNames : null,
                colors = FbxLayerChannels.ColorElement(mesh) != null,
            };
        }

        // The mesh a sidecar collision entry was made from: the mesh of that name, else the one
        // LOD0 (or unsuffixed) mesh whose group key it is.
        static string CollisionSource(Dictionary<string, FbxChannelWrite.Tagged> tagged, string key)
        {
            if (tagged.ContainsKey(key)) return key;
            var matches = tagged.Keys.Where(n => !MeshNaming.IsCollision(n) && MeshNaming.GroupKey(n) == key && MeshNaming.LodIndex(n) <= 0).ToList();
            if (matches.Count == 1) return matches[0];
            throw new FbxStructureRefusalException(matches.Count == 0
                ? $"collision '{key}': no mesh of the FBX is named '{key}' or has it as its group key"
                : $"collision '{key}': {string.Join(", ", matches.Select(m => $"'{m}'"))} all have that group key");
        }

        static void RequireLodSibling(FbxScene scene, Reference source, string name, bool preserveHierarchy)
        {
            string sourceNode = source.node.GetName();
            if (!MeshNaming.TryParseLod(sourceNode, out string sourceBase, out _) || !MeshNaming.TryParseLod(name, out string newBase, out _)
                || !string.Equals(sourceBase, newBase, StringComparison.OrdinalIgnoreCase))
                throw new FbxStructureRefusalException(
                    $"'{name}' would not be grouped with '{sourceNode}': Unity groups LODs by one base name with _LOD<N> suffixes, " +
                    "and renaming the source node is the explicit hierarchy normalisation (Prefab Builder)");
            RequireSiblingRoom(scene, source, preserveHierarchy);
        }

        // A flat source's import tells the map only within its plane. Off it the fit assumes a
        // similarity, which holds when the plane maps without stretch and the geometric
        // scaling Unity bakes in is uniform; otherwise geometry leaving the plane has no
        // known place in the file.
        static void RequirePlaceable(Reference source, Mesh mesh, string name)
        {
            if (!source.fit.Flat || (source.fit.Conformal && FbxStructureEdit.UniformGeometricScaling(source.node))) return;
            foreach (var v in mesh.vertices)
                if (!source.fit.OnPlane(v.x, v.y, v.z))
                    throw new FbxStructureRefusalException(
                        $"'{name}' leaves the plane of the flat mesh '{source.name}', and its import does not tell how the file's space scales off that plane");
        }

        static void RequireSiblingRoom(FbxScene scene, Reference source, bool preserveHierarchy)
        {
            if (FbxStructureEdit.HasTransformAnimation(source.node))
                throw new FbxStructureRefusalException(
                    $"'{source.node.GetName()}' has an animated transform; a node next to it would not follow the animation");
            if (!preserveHierarchy && FbxStructureEdit.IsSoleTopNode(scene, source.node))
                throw new FbxStructureRefusalException(
                    $"'{source.node.GetName()}' is the FBX's only top-level node, which Unity imports as the model root; a node next to it " +
                    "would change the imported hierarchy (normalise it in Prefab Builder first)");
        }

        // Removes the siblings a new node replaces (same name, same parent); a node with children
        // is not ours to drop, and a same-named node elsewhere in the file is another branch's.
        static int RemoveNamed(FbxNode parent, string name)
        {
            var nodes = FbxStructureEdit.ChildrenNamed(parent, name);
            foreach (var node in nodes)
            {
                if (node.GetChildCount() > 0)
                    throw new FbxStructureRefusalException($"'{name}' already exists with child nodes; replacing it would drop them");
                FbxStructureEdit.RemoveSubtree(node);
            }
            return nodes.Count;
        }

        static FbxMeshData.Source SourceOf(Mesh mesh, Reference reference, Options options)
        {
            var source = new FbxMeshData.Source
            {
                positions = Flatten(mesh.vertices),
                submeshTriangles = new int[mesh.subMeshCount][],
                submeshFaceSizes = new int[mesh.subMeshCount],
            };
            // Triangles and quads (Keep Quads imports) both go in as polygons of their own size.
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                var topology = mesh.GetTopology(s);
                bool faces = topology == MeshTopology.Triangles || topology == MeshTopology.Quads;
                source.submeshTriangles[s] = faces ? mesh.GetIndices(s) : null;
                source.submeshFaceSizes[s] = topology == MeshTopology.Quads ? 4 : 3;
            }
            var normals = mesh.normals;
            if (normals.Length == mesh.vertexCount) source.normals = Flatten(normals);
            // Unity reads a tangent frame from the file only when the importer imports tangents.
            var tangents = options.writeTangents ? mesh.tangents : null;
            if (tangents != null && tangents.Length == mesh.vertexCount)
            {
                source.tangents = new float[tangents.Length * 4];
                for (int v = 0; v < tangents.Length; v++)
                {
                    source.tangents[v * 4] = tangents[v].x; source.tangents[v * 4 + 1] = tangents[v].y;
                    source.tangents[v * 4 + 2] = tangents[v].z; source.tangents[v * 4 + 3] = tangents[v].w;
                }
            }

            // The UV sets the source FBX mesh has, and the ones MeshLab's work added (a repacked
            // UV1 the file never had). A channel Unity generates on import (lightmap UVs) is
            // not editable and stays out of the file, unless UV1 is being baked.
            if (reference.uvNames != null) source.uvNames.AddRange(reference.uvNames);
            var list = new List<Vector2>();
            int required = Math.Max(reference.uvSets, options.requiredUvSets);
            int sets = Math.Max(required, options.editableUvSets);
            for (int set = 0; set < sets; set++)
            {
                int channel = FbxChannelWrite.FbxUvSet(set, options.swapUv);
                mesh.GetUVs(channel, list);
                if (list.Count != mesh.vertexCount)
                {
                    if (set < required)
                        UvtLog.Warn($"[FBX Export] '{mesh.name}' has no UV{channel}; it gets {set} of the {required} UV set(s) of '{reference.name}'.");
                    break;
                }
                var uv = new float[list.Count * 2];
                for (int v = 0; v < list.Count; v++) { uv[v * 2] = list[v].x; uv[v * 2 + 1] = list[v].y; }
                source.uvs.Add(uv);
                options.uv1Written |= channel == 1;
            }
            var colors = mesh.colors;
            if ((reference.colors || options.writeColors) && colors.Length == mesh.vertexCount)
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

        // The mesh nodes a collision entry writes.
        static IEnumerable<string> NodeNames((string key, List<Mesh> meshes, bool convex) collision)
            => IsSingle(collision) ? new[] { collision.key + "_COL" } : Enumerable.Range(0, collision.meshes.Count).Select(i => HullName(collision.key, i));

        static int WriteCollision(FbxScene scene, Reference r, (string key, List<Mesh> meshes, bool convex) collision, List<string> log)
        {
            string name = collision.key + "_COL";
            var replaced = FbxStructureEdit.ChildrenNamed(r.node.GetParent(), name);
            int removed = replaced.Count;
            foreach (var old in replaced) FbxStructureEdit.RemoveSubtree(old);
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
                        throw new FbxStructureRefusalException($"'{HullName(collision.key, i)}' does not land where '{r.node.GetName()}' is");
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
                // UVs or colours on the stored collider: rewritten without them.
                if (tag.uvs.Any(uv => uv != null) || tag.colors != null) return false;
            }
            return true;
        }

        // Same triangles on the same points, same winding (within the precision an import
        // round trip leaves): the imported corners snap to the stored vertices, then both
        // triangle lists compare as multisets of point loops.
        static bool SameGeometry(FbxChannelWrite.Tagged tag, Mesh mesh)
        {
            var triangles = mesh.triangles;
            var importedCorners = tag.submeshCorners.SelectMany(c => c).ToArray();
            if (importedCorners.Length != triangles.Length) return false;

            var stored = mesh.vertices;
            var pointOf = new int[stored.Length];
            var firstAt = new Dictionary<Vector3, int>();
            for (int v = 0; v < stored.Length; v++)
            {
                if (!firstAt.TryGetValue(stored[v], out int point)) firstAt[stored[v]] = point = v;
                pointOf[v] = point;
            }
            float scale = 0;
            foreach (var p in stored) scale = Mathf.Max(scale, Mathf.Abs(p.x), Mathf.Abs(p.y), Mathf.Abs(p.z));
            var grid = new PointGrid(stored, 1e-5f * Mathf.Max(scale, 1e-3f));

            var counts = new Dictionary<(int, int, int), int>();
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                var key = Loop(pointOf[triangles[t]], pointOf[triangles[t + 1]], pointOf[triangles[t + 2]]);
                counts.TryGetValue(key, out int n);
                counts[key] = n + 1;
            }
            var snapped = new int[3];
            for (int t = 0; t + 2 < importedCorners.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    int c = importedCorners[t + k];
                    if (c < 0 || c * 3 + 2 >= tag.cornerPositions.Length) return false;
                    int v = grid.Nearest(new Vector3(tag.cornerPositions[c * 3], tag.cornerPositions[c * 3 + 1], tag.cornerPositions[c * 3 + 2]));
                    if (v < 0) return false;
                    snapped[k] = pointOf[v];
                }
                var key = Loop(snapped[0], snapped[1], snapped[2]);
                if (!counts.TryGetValue(key, out int n) || n == 0) return false;
                counts[key] = n - 1;
            }
            return true;
        }

        // A triangle as a loop of points, started at its smallest: any starting corner, same winding.
        static (int, int, int) Loop(int a, int b, int c)
        {
            if (a <= b && a <= c) return (a, b, c);
            return b <= c ? (b, c, a) : (c, a, b);
        }

        // Vertices bucketed in tolerance-sized cells, for nearest-within-tolerance lookups.
        sealed class PointGrid
        {
            readonly Vector3[] points;
            readonly float tolerance;
            readonly Dictionary<(long, long, long), List<int>> cells = new Dictionary<(long, long, long), List<int>>();

            public PointGrid(Vector3[] points, float tolerance)
            {
                this.points = points;
                this.tolerance = tolerance;
                for (int i = 0; i < points.Length; i++)
                {
                    var cell = Cell(points[i]);
                    if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<int>(1);
                    list.Add(i);
                }
            }

            (long, long, long) Cell(Vector3 p) => ((long)Math.Floor(p.x / tolerance), (long)Math.Floor(p.y / tolerance), (long)Math.Floor(p.z / tolerance));

            /// <summary>The closest point within tolerance of <paramref name="p"/>, or -1.</summary>
            public int Nearest(Vector3 p)
            {
                var (cx, cy, cz) = Cell(p);
                int best = -1;
                float bestDistance = tolerance * tolerance;
                for (long dx = -1; dx <= 1; dx++)
                    for (long dy = -1; dy <= 1; dy++)
                        for (long dz = -1; dz <= 1; dz++)
                            if (cells.TryGetValue((cx + dx, cy + dy, cz + dz), out var list))
                                best = Closest(list, p, best, ref bestDistance);
                return best;
            }

            int Closest(List<int> candidates, Vector3 p, int best, ref float bestDistance)
            {
                foreach (int i in candidates)
                {
                    float d = (points[i] - p).sqrMagnitude;
                    if (d <= bestDistance) { bestDistance = d; best = i; }
                }
                return best;
            }
        }
    }
}
#endif
