using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class MeshViewport3DTests
    {
        static Mesh Quad()
        {
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0) };
            mesh.normals = new[] { Vector3.forward, Vector3.back, Vector3.up, Vector3.right };
            mesh.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(0, 1, 0, -1), new Vector4(0, 0, 1, 1), new Vector4(-1, 0, 0, 1) };
            mesh.uv = new[] { Vector2.zero, new Vector2(1.5f, 0.25f), new Vector2(-0.25f, 1f), Vector2.one };
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };
            return mesh;
        }

        [Test]
        public void ShadingModesEncodeTheMeshDataAsColors()
        {
            var mesh = Quad();
            try {
                var normals = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Normals);
                Assert.AreEqual(new Color32(128, 128, 255, 255), normals[0]);   // +z
                Assert.AreEqual(new Color32(128, 128, 0, 255), normals[1]);     // -z
                Assert.AreEqual(new Color32(128, 255, 128, 255), normals[2]);   // +y
                var tangents = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.Tangents);
                Assert.AreEqual(new Color32(255, 128, 128, 255), tangents[0]);
                Assert.AreEqual(new Color32(0, 128, 128, 255), tangents[3]);
                // UVs wrap: 1.5 → 0.5, -0.25 → 0.75, 1.0 → 0.
                var uv = MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.UV0);
                Assert.AreEqual(127, uv[1].r); Assert.AreEqual(63, uv[1].g);
                Assert.AreEqual(191, uv[2].r); Assert.AreEqual(255, uv[2].g);
                Assert.AreEqual(0, uv[3].r); Assert.AreEqual(0, uv[3].g);
                // Missing data: no UV1 → null; no vertex colours → neutral grey.
                Assert.IsNull(MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.UV1));
                Assert.AreEqual(new Color32(200, 200, 200, 255), MeshViewport3D.EncodeColors(mesh, MeshViewport3D.Shading.VertexColors)[0]);
                // Unique edges of two triangles sharing one: five lines.
                Assert.AreEqual(10, MeshViewport3D.EdgeIndices(mesh).Count);
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
