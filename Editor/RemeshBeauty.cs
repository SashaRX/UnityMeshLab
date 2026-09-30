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
    /// Scene data stays in world space; queries take capture-space points and normals
    /// and convert through the bound space matrix, so one snapshot serves the weld and
    /// every keep-hierarchy node (ForSpace). Baked-only lights are skipped — their
    /// contribution already lives in the lightmap, so nothing is counted twice.
    /// </summary>
    internal sealed class RemeshBeauty
    {
        internal sealed class Lightmap
        {
            public Color[] pixels;            // linear, decoded
            public int width, height;
            public Vector4 st;                // renderer.lightmapScaleOffset: uv2 * xy + zw
            // Directional mode: rgb = dominant direction (encoded, NOT unit length —
            // its length is the directionality), a = the rebalancing coefficient.
            public Color[] dirPixels;
            public int dirWidth, dirHeight;
            public bool hasDir;
        }

        internal sealed class Probe
        {
            public Color[][] mips;             // equirect mip chain, sharpest first
            public int[] widths, heights;      // per level
            public int mipCount;
            public Bounds bounds;              // world space
            public bool bounded;
            public Vector3 worldPos, boxMin, boxMax;
            public bool boxProjection;
            public float importance;
        }

        struct Directional { public Vector3 dir; public Color color; public float shadowStrength; }
        struct Local
        {
            public Vector3 pos, axis;         // world space (axis used when spot)
            public Color color;
            public float rangeSqr, range, innerCos, outerCos, shadowStrength;
            public bool spot;
        }

        const int AmbientW = 32, AmbientH = 16;

        // Scene data lives in WORLD space; every query converts its capture-space
        // point/normal through localToWorld first, so one snapshot serves the weld and
        // every keep-hierarchy node alike (see ForSpace) and light ranges, distances and
        // attenuation all stay in the same units whatever the source root's scale.
        readonly Directional[] directionals = Array.Empty<Directional>();
        readonly Local[] locals = Array.Empty<Local>();
        readonly Probe[] probes;
        readonly Probe fallbackProbe;
        readonly Color[] ambientGrid;         // evaluated by Unity on the main thread
        readonly bool flatAmbient, trilightAmbient;
        readonly Color ambientFlat, ambientSky, ambientEquator, ambientGround;
        readonly Vector3 viewPosition;        // world space; specular is baked for this vantage
        Matrix4x4 localToWorld, worldToLocal;
        TriangleBvh bvh;                      // capture-space source BVH, bound by the bake before workers run
        float shadowEpsilon;

        /// <summary>Capture the lighting environment around root for geometry expressed in
        /// root-local space. Main thread only.</summary>
        internal RemeshBeauty(GameObject root, float sourceDiagonal)
        {
            SetSpace(root.transform.localToWorldMatrix, sourceDiagonal);

            var dirList = new List<Directional>();
            var locList = new List<Local>();
            foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) {
                if (!light.enabled || !light.gameObject.activeInHierarchy) continue;
                // Baked-only lights live in the lightmap; including them again would
                // double-count. URP has no subtractive mode, so Mixed direct is realtime.
                if (light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked) continue;
                Color color = light.color.linear * light.intensity;
                float shadowStrength = light.shadows == LightShadows.None ? 0f : light.shadowStrength;
                if (light.type == LightType.Directional) {
                    dirList.Add(new Directional {
                        dir = -(light.transform.rotation * Vector3.forward).normalized,
                        color = color, shadowStrength = shadowStrength,
                    });
                }
                else if (light.type == LightType.Point) {
                    locList.Add(new Local { pos = light.transform.position, color = color,
                        rangeSqr = light.range * light.range, range = light.range, shadowStrength = shadowStrength });
                }
                else if (light.type == LightType.Spot) {
                    float outer = light.spotAngle * 0.5f * Mathf.Deg2Rad;
                    float inner = Mathf.Min(light.innerSpotAngle * 0.5f * Mathf.Deg2Rad, outer - 1e-4f);
                    locList.Add(new Local { pos = light.transform.position, axis = light.transform.forward.normalized,
                        color = color, rangeSqr = light.range * light.range, range = light.range,
                        innerCos = Mathf.Cos(inner), outerCos = Mathf.Cos(outer), spot = true, shadowStrength = shadowStrength });
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
                var probe = ReadProbe(texture, ProbeMips);
                if (probe == null) continue;
                probe.bounds = rp.bounds; probe.bounded = true; probe.importance = rp.importance;
                probe.worldPos = rp.transform.position;
                probe.boxMin = rp.bounds.min; probe.boxMax = rp.bounds.max;
                probe.boxProjection = rp.boxProjection;
                probeList.Add(probe);
            }
            probes = probeList.ToArray();
            // No scene probe applies → the environment's custom reflection (Lighting ▸
            // Environment Reflections) is the fallback Unity would blend to.
            fallbackProbe = RenderSettings.customReflection != null ? ReadProbe(RenderSettings.customReflection, ProbeMips) : null;

            // Specular is view-dependent; bake it for the scene view camera's vantage
            // when one is open, else a three-quarter view of the object.
            var sceneCamera = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.camera : null;
            Bounds world = ComputeWorldBounds(root);
            viewPosition = sceneCamera != null
                ? sceneCamera.transform.position
                : world.center + new Vector3(0.55f, 0.45f, -0.7f).normalized * (world.extents.magnitude * 2.5f + 1f);
        }

        /// <summary>The same scene snapshot for geometry captured in another space (a
        /// keep-hierarchy node): shares the probe, lightmap and light data, converts
        /// through the given matrix instead. Bind the node's own BVH before baking.</summary>
        internal RemeshBeauty ForSpace(Matrix4x4 spaceToWorld, float sourceDiagonal)
        {
            var copy = (RemeshBeauty)MemberwiseClone();
            copy.bvh = null;
            copy.SetSpace(spaceToWorld, sourceDiagonal);
            return copy;
        }

        void SetSpace(Matrix4x4 spaceToWorld, float sourceDiagonal)
        {
            localToWorld = spaceToWorld;
            worldToLocal = spaceToWorld.inverse;
            shadowEpsilon = Mathf.Max(sourceDiagonal * 2e-3f, 1e-5f);
        }

        /// <summary>The bake's source BVH (capture space), used for direct-light shadow rays.</summary>
        internal void BindShadows(TriangleBvh sourceBvh) => bvh = sourceBvh;

        Vector3 WorldNormal(Vector3 n) => worldToLocal.transpose.MultiplyVector(n).normalized;

        /// <summary>Direct realtime/mixed light at a source point (capture space). Thread-safe.</summary>
        internal Color Direct(Vector3 p, Vector3 n)
        {
            var sum = Color.black;
            Vector3 pw = localToWorld.MultiplyPoint3x4(p), nw = WorldNormal(n);
            Vector3 origin = p + n * shadowEpsilon;
            for (int i = 0; i < directionals.Length; ++i) {
                var d = directionals[i];
                float ndl = Vector3.Dot(nw, d.dir);
                if (ndl <= 0f) continue;
                float shadow = d.shadowStrength > 0f ? Shadow(origin, worldToLocal.MultiplyVector(d.dir).normalized, float.MaxValue) * d.shadowStrength : 0f;
                if (shadow >= 1f) continue;
                sum += d.color * (ndl * (1f - shadow));
            }
            for (int i = 0; i < locals.Length; ++i) {
                var l = locals[i];
                Vector3 toLight = l.pos - pw;
                float distSq = toLight.sqrMagnitude;
                if (distSq > l.rangeSqr || distSq < 1e-10f) continue;
                float dist = Mathf.Sqrt(distSq);
                Vector3 dir = toLight / dist;
                float weight = Vector3.Dot(nw, dir);
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
                float shadow = 0f;
                if (l.shadowStrength > 0f) {
                    // The shadow segment runs in capture space, so its length is the
                    // capture-space distance to the light, whatever the root's scale.
                    Vector3 toLightLocal = worldToLocal.MultiplyPoint3x4(l.pos) - p;
                    shadow = Shadow(origin, toLightLocal.normalized, toLightLocal.magnitude) * l.shadowStrength;
                }
                sum += l.color * (weight * attenuation * (1f - shadow));
            }
            return sum;
        }

        /// <summary>Ambient irradiance for a surface direction (capture space). Thread-safe.</summary>
        internal Color Ambient(Vector3 n)
        {
            Vector3 nw = WorldNormal(n);
            if (flatAmbient) return ambientFlat;
            if (trilightAmbient) {
                float up = nw.y * 0.5f + 0.5f;
                return up < 0.5f
                    ? Color.Lerp(ambientGround, ambientEquator, up * 2f)
                    : Color.Lerp(ambientEquator, ambientSky, (up - 0.5f) * 2f);
            }
            return SampleBilinear(ambientGrid, AmbientW, AmbientH, EquirectUV(nw));
        }

        /// <summary>Probe reflection seen from a source point (capture space): V points from
        /// the surface toward the viewer. Ports the game's specular path: URP's
        /// BoxProjectedCubemapDirection, the prefiltered-mip remap r(1.7−0.7r)·maxMip
        /// with the fractional part lerped between two levels, and
        /// EnvironmentBRDFSpecular (surface reduction 1/(r²+1), grazing term
        /// saturate(smoothness+reflectivity), Schlick Fresnel on NdotV).</summary>
        internal Color Specular(Vector3 p, Vector3 n, Color albedo, float metallic, float smoothness)
        {
            Vector3 worldP = localToWorld.MultiplyPoint3x4(p);
            Probe probe = PickProbe(worldP);
            if (probe == null) return Color.black;
            Vector3 v = viewPosition - worldP;
            if (v.sqrMagnitude < 1e-10f) return Color.black;
            v.Normalize();
            Vector3 nw = WorldNormal(n);
            Vector3 worldR = Vector3.Reflect(-v, nw).normalized;
            Vector3 dir = probe.boxProjection ? BoxProjected(worldR, worldP, probe) : worldR;
            float roughness = Mathf.Clamp01(1f - smoothness);
            float remapped = roughness * (1.7f - 0.7f * roughness);
            float mipFloat = remapped * (probe.mipCount - 1);
            int m0 = Mathf.Clamp(Mathf.FloorToInt(mipFloat), 0, probe.mipCount - 1);
            int m1 = Mathf.Min(m0 + 1, probe.mipCount - 1);
            Color sample = Color.Lerp(
                SampleEquirect(probe.mips[m0], probe.widths[m0], probe.heights[m0], dir),
                SampleEquirect(probe.mips[m1], probe.widths[m1], probe.heights[m1], dir),
                mipFloat - m0);
            float surfaceReduction = 1f / (roughness * roughness + 1f);
            Vector3 f0 = Vector3.Lerp(new Vector3(0.04f, 0.04f, 0.04f),
                new Vector3(albedo.r, albedo.g, albedo.b), metallic);
            float grazingComponent = Mathf.Clamp01(smoothness + Mathf.Lerp(0.04f, 1f, metallic));
            float fresnel = Mathf.Pow(1f - Mathf.Clamp01(Vector3.Dot(nw, v)), 5f);
            Vector3 specular = Vector3.Lerp(f0, new Vector3(grazingComponent, grazingComponent, grazingComponent), fresnel) * surfaceReduction;
            return new Color(specular.x * sample.r, specular.y * sample.g, specular.z * sample.b, 0f);
        }

        // URP BoxProjectedCubemapDirection, verbatim: the reflection ray is bent toward
        // the probe-box faces so reflections localize inside the probe's volume.
        static Vector3 BoxProjected(Vector3 r, Vector3 p, Probe probe)
        {
            var rb = new Vector3(
                (r.x > 0f ? probe.boxMax.x : probe.boxMin.x) - p.x,
                (r.y > 0f ? probe.boxMax.y : probe.boxMin.y) - p.y,
                (r.z > 0f ? probe.boxMax.z : probe.boxMin.z) - p.z);
            var t = new Vector3(rb.x / r.x, rb.y / r.y, rb.z / r.z);
            float fa = Mathf.Min(Mathf.Min(t.x, t.y), t.z);
            return p - probe.worldPos + r * fa;
        }

        internal Color SampleLightmap(Lightmap map, Vector2 uv2, Vector3 localNormal)
        {
            Vector2 uv = new Vector2(uv2.x * map.st.x + map.st.z, uv2.y * map.st.y + map.st.w);
            Color illuminance = SampleBilinear(map.pixels, map.width, map.height, uv, clamp: true);
            if (map.hasDir) {
                // The game's exact directional-lightmap response (URP EntityLighting's
                // SampleDirectionalLightmap): the encoded dominant direction is dotted
                // against the WORLD normal as a half-Lambert and the result is divided
                // by the texel's rebalancing coefficient. A flat colour would overlight
                // every surface whose normal disagrees with the dominant direction.
                Color d = SampleBilinear(map.dirPixels, map.dirWidth, map.dirHeight, uv, clamp: true);
                Vector3 dir = new Vector3(d.r, d.g, d.b) - new Vector3(0.5f, 0.5f, 0.5f);
                float halfLambert = Vector3.Dot(WorldNormal(localNormal), dir) + 0.5f;
                float scale = halfLambert / Mathf.Max(1e-4f, d.a);
                illuminance = new Color(illuminance.r * scale, illuminance.g * scale, illuminance.b * scale, 1f);
            }
            return illuminance;
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

        static Color SampleEquirect(Color[] pixels, int width, int height, Vector3 dir)
            => SampleBilinear(pixels, width, height, EquirectUV(dir));

        // clamp: lightmap regions are sub-rects of the atlas, so their edges clamp
        // (wrapping would read the far edge); equirect maps wrap.
        static Color SampleBilinear(Color[] pixels, int width, int height, Vector2 uv, bool clamp = false)
        {
            if (pixels == null || pixels.Length == 0) return Color.black;
            float x = uv.x * width - 0.5f, y = uv.y * height - 0.5f;
            int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
            float fx = x - ix, fy = y - iy;
            Color At(int xx, int yy) => clamp
                ? pixels[Mathf.Clamp(yy, 0, height - 1) * width + Mathf.Clamp(xx, 0, width - 1)]
                : pixels[Wrap(yy, height) * width + Wrap(xx, width)];
            Color Row(int yy) => Color.LerpUnclamped(At(ix, yy), At(ix + 1, yy), fx);
            return Color.LerpUnclamped(Row(iy), Row(iy + 1), fy);
        }

        static int Wrap(int v, int size) => v < 0 ? v + size : v >= size ? v - size : v;

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
        // (directions in world axes, linear). lod samples the probe's own prefiltered
        // mip — the roughness blur the game samples.
        internal static Color[] ReadEquirect(Texture cube, int lod, int width, int height)
        {
            var shader = Shader.Find("Hidden/MeshLab/RemeshBeautyEquirect");
            if (!shader || !cube) return null;
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try {
                material.SetFloat("_Lod", lod);
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

        // Six-level equirectangular mip chain (256×128 halving down), mirroring the
        // prefiltered mip chain the game samples with its r(1.7−0.7r)·maxMip remap.
        const int ProbeMips = 6;

        static Probe ReadProbe(Texture cube, int levels)
        {
            var probe = new Probe { mips = new Color[levels][], widths = new int[levels], heights = new int[levels] };
            for (int lod = 0; lod < levels; ++lod) {
                int width = Mathf.Max(4, 256 >> lod), height = Mathf.Max(2, 128 >> lod);
                var read = ReadEquirect(cube, lod, width, height);
                if (read == null) break;
                probe.mips[lod] = read; probe.widths[lod] = width; probe.heights[lod] = height;
                probe.mipCount = lod + 1;
            }
            return probe.mipCount > 0 ? probe : null;
        }

        // Lightmap readbacks are float (16 bytes per texel) and a scene lightmap can be
        // 8K or 16K, so only the REGION a renderer occupies (its scaleOffset rect, one
        // texel of padding) is read, and a region larger than this many texels is
        // downsampled to fit. 4M texels = 64 MiB per region.
        internal const int MaxLightmapPixels = 2048 * 2048;

        // Lightmap readback: the renderer's region of the lightmap blitted into a float
        // RT, then decoded. HDR encodings (Unity "High Quality", Bakery's default .hdr
        // output) come through linear directly. Unity's own RGBA32 lightmaps are RGBM
        // (rgb * a * 8); Bakery's 8-bit output is plain linear with alpha pinned at 1,
        // so "alpha ~ 1 everywhere" (a real RGBM scene always has dark texels with
        // alpha < 1) identifies a plain map and skips the decode that would
        // overbrighten it eightfold. The optional direction texture is linear data and
        // needs no decode at all. The returned st maps uv2 into the region.
        internal static Lightmap ReadLightmap(Texture2D lightmap, Texture2D lightmapDir, Vector4 st)
        {
            var region = LightmapRegion(st, lightmap.width, lightmap.height, out Vector4 regionSt);
            bool hdr = lightmap.format == TextureFormat.RGBAFloat || lightmap.format == TextureFormat.RGBAHalf;
            var pixels = ReadRegion(lightmap, region, out int width, out int height);
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
            var map = new Lightmap { pixels = pixels, width = width, height = height, st = regionSt };
            if (lightmapDir != null) {
                map.dirPixels = ReadRegion(lightmapDir, region, out map.dirWidth, out map.dirHeight);
                map.hasDir = map.dirPixels != null;
            }
            return map;
        }

        // The UV rect [x, y, x+width, y+height] of the lightmap that uv2 × st.xy + st.zw
        // covers, clamped to the texture and padded by one texel for bilinear reads,
        // plus the st that maps uv2 into that rect instead of the whole map.
        internal static Rect LightmapRegion(Vector4 st, int texWidth, int texHeight, out Vector4 regionSt)
        {
            float u0 = Mathf.Min(st.z, st.z + st.x), u1 = Mathf.Max(st.z, st.z + st.x);
            float v0 = Mathf.Min(st.w, st.w + st.y), v1 = Mathf.Max(st.w, st.w + st.y);
            float padU = 1f / Mathf.Max(1, texWidth), padV = 1f / Mathf.Max(1, texHeight);
            u0 = Mathf.Clamp01(u0 - padU); u1 = Mathf.Clamp01(u1 + padU);
            v0 = Mathf.Clamp01(v0 - padV); v1 = Mathf.Clamp01(v1 + padV);
            if (u1 - u0 < padU) { u0 = Mathf.Clamp01(u0 - padU); u1 = Mathf.Clamp01(u0 + 2 * padU); }
            if (v1 - v0 < padV) { v0 = Mathf.Clamp01(v0 - padV); v1 = Mathf.Clamp01(v0 + 2 * padV); }
            float w = u1 - u0, h = v1 - v0;
            regionSt = new Vector4(st.x / w, st.y / h, (st.z - u0) / w, (st.w - v0) / h);
            return new Rect(u0, v0, w, h);
        }

        // Region size in texels, downsampled uniformly when it exceeds MaxLightmapPixels.
        static void RegionSize(Rect region, int texWidth, int texHeight, out int width, out int height)
        {
            width = Mathf.Max(1, Mathf.CeilToInt(region.width * texWidth));
            height = Mathf.Max(1, Mathf.CeilToInt(region.height * texHeight));
            long pixels = (long)width * height;
            if (pixels > MaxLightmapPixels) {
                float scale = Mathf.Sqrt(MaxLightmapPixels / (float)pixels);
                width = Mathf.Max(1, Mathf.FloorToInt(width * scale));
                height = Mathf.Max(1, Mathf.FloorToInt(height * scale));
            }
        }

        static Color[] ReadRegion(Texture2D texture, Rect region, out int width, out int height)
        {
            RegionSize(region, texture.width, texture.height, out width, out height);
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D copy = null;
            try {
                // Blit(scale, offset) samples the source at uv × scale + offset — the region.
                Graphics.Blit(texture, rt, new Vector2(region.width, region.height), new Vector2(region.x, region.y));
                RenderTexture.active = rt;
                copy = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                copy.Apply();
                return copy.GetPixels();
            }
            finally {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                if (copy) Object.DestroyImmediate(copy);
            }
        }
    }
}
