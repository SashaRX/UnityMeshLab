using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Scene lighting captured on the main thread at bake start and sampled from the
    /// bake workers: realtime/mixed direct light (shadowed by rays through the source
    /// BVH), baked lightmaps (decoded to linear at readback — RGBM on desktop, direct
    /// HDR), the ambient probe (evaluated by Unity itself into a direction grid the
    /// workers interpolate) and reflection probes (equirectangular readbacks).
    /// Everything is pre-transformed into the source root's local space so workers
    /// stay in the capture's coordinate system. Baked-only lights are skipped — their
    /// contribution already lives in the lightmap, so nothing is counted twice.
    /// </summary>
    internal sealed class RemeshBeauty
    {
        internal sealed class Lightmap
        {
            public Color[] pixels;            // linear, decoded
            public int width, height;
            public Vector4 st;                // renderer.lightmapScaleOffset: uv2 * xy + zw
        }

        internal sealed class Probe
        {
            public Color[] pixels;            // linear equirect: u = atan2(z,x)/2π, v = acos(y)/π
            public int width, height;
            public Bounds bounds;             // world space
            public bool bounded;
            public float importance;
            public Color average;
        }

        struct Directional { public Vector3 dir; public Color color; public float shadowStrength; }
        struct Local
        {
            public Vector3 pos, axis;         // spot axis in local space (axis used when spot)
            public Color color;
            public float rangeSqr, range, innerCos, outerCos;
            public bool spot;
        }

        const int AmbientW = 32, AmbientH = 16;

        readonly Directional[] directionals = Array.Empty<Directional>();
        readonly Local[] locals = Array.Empty<Local>();
        readonly Probe[] probes;
        readonly Probe fallbackProbe;
        readonly Color[] ambientGrid;         // evaluated by Unity on the main thread
        readonly bool flatAmbient, trilightAmbient;
        readonly Color ambientFlat, ambientSky, ambientEquator, ambientGround;
        readonly Vector3 viewPosition;        // local space; specular is baked for this vantage
        readonly Matrix4x4 localToWorld;
        TriangleBvh bvh;                      // bound by the bake before workers run
        float shadowEpsilon;

        /// <summary>Capture the lighting environment around root. Main thread only.</summary>
        internal RemeshBeauty(GameObject root, float sourceDiagonal)
        {
            var rootTransform = root.transform;
            Matrix4x4 worldToLocal = rootTransform.worldToLocalMatrix;
            localToWorld = rootTransform.localToWorldMatrix;

            var dirList = new List<Directional>();
            var locList = new List<Local>();
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) {
                if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
                // Baked-only lights live in the lightmap; including them again would
                // double-count. URP has no subtractive mode, so Mixed direct is realtime.
                if (light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                Color color = light.color.linear * light.intensity;
                if (light.type == LightType.Directional) {
                    dirList.Add(new Directional {
                        dir = worldToLocal.MultiplyVector(-(light.transform.rotation * Vector3.forward)).normalized,
                        color = color,
                        shadowStrength = light.shadows == LightShadows.None ? 0f : light.shadowStrength,
                    });
                }
                else if (light.type == LightType.Point) {
                    locList.Add(new Local { pos = worldToLocal.MultiplyPoint3x4(light.transform.position),
                        color = color, rangeSqr = light.range * light.range, range = light.range });
                }
                else if (light.type == LightType.Spot) {
                    float outer = light.spotAngle * 0.5f * Mathf.Deg2Rad;
                    float inner = Mathf.Min(light.innerSpotAngle * 0.5f * Mathf.Deg2Rad, outer - 1e-4f);
                    locList.Add(new Local { pos = worldToLocal.MultiplyPoint3x4(light.transform.position),
                        axis = worldToLocal.MultiplyVector(light.transform.forward).normalized,
                        color = color, rangeSqr = light.range * light.range, range = light.range,
                        innerCos = Mathf.Cos(inner), outerCos = Mathf.Cos(outer), spot = true });
                }
            }
            directionals = dirList.ToArray();
            locals = locList.ToArray();

            if (RenderSettings.ambientMode == AmbientMode.Flat) {
                flatAmbient = true;
                ambientFlat = RenderSettings.ambientLight.linear;
                ambientGrid = null;
            }
            else if (RenderSettings.ambientMode == AmbientMode.Trilight) {
                trilightAmbient = true;
                ambientSky = RenderSettings.ambientSkyColor.linear;
                ambientEquator = RenderSettings.ambientEquatorColor.linear;
                ambientGround = RenderSettings.ambientGroundColor.linear;
                ambientGrid = null;
            }
            else {
                // Unity evaluates its own SH (exact math, main thread); ambient is L2-low-
                // frequency, so a 32×16 direction grid with worker-side bilinear lookup is
                // visually exact without porting ShadeSH9's packed constants.
                ambientGrid = new Color[AmbientW * AmbientH];
                var directions = new Vector3[ambientGrid.Length];
                var results = new Color[ambientGrid.Length];
                int i = 0;
                for (int y = 0; y < AmbientH; ++y)
                    for (int x = 0; x < AmbientW; ++x, ++i) {
                        float phi = (x + 0.5f) / AmbientW * 2f * Mathf.PI;
                        float theta = (y + 0.5f) / AmbientH * Mathf.PI;
                        directions[i] = new Vector3(
                            Mathf.Sin(theta) * Mathf.Cos(phi), Mathf.Cos(theta), Mathf.Sin(theta) * Mathf.Sin(phi));
                    }
                RenderSettings.ambientProbe.Evaluate(directions, results);
                for (int j = 0; j < ambientGrid.Length; ++j) ambientGrid[j] = results[j];
            }

            var probeList = new List<Probe>();
            foreach (var rp in Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None)) {
                if (!rp.enabled || !rp.gameObject.activeInHierarchy) continue;
                var texture = rp.customBakedTexture ? rp.customBakedTexture : rp.bakedTexture;
                if (texture == null) continue;
                var read = ReadEquirect(texture, out int w, out int h);
                if (read == null) continue;
                probeList.Add(new Probe { pixels = read, width = w, height = h,
                    bounds = rp.bounds, bounded = true, importance = rp.importance, average = Average(read) });
            }
            probes = probeList.ToArray();
            // No scene probe applies → the environment's custom reflection (Lighting ▸
            // Environment Reflections) is the fallback Unity would blend to.
            fallbackProbe = null;
            if (RenderSettings.customReflection != null) {
                var read = ReadEquirect(RenderSettings.customReflection, out int w2, out int h2);
                if (read != null)
                    fallbackProbe = new Probe { pixels = read, width = w2, height = h2, average = Average(read) };
            }

            // Specular is view-dependent; bake it for the scene view camera's vantage
            // when one is open, else a three-quarter view of the object.
            var sceneCamera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            Bounds world = ComputeWorldBounds(root);
            Vector3 viewWorld = sceneCamera != null
                ? sceneCamera.transform.position
                : world.center + new Vector3(0.55f, 0.45f, -0.7f).normalized * (world.extents.magnitude * 2.5f + 1f);
            viewPosition = worldToLocal.MultiplyPoint3x4(viewWorld);
            shadowEpsilon = Mathf.Max(sourceDiagonal * 2e-3f, 1e-5f);
        }

        /// <summary>The bake's source BVH, used for direct-light shadow rays.</summary>
        internal void BindShadows(TriangleBvh sourceBvh) => bvh = sourceBvh;

        /// <summary>Direct realtime/mixed light at a source point (local space). Thread-safe.</summary>
        internal Color Direct(Vector3 p, Vector3 n)
        {
            var sum = Color.black;
            for (int i = 0; i < directionals.Length; ++i) {
                var d = directionals[i];
                float ndl = Vector3.Dot(n, d.dir);
                if (ndl <= 0f) continue;
                float shadow = Shadow(p + n * shadowEpsilon, d.dir, float.MaxValue);
                if (shadow * d.shadowStrength >= 1f) continue;
                sum += d.color * (ndl * (1f - shadow * d.shadowStrength));
            }
            for (int i = 0; i < locals.Length; ++i) {
                var l = locals[i];
                Vector3 toLight = l.pos - p;
                float distSq = toLight.sqrMagnitude;
                if (distSq > l.rangeSqr || distSq < 1e-10f) continue;
                float dist = Mathf.Sqrt(distSq);
                Vector3 dir = toLight / dist;
                float weight = Vector3.Dot(n, dir);
                if (weight <= 0f) continue;
                if (l.spot) {
                    float axis = -Vector3.Dot(dir, l.axis); // receiver looks along -axis toward the spot
                    if (axis <= l.outerCos) continue;
                    weight *= Mathf.SmoothStep(l.outerCos, l.innerCos, axis);
                }
                // URP physical falloff: inverse square, smoothly clamped at range.
                float q = dist / l.range;
                float window = Mathf.Max(0f, 1f - q * q * q * q);
                float attenuation = window * window / Mathf.Max(distSq, 1e-6f);
                float shadow = Shadow(p + n * shadowEpsilon, dir, dist);
                sum += l.color * (weight * attenuation * (1f - shadow));
            }
            return sum;
        }

        /// <summary>Ambient irradiance for a surface direction (local space). Thread-safe.</summary>
        internal Color Ambient(Vector3 n)
        {
            if (flatAmbient) return ambientFlat;
            if (trilightAmbient) {
                float up = n.y * 0.5f + 0.5f;
                return up < 0.5f
                    ? Color.Lerp(ambientGround, ambientEquator, up * 2f)
                    : Color.Lerp(ambientEquator, ambientSky, (up - 0.5f) * 2f);
            }
            return SampleBilinear(ambientGrid, AmbientW, AmbientH, EquirectUV(localToWorld.MultiplyVector(n)));
        }

        /// <summary>Probe reflection seen from a source point: V points from the surface
        /// toward the viewer. A simplified Standard Fresnel scales the probe colour.</summary>
        internal Color Specular(Vector3 p, Vector3 n, Color albedo, float metallic, float smoothness)
        {
            Probe probe = PickProbe(localToWorld.MultiplyPoint3x4(p));
            if (probe == null) return Color.black;
            Vector3 v = viewPosition - p;
            if (v.sqrMagnitude < 1e-10f) return Color.black;
            v.Normalize();
            Vector3 worldR = localToWorld.MultiplyVector(Vector3.Reflect(-v, n)).normalized;
            Color sample = SampleBilinear(probe.pixels, probe.width, probe.height, EquirectUV(worldR));
            float roughness = Mathf.Clamp01(1f - smoothness);
            Color color = Color.Lerp(sample, probe.average, roughness * 0.85f);
            float ndv = Mathf.Clamp01(Vector3.Dot(n, v));
            Vector3 f0 = Vector3.Lerp(new Vector3(0.04f, 0.04f, 0.04f),
                new Vector3(albedo.r, albedo.g, albedo.b), metallic);
            Vector3 fresnel = f0 + (Vector3.one - f0) * Mathf.Pow(1f - ndv, 5f);
            float energy = 0.25f + 0.75f * smoothness;
            return new Color(color.r * fresnel.x * energy, color.g * fresnel.y * energy, color.b * fresnel.z * energy, 0f);
        }

        internal Color SampleLightmap(Lightmap map, Vector2 uv2)
        {
            Vector2 uv = new Vector2(uv2.x * map.st.x + map.st.z, uv2.y * map.st.y + map.st.w);
            return SampleBilinear(map.pixels, map.width, map.height, uv);
        }

        // ── plumbing ──

        float Shadow(Vector3 origin, Vector3 dir, float maxDist)
        {
            if (bvh == null) return 0f;
            var hit = bvh.Raycast(origin, dir, maxDist);
            return hit.triangleIndex >= 0 ? 1f : 0f;
        }

        Probe PickProbe(Vector3 world)
        {
            Probe best = null;
            for (int i = 0; i < probes.Length; ++i) {
                var probe = probes[i];
                if (probe.bounded && !probe.bounds.Contains(world)) continue;
                if (best == null || probe.importance > best.importance) best = probe;
            }
            return best ?? fallbackProbe;
        }

        static Vector2 EquirectUV(Vector3 dir)
        {
            float u = Mathf.Atan2(dir.z, dir.x) / (2f * Mathf.PI);
            if (u < 0f) u += 1f;
            float v = Mathf.Acos(Mathf.Clamp(dir.y, -1f, 1f)) / Mathf.PI;
            return new Vector2(u, v);
        }

        static Color SampleBilinear(Color[] pixels, int width, int height, Vector2 uv)
        {
            if (pixels == null || pixels.Length == 0) return Color.black;
            float x = uv.x * width - 0.5f, y = uv.y * height - 0.5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            Color At(int xx, int yy) => pixels[Wrap(yy, height) * width + Wrap(xx, width)];
            Color Row(int yy) => Color.LerpUnclamped(At(ix, yy), At(ix + 1, yy), fx);
            return Color.LerpUnclamped(Row(iy), Row(iy + 1), fy);
        }

        static int Wrap(int v, int size) => v < 0 ? v + size : v >= size ? v - size : v;

        static Color Average(Color[] pixels)
        {
            var sum = new Color();
            for (int i = 0; i < pixels.Length; ++i) sum += pixels[i];
            return pixels.Length > 0 ? sum / pixels.Length : Color.black;
        }

        static Bounds ComputeWorldBounds(GameObject root)
        {
            bool any = false;
            var bounds = new Bounds();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>()) {
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return any ? bounds : new Bounds(root.transform.position, Vector3.one);
        }

        // Cubemap → equirect readback through Hidden/MeshLab/RemeshBeautyEquirect
        // (directions in world axes, linear).
        internal static Color[] ReadEquirect(Texture cube, out int width, out int height)
        {
            width = 256; height = 128;
            var shader = Shader.Find("Hidden/MeshLab/RemeshBeautyEquirect");
            if (!shader || !cube) return null;
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try {
                Graphics.Blit(cube, rt, material);
                RenderTexture.active = rt;
                copy = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy.GetPixels();
            }
            catch (Exception) { return null; }
            finally {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy) Object.DestroyImmediate(copy);
                Object.DestroyImmediate(material);
            }
        }

        // Lightmap readback for the source capture: plain blit into a float RT, then
        // decode. HDR encodings (Unity "High Quality", Bakery's default .hdr output)
        // come through linear directly. Unity's own RGBA32 lightmaps are RGBM
        // (rgb * a * 8); Bakery's 8-bit output is plain linear with alpha pinned at 1,
        // so "alpha ~ 1 everywhere" (a real RGBM scene always has dark texels with
        // alpha < 1) identifies a plain map and skips the decode that would
        // overbrighten it eightfold.
        internal static Lightmap ReadLightmap(Texture2D lightmap, Vector4 st)
        {
            int width = lightmap.width, height = lightmap.height;
            bool hdr = lightmap.format == TextureFormat.RGBAFloat || lightmap.format == TextureFormat.RGBAHalf;
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try {
                Graphics.Blit(lightmap, rt);
                RenderTexture.active = rt;
                copy = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                var pixels = copy.GetPixels();
                if (!hdr) {
                    bool rgbm = false;
                    int step = Math.Max(1, pixels.Length / 4096);
                    for (int i = 0; i < pixels.Length; i += step)
                        if (pixels[i].a < 0.999f) { rgbm = true; break; }
                    if (rgbm)
                        for (int i = 0; i < pixels.Length; ++i) {
                            var c = pixels[i];
                            float scale = c.a * 8f;
                            pixels[i] = new Color(c.r * scale, c.g * scale, c.b * scale, 1f);
                        }
                }
                return new Lightmap { pixels = pixels, width = width, height = height, st = st };
            }
            finally {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy) Object.DestroyImmediate(copy);
            }
        }
    }
}
