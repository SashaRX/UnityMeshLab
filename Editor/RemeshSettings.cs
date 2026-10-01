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
        public int voxelResolution = 128;
        public bool solve = true;
        public bool shell;
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
        // pieces whose bounds diagonal is below minPartSize × capture diagonal (bolts,
        // railings, debris) or whose smallest bounds side is below minPartThickness ×
        // capture diagonal (decals, glass sheets) are excluded. 0 = off.
        public float minPartSize = 0.02f;
        public float minPartThickness;
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
        // On coarse organic decimations an angle crease reads as fully faceted
        // and also feeds xatlas crease-split normals as seams, which shatters
        // the atlas into slivers.
        public RemeshHardEdges hardEdges = RemeshHardEdges.UvIslands;
        public float normalCrease = 60;
        public float normalSmoothing = 1;
        // Regenerated after the UV cut, weighted per this mode (Blender Weighted
        // Normal analog); smooth inside every split group, hard across every split.
        public RemeshNormalWeighting normalWeighting = RemeshNormalWeighting.FaceArea;
        public float chartMaxCost = 2;
        public float chartNormalDeviation = 2;
        public float chartRoundness = 0.01f;
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
        public int bakeSamples = 4;              // per texel: 1, 4, 9 or 16
        public bool transferVertexColor;
        public bool transferVertexAlpha;
        // Multiply the source albedo by the source vertex color (RGB) while projecting,
        // for shaders that use the vertex color as an albedo tint.
        public bool vertexColorTint;

        // Save (FBX): embed the baked maps into the binary FBX instead of
        // linking them by absolute path. Portable, but the file grows by the
        // map sizes — the float EXR emission map alone is 16 bytes per texel.
        public bool embedFbxTextures = true;

        // Save: bake the source's world scale into the saved geometry so the model
        // keeps its real size with a scale-1 transform, regardless of the source's
        // own scaling. Off: the saved transform carries the source's scale instead.
        public bool normalizeSize = true;

        public void Validate()
        {
            if (voxelResolution < 4 || voxelResolution > 256 || targetTriangles < 0 || targetTriangles > 5000000 ||
                !Finite(maximumError) || maximumError < 0 || maximumError > 1 ||
                !Finite(normalCrease) || normalCrease < 0 || normalCrease > 180 ||
                !Finite(normalSmoothing) || normalSmoothing < 0 || normalSmoothing > 10 ||
                !NonNegative(chartMaxCost) || chartMaxCost <= 0 || !NonNegative(chartNormalDeviation) ||
                !NonNegative(chartRoundness) || !NonNegative(chartStraightness) || !NonNegative(chartNormalSeam) ||
                chartIterations < 1 || chartIterations > 16 || !NonNegative(maxChartArea) || !NonNegative(maxChartBoundary) ||
                textureResolution < 64 || textureResolution > 8192 || (textureResolution & (textureResolution - 1)) != 0 ||
                padding < 1 || padding > 32 || !Finite(projectionDistance) || projectionDistance <= 0 || projectionDistance > 1 ||
                (bakeSamples != 1 && bakeSamples != 4 && bakeSamples != 9 && bakeSamples != 16) ||
                hullResolution < 4 || hullResolution > 256 || hullTriangles < 12 || hullTriangles > 100000 ||
                !Finite(minPartSize) || minPartSize < 0 || minPartSize > 0.5f ||
                !Finite(minPartThickness) || minPartThickness < 0 || minPartThickness > 0.5f)
                throw new ArgumentException("Invalid remesh settings.");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool NonNegative(float value) => Finite(value) && value >= 0;
    }
}
