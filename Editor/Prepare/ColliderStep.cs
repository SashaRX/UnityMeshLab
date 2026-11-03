// ColliderStep.cs — the Colliders step. Every collision node (MeshNaming.IsCollision: _COL,
// _COL_Hull{N}, _Collider, _Collision; found by MeshHygieneUtility.FindCollisionObjects) that
// carries a mesh gets a MeshCollider over it, non-convex with Unity's default cooking options.
// The main collider, the one of greatest volume in the root's space (a closed mesh's own, else
// its bounds'), goes on the model root when its node sits at the root's placement
// (LodHierarchy.OffsetFromRoot), else stays on its node, where its mesh lies; the others stay
// on their nodes. Collision nodes lose their MeshRenderer and MeshFilter. A model without
// collision nodes collides with one LOD level (the coarsest by default), whose renderers stay.
// A model whose root already has a MeshCollider is left as it is.
//
// Each collider's object is given its surface through the surface binding (SurfaceBinding):
// from the part's name, else its materials, else the model's name. A collider that already
// has its mesh keeps it, and a binding that already has a surface keeps it, so a second run
// changes nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    internal sealed class ColliderStep : IPrepareStep
    {
        internal const string StepName = "Colliders";
        const string Check = "Collider";

        /// <summary>Unity's default for a new MeshCollider (cooking options 30).</summary>
        internal const MeshColliderCookingOptions DefaultCooking = MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning | MeshColliderCookingOptions.WeldColocatedVertices
            | MeshColliderCookingOptions.UseFastMidphase;

        readonly ISurfaceBinding binding;
        readonly MeshNaming.Vocabulary vocabulary;

        /// <param name="binding">Gives each collider its surface; none when null.</param>
        /// <param name="vocabulary">The surface rules names are read against; MeshNaming's default when null.</param>
        internal ColliderStep(ISurfaceBinding binding = null, MeshNaming.Vocabulary vocabulary = null)
        {
            this.binding = binding;
            this.vocabulary = vocabulary ?? MeshNaming.Vocabulary.Default;
        }

        /// <summary>The LOD level a model without collision nodes collides with; -1, or a level it lacks, for its coarsest.</summary>
        internal int FallbackLod { get; set; } = -1;

        public string Name => StepName;

        public string Purpose => "Every _COL node gets a MeshCollider, the largest on the root, and loses its renderer; each collider gets its surface.";

        public string SkipReason(PrepareContext context)
            => context.Root == null ? "a model's own hierarchy cannot be edited; prepare its prefab or a scene object made from it" : null;

        public void Plan(PrepareContext context, PrepareReport report) => Run(context, report, false);

        public void Apply(PrepareContext context, PrepareReport report) => Run(context, report, true);

        // One collider to give: the node whose mesh it collides with, and whether that node is a
        // collision node (its renderers go) or a render node of the fallback LOD level (they stay).
        sealed class Source
        {
            internal Transform node;
            internal Mesh mesh;
            internal bool collisionNode;
            internal double volume;
        }

        void Run(PrepareContext context, PrepareReport report, bool apply)
        {
            var root = context.Root;
            var rootSubject = Subject(root.transform, root.transform);
            if (root.TryGetComponent(out MeshCollider own))
            {
                report.Add(PrepareLevel.Unchanged, rootSubject, Check, own.sharedMesh != null
                    ? $"the root already has a MeshCollider ('{own.sharedMesh.name}'); the model is left as it is"
                    : "the root already has a MeshCollider; the model is left as it is");
                return;
            }

            var sources = CollisionSources(root);
            if (sources.Count == 0)
            {
                sources = LodSources(root, FallbackLod, out string level);
                if (sources.Count == 0)
                {
                    report.Add(PrepareLevel.Warning, rootSubject, Check, "no collision node and no LOD level with a mesh: nothing to collide with");
                    return;
                }
                report.Add(PrepareLevel.Unchanged, rootSubject, Check, $"no collision node: it collides with {level}");
            }
            foreach (var source in sources) source.volume = Volume(root.transform, source.node, source.mesh);
            var main = sources.OrderByDescending(s => s.volume).First();

            if (binding?.Unavailable != null)
                report.Add(PrepareLevel.Unchanged, rootSubject, ComponentSurfaceBinding.Check, $"surfaces are not bound: {binding.Unavailable}");
            foreach (var source in sources)
            {
                string offset = source == main ? LodHierarchy.OffsetFromRoot(root.transform, source.node) : null;
                var host = source == main && offset == null ? root.transform : source.node;
                string subject = Subject(root.transform, source.node);
                string where = host == root.transform ? "main, on the root"
                    : source == main ? $"main, on its node: it sits off the root's placement ({offset})"
                    : "on its node";
                GiveCollider(context, report, apply, source, host, subject, where);
                if (source.collisionNode) RemoveRenderers(context, report, apply, source.node, subject);
                if (binding != null && binding.Unavailable == null)
                {
                    var (surface, origin) = SurfaceBinding.Resolve(source.node.name, MaterialNames(root.transform, source), context.Target.Name, vocabulary);
                    binding.Bind(context, host.gameObject, surface, origin, Subject(root.transform, host), report, apply);
                }
            }
        }

        static void GiveCollider(PrepareContext context, PrepareReport report, bool apply, Source source, Transform host, string subject, string where)
        {
            var existing = host.GetComponents<MeshCollider>().FirstOrDefault(c => c.sharedMesh == source.mesh);
            if (existing != null)
            {
                report.Add(existing.enabled ? PrepareLevel.Unchanged : PrepareLevel.Warning, subject, Check,
                    $"has its MeshCollider ('{source.mesh.name}', {where})" + (existing.enabled ? "" : ", disabled: it does not collide"));
                return;
            }
            report.Add(PrepareLevel.Changed, subject, Check, $"MeshCollider with '{source.mesh.name}' ({where})");
            if (apply)
            {
                var collider = context.AddComponent<MeshCollider>(host.gameObject);
                collider.sharedMesh = source.mesh;
                collider.convex = false;
                collider.cookingOptions = DefaultCooking;
            }

            // A collider on an inactive object does not collide. An inactive collision node is
            // switched on (it draws nothing once its renderers go); anything else is only reported.
            var node = host.gameObject;
            if (node.activeInHierarchy) return;
            bool parentInactive = host.parent != null && !host.parent.gameObject.activeInHierarchy;
            if (parentInactive || !source.collisionNode || host != source.node)
            {
                report.Add(PrepareLevel.Warning, subject, Check, "its object is inactive, so the collider does not collide");
                return;
            }
            report.Add(PrepareLevel.Changed, subject, Check, "activated: an inactive object does not collide");
            if (!apply) return;
            context.Record(node, "Activate Collision");
            node.SetActive(true);
            context.Changed(node);
        }

        static void RemoveRenderers(PrepareContext context, PrepareReport report, bool apply, Transform node, string subject)
        {
            var parts = new List<Component>();
            if (node.TryGetComponent(out MeshRenderer renderer)) parts.Add(renderer);
            if (node.TryGetComponent(out MeshFilter filter)) parts.Add(filter);
            if (parts.Count == 0) return;
            report.Add(PrepareLevel.Changed, subject, "Renderer", $"{string.Join(" and ", parts.Select(p => p.GetType().Name))} removed: a collision node is not drawn");
            if (!apply) return;
            foreach (var part in parts) context.Destroy(part);
        }

        // ── Sources ──

        /// <summary>Each collision node with a mesh: its own MeshCollider's, else its MeshFilter's (a container of hulls has none).</summary>
        static List<Source> CollisionSources(GameObject root)
        {
            var sources = new List<Source>();
            foreach (var node in MeshHygieneUtility.FindCollisionObjects(root.transform))
            {
                var mesh = node.GetComponents<MeshCollider>().Select(c => c.sharedMesh).FirstOrDefault(m => m != null);
                if (mesh == null && node.TryGetComponent(out MeshFilter filter)) mesh = filter.sharedMesh;
                if (mesh != null) sources.Add(new Source { node = node.transform, mesh = mesh, collisionNode = true });
            }
            return sources;
        }

        /// <summary>
        /// The render nodes of one LOD level: the LODGroup's level <paramref name="lod"/>
        /// (its coarsest with a mesh when -1 or missing), or without a LODGroup the nodes
        /// named for that level (every render node when none is named for a level).
        /// </summary>
        static List<Source> LodSources(GameObject root, int lod, out string level)
        {
            var group = root.GetComponentInChildren<LODGroup>(true);
            var levels = new List<(int index, List<Transform> nodes)>();
            if (group != null)
            {
                var lods = group.GetLODs();
                for (int i = 0; i < lods.Length; ++i)
                    levels.Add((i, lods[i].renderers.Where(r => r is MeshRenderer).Select(r => r.transform).ToList()));
            }
            else
            {
                var nodes = root.GetComponentsInChildren<MeshRenderer>(true).Select(r => r.transform)
                    .Where(t => !MeshNaming.IsCollision(t.name)).ToList();
                levels.AddRange(nodes.GroupBy(t => MeshNaming.LodIndex(t.name)).OrderBy(g => g.Key).Select(g => (g.Key, g.ToList())));
            }
            var usable = levels.Select(l => (l.index, sources: l.nodes.Where(n => n != null && !MeshNaming.IsCollision(n.name))
                    .Select(n => (node: n, mesh: n.TryGetComponent(out MeshFilter f) ? f.sharedMesh : null))
                    .Where(s => s.mesh != null)
                    .Select(s => new Source { node = s.node, mesh = s.mesh }).ToList()))
                .Where(l => l.sources.Count > 0).ToList();
            if (usable.Count == 0) { level = null; return new List<Source>(); }
            var chosen = lod >= 0 && usable.Any(l => l.index == lod) ? usable.First(l => l.index == lod) : usable[usable.Count - 1];
            level = chosen.index < 0 ? "its render nodes" : $"LOD{chosen.index}";
            return chosen.sources;
        }

        // The part's materials: a render node's own; a collision node's, those of the render
        // nodes of its part (the same name once LOD and collision suffixes are off).
        static IEnumerable<string> MaterialNames(Transform root, Source source)
        {
            IEnumerable<Renderer> renderers;
            if (!source.collisionNode) renderers = source.node.GetComponents<Renderer>();
            else
            {
                string key = MeshNaming.GroupKey(source.node.name);
                renderers = root.GetComponentsInChildren<Renderer>(true).Where(r => !MeshNaming.IsCollision(r.name)
                    && string.Equals(MeshNaming.GroupKey(r.name), key, StringComparison.OrdinalIgnoreCase));
            }
            return renderers.SelectMany(r => r.sharedMaterials).Where(m => m != null).Select(m => m.name).Distinct();
        }

        // ── Volume ──

        /// <summary>The mesh's volume in the root's space: a closed mesh's own, else its bounds'.</summary>
        internal static double Volume(Transform root, Transform node, Mesh mesh)
        {
            double scale = Math.Abs((root.worldToLocalMatrix * node.localToWorldMatrix).determinant);
            Mesh readable;
            bool copy;
            try { readable = MeshAccess.Readable(mesh, out copy); }
            catch (Exception) { return BoundsVolume(mesh.bounds) * scale; }
            try { return Volume(readable.vertices, MeshValidation.Triangles(readable), readable.bounds) * scale; }
            finally { if (copy) Object.DestroyImmediate(readable); }
        }

        /// <summary>A closed mesh's enclosed volume, else its bounds' volume.</summary>
        internal static double Volume(Vector3[] positions, int[] triangles, Bounds bounds)
            => MeshGeometry.IsClosed(positions, triangles) ? Math.Abs(FoliageProxyBuilder.SignedVolume(positions, triangles)) : BoundsVolume(bounds);

        static double BoundsVolume(Bounds bounds) => (double)bounds.size.x * bounds.size.y * bounds.size.z;

        static string Subject(Transform root, Transform node)
        {
            if (node == root) return $"'{root.name}'";
            var path = node.name;
            for (var parent = node.parent; parent != null && parent != root; parent = parent.parent) path = parent.name + "/" + path;
            return $"'{path}'";
        }
    }
}
