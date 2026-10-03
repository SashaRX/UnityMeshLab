using System;
using System.Threading;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    [Serializable]
    public sealed class SourceAoSettings
    {
        public int samples = 64;
        public float radius = .2f; // fraction of source bounds diagonal
        public float intensity = 1f;
        public float bias = .0001f; // fraction of source bounds diagonal
        public bool normalMap = true;
        public bool cosineWeighted = true;
        public bool binaryHit;
        public bool backfaceCulling = true;
        public bool groundPlane;
        public float groundOffset = .01f; // fraction of source bounds diagonal

        public void Validate()
        {
            if (samples < 16 || samples > 1024 || !Positive(radius) || radius > 10 ||
                !Positive(intensity) || intensity > 10 || !Positive(bias) || bias > .1f ||
                float.IsNaN(groundOffset) || float.IsInfinity(groundOffset) || groundOffset < 0 || groundOffset > 10)
                throw new ArgumentException("Invalid source AO settings.");
        }
        static bool Positive(float value) => !float.IsNaN(value) && !float.IsInfinity(value) && value > 0;
        internal string Key => $"{samples}|{radius:R}|{intensity:R}|{bias:R}|{normalMap}|{cosineWeighted}|{binaryHit}|{backfaceCulling}|{groundPlane}|{groundOffset:R}";
    }

    // Immutable, array-only bake context: the same hemisphere weighting / distance
    // falloff as Vertex AO, evaluated at source hits instead of result vertices.
    internal sealed class SourceAoBaker
    {
        readonly RemeshSource source;
        readonly TriangleBvh bvh;
        readonly Vector3[] faceNormals, directions;
        readonly bool[] twoSided;
        readonly SourceAoSettings settings;
        readonly float distance, offset, groundY;

        internal SourceAoBaker(RemeshSource source, TriangleBvh bvh, Vector3[] faceNormals, bool[] twoSided, SourceAoSettings settings)
        {
            settings.Validate();
            this.source = source; this.bvh = bvh; this.faceNormals = faceNormals;
            this.twoSided = twoSided; this.settings = settings;
            directions = MeshGeometry.SphereDirections(settings.samples);
            distance = Mathf.Max(source.diagonal * settings.radius, 1e-8f);
            offset = Mathf.Max(source.diagonal * settings.bias, 1e-10f);
            float minY = float.PositiveInfinity;
            foreach (var p in source.positions) minY = Mathf.Min(minY, p.y);
            groundY = minY - source.diagonal * settings.groundOffset;
        }

        internal float Sample(int face, Vector3 weights, int seed, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int a = source.indices[face * 3], b = source.indices[face * 3 + 1], c = source.indices[face * 3 + 2];
            Vector3 geometric = faceNormals[face];
            Vector3 normal = RemeshBaker.SourceNormal(source, face, weights, settings.normalMap);
            if (normal.sqrMagnitude < 1e-12f) normal = geometric;
            if (geometric.sqrMagnitude < 1e-12f || normal.sqrMagnitude < 1e-12f) return 1f;
            // Offset by geometric normal: normal-map tilt must not push an origin
            // inside the surface and turn the source itself into false occlusion.
            Vector3 origin = source.positions[a] * weights.x + source.positions[b] * weights.y + source.positions[c] * weights.z + geometric * offset;
            float angle = (unchecked((uint)seed * 2654435761u) % 360) * Mathf.Deg2Rad;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle), total = 0, occluded = 0;
            for (int i = 0; i < directions.Length; ++i) {
                if ((i & 31) == 0) token.ThrowIfCancellationRequested();
                var d = directions[i];
                d = new Vector3(d.x * cos - d.z * sin, d.y, d.x * sin + d.z * cos);
                float dot = Vector3.Dot(d, normal);
                if (dot <= 0) continue;
                float weight = settings.cosineWeighted ? dot : 1f;
                total += weight;
                var hit = settings.backfaceCulling ? bvh.RaycastFacingFiltered(origin, d, distance, faceNormals, twoSided) : bvh.Raycast(origin, d, distance);
                float reach = hit.triangleIndex >= 0 && hit.t > offset * .1f ? hit.t : float.PositiveInfinity;
                if (settings.groundPlane && d.y < -.001f) {
                    float ground = (groundY - origin.y) / d.y;
                    if (ground > 0) reach = Mathf.Min(reach, ground);
                }
                if (reach < distance) occluded += weight * (settings.binaryHit ? 1f : 1f - reach / distance);
            }
            float ao = total > 0 ? 1f - occluded / total : 1f;
            return Mathf.Pow(Mathf.Clamp01(ao), settings.intensity);
        }
    }
}
