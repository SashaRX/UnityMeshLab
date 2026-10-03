using System;
using System.Collections.Generic;
using System.Linq;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Declare libraries, never another tab, as a tool's required dependencies.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class MeshLabToolAttribute : Attribute
    {
        public string Id { get; }
        public string[] Libraries { get; }
        public MeshLabToolAttribute(string id, params string[] libraries)
        { Id = id; Libraries = libraries ?? Array.Empty<string>(); }
    }

    public static class MeshLabLibraries
    {
        public const string Core = "core";
        public const string Geometry = "geometry";
        public const string Uv = "uv";
        public const string View = "mesh_view";
        public const string Hierarchy = "hierarchy";
        public const string VertexChannels = "vertex_channels";
        public const string Assets = "mesh_assets";
        public const string Simplification = "meshoptimizer";
        public const string UvTransfer = "uv_transfer";
        public const string Remesh = "remesh";
        public const string Collision = "collision";
        public const string Baking = "baking";
        public const string Bench = "benchmark";
    }

    internal sealed class MeshLabLibrary
    {
        internal readonly string Id, Name;
        internal readonly string[] Requires;
        internal MeshLabLibrary(string id, string name, params string[] requires)
        { Id = id; Name = name; Requires = requires; }
    }

    /// <summary>Dependency resolution has no tool instances, scene state or UI callbacks.</summary>
    internal sealed class MeshLabModuleRegistry
    {
        internal static readonly MeshLabLibrary[] Libraries = {
            new MeshLabLibrary(MeshLabLibraries.Core, "Context & contracts"),
            new MeshLabLibrary(MeshLabLibraries.Geometry, "Mesh geometry & access", MeshLabLibraries.Core),
            new MeshLabLibrary(MeshLabLibraries.Uv, "UV topology & atlas", MeshLabLibraries.Geometry),
            new MeshLabLibrary(MeshLabLibraries.VertexChannels, "Vertex channels", MeshLabLibraries.Geometry),
            new MeshLabLibrary(MeshLabLibraries.View, "Universal 2D / 3D inspection", MeshLabLibraries.Uv, MeshLabLibraries.VertexChannels),
            new MeshLabLibrary(MeshLabLibraries.Hierarchy, "Hierarchy & LODGroup", MeshLabLibraries.Geometry),
            new MeshLabLibrary(MeshLabLibraries.Assets, "Mesh assets, FBX & sidecars", MeshLabLibraries.Hierarchy, MeshLabLibraries.VertexChannels),
            new MeshLabLibrary(MeshLabLibraries.Simplification, "meshoptimizer simplification", MeshLabLibraries.Geometry),
            new MeshLabLibrary(MeshLabLibraries.UvTransfer, "UV transfer workflow", MeshLabLibraries.Uv, MeshLabLibraries.Assets, MeshLabLibraries.Simplification),
            new MeshLabLibrary(MeshLabLibraries.Remesh, "Remesh pipeline", MeshLabLibraries.Simplification, MeshLabLibraries.Uv),
            new MeshLabLibrary(MeshLabLibraries.Collision, "Collision generation", MeshLabLibraries.Geometry),
            new MeshLabLibrary(MeshLabLibraries.Baking, "CPU / GPU baking", MeshLabLibraries.VertexChannels),
            new MeshLabLibrary(MeshLabLibraries.Bench, "Sweep & benchmark", MeshLabLibraries.UvTransfer),
        };

        readonly Dictionary<string, MeshLabLibrary> libraries;
        readonly HashSet<string> disabledTools, disabledLibraries;
        internal MeshLabModuleRegistry(IEnumerable<string> disabledTools = null, IEnumerable<string> disabledLibraries = null,
            IEnumerable<MeshLabLibrary> libraries = null)
        {
            this.disabledTools = new HashSet<string>(disabledTools ?? Array.Empty<string>(), StringComparer.Ordinal);
            this.disabledLibraries = new HashSet<string>(disabledLibraries ?? Array.Empty<string>(), StringComparer.Ordinal);
            this.libraries = (libraries ?? Libraries).ToDictionary(l => l.Id, StringComparer.Ordinal);
        }

        internal static MeshLabToolAttribute Metadata(Type type)
            => Attribute.GetCustomAttribute(type, typeof(MeshLabToolAttribute)) as MeshLabToolAttribute;

        internal static IEnumerable<Type> ToolTypes()
            => UnityEditor.TypeCache.GetTypesDerivedFrom<IUvTool>()
                .Where(t => !t.IsAbstract && !t.IsInterface && !t.ContainsGenericParameters);

        internal string LibraryUnavailableReason(string id) => Visit(id, new HashSet<string>(), null);

        string Visit(string id, HashSet<string> visiting, HashSet<string> resolved)
        {
            if (string.IsNullOrEmpty(id) || !libraries.TryGetValue(id, out var library)) return "Missing library: " + id;
            if (disabledLibraries.Contains(id)) return "Disabled library: " + library.Name;
            if (!visiting.Add(id)) return "Library dependency cycle: " + id;
            foreach (var required in library.Requires)
            {
                string reason = Visit(required, visiting, resolved);
                if (reason != null) return library.Name + " → " + reason;
            }
            visiting.Remove(id);
            resolved?.Add(id);
            return null;
        }

        internal string ToolUnavailableReason(Type type)
        {
            var metadata = Metadata(type);
            if (metadata == null || string.IsNullOrEmpty(metadata.Id)) return "Missing MeshLabTool dependency declaration";
            if (disabledTools.Contains(metadata.Id)) return "Tool disabled";
            foreach (string library in metadata.Libraries.Concat(new[] { MeshLabLibraries.View }))
            {
                string reason = LibraryUnavailableReason(library);
                if (reason != null) return reason;
            }
            return null;
        }

        internal IEnumerable<string> RequiredLibraries(Type type)
        {
            var resolved = new HashSet<string>();
            var metadata = Metadata(type);
            var pending = new Stack<string>((metadata?.Libraries ?? Array.Empty<string>()).Concat(new[] { MeshLabLibraries.View }));
            while (pending.Count > 0)
            {
                string id = pending.Pop();
                if (!resolved.Add(id)) continue;
                if (libraries.TryGetValue(id, out var library))
                    foreach (string required in library.Requires) pending.Push(required);
            }
            return resolved.OrderBy(id => id, StringComparer.Ordinal);
        }

        internal List<IUvTool> CreateTools(IEnumerable<Type> types, Action repaint, IEnumerable<IUvTool> existing = null)
        {
            var result = new List<IUvTool>();
            var candidates = types.ToList();
            var duplicateIds = new HashSet<string>(candidates.Select(Metadata).Where(m => m != null)
                .GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => g.Key));
            foreach (var type in candidates)
            {
                var metadata = Metadata(type);
                if (ToolUnavailableReason(type) != null || duplicateIds.Contains(metadata.Id)) continue;
                try
                {
                    var tool = existing?.FirstOrDefault(t => t.GetType() == type) ?? (IUvTool)Activator.CreateInstance(type);
                    if (tool.ToolId != metadata.Id) throw new InvalidOperationException("ToolId differs from dependency declaration");
                    tool.RequestRepaint = repaint;
                    result.Add(tool);
                }
                catch (Exception ex) { UvtLog.Warn($"[Modules] Failed to create {type.Name}: {ex.Message}"); }
            }
            return result.OrderBy(t => t.ToolOrder).ThenBy(t => t.ToolId, StringComparer.Ordinal).ToList();
        }
    }
}
