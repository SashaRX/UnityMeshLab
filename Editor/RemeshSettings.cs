using System;

namespace SashaRX.UnityMeshLab
{
    /// <summary>Where the result mesh gets hard (split) normals.</summary>
    public enum RemeshHardEdges
    {
        /// <summary>Everything smooth.</summary>
        Smooth,
        /// <summary>Edges sharper than the crease angle.</summary>
        Angle,
        /// <summary>Only along UV island borders; smooth inside every island.</summary>
        UvIslands,
        /// <summary>UV island borders plus edges sharper than the crease angle.</summary>
        UvIslandsAndAngle,
    }

    /// <summary>How strongly simplification evens out triangle sizes.</summary>
    public enum RemeshRegularize
    {
        /// <summary>Adaptive: flat areas collapse first, detail keeps its density.</summary>
        None,
        Light,
        Strong,
    }

    /// <summary>How face normals are weighted when the UV-split vertex normals are
    /// regenerated — the Blender Weighted Normal modifier analog.</summary>
    public enum RemeshNormalWeighting
    {
        /// <summary>Face-area weighting (meshopt's own accumulation).</summary>
        FaceArea,
        /// <summary>Corner-angle weighting: sharp corners pull their normal harder.</summary>
        CornerAngle,
        /// <summary>Face area multiplied by corner angle.</summary>
        FaceAreaAndCornerAngle,
    }

    /// <summary>What the bake writes into the maps.</summary>
    public enum RemeshBakeMode
    {
        /// <summary>Transfer the source material maps (albedo, normal, metal, AO, emission).</summary>
        Materials,
        /// <summary>Bake the object as the player sees it: realtime/mixed light with ray
        /// shadows, lightmaps, ambient and reflection probes folded into one lit BaseColor
        /// texture; the saved material becomes Unlit/Texture.</summary>
        Beauty,
    }

    /// <summary>
    /// Whether the source's back faces count as surface. A material that renders
    /// both sides (Cull Off, double-sided) shows its sheet from behind too, so the
    /// remesh keeps both sides of it and the bake samples it from either side.
    /// </summary>
    public enum RemeshBackfaces
    {
        /// <summary>Per material: two-sided when its cull mode or double-sided switch says so.</summary>
        FromMaterials,
        /// <summary>Every source face is two-sided.</summary>
        Always,
        /// <summary>Only the front of every source face counts.</summary>
        Never,
    }

    /// <summary>What the remesh stage builds from the captured source.</summary>
    public enum RemeshShape
    {
        /// <summary>Voxelize the captured geometry (the default remesh).</summary>
        LOD0,
        /// <summary>One box per captured renderer, aligned to that renderer's own axes
        /// (the mesh's authored bounds carried by its transform) — a far-LOD proxy;
        /// materials and lighting bake from the original geometry, projected onto the
        /// box faces.</summary>
        BoundingBox,
        /// <summary>A coarse blocky hull: the (filtered) capture voxelized at a low
        /// resolution without surface fitting and simplified to a small triangle
        /// budget, so L/T footprints, courtyards and roofs keep their silhouette.</summary>
        Hull,
    }

    [Serializable]
    public sealed class RemeshSettings
    {
        // 1 · Voxel remesh
        internal const int MaxVoxelResolution = 1024;
        public int voxelResolution = 128;
        public bool solve = true;
        public bool shell;
        // Explicit disk intent, opt-in. Loop numbers follow welded vertex order;
        // rerun preparation after changing the source. No automatic Bridge inference.
        public bool planarCap;
        public string planarCapLoops = "0";
        public bool planarCapLocalPlanes;
        // After the voxel remesh, drop the faces the source has no surface for: the
        // voxelizer closes an open sheet into a slab, and its back side and rims have no
        // source face nearby with an aligned normal. Closed sources are left whole.
        public bool trimToSource = true;
        // Which source faces count from behind as well (trim keeps both sides of them,
        // the bake's front-face filter accepts either side).
        public RemeshBackfaces sourceBackfaces = RemeshBackfaces.FromMaterials;
        // Source capture: skip meshes named Name_LOD1 and higher (LODGroups already
        // contribute LOD0 only).
        public bool lod0Only = true;
        // What the remesh stage builds from the capture: the voxelized geometry, one
        // oriented box per renderer, or a coarse voxel hull.
        public RemeshShape sourceShape = RemeshShape.LOD0;
        // Hull shape: voxel resolution of the coarse pass and the triangle budget the
        // strong-regularized simplification stops at.
        public int hullResolution = 24;
        public int hullTriangles = 120;
        // Part filter, applied to the capture before any shape is built: connected
        // pieces whose extent is below minPartSize × capture diagonal (bolts, debris), or
        // whose cross-section — the two smaller principal extents — is under minRodVoxels
        // voxel cells (pipes, cables, railings: a section the voxel grid cannot carry)
        // are excluded. Sheets of any thickness stay. 0 = off.
        public float minPartSize = 0.02f;
        public float minRodVoxels = 1f;
        // Keep hierarchy: remesh every captured node SEPARATELY and save the result as a
        // hierarchy of meshes under one root (per-node materials, local transforms kept),
        // instead of welding everything into one _LOD0 mesh with one baked material.
        public bool keepHierarchy;

        // 2 · Simplify
        public bool simplify = true;
        public int targetTriangles; // stop at this count; 0 = limited by maximumError only
        public float maximumError = 0.01f;
        public RemeshRegularize regularize = RemeshRegularize.None;
        public bool preserveFolds = true;
        public bool pruneSmallParts;

        // 3 · Normals & UV
        // UV islands is the bake-oriented default: smooth inside every island,
        // hard only along island borders, and the normal map carries the detail.
        // Final shading settings run after unwrap. Intermediate geometry and
        // the xatlas input use averaged normals, independent of these modes.
        public RemeshHardEdges hardEdges = RemeshHardEdges.UvIslands;
        public float normalCrease = 60;
        public float normalSmoothing = 1;
        // Compare the requested chart settings with two bounded alternatives and
        // keep fewer islands only within bounded UV stretch.
        public bool reduceUvFragmentation = true;
        // Merge adjacent islands after the unwrap when their seam aligns within bounded
        // stretch and texel density, then re-pack the atlas. Deterministic; off while
        // the chart-merge experiment runs (Documentation~/EXPERIMENTS.md).
        public bool mergeCharts;
        // Regenerated after the UV cut, weighted per this mode (Blender Weighted
        // Normal analog); smooth inside every split group, hard across every split.
        public RemeshNormalWeighting normalWeighting = RemeshNormalWeighting.FaceArea;
        public float chartMaxCost = 2;
        public float chartNormalDeviation = 2;
        // Measured balanced preset: compact charts avoid slivers without increasing
        // cost/iterations globally. Corpus and packing comparisons: EXPERIMENTS.md.
        public float chartRoundness = 0.5f;
        public float chartStraightness = 6;
        public float chartNormalSeam = 4;
        public int chartIterations = 1;
        public float maxChartArea;       // source units², 0 = no limit
        public float maxChartBoundary;   // source units, 0 = no limit
        public bool packBruteForce;
        public bool packRotate = true;
        public bool packBlockAlign;
        public int textureResolution = 2048;
        public int padding = 8;

        // 4 · Bake
        // Materials transfers the source maps; Beauty additionally folds the scene's
        // lighting into one BaseColor texture and the saved material becomes Unlit.
        public RemeshBakeMode bakeMode = RemeshBakeMode.Materials;
        public float projectionDistance = 0.02f; // fraction of source bounds diagonal
        // Cage: Laplacian passes over the welded side directions (0 = raw averaged
        // normals), and whether each side's reach is fitted to where the source sits
        // along its ray (clamped between 1× and 8× the projection distance) instead of
        // the one global distance.
        public float cageSmoothing = 2f;
        public bool cageFit = true;
        // Proxy shapes (boxes, hull): how deep behind a proxy face a texel looks for the
        // source surface, as a fraction of the capture diagonal — the ray along the face
        // normal, then the nearest surface within the same reach. A full-depth look
        // sees the far side of a courtyard through the block and costs a whole BVH
        // traversal per empty texel; a short one keeps each face to what sits behind it.
        public float proxyDepth = 0.1f;
        public int bakeSamples = 4;              // per texel: 1, 4, 9 or 16
        // Additional pixel radius after atlas padding; 0 keeps padding alone.
        internal const int DefaultDilationRadius = 64, MaxDilationRadius = 8192;
        public int dilationRadius = DefaultDilationRadius;
        // Run the bake's geometry queries (projection rays, nearest fallbacks) on the
        // GPU through BvhQueries.compute; identical results, the CPU BVH otherwise.
        public bool gpuProjection = true;
        public bool bakeSourceAO;
        public bool multiplySourceAO;
        public SourceAoSettings sourceAO = new SourceAoSettings();
        public bool transferVertexColor;
        public bool transferVertexAlpha;
        // Multiply the source albedo by the source vertex color (RGB) while projecting —
        // the way vertex-tinting shaders read it. On by default; a mesh without colors
        // is unaffected.
        public bool vertexColorTint = true;

        // Save (FBX): embed the baked maps into the binary FBX instead of
        // linking them by absolute path. Portable, but the file grows by the
        // map sizes — the float EXR emission map alone is 16 bytes per texel.
        public bool embedFbxTextures = true;

        // Save: bake the source root's world rotation and scale into the saved geometry
        // so the model keeps its real size and orientation with an identity transform,
        // regardless of the source's own TRS. Off: the saved transform carries the
        // source's rotation and scale instead.
        public bool normalizeSize = true;

        /// <summary>Apply the measured xatlas preset without resetting the mesh,
        /// shading, atlas resolution/padding, optimizer toggles or bake settings.</summary>
        internal void ApplyDefaultXatlasSettings()
        {
            var defaults = new RemeshSettings();
            chartMaxCost = defaults.chartMaxCost;
            chartNormalDeviation = defaults.chartNormalDeviation;
            chartRoundness = defaults.chartRoundness;
            chartStraightness = defaults.chartStraightness;
            chartNormalSeam = defaults.chartNormalSeam;
            chartIterations = defaults.chartIterations;
            maxChartArea = defaults.maxChartArea;
            maxChartBoundary = defaults.maxChartBoundary;
            packBruteForce = defaults.packBruteForce;
            packRotate = defaults.packRotate;
            packBlockAlign = defaults.packBlockAlign;
        }

        internal static RemeshSettings FromSavedJson(string json)
        {
            var restored = UnityEngine.JsonUtility.FromJson<RemeshSettings>(json);
            // Older EditorPrefs have no radius. Preserve every existing field and
            // enable the new pass, while an explicitly saved 0 still disables it.
            if (restored != null && json.IndexOf("\"dilationRadius\"", StringComparison.Ordinal) < 0)
                restored.dilationRadius = DefaultDilationRadius;
            if (restored != null && json.IndexOf("\"reduceUvFragmentation\"", StringComparison.Ordinal) < 0)
                restored.reduceUvFragmentation = true;
            return restored;
        }

        public void Validate()
        {
            if (bakeSourceAO) {
                if (sourceAO == null) throw new ArgumentException("Source AO settings are missing.");
                sourceAO.Validate();
            }
            if (voxelResolution < 4 || voxelResolution > MaxVoxelResolution || targetTriangles < 0 || targetTriangles > 5000000 ||
                !Finite(maximumError) || maximumError < 0 || maximumError > 1 ||
                !Finite(normalCrease) || normalCrease < 0 || normalCrease > 180 ||
                !Finite(normalSmoothing) || normalSmoothing < 0 || normalSmoothing > 10 ||
                !NonNegative(chartMaxCost) || chartMaxCost <= 0 || !NonNegative(chartNormalDeviation) ||
                !NonNegative(chartRoundness) || !NonNegative(chartStraightness) || !NonNegative(chartNormalSeam) ||
                chartIterations < 1 || chartIterations > 16 || !NonNegative(maxChartArea) || !NonNegative(maxChartBoundary) ||
                textureResolution < 64 || textureResolution > 8192 || (textureResolution & (textureResolution - 1)) != 0 ||
                padding < 1 || padding > 32 || !Finite(projectionDistance) || projectionDistance <= 0 || projectionDistance > 1 ||
                dilationRadius < 0 || dilationRadius > MaxDilationRadius ||
                !Finite(cageSmoothing) || cageSmoothing < 0 || cageSmoothing > 10 ||
                !Finite(proxyDepth) || proxyDepth < 0.005f || proxyDepth > 1 ||
                (bakeSamples != 1 && bakeSamples != 4 && bakeSamples != 9 && bakeSamples != 16) ||
                hullResolution < 4 || hullResolution > 256 || hullTriangles < 12 || hullTriangles > 100000 ||
                !Finite(minPartSize) || minPartSize < 0 || minPartSize > 0.5f ||
                !Finite(minRodVoxels) || minRodVoxels < 0 || minRodVoxels > 16f)
                throw new ArgumentException("Invalid remesh settings.");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool NonNegative(float value) => Finite(value) && value >= 0;
    }
}
