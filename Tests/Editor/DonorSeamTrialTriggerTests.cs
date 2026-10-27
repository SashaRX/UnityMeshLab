using NUnit.Framework;
using UnityEngine;

namespace SashaRX.UnityMeshLab.Tests
{
    public class DonorSeamTrialTriggerTests
    {
        static TransferUvQuality Quality(int degenerate = 0, int pairs = 0, double worst = 1)
            => new TransferUvQuality { degenerateFaces = degenerate, overlapPairs = pairs, worstAnisotropy = worst };

        /// <summary>Zero-area 3D faces are degenerate in every layout; alone they no longer start the trial.</summary>
        [Test]
        public void ZeroAreaGeometryAloneDoesNotStartTheTrial()
        {
            Assert.IsFalse(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(degenerate: 3), 3));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(degenerate: 4), 3),
                "A collapsed UV face beyond the zero-area geometry still starts it.");
        }

        /// <summary>Stretch, overlap and collapsed UVs start the trial as before.</summary>
        [Test]
        public void UvDefectsStillStartTheTrial()
        {
            Assert.IsFalse(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(worst: 4), 0));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(worst: 4.01), 0));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(pairs: 1), 0));
            Assert.IsTrue(UvTransferWorkflow.NeedsDonorSeamTrial(Quality(degenerate: 1), 0));
        }

        static Mesh Triangle()
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            mesh.triangles = new[] { 0, 1, 2 };
            return mesh;
        }

        /// <summary>
        /// A skinned or blend-shape target keeps its faces: the subdivision drops the
        /// deformation and the FBX cannot take new faces on a deformed mesh. A scene edit
        /// read without the import's skinning still counts as deformed.
        /// </summary>
        [Test]
        public void DeformedTargetsKeepTheirFaces()
        {
            var plain = Triangle();
            var skinned = Triangle();
            skinned.boneWeights = new[] { new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 }, new BoneWeight { weight0 = 1 } };
            skinned.bindposes = new[] { Matrix4x4.identity };
            var shaped = Triangle();
            shaped.AddBlendShapeFrame("shape", 100, new[] { Vector3.up, Vector3.up, Vector3.up }, null, null);
            try
            {
                Assert.IsFalse(UvTransferWorkflow.KeepsFaces(new MeshEntry { fbxMesh = plain }));
                Assert.IsTrue(UvTransferWorkflow.KeepsFaces(new MeshEntry { fbxMesh = skinned }));
                Assert.IsTrue(UvTransferWorkflow.KeepsFaces(new MeshEntry { fbxMesh = shaped }));
                Assert.IsTrue(UvTransferWorkflow.KeepsFaces(new MeshEntry { fbxMesh = skinned, sceneEditMesh = plain }),
                    "A scene edit of a skinned import is read without its skinning.");
                Assert.IsFalse(UvTransferWorkflow.KeepsFaces(new MeshEntry()));
            }
            finally
            {
                Object.DestroyImmediate(plain);
                Object.DestroyImmediate(skinned);
                Object.DestroyImmediate(shaped);
            }
        }
    }
}
