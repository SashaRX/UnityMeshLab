using System;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    internal enum FoliageFaceSelection { AllTriangleSubmeshes, SelectedMaterials, SelectedSubmeshIndices }
    internal enum FoliageNormalSource { VoxelEnvelope, Sphere, Hemisphere }

    [Serializable]
    internal sealed class FoliageNormalsSettings
    {
        internal const int HardPrimitiveCap = 50000;
        public bool lod0Only = true;
        public FoliageFaceSelection selection;
        public Material[] materials = Array.Empty<Material>();
        public int[] submeshes = Array.Empty<int>();
        public MeshRenderer[] renderers = Array.Empty<MeshRenderer>(); // empty = all eligible renderers
        public bool filterRenderers; // when enabled, an empty renderer selection means none
        public FoliageNormalSource normalSource;
        public bool autoFitProxy = true;
        public Vector3 proxyCenter, proxyRotation;
        public float proxyRadius = 1, proxyRadiusScale = 1;
        public int proxySegments = 64;
        public float sampleSpacing = .05f;
        public bool relativeSpacing = true;
        public float radiusMultiplier = 1f, dedupSpacingFraction = .15f;
        public int minimumSamplesPerIsland = 1, deterministicSeed = 1729, maximumPrimitiveCount = 20000;
        public bool includeSourceVertices = true, includeTriangleCentroids;
        public int primitiveSubdivision = 1, voxelResolution = 64;
        public int smoothingIterations = 5;
        public float smoothingStrength = 1f, lambda = .5f, mu = -.53f;
        public bool remeshProxy = true;
        public float remeshEdgeLength; // zero = mean edge length of the smoothed proxy
        public int remeshIterations = 5, maximumProxyTriangles = 100000;
        public float remeshRelaxation = .5f;
        public float maxTransferDistance; // zero = twice the capture diagonal
        public float transferBlend = 1;
        public bool alignToOriginalHemisphere, gpuProjection = true, recalculateTangents = true;

        internal void Validate()
        {
            if (!Enum.IsDefined(typeof(FoliageNormalSource), normalSource)) throw new ArgumentException("Unknown normal source.");
            Range(maxTransferDistance, 0, 1e10f, nameof(maxTransferDistance)); Range(transferBlend, 0, 1, nameof(transferBlend));
            if (normalSource != FoliageNormalSource.VoxelEnvelope)
            {
                ValidateShape();
                return;
            }
            ValidateEnvelope();
        }

        void ValidateShape()
        {
            if (!FoliageTargetSnapshot.Finite(proxyCenter) || !FoliageTargetSnapshot.Finite(proxyRotation))
                throw new ArgumentException("Proxy center and rotation must be finite.");
            Range(proxyRadius, 1e-6f, 1e6f, nameof(proxyRadius)); Range(proxyRadiusScale, .01f, 100, nameof(proxyRadiusScale));
            if (proxySegments < 16 || proxySegments > 128 || proxySegments % 4 != 0)
                throw new ArgumentException("Proxy segments must be a multiple of four between 16 and 128.");
        }

        void ValidateEnvelope()
        {
            Range(sampleSpacing, 1e-6f, 1e6f, nameof(sampleSpacing));
            Range(radiusMultiplier, .1f, 10, nameof(radiusMultiplier));
            Range(dedupSpacingFraction, .001f, 1, nameof(dedupSpacingFraction));
            Range(smoothingStrength, 0, 1, nameof(smoothingStrength));
            Range(lambda, .001f, .6f, nameof(lambda)); Range(mu, -.65f, -.001f, nameof(mu));
            if (-mu < lambda) throw new ArgumentException("Taubin compensation must be at least lambda in magnitude.");
            if (remeshProxy) ValidateRemesh();
            if (minimumSamplesPerIsland < 1 || minimumSamplesPerIsland > 100 || maximumPrimitiveCount < 1 || maximumPrimitiveCount > HardPrimitiveCap)
                throw new ArgumentException("Sample budgets must be positive; primitive hard cap is 50,000.");
            if (primitiveSubdivision < 0 || primitiveSubdivision > 1 || voxelResolution < 4 || voxelResolution > 256 || smoothingIterations < 0 || smoothingIterations > 100)
                throw new ArgumentException("Subdivision must be 0–1, voxel resolution 4–256, smoothing iterations 0–100.");
        }

        void ValidateRemesh()
        {
            Range(remeshEdgeLength, 0, 1e6f, nameof(remeshEdgeLength)); Range(remeshRelaxation, 0, 1, nameof(remeshRelaxation));
            if (remeshIterations < 1 || remeshIterations > 20 || maximumProxyTriangles < 4 || maximumProxyTriangles > 200000)
                throw new ArgumentException("Proxy remesh iterations must be 1–20 and triangle budget 4–200,000.");
        }

        static void Range(float value, float min, float max, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
                throw new ArgumentException($"{name} must be finite and between {min} and {max}.");
        }

        internal FoliageNormalsSettings Snapshot() => new FoliageNormalsSettings {
            lod0Only = lod0Only, selection = selection, materials = (Material[])materials.Clone(), submeshes = (int[])submeshes.Clone(),
            renderers = (MeshRenderer[])renderers.Clone(), filterRenderers = filterRenderers, sampleSpacing = sampleSpacing, relativeSpacing = relativeSpacing,
            normalSource = normalSource, autoFitProxy = autoFitProxy, proxyCenter = proxyCenter, proxyRotation = proxyRotation,
            proxyRadius = proxyRadius, proxyRadiusScale = proxyRadiusScale, proxySegments = proxySegments,
            radiusMultiplier = radiusMultiplier, dedupSpacingFraction = dedupSpacingFraction, minimumSamplesPerIsland = minimumSamplesPerIsland,
            deterministicSeed = deterministicSeed, maximumPrimitiveCount = maximumPrimitiveCount, includeSourceVertices = includeSourceVertices,
            includeTriangleCentroids = includeTriangleCentroids, primitiveSubdivision = primitiveSubdivision, voxelResolution = voxelResolution,
            smoothingIterations = smoothingIterations, smoothingStrength = smoothingStrength, lambda = lambda, mu = mu,
            remeshProxy = remeshProxy, remeshEdgeLength = remeshEdgeLength, remeshIterations = remeshIterations,
            maximumProxyTriangles = maximumProxyTriangles, remeshRelaxation = remeshRelaxation,
            maxTransferDistance = maxTransferDistance, transferBlend = transferBlend, alignToOriginalHemisphere = alignToOriginalHemisphere,
            gpuProjection = gpuProjection, recalculateTangents = recalculateTangents
        };
    }
}
