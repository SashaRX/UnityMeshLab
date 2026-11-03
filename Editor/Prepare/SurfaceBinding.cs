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
            if (settings == null || string.IsNullOrWhiteSpace(settings.componentType))
            {
                Unavailable = "no surface component is set (Project Settings ▸ Mesh Lab ▸ Prepare)";
                return;
            }
            component = FindType(settings.componentType, typeof(Component));
            data = FindType(settings.dataType, typeof(Object));
            field = settings.field?.Trim();
            if (component == null) Unavailable = $"there is no component type '{settings.componentType}' in this project";
            else if (data == null) Unavailable = $"there is no asset type '{settings.dataType}' in this project";
            else
            {
                var info = FindField(component, field);
                if (info == null) Unavailable = $"'{component.Name}' has no field '{field}'";
                else if (info.IsPublic ? info.IsNotSerialized : !info.IsDefined(typeof(SerializeField), true))
                    Unavailable = $"'{component.Name}.{field}' is not serialized";
                else if (!info.FieldType.IsAssignableFrom(data)) Unavailable = $"'{component.Name}.{field}' does not take a '{data.Name}'";
            }
        }

        public string Unavailable { get; }

        public void Bind(PrepareContext context, GameObject host, string surface, string origin, string subject, PrepareReport report, bool apply)
        {
            if (Unavailable != null) return;
            var existing = host.GetComponent(component);
            if (existing != null)
            {
                var current = new SerializedObject(existing).FindProperty(field)?.objectReferenceValue;
                if (current != null)
                {
                    report.Add(PrepareLevel.Unchanged, subject, Check, $"keeps {component.Name} '{current.name}'"
                        + (surface != null && !string.Equals(current.name, surface, StringComparison.OrdinalIgnoreCase) ? $" ({origin} says {surface})" : ""));
                    return;
                }
            }
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
            if (!apply) return;
            var target = existing != null ? existing : context.AddComponent(host, component);
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

        /// <summary>An instance field of that name on the type or a base type, public or not.</summary>
        static FieldInfo FindField(Type type, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (var t = type; t != null && t != typeof(Component); t = t.BaseType)
            {
                var info = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (info != null) return info;
            }
            return null;
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
