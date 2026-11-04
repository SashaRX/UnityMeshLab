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
        /// normal map still decodes. Bounds are recalculated. Readable mesh required, except
        /// while its model imports.
        /// </summary>
        /// <param name="importing">
        /// Called from a model's OnPostprocessModel, where Unity lets a mesh be changed whatever
        /// its Read/Write setting (its isReadable is false there when the setting is off).
        /// </param>
        public static void BakeMatrix(Mesh mesh, Matrix4x4 matrix, bool importing = false)
        {
            if (mesh == null || (!importing && !mesh.isReadable)) return;
            if (matrix.isIdentity) return;
            bool mirrored = matrix.determinant < 0f;
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; ++i) vertices[i] = matrix.MultiplyPoint3x4(vertices[i]);
            mesh.vertices = vertices;
            var normals = BakeNormals(mesh, matrix.inverse.transpose, vertices.Length);
            BakeTangents(mesh, matrix, normals, vertices.Length, mirrored);
            if (mirrored)
                for (int sub = 0; sub < mesh.subMeshCount; ++sub) FlipWinding(mesh, sub);
            mesh.RecalculateBounds();
        }

        // The mesh's normals by the inverse transpose, written back; none when it has none per vertex.
        static Vector3[] BakeNormals(Mesh mesh, Matrix4x4 normalMatrix, int vertexCount)
        {
            var normals = mesh.normals;
            if (normals == null || normals.Length != vertexCount) return System.Array.Empty<Vector3>();
            for (int i = 0; i < normals.Length; ++i) normals[i] = normalMatrix.MultiplyVector(normals[i]).normalized;
            mesh.normals = normals;
            return normals;
        }

        // Tangents by the matrix, re-orthogonalised against the new normals; the handedness flips when mirrored.
        static void BakeTangents(Mesh mesh, Matrix4x4 matrix, Vector3[] normals, int vertexCount, bool mirrored)
        {
            var tangents = mesh.tangents;
            if (tangents == null || tangents.Length != vertexCount) return;
            bool hasNormals = normals.Length == vertexCount;
            for (int i = 0; i < tangents.Length; ++i)
            {
                var t = matrix.MultiplyVector(new Vector3(tangents[i].x, tangents[i].y, tangents[i].z));
                if (hasNormals) t -= normals[i] * Vector3.Dot(t, normals[i]);
                t.Normalize();
                tangents[i] = new Vector4(t.x, t.y, t.z, mirrored ? -tangents[i].w : tangents[i].w);
            }
            mesh.tangents = tangents;
        }

        // Each submesh keeps its topology; only faces (triangles, quads) have a winding to flip.
        static void FlipWinding(Mesh mesh, int sub)
        {
            var topology = mesh.GetTopology(sub);
            int corners = topology switch { MeshTopology.Triangles => 3, MeshTopology.Quads => 4, _ => 0 };
            if (corners == 0) return;
            var indices = mesh.GetIndices(sub);
            // The second and the last corner swap: 0 1 2 → 0 2 1, 0 1 2 3 → 0 3 2 1.
            for (int i = 0; i + corners - 1 < indices.Length; i += corners)
            {
                int tmp = indices[i + 1]; indices[i + 1] = indices[i + corners - 1]; indices[i + corners - 1] = tmp;
            }
            mesh.SetIndices(indices, topology, sub, calculateBounds: false);
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
