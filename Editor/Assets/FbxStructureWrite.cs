// FbxStructureWrite.cs — the geometry a save adds to or replaces in an FBX beyond
// per-vertex channels: generated LODs, collision from the sidecar, and meshes whose faces
// MeshLab changed. It is written into the same FBX document as the channel edits
// (FbxChannelWrite), through the FBX SDK, without re-exporting the file:
//
//   • a generated LOD becomes a node next to the node it was generated from, with that
//     node's transform and materials, named so Unity groups it with its siblings;
//   • collision becomes `{key}_COL` (or a `{key}_COL` container of `_COL_Hull{i}` nodes)
//     next to its source node; one already in the file with the same geometry is left alone;
//   • a mesh with changed faces gets a new FBX mesh on the same node(s);
//   • a renderer whose materials changed gets them in its node's slots (an FBX material
//     named after each Unity material, mapped to it on the importer).
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
            /// <summary>The generated renderer's materials where they differ from its source renderer's; null when they do not.</summary>
            public Material[] materials, replaced;
        }

        /// <summary>A renderer whose materials differ from the ones its mesh's node gives it.</summary>
        internal sealed class MaterialEdit
        {
            /// <summary>The FBX mesh, and the node the renderer is (its GameObject's name).</summary>
            public string meshName, nodeName;
            /// <summary>The renderer's materials, and the model's it replaces, per submesh.</summary>
            public Material[] materials, replaced;
        }

        internal readonly List<NewLod> lods = new List<NewLod>();
        internal readonly List<(string name, Mesh mesh)> reshaped = new List<(string, Mesh)>();
        /// <summary>From the sidecar; the meshes are owned by the plan.</summary>
        internal readonly List<(string key, List<Mesh> meshes, bool convex)> collisions = new List<(string, List<Mesh>, bool)>();
        internal readonly List<MaterialEdit> materials = new List<MaterialEdit>();
        internal readonly List<string> refusals = new List<string>();
        /// <summary>A mesh written whole carries UV1 work (repack, transfer) of its own.</summary>
        internal bool wholeMeshUv1Edited;

        /// <summary>Renderer or generated-LOD materials the save puts into node slots.</summary>
        internal bool HasMaterialEdits => materials.Count > 0 || lods.Any(l => l.materials != null);

        public bool IsEmpty => lods.Count == 0 && reshaped.Count == 0 && collisions.Count == 0 && materials.Count == 0 && refusals.Count == 0;

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
        /// meshes whose faces or points changed, renderers whose materials changed, and the
        /// sidecar's collision. Changed normals and tangents are channel work (FbxChannelWrite).
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
                    var edit = MaterialChange(entry);
                    if (edit != null) plan.materials.Add(edit);
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
                else if (from.Count == 1)
                {
                    var lod = new FbxStructurePlan.NewLod { name = name, sourceName = from[0], mesh = result };
                    // The node starts with its source node's materials; a generated renderer
                    // given others since takes them into its own slots.
                    var source = group.Select(p => p.entry).FirstOrDefault(e => InFile(e) && e.lodIndex == sourceLodIndex && e.fbxMesh.name == from[0]);
                    if (entry.renderer != null && source?.renderer != null && ImportsMaterials(fbxPath)
                        && !entry.renderer.sharedMaterials.SequenceEqual(source.renderer.sharedMaterials))
                    {
                        lod.materials = entry.renderer.sharedMaterials;
                        lod.replaced = source.renderer.sharedMaterials;
                    }
                    plan.lods.Add(lod);
                }
                else plan.refusals.Add($"'{name}' cannot be paired with the LOD{sourceLodIndex} mesh of '{System.IO.Path.GetFileName(fbxPath)}' it was generated from");
            }
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

        // Everything a whole-mesh write takes from the mesh: positions, faces, normals,
        // tangents, colours and every UV channel.
        static bool SameMeshData(Mesh a, Mesh b)
        {
            if (a == b) return true;
            if (a.subMeshCount != b.subMeshCount || !a.vertices.SequenceEqual(b.vertices) || !a.normals.SequenceEqual(b.normals)
                || !a.tangents.SequenceEqual(b.tangents) || !a.colors.SequenceEqual(b.colors)) return false;
            for (int s = 0; s < a.subMeshCount; s++)
                if (a.GetTopology(s) != b.GetTopology(s) || !a.GetIndices(s).SequenceEqual(b.GetIndices(s))) return false;
            var uvA = new List<Vector4>();
            var uvB = new List<Vector4>();
            for (int ch = 0; ch < 8; ch++)
            {
                a.GetUVs(ch, uvA);
                b.GetUVs(ch, uvB);
                if (!uvA.SequenceEqual(uvB)) return false;
            }
            return true;
        }

        // The renderer's materials against the ones its mesh's node gives it in the model: the
        // prefab source renderer's, or, for a renderer that is no prefab instance (unpacked,
        // assembled by hand, standalone), the model's renderers that show the same mesh. With
        // material import off the model has no assignments of its own, so there is nothing to write.
        internal static FbxStructurePlan.MaterialEdit MaterialChange(MeshEntry entry)
        {
            if (entry?.renderer == null || entry.fbxMesh == null) return null;
            string path = AssetDatabase.GetAssetPath(entry.fbxMesh);
            if (!ImportsMaterials(path)) return null;
            var materials = entry.renderer.sharedMaterials;
            Material[] model;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(entry.renderer);
            if (source != null) model = source.sharedMaterials;
            else
            {
                var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (root == null) return null;
                var counterparts = root.GetComponentsInChildren<Renderer>(true).Where(r => RendererMesh(r) == entry.fbxMesh).ToList();
                // The node of the renderer's name is its counterpart; only without one is a
                // match with any instance of the mesh taken as no change.
                var named = counterparts.FirstOrDefault(r => r.name == entry.renderer.name);
                if (named != null) model = named.sharedMaterials;
                else if (counterparts.Count == 0 || counterparts.Any(r => materials.SequenceEqual(r.sharedMaterials))) return null;
                else model = counterparts[0].sharedMaterials;
            }
            if (materials.SequenceEqual(model)) return null;
            return new FbxStructurePlan.MaterialEdit
                { meshName = entry.fbxMesh.name, nodeName = entry.renderer.name, materials = materials, replaced = model };
        }

        static bool ImportsMaterials(string fbxPath)
            => AssetImporter.GetAtPath(fbxPath) is ModelImporter importer && importer.materialImportMode != ModelImporterMaterialImportMode.None;

        static Mesh RendererMesh(Renderer renderer)
            => renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh
             : renderer.TryGetComponent<MeshFilter>(out var filter) ? filter.sharedMesh : null;

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
            /// <summary>
            /// 'Generate Lightmap UVs' stays on: the import replaces UV1 whatever the file holds, so
            /// a source's set there is still written (its set count is kept) but is no UV1 write.
            /// </summary>
            public bool uv1Regenerated;
            /// <summary>Set by <see cref="Apply"/>: some mesh got the UV set Unity imports as UV1.</summary>
            public bool uv1Written;
            /// <summary>The FBX file being written; new materials' texture paths are made relative to it.</summary>
            public string fbxPath;
            /// <summary>The importer's material remaps already in place, by FBX material name.</summary>
            public Dictionary<string, Material> existingRemaps = new Dictionary<string, Material>(StringComparer.Ordinal);
            /// <summary>Filled by <see cref="Apply"/>: FBX material name → the Unity material the importer must map it to.</summary>
            public readonly Dictionary<string, Material> materialRemaps = new Dictionary<string, Material>(StringComparer.Ordinal);
            /// <summary>Set by <see cref="Apply"/>: the names of the materials the file had before the save.</summary>
            internal HashSet<string> sceneMaterials = new HashSet<string>(StringComparer.Ordinal);

            // The FBX material name for a Unity material: its own name made FBX-safe (ASCII
            // letters, digits and underscores, as the pipeline's naming rules require), unless
            // that name already maps (in this save or in the importer) to another material, or
            // names a material of the file that is not mapped to this one (its other nodes
            // would follow the remap).
            internal string MaterialName(Material material)
            {
                string stem = MeshHygieneUtility.SanitizeName(material.name);
                string name = stem;
                int n = 0;
                while (MapsElsewhere(name, material))
                    name = $"{stem}_{++n}";
                materialRemaps[name] = material;
                return name;
            }

            bool MapsElsewhere(string name, Material material)
            {
                if (materialRemaps.TryGetValue(name, out var mapped)) return mapped != material;
                bool remapped = existingRemaps.TryGetValue(name, out var existing) && existing != null;
                if (remapped) return existing != material;
                return sceneMaterials.Contains(name);
            }
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
            options.sceneMaterials = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < scene.GetMaterialCount(); i++)
            {
                var material = scene.GetMaterial(i);
                if (material != null) options.sceneMaterials.Add(material.GetName());
            }
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
            foreach (var lod in plan.lods.Where(l => l.materials != null))
                RequireMaterials(Ref(lod.sourceName).submeshMaterials, lod.materials, lod.replaced, lod.name);
            var materialTargets = new List<(FbxStructurePlan.MaterialEdit edit, FbxNode node, int[] slots)>();
            var editedNodes = new Dictionary<(int mesh, int node), FbxStructurePlan.MaterialEdit>();
            foreach (var edit in plan.materials)
            {
                var (target, key) = MaterialTarget(document, tagged, edit);
                // Several renderers of one node (scene instances of the model) can take one set.
                if (editedNodes.TryGetValue(key, out var earlier))
                {
                    if (earlier.materials.SequenceEqual(edit.materials)) continue;
                    throw new FbxStructureRefusalException($"'{edit.nodeName}': renderers of the same FBX node were given different materials; the node can hold one set");
                }
                editedNodes[key] = edit;
                materialTargets.Add(target);
            }

            int changes = 0;
            // Material edits first: a generated LOD starts with its source node's materials,
            // which must already be the ones the source renderer was given.
            foreach (var (edit, node, slots) in materialTargets)
            {
                int changed = ReplaceSlots(scene, node, slots, edit.materials, edit.replaced, options);
                if (changed == 0) continue;
                log.Add($"'{edit.nodeName}': {changed} material slot(s) take the renderer's materials");
                changes++;
            }
            foreach (var lod in plan.lods)
            {
                var r = Ref(lod.sourceName);
                int removed = RemoveNamed(r.node.GetParent(), lod.name);
                var data = FbxMeshData.FromTriangles(SourceOf(lod.mesh, r, options), r.fit, r.reverse, r.submeshMaterials);
                var node = FbxStructureEdit.AddSibling(r.node, lod.name, true);
                FbxStructureEdit.AddMaterials(node, r.node, Enumerable.Range(0, r.node.GetMaterialCount()));
                int slots = lod.materials != null ? ReplaceSlots(scene, node, r.submeshMaterials, lod.materials, lod.replaced, options) : 0;
                node.SetNodeAttribute(FbxStructureEdit.CreateMesh(scene, lod.name, data));
                log.Add($"'{lod.name}': {data.polygonSizes.Length} polygon(s) next to '{r.node.GetName()}'{(removed > 0 ? " (replacing the node of that name)" : "")}" +
                    (slots > 0 ? $", {slots} material slot(s) of its own" : ""));
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

        // ── Materials ──

        // The node a material edit is about, and the node material slot of each submesh.
        // Keyed by the mesh's ordinal and the node's index among the mesh's nodes.
        static ((FbxStructurePlan.MaterialEdit edit, FbxNode node, int[] slots) target, (int mesh, int node) key) MaterialTarget(
            FbxSourceDocument document, Dictionary<string, FbxChannelWrite.Tagged> tagged, FbxStructurePlan.MaterialEdit edit)
        {
            if (!tagged.TryGetValue(edit.meshName, out var tag))
                throw new FbxStructureRefusalException($"'{edit.meshName}' is not a mesh of the FBX (or its corner tags did not survive the import)");
            var mesh = document.Meshes[tag.ordinal];
            int index = -1, matches = 0;
            for (int i = 0; i < mesh.GetNodeCount(); i++)
                if (mesh.GetNode(i).GetName() == edit.nodeName) { index = i; matches++; }
            if (matches == 0 && mesh.GetNodeCount() == 1) index = 0;
            if (matches > 1)
                throw new FbxStructureRefusalException($"'{edit.nodeName}': several nodes of that name show the instanced mesh '{edit.meshName}'; which one it is cannot be told");
            if (index < 0)
                throw new FbxStructureRefusalException($"'{edit.nodeName}': which node of the instanced mesh '{edit.meshName}' it is cannot be told");
            var node = mesh.GetNode(index);
            var slots = SubmeshSlots(mesh, node, tag);
            RequireMaterials(slots, edit.materials, edit.replaced, edit.nodeName);
            return ((edit, node, slots), (tag.ordinal, index));
        }

        // What a slot write needs: a slot for each changed submesh and an asset to map it to.
        static void RequireMaterials(int[] slots, Material[] materials, Material[] replaced, string what)
        {
            if (materials.Length < replaced.Length)
                throw new FbxStructureRefusalException(
                    $"'{what}': the renderer has {materials.Length} material(s) where the import gives it {replaced.Length}; the FBX node's slots are not dropped by a document save");
            for (int s = 0; s < materials.Length; s++)
            {
                if (materials[s] == (s < replaced.Length ? replaced[s] : null)) continue;
                if (slots == null || s >= slots.Length || slots[s] < 0)
                    throw new FbxStructureRefusalException($"'{what}': submesh {s} has no material slot in the FBX to take '{(materials[s] != null ? materials[s].name : "None")}'");
                if (materials[s] == null)
                    throw new FbxStructureRefusalException($"'{what}': submesh {s} has no material; the FBX cannot hold an empty slot");
                if (!EditorUtility.IsPersistent(materials[s]))
                    throw new FbxStructureRefusalException($"'{what}': material '{materials[s].name}' is not an asset, so the import cannot be mapped to it");
            }
        }

        // The slots of the submeshes whose material changed take an FBX material named after
        // the Unity material (the scene's own of that name, or a new one), and the importer
        // maps that name to the asset. Other slots and other nodes are left as they are.
        static int ReplaceSlots(FbxScene scene, FbxNode node, int[] slots, Material[] materials, Material[] replaced, Options options)
        {
            // All slots change in one step: two submeshes exchanging materials are no duplicate.
            var bySlot = new Dictionary<int, Autodesk.Fbx.FbxSurfaceMaterial>();
            for (int s = 0; s < materials.Length && s < slots.Length; s++)
            {
                if (materials[s] == (s < replaced.Length ? replaced[s] : null)) continue;
                var material = FbxMaterial(scene, materials[s], options);
                if (bySlot.TryGetValue(slots[s], out var other) && other.GetName() != material.GetName())
                    throw new FbxStructureRefusalException($"'{node.GetName()}': submeshes sharing material slot {slots[s]} were given different materials");
                bySlot[slots[s]] = material;
            }
            if (bySlot.Count > 0) FbxStructureEdit.SetMaterials(node, bySlot);
            return bySlot.Count;
        }

        // The FBX material for a Unity material: the file's own of its name, or a new one
        // carrying the material's colour and main texture (the file renders textured outside
        // Unity; inside Unity the importer remap maps it to the asset).
        static Autodesk.Fbx.FbxSurfaceMaterial FbxMaterial(FbxScene scene, Material material, Options options)
        {
            string name = options.MaterialName(material);
            var existing = scene.GetMaterial(name);
            if (existing != null) return existing;
            var color = material.HasProperty("_BaseColor") ? material.GetColor("_BaseColor")
                : material.HasProperty("_Color") ? material.GetColor("_Color") : Color.white;
            var texture = material.HasProperty("_MainTex") || material.HasProperty("_BaseMap") ? material.mainTexture : null;
            string assetPath = texture != null ? AssetDatabase.GetAssetPath(texture) : null;
            string file = string.IsNullOrEmpty(assetPath) ? null : System.IO.Path.GetFullPath(assetPath).Replace('\\', '/');
            string relative = null;
            if (file != null && !string.IsNullOrEmpty(options.fbxPath))
            {
                string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(options.fbxPath));
                relative = System.IO.Path.GetRelativePath(folder, file).Replace('\\', '/');
            }
            return FbxStructureEdit.NewMaterial(scene, name, color.r, color.g, color.b, texture != null ? texture.name : null, file, relative);
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

            var (fit, reverse) = FitOf(mesh, tag, name);
            // A submesh no polygon tells about takes the first slot.
            var submeshMaterials = SubmeshSlots(mesh, node, tag)?.Select(slot => Math.Max(slot, 0)).ToArray();
            int uvSets = FbxLayerChannels.UvElements(mesh).Count;
            // A material's texture finds its UV set by name: a rebuilt mesh keeps the source's.
            var uvNames = document.UvSetNames(mesh.GetName());
            return new Reference
            {
                name = name, mesh = mesh, node = node, fit = fit, reverse = reverse, submeshMaterials = submeshMaterials,
                uvSets = uvSets, uvNames = uvNames != null && uvNames.Count == uvSets ? uvNames : null,
                colors = FbxLayerChannels.ColorElement(mesh) != null,
            };
        }

        /// <summary>
        /// The map from <paramref name="mesh"/>'s control points to its Unity import (the tagged
        /// one), and whether the import reversed the winding. Refused when the import is not an
        /// affine image of the control points.
        /// </summary>
        internal static (FbxSpaceFit fit, bool reverse) FitOf(FbxMesh mesh, FbxChannelWrite.Tagged tag, string name)
        {
            var topology = new FbxLayerChannels.Topology(mesh);
            int relation = FbxSpaceFit.WindingRelation(topology.polygonSizes, tag.TriangleCorners());
            // No triangle tells (all degenerate): Unity mirrors X on import and reverses the winding.
            if (relation == 0) relation = -1;
            var fit = FbxSpaceFit.FitCorners(FbxStructureEdit.ControlPoints(mesh), topology.cornerControlPoint, tag.cornerPositions, relation, out string error)
                ?? throw new FbxStructureRefusalException($"'{name}': {error}");
            return (fit, relation < 0);
        }

        // The node material slot of each Unity submesh (read from the polygons the submesh was
        // imported from); -1 where no polygon tells. Null when the node has no materials.
        static int[] SubmeshSlots(FbxMesh mesh, FbxNode node, FbxChannelWrite.Tagged tag)
        {
            if (node.GetMaterialCount() == 0) return null;
            var topology = new FbxLayerChannels.Topology(mesh);
            var polygonMaterials = FbxStructureEdit.PolygonMaterials(mesh);
            var slots = new int[Math.Max(1, tag.submeshCorners.Length)];
            for (int s = 0; s < slots.Length; s++)
            {
                var corners = s < tag.submeshCorners.Length ? tag.submeshCorners[s] : Array.Empty<int>();
                int at = Array.FindIndex(corners, c => c >= 0 && c < topology.CornerCount);
                int material = at >= 0 ? polygonMaterials[topology.cornerPolygon[corners[at]]] : -1;
                slots[s] = material >= 0 && material < node.GetMaterialCount() ? material : -1;
            }
            return slots;
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
            if (source.mesh.GetNodeCount() > 1)
                throw new FbxStructureRefusalException(
                    $"'{source.name}' is instanced by {source.mesh.GetNodeCount()} nodes; which instance a node next to it belongs with cannot be told from the mesh");
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
                    // Sets go in order: a later one past this gap would be renumbered.
                    for (int later = set + 1; later < sets; later++)
                    {
                        mesh.GetUVs(FbxChannelWrite.FbxUvSet(later, options.swapUv), list);
                        if (list.Count == mesh.vertexCount)
                            throw new FbxStructureRefusalException(
                                $"'{mesh.name}' has no UV{channel} but has UV{FbxChannelWrite.FbxUvSet(later, options.swapUv)}; written whole it would renumber its UV sets");
                    }
                    // A baked UV1 switches 'Generate Lightmap UVs' off for the whole model: a mesh
                    // written without one would lose its lightmap UVs on reimport.
                    if (set < options.requiredUvSets)
                        throw new FbxStructureRefusalException(
                            $"'{mesh.name}' has no UV{channel}, and writing UV1 switches 'Generate Lightmap UVs' off for the whole model");
                    if (set < required)
                        UvtLog.Warn($"[FBX Export] '{mesh.name}' has no UV{channel}; it gets {set} of the {required} UV set(s) of '{reference.name}'.");
                    break;
                }
                var uv = new float[list.Count * 2];
                for (int v = 0; v < list.Count; v++) { uv[v * 2] = list[v].x; uv[v * 2 + 1] = list[v].y; }
                source.uvs.Add(uv);
                options.uv1Written |= channel == 1 && !options.uv1Regenerated;
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
            var importedCorners = tag.TriangleCorners();
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
