using System;

namespace SashaRX.UnityMeshLab
{
    [Serializable]
    public sealed class RemeshSettings
    {
        public int voxelResolution = 128;
        public int targetTriangles = 10000;
        public float maximumError = 0.01f;
        public float normalCrease = 60;
        public float normalSmoothing = 1;
        public int textureResolution = 2048;
        public int padding = 8;
        public float projectionDistance = 0.02f; // fraction of source bounds diagonal
        public bool solve = true;
        public bool shell;

        public void Validate()
        {
            if (voxelResolution < 4 || voxelResolution > 256 || targetTriangles < 1 || targetTriangles > 5000000 ||
                !Finite(maximumError) || maximumError < 0 || maximumError > 1 ||
                !Finite(normalCrease) || normalCrease < 0 || normalCrease > 180 ||
                !Finite(normalSmoothing) || normalSmoothing < 0 || normalSmoothing > 10 ||
                textureResolution < 64 || textureResolution > 8192 || (textureResolution & (textureResolution - 1)) != 0 ||
                padding < 1 || padding > 32 || !Finite(projectionDistance) || projectionDistance <= 0 || projectionDistance > 1)
                throw new ArgumentException("Invalid remesh settings.");
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
