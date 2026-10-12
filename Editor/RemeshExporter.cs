using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Writes a baked <see cref="RemeshPipeline"/> result to a new, uniquely named folder:
    /// the five maps per node (imported with the right texture settings), one material
    /// per node, the meshes and a prefab. A single-node (weld) result ships as an FBX
    /// when the FBX exporter package is present, otherwise as a mesh asset; a
    /// keep-hierarchy result is always mesh assets under a rebuilt root prefab.
    /// The shared footer can also replace a source FBX with the complete baked model.
    /// A failure before the file commits deletes the new output folder again.
    /// </summary>
    internal static class RemeshExporter
    {
        static readonly string[] MapNames = { "BaseColor", "Normal", "MetallicSmoothness", "Occlusion", "Emission" };
        const int EmissionMap = 4;

        /// <summary>
        /// "<source asset folder>/remesh" when the source root resolves to an imported
        /// asset (a prefab/model instance, or any mesh asset under it), created if
        /// missing; null when the source is scene-only and a folder must be picked.
        /// </summary>
        public static string ResolveDefaultFolder(GameObject source)
        {
            string assetPath = null;
            if (source) {
                assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
                if (string.IsNullOrEmpty(assetPath)) {
                    var filter = source.GetComponentInChildren<MeshFilter>();
                    if (filter && filter.sharedMesh) assetPath = AssetDatabase.GetAssetPath(filter.sharedMesh);
                }
            }
            return ResolveDefaultFolder(assetPath);
        }

        internal static string ResolveDefaultFolder(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;
            string dir = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(dir)) return null;
            dir = dir.Replace('\\', '/');
            if (!dir.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !AssetDatabase.IsValidFolder(dir))
                return null;
            string remesh = dir + "/remesh";
            if (!AssetDatabase.IsValidFolder(remesh)) AssetDatabase.CreateFolder(dir, "remesh");
            return AssetDatabase.IsValidFolder(remesh) ? remesh : null;
        }

        /// <summary>Folder picker restricted to Assets; null with a reason when declined or invalid.</summary>
        public static string PickFolder(out string error)
        {
            error = null;
            string parent = EditorUtility.OpenFolderPanel("Save Remesh Result", Application.dataPath, "");
            if (string.IsNullOrEmpty(parent)) return null;
            parent = parent.Replace('\\', '/');
            string assets = Application.dataPath.Replace('\\', '/');
            if (parent != assets && !parent.StartsWith(assets + "/", StringComparison.Ordinal)) {
                error = "Choose a folder inside Assets."; return null;
            }
            string relative = "Assets" + parent.Substring(assets.Length);
            // The folder panel can create a directory the AssetDatabase has not imported
            // yet; GenerateUniqueAssetPath/CreateFolder then fail on the unknown parent.
            if (!AssetDatabase.IsValidFolder(relative)) AssetDatabase.Refresh();
            if (!AssetDatabase.IsValidFolder(relative)) {
                error = relative + " is not an imported asset folder. Refresh the Project window and save again."; return null;
            }
            return relative;
        }

        /// <summary>Exports the baked result into a new folder under parentFolder and
        /// returns the status line. Throws after rolling the folder back on failure.</summary>
        public static string Export(RemeshPipeline pipeline, RemeshSettings settings, string parentFolder, string outputFbxPath = null, bool meshAssetsOnly = false, bool fbxModel = false)
        {
            if (pipeline.Nodes.Count == 0 || !pipeline.Has(RemeshPipeline.Stage.Bake)) throw new InvalidOperationException("Nothing to save: run the bake first.");
            string clean = SafeName(pipeline.ResultName, "Remesh");
            string folder = AssetDatabase.GenerateUniqueAssetPath(parentFolder + "/" + clean + "_Remesh");
            bool createdFolder = false, sourceCommitted = false;
            bool sourceOverwrite = !string.IsNullOrEmpty(outputFbxPath) && System.IO.File.Exists(outputFbxPath);
            var temporary = new List<Object>();
            try {
                var model = new SavedModel(pipeline, settings, sourceOverwrite);
                string guid = AssetDatabase.CreateFolder(parentFolder, Path.GetFileName(folder));
                if (string.IsNullOrEmpty(guid)) throw new IOException("Could not create " + folder + ".");
                createdFolder = true;
                folder = AssetDatabase.GUIDToAssetPath(guid);

                var names = ExportNames(pipeline.Nodes, model.hierarchy, sourceOverwrite, outputFbxPath);
                var paths = WriteNodeMaps(folder, pipeline.Nodes, names, model.hierarchy);
                BuildResultModel(model, pipeline, settings, names, paths, sourceOverwrite, temporary);
                var ping = WriteResultModel(model, folder, clean, names, outputFbxPath, meshAssetsOnly, fbxModel,
                    () => sourceCommitted = sourceOverwrite);
                AssetDatabase.SaveAssets();
                EditorGUIUtility.PingObject(ping);
                string status = ExportStatus(pipeline, folder, model);
                UvtLog.Info("[Remesh export] " + status);
                return status;
            }
            catch (Exception e) {
                ReportFailure(folder, createdFolder, sourceCommitted, e);
                throw;
            }
            finally {
                DestroyTemporary(temporary);
            }
        }

        sealed class SavedModel
        {
            public readonly Shader shader;
            public readonly bool urp, hierarchy, normalize, sourceOverwrite;
            public readonly Material[] materials;
            public readonly Mesh[] meshes;
            public GameObject root;
            public bool twoSidedWarned;

            public SavedModel(RemeshPipeline pipeline, RemeshSettings settings, bool sourceOverwrite)
            {
                this.sourceOverwrite = sourceOverwrite;
                hierarchy = pipeline.IsHierarchy;
                normalize = settings.normalizeSize && !hierarchy && !sourceOverwrite;
                shader = ResolveShader(pipeline.Primary.maps.beauty, out urp);
                materials = new Material[pipeline.Nodes.Count];
                meshes = new Mesh[pipeline.Nodes.Count];
            }
        }

        static string[] ExportNames(IReadOnlyList<RemeshPipeline.Node> nodes, bool hierarchy, bool sourceOverwrite, string outputFbxPath)
        {
            var names = UniqueNames(nodes, hierarchy);
            if (!sourceOverwrite || nodes.Count != 1) return names;
            // Preserve the sub-asset file ID of a single-mesh FBX overwrite.
            var importedMeshes = new List<Mesh>();
            foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(outputFbxPath))
                if (asset is Mesh mesh) importedMeshes.Add(mesh);
            if (importedMeshes.Count == 1) names[0] = importedMeshes[0].name;
            return names;
        }

        static string[][] WriteNodeMaps(string folder, IReadOnlyList<RemeshPipeline.Node> nodes, string[] names, bool hierarchy)
        {
            // Close the write/import batch before looking up and configuring importers.
            var paths = new string[nodes.Count][];
            using (new AssetDatabase.AssetEditingScope())
                for (int i = 0; i < nodes.Count; ++i)
                    paths[i] = WriteMaps(folder, hierarchy ? names[i] + "_" : "", nodes[i].maps);
            using (new AssetDatabase.AssetEditingScope())
                for (int i = 0; i < nodes.Count; ++i) ConfigureMaps(paths[i], nodes[i].maps.size);
            return paths;
        }

        static void BuildResultModel(SavedModel model, RemeshPipeline pipeline, RemeshSettings settings,
            string[] names, string[][] paths, bool sourceOverwrite, List<Object> temporary)
        {
            if (settings.normalizeSize && model.hierarchy)
                UvtLog.Warn("[Remesh export] Keep-hierarchy saves keep the source root's scale on the prefab root; 'Normalize saved transform' applies to the single-mesh weld only.");
            string clean = SafeName(pipeline.ResultName, "Remesh");
            model.root = new GameObject(sourceOverwrite && !model.hierarchy ? names[0] : clean + "_LOD0");
            temporary.Add(model.root);
            model.root.transform.localScale = model.normalize || sourceOverwrite ? Vector3.one : pipeline.RootScale;
            model.root.transform.localRotation = model.normalize || sourceOverwrite ? Quaternion.identity : pipeline.RootRotation;
            for (int i = 0; i < pipeline.Nodes.Count; ++i)
                CreateNodeAssets(model, pipeline.Nodes[i], i, names[i], paths[i], sourceOverwrite, temporary);
        }

        static void CreateNodeAssets(SavedModel model, RemeshPipeline.Node node, int index, string name,
            string[] paths, bool sourceOverwrite, List<Object> temporary)
        {
            var material = CreateMaterial(model.shader, model.urp, node.maps.beauty, name + "_Remesh", paths);
            model.materials[index] = material;
            temporary.Add(material);
            ConfigureTwoSidedMaterial(model, node, material);
            var mesh = Object.Instantiate(node.mesh);
            model.meshes[index] = mesh;
            mesh.name = sourceOverwrite ? name : name + "_LOD0";
            mesh.hideFlags = HideFlags.None;
            temporary.Add(mesh);
            if (model.normalize)
            {
                var linear = node.spaceToWorld;
                linear.SetColumn(3, new Vector4(0, 0, 0, 1));
                MeshTransform.BakeMatrix(mesh, linear);
            }
            var go = model.hierarchy ? new GameObject(name) : model.root;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = material;
            if (!model.hierarchy) return;
            go.transform.localPosition = node.localPosition;
            go.transform.localRotation = node.localRotation;
            go.transform.localScale = node.localScale;
            go.transform.SetParent(model.root.transform, false);
        }

        static void ConfigureTwoSidedMaterial(SavedModel model, RemeshPipeline.Node node, Material material)
        {
            if (!node.twoSided) return;
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", (float)CullMode.Off);
                material.doubleSidedGI = true;
            }
            else if (!model.twoSidedWarned)
            {
                model.twoSidedWarned = true;
                UvtLog.Warn("[Remesh export] The source renders both sides, but " + model.shader.name + " has no two-sided mode; assign a two-sided shader to the result material or its back faces will be culled.");
            }
        }

        static Object WriteResultModel(SavedModel model, string folder, string clean, string[] names,
            string outputFbxPath, bool meshAssetsOnly, bool fbxModel, Action committed)
        {
            string prefabPath = folder + "/" + clean + ".prefab";
            using (new AssetDatabase.AssetEditingScope())
                for (int i = 0; i < names.Length; ++i)
                    AssetDatabase.CreateAsset(model.materials[i], folder + "/" + names[i] + ".mat");
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
            if (!meshAssetsOnly && (!model.hierarchy || outputFbxPath != null || fbxModel))
                return ExportFbx(outputFbxPath ?? folder + "/" + clean + ".fbx", model.root, model.materials, prefabPath,
                    model.normalize || model.sourceOverwrite, committed);
#endif
            using (new AssetDatabase.AssetEditingScope())
                for (int i = 0; i < names.Length; ++i)
                    AssetDatabase.CreateAsset(model.meshes[i], folder + "/" + names[i] + "_LOD0.asset");
            PrefabUtility.SaveAsPrefabAsset(model.root, prefabPath, out bool success);
            if (!success) throw new IOException("Prefab save failed.");
            return AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        }

        static string ExportStatus(RemeshPipeline pipeline, string folder, SavedModel model)
        {
            string status = model.hierarchy ? $"Saved: {folder} ({pipeline.Nodes.Count} node(s))" : "Saved: " + folder;
            if (model.twoSidedWarned) return status + ". Two-sided source: assign a two-sided shader (see Console).";
            return pipeline.ResultTwoSided ? status + ". Material renders both sides." : status;
        }

        static void ReportFailure(string folder, bool createdFolder, bool sourceCommitted, Exception error)
        {
            if (createdFolder && !sourceCommitted && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            if (sourceCommitted) UvtLog.Error("[Remesh export] FBX committed; maps and materials retained at " + folder + ". Fix the import and reimport the FBX.");
            UvtLog.Error("[Remesh export] " + error);
        }

        static void DestroyTemporary(List<Object> temporary)
        {
            // Persistent assets are now owned by the AssetDatabase.
            foreach (var item in temporary)
                if (item && !AssetDatabase.Contains(item)) Object.DestroyImmediate(item);
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        // The model ships as an FBX (geometry, split normals, UV0, tangents, vertex
        // colors); the material stays a curated asset next to it that the prefab
        // references. This is brand-new geometry in a uniquely created folder, not a
        // channel re-save of a source FBX, so the isolated-export core's same-vertex-
        // count snapshot contract does not apply; a failed export still rolls the
        // whole folder back.
        static Object ExportFbx(string fbxPath, GameObject root, Material[] materials, string prefabPath,
            bool normalizedTransforms, Action committed)
        {
            MeshAssetOperations.WriteModelResult(fbxPath, root, materials, normalizedTransforms, committed);
            var fbxRoot = AssetDatabase.LoadMainAssetAtPath(fbxPath) as GameObject;
            if (!fbxRoot) throw new IOException("FBX reimport failed.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
            try {
                var renderers = instance.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < renderers.Length && i < materials.Length; i++)
                    renderers[i].sharedMaterial = materials[i];
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
                if (!success) throw new IOException("Prefab save failed.");
            }
            finally { Object.DestroyImmediate(instance); }
            return fbxRoot;
        }
#endif

        // ── pieces ──

        internal static Shader ResolveShader(bool unlit, out bool urp)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            urp = pipeline != null;
            if (urp && !pipeline.GetType().Name.Contains("Universal"))
                throw new InvalidOperationException("Result materials support Built-in and URP; HDRP export is not implemented.");
            string shaderName = unlit ? (urp ? "Universal Render Pipeline/Unlit" : "Unlit/Texture")
                : urp ? "Universal Render Pipeline/Lit" : "Standard";
            var shader = Shader.Find(shaderName);
            if (!shader) throw new InvalidOperationException("Result shader is unavailable: " + shaderName);
            return shader;
        }

        // Node file names: the sanitized node name, made unique; the weld uses the
        // result name itself.
        static string[] UniqueNames(IReadOnlyList<RemeshPipeline.Node> nodes, bool hierarchy)
        {
            var names = new string[nodes.Count];
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nodes.Count; ++i) {
                string clean = SafeName(nodes[i].name, hierarchy ? "Node" : "Remesh");
                string unique = clean;
                int suffix = 2;
                while (!used.Add(unique)) unique = clean + "_" + suffix++;
                names[i] = unique;
            }
            return names;
        }

        internal static string SafeName(string name, string fallback)
        {
            string clean = name ?? "";
            foreach (char c in Path.GetInvalidFileNameChars()) clean = clean.Replace(c, '_');
            clean = clean.Replace('/', '_').Replace('\\', '_');
            return string.IsNullOrWhiteSpace(clean) ? fallback : clean;
        }

        // Writes and imports the five maps; call inside an AssetEditingScope. These are
        // new exports in a uniquely created folder, never source-asset writes.
        static string[] WriteMaps(string folder, string prefix, RemeshBaker.Maps maps)
        {
            var paths = new string[MapNames.Length];
            for (int i = 0; i < paths.Length; ++i) {
                paths[i] = folder + "/" + prefix + MapNames[i] + (i == EmissionMap ? ".exr" : ".png");
                File.WriteAllBytes(paths[i], Encode(maps, i));
                AssetDatabase.ImportAsset(paths[i]);
            }
            return paths;
        }

        // Base color is sRGB, the normal map a normal map, the rest linear data.
        static void ConfigureMaps(string[] paths, int size)
        {
            for (int i = 0; i < paths.Length; ++i)
                TextureAssets.Configure(paths[i],
                    i == 0 ? TextureAssets.Kind.Color : i == 1 ? TextureAssets.Kind.NormalMap : TextureAssets.Kind.Linear, size);
        }

        static Material CreateMaterial(Shader shader, bool urp, bool unlit, string name, string[] paths)
        {
            var material = new Material(shader) { name = name };
            var maps = new Texture[paths.Length];
            for (int i = 0; i < paths.Length; ++i) maps[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(paths[i]);
            ConfigureMaterial(material, urp, unlit, maps);
            return material;
        }

        // Preview and export use the same shader, channels and strengths. Preview
        // textures are transient; exported ones are imported assets.
        internal static void ConfigureMaterial(Material material, bool urp, bool unlit, IReadOnlyList<Texture> maps)
        {
            material.SetTexture(urp ? "_BaseMap" : "_MainTex", maps[0]);
            string colorProperty = urp ? "_BaseColor" : "_Color";
            if (material.HasProperty(colorProperty)) material.SetColor(colorProperty, Color.white);
            if (unlit) {
                // One lit texture in, one texture out — the other maps still export
                // alongside for reference, but nothing samples them.
                return;
            }
            material.SetTexture("_BumpMap", maps[1]);
            material.SetTexture("_MetallicGlossMap", maps[2]);
            material.SetTexture("_OcclusionMap", maps[3]);
            material.SetTexture("_EmissionMap", maps[4]);
            material.SetColor("_EmissionColor", maps[4] ? Color.white : Color.black);
            material.SetFloat("_Metallic", maps[2] ? 1 : 0); material.SetFloat(urp ? "_Smoothness" : "_GlossMapScale", maps[2] ? 1 : 0);
            if (!urp) material.SetFloat("_Glossiness", 0);
            material.SetFloat("_BumpScale", 1); material.SetFloat("_OcclusionStrength", 1);
            SetKeyword(material, "_NORMALMAP", maps[1]);
            SetKeyword(material, "_EMISSION", maps[4]);
            SetKeyword(material, urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP", maps[2]);
            if (urp) SetKeyword(material, "_OCCLUSIONMAP", maps[3]);
        }

        static void SetKeyword(Material material, string keyword, bool enabled)
        {
            if (enabled) material.EnableKeyword(keyword); else material.DisableKeyword(keyword);
        }

        // Bakes a world scale into the vertex data so the saved root sits at scale 1:
        // positions and tangents (direction vectors) scale component-wise, normals take
        // the inverse (inverse-transpose of a diagonal matrix); both renormalize and the
        // tangent is re-orthogonalized against its normal. A negative determinant
        // mirrors the mesh, so the winding and the tangent handedness flip with it.
        internal static void BakeScaleIntoMesh(Mesh mesh, Vector3 scale)
        {
            if (Mathf.Approximately(scale.x, 1) && Mathf.Approximately(scale.y, 1) && Mathf.Approximately(scale.z, 1)) return;
            if (Mathf.Abs(scale.x) < 1e-6f || Mathf.Abs(scale.y) < 1e-6f || Mathf.Abs(scale.z) < 1e-6f) return;
            MeshTransform.BakeMatrix(mesh, Matrix4x4.Scale(scale));
        }

        static byte[] Encode(RemeshBaker.Maps maps, int index)
        {
            var bytes = index == EmissionMap
                ? TextureAssets.EncodeExr(maps.emission, maps.size, maps.size)
                : TextureAssets.EncodePng(index == 0 ? maps.color : index == 1 ? maps.normal : index == 2 ? maps.metal : maps.ao, maps.size, maps.size);
            // A missing or mis-sized buffer is a bake bug; say which map, and let the
            // export roll its folder back as it does for every other failure.
            if (bytes == null) throw new InvalidOperationException($"Map '{MapNames[index]}' has no {maps.size}×{maps.size} pixel buffer to write.");
            return bytes;
        }
    }
}
