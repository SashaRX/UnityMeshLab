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

    [Serializable]
    public sealed class RemeshSettings
    {
        // 1 · Voxel remesh
        public int voxelResolution = 128;
        public bool solve = true;
        public bool shell;

        // 2 · Simplify
        public bool simplify = true;
        public int targetTriangles; // stop at this count; 0 = limited by maximumError only
        public float maximumError = 0.01f;
        public RemeshRegularize regularize = RemeshRegularize.None;
        public bool preserveFolds = true;
        public bool pruneSmallParts;

        // 3 · Normals & UV
        public RemeshHardEdges hardEdges = RemeshHardEdges.Angle;
        public float normalCrease = 60;
        public float normalSmoothing = 1;
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
        public float projectionDistance = 0.02f; // fraction of source bounds diagonal
        public int bakeSamples = 4;              // per texel: 1, 4, 9 or 16
        public bool transferVertexColor;
        public bool transferVertexAlpha;

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
                (bakeSamples != 1 && bakeSamples != 4 && bakeSamples != 9 && bakeSamples != 16))
                throw new ArgumentException("Invalid remesh settings.");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        static bool NonNegative(float value) => Finite(value) && value >= 0;
    }
}
