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
    /// Source assets are never written; a failed export deletes the folder again.
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
        public static string Export(RemeshPipeline pipeline, RemeshSettings settings, string parentFolder)
        {
            if (pipeline.Nodes.Count == 0 || !pipeline.Has(RemeshPipeline.Stage.Bake)) throw new InvalidOperationException("Nothing to save: run the bake first.");
            string clean = SafeName(pipeline.ResultName, "Remesh");
            string folder = AssetDatabase.GenerateUniqueAssetPath(parentFolder + "/" + clean + "_Remesh");
            bool createdFolder = false;
            var temporary = new List<Object>();
            try {
                // Beauty bakes fold the lighting into the BaseColor; the result shades
                // Unlit (pipeline-agnostic) instead of Standard/URP-Lit.
                bool unlit = pipeline.Primary.maps.beauty;
                var shader = ResolveShader(unlit, out bool urp);
                string guid = AssetDatabase.CreateFolder(parentFolder, Path.GetFileName(folder));
                if (string.IsNullOrEmpty(guid)) throw new IOException("Could not create " + folder + ".");
                createdFolder = true;
                folder = AssetDatabase.GUIDToAssetPath(guid);

                bool hierarchy = pipeline.IsHierarchy;
                var nodes = pipeline.Nodes;
                var names = UniqueNames(nodes, hierarchy);
                // Maps: write + import every file in one batch, then configure the
                // importers in a second batch — imports are deferred until the first
                // scope closes, so an importer lookup inside it would come back null.
                var paths = new string[nodes.Count][];
                using (new AssetDatabase.AssetEditingScope())
                    for (int i = 0; i < nodes.Count; ++i)
                        paths[i] = WriteMaps(folder, hierarchy ? names[i] + "_" : "", nodes[i].maps);
                using (new AssetDatabase.AssetEditingScope())
                    for (int i = 0; i < nodes.Count; ++i) ConfigureMaps(paths[i], nodes[i].maps.size);

                // Materials, meshes and the object tree. The result meshes live in their
                // capture space; the weld bakes the root's world scale into the vertices
                // when Normalize size is on (real size at scale 1), a hierarchy keeps the
                // root scale on the prefab root (no single vertex space to bake it into).
                bool normalize = settings.normalizeSize && !hierarchy;
                if (settings.normalizeSize && hierarchy)
                    UvtLog.Warn("[Remesh export] Keep-hierarchy saves keep the source root's scale on the prefab root; 'Normalize size' applies to the single-mesh weld only.");
                var root = new GameObject(clean + "_LOD0");
                temporary.Add(root);
                root.transform.localScale = normalize ? Vector3.one : pipeline.RootScale;
                var materials = new Material[nodes.Count];
                var meshes = new Mesh[nodes.Count];
                bool twoSidedWarned = false;
                for (int i = 0; i < nodes.Count; ++i) {
                    var node = nodes[i];
                    materials[i] = CreateMaterial(shader, urp, node.maps.beauty, names[i] + "_Remesh", paths[i]);
                    // A source that renders both sides keeps one sheet in the result; the
                    // material renders both sides of it. URP Lit/Unlit expose the cull mode;
                    // Standard and Unlit/Texture do not, so the user is told once.
                    if (node.twoSided) {
                        if (materials[i].HasProperty("_Cull")) { materials[i].SetFloat("_Cull", (float)CullMode.Off); materials[i].doubleSidedGI = true; }
                        else if (!twoSidedWarned) {
                            twoSidedWarned = true;
                            UvtLog.Warn("[Remesh export] The source renders both sides, but " + shader.name + " has no two-sided mode; assign a two-sided shader to the result material or its back faces will be culled.");
                        }
                    }
                    temporary.Add(materials[i]);
                    meshes[i] = Object.Instantiate(node.mesh);
                    meshes[i].name = names[i] + "_LOD0"; meshes[i].hideFlags = HideFlags.None;
                    temporary.Add(meshes[i]);
                    if (normalize) BakeScaleIntoMesh(meshes[i], pipeline.RootScale);
                    var go = hierarchy ? new GameObject(names[i]) : root;
                    go.AddComponent<MeshFilter>().sharedMesh = meshes[i];
                    go.AddComponent<MeshRenderer>().sharedMaterial = materials[i];
                    if (hierarchy) {
                        go.transform.localPosition = node.localPosition;
                        go.transform.localRotation = node.localRotation;
                        go.transform.localScale = node.localScale;
                        go.transform.SetParent(root.transform, false);
                    }
                }

                string prefabPath = folder + "/" + clean + ".prefab";
                using (new AssetDatabase.AssetEditingScope())
                    for (int i = 0; i < nodes.Count; ++i)
                        AssetDatabase.CreateAsset(materials[i], folder + "/" + names[i] + ".mat");
                Object ping;
#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
                if (!hierarchy) {
                    ping = ExportFbx(folder + "/" + clean + ".fbx", root, materials[0], prefabPath, settings.embedFbxTextures);
                }
                else
#endif
                {
                    using (new AssetDatabase.AssetEditingScope())
                        for (int i = 0; i < nodes.Count; ++i)
                            AssetDatabase.CreateAsset(meshes[i], folder + "/" + names[i] + "_LOD0.asset");
                    PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out bool success);
                    if (!success) throw new IOException("Prefab save failed.");
                    ping = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                }
                AssetDatabase.SaveAssets();
                EditorGUIUtility.PingObject(ping);
                string status = (hierarchy ? $"Saved: {folder} ({nodes.Count} node(s))" : "Saved: " + folder) +
                    (twoSidedWarned ? ". Two-sided source: assign a two-sided shader (see Console)." : pipeline.ResultTwoSided ? ". Material renders both sides." : "");
                UvtLog.Info("[Remesh export] " + status);
                return status;
            }
            catch (Exception e) {
                if (createdFolder && AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
                UvtLog.Error("[Remesh export] " + e);
                throw;
            }
            finally {
                // Everything that became an asset is owned by the AssetDatabase now; the
                // rest (the scene-less object tree, and all of it on failure) goes.
                foreach (var o in temporary)
                    if (o && !AssetDatabase.Contains(o)) Object.DestroyImmediate(o);
            }
        }

#if LIGHTMAP_UV_TOOL_FBX_EXPORTER
        // The model ships as an FBX (geometry, split normals, UV0, tangents, vertex
        // colors); the material stays a curated asset next to it that the prefab
        // references. This is brand-new geometry in a uniquely created folder, not a
        // channel re-save of a source FBX, so the isolated-export core's same-vertex-
        // count snapshot contract does not apply; a failed export still rolls the
        // whole folder back.
        static Object ExportFbx(string fbxPath, GameObject root, Material material, string prefabPath, bool embedTextures)
        {
            // Embedded maps make the FBX self-contained; a linked FBX would reference
            // this machine's absolute paths instead.
            FbxExport.Write(fbxPath, root, embedTextures);
            var modelImporter = (ModelImporter)AssetImporter.GetAtPath(fbxPath);
            if (modelImporter != null) {
                // The curated material + prefab ship next to the FBX; keep the importer
                // from generating a duplicate MaterialDescription copy.
                modelImporter.materialImportMode = ModelImporterMaterialImportMode.None;
                // The normal map was baked against the exported tangent frame; a
                // recalculation (MikkT default) can flip its handedness per vertex,
                // which green-flips chunks of the baked map.
                modelImporter.importNormals = ModelImporterNormals.Import;
                modelImporter.importTangents = ModelImporterTangents.Import;
                modelImporter.SaveAndReimport();
            }
            var fbxRoot = AssetDatabase.LoadMainAssetAtPath(fbxPath) as GameObject;
            if (!fbxRoot) throw new IOException("FBX reimport failed.");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
            try {
                instance.GetComponentInChildren<MeshRenderer>().sharedMaterial = material;
                PrefabUtility.SaveAsPrefabAsset(instance, prefabPath, out bool success);
                if (!success) throw new IOException("Prefab save failed.");
            }
            finally { Object.DestroyImmediate(instance); }
            return fbxRoot;
        }
#endif

        // ── pieces ──

        static Shader ResolveShader(bool unlit, out bool urp)
        {
            var pipeline = GraphicsSettings.currentRenderPipeline;
            urp = !unlit && pipeline != null;
            if (urp && !pipeline.GetType().Name.Contains("Universal"))
                throw new InvalidOperationException("Result materials support Built-in and URP; HDRP export is not implemented.");
            string shaderName = unlit ? "Unlit/Texture" : urp ? "Universal Render Pipeline/Lit" : "Standard";
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

        static void ConfigureMaps(string[] paths, int size)
        {
            for (int i = 0; i < paths.Length; ++i) {
                var importer = AssetImporter.GetAtPath(paths[i]) as TextureImporter;
                if (importer == null) throw new IOException("Texture import failed: " + paths[i]);
                importer.textureType = i == 1 ? TextureImporterType.NormalMap : TextureImporterType.Default;
                importer.sRGBTexture = i == 0; importer.wrapMode = TextureWrapMode.Clamp;
                importer.maxTextureSize = size; importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = true; importer.alphaSource = TextureImporterAlphaSource.FromInput;
                importer.SaveAndReimport();
            }
        }

        static Material CreateMaterial(Shader shader, bool urp, bool unlit, string name, string[] paths)
        {
            var material = new Material(shader) { name = name };
            Texture2D Map(int i) => AssetDatabase.LoadAssetAtPath<Texture2D>(paths[i]);
            if (unlit) {
                // One lit texture in, one texture out — the other maps still export
                // alongside for reference, but nothing samples them.
                material.SetTexture("_MainTex", Map(0));
                return material;
            }
            material.SetTexture(urp ? "_BaseMap" : "_MainTex", Map(0));
            material.SetColor(urp ? "_BaseColor" : "_Color", Color.white);
            material.SetTexture("_BumpMap", Map(1));
            material.SetTexture("_MetallicGlossMap", Map(2));
            material.SetTexture("_OcclusionMap", Map(3));
            material.SetTexture("_EmissionMap", Map(4));
            material.SetColor("_EmissionColor", Color.white);
            material.SetFloat("_Metallic", 1); material.SetFloat(urp ? "_Smoothness" : "_GlossMapScale", 1);
            material.SetFloat("_BumpScale", 1); material.SetFloat("_OcclusionStrength", 1);
            material.EnableKeyword("_NORMALMAP"); material.EnableKeyword("_EMISSION");
            material.EnableKeyword(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
            if (urp) material.EnableKeyword("_OCCLUSIONMAP");
            return material;
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
            var texture = new Texture2D(maps.size, maps.size,
                index == EmissionMap ? TextureFormat.RGBAFloat : TextureFormat.RGBA32, false, true);
            try {
                if (index == EmissionMap) { texture.SetPixels(maps.emission); texture.Apply(); return texture.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat); }
                texture.SetPixels32(index == 0 ? maps.color : index == 1 ? maps.normal : index == 2 ? maps.metal : maps.ao);
                texture.Apply(); return texture.EncodeToPNG();
            }
            finally { Object.DestroyImmediate(texture); }
        }
    }
}
