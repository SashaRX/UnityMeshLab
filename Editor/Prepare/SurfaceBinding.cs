// SurfaceBinding.cs — gives a collider the surface it is made of, so a game can tell what a
// hit struck (wood, concrete, metal…). The package knows no game type: ISurfaceBinding is the
// extension point, and ComponentSurfaceBinding the implementation by type names from Project
// Settings ▸ Mesh Lab ▸ Prepare — a component put on each collider's object, an object field
// on it, and the type of asset that field takes, found by name in the project and picked by
// the surface's name.
//
// The surface comes from the part's name (Seat_Wood_COL), else from the names of the part's
// materials (M_Wood_Oak), else from the model's name, each read by MeshNaming.ParseTags
// against the surface rules (a keyword → the surface asset's name).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>A keyword a name's word starts with → the name of the surface asset it stands for.</summary>
    [Serializable]
    internal sealed class SurfaceRule
    {
        public string keyword;
        public string asset;

        public SurfaceRule() { }
        public SurfaceRule(string keyword, string asset) { this.keyword = keyword; this.asset = asset; }
    }

    /// <summary>The component, field and asset type a collider's surface is set through, and the surface rules.</summary>
    [Serializable]
    internal sealed class SurfaceBindingSettings
    {
        /// <summary>The component put on each collider's object, by full or short type name; empty binds nothing.</summary>
        public string componentType = "";

        /// <summary>The component's serialized object field that takes the surface asset.</summary>
        public string field = "";

        /// <summary>The type of the surface assets, by full or short type name.</summary>
        public string dataType = "";

        public List<SurfaceRule> rules = MeshNaming.Vocabulary.Default.SurfaceKeywords
            .Select(k => new SurfaceRule(k.keyword, k.surface)).ToList();
    }

    internal interface ISurfaceBinding
    {
        /// <summary>Why no collider gets a surface (nothing set, a type missing from the project), or null.</summary>
        string Unavailable { get; }

        /// <summary>
        /// Gives <paramref name="host"/> the binding of <paramref name="surface"/> (read from
        /// <paramref name="origin"/>), or reports in a plan what it would do: one line into <paramref name="report"/>.
        /// </summary>
        void Bind(PrepareContext context, GameObject host, string surface, string origin, string subject, PrepareReport report, bool apply);
    }

    /// <summary>A component by type name on the collider's object, its object field set to the surface asset of the surface's name.</summary>
    internal sealed class ComponentSurfaceBinding : ISurfaceBinding
    {
        internal const string Check = "Surface";

        readonly Type component;
        readonly Type data;
        readonly string field;
        readonly Func<Type, string, string, Object> find;

        /// <param name="find">The surface asset of a type and name, nearest a path; the project's assets when null.</param>
        internal ComponentSurfaceBinding(SurfaceBindingSettings settings, Func<Type, string, string, Object> find = null)
        {
            this.find = find ?? FindAsset;
            Unavailable = Resolve(settings, out component, out data, out field);
        }

        public string Unavailable { get; }

        // The component, asset type and field the settings name; why they cannot bind, or null.
        static string Resolve(SurfaceBindingSettings settings, out Type component, out Type data, out string field)
        {
            component = null; data = null; field = settings?.field?.Trim();
            if (settings == null || string.IsNullOrWhiteSpace(settings.componentType))
                return "no surface component is set (Project Settings ▸ Mesh Lab ▸ Prepare)";
            component = FindType(settings.componentType, typeof(Component));
            if (component == null) return $"there is no component type '{settings.componentType}' in this project";
            data = FindType(settings.dataType, typeof(Object));
            if (data == null) return $"there is no asset type '{settings.dataType}' in this project";
            var info = FindField(component, field);
            if (info == null) return $"'{component.Name}' has no serialized field '{field}'";
            return info.FieldType.IsAssignableFrom(data) ? null : $"'{component.Name}.{field}' does not take a '{data.Name}'";
        }

        public void Bind(PrepareContext context, GameObject host, string surface, string origin, string subject, PrepareReport report, bool apply)
        {
            if (Unavailable != null) return;
            var existing = host.GetComponent(component);
            if (Keeps(existing, surface, origin, subject, report)) return;
            if (surface == null)
            {
                report.Add(PrepareLevel.Warning, subject, Check, "no surface: neither the part's name, its materials nor the model's name names one");
                return;
            }
            var asset = find(data, surface, context.NearPath);
            if (asset == null)
            {
                report.Add(PrepareLevel.Warning, subject, Check, $"no {data.Name} asset named '{surface}' ({origin} says {surface})");
                return;
            }
            report.Add(PrepareLevel.Changed, subject, Check, $"{component.Name} '{asset.name}' ({origin} says {surface})");
            if (apply) Set(context, existing != null ? existing : context.AddComponent(host, component), asset, subject, report);
        }

        // A surface already set stays: the binding is the game's to change.
        bool Keeps(Component existing, string surface, string origin, string subject, PrepareReport report)
        {
            var current = existing != null ? new SerializedObject(existing).FindProperty(field)?.objectReferenceValue : null;
            if (current == null) return false;
            string says = surface != null && !string.Equals(current.name, surface, StringComparison.OrdinalIgnoreCase) ? $" ({origin} says {surface})" : "";
            report.Add(PrepareLevel.Unchanged, subject, Check, $"keeps {component.Name} '{current.name}'{says}");
            return true;
        }

        void Set(PrepareContext context, Component target, Object asset, string subject, PrepareReport report)
        {
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(field);
            if (property == null || property.propertyType != SerializedPropertyType.ObjectReference)
            {
                report.Add(PrepareLevel.Warning, subject, Check, $"{component.Name} has no serialized object field '{field}'; its surface is not set");
                return;
            }
            property.objectReferenceValue = asset;
            if (context.UsesUndo) serialized.ApplyModifiedProperties();
            else serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>The type of that full or short name derived from <paramref name="baseType"/>; the full name wins.</summary>
        internal static Type FindType(string name, Type baseType)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            var types = TypeCache.GetTypesDerivedFrom(baseType);
            return types.FirstOrDefault(t => !t.IsAbstract && t.FullName == name) ?? types.FirstOrDefault(t => !t.IsAbstract && t.Name == name);
        }

        /// <summary>
        /// The field of that name Unity serializes on the type or a base type: a public instance
        /// field not marked [NonSerialized], or one marked [SerializeField].
        /// </summary>
        static FieldInfo FindField(Type type, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var open = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (open != null) return open.IsNotSerialized ? null : open;
            return TypeCache.GetFieldsWithAttribute<SerializeField>()
                .FirstOrDefault(f => f.Name == name && !f.IsStatic && f.DeclaringType != null && f.DeclaringType.IsAssignableFrom(type));
        }

        /// <summary>The project's asset of <paramref name="type"/> named <paramref name="name"/>; of several, the one sharing the longest folder path with <paramref name="near"/>.</summary>
        static Object FindAsset(Type type, string name, string near)
        {
            var named = AssetDatabase.FindAssets($"t:{type.Name} {name}")
                .Select(AssetDatabase.GUIDToAssetPath).Distinct(StringComparer.Ordinal)
                .Select(path => (path, asset: AssetDatabase.LoadAssetAtPath(path, type)))
                .Where(p => p.asset != null && string.Equals(p.asset.name, name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => SharedFolders(p.path, near)).ThenBy(p => p.path, StringComparer.Ordinal)
                .ToList();
            return named.Count > 0 ? named[0].asset : null;
        }

        /// <summary>How many leading folders two asset paths share.</summary>
        internal static int SharedFolders(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
            var x = a.Split('/'); var y = b.Split('/');
            int n = 0;
            while (n < x.Length - 1 && n < y.Length - 1 && string.Equals(x[n], y[n], StringComparison.OrdinalIgnoreCase)) ++n;
            return n;
        }
    }

    internal static class SurfaceBinding
    {
        /// <summary>The binding the project's settings describe.</summary>
        internal static ISurfaceBinding FromSettings() => new ComponentSurfaceBinding(MeshLabProjectSettings.Instance.surfaceBinding);

        /// <summary>The project's surface rules as a vocabulary for <see cref="MeshNaming.ParseTags"/>.</summary>
        internal static MeshNaming.Vocabulary Vocabulary(SurfaceBindingSettings settings = null)
        {
            settings = settings ?? MeshLabProjectSettings.Instance.surfaceBinding;
            var rules = settings?.rules ?? new List<SurfaceRule>();
            return new MeshNaming.Vocabulary(MeshNaming.Vocabulary.Default.ClassPrefixes,
                rules.Where(r => r != null).Select(r => (r.keyword?.Trim(), r.asset?.Trim())).ToArray());
        }

        /// <summary>
        /// The surface a collider is made of and where it was read: the part's name, else the
        /// first of its materials' names that names one, else the model's name; (null, null) when none does.
        /// </summary>
        internal static (string surface, string origin) Resolve(string part, IEnumerable<string> materials, string model, MeshNaming.Vocabulary vocabulary)
        {
            string surface = MeshNaming.ParseTags(part, vocabulary).Surface;
            if (surface != null) return (surface, $"the name '{part}'");
            foreach (string material in materials ?? Enumerable.Empty<string>())
            {
                surface = MeshNaming.ParseTags(material, vocabulary).Surface;
                if (surface != null) return (surface, $"the material '{material}'");
            }
            surface = MeshNaming.ParseTags(model, vocabulary).Surface;
            return surface != null ? (surface, $"the model's name '{model}'") : (null, null);
        }
    }
}
