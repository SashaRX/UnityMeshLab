using System.Collections.Generic;
using UnityEngine;

namespace SashaRX.UnityMeshLab
{
    /// <summary>
    /// Mesh-level transforms and bounds every tool used to carry a copy of: baking a
    /// matrix into a mesh (positions, normals by the inverse transpose, tangents
    /// re-orthogonalised, winding and tangent handedness flipped under a mirroring
    /// matrix), and world / combined bounds of meshes and hierarchies. Main thread
    /// (Mesh access); the pure box math is in <see cref="MeshGeometry"/>.
    /// </summary>
    internal static class MeshTransform
    {
        /// <summary>
        /// Writes <paramref name="matrix"/> into the mesh's vertex data. Normals take the
        /// inverse transpose (correct under non-uniform scale, where MultiplyVector is
        /// not), tangents the matrix itself and are re-orthogonalised against the new
        /// normal; a mirroring matrix (negative determinant) flips every triangle's
        /// winding and the tangent handedness so the surface still faces out and the
        /// normal map still decodes. Bounds are recalculated. Readable mesh required.
        /// </summary>
        public static void BakeMatrix(Mesh mesh, Matrix4x4 matrix)
        {
            if (mesh == null || !mesh.isReadable) return;
            if (matrix.isIdentity) return;
            bool mirrored = matrix.determinant < 0f;
            var normalMatrix = matrix.inverse.transpose;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; ++i) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            mesh.vertices = vertices;
            var normals = mesh.normals;
            bool hasNormals = normals != null && normals.Length == vertices.Length;
            if (hasNormals)
            {
                for (int i = 0; i < normals.Length; ++i) normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
                mesh.normals = normals;
            }
            var tangents = mesh.tangents;
            if (tangents != null && tangents.Length == vertices.Length)
            {
                for (int i = 0; i < tangents.Length; ++i)
                {
                    var t = matrix.MultiplyVector(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z));
                    if (hasNormals) t -= normals[i] * Vector3.Dot(t, normals[i]);
                    t.Normalize();
                    tangents[i] = new Vector4(t.x, t.y, t.z, mirrored ? -tangents[i].w : tangents[i].w);
                }
                mesh.tangents = tangents;
            }
            if (mirrored)
                for (int sub = 0; sub < mesh.subMeshCount; ++sub)
                {
                    var triangles = mesh.GetTriangles(sub);
                    for (int i = 0; i + 2 < triangles.Length; i += 3) { int tmp = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = tmp; }
                    mesh.SetTriangles(triangles, sub);
                }
            mesh.RecalculateBounds();
        }

        /// <summary>The union of every renderer's world bounds under root; <paramref name="fallback"/> when it has none.</summary>
        public static Bounds WorldBounds(GameObject root, Bounds fallback)
        {
            bool any = false; var bounds = new Bounds();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>())
            {
                if (!any) { bounds = renderer.bounds; any = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return any ? bounds : fallback;
        }

        /// <summary>The union of each mesh's bounds carried through its matrix; an empty box at the origin when there are none.</summary>
        public static Bounds CombinedBounds(IEnumerable<(Mesh mesh, Matrix4x4 matrix)> meshes)
        {
            bool any = false; var bounds = new Bounds();
            foreach (var (mesh, matrix) in meshes)
            {
                if (mesh == null) continue;
                var b = MeshGeometry.TransformBounds(mesh.bounds, matrix);
                if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
            }
            return bounds;
        }
    }
}
