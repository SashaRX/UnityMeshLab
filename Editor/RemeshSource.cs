using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    // All Unity objects are read on the main thread. Workers only see these snapshots.
    internal sealed class RemeshSource
    {
        public Vector3[] positions, normals;
        public Vector4[] tangents;
        public Vector2[] uv;
        public int[] indices, faceMaterials;
        public Surface[] materials;
        public float diagonal;
        public string[] warnings = Array.Empty<string>();

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

        public static RemeshSource Capture(GameObject root)
        {
            if (!root) throw new ArgumentException("Select a source root.");
            if (root.GetComponentInChildren<SkinnedMeshRenderer>() != null)
                throw new InvalidOperationException("Remesh & Bake currently accepts static MeshRenderers. Bake skinned geometry to a static mesh first.");
            var excluded = new HashSet<Renderer>();
            foreach (var group in root.GetComponentsInChildren<LODGroup>()) {
                var lods = group.GetLODs();
                var first = new HashSet<Renderer>(lods.Length > 0 ? lods[0].renderers : Array.Empty<Renderer>());
                for (int l = 1; l < lods.Length; ++l)
                    foreach (var r in lods[l].renderers) if (!first.Contains(r)) excluded.Add(r);
            }
            var positions = new List<Vector3>(); var normals = new List<Vector3>();
            var tangents = new List<Vector4>(); var uv = new List<Vector2>();
            var indices = new List<int>(); var faces = new List<int>(); var materials = new List<Surface>();
            var materialIds = new Dictionary<Material, int>();
            string[] warnings;
            using (var reader = new Reader()) {
                foreach (var renderer in root.GetComponentsInChildren<MeshRenderer>()) {
                    if (!renderer.enabled || excluded.Contains(renderer) || MeshHygieneUtility.IsCollisionNodeName(renderer.name)) continue;
                    var filter = renderer.GetComponent<MeshFilter>();
                    if (!filter || !filter.sharedMesh) continue;
                    var sourceMesh = filter.sharedMesh;
                    for (int sub = 0; sub < sourceMesh.subMeshCount; ++sub)
                        if (sourceMesh.GetTopology(sub) != MeshTopology.Triangles) throw new InvalidOperationException(renderer.name + ": only triangle meshes are supported.");
                    // An Instantiate clone of a Read/Write-disabled import carries no CPU data,
                    // but editor code can still read the imported asset; copy from that.
                    var mesh = UvCanvasView.MakeReadableCopy(sourceMesh);
                    try {
                        if (mesh.uv.Length != mesh.vertexCount)
                            throw new InvalidOperationException(renderer.name + " needs source UV0 for material transfer.");
                        if (mesh.normals.Length != mesh.vertexCount) mesh.RecalculateNormals();
                        if (mesh.tangents.Length != mesh.vertexCount) mesh.RecalculateTangents();
                        var p = mesh.vertices; var n = mesh.normals; var t = mesh.tangents;
                        if (t.Length != p.Length) throw new InvalidOperationException(renderer.name + " has no valid tangent frame.");
                        var transform = root.transform.worldToLocalMatrix * renderer.localToWorldMatrix;
                        if (Mathf.Abs(transform.determinant) < 1e-12f) throw new InvalidOperationException("Zero-scale source transform.");
                        var normalTransform = transform.inverse.transpose;
                        float sign = transform.determinant < 0 ? -1 : 1;
                        int first = positions.Count;
                        for (int i = 0; i < p.Length; ++i) {
                            positions.Add(transform.MultiplyPoint3x4(p[i]));
                            Vector3 nn = normalTransform.MultiplyVector(n[i]).normalized;
                            normals.Add(nn);
                            Vector3 tt = transform.MultiplyVector(new Vector3(t[i].x, t[i].y, t[i].z));
                            tt = (tt - nn * Vector3.Dot(nn, tt)).normalized;
                            tangents.Add(new Vector4(tt.x, tt.y, tt.z, t[i].w * sign));
                        }
                        uv.AddRange(mesh.uv);
                        var shared = renderer.sharedMaterials;
                        for (int sub = 0; sub < mesh.subMeshCount; ++sub) {
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
                            }
                        }
                    }
                    finally { Object.DestroyImmediate(mesh); }
                }
                warnings = reader.warnings.ToArray();
            }
            if (indices.Count == 0) throw new InvalidOperationException("No static source triangles found.");
            var bounds = new Bounds(positions[0], Vector3.zero);
            foreach (var p in positions) bounds.Encapsulate(p);
            if (bounds.size.magnitude <= 1e-8f) throw new InvalidOperationException("Source bounds are empty.");
            return new RemeshSource { positions = positions.ToArray(), normals = normals.ToArray(), tangents = tangents.ToArray(),
                uv = uv.ToArray(), indices = indices.ToArray(), faceMaterials = faces.ToArray(), materials = materials.ToArray(),
                diagonal = bounds.size.magnitude, warnings = warnings };
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
